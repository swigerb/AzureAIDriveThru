"""Post-deploy smoke check for the realtime session configuration.

Builds the EXACT session.update payloads the middle tier sends (bootstrap,
relayed browser update, minimal fallback) from the app code and config.yaml,
sends them to a live Azure OpenAI realtime deployment, and fails unless every
one comes back as `session.updated` with all tools registered, tool_choice
"auto", the instructions applied, and no `error`.

Why: GA rejects an invalid session.update WHOLESALE. One bad field (a voice
change after audio, `reasoning` on a non-reasoning model, ...) silently drops
the tools and the carhop takes orders it never records. This demo has lost its
tools that way twice.

It also checks, unless --skip-transcription, that guest speech is actually
transcribed with the configured transcription model: Azure accepts any model
name in session.update and only fails later, per turn, with DeploymentNotFound.
The test audio is the realtime model reading TRANSCRIPTION_PHRASE aloud, and the
transcript must match that phrase word for word (after normalising case,
punctuation and spacing); a model that answered or paraphrased the phrase
instead of reading it fails the check rather than passing it on a technicality.

Usage (from the repo root, with `az login` / `azd auth login` done):

    python scripts/smoke_realtime.py                       # uses azd env / env vars
    python scripts/smoke_realtime.py --deployment gpt-realtime-1.5
    python scripts/smoke_realtime.py --endpoint https://<aoai>.openai.azure.com/ --deployment gpt-realtime-2.1
    python scripts/smoke_realtime.py --persona <persona-id> --model gpt-realtime-mini

    python scripts/smoke_realtime.py --tenant <tenant-id> --subscription <subscription-id>

Endpoint/deployment default to AZURE_OPENAI_EASTUS2_ENDPOINT /
AZURE_OPENAI_REALTIME_DEPLOYMENT, read from the environment or `azd env
get-values`. `--persona <id>` (#83, P2-14) picks which persona pack's prompts
and tool schemas to smoke-test (default: the persona catalog's own default
persona -- never a hardcoded brand); `--model <catalog-id>` resolves a
deployment via the SAME `AZURE_AI_MODEL_DEPLOYMENTS` map (config.yaml
`models.catalog` + that env var) `app.create_app()` uses, taking precedence
over --deployment/env when given. Auth: AZURE_OPENAI_EASTUS2_API_KEY if set,
else an Entra ID token from the resource's tenant (needs "Cognitive Services
OpenAI User" on the resource, which `azd up` grants the deploying principal).
The tenant and subscription come from --tenant / --subscription, else
AZURE_TENANT_ID / AZURE_SUBSCRIPTION_ID in the environment, else the azd env.
With a subscription, `az` is asked for a token for that subscription (the
sign-in that owns it, without changing the global `az account` default); with
a tenant, azd and az are pinned to it; with neither, DefaultAzureCredential.
So a machine signed in to several tenants no longer gets HTTP 400 "Tenant
provided in token does not match resource token".

Exit codes: 0 = all checks passed, 1 = a check failed, 2 = could not run
(missing endpoint/deployment, auth or network failure).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import copy
import difflib
import io
import json
import os
import re
import subprocess
import sys
import time
import unicodedata
import wave
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
BACKEND_DIR = REPO_ROOT / "app" / "backend"
sys.path.insert(0, str(BACKEND_DIR))

import aiohttp  # noqa: E402
from azure.core.credentials import AzureKeyCredential  # noqa: E402

from config_loader import get_config  # noqa: E402
from model_catalog import ModelCatalog, ModelValidationError  # noqa: E402
from persona_loader import Persona, PersonaCatalog, PersonaValidationError  # noqa: E402
from prompt_loader import PromptLoader  # noqa: E402
from rtmt import RTMiddleTier, Tool, configure_realtime_model  # noqa: E402

EXPECTED_TOOLS = ("search", "update_order", "get_order", "reset_order")

# The guest turn sent for the live one-tool-call check (#302): generic enough that every
# persona's own system prompt/menu should route it to the "search" tool rather than
# update_order/get_order/reset_order.
SEARCH_TOOL_GUEST_TEXT = "What drinks do you have?"

# What app/frontend/src/hooks/useRealtime.tsx startSession() sends.
BROWSER_SESSION = {
    "turn_detection": {"type": "server_vad", "threshold": 0.7, "prefix_padding_ms": 300, "silence_duration_ms": 500},
    "input_audio_transcription": {"model": "whisper-1"},
}

# Fallback phrase when no persona (or a persona with too few menu items) is available. Kept
# brand-neutral -- the normal path (#83, P2-14) builds a persona-specific phrase instead, see
# `_transcription_phrase_for`.
_DEFAULT_TRANSCRIPTION_PHRASE = "Hi, can I get a small drink and a side, please?"
# Minimum similarity (difflib ratio over the normalised text) between the phrase
# and its transcript. Tolerates a transcriber's spelling ("lime aid") but not an
# answer or a paraphrase.
TRANSCRIPT_MATCH_THRESHOLD = 0.85


def _transcription_phrase_for(persona: Persona | None) -> str:
    """A guest order phrase naming two of *persona*'s own real menu items, so the live
    transcription check (#83, P2-14) exercises each persona's own vocabulary instead of a
    single hardcoded brand's. Falls back to `_DEFAULT_TRANSCRIPTION_PHRASE` when there's no
    persona (legacy no-`--persona` invocation) or its menu has fewer than two items."""
    if persona is None:
        return _DEFAULT_TRANSCRIPTION_PHRASE
    import menu_utils
    catalog = menu_utils.get_catalog_for_persona(persona)
    names = [fields["name"] for fields in catalog.item_fields.values() if fields.get("name")]
    if len(names) < 2:
        return _DEFAULT_TRANSCRIPTION_PHRASE
    return f"Hi, can I get a {names[0]} and a {names[1]}, please?"


class SmokeError(Exception):
    """The smoke check could not run (exit 2), as opposed to a failed check."""


def _azd_env_values() -> dict[str, str]:
    try:
        out = subprocess.run(["azd", "env", "get-values"], capture_output=True, text=True, timeout=30,
                             cwd=REPO_ROOT, shell=(os.name == "nt"))
    except (OSError, subprocess.SubprocessError):
        return {}
    if out.returncode != 0:
        return {}
    values = {}
    for line in out.stdout.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip().strip('"')
    return values


def resolve_setting(name: str, cli_value: str | None, azd_values: dict[str, str]) -> str | None:
    return cli_value or os.environ.get(name) or azd_values.get(name) or None


def _tool_schemas(prompt_loader: PromptLoader) -> list[dict]:
    """Same resolution as tools.attach_tools_rtmt: YAML schema, else the hardcoded one."""
    import tools
    schema_map = {s["name"]: s for s in prompt_loader.get_tool_schemas()}
    fallback = {
        "search": tools.search_tool_schema,
        "update_order": tools.update_order_tool_schema,
        "get_order": tools.get_order_tool_schema,
        "reset_order": tools.reset_order_tool_schema,
    }
    return [schema_map.get(name, fallback[name]) for name in EXPECTED_TOOLS]


def build_middle_tier(endpoint: str, deployment: str, voice: str | None = None,
                      environ: dict | None = None, persona: Persona | None = None) -> RTMiddleTier:
    """An RTMiddleTier configured exactly like app.create_app() configures it
    (config.yaml `model:` + env overrides, real system prompt and tool schemas),
    without starting a server or touching Azure AI Search.

    *persona* (#83, P2-14): when given, prompts/tool schemas load from that persona's own
    pack (`brand=persona.id, prompts_dir=persona.prompts_dir`) instead of `PromptLoader`'s
    single-brand default -- lets this smoke check exercise any enabled persona pack, not only
    whichever one happens to be the deployment default."""
    env = os.environ if environ is None else environ
    model_cfg = get_config().get("model", {})
    prompt_loader = (
        PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir)
        if persona is not None
        else PromptLoader()
    )
    rtmt = RTMiddleTier(
        endpoint=endpoint,
        deployment=deployment,
        credentials=AzureKeyCredential("unused"),
        voice_choice=voice or env.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or model_cfg.get("default_voice", "marin"),
    )
    configure_realtime_model(rtmt, model_cfg, env)
    rtmt.system_message = prompt_loader.get_system_prompt()
    for schema in _tool_schemas(prompt_loader):
        rtmt.tools[schema["name"]] = Tool(target=None, schema=schema)
    return rtmt


_TOKEN_SCOPE = "https://cognitiveservices.azure.com/.default"


def _credentials(tenant_id: str | None, subscription_id: str | None) -> list:
    """Credentials to try, most specific first.

    The token must come from the resource's tenant. Following whatever `az` or
    `azd` default is active gets HTTP 400 "Tenant provided in token does not match
    resource token" as soon as that default is another tenant, which is common on
    a machine with several sign-ins.
    """
    from azure.identity import (
        AzureCliCredential,
        AzureDeveloperCliCredential,
        DefaultAzureCredential,
    )
    creds = []
    if subscription_id:
        # Picks the `az` sign-in that owns the azd env's subscription, without
        # changing the global `az account` default.
        creds.append(AzureCliCredential(subscription=subscription_id, process_timeout=60))
    if tenant_id:
        creds.append(AzureDeveloperCliCredential(tenant_id=tenant_id, process_timeout=60))
        creds.append(AzureCliCredential(tenant_id=tenant_id, process_timeout=60))
    if not creds:
        creds.append(DefaultAzureCredential(exclude_interactive_browser_credential=True))
    return creds


def get_auth_headers(tenant_id: str | None = None, subscription_id: str | None = None) -> dict[str, str]:
    if key := os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY"):
        return {"api-key": key}
    errors = []
    try:
        credentials = _credentials(tenant_id, subscription_id)
    except Exception as exc:  # noqa: BLE001 - any credential failure means "cannot run"
        raise SmokeError(f"could not get an Entra ID token for Azure OpenAI: {exc}") from exc
    # Tried in turn: unlike ChainedTokenCredential, a hard auth error (e.g. azd
    # signed in as a user who isn't in the tenant) moves on to the next one.
    for credential in credentials:
        try:
            return {"Authorization": "Bearer " + credential.get_token(_TOKEN_SCOPE).token}
        except Exception as exc:  # noqa: BLE001
            errors.append(f"{type(credential).__name__}: {str(exc).splitlines()[0] if str(exc) else exc!r}")
    raise SmokeError("could not get an Entra ID token for Azure OpenAI: " + "; ".join(errors))


def realtime_url(endpoint: str, deployment: str) -> str:
    host = endpoint.split("://", 1)[-1].rstrip("/")
    # Plain http only for a local fake endpoint (tests); Azure is always wss.
    scheme = "ws" if endpoint.startswith("http://") else "wss"
    return f"{scheme}://{host}/openai/v1/realtime?model={deployment}"


async def _next_event(ws, timeout: float) -> dict | None:
    msg = await asyncio.wait_for(ws.receive(), timeout=timeout)
    if msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
        raise SmokeError(f"upstream closed the socket ({msg.type.name}: {msg.data!r})")
    if msg.type != aiohttp.WSMsgType.TEXT:
        return None
    return json.loads(msg.data)


async def send_session_update(ws, payload: str, timeout: float) -> tuple[dict | None, dict | None]:
    """Send one session.update; return (session.updated session, error event)."""
    await ws.send_str(payload)
    deadline = time.monotonic() + timeout
    while (remaining := deadline - time.monotonic()) > 0:
        try:
            event = await _next_event(ws, remaining)
        except TimeoutError:
            break
        if event is None:
            continue
        if event.get("type") == "error":
            return None, event
        if event.get("type") == "session.updated":
            return event.get("session") or {}, None
    return None, {"error": {"code": "timeout", "message": f"no session.updated within {timeout:.0f}s"}}


def check_session(label: str, sent: dict, echoed: dict | None, error: dict | None,
                  expect_reasoning: dict | None) -> list[str]:
    """Return a list of failure strings (empty = pass)."""
    if error is not None:
        err = error.get("error") or {}
        return [f"{label}: REJECTED code={err.get('code')} param={err.get('param')} message={err.get('message')}"]
    failures = []
    tools = sorted(t.get("name") for t in echoed.get("tools") or [])
    if tools != sorted(EXPECTED_TOOLS):
        failures.append(f"{label}: tools registered {tools}, expected {sorted(EXPECTED_TOOLS)}")
    if echoed.get("tool_choice") != "auto":
        failures.append(f"{label}: tool_choice={echoed.get('tool_choice')!r}, expected 'auto'")
    if not echoed.get("instructions"):
        failures.append(f"{label}: instructions were not applied")
    if expect_reasoning is not None and echoed.get("reasoning") != expect_reasoning:
        failures.append(f"{label}: reasoning={echoed.get('reasoning')!r}, expected {expect_reasoning!r}")
    sent_voice = ((sent.get("audio") or {}).get("output") or {}).get("voice")
    echoed_voice = ((echoed.get("audio") or {}).get("output") or {}).get("voice")
    if sent_voice and echoed_voice != sent_voice:
        failures.append(f"{label}: voice={echoed_voice!r}, expected {sent_voice!r}")
    return failures


async def check_session_updates(rtmt: RTMiddleTier, url: str, headers: dict, timeout: float) -> tuple[list[str], list[str]]:
    tool_schemas = [tool.schema for tool in rtmt.tools.values()]
    payloads = [
        ("bootstrap", rtmt.build_bootstrap_session_update(system_message=rtmt.system_message, tool_schemas=tool_schemas)),
        ("relayed browser session.update",
         json.dumps({"type": "session.update", "event_id": "smoke_relayed",
                     "session": rtmt._build_session(copy.deepcopy(BROWSER_SESSION), system_message=rtmt.system_message, tool_schemas=tool_schemas)})),
        ("minimal fallback", rtmt.build_fallback_session_update(system_message=rtmt.system_message, tool_schemas=tool_schemas)),
    ]
    expect_reasoning = {"effort": rtmt.reasoning_effort} if rtmt.reasoning_enabled() else None
    failures: list[str] = []
    report: list[str] = []
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        for label, payload in payloads:
            sent = json.loads(payload)["session"]
            echoed, error = await send_session_update(ws, payload, timeout)
            problems = check_session(label, sent, echoed, error,
                                     expect_reasoning if "reasoning" in sent else None)
            failures += problems
            if problems:
                report += [f"FAIL  {p}" for p in problems]
            else:
                report.append(f"PASS  {label}: session.updated, {len(echoed.get('tools') or [])} tools, "
                              f"tool_choice=auto, reasoning={echoed.get('reasoning')}")
    return failures, report


def get_search_credential(tenant_id: str | None = None, subscription_id: str | None = None):
    """A credential for Azure AI Search's own ``SearchClient`` (#302's one-tool-call check):
    ``AZURE_SEARCH_API_KEY`` if set (mirrors ``app.py``'s own ``search_credential``
    resolution), else the SAME tenant-pinned ``azure.identity`` credential chain as
    ``get_auth_headers``/``_credentials`` -- ``SearchClient`` takes a credential OBJECT
    (not a header dict), so this returns the credential itself."""
    if key := os.environ.get("AZURE_SEARCH_API_KEY"):
        return AzureKeyCredential(key)
    errors = []
    try:
        credentials = _credentials(tenant_id, subscription_id)
    except Exception as exc:  # noqa: BLE001 - any credential failure means "cannot run"
        raise SmokeError(f"could not get credentials for Azure AI Search: {exc}") from exc
    for credential in credentials:
        try:
            credential.get_token("https://search.azure.com/.default")
            return credential
        except Exception as exc:  # noqa: BLE001
            errors.append(f"{type(credential).__name__}: {str(exc).splitlines()[0] if str(exc) else exc!r}")
    raise SmokeError("could not get credentials for Azure AI Search: " + "; ".join(errors))


def _search_context_for(persona: Persona | None, search_credential) -> dict:
    """The same search field-schema config (semantic configuration, identifier/content/embedding
    fields, vector/ranker flags) ``app.py``'s ``persona_search_contexts`` builds for ``tools.
    search()``, resolved for *persona* (its own ``search.indexName``) or, with no persona (the
    legacy no-``--persona`` invocation), the deployment-wide ``AZURE_SEARCH_INDEX`` default --
    same fallback shape as ``build_middle_tier``/``_transcription_phrase_for``."""
    from azure.search.documents.aio import SearchClient

    import default_persona
    import menu_utils

    endpoint = os.environ.get("AZURE_SEARCH_ENDPOINT")
    if not endpoint:
        raise SmokeError("AZURE_SEARCH_ENDPOINT is not set -- cannot run the search tool-call check")
    index = persona.manifest.search.indexName if persona is not None else os.environ.get("AZURE_SEARCH_INDEX")
    if not index:
        raise SmokeError("no Azure AI Search index configured (persona.json's search.indexName, "
                         "or AZURE_SEARCH_INDEX for the legacy no-persona invocation)")
    menu = menu_utils.get_catalog_for_persona(persona) if persona is not None else default_persona.get_default_menu_catalog()
    prompt_loader = (PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir)
                     if persona is not None else PromptLoader())
    return {
        "search_client": SearchClient(endpoint, index, search_credential, user_agent="RTMiddleTier"),
        "semantic_configuration": os.environ.get("AZURE_SEARCH_SEMANTIC_CONFIGURATION") or "menuSemanticConfig",
        "identifier_field": os.environ.get("AZURE_SEARCH_IDENTIFIER_FIELD") or "id",
        "content_field": os.environ.get("AZURE_SEARCH_CONTENT_FIELD") or "description",
        "embedding_field": os.environ.get("AZURE_SEARCH_EMBEDDING_FIELD") or "embedding",
        "use_vector_query": _get_bool_env("AZURE_SEARCH_USE_VECTOR_QUERY", True),
        "use_semantic_ranker": (os.environ.get("AZURE_SEARCH_SEMANTIC_RANKER") or "standard").lower() != "disabled",
        "menu": menu,
        "prompt_loader": prompt_loader,
    }


def _get_bool_env(variable_name: str, default: bool = False) -> bool:
    """Same parsing as ``app.py``'s own ``_get_bool_env`` -- duplicated here (not imported) to
    keep this standalone script's only backend-module dependency the shared domain modules
    (``tools``, ``menu_utils``, ``default_persona``, ...), never ``app.py`` itself, which builds
    a whole ``aiohttp.web.Application`` on import."""
    value = os.environ.get(variable_name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _count_search_hits(result_text: str) -> int:
    """Hits in a ``tools.search()`` ``ToolResult``'s text: one ``"[identifier]: ..."`` summary
    per result, joined by ``"\\n-----\\n"`` (see ``tools.search``'s own ``joined_results``) -- 0
    for the "no results" error message, which never starts with ``"["``."""
    if not result_text.strip():
        return 0
    return sum(1 for block in result_text.split("\n-----\n") if block.strip().startswith("["))


async def check_search_tool_call(rtmt: RTMiddleTier, url: str, headers: dict, timeout: float,
                                 persona: Persona | None, search_credential) -> tuple[list[str], list[str]]:
    """#302 step 1: send a scripted guest turn over the realtime session and assert the model
    emits a well-formed ``search`` ``function_call`` (name and JSON ``{"query": ...}`` args),
    then run THAT SAME query for real against the persona's own index through the backend's own
    ``tools.search()`` code path (no app server needed) and assert at least one hit."""
    import tools

    tool_schemas = [tool.schema for tool in rtmt.tools.values()]
    failures: list[str] = []
    report: list[str] = []
    call: dict | None = None
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        session_payload = rtmt.build_bootstrap_session_update(system_message=rtmt.system_message, tool_schemas=tool_schemas)
        _echoed, error = await send_session_update(ws, session_payload, timeout)
        if error is not None:
            return [f"search tool call: session.update rejected: {error.get('error')}"], []
        await ws.send_json({
            "type": "conversation.item.create",
            "item": {"type": "message", "role": "user",
                     "content": [{"type": "input_text", "text": SEARCH_TOOL_GUEST_TEXT}]},
        })
        await ws.send_json({"type": "response.create"})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            try:
                event = await _next_event(ws, remaining)
            except TimeoutError:
                break
            if event is None:
                continue
            kind = event.get("type")
            if kind == "response.output_item.done" and (event.get("item") or {}).get("type") == "function_call":
                call = event["item"]
                break
            if kind == "error":
                return [f"search tool call: error {event.get('error')}"], []
            if kind == "response.done":
                break
    if call is None:
        return [f"search tool call: the model did not emit a function_call for the guest turn "
                f"{SEARCH_TOOL_GUEST_TEXT!r} within {timeout:.0f}s"], []
    if call.get("name") != "search":
        return [f"search tool call: the model called {call.get('name')!r} instead of 'search' "
                f"for the guest turn {SEARCH_TOOL_GUEST_TEXT!r}"], []
    try:
        args = json.loads(call.get("arguments") or "")
    except json.JSONDecodeError as exc:
        return [f"search tool call: arguments were not valid JSON: {exc} (raw: {call.get('arguments')!r})"], []
    query = args.get("query") if isinstance(args, dict) else None
    if not query:
        return [f"search tool call: arguments had no non-empty 'query' field (raw: {args!r})"], []
    report.append(f"PASS  search tool call: the model called search({args!r}) for {SEARCH_TOOL_GUEST_TEXT!r}")

    # Run that SAME query for real, through the backend's own search() code path -- no app
    # server needed, just the same SearchClient/field-config app.py itself builds.
    ctx = _search_context_for(persona, search_credential)
    result = await tools.search(
        ctx["search_client"], ctx["semantic_configuration"], ctx["identifier_field"],
        ctx["content_field"], ctx["embedding_field"], ctx["use_vector_query"], {"query": query},
        ctx["use_semantic_ranker"], menu=ctx["menu"], prompt_loader=ctx["prompt_loader"],
        persona_id=persona.id if persona is not None else None,
    )
    hits = _count_search_hits(result.to_text())
    if hits < 1:
        failures.append(f"search tool call: query {query!r} against "
                        f"{persona.id if persona is not None else '(default)'}'s own index "
                        f"returned no hits")
    else:
        report.append(f"PASS  search tool call: query {query!r} returned {hits} hit(s) from "
                      f"{persona.id if persona is not None else '(default)'}'s own index")
    return failures, report


def synthesis_instructions(text: str) -> str:
    return f"Say exactly this sentence, word for word, and nothing else: \"{text}\""


async def _synthesize(url: str, headers: dict, text: str, timeout: float, voice: str = "alloy") -> bytes:
    """24 kHz mono PCM16 of the realtime model reading `text` aloud."""
    pcm = bytearray()
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        await ws.send_json({"type": "session.update", "session": {
            "type": "realtime",
            "instructions": "You are a text-to-speech engine. Say only what you are told to say.",
            "audio": {"input": {"turn_detection": None}, "output": {"voice": voice}}}})
        # The phrase goes in the response instructions, not a user turn: given a user
        # turn, gpt-realtime-2.1 answers the order instead of reading it. Live probe
        # (the default pack, 2.1 and 2.1-dz, 3 runs each): user turn 1/6 verbatim, this 6/6.
        await ws.send_json({"type": "response.create", "response": {"instructions": synthesis_instructions(text)}})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            event = await _next_event(ws, remaining)
            if event is None:
                continue
            if event["type"] == "response.output_audio.delta":
                pcm += base64.b64decode(event["delta"])
            elif event["type"] == "response.done":
                break
            elif event["type"] == "error":
                raise SmokeError(f"could not synthesize test audio: {event.get('error')}")
    return bytes(pcm)


def normalise_transcript(text: str) -> str:
    """Lower case, no punctuation or symbols, single spaces (NFKC first, so
    full-width Japanese punctuation goes too)."""
    text = unicodedata.normalize("NFKC", text or "").lower()
    text = "".join(" " if unicodedata.category(ch)[0] in "PS" else ch for ch in text)
    return re.sub(r"\s+", " ", text).strip()


def transcript_similarity(phrase: str, transcript: str) -> float:
    return difflib.SequenceMatcher(None, normalise_transcript(phrase), normalise_transcript(transcript)).ratio()


def transcript_matches(phrase: str, transcript: str, threshold: float = TRANSCRIPT_MATCH_THRESHOLD) -> bool:
    """True if `transcript` is `phrase`, word for word, give or take case,
    punctuation, spacing and a transcriber's spelling."""
    return bool(normalise_transcript(transcript)) and transcript_similarity(phrase, transcript) >= threshold


async def check_transcription(rtmt: RTMiddleTier, url: str, headers: dict, timeout: float,
                              phrase: str = _DEFAULT_TRANSCRIPTION_PHRASE) -> tuple[list[str], list[str]]:
    pcm = await _synthesize(url, headers, phrase, timeout)
    if not pcm:
        raise SmokeError("could not synthesize test audio (no audio returned)")
    session = json.loads(rtmt.build_bootstrap_session_update(
        system_message=rtmt.system_message, tool_schemas=[tool.schema for tool in rtmt.tools.values()]))["session"]
    # Commit explicitly instead of waiting on server VAD; same transcription field.
    session["audio"]["input"]["turn_detection"] = None
    model = session["audio"]["input"].get("transcription", {}).get("model")
    async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
        echoed, error = await send_session_update(
            ws, json.dumps({"type": "session.update", "session": session}), timeout)
        if error is not None:
            return [f"transcription: session.update rejected: {error.get('error')}"], []
        for i in range(0, len(pcm), 4800):
            await ws.send_json({"type": "input_audio_buffer.append", "audio": base64.b64encode(pcm[i:i + 4800]).decode()})
        await ws.send_json({"type": "input_audio_buffer.commit"})
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            try:
                event = await _next_event(ws, remaining)
            except TimeoutError:
                break
            if event is None:
                continue
            kind = event.get("type")
            if kind == "conversation.item.input_audio_transcription.completed":
                transcript = event.get("transcript") or ""
                if not transcript.strip():
                    return [f"transcription ({model}): completed with an empty transcript"], []
                similarity = transcript_similarity(phrase, transcript)
                if not transcript_matches(phrase, transcript):
                    # Either the TTS step answered or paraphrased the phrase instead of
                    # reading it, or the transcriber got it wrong. Both mean this check
                    # did not show that guest speech is transcribed faithfully.
                    return [f"transcription ({model}): transcript {transcript!r} does not match the test phrase "
                            f"{phrase!r} (similarity {similarity:.2f} < "
                            f"{TRANSCRIPT_MATCH_THRESHOLD:.2f})"], []
                return [], [f"PASS  transcription ({model}): {transcript!r} (matches the test phrase, "
                            f"similarity {similarity:.2f})"]
            if kind == "conversation.item.input_audio_transcription.failed":
                err = event.get("error") or {}
                return [f"transcription ({model}): FAILED code={err.get('code')} message={err.get('message')} "
                        "-- guest speech will not be transcribed (does this model need its own deployment?)"], []
            if kind == "error":
                return [f"transcription ({model}): error {event.get('error')}"], []
    return [f"transcription ({model}): no transcription event within {timeout:.0f}s"], []


async def run(endpoint: str, deployment: str, *, voice: str | None, timeout: float,
              skip_transcription: bool, headers: dict[str, str] | None = None,
              tenant_id: str | None = None, subscription_id: str | None = None,
              persona: Persona | None = None, skip_search: bool = False) -> int:
    rtmt = build_middle_tier(endpoint, deployment, voice, persona=persona)
    url = realtime_url(endpoint, deployment)
    print(f"Realtime smoke check: persona={persona.id if persona else '(default)'} "
          f"deployment={deployment} voice={rtmt.voice_choice} "
          f"transcription={rtmt.transcription_model} "
          f"reasoning={rtmt.reasoning_effort if rtmt.reasoning_enabled() else 'off'}")
    headers = get_auth_headers(tenant_id, subscription_id) if headers is None else headers
    try:
        failures, report = await check_session_updates(rtmt, url, headers, timeout)
        if not skip_transcription:
            phrase = _transcription_phrase_for(persona)
            t_failures, t_report = await check_transcription(rtmt, url, headers, timeout, phrase)
            failures += t_failures
            report += t_report + [f"FAIL  {f}" for f in t_failures]
        if not skip_search:
            # #302 step 1: one scripted guest turn -> assert a well-formed `search`
            # function_call -> run that query for real against the persona's own index.
            search_credential = get_search_credential(tenant_id, subscription_id)
            s_failures, s_report = await check_search_tool_call(rtmt, url, headers, timeout, persona, search_credential)
            failures += s_failures
            report += s_report + [f"FAIL  {f}" for f in s_failures]
    except aiohttp.WSServerHandshakeError as exc:
        raise SmokeError(f"websocket handshake failed: HTTP {exc.status} {exc.message}") from exc
    except (aiohttp.ClientError, OSError) as exc:
        raise SmokeError(f"could not reach {url}: {exc}") from exc
    for line in report:
        print(f"  {line}")
    if failures:
        print(f"SMOKE CHECK FAILED ({len(failures)} problem(s)) -- the carhop would run without its tools "
              "or without transcripts on this deployment.")
        return 1
    print("SMOKE CHECK PASSED")
    return 0


# ── Cascade pipeline smoke (#302 step 2) ────────────────────────────────────────────────
#
# `CascadeProcessor` (app/backend/cascade_processor.py) is STT (Azure OpenAI
# `gpt-4o-transcribe`) -> a Foundry chat model (azure-ai-inference `ChatCompletionsClient`,
# tool calling) -> TTS (Azure OpenAI `gpt-4o-mini-tts`), and -- unlike the realtime pipeline's
# `llm_credential`/`search_credential` (which may be an API key) -- ALWAYS authenticates with a
# real Entra ID token (design doc section 7.4 / issue #82: "DefaultAzureCredential only, never
# an API key"), so this smoke check's own cascade credential chain below has no api-key branch.

def _async_credentials(tenant_id: str | None, subscription_id: str | None) -> list:
    """The async ``azure.identity.aio`` equivalent of ``_credentials`` -- same tenant-pinned
    fallback order, needed because ``ChatCompletionsClient`` (and this script's own bearer-token
    audio calls) are async, unlike the realtime pipeline's sync ``get_auth_headers`` path."""
    from azure.identity.aio import (
        AzureCliCredential,
        AzureDeveloperCliCredential,
        DefaultAzureCredential,
    )
    creds = []
    if subscription_id:
        creds.append(AzureCliCredential(subscription=subscription_id, process_timeout=60))
    if tenant_id:
        creds.append(AzureDeveloperCliCredential(tenant_id=tenant_id, process_timeout=60))
        creds.append(AzureCliCredential(tenant_id=tenant_id, process_timeout=60))
    if not creds:
        creds.append(DefaultAzureCredential(exclude_interactive_browser_credential=True))
    return creds


async def get_cascade_credential(tenant_id: str | None = None, subscription_id: str | None = None):
    """An async ``TokenCredential`` for the cascade pipeline's own Foundry chat/audio calls --
    never an API key (see module note above). Tried in turn exactly like ``get_auth_headers``:
    a hard auth error moves on to the next candidate instead of failing outright."""
    errors = []
    try:
        credentials = _async_credentials(tenant_id, subscription_id)
    except Exception as exc:  # noqa: BLE001
        raise SmokeError(f"could not get an Entra ID token for the cascade pipeline: {exc}") from exc
    for credential in credentials:
        try:
            await credential.get_token(_TOKEN_SCOPE)
            return credential
        except Exception as exc:  # noqa: BLE001
            errors.append(f"{type(credential).__name__}: {str(exc).splitlines()[0] if str(exc) else exc!r}")
    raise SmokeError("could not get an Entra ID token for the cascade pipeline: " + "; ".join(errors))


async def _cascade_bearer_token(credential) -> str:
    token = await credential.get_token(_TOKEN_SCOPE)
    return token.token


_CASCADE_AUDIO_SAMPLE_RATE = 24000
_CASCADE_AUDIO_SAMPLE_WIDTH = 2  # PCM16, matches cascade_processor.py's own audio constants.


def _pcm16_to_wav_bytes(pcm: bytes, sample_rate: int = _CASCADE_AUDIO_SAMPLE_RATE) -> bytes:
    """Wraps raw PCM16 mono audio in a minimal WAV container (stdlib ``wave``) -- the exact
    same transform ``cascade_processor._pcm16_to_wav_bytes`` applies before POSTing to the
    transcription endpoint, duplicated here (not imported) for the same reason as
    ``_cascade_tool_definitions``: this standalone script never imports ``cascade_processor``."""
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(_CASCADE_AUDIO_SAMPLE_WIDTH)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(pcm)
    return buf.getvalue()


async def _cascade_transcribe(audio_endpoint: str, deployment: str, pcm: bytes, credential, timeout: float) -> str:
    """POSTs *pcm* (24kHz mono PCM16) to the cascade pipeline's own STT deployment, the exact
    request shape ``CascadeProcessor._transcribe`` sends (a WAV-wrapped multipart upload to
    ``/openai/v1/audio/transcriptions``), live."""
    wav_bytes = _pcm16_to_wav_bytes(pcm)
    token = await _cascade_bearer_token(credential)
    url = f"{audio_endpoint.rstrip('/')}/openai/v1/audio/transcriptions"
    form = aiohttp.FormData()
    form.add_field("file", wav_bytes, filename="smoke.wav", content_type="audio/wav")
    form.add_field("model", deployment)
    async with aiohttp.ClientSession() as http:
        async with http.post(url, data=form, headers={"Authorization": "Bearer " + token},
                              timeout=aiohttp.ClientTimeout(total=timeout)) as resp:
            resp.raise_for_status()
            payload = await resp.json()
    return payload.get("text", "")


async def _cascade_speak(audio_endpoint: str, deployment: str, text: str, credential, timeout: float) -> bytes:
    """POSTs to the cascade pipeline's own TTS deployment, the exact request shape
    ``CascadeProcessor._speak`` sends (``/openai/v1/audio/speech``, ``response_format: "pcm"``),
    live."""
    token = await _cascade_bearer_token(credential)
    url = f"{audio_endpoint.rstrip('/')}/openai/v1/audio/speech"
    body = {"model": deployment, "input": text, "voice": "alloy", "response_format": "pcm"}
    async with aiohttp.ClientSession() as http:
        async with http.post(url, json=body, headers={"Authorization": "Bearer " + token},
                              timeout=aiohttp.ClientTimeout(total=timeout)) as resp:
            resp.raise_for_status()
            return await resp.read()


def _cascade_tool_definitions(tool_schemas: list[dict]):
    """``tools.py``'s flat Realtime-API-style schemas, converted to the nested
    Chat-Completions-style definitions the azure-ai-inference SDK expects -- the exact same
    conversion ``cascade_processor._tool_definitions`` does, duplicated here (not imported) so
    this standalone script never imports ``cascade_processor`` itself (which pulls in
    ``aiohttp.web``/``session_manager``/the whole processor machinery for a single pure
    function)."""
    from azure.ai.inference.models import ChatCompletionsToolDefinition, FunctionDefinition
    return [
        ChatCompletionsToolDefinition(function=FunctionDefinition(
            name=schema["name"], description=schema.get("description", ""), parameters=schema.get("parameters", {})))
        for schema in tool_schemas
    ]


async def _cascade_chat_completion(foundry_endpoint: str, deployment: str, persona: Persona,
                                   tool_schemas: list[dict], guest_text: str, credential,
                                   timeout: float) -> tuple[bool, str]:
    """One chat completion against the cascade pipeline's own Foundry chat deployment
    (azure-ai-inference ``ChatCompletionsClient``, the same client/message/tool shape
    ``CascadeProcessor._run_chat_tool_loop`` uses), live. Passes iff the model returns a tool
    call OR non-empty text -- either is a well-formed completion; this check isn't asserting
    which one a guest's own words should produce."""
    from azure.ai.inference.aio import ChatCompletionsClient
    from azure.ai.inference.models import SystemMessage, UserMessage

    prompt_loader = PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir)
    tool_defs = _cascade_tool_definitions(tool_schemas)
    client = ChatCompletionsClient(endpoint=foundry_endpoint, credential=credential,
                                   credential_scopes=[_TOKEN_SCOPE])
    try:
        completion = await asyncio.wait_for(
            client.complete(
                messages=[SystemMessage(content=prompt_loader.get_system_prompt()), UserMessage(content=guest_text)],
                model=deployment,
                tools=tool_defs or None,
            ),
            timeout=timeout,
        )
    finally:
        await client.close()
    message = completion.choices[0].message
    tool_calls = message.tool_calls or []
    if tool_calls:
        names = ", ".join(tc.function.name for tc in tool_calls)
        return True, f"tool_call(s): {names}"
    content = (message.content or "").strip()
    if content:
        return True, f"text: {content[:80]!r}"
    return False, "returned neither a tool call nor any text"


async def run_cascade(endpoint: str, deployment: str, *, audio_endpoint: str, foundry_endpoint: str,
                      persona: Persona, model_catalog: ModelCatalog, timeout: float,
                      headers: dict[str, str], tenant_id: str | None, subscription_id: str | None) -> int:
    """#302 step 2: per persona, ``gpt-4o-transcribe`` on the test phrase, one chat completion
    with the tool schemas on the cascade chat model from the catalog, and ``gpt-4o-mini-tts``
    returning audio bytes. Deployments resolve via ``AZURE_AI_MODEL_DEPLOYMENTS`` exactly like
    ``app.create_app()`` resolves them for a real cascade session."""
    cascade_cfg = persona.manifest.models.cascade
    if cascade_cfg is None:
        raise SmokeError(f"persona {persona.id!r} has no cascade pipeline configured (models.cascade in persona.json)")
    audio_cfg = model_catalog.cascade_audio
    if audio_cfg is None:
        raise SmokeError("config.yaml's models.cascade (transcription/tts catalog ids) is not configured")
    missing = [(label, model_id) for label, model_id in (
        ("cascade chat model", cascade_cfg.default),
        ("cascade transcription model", audio_cfg.transcription),
        ("cascade tts model", audio_cfg.tts),
    ) if model_catalog.deployment_for(model_id) is None]
    if missing:
        raise SmokeError("AZURE_AI_MODEL_DEPLOYMENTS is missing an entry for: " +
                         ", ".join(f"{label} {model_id!r}" for label, model_id in missing))
    if not foundry_endpoint:
        raise SmokeError("AZURE_AI_FOUNDRY_ENDPOINT is not set -- cannot run the cascade chat completion check")
    chat_deployment = model_catalog.deployment_for(cascade_cfg.default)
    transcription_deployment = model_catalog.deployment_for(audio_cfg.transcription)
    tts_deployment = model_catalog.deployment_for(audio_cfg.tts)

    print(f"Cascade smoke check: persona={persona.id} "
          f"chat={cascade_cfg.default}->{chat_deployment} "
          f"transcription={audio_cfg.transcription}->{transcription_deployment} "
          f"tts={audio_cfg.tts}->{tts_deployment}")

    failures: list[str] = []
    report: list[str] = []
    try:
        credential = await get_cascade_credential(tenant_id, subscription_id)

        phrase = _transcription_phrase_for(persona)
        realtime_ws_url = realtime_url(endpoint, deployment)
        pcm = await _synthesize(realtime_ws_url, headers, phrase, timeout)
        if not pcm:
            raise SmokeError("could not synthesize test audio for the cascade transcription check")
        transcript = await _cascade_transcribe(audio_endpoint, transcription_deployment, pcm, credential, timeout)
        if not transcript.strip():
            failures.append(f"cascade transcription ({transcription_deployment}): completed with an empty transcript")
        elif not transcript_matches(phrase, transcript):
            similarity = transcript_similarity(phrase, transcript)
            failures.append(f"cascade transcription ({transcription_deployment}): transcript {transcript!r} does "
                            f"not match the test phrase {phrase!r} (similarity {similarity:.2f} < "
                            f"{TRANSCRIPT_MATCH_THRESHOLD:.2f})")
        else:
            report.append(f"PASS  cascade transcription ({transcription_deployment}): {transcript!r} "
                          "(matches the test phrase)")

        tool_schemas = _tool_schemas(PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir))
        completion_ok, detail = await _cascade_chat_completion(
            foundry_endpoint, chat_deployment, persona, tool_schemas, phrase, credential, timeout)
        if completion_ok:
            report.append(f"PASS  cascade chat completion ({chat_deployment}): {detail}")
        else:
            failures.append(f"cascade chat completion ({chat_deployment}): {detail}")

        audio_bytes = await _cascade_speak(audio_endpoint, tts_deployment, "Here's your order, thanks for stopping by!",
                                           credential, timeout)
        if audio_bytes:
            report.append(f"PASS  cascade tts ({tts_deployment}): {len(audio_bytes)} bytes of audio returned")
        else:
            failures.append(f"cascade tts ({tts_deployment}): no audio bytes returned")
    except aiohttp.ClientResponseError as exc:
        raise SmokeError(f"cascade pipeline request failed: HTTP {exc.status} {exc.message}") from exc
    except (aiohttp.ClientError, OSError) as exc:
        raise SmokeError(f"could not reach the cascade pipeline: {exc}") from exc

    for line in report:
        print(f"  {line}")
    for f in failures:
        print(f"  FAIL  {f}")
    if failures:
        print(f"CASCADE SMOKE CHECK FAILED ({len(failures)} problem(s))")
        return 1
    print("CASCADE SMOKE CHECK PASSED")
    return 0


def resolve_identity(tenant: str | None, subscription: str | None,
                     azd_values: dict[str, str] | None = None) -> tuple[str | None, str | None]:
    """(tenant id, subscription id): CLI, else env, else the azd env, each on its own.
    The azd env is read only when something is still missing."""
    identity = {"AZURE_TENANT_ID": tenant or os.environ.get("AZURE_TENANT_ID") or None,
                "AZURE_SUBSCRIPTION_ID": subscription or os.environ.get("AZURE_SUBSCRIPTION_ID") or None}
    if not all(identity.values()):
        azd_values = azd_values or _azd_env_values()
        for name, value in identity.items():
            identity[name] = value or azd_values.get(name) or None
    return identity["AZURE_TENANT_ID"], identity["AZURE_SUBSCRIPTION_ID"]


def resolve_persona(persona_id: str | None) -> Persona | None:
    """*persona_id* (--persona, #83/P2-14), else `None` -- the legacy, no-`--persona`
    invocation, which leaves `build_middle_tier` on `PromptLoader`'s own single-brand
    default (unchanged). Loads the SAME real `PersonaCatalog` `app.create_app()` does, so
    an unknown or broken pack fails the same way it would in production -- never a silent
    fallback to a hardcoded brand."""
    if persona_id is None:
        return None
    try:
        catalog = PersonaCatalog.load()
    except PersonaValidationError as exc:
        raise SmokeError(f"could not load persona catalog: {exc}") from exc
    try:
        return catalog.get(persona_id)
    except KeyError as exc:
        raise SmokeError(str(exc)) from exc


def resolve_model_deployment(model_id: str | None, persona: Persona | None) -> str | None:
    """--model (#83/P2-14; a `model_catalog.py` catalog id) resolved to its configured
    deployment name via the SAME `AZURE_AI_MODEL_DEPLOYMENTS` map (config.yaml
    `models.catalog` + that env var) `app.create_app()` uses -- takes precedence over
    --deployment/env when given. Returns `None` when --model is omitted, so
    --deployment/env keep resolving the deployment exactly as before."""
    if model_id is None:
        return None
    catalog = ModelCatalog.load()
    if model_id not in catalog:
        raise SmokeError(f"--model {model_id!r} is not in the model catalog (config.yaml models.catalog)")
    if persona is not None and model_id not in persona.manifest.models.realtime.allowed:
        raise SmokeError(
            f"--model {model_id!r} is not in persona {persona.id!r}'s allowed realtime models "
            f"({', '.join(persona.manifest.models.realtime.allowed)})"
        )
    deployment = catalog.deployment_for(model_id)
    if not deployment:
        raise SmokeError(f"--model {model_id!r} has no AZURE_AI_MODEL_DEPLOYMENTS entry -- nothing to smoke-test")
    return deployment


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--endpoint", help="Azure OpenAI endpoint (default: AZURE_OPENAI_EASTUS2_ENDPOINT)")
    parser.add_argument("--deployment", help="Realtime deployment (default: AZURE_OPENAI_REALTIME_DEPLOYMENT)")
    parser.add_argument("--persona", help="Persona pack id to smoke-test prompts/tools for "
                                          "(default: unchanged single-brand PromptLoader default, "
                                          "or -- with --pipeline cascade -- the persona catalog's own default)")
    parser.add_argument("--model", help="Model catalog id (config.yaml models.catalog) to resolve a "
                                         "deployment for via AZURE_AI_MODEL_DEPLOYMENTS; overrides "
                                         "--deployment/env when given")
    parser.add_argument("--pipeline", choices=["realtime", "cascade"], default="realtime",
                        help="Which pipeline to smoke-test (default: realtime)")
    parser.add_argument("--foundry-endpoint", help="Foundry chat endpoint for --pipeline cascade "
                                                    "(default: AZURE_AI_FOUNDRY_ENDPOINT)")
    parser.add_argument("--voice", help="Voice to send (default: AZURE_OPENAI_REALTIME_VOICE_CHOICE or config.yaml)")
    parser.add_argument("--tenant", help="Entra tenant of the Azure OpenAI resource (default: AZURE_TENANT_ID)")
    parser.add_argument("--subscription", help="Subscription whose `az` sign-in to use (default: AZURE_SUBSCRIPTION_ID)")
    parser.add_argument("--timeout", type=float, default=20.0, help="Seconds to wait per server reply (default 20)")
    parser.add_argument("--skip-transcription", action="store_true",
                        help="Skip the live speech-transcription check (--pipeline realtime only)")
    parser.add_argument("--skip-search", action="store_true",
                        help="Skip the live search tool-call check (--pipeline realtime only)")
    args = parser.parse_args(argv)

    try:
        persona = resolve_persona(args.persona)
        model_deployment = resolve_model_deployment(args.model, persona)
        if args.pipeline == "cascade":
            # Cascade (#302 step 2) always needs a real persona pack -- its models.cascade
            # config, prompts and tool schemas -- so an omitted --persona resolves to the
            # SAME persona catalog's own default, never a single hardcoded brand.
            if persona is None:
                catalog = PersonaCatalog.load()
                persona = catalog.get(catalog.default_persona_id)
            model_catalog = ModelCatalog.load()
    except (SmokeError, PersonaValidationError, ModelValidationError) as exc:
        print(f"{'Cascade' if args.pipeline == 'cascade' else 'Realtime'} smoke check could not run: {exc}",
              file=sys.stderr)
        return 2

    azd_values = {} if (args.endpoint and (args.deployment or model_deployment)) else _azd_env_values()
    endpoint = resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
    deployment = model_deployment or resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values)
    if not endpoint or not deployment:
        print("Realtime smoke check could not run: set --endpoint/--deployment (or --model) or "
              "AZURE_OPENAI_EASTUS2_ENDPOINT/AZURE_OPENAI_REALTIME_DEPLOYMENT (or run inside an azd env).",
              file=sys.stderr)
        return 2
    # Every env var the app reads for the realtime session.
    for name in ("AZURE_OPENAI_REALTIME_REASONING_EFFORT", "AZURE_OPENAI_REALTIME_REASONING_MODEL",
                 "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL", "AZURE_OPENAI_REALTIME_VOICE_CHOICE"):
        if name not in os.environ and azd_values.get(name):
            os.environ[name] = azd_values[name]
    tenant_id = subscription_id = None
    if not os.environ.get("AZURE_OPENAI_EASTUS2_API_KEY"):
        tenant_id, subscription_id = resolve_identity(args.tenant, args.subscription, azd_values)

    if args.pipeline == "cascade":
        foundry_endpoint = resolve_setting("AZURE_AI_FOUNDRY_ENDPOINT", args.foundry_endpoint, azd_values)
        try:
            headers = get_auth_headers(tenant_id, subscription_id)
            return asyncio.run(run_cascade(endpoint, deployment, audio_endpoint=endpoint,
                                           foundry_endpoint=foundry_endpoint, persona=persona,
                                           model_catalog=model_catalog, timeout=args.timeout,
                                           headers=headers, tenant_id=tenant_id, subscription_id=subscription_id))
        except SmokeError as exc:
            print(f"Cascade smoke check could not run: {exc}", file=sys.stderr)
            return 2

    try:
        return asyncio.run(run(endpoint, deployment, voice=args.voice, timeout=args.timeout,
                               skip_transcription=args.skip_transcription,
                               tenant_id=tenant_id, subscription_id=subscription_id,
                               persona=persona, skip_search=args.skip_search))
    except SmokeError as exc:
        print(f"Realtime smoke check could not run: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
