import gzip
import logging
import os
import sys
from pathlib import Path

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from azure.search.documents.aio import SearchClient
from dotenv import load_dotenv

from config_loader import get_config
from persona_loader import Persona, PersonaCatalog, PersonaValidationError
from prompt_loader import PromptLoader
from rtmt import RTMiddleTier, configure_realtime_model, create_hmac_token
from tools import attach_tools_rtmt

# Production: INFO; override with LOG_LEVEL env var for debugging
_log_level = os.environ.get("LOG_LEVEL", "INFO").upper()
logging.basicConfig(level=getattr(logging, _log_level, logging.INFO))
logger = logging.getLogger(__name__)

# Load centralized config
_config = get_config()
_compression_cfg = _config.get("compression", {})

# Minimum response size worth compressing (bytes)
_COMPRESS_MIN_SIZE = _compression_cfg.get("min_size_bytes", 256)
# Cache-Control for immutable hashed assets (JS/CSS bundles from Vite)
_STATIC_IMMUTABLE_MAX_AGE = _compression_cfg.get("static_immutable_max_age", 31_536_000)
# Cache-Control for mutable files (index.html, etc.)
_STATIC_DEFAULT_MAX_AGE = _compression_cfg.get("static_default_max_age", 3600)
# Compressible content-type substrings
_COMPRESSIBLE_TYPES = ("text/", "application/json", "application/javascript", "image/svg")

# App version — exposed via /health endpoint
_APP_VERSION = "1.0.0"

# Required environment variables for the app to function
_REQUIRED_ENV_VARS = [
    "AZURE_OPENAI_EASTUS2_ENDPOINT",
    "AZURE_OPENAI_REALTIME_DEPLOYMENT",
    "AZURE_SEARCH_ENDPOINT",
    "AZURE_SEARCH_INDEX",
]

# Startup validation state — read by /health endpoint
_startup_checks = {
    "personas_loaded": False,
    "prompts_loaded": False,
    "config_loaded": True,  # validated at module load by get_config()
    "env_vars": False,
}

# Populated by create_app() from the validated persona pack catalog (issue #70). Read by
# /health to list the loaded personas -- additive, does not change any existing /health field.
_persona_catalog: PersonaCatalog | None = None


def _get_bool_env(variable_name: str, default: bool = False) -> bool:
    """Parse boolean environment variables with predictable defaults."""
    value = os.environ.get(variable_name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


# ---------------------------------------------------------------------------
# Persona discovery endpoints (issue #74, design doc section 5.2)
# ---------------------------------------------------------------------------

def _persona_logo_url(persona: Persona) -> str:
    """Best-effort static asset URL matching the `/personas/{id}/assets/*` route documented
    in design doc section 5.2. That static route is separate, frontend-facing work (out of
    scope for #74 per Brian's scoping to `/api/personas` and `/api/personas/{id}` only) --
    this URL shape is forward-compatible with it once it lands."""
    return f"/personas/{persona.id}/assets/{persona.manifest.ui.assets.logo}"


def _persona_menu_url(persona: Persona) -> str:
    """See `_persona_logo_url` -- same scoping note, for `/personas/{id}/menu.json`."""
    return f"/personas/{persona.id}/menu.json"


def _model_pipelines_body(models) -> dict:
    """The selectable `models` per pipeline (design doc section 7); only the pipelines a
    persona actually declares (`cascade`/`local` are optional)."""
    body = {"realtime": {"default": models.realtime.default, "allowed": models.realtime.allowed}}
    if models.cascade is not None:
        body["cascade"] = {"default": models.cascade.default, "allowed": models.cascade.allowed}
    if models.local is not None:
        body["local"] = {"default": models.local.default, "allowed": models.local.allowed}
    return body


def _persona_summary_body(persona: Persona) -> dict:
    """One entry of `GET /api/personas`'s `personas` list (design doc section 5.2)."""
    return {
        "id": persona.id,
        "displayName": persona.manifest.displayName,
        "logoUrl": _persona_logo_url(persona),
        "theme": persona.manifest.ui.theme.model_dump(exclude_none=True),
    }


def _personas_index_body(catalog: PersonaCatalog) -> dict:
    """`GET /api/personas` response body (design doc section 5.2), enabled personas only.

    `backends` lists this deployment's own backend only -- the multi-backend list
    (python + dotnet) is populated once the C# port also serves `/api/personas` (#74's
    "C# follows later" note), not part of this PR.
    """
    return {
        "default": catalog.default_persona_id,
        "personas": [_persona_summary_body(catalog.get(persona_id)) for persona_id in catalog.ids],
        "backends": [{"id": "python", "url": "/realtime"}],
    }


def _persona_detail_body(persona: Persona) -> dict:
    """`GET /api/personas/{id}` response body (design doc section 5.2): the pack's `ui`
    block, plus `voice.default`, `locales`, `features.dayparts`, `menuUrl`, and the
    selectable `models` per pipeline. Callers must check the persona is enabled first
    (404 otherwise) -- this function assumes it already is."""
    manifest = persona.manifest
    return {
        "id": persona.id,
        **manifest.ui.model_dump(exclude_none=True),
        "voice": {"default": manifest.voice.default},
        "locales": manifest.locales.model_dump(exclude_none=True),
        "features": {"dayparts": manifest.features.dayparts},
        "menuUrl": _persona_menu_url(persona),
        "models": _model_pipelines_body(manifest.models),
    }


def load_app_secret(environ=None) -> bytes:
    """HMAC secret for /api/auth/session tokens.

    APP_SESSION_SECRET (a Container App secret in Azure, see infra/main.bicep)
    is shared by every replica and survives restarts, so a token minted by one
    process validates in another. Without it (local dev) a random per-process
    secret is used, which is only safe with a single process.
    """
    env = os.environ if environ is None else environ
    configured = (env.get("APP_SESSION_SECRET") or "").strip()
    if configured:
        if len(configured) < 32:
            logger.warning("APP_SESSION_SECRET is shorter than 32 characters; use a longer random value")
        return configured.encode("utf-8")
    if (env.get("RUNNING_IN_PRODUCTION") or "").strip().lower() in {"1", "true", "yes", "on"}:
        logger.warning("APP_SESSION_SECRET is not set; using a per-process random secret, so session tokens "
                       "will not validate across replicas or restarts")
    return os.urandom(32)


# ---------------------------------------------------------------------------
# Middleware
# ---------------------------------------------------------------------------

@web.middleware
async def _compression_middleware(request: web.Request, handler):
    """Gzip-compress eligible responses when the client accepts it."""
    response = await handler(request)

    # Only compress regular Response objects (not FileResponse, StreamResponse, WebSocket)
    if not isinstance(response, web.Response) or isinstance(response, web.WebSocketResponse):
        return response
    if response.body is None or len(response.body) < _COMPRESS_MIN_SIZE:
        return response

    accept_encoding = request.headers.get("Accept-Encoding", "")
    if "gzip" not in accept_encoding:
        return response

    content_type = response.content_type or ""
    if not any(ct in content_type for ct in _COMPRESSIBLE_TYPES):
        return response

    compressed = gzip.compress(response.body, compresslevel=_compression_cfg.get("level", 6))
    if len(compressed) >= len(response.body):
        return response

    response.body = compressed
    response.headers["Content-Encoding"] = "gzip"
    response.headers["Vary"] = "Accept-Encoding"
    return response


# ---------------------------------------------------------------------------
# Static file helpers
# ---------------------------------------------------------------------------

async def _index_handler(_request: web.Request) -> web.FileResponse:
    current_directory = Path(__file__).parent
    resp = web.FileResponse(current_directory / "static" / "index.html")
    resp.headers["Cache-Control"] = "no-cache"
    return resp


async def _health_handler(_request: web.Request) -> web.Response:
    all_ok = all(_startup_checks.values())
    body = {
        "status": "healthy" if all_ok else "unhealthy",
        "version": _APP_VERSION,
        "checks": _startup_checks,
    }
    # Additive (issue #70): list the loaded persona packs once the catalog has been validated.
    if _persona_catalog is not None:
        body["personas"] = _persona_catalog.ids
    return web.json_response(body, status=200 if all_ok else 503)


async def _check_service_connectivity() -> None:
    """Verify Azure service endpoints are reachable. Non-blocking — logs warnings only."""
    endpoints = {
        "Azure OpenAI": os.environ.get("AZURE_OPENAI_EASTUS2_ENDPOINT"),
        "Azure Search": os.environ.get("AZURE_SEARCH_ENDPOINT"),
    }
    timeout = aiohttp.ClientTimeout(total=5)
    try:
        async with aiohttp.ClientSession(timeout=timeout) as session:
            for name, url in endpoints.items():
                if not url:
                    continue
                try:
                    async with session.get(url, ssl=True) as resp:
                        logger.info("✅ %s reachable (HTTP %d)", name, resp.status)
                except Exception as exc:
                    logger.warning("⚠️ %s unreachable at %s — %s (non-fatal)", name, url, exc)
    except Exception as exc:
        logger.warning("⚠️ Service connectivity check failed — %s (non-fatal)", exc)


async def create_app() -> web.Application:
    """Configure and return the aiohttp application for realtime ordering."""

    if not _get_bool_env("RUNNING_IN_PRODUCTION", False):
        logger.info("Running in development mode; loading values from .env")
        load_dotenv()

    # ── Startup Validation ────────────────────────────────────────────────

    # 1. Validate required environment variables
    missing_vars = [v for v in _REQUIRED_ENV_VARS if not os.environ.get(v)]
    if missing_vars:
        logger.critical(
            "FATAL: Missing required environment variables: %s", ", ".join(missing_vars)
        )
        sys.exit(1)
    _startup_checks["env_vars"] = True

    # 2. Load and validate every enabled persona pack (issue #70). Refuses to start on an
    # invalid pack, naming the persona, file and field. Fails fast, same as prompt loading below.
    global _persona_catalog
    try:
        _persona_catalog = PersonaCatalog.load()
    except PersonaValidationError as exc:
        logger.critical("FATAL: Failed to load persona pack(s) — %s", exc)
        sys.exit(1)
    _startup_checks["personas_loaded"] = True

    # 3. Build one PromptLoader per enabled persona pack (issue #74; #92 review note --
    # removes the hardcoded PromptLoader(brand="sonic")). Each loader reads from its own
    # catalog-resolved Persona.prompts_dir, so a pack doesn't need to live under
    # PERSONAS_DIR/<id>/prompts for prompt loading to find it (same fail-fast behavior as
    # before: a missing/malformed prompts dir aborts startup, naming the persona).
    prompt_loaders: dict[str, PromptLoader] = {}
    try:
        for persona_id in _persona_catalog.ids:
            persona = _persona_catalog.get(persona_id)
            prompt_loaders[persona_id] = PromptLoader(brand=persona_id, prompts_dir=persona.prompts_dir)
    except (FileNotFoundError, ValueError) as exc:
        logger.critical("FATAL: Failed to load prompts — %s", exc)
        sys.exit(1)
    _startup_checks["prompts_loaded"] = True
    prompt_loader = prompt_loaders[_persona_catalog.default_persona_id]

    # 4. Optional: verify Azure service connectivity (non-blocking)
    await _check_service_connectivity()

    env_count = len(_REQUIRED_ENV_VARS)
    logger.info(
        "✅ Startup validation passed: personas=%s, prompts loaded, config valid, %d/%d env vars set",
        ", ".join(_persona_catalog.ids),
        env_count,
        env_count,
    )

    # ── App Configuration ─────────────────────────────────────────────────

    model_cfg = _config.get("model", {})
    conn_cfg = _config.get("connection", {})

    llm_endpoint = os.environ.get("AZURE_OPENAI_EASTUS2_ENDPOINT")
    llm_deployment = os.environ.get("AZURE_OPENAI_REALTIME_DEPLOYMENT")

    llm_key = os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY")
    search_key = os.environ.get("AZURE_SEARCH_API_KEY")

    credential = None
    if not llm_key or not search_key:
        if tenant_id := os.environ.get("AZURE_TENANT_ID"):
            logger.info("Using AzureDeveloperCliCredential with tenant_id %s", tenant_id)
            credential = AzureDeveloperCliCredential(tenant_id=tenant_id, process_timeout=60)
        else:
            logger.info("Using DefaultAzureCredential")
            credential = DefaultAzureCredential()

    llm_credential = AzureKeyCredential(llm_key) if llm_key else credential
    search_credential = AzureKeyCredential(search_key) if search_key else credential

    app = web.Application(
        middlewares=[_compression_middleware],
        client_max_size=conn_cfg.get("client_max_size_bytes", 4 * 1024 * 1024),
    )

    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=llm_endpoint,
        deployment=llm_deployment,
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "marin"),
        prompt_loader=prompt_loader,
    )
    # Shared HMAC secret for session tokens (APP_SESSION_SECRET; random for local dev)
    app_secret = load_app_secret()
    rtmt.app_secret = app_secret
    configure_realtime_model(rtmt, model_cfg)
    rtmt.system_message = prompt_loader.get_system_prompt()

    # Issue #74: the enabled-persona catalog and one PromptLoader per persona, so
    # _websocket_handler can resolve `?persona=`, reject an unknown one with 404 before the
    # WebSocket upgrade, and _forward_messages can use each session's own bound persona's
    # system prompt and default voice instead of the deployment-wide defaults above (which
    # remain exactly the default persona's -- unchanged Sonic behavior when `?persona` is
    # omitted, since `_persona_catalog.default_persona_id` is "sonic" today).
    rtmt.persona_catalog = _persona_catalog
    rtmt.persona_prompt_loaders = prompt_loaders

    # One search index per persona (design doc 3.2/5.2): the shared field-schema config
    # below (semantic configuration, identifier/content/embedding fields, vector/ranker
    # flags) is common to every brand's ingestion pipeline, but the index itself is not.
    persona_search_contexts: dict[str, dict] = {}
    for persona_id in _persona_catalog.ids:
        persona = _persona_catalog.get(persona_id)
        persona_search_contexts[persona_id] = {
            "search_client": SearchClient(
                os.environ.get("AZURE_SEARCH_ENDPOINT"),
                persona.manifest.search.indexName,
                search_credential,
                user_agent="RTMiddleTier",
            ),
            "semantic_configuration": os.environ.get("AZURE_SEARCH_SEMANTIC_CONFIGURATION") or "menuSemanticConfig",
            "identifier_field": os.environ.get("AZURE_SEARCH_IDENTIFIER_FIELD") or "id",
            "content_field": os.environ.get("AZURE_SEARCH_CONTENT_FIELD") or "description",
            "embedding_field": os.environ.get("AZURE_SEARCH_EMBEDDING_FIELD") or "embedding",
            "use_vector_query": _get_bool_env("AZURE_SEARCH_USE_VECTOR_QUERY", True),
            "use_semantic_ranker": (os.environ.get("AZURE_SEARCH_SEMANTIC_RANKER") or "standard").lower() != "disabled",
            "prompt_loader": prompt_loaders[persona_id],
        }

    attach_tools_rtmt(
        rtmt,
        credentials=search_credential,
        search_endpoint=os.environ.get("AZURE_SEARCH_ENDPOINT"),
        search_index=os.environ.get("AZURE_SEARCH_INDEX"),
        # Defaults aligned with the menu ingestion index schema; override via env vars as needed.
        semantic_configuration=os.environ.get("AZURE_SEARCH_SEMANTIC_CONFIGURATION") or "menuSemanticConfig",
        identifier_field=os.environ.get("AZURE_SEARCH_IDENTIFIER_FIELD") or "id",
        content_field=os.environ.get("AZURE_SEARCH_CONTENT_FIELD") or "description",
        embedding_field=os.environ.get("AZURE_SEARCH_EMBEDDING_FIELD") or "embedding",
        title_field=os.environ.get("AZURE_SEARCH_TITLE_FIELD") or "name",
        use_vector_query=_get_bool_env("AZURE_SEARCH_USE_VECTOR_QUERY", True),
        # The free search SKU has no semantic ranker; asking for one returns
        # HTTP 400 on every query. main.bicep emits the effective level. If the
        # variable is absent we assume the ranker exists and rely on the
        # runtime fallback in tools.search to recover.
        use_semantic_ranker=(os.environ.get("AZURE_SEARCH_SEMANTIC_RANKER") or "standard").lower() != "disabled",
        prompt_loader=prompt_loader,
        personas=persona_search_contexts,
    )

    rtmt.attach_to_app(app, "/realtime")

    # ── HMAC Session Token Endpoint (Task 4) ──
    async def get_session_token(_request: web.Request) -> web.Response:
        token = create_hmac_token(app_secret, expiry_seconds=900)
        return web.json_response({"token": token})

    # ── Persona discovery endpoints (issue #74, design doc section 5.2) ──
    async def get_personas(_request: web.Request) -> web.Response:
        return web.json_response(_personas_index_body(_persona_catalog))

    async def get_persona_detail(request: web.Request) -> web.Response:
        persona_id = request.match_info["persona_id"]
        if persona_id not in _persona_catalog:
            return web.json_response({"error": f"Unknown or disabled persona: {persona_id!r}"}, status=404)
        return web.json_response(_persona_detail_body(_persona_catalog.get(persona_id)))

    current_directory = Path(__file__).parent
    app.add_routes([
        web.get('/', _index_handler),
        web.get('/health', _health_handler),
        web.get('/api/auth/session', get_session_token),
        web.get('/api/personas', get_personas),
        web.get('/api/personas/{persona_id}', get_persona_detail),
    ])
    app.router.add_static(
        '/',
        path=current_directory / 'static',
        name='static',
        append_version=True,
    )

    async def _on_startup(app: web.Application):
        rtmt.start_background_tasks()
        logger.info("Background tasks started (token refresh, idle checker)")

    async def _on_shutdown(app: web.Application):
        logger.info("Graceful shutdown initiated — cleaning up active sessions")
        rtmt.stop_background_tasks()

    app.on_startup.append(_on_startup)
    app.on_shutdown.append(_on_shutdown)

    return app


if __name__ == "__main__":
    host = os.environ.get("HOST", "localhost")
    port = int(os.environ.get("PORT", 8000))
    conn_cfg = _config.get("connection", {})
    web.run_app(
        create_app(),
        host=host,
        port=port,
        shutdown_timeout=conn_cfg.get("shutdown_timeout", 10.0),
        keepalive_timeout=conn_cfg.get("keepalive_timeout", 75.0),
    )
