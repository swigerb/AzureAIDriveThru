"""The companion local-runtime's aiohttp server -- implements exactly the three-endpoint contract
`app/backend/local_runtime.py`'s `HttpLocalRuntimeClient` calls (see that module's own docstring
and this package's `__init__.py`):

    POST /v1/transcribe  raw 16kHz mono PCM16              -> {"text": "..."}
    POST /v1/chat        {"messages": [...], "tools": [...]} -> {"content": ..., "tool_calls": [...]}
    POST /v1/speak       {"text": "...", "voice": "..."}    -> raw 24kHz mono PCM16

Plus `GET /health`, a small operational convenience (not part of the client's own contract, so the
client never calls it) reporting whether each engine has loaded successfully -- useful for
`docker-compose.local.yml`'s healthcheck and for a developer sanity-checking a fresh model download
without triggering a real transcribe/chat/speak call.

`create_app()` takes engines directly (this module's own seam, mirroring `local_processor.py`'s
`runtime_factory` constructor injection) so `local_runtime/tests/` can pass tiny stub
`SttEngine`/`ChatEngine`/`TtsEngine` implementations -- no real model weights, no heavy ML
dependencies -- exercising the exact same route handlers real requests hit. `local_runtime/__main__.py`
is the only place real engines (`WhisperSttEngine`/`Phi4ChatEngine`/`PiperTtsEngine`) get built.
"""

from __future__ import annotations

import logging
from typing import Any

from aiohttp import web

from local_runtime.engines import ChatEngine, EngineNotReadyError, SttEngine, TtsEngine
from local_runtime.voices import resolve_voice_id

logger = logging.getLogger(__name__)

__all__ = ["create_app"]

# The wire contract's own fixed rates (`app/backend/local_runtime.py`'s module docstring): the
# client always resamples mic audio to 16kHz before uploading, and always expects 24kHz back from
# /v1/speak, regardless of the pack/session's own capture/playback rate.
_STT_SAMPLE_RATE = 16000
_TTS_SAMPLE_RATE = 24000


def _error_response(exc: Exception, *, status: int) -> web.Response:
    return web.json_response({"error": str(exc)}, status=status)


def create_app(
    *,
    stt: SttEngine,
    chat: ChatEngine,
    tts: TtsEngine,
    default_voice: str = "en_US-amy-medium",
    max_upload_bytes: int = 10 * 1024 * 1024,
) -> web.Application:
    app = web.Application(client_max_size=max_upload_bytes)
    app["stt"] = stt
    app["chat"] = chat
    app["tts"] = tts
    app["default_voice"] = default_voice
    app["max_upload_bytes"] = max_upload_bytes
    app.router.add_post("/v1/transcribe", _handle_transcribe)
    app.router.add_post("/v1/chat", _handle_chat)
    app.router.add_post("/v1/speak", _handle_speak)
    app.router.add_get("/health", _handle_health)
    return app


async def _handle_transcribe(request: web.Request) -> web.Response:
    max_upload_bytes = request.app["max_upload_bytes"]
    body = await request.read()
    if len(body) > max_upload_bytes:
        return _error_response(
            ValueError(f"upload of {len(body)} bytes exceeds the {max_upload_bytes}-byte limit"),
            status=413,
        )
    stt: SttEngine = request.app["stt"]
    try:
        text = await stt.transcribe(body, sample_rate=_STT_SAMPLE_RATE)
    except EngineNotReadyError as exc:
        logger.warning("STT engine not ready: %s", exc)
        return _error_response(exc, status=503)
    except Exception as exc:  # noqa: BLE001 - never leak a bare 500 traceback to the wire
        logger.exception("Transcription failed")
        return _error_response(exc, status=500)
    return web.json_response({"text": text})


def _validate_chat_body(payload: Any) -> tuple[list[dict[str, Any]], list[dict[str, Any]]] | web.Response:
    if not isinstance(payload, dict):
        return _error_response(ValueError("request body must be a JSON object"), status=400)
    messages = payload.get("messages")
    tools = payload.get("tools") or []
    if not isinstance(messages, list):
        return _error_response(ValueError("'messages' must be a list"), status=400)
    if not isinstance(tools, list):
        return _error_response(ValueError("'tools' must be a list"), status=400)
    return messages, tools


async def _handle_chat(request: web.Request) -> web.Response:
    try:
        payload = await request.json()
    except ValueError as exc:
        return _error_response(exc, status=400)
    validated = _validate_chat_body(payload)
    if isinstance(validated, web.Response):
        return validated
    messages, tools = validated

    chat: ChatEngine = request.app["chat"]
    try:
        result = await chat.chat(messages, tools)
    except EngineNotReadyError as exc:
        logger.warning("Chat engine not ready: %s", exc)
        return _error_response(exc, status=503)
    except Exception as exc:  # noqa: BLE001
        logger.exception("Chat inference failed")
        return _error_response(exc, status=500)

    return web.json_response({
        "content": result.content,
        "tool_calls": [
            {"id": call.id, "name": call.name, "arguments": call.arguments}
            for call in result.tool_calls
        ],
    })


async def _handle_speak(request: web.Request) -> web.Response:
    try:
        payload = await request.json()
    except ValueError as exc:
        return _error_response(exc, status=400)
    if not isinstance(payload, dict):
        return _error_response(ValueError("request body must be a JSON object"), status=400)
    text = payload.get("text")
    if not isinstance(text, str):
        return _error_response(ValueError("'text' must be a string"), status=400)
    requested_voice = payload.get("voice")
    if requested_voice is not None and not isinstance(requested_voice, str):
        return _error_response(ValueError("'voice' must be a string or omitted"), status=400)

    tts: TtsEngine = request.app["tts"]
    try:
        await tts.ensure_loaded()
    except EngineNotReadyError as exc:
        logger.warning("TTS engine not ready: %s", exc)
        return _error_response(exc, status=503)

    voice = resolve_voice_id(
        requested_voice, default_voice=request.app["default_voice"], available=tts.available_voices(),
    )
    try:
        pcm = await tts.synthesize(text, voice, sample_rate=_TTS_SAMPLE_RATE)
    except EngineNotReadyError as exc:
        logger.warning("TTS engine not ready: %s", exc)
        return _error_response(exc, status=503)
    except Exception as exc:  # noqa: BLE001
        logger.exception("Speech synthesis failed")
        return _error_response(exc, status=500)
    return web.Response(body=pcm, content_type="application/octet-stream")


async def _handle_health(request: web.Request) -> web.Response:
    """Operational convenience, not part of the client's own contract (see this module's own
    docstring) -- best-effort `ensure_loaded()` probe per engine, without a real
    transcribe/chat/speak call. Never raises: an engine reporting `False` here is exactly the
    "run scripts/download_local_models.py" signal a developer needs, not a 500."""
    statuses: dict[str, bool] = {}
    for name, engine in (("stt", request.app["stt"]), ("chat", request.app["chat"]), ("tts", request.app["tts"])):
        try:
            await engine.ensure_loaded()
            statuses[name] = True
        except EngineNotReadyError:
            statuses[name] = False
        except Exception:  # noqa: BLE001 - health check must never 500
            logger.exception("Unexpected error probing the %s engine for /health", name)
            statuses[name] = False
    ready = all(statuses.values())
    return web.json_response({"ready": ready, "engines": statuses}, status=200 if ready else 503)
