import gzip
import hashlib
import logging
import os
import sys
from pathlib import Path, PurePosixPath, PureWindowsPath

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from azure.identity.aio import DefaultAzureCredential as AsyncDefaultAzureCredential
from azure.search.documents.aio import SearchClient
from dotenv import load_dotenv

import conformance_hooks
import default_persona
import entra_auth
from access_log import access_log_kwargs
from cascade_processor import CascadeProcessor
from config_loader import get_config
from entra_auth import EntraConfigError
from model_catalog import ModelCatalog, ModelValidationError
from persona_loader import Persona, PersonaCatalog, PersonaValidationError
from processors import ProcessorRegistry
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
    "prod_guard": False,
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

def _content_hash(path: Path) -> str:
    """Short, stable content-hash for the `?v=` query param on persona asset/menu URLs
    (Rick's PR #102 review item 2). Recomputed fresh from the file's current bytes on every
    request (see `_asset_cache_headers`), never cached across a file edit -- so a stale `?v=`
    from a URL minted before a pack update simply falls through to the short/no-cache
    fallback below instead of being trusted. 16 hex chars (64 bits) of SHA-256 is plenty of
    collision resistance for a cache-busting token; the full digest would just make the URL
    (and every response referencing it) needlessly longer.
    """
    return hashlib.sha256(path.read_bytes()).hexdigest()[:16]


def _persona_logo_url(persona: Persona) -> str:
    """Static asset URL served by the `/personas/{id}/assets/*` route below (design doc
    section 5.2, Rick's PR #102 review item 5).

    `ui.assets.logo` in persona.json is a path relative to the PACK ROOT (e.g.
    `"assets/logo.svg"`, matching the on-disk layout `<pack>/assets/logo.svg`) -- while
    `_resolve_persona_asset_path` resolves the route's tail relative to `persona.assets_dir`
    (`<pack>/assets`) itself, to keep that route's traversal boundary scoped to just the
    assets subtree. Strip the redundant leading `assets/` here so the URL this function
    builds is one the asset route can actually resolve, instead of doubling that segment.

    Rick's PR #102 review item 2: the URL carries a `?v=<content-hash>` of the logo file
    itself, so the asset route (`_asset_cache_headers`) can tell a request that's actually
    pinned to this exact content (safe to cache for a year, immutably) apart from an
    unversioned or stale-hash request (which might get different bytes the next time this
    same path is requested, e.g. after a pack update) -- never immutable caching by default.
    Falls back to no `?v=` (rare: schema validation doesn't check the logo file itself
    exists on disk, only that persona.json's string field is present) rather than raising --
    an unversioned URL still works, just without the year-long cache policy.
    """
    logo = persona.manifest.ui.assets.logo
    relative = logo.removeprefix('assets/')
    route = f"/personas/{persona.id}/assets/{relative}"
    resolved = _resolve_persona_asset_path(persona, relative)
    if resolved is not None:
        route += f"?v={_content_hash(resolved)}"
    return route


def _persona_menu_url(persona: Persona) -> str:
    """See `_persona_logo_url` -- same route family and `?v=<content-hash>` versioning
    scheme, for `/personas/{id}/menu.json`. Unlike the logo, `persona.menu_path` is
    guaranteed to exist (persona_loader.py's `PersonaCatalog.load` fails fast at startup if
    the menu file is missing), so this always carries a `?v=`."""
    return f"/personas/{persona.id}/menu.json?v={_content_hash(persona.menu_path)}"


def _selectable_models(pipeline_cfg, pipeline_name: str, model_catalog: ModelCatalog) -> list[dict]:
    """*pipeline_cfg.allowed*, narrowed to ONLY what's actually selectable right now (design doc
    section 7.3: "Selectable = catalog ∩ deployment ∩ persona-allowed") and shaped as
    `{id, label, reasoning}` per entry (Rick's PR #106 review item 3) -- Morty's model picker
    (F10) needs a label to show and a reasoning flag to group by, not a bare id.

    Rick's PR #106 review item 1/3: there is NO carve-out for the pipeline's own `default` --
    an unselectable default (e.g. its deployment only resolves through the
    `AZURE_OPENAI_REALTIME_DEPLOYMENT` back-compat fallback, never `AZURE_AI_MODEL_DEPLOYMENTS`
    itself) is deliberately left OUT of this list: the picker must never offer a model that
    would 404 if explicitly requested by id (`model_catalog.is_selectable` already encodes that
    exact rule -- see its own docstring). Every enabled persona's own default is still
    guaranteed catalogued (`ModelCatalog.validate_persona_defaults` fails startup otherwise);
    whether it's also independently *deployed* (and therefore listed here) is a separate,
    narrower question this function answers honestly rather than papering over."""
    return [
        {"id": model_id, "label": model_catalog.get(model_id).label, "reasoning": model_catalog.get(model_id).reasoning}
        for model_id in pipeline_cfg.allowed
        if model_catalog.is_selectable(model_id, pipeline_name)
    ]


def _model_pipelines_body(models, model_catalog: ModelCatalog) -> dict:
    """The selectable `models` per pipeline (design doc section 5.2/7; Rick's PR #106 review item
    3: ALWAYS only selectable models, `{id, label, reasoning}` shaped); only the pipelines a
    persona actually declares (`cascade` is optional). *model_catalog* is mandatory --
    there is no unfiltered fallback shape any more.

    PR #106 review round 3 (Rick's nit, issue #75): each pipeline's list is keyed `models`, not
    `allowed` -- it means *selectable* now (catalog ∩ deployment ∩ persona-allowed), not the
    persona pack's own raw `models.<pipeline>.allowed` config field, which this list is filtered
    FROM, not identical to. Renamed before #110 (frontend) starts consuming it as
    `{id, label, reasoning}` objects."""
    body = {
        "realtime": {
            "default": models.realtime.default,
            "models": _selectable_models(models.realtime, "realtime", model_catalog),
        }
    }
    if models.cascade is not None:
        body["cascade"] = {
            "default": models.cascade.default,
            "models": _selectable_models(models.cascade, "cascade", model_catalog),
        }
    return body


def _persona_summary_body(persona: Persona) -> dict:
    """One entry of `GET /api/personas`'s `personas` list (design doc section 5.2)."""
    return {
        "id": persona.id,
        "displayName": persona.manifest.displayName,
        "logoUrl": _persona_logo_url(persona),
        "theme": persona.manifest.ui.theme.model_dump(exclude_none=True),
    }


def _backend_entries() -> list[dict]:
    """`backends` entries in `GET /api/personas` (design doc section 5.2/10.1, Rick's PR
    #102 review item 4): each backend's PUBLIC BASE URL, not `/realtime` -- so the F11
    header switch (design doc section 8) can link across hostnames, not just paths on this
    one origin. Built from `BACKEND_URI` / `BACKEND_DOTNET_URI` (azd outputs from #93; see
    `.env-sample`). `python` always has an entry: an unset `BACKEND_URI` (local dev) falls
    back to `""` (same origin) rather than being omitted, since this process IS the python
    backend. `dotnet` is omitted entirely when `BACKEND_DOTNET_URI` isn't set -- e.g. no
    .NET backend deployed for this environment -- rather than reported with a placeholder.
    """
    entries = [{"id": "python", "url": (os.environ.get("BACKEND_URI") or "").strip()}]
    dotnet_uri = (os.environ.get("BACKEND_DOTNET_URI") or "").strip()
    if dotnet_uri:
        entries.append({"id": "dotnet", "url": dotnet_uri})
    return entries


def _personas_index_body(catalog: PersonaCatalog) -> dict:
    """`GET /api/personas` response body (design doc section 5.2), enabled personas only."""
    return {
        "default": catalog.default_persona_id,
        "personas": [_persona_summary_body(catalog.get(persona_id)) for persona_id in catalog.ids],
        "backends": _backend_entries(),
    }


def _persona_detail_body(persona: Persona, model_catalog: ModelCatalog) -> dict:
    """`GET /api/personas/{id}` response body (design doc section 5.2): `roleName`, the
    pack's `ui` block, plus `voice.default`, `locales`, `features.dayparts`, `menuUrl`, and
    the selectable `models` per pipeline. Callers must check the persona is enabled first
    (404 otherwise) -- this function assumes it already is. *model_catalog* is mandatory
    (Rick's PR #106 review item 3) -- see `_model_pipelines_body`.

    Issue #164 E2: `taxRate` (the pack's own `pricing.taxRate`, e.g. "0.08") is forwarded here
    so the ticket can render "Tax (N%)" -- it was previously never exposed on this endpoint at
    all (only consumed server-side, in `tools.py`'s order math)."""
    manifest = persona.manifest
    return {
        "id": persona.id,
        "roleName": manifest.roleName,
        **manifest.ui.model_dump(exclude_none=True),
        "voice": {"default": manifest.voice.default},
        "locales": manifest.locales.model_dump(exclude_none=True),
        "features": {"dayparts": manifest.features.dayparts},
        "menuUrl": _persona_menu_url(persona),
        "models": _model_pipelines_body(manifest.models, model_catalog),
        "taxRate": manifest.pricing.taxRate,
    }


def _resolve_persona_asset_path(persona: Persona, requested_path: str) -> Path | None:
    """Resolve `requested_path` (the tail of `/personas/{id}/assets/{requested_path}`) to a
    real file under `persona.assets_dir`, or ``None`` if it doesn't exist or the path
    doesn't stay under that directory (Rick's PR #102 review item 5).

    aiohttp's router percent-decodes the raw URL before populating `match_info`, so any
    encoded traversal variant (``%2e%2e%2f``, double-encoded, etc.) already looks like a
    plain ``../`` by the time it reaches here -- there is nothing extra to decode.

    Traversal defense is structural, not string-matching: split on path separators, reject
    any segment that is empty, ``.``, ``..``, or looks like a Windows drive letter (``C:``),
    THEN join those pre-validated segments onto ``assets_dir`` one at a time. This avoids
    the classic pathlib pitfall where ``Path(base) / "/etc/passwd"`` silently discards
    `base` because the right-hand operand is absolute -- since no individual validated
    segment can itself be absolute, that substitution can never happen here. Finally,
    ``Path.resolve()`` (which follows symlinks) must land back under the assets directory's
    own resolved real path -- this is what catches a symlink planted inside the pack that
    points back out of it (a segment-only check would miss that).
    """
    if not requested_path or "\x00" in requested_path:
        return None
    segments = requested_path.replace("\\", "/").split("/")
    if any(seg in ("", ".", "..") or ":" in seg for seg in segments):
        return None
    if PurePosixPath(requested_path).is_absolute() or PureWindowsPath(requested_path).is_absolute():
        return None
    assets_root = persona.assets_dir.resolve()
    candidate = assets_root.joinpath(*segments)
    try:
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError):
        return None
    try:
        resolved.relative_to(assets_root)
    except ValueError:
        return None
    if not resolved.is_file():
        return None
    return resolved


# Non-blocking (Rick's PR #102 review): explicit content types for persona asset files,
# instead of trusting `mimetypes.guess_type`'s OS-dependent registry (e.g. `.svg`/`.ico` are
# missing from Python's default table on some platforms, silently falling back to
# `application/octet-stream`; `.wav` can resolve to `audio/x-wav` instead of `audio/wav`
# depending on the local `mimetypes` database).
_ASSET_CONTENT_TYPES = {
    ".svg": "image/svg+xml",
    ".wav": "audio/wav",
    ".ico": "image/x-icon",
    ".json": "application/json",
}


def _asset_cache_headers(response: web.Response, request: web.Request, expected_v: str | None) -> None:
    """Content-hash versioned caching (Rick's PR #102 review item 2): year-long immutable
    caching on a URL with no content-hash pinning it to a specific set of bytes was wrong --
    a later pack update could silently serve stale content for a year to anyone still holding
    the unversioned URL. Only a request whose `?v=` query param matches *this file's current*
    content hash (`expected_v`, computed fresh per-request by the caller via `_content_hash`)
    is safe to mark immutable: that exact URL can never resolve to different bytes later,
    because a future edit changes the file's hash and therefore the URL a fresh
    `_persona_logo_url`/`_persona_menu_url` call would mint. An unversioned request (no `v`
    at all -- e.g. a client that cached an old response body containing a pre-versioning URL,
    or hit the route directly) or one carrying a stale/mismatched hash gets the same short,
    revalidate-often policy as any other mutable static file (`_STATIC_DEFAULT_MAX_AGE` --
    previously defined but unused; this is that "short/no-cache policy consistent with
    existing static handling" now actually wired up), never the immutable policy.
    """
    requested_v = request.query.get("v")
    if expected_v is not None and requested_v == expected_v:
        response.headers["Cache-Control"] = f"public, max-age={_STATIC_IMMUTABLE_MAX_AGE}, immutable"
    else:
        response.headers["Cache-Control"] = f"public, max-age={_STATIC_DEFAULT_MAX_AGE}"
    # Non-blocking (Rick's PR #102 review): defense-in-depth against content-type sniffing on
    # these persona-supplied (not first-party-authored) files.
    response.headers["X-Content-Type-Options"] = "nosniff"


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


def register_persona_routes(app: web.Application, catalog: PersonaCatalog, model_catalog: ModelCatalog) -> None:
    """Register `/api/personas`, `/api/personas/{id}`, `/personas/{id}/assets/*` and
    `/personas/{id}/menu.json` against `catalog` (issue #74, design doc section 5.2).

    A standalone, module-level function -- not a `create_app()` closure -- specifically so
    conformance tests can register these routes on a small standalone `web.Application`
    against a fixture catalog, hitting the real HTTP routes (path matching, traversal
    handling, status codes) without needing `create_app()`'s full Azure OpenAI/Search
    startup dependencies.

    *model_catalog* (issue #75, Rick's PR #106 review item 3) is mandatory -- there is no
    unfiltered fallback shape any more: `/api/personas/{id}`'s `models` block ALWAYS narrows to
    what's actually selectable (catalog ∩ deployment ∩ persona-allowed, design doc section 7.3),
    `{id, label, reasoning}` shaped, never bare ids.
    """

    async def get_personas(_request: web.Request) -> web.Response:
        return web.json_response(_personas_index_body(catalog))

    async def get_persona_detail(request: web.Request) -> web.Response:
        persona_id = request.match_info["persona_id"]
        if persona_id not in catalog:
            return web.json_response({"error": f"Unknown or disabled persona: {persona_id!r}"}, status=404)
        return web.json_response(_persona_detail_body(catalog.get(persona_id), model_catalog))

    # ── Persona static asset routes (issue #74, design doc section 5.2, Rick's PR #102
    # review item 5): serve only the ENABLED pack's own files, matching `logoUrl`/
    # `menuUrl` in the persona summary/detail bodies above. ──
    async def get_persona_asset(request: web.Request) -> web.Response:
        persona_id = request.match_info["persona_id"]
        if persona_id not in catalog:
            return web.json_response({"error": f"Unknown or disabled persona: {persona_id!r}"}, status=404)
        asset_path = request.match_info["asset_path"]
        resolved = _resolve_persona_asset_path(catalog.get(persona_id), asset_path)
        if resolved is None:
            return web.json_response({"error": f"Unknown persona asset: {asset_path!r}"}, status=404)
        resp = web.FileResponse(resolved)
        # Non-blocking (Rick's PR #102 review): pin the content type for known persona-asset
        # extensions instead of trusting the local `mimetypes` registry (see
        # `_ASSET_CONTENT_TYPES`'s docstring). Must be set before `_asset_cache_headers`
        # returns the response, since `FileResponse` only guesses a Content-Type if one
        # isn't already present in its headers at prepare() time.
        content_type = _ASSET_CONTENT_TYPES.get(resolved.suffix.lower())
        if content_type is not None:
            resp.headers["Content-Type"] = content_type
        _asset_cache_headers(resp, request, _content_hash(resolved))
        return resp

    async def get_persona_menu(request: web.Request) -> web.Response:
        persona_id = request.match_info["persona_id"]
        if persona_id not in catalog:
            return web.json_response({"error": f"Unknown or disabled persona: {persona_id!r}"}, status=404)
        menu_path = catalog.get(persona_id).menu_path
        if not menu_path.is_file():
            return web.json_response({"error": f"No menu data for persona: {persona_id!r}"}, status=404)
        # A plain Response (not FileResponse) so the existing gzip _compression_middleware
        # (which explicitly skips FileResponse) applies to this JSON payload too, matching
        # design doc section 5.2 ("the existing compression and caching middleware").
        resp = web.Response(body=menu_path.read_bytes(), content_type="application/json")
        _asset_cache_headers(resp, request, _content_hash(menu_path))
        return resp

    app.add_routes([
        web.get('/api/personas', get_personas, name='personas-index'),
        web.get('/api/personas/{persona_id}', get_persona_detail, name='persona-detail'),
        web.get('/personas/{persona_id}/menu.json', get_persona_menu, name='persona-menu'),
        # Named 'persona-asset' on purpose (entra_auth.py's PERSONA_ASSET_ROUTE_NAME):
        # this ONE route is anonymous for public-branding extensions and Entra-
        # protected for everything else (issue #144, design doc section 18.2).
        web.get('/personas/{persona_id}/assets/{asset_path:.*}', get_persona_asset, name='persona-asset'),
    ])


async def create_app() -> web.Application:
    """Configure and return the aiohttp application for realtime ordering."""

    # 0. Production guard (Rick's PR #118 review, required item 4): refuse to start with
    # CONFORMANCE_TEST_HOOKS=1 (conformance_hooks.py's fake credentials/timers/HTTP overrides
    # active) AND RUNNING_IN_PRODUCTION both set -- a deployed app must never run with test
    # hooks live, and this must fail loudly at startup rather than silently 401ing on the
    # first request or, worse, accepting a fake bearer token in prod. Checked before even the
    # dev-mode .env load below and before the required-env-vars check, since this is the one
    # startup failure that must never be masked by any other. Uses hooks_enabled_now() (a live
    # re-check), not the frozen HOOKS_ENABLED constant, so this guard reflects this process's
    # actual environment even if some earlier import already froze that constant differently.
    if conformance_hooks.hooks_enabled_now() and _get_bool_env("RUNNING_IN_PRODUCTION", False):
        logger.critical(
            "FATAL: CONFORMANCE_TEST_HOOKS=1 is set alongside RUNNING_IN_PRODUCTION=1. "
            "Test-only fake credentials, timers, and HTTP overrides must never be active in "
            "a deployed environment. Refusing to start."
        )
        sys.exit(1)
    _startup_checks["prod_guard"] = True

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

    # 1b. Resolve the Entra auth mode (issue #144, ADR-002, design doc section 18.5).
    # Fails fast on any invalid or ambiguous configuration -- the process must never
    # listen with an unclear auth story, same as every other startup check here. Read
    # AFTER the .env load above so local dev's ENTRA_* values, if any, are already in
    # os.environ.
    try:
        entra_settings = entra_auth.resolve_settings(os.environ)
    except EntraConfigError as exc:
        logger.critical("FATAL: Entra auth configuration is invalid: %s", exc)
        sys.exit(1)

    # 2. Load and validate every enabled persona pack (issue #70). Refuses to start on an
    # invalid pack, naming the persona, file and field. Fails fast, same as prompt loading below.
    global _persona_catalog
    try:
        _persona_catalog = PersonaCatalog.load()
    except PersonaValidationError as exc:
        logger.critical("FATAL: Failed to load persona pack(s) — %s", exc)
        sys.exit(1)
    _startup_checks["personas_loaded"] = True

    # 2b. Load and validate the shared realtime/cascade model catalog (issue #75,
    # design doc section 7): config.yaml's `models.catalog` (layer 1) plus the
    # AZURE_AI_MODEL_DEPLOYMENTS deployment-name map (layer 2, populated by #93's Bicep
    # outputs). Fails fast on a malformed catalog entry, same pattern as persona/prompt
    # loading above -- an unusable catalog should stop startup, not surface as a confusing
    # 404 on the first `?model=` request. Not tracked in `_startup_checks`/`/health` (unlike
    # personas/prompts/env vars above): those three predate #75 and are covered by existing
    # tests that assert an exact set of `/health` keys; a load failure here still aborts
    # startup via `sys.exit(1)`, same as they do.
    try:
        model_catalog = ModelCatalog.load()
    except ModelValidationError as exc:
        logger.critical("FATAL: Failed to load model catalog — %s", exc)
        sys.exit(1)

    # 2c. Rick's PR #106 review item 1 (second half): fail fast if any ENABLED persona's own
    # pipeline default model isn't in the catalog for that pipeline -- an unusable default
    # should stop startup, not surface as a confusing 404 on the first `?model=`-omitted
    # request. Must run after both catalogs above are loaded.
    try:
        model_catalog.validate_persona_defaults(_persona_catalog)
    except ModelValidationError as exc:
        logger.critical("FATAL: Persona/model catalog mismatch — %s", exc)
        sys.exit(1)

    # 3. Build one PromptLoader per enabled persona pack (issue #74; #92 review note --
    # removes the single hardcoded default-brand PromptLoader). Each loader reads from its own
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

    entra_middleware = entra_auth.create_middleware(entra_settings)
    app = web.Application(
        # Entra runs FIRST (18.3's check order: Entra, then Origin, then session
        # token, then limits/404s) -- middlewares wrap the handler in list order, so
        # the first entry here is the outermost, and therefore the first to run.
        middlewares=[entra_middleware, _compression_middleware],
        client_max_size=conn_cfg.get("client_max_size_bytes", 4 * 1024 * 1024),
    )

    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=llm_endpoint,
        deployment=llm_deployment,
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "marin"),
        prompt_loader=prompt_loader,
    )
    # Issue #144/18.3: in Entra mode, require_session_token is FORCED on (config.yaml
    # can't turn it off) and /realtime's session token is bound to the Entra
    # principal's oid. In Development pass-through it follows config.yaml, as today.
    rtmt.entra_mode = entra_settings.mode == entra_auth.Mode.ENTRA
    # Shared HMAC secret for session tokens (APP_SESSION_SECRET; random for local dev)
    app_secret = load_app_secret()
    rtmt.app_secret = app_secret
    configure_realtime_model(rtmt, model_cfg)
    rtmt.system_message = prompt_loader.get_system_prompt()

    # Issue #74: the enabled-persona catalog and one PromptLoader per persona, so
    # _websocket_handler can resolve `?persona=`, reject an unknown one with 404 before the
    # WebSocket upgrade, and _forward_messages can use each session's own bound persona's
    # system prompt and default voice instead of the deployment-wide defaults above (which
    # remain exactly the default persona's -- unchanged behavior when `?persona` is
    # omitted, since it resolves to `_persona_catalog.default_persona_id` today).
    rtmt.persona_catalog = _persona_catalog
    rtmt.persona_prompt_loaders = prompt_loaders

    # Issue #75: the shared model catalog + deployment map, so `_websocket_handler` can
    # resolve/validate `?model=` (`RTMiddleTier.resolve_model` -> `resolve_realtime_model`)
    # against something other than the always-empty catalog `RTMiddleTier.__init__` defaults
    # to. `rtmt.processor_registry` is reassigned again below, once the cascade processor
    # (#82) is constructed, to include it alongside `rtmt` -- this intermediate value keeps
    # the realtime-only path correct even if that later assignment is ever removed.
    rtmt.model_catalog = model_catalog
    rtmt.processor_registry = ProcessorRegistry([rtmt])

    # Issue #74 (Rick's PR #102 review, item 2): every other module that resolves a
    # persona/menu when none is explicitly passed (order_state, tools, and this module's own
    # asset/menu routes below) goes through `default_persona`, not a private duplicate of the
    # catalog. Point it at the one catalog this process just validated, so those call sites
    # see the exact same enabled packs / default id as the WebSocket path above.
    default_persona.configure_default_catalog(_persona_catalog)

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

    # Issue #82: the cascade pipeline processor (STT -> Foundry chat model with tool
    # calling -> TTS), registered alongside `rtmt` on the same `ProcessorRegistry` seam
    # #75 built for exactly this -- `_websocket_handler` dispatches `?model=` to whichever
    # processor's catalog entry claims it, so no rtmt.py edit is needed. It shares rtmt's
    # own `tools` dict (Beth's #77 tools, unedited) and `SessionManager` instance so
    # tool calling and session-lifecycle behavior (idle timeout, concurrency limit,
    # session metadata/round-trip tokens) are identical on both pipelines. The Foundry
    # chat model client uses DefaultAzureCredential only (design doc 7.4 / issue #82) --
    # never an API key -- via a dedicated async credential (the SDK's async
    # `ChatCompletionsClient` needs an async `get_token`, unlike the sync `credential`
    # above used for the realtime/search clients). conformance_hooks.cascade_credential()
    # substitutes a fake, static-token credential only when CONFORMANCE_TEST_HOOKS=1 AND
    # CONFORMANCE_CASCADE_FAKE_TOKEN are both set (the conformance harness's own child
    # process); every other process (real deployments, `python -m pytest`, a developer's
    # local run) gets exactly today's real DefaultAzureCredential, unchanged.
    cascade_credential = conformance_hooks.cascade_credential() or AsyncDefaultAzureCredential()
    cascade_processor = CascadeProcessor(
        tools=rtmt.tools,
        sessions=rtmt._sessions,
        persona_catalog=_persona_catalog,
        persona_prompt_loaders=prompt_loaders,
        persona_tool_schemas=rtmt.persona_tool_schemas,
        model_catalog=model_catalog,
        foundry_endpoint=os.environ.get("AZURE_AI_FOUNDRY_ENDPOINT"),
        audio_endpoint=llm_endpoint,
        credential=cascade_credential,
        default_voice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "marin"),
    )
    rtmt.processor_registry = ProcessorRegistry([rtmt, cascade_processor])

    rtmt.attach_to_app(app, "/realtime")

    # ── HMAC Session Token Endpoint (Task 4) ──
    async def get_session_token(request: web.Request) -> web.Response:
        # Issue #144/18.3: the minted token gains an `oid` claim, the caller's Entra
        # object id, whenever a principal is present (both Entra mode and the
        # Development pass-through's synthetic principal set it -- see
        # entra_auth.py's middleware). /realtime later rejects unless that oid
        # matches the Entra token's own oid.
        principal = request.get("principal")
        oid = principal.get("oid") if principal else None
        token = create_hmac_token(app_secret, expiry_seconds=900, oid=oid)
        return web.json_response({"token": token})

    current_directory = Path(__file__).parent
    app.add_routes([
        web.get('/', _index_handler, name='index'),
        web.get('/health', _health_handler, name='health'),
        web.get('/api/auth/session', get_session_token, name='session-token'),
    ])
    # ── Persona discovery + static asset routes (issue #74, design doc section 5.2) ──
    register_persona_routes(app, _persona_catalog, model_catalog)
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


async def create_runner() -> web.AppRunner:
    """Async factory for gunicorn's `aiohttp.GunicornWebWorker` (issue #144, design
    doc section 18.4). The worker accepts an async factory that returns a
    `web.AppRunner` and uses that runner as-is, so the runner itself -- not
    gunicorn's `--keep-alive`/`--graceful-timeout` flags, which the worker then
    ignores -- carries the real keep-alive and shutdown timeouts (65 s and 28.5 s,
    the worker's 95% of the 30 s graceful timeout the arbiter still enforces).

    `create_app()` still fails fast with `sys.exit(1)` on a startup error. Under
    `GunicornWebWorker` a `SystemExit` doesn't stop the process the way an
    `Exception` does (gunicorn 23 `arbiter.py`): the arbiter's boot-failure path
    (master exit code 3) only triggers on an `Exception` raised before the worker
    has booted, not a `SystemExit`. Left unwrapped, the worker would exit 1 and the
    master would respawn it in a tight loop -- about 100 boots in 8 seconds, TCP
    accepting connections and HTTP requests hanging the whole time. That still
    fails closed, but it floods logs and makes "fails fast" untrue in the
    container. So this wraps `create_app()` and re-raises as a plain `RuntimeError`,
    which gunicorn's arbiter treats as a genuine boot failure: it logs
    "Worker failed to boot." once and the master exits 3, never a respawn loop.
    """
    try:
        app = await create_app()
    except SystemExit as exc:
        raise RuntimeError("startup failed") from exc
    return web.AppRunner(
        app,
        logger=logging.getLogger("gunicorn.error"),
        access_log=logging.getLogger("gunicorn.access"),
        keepalive_timeout=65,
        shutdown_timeout=28.5,
        **access_log_kwargs(),
    )


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
        **access_log_kwargs(),
    )
