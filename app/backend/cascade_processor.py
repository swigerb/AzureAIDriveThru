"""Cascade PipelineProcessor (issue #82, P2-13): Azure OpenAI speech-to-text -> a Foundry chat
model (Azure AI Inference SDK, tool calling) -> Azure OpenAI text-to-speech, registered in
`processors.ProcessorRegistry` alongside `RTMiddleTier`'s own realtime pipeline (#75's seam,
PR #106) -- reached ONLY through `RTMiddleTier._websocket_handler`'s own `dispatch_processor`
call, never a second WebSocket route: there is exactly one `/realtime` route, shared by both
pipelines' processors (see app.py's wiring).

Design doc references: docs/persona-architecture.md section 7.1 (cascade pipeline shape), 7.4
(reasoning), 5.2 (per-pipeline `/api/personas` models). See
`.squad/decisions/inbox/summer-82.md` for the SDK/endpoint rationale (azure-ai-inference for
ALL cascade chat models, both OpenAI-format and non-OpenAI-format, vs. the design doc's own
"openai SDK" mention) and the local-VAD / session-sharing / resume-scope-cut decisions.

Wire-protocol contract (MUST match RTMiddleTier's realtime pipeline on every field the frontend
actually reads -- see useRealtime.tsx/types.ts):
  - `extension.session_metadata` / `extension.round_trip_token`: emitted via the SAME
    `SessionManager.emit_session_identifiers` and the SAME `order_state_singleton`-sourced
    `SessionIdentifiers`, so the shape is byte-identical.
  - `extension.middle_tier_tool_response`: the SAME `{previous_item_id, tool_name, tool_result}`
    shape, gated by the SAME `ToolResultDirection` logic, executed against the SAME shared
    `rtmt.tools` dict (byte-identical `Tool`/`ToolResult` objects -- see tools.py's
    `attach_tools_rtmt()`, Beth's #77 scope, imported here but never edited).
  - `response.created` / `response.audio.delta` / `response.audio_transcript.delta` /
    `response.done`: legacy (non-GA) event names, exactly as `audio_pipeline.py`'s translation
    table documents the frontend expects.
  - `input_audio_buffer.speech_started` / `conversation.item.input_audio_transcription.completed`:
    emitted by this processor's own local VAD (`_TurnDetector`) -- there is no upstream Realtime
    API turn-segmenting audio for us; that is the whole point of the "cascade" pipeline.

Session/tool/persona sharing (see app.py): `CascadeProcessor` does NOT own its own
`SessionManager`, tool registry, or persona catalog -- it receives the SAME objects
`RTMiddleTier` already constructed/populated, so idle-timeout/concurrency-limit background
tasks (`RTMiddleTier.start_background_tasks`) and tool calling behave identically for both
pipelines with zero duplicated lifecycle logic.
"""

from __future__ import annotations

import array
import asyncio
import base64
import io
import json
import logging
import os
import time
import wave
from dataclasses import dataclass, field
from typing import Any

import aiohttp
from aiohttp import web
from azure.ai.inference.aio import ChatCompletionsClient
from azure.ai.inference.models import (
    AssistantMessage,
    ChatCompletionsToolDefinition,
    FunctionDefinition,
    SystemMessage,
    ToolMessage,
    UserMessage,
)
from azure.core.exceptions import HttpResponseError

from config_loader import get_config
from conformance_hooks import cascade_chat_kwargs
from order_state import order_state_singleton
from processors import ResolvedModel, resolve_cascade_model
from rate_limit import (
    FIRST_RETRY_BOUNDS,
    RATE_LIMITED_EVENT,
    SECOND_RETRY_BOUNDS,
    RateLimitSettings,
    parse_retry_hint,
    retry_delay,
)
from rtmt import (
    _DEFAULT_ALLOWED_VOICES,
    Tool,
    ToolResult,
    ToolResultDirection,
    _extract_raw_mode_param,
    _sanitize_voice,
    _truncate_for_log,
)
from session_manager import SessionManager, new_middle_tier_item_id

logger = logging.getLogger(__name__)

_config = get_config()
_conn_cfg = _config.get("connection", {})
_vad_cfg = _config.get("vad", {})

_WS_HEARTBEAT_SEC = _conn_cfg.get("ws_heartbeat_seconds", 15.0)
_WS_COMPRESS = bool(_conn_cfg.get("ws_compression", False))

_AUDIO_SAMPLE_RATE = 24000
_AUDIO_SAMPLE_WIDTH = 2  # PCM16, matches both mic input and speaker output (confirmed against
                          # useAudioRecorder.tsx/useAudioPlayer.tsx -- no resampling needed).
# Rick's #82 infra notes: the Foundry chat endpoint (and the audio endpoint we reuse for
# STT/TTS) is a Cognitive Services resource -- the same bearer-token scope rtmt.py already uses
# (see rtmt.py's own `get_bearer_token_provider` call site) applies to both.
_COGNITIVE_SERVICES_SCOPE = "https://cognitiveservices.azure.com/.default"

__all__ = ["CascadeProcessor"]


def _tool_definitions(tool_schemas: list[dict]) -> list[ChatCompletionsToolDefinition]:
    """Converts tools.py's flat Realtime-API-style schemas (`{"type": "function", "name": ...,
    "parameters": ...}`) into the nested Chat-Completions-style `ChatCompletionsToolDefinition`
    the azure-ai-inference SDK expects. #170 R4 (Rick's PR #175 round-2 review): *tool_schemas*
    is THIS session's own bound persona's tool schema list (`CascadeProcessor.persona_tool_schemas`,
    resolved per `state.persona_id` by `_run_chat_tool_loop`) -- previously this read
    `tools.values()` (the module-level `Tool` registry) directly, so every cascade session's tool
    descriptions named the deployment default persona's menu/ticket regardless of which persona was
    actually bound."""
    definitions = []
    for schema in tool_schemas:
        definitions.append(
            ChatCompletionsToolDefinition(
                function=FunctionDefinition(
                    name=schema["name"],
                    description=schema.get("description", ""),
                    parameters=schema.get("parameters", {}),
                )
            )
        )
    return definitions


# Fire-and-forget turn-processing tasks (Rick's #118 review item 5, barge-in). Mirrors
# rtmt.py's own `_spawn`/`_BACKGROUND_TASKS`/`_on_background_task_done` pattern verbatim,
# duplicated locally (not imported) to keep this module's own boundary clean -- see the module
# docstring's "Session/tool/persona sharing" note on what IS shared with rtmt.py vs. not.
_BACKGROUND_TASKS: set[asyncio.Task] = set()


def _spawn(coro) -> asyncio.Task:
    task = asyncio.ensure_future(coro)
    _BACKGROUND_TASKS.add(task)
    task.add_done_callback(_on_background_task_done)
    return task


def _on_background_task_done(task: asyncio.Task) -> None:
    _BACKGROUND_TASKS.discard(task)
    if task.cancelled():
        return
    exc = task.exception()
    if exc is not None:
        logger.debug("Cascade background turn task raised (retrieved, not re-raised): %r", exc)


class CascadeRateLimitExhausted(Exception):
    """Raised internally once a chat/STT/TTS call has been retried through the full ladder
    below and still failed with a 429 -- by the time this is raised the client has already
    received the final `extension.rate_limited` notice, so callers just need to end the turn
    cleanly (same as the realtime pipeline's own guest-repeats-themselves outcome)."""


def _http_status_of(exc: Exception) -> int | None:
    """The HTTP status code of an azure-core `HttpResponseError` (`.status_code`) or an
    aiohttp `ClientResponseError` (`.status`) -- the two shapes cascade's chat/STT/TTS calls
    can raise."""
    status = getattr(exc, "status_code", None)
    if status is not None:
        return status
    return getattr(exc, "status", None)


def _retry_hint_of(exc: Exception) -> float | None:
    """Seconds the service asked us to wait, preferring a `Retry-After` response header (both
    azure-core's `HttpResponseError.response.headers` and aiohttp's own
    `ClientResponseError.headers` expose one) and falling back to
    `rate_limit.parse_retry_hint`'s free-text parse of the error message."""
    headers = getattr(exc, "headers", None)
    if headers is None:
        response = getattr(exc, "response", None)
        headers = getattr(response, "headers", None) if response is not None else None
    if headers is not None:
        try:
            retry_after = headers.get("Retry-After") or headers.get("retry-after")
        except AttributeError:
            retry_after = None
        if retry_after is not None:
            try:
                return float(retry_after)
            except (TypeError, ValueError):
                pass
    return parse_retry_hint(str(exc))


def _pcm16_to_wav_bytes(pcm: bytes, sample_rate: int = _AUDIO_SAMPLE_RATE) -> bytes:
    """Wraps raw PCM16 mono audio in a minimal WAV container (stdlib `wave`) -- the
    transcription endpoint needs a file with a real header/extension, not a bare byte stream."""
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(_AUDIO_SAMPLE_WIDTH)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(pcm)
    return buf.getvalue()


class _TurnDetector:
    """A minimal, local RMS-energy voice-activity detector (issue #82). Unlike the realtime
    pipeline, there is no upstream Realtime API doing `server_vad` turn segmentation for us --
    the whole point of "cascade" is that WE own STT/chat/TTS, so we must decide for ourselves
    when the guest has started and finished speaking. Mirrors config.yaml's existing
    `vad.threshold`/`vad.silence_duration_ms` (already used to configure the realtime session's
    own `server_vad`) so both pipelines feel the same to a guest, without pretending to
    reimplement OpenAI's actual VAD algorithm -- just enough to segment turns for STT.

    Uses the stdlib `array` module (not the deprecated, Python-3.13-removed `audioop`) since
    pyproject.toml's `target-version = "py312"` doesn't guarantee `audioop` stays available for
    the life of this module.
    """

    def __init__(self, threshold: float = 0.5, silence_duration_ms: int = 200, sample_rate: int = _AUDIO_SAMPLE_RATE):
        self._cutoff = threshold * 32767
        self._silence_samples_needed = max(1, int(silence_duration_ms / 1000 * sample_rate))
        self._speaking = False
        self._silence_run = 0
        self._buffer = bytearray()

    @property
    def is_speaking(self) -> bool:
        return self._speaking

    def reset(self) -> None:
        self._speaking = False
        self._silence_run = 0
        self._buffer.clear()

    def take_buffer(self) -> bytes:
        data = bytes(self._buffer)
        self._buffer.clear()
        return data

    def feed(self, pcm16_bytes: bytes) -> str | None:
        """Feed one chunk of raw PCM16 mono audio. Returns "speech_started" the first time this
        turn crosses the energy threshold, "speech_stopped" once enough trailing silence has
        elapsed after speech was detected, or None otherwise. Always buffers the raw audio (even
        pre-threshold, so a turn's very first word isn't clipped) for the eventual transcription
        upload."""
        self._buffer.extend(pcm16_bytes)
        usable_len = len(pcm16_bytes) - (len(pcm16_bytes) % 2)
        if usable_len <= 0:
            return None
        samples = array.array("h")
        samples.frombytes(pcm16_bytes[:usable_len])
        if not samples:
            return None
        rms = (sum(s * s for s in samples) / len(samples)) ** 0.5
        event = None
        if rms >= self._cutoff:
            if not self._speaking:
                self._speaking = True
                event = "speech_started"
            self._silence_run = 0
        elif self._speaking:
            self._silence_run += len(samples)
            if self._silence_run >= self._silence_samples_needed:
                event = "speech_stopped"
        return event


@dataclass
class _CascadeSessionState:
    """Per-connection cascade state (issue #82) -- mirrors the realtime pipeline's own
    per-connection locals inside `RTMiddleTier._forward_messages` (e.g. `tools_pending`), just
    collected into one object since this processor owns its whole connection loop directly
    rather than relaying an upstream Realtime API's own events."""

    session_id: str
    persona_id: str
    deployment: str
    voice: str
    messages: list[Any] = field(default_factory=list)
    # The currently in-flight background turn task (greeting or guest turn), if any -- set by
    # `CascadeProcessor._start_turn`/`_send_greeting`, cancelled by `_cancel_current_turn` on a
    # new `speech_started` (barge-in, Rick's #118 review item 5).
    current_turn_task: asyncio.Task | None = None


def _resolve_persona_voice(persona, default_voice: str, allowed_voices: frozenset[str]) -> str:
    """#248: the per-persona default-voice lookup `RTMiddleTier._forward_messages` already does
    for realtime (``persona_voice = _sanitize_voice(bound_persona.manifest.voice.default,
    self.allowed_voices); if persona_voice is not None: voice = persona_voice``), extracted into
    its own function so it's unit-testable without driving `CascadeProcessor._run_session`'s
    whole async message loop. Unlike realtime's relay loop (which may have no bound persona at
    all on some code paths), `persona` here is always the already-resolved, already-validated
    bound persona `RTMiddleTier._websocket_handler` selected before ever dispatching to
    `CascadeProcessor.handle` -- so there is no "persona is None"/"persona not in catalog"
    branch to port, only the sanitize-and-fall-back-to-default shape itself. Returns
    `default_voice` verbatim when the persona's own voice is unset, not a string, or not in
    `allowed_voices` (the exact same silent-fallback semantics `_sanitize_voice`'s own callers
    everywhere else in this codebase already rely on)."""
    persona_voice = _sanitize_voice(persona.manifest.voice.default, allowed_voices)
    return persona_voice if persona_voice is not None else default_voice


class CascadeProcessor:
    """`processors.PipelineProcessor` for the cascade pipeline (issue #82, design doc 7.1/7.4)."""

    pipeline_name: str = "cascade"

    # A guest turn resolves in at most this many chat-completion round trips before this
    # processor gives up and answers with nothing rather than looping forever (a model that
    # keeps calling tools regardless of context is a bug in the model/prompt, not something
    # this processor should let become an unbounded, unbilled request).
    _MAX_TOOL_ROUNDS = 8

    def __init__(
        self,
        *,
        tools: dict[str, Tool],
        sessions: SessionManager,
        persona_catalog,
        persona_prompt_loaders: dict,
        persona_tool_schemas: dict,
        model_catalog,
        foundry_endpoint: str,
        audio_endpoint: str,
        credential,
        default_voice: str = "marin",
        allowed_voices: frozenset[str] | None = None,
    ):
        self.tools = tools
        self._sessions = sessions
        self.persona_catalog = persona_catalog
        self.persona_prompt_loaders = persona_prompt_loaders
        # #170 R4 (Rick's PR #175 round-2 review): per-persona tool schema list, keyed by
        # persona id -- the same `rtmt.persona_tool_schemas` dict `tools.attach_tools_rtmt()`
        # builds for the realtime pipeline (app.py passes it through unchanged here too), so
        # both pipelines advertise the bound persona's own tool descriptions, never the
        # deployment default's.
        self.persona_tool_schemas = persona_tool_schemas
        self.model_catalog = model_catalog
        self.foundry_endpoint = foundry_endpoint
        self.audio_endpoint = audio_endpoint.rstrip("/") if audio_endpoint else audio_endpoint
        self.credential = credential
        self.default_voice = default_voice
        # #248: the SAME allow-list rtmt.py's own `configure_realtime_model` computes
        # (`model.allowed_voices`/the ten GA voices default) -- passed through from app.py as
        # `rtmt.allowed_voices` so a per-persona voice this app's own config.yaml disallows is
        # rejected here exactly like it already is for realtime, rather than trusting any
        # string a persona.json happens to declare.
        self.allowed_voices = allowed_voices if allowed_voices is not None else _DEFAULT_ALLOWED_VOICES
        self._chat_client: ChatCompletionsClient | None = None
        self._vad_threshold = _vad_cfg.get("threshold", 0.5)
        self._vad_silence_ms = _vad_cfg.get("silence_duration_ms", 200)
        # Rick's #118 review item 5 (429 parity): reuses rate_limit.py's shared
        # settings/constants/ladder-shape so a chat/STT/TTS 429 surfaces to the guest through
        # the SAME `extension.rate_limited` event contract the realtime pipeline's own
        # `RateLimitRecovery` already uses -- see `_with_rate_limit_retry` below.
        # #248 (Summer's review note): `from_config` was called with no `environ` argument,
        # so its `RATE_LIMIT_RECOVERY_ENABLED` override (`environ.get(ENABLED_ENV)`) silently
        # never fired for cascade sessions -- `rtmt.py:1341`'s own call already passes
        # `os.environ` and has for as long as the env override has existed; this was simply
        # never ported over when `_rate_limit_settings` was added here. A cascade session's
        # rate-limit recovery could not be toggled off (or on, over a config.yaml default of
        # off) via the env var the way realtime's already can.
        self._rate_limit_settings = RateLimitSettings.from_config(_config, os.environ)

    def resolve_model(self, persona, requested_model_id: str | None) -> ResolvedModel:
        """`processors.PipelineProcessor`'s model-resolution hook -- delegates to
        `resolve_cascade_model` (no legacy deployment fallback, unlike the realtime pipeline's
        `resolve_realtime_model`; see that function's own docstring)."""
        return resolve_cascade_model(persona, requested_model_id, self.model_catalog, pipeline_name=self.pipeline_name)

    async def _get_chat_client(self) -> ChatCompletionsClient:
        if self._chat_client is None:
            self._chat_client = ChatCompletionsClient(
                endpoint=self.foundry_endpoint,
                credential=self.credential,
                credential_scopes=[_COGNITIVE_SERVICES_SCOPE],
            )
        return self._chat_client

    async def _bearer_token(self) -> str:
        token = await self.credential.get_token(_COGNITIVE_SERVICES_SCOPE)
        return token.token

    async def handle(self, request: web.Request, persona, resolved_model: ResolvedModel) -> web.StreamResponse:
        """`processors.PipelineProcessor`'s other required method (#75/PR #106 review item 5).
        Owns this connection's entire lifetime: the WebSocket upgrade, session creation, this
        pipeline's own relay loop, and teardown -- mirroring `RTMiddleTier.handle`'s own
        prepare -> create_session -> loop -> detach shape verbatim, just with a cascade-specific
        loop instead of a realtime-relay one. Reached only through
        `RTMiddleTier._websocket_handler`'s dispatch seam, once `dispatch_processor` has picked
        `self` for `resolved_model`'s own pipeline."""
        ws = web.WebSocketResponse(
            heartbeat=_WS_HEARTBEAT_SEC,
            autoping=True,
            autoclose=True,
            compress=_WS_COMPRESS,
        )
        await ws.prepare(request)

        # #248 (issue #165 parity): re-derived from `request.query["mode"]` the exact same way
        # `RTMiddleTier.handle` does -- `_websocket_handler` (the SHARED dispatch seam this
        # pipeline is only ever reached through, see `app.py`'s wiring) has already validated,
        # before the WS upgrade, that an explicit `?mode=` is `None`/"breakfast"/"lunch" for a
        # persona declaring `features.dayparts`, and rejected anything else with a 400 -- this
        # is redundant-but-harmless defense in depth, not the only enforcement point, same as
        # that method's own comment explains. `None` for a persona that doesn't declare
        # `features.dayparts`, so `create_session`/`order_state_singleton` normalize it to "no
        # menu mode" (unfiltered menu) exactly like realtime already does for the same persona.
        requested_menu_mode = _extract_raw_mode_param(request) if persona.manifest.features.dayparts else None

        session_id = self._sessions.create_session(
            ws, persona=persona, model_id=resolved_model.id,
            model_deployment=resolved_model.deployment, model_reasoning=resolved_model.reasoning,
            model_pipeline=resolved_model.pipeline, menu_mode=requested_menu_mode,
        )
        try:
            identifiers = order_state_singleton.get_session_identifiers(session_id)
            await self._sessions.emit_session_identifiers(ws, "extension.session_metadata", identifiers)
            await self._run_session(ws, session_id, persona, resolved_model)
        finally:
            # Covers an upstream (transcription/chat/TTS) connect failure the same way
            # RTMiddleTier.handle's own finally does -- a no-op if the loop's own exit already
            # detached the session.
            self._sessions.detach_session(ws, self._sessions.get_session_id(ws),
                                          reason=f"handler exit code={ws.close_code}")
        return ws

    async def _run_session(self, ws: web.WebSocketResponse, session_id: str, persona, resolved_model: ResolvedModel) -> None:
        prompt_loader = self.persona_prompt_loaders.get(persona.id)
        # #248: per-persona default voice -- see `_resolve_persona_voice`'s own doc comment.
        voice = _resolve_persona_voice(persona, self.default_voice, self.allowed_voices)
        state = _CascadeSessionState(
            session_id=session_id,
            persona_id=persona.id,
            deployment=resolved_model.deployment,
            voice=voice,
        )
        if prompt_loader is not None:
            state.messages.append(SystemMessage(content=prompt_loader.get_system_prompt()))
        detector = _TurnDetector(threshold=self._vad_threshold, silence_duration_ms=self._vad_silence_ms)

        # Rick's #118 review item 5 (greeting on connect, design 7.1): spawned as a background
        # task -- like every other turn below -- rather than awaited inline, so the WS loop
        # starts consuming audio frames immediately and a guest who starts talking over the
        # greeting can still barge in on it via the SAME cancellation path as any other turn.
        self._start_greeting(ws, session_id, state, prompt_loader)

        async for msg in ws:
            if msg.type == web.WSMsgType.TEXT:
                try:
                    data = json.loads(msg.data)
                except (ValueError, TypeError):
                    logger.warning("Cascade: ignoring malformed client message (session=%s)", session_id)
                    continue
                try:
                    await self._handle_client_message(ws, session_id, state, detector, data, prompt_loader)
                except Exception:
                    # A guest turn failing (a bad STT/chat/TTS call, etc.) must not tear down
                    # the whole WebSocket -- the guest should be able to just try again. Same
                    # philosophy as rtmt.py's own tool-execution catch-all (#36).
                    logger.exception("Cascade: unhandled error processing a client message (session=%s)", session_id)
            elif msg.type in (web.WSMsgType.ERROR, web.WSMsgType.CLOSE, web.WSMsgType.CLOSING):
                break
        await self._cancel_current_turn(state, "connection closing")

    async def _handle_client_message(self, ws, session_id, state, detector, data, prompt_loader) -> None:
        msg_type = data.get("type")
        if msg_type == "input_audio_buffer.append":
            try:
                pcm = base64.b64decode(data.get("audio", ""))
            except (ValueError, TypeError):
                return
            event = detector.feed(pcm)
            if event == "speech_started":
                # Barge-in (Rick's #118 review item 5): cancel whatever turn (guest turn OR
                # greeting) is still in flight BEFORE telling the client speech started, so no
                # further response.audio.delta/chat-completion work for the stale turn survives
                # past this point -- useRealtime.tsx already stops playback purely reactively on
                # this same event, so no new wire-protocol event is needed on top of it.
                await self._cancel_current_turn(state, "guest started speaking (barge-in)")
                await ws.send_json({"type": "input_audio_buffer.speech_started"})
            elif event == "speech_stopped":
                turn_audio = detector.take_buffer()
                detector.reset()
                self._start_turn(ws, session_id, state, turn_audio)
        elif msg_type == "input_audio_buffer.clear":
            detector.reset()
        elif msg_type == "extension.set_voice":
            # Rick's #253 review item 1: mirror realtime's own extension.set_voice handler
            # (rtmt.py's `_sanitize_voice` call) -- only a value from `self.allowed_voices`
            # may ever be adopted. Before this fix, any truthy value (an unknown string, a
            # dict, an int, ...) was accepted straight into `state.voice`/the session store,
            # silently breaking TTS on the next turn instead of being dropped with a warning.
            new_voice = _sanitize_voice(data.get("voice"), self.allowed_voices)
            if new_voice is None:
                logger.warning(
                    "Cascade: dropped extension.set_voice with an unknown/invalid voice %s (session=%s)",
                    _truncate_for_log(data.get("voice")), session_id)
            else:
                state.voice = new_voice
                self._sessions.set_voice(session_id, new_voice)
        # session.update / extension.resume / anything else not listed above: a documented,
        # explicit scope cut for this issue's v1 (see the decision note) -- no-op rather than an
        # error, so an unrecognized/unused message never disrupts the session.

    def _start_turn(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, turn_audio: bytes) -> None:
        """Spawns `_process_turn` as a background task (Rick's #118 review item 5) instead of
        awaiting it inline, so the WS message loop in `_run_session` keeps consuming incoming
        audio frames -- and can therefore detect a NEW `speech_started` at all -- while a turn's
        STT/chat/TTS work is still in flight. Before this fix, `_process_turn` was awaited
        directly inside the loop, so barge-in could never even be detected, let alone cancelled,
        during a turn."""
        async def _run() -> None:
            try:
                await self._process_turn(ws, session_id, state, turn_audio)
            except Exception:
                logger.exception("Cascade: unhandled error processing a turn (session=%s)", session_id)
        state.current_turn_task = _spawn(_run())

    def _start_greeting(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, prompt_loader) -> None:
        async def _run() -> None:
            try:
                await self._send_greeting(ws, session_id, state, prompt_loader)
            except Exception:
                logger.exception("Cascade: unhandled error sending the greeting (session=%s)", session_id)
        state.current_turn_task = _spawn(_run())

    async def _cancel_current_turn(self, state: _CascadeSessionState, reason: str) -> None:
        """Barge-in's cancellation half (Rick's #118 review item 5): cancels whatever turn
        (guest turn or greeting) `_start_turn`/`_start_greeting` most recently spawned, if it
        hasn't already finished. Cancellation naturally halts further `ws.send_json` calls in
        `_speak`'s streaming loop and further azure-ai-inference/aiohttp calls in
        `_run_chat_tool_loop`/`_transcribe`/`_speak` -- no extra flag-checking is needed at each
        of those await points."""
        task = state.current_turn_task
        if task is None or task.done():
            state.current_turn_task = None
            return
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        except Exception:
            logger.exception("Cascade: in-flight turn raised while being cancelled (session=%s)", state.session_id)
        logger.info("Cascade: cancelled in-flight turn: %s (session=%s)", reason, state.session_id)
        state.current_turn_task = None

    async def _process_turn(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, turn_audio: bytes) -> None:
        if not turn_audio:
            return
        try:
            transcript = await self._with_rate_limit_retry(ws, session_id, lambda: self._transcribe(turn_audio), "transcription")
        except CascadeRateLimitExhausted:
            return  # the client already got the final extension.rate_limited notice
        except Exception:
            logger.exception("Cascade transcription failed (session=%s)", session_id)
            return
        if not transcript.strip():
            return
        await ws.send_json({
            "type": "conversation.item.input_audio_transcription.completed",
            "transcript": transcript,
        })
        state.messages.append(UserMessage(content=transcript))
        await self._run_turn_and_speak(ws, session_id, state)

    async def _send_greeting(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, prompt_loader) -> None:
        """Cascade parity with the realtime pipeline's connect-time greeting (Rick's #118
        review item 5, design 7.1). `RTMiddleTier`'s own greeting (session_manager.py's
        `build_greeting_msg`) is a literal `conversation.item.create` message seeded straight
        into the Realtime API's own upstream conversation; cascade has no such upstream
        conversation to seed, so instead this feeds the SAME instruction text
        (`greeting.yaml`'s `item.content[0].text`, "Say EXACTLY this greeting...") into this
        pipeline's own normal chat-tool-loop + TTS turn machinery once, up front -- the model is
        told to say the greeting verbatim, so this naturally produces the identical spoken
        greeting through this pipeline's own turn-taking path rather than duplicating it."""
        if prompt_loader is None:
            return
        try:
            text = prompt_loader.get_greeting()["item"]["content"][0]["text"]
        except (KeyError, IndexError, TypeError):
            logger.warning("Cascade: greeting.yaml has an unexpected shape; skipping the greeting (session=%s)",
                            session_id)
            return
        state.messages.append(UserMessage(content=text))
        await self._run_turn_and_speak(ws, session_id, state)

    async def _run_turn_and_speak(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState) -> None:
        """Shared tail of both a guest turn and the connect-time greeting: run the chat-tool
        loop against whatever's already in `state.messages`, then speak the final answer.
        Cancellation (barge-in, `_cancel_current_turn`) propagates through the awaits below
        exactly like any other `asyncio.CancelledError` -- there is no extra flag-checking."""
        response_id = new_middle_tier_item_id()
        await ws.send_json({"type": "response.created", "response": {"id": response_id}})

        try:
            final_text = await self._run_chat_tool_loop(ws, session_id, state)
        except CascadeRateLimitExhausted:
            await ws.send_json({"type": "response.done", "response": {"id": response_id}})
            return

        if final_text:
            await ws.send_json({"type": "response.audio_transcript.delta", "delta": final_text})
            try:
                await self._with_rate_limit_retry(ws, session_id, lambda: self._speak(ws, final_text, state.voice), "text-to-speech")
            except CascadeRateLimitExhausted:
                pass
            except Exception:
                logger.exception("Cascade TTS failed (session=%s)", session_id)

        await ws.send_json({"type": "response.done", "response": {"id": response_id}})

        identifiers = order_state_singleton.advance_round_trip(session_id)
        await self._sessions.emit_session_identifiers(ws, "extension.round_trip_token", identifiers)

    async def _with_rate_limit_retry(self, ws: web.WebSocketResponse, session_id: str, op, op_name: str):
        """Runs *op* (a zero-arg async callable performing ONE chat/STT/TTS call) applying the
        SAME retry-ladder semantics as `rate_limit.py`'s `RateLimitRecovery` (silent retry, then
        `extension.rate_limited` at attempt 1, then `extension.rate_limited` with `final: true`)
        -- adapted for cascade's REST-call-based 429s (an azure-ai-inference
        `HttpResponseError`/aiohttp `ClientResponseError` raised directly from the call) instead
        of the realtime pipeline's own WS `response.create`/`response.done` lifecycle. A non-429
        error, or a 429 while `RATE_LIMIT_RECOVERY_ENABLED` is off, propagates unchanged so
        existing callers' own try/except keep handling it exactly as before. Raises
        `CascadeRateLimitExhausted` once the ladder is spent; by then the client has already
        gotten the final notice."""
        settings = self._rate_limit_settings
        attempt = 0
        while True:
            try:
                return await op()
            except (HttpResponseError, aiohttp.ClientResponseError) as exc:
                if _http_status_of(exc) != 429 or not settings.enabled:
                    raise
                hint = _retry_hint_of(exc)
                if attempt >= settings.max_retries:
                    logger.warning("Cascade %s rate-limited; retries exhausted after %d attempt(s) (session=%s)",
                                   op_name, attempt, session_id)
                    await ws.send_json({"type": RATE_LIMITED_EVENT, "attempt": attempt, "final": True})
                    raise CascadeRateLimitExhausted(op_name) from exc
                if attempt == 0:
                    delay = retry_delay(hint, settings.retry_delay_seconds, FIRST_RETRY_BOUNDS)
                else:
                    delay = retry_delay(hint, settings.second_retry_delay_seconds, SECOND_RETRY_BOUNDS)
                    await ws.send_json({"type": RATE_LIMITED_EVENT, "attempt": attempt})
                logger.info("Cascade %s rate-limited; retry %d after %.2fs (session=%s)",
                            op_name, attempt + 1, delay, session_id)
                await asyncio.sleep(delay)
                attempt += 1

    async def _run_chat_tool_loop(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState) -> str:
        client = await self._get_chat_client()
        # #170 R4: this session's own bound persona's tool schemas, falling back to
        # `self.tools`' own (the deployment default's) schemas only when the bound
        # persona has no `persona_tool_schemas` entry -- same fallback shape as the
        # realtime pipeline's `_forward_messages` (rtmt.py).
        tool_schemas = self.persona_tool_schemas.get(state.persona_id)
        if tool_schemas is None:
            logger.warning(
                "No persona_tool_schemas entry for persona_id=%s; falling back to the "
                "deployment default's tool schemas (session=%s)", state.persona_id, session_id)
            tool_schemas = [tool.schema for tool in self.tools.values()]
        tool_defs = _tool_definitions(tool_schemas)
        for _round in range(self._MAX_TOOL_ROUNDS):
            completion = await self._with_rate_limit_retry(
                ws, session_id,
                lambda: client.complete(
                    messages=state.messages,
                    model=state.deployment,
                    tools=tool_defs or None,
                    **cascade_chat_kwargs(),
                ),
                "chat completion",
            )
            message = completion.choices[0].message
            tool_calls = message.tool_calls or []
            if not tool_calls:
                content = message.content or ""
                state.messages.append(AssistantMessage(content=content))
                return content

            state.messages.append(AssistantMessage(content=message.content, tool_calls=tool_calls))
            previous_item_id = new_middle_tier_item_id()
            for tool_call in tool_calls:
                await self._execute_tool_call(ws, session_id, state, tool_call, previous_item_id)

        logger.warning("Cascade tool-call loop hit the %d-round cap without a final answer (session=%s)",
                        self._MAX_TOOL_ROUNDS, session_id)
        return ""

    async def _execute_tool_call(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, tool_call, previous_item_id: str) -> None:
        """Replicates `RTMiddleTier`'s own `response.output_item.done` tool-execution contract
        (rtmt.py) verbatim, adapted from a single Realtime-API function_call item to a Chat
        Completions `tool_call`: same target-call convention (session_id passed for
        update_order/get_order/reset_order/search), same `ToolResultDirection`-gated
        send-to-client logic, same best-effort ticket-refresh-on-exception fallback."""
        name = tool_call.function.name
        tool = self.tools.get(name)
        if tool is None:
            logger.error("Unknown tool requested: %s", name)
            state.messages.append(ToolMessage(content="", tool_call_id=tool_call.id))
            return

        output_text: str
        send_to_client: bool
        client_text: str | None
        try:
            args = json.loads(tool_call.function.arguments or "{}")
            logger.info("Executing tool '%s' (session=%s)", name, session_id)
            t0 = time.monotonic()
            if name in ("update_order", "get_order", "reset_order", "search"):
                result: ToolResult = await tool.target(args, session_id)
            else:
                result = await tool.target(args)
            elapsed_ms = (time.monotonic() - t0) * 1000
            logger.info("Tool '%s' result direction=%s (%.1fms)", name, result.destination, elapsed_ms)

            ctx_monitor = self._sessions.get_context_monitor(session_id)
            if ctx_monitor:
                ctx_monitor.add_content(tool_call.function.arguments or "")
                ctx_monitor.add_content(result.to_text())

            output_text = result.to_text() if result.destination in (ToolResultDirection.TO_SERVER, ToolResultDirection.TO_BOTH) else ""
            send_to_client = result.destination in (ToolResultDirection.TO_CLIENT, ToolResultDirection.TO_BOTH)
            client_text = result.to_client_text() if send_to_client else None
        except Exception:
            logger.exception("Tool '%s' raised an unhandled exception (session=%s)", name, session_id)
            prompt_loader = self.persona_prompt_loaders.get(state.persona_id)
            output_text = prompt_loader.render_error("tool_execution_failed") if prompt_loader else (
                "Something went wrong with that action and it did not complete. "
                "Don't retry it yet -- call get_order to confirm the order's current "
                "state, then ask the guest to repeat what they'd like."
            )
            send_to_client = False
            client_text = None
            if session_id is not None:
                try:
                    ticket_json = order_state_singleton.get_order_summary_json(session_id)
                except Exception:
                    logger.warning(
                        "Could not read order state to refresh the ticket after a tool "
                        "failure (session=%s)", session_id,
                    )
                else:
                    await ws.send_json({
                        "type": "extension.middle_tier_tool_response",
                        "previous_item_id": previous_item_id,
                        "tool_name": "get_order",
                        "tool_result": ticket_json,
                    })

        state.messages.append(ToolMessage(content=output_text, tool_call_id=tool_call.id))
        if send_to_client:
            await ws.send_json({
                "type": "extension.middle_tier_tool_response",
                "previous_item_id": previous_item_id,
                "tool_name": name,
                "tool_result": client_text,
            })

    async def _transcribe(self, pcm16_bytes: bytes) -> str:
        wav_bytes = _pcm16_to_wav_bytes(pcm16_bytes)
        token = await self._bearer_token()
        deployment = self.model_catalog.deployment_for(self.model_catalog.cascade_audio.transcription)
        url = f"{self.audio_endpoint}/openai/v1/audio/transcriptions"
        form = aiohttp.FormData()
        form.add_field("file", wav_bytes, filename="turn.wav", content_type="audio/wav")
        form.add_field("model", deployment)
        async with aiohttp.ClientSession() as http:
            async with http.post(url, data=form, headers={"Authorization": f"Bearer {token}"}) as resp:
                resp.raise_for_status()
                payload = await resp.json()
        return payload.get("text", "")

    async def _speak(self, ws: web.WebSocketResponse, text: str, voice: str) -> None:
        token = await self._bearer_token()
        deployment = self.model_catalog.deployment_for(self.model_catalog.cascade_audio.tts)
        url = f"{self.audio_endpoint}/openai/v1/audio/speech"
        body = {"model": deployment, "input": text, "voice": voice, "response_format": "pcm"}
        async with aiohttp.ClientSession() as http:
            async with http.post(url, json=body, headers={"Authorization": f"Bearer {token}"}) as resp:
                resp.raise_for_status()
                audio_bytes = await resp.read()
        chunk_size = 24000  # ~0.5s of 24kHz mono PCM16 per delta frame -- an arbitrary but
                            # reasonable chunk size for smooth client-side streamed playback.
        for i in range(0, len(audio_bytes), chunk_size):
            chunk = audio_bytes[i:i + chunk_size]
            await ws.send_json({
                "type": "response.audio.delta",
                "delta": base64.b64encode(chunk).decode("ascii"),
            })
