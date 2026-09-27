"""Local PipelineProcessor (issue #81, P2-12): on-device STT -> chat (tool calling) -> TTS,
talking to a companion process (`local_runtime.py`'s `LocalRuntimeClient`) instead of any Azure
endpoint, registered in `processors.ProcessorRegistry` alongside `RTMiddleTier`'s realtime
pipeline and `CascadeProcessor`'s cascade pipeline (#75's seam, extended by #82) -- reached ONLY
through `RTMiddleTier._websocket_handler`'s own `dispatch_processor` call, never a second
WebSocket route: there is exactly one `/realtime` route, shared by all three processors (see
app.py's wiring).

Design doc references: ADR-001 decision 7 (keep the sibling repo's local on-device mode, but
make it a persona-agnostic pipeline, off by default everywhere), docs/persona-architecture.md
section 7 (local pipeline / processor interface / model catalog). See `local_runtime.py`'s own
module docstring for the companion-process HTTP/JSON wire contract and the "why HTTP, not
in-process ONNX/Whisper/Piper" rationale.

Structurally, this module is `cascade_processor.py` (issue #82) ported onto a local companion
runtime instead of Azure OpenAI/a Foundry chat model:
  - SAME wire-protocol contract towards the frontend (`extension.session_metadata`,
    `extension.round_trip_token`, `extension.middle_tier_tool_response`, `response.created`/
    `response.audio.delta`/`response.audio_transcript.delta`/`response.done`,
    `input_audio_buffer.speech_started`/`conversation.item.input_audio_transcription.completed`)
    -- issue #81's own "same tools, structured results, session metadata and wire protocol as
    realtime/cascade" acceptance line.
  - SAME shared `tools`/`SessionManager`/persona catalog objects `app.py` already built for
    `rtmt`/`cascade_processor` -- `_execute_tool_call` below is the SAME
    `ToolResultDirection`-gated send-to-client contract as cascade's own (byte-identical
    `Tool`/`ToolResult` objects, Beth's #77 scope, imported here but never edited).
  - SAME local RMS-energy `_TurnDetector` VAD (duplicated, not imported -- see cascade's own
    module docstring's rationale for why turn-detection is duplicated locally rather than
    shared; the same boundary discipline applies one level further out, between this module and
    cascade_processor.py).
  - NO rate-limit retry ladder (`cascade_processor._with_rate_limit_retry`/
    `CascadeRateLimitExhausted`): that machinery exists purely for Azure quota 429s, which don't
    apply to an on-device/companion process. A `local_runtime.LocalRuntimeError` (or any other
    unexpected failure) during a turn is logged and ends the turn cleanly instead -- the guest
    can just try again, same end-user outcome as cascade's own exhausted-retries path, minus the
    `extension.rate_limited` notice (there is nothing to wait out).
  - NO WAV-wrapping of the transcription upload (`cascade_processor._pcm16_to_wav_bytes`): the
    local runtime's `/v1/transcribe` endpoint takes raw PCM16 directly (matching the sibling
    repo's own `whisper_stt.py::transcribe(audio_pcm: bytes)` signature) -- there's no
    file-upload endpoint on this side of the wire needing a real container/extension.
"""

from __future__ import annotations

import array
import asyncio
import base64
import json
import logging
from dataclasses import dataclass, field
from typing import Any

from aiohttp import web

from config_loader import get_config
from local_runtime import HttpLocalRuntimeClient, LocalRuntimeClient, LocalRuntimeError
from order_state import order_state_singleton
from processors import ResolvedModel, resolve_local_model
from rtmt import Tool, ToolResult, ToolResultDirection
from session_manager import SessionManager, new_middle_tier_item_id

logger = logging.getLogger(__name__)

_config = get_config()
_conn_cfg = _config.get("connection", {})
_vad_cfg = _config.get("vad", {})

_WS_HEARTBEAT_SEC = _conn_cfg.get("ws_heartbeat_seconds", 15.0)
_WS_COMPRESS = bool(_conn_cfg.get("ws_compression", False))

_AUDIO_SAMPLE_RATE = 24000
_AUDIO_SAMPLE_WIDTH = 2  # PCM16, matches both mic input and speaker output -- same as
                          # cascade_processor.py's own _AUDIO_SAMPLE_RATE/_AUDIO_SAMPLE_WIDTH,
                          # since it's the frontend's own recorder/player format, not a
                          # pipeline-specific choice.

__all__ = ["LocalProcessor"]


def _tool_definitions(tools: dict[str, Tool]) -> list[dict[str, Any]]:
    """tools.py's own flat Realtime-API-style schemas (`{"type": "function", "name": ...,
    "parameters": ...}`), passed straight through to `/v1/chat` unedited -- unlike cascade's own
    `_tool_definitions`, no SDK-specific nested wrapping is needed: the local runtime's wire
    contract IS this flat shape (see local_runtime.py's module docstring)."""
    return [tool.schema for tool in tools.values()]


# Fire-and-forget turn-processing tasks (barge-in support, mirroring cascade's own
# `_spawn`/`_BACKGROUND_TASKS`/`_on_background_task_done`, itself mirroring rtmt.py's).
# Duplicated locally (not imported from cascade_processor.py) -- see this module's own
# docstring's boundary-discipline note.
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
        logger.debug("Local background turn task raised (retrieved, not re-raised): %r", exc)


class _TurnDetector:
    """A minimal, local RMS-energy voice-activity detector -- identical in behavior to
    `cascade_processor._TurnDetector` (same config keys, same algorithm), duplicated here rather
    than imported for the same boundary-discipline reason as `_spawn`/`_BACKGROUND_TASKS` above.
    There is no upstream Realtime API turn-segmenting audio for either non-realtime pipeline; the
    local pipeline needs its own VAD for exactly the same reason cascade does."""

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
class _LocalSessionState:
    """Per-connection local-pipeline state -- mirrors `cascade_processor._CascadeSessionState`
    field-for-field, except `runtime` replaces `deployment`: the local pipeline talks to one
    `LocalRuntimeClient` for the whole connection (built once in `_run_session` from
    `resolved_model.deployment`, the local runtime's base URL -- see
    `processors.resolve_local_model`'s own docstring for why that field is repurposed this way),
    rather than looking up a Foundry deployment name per-call."""

    session_id: str
    persona_id: str
    runtime: LocalRuntimeClient
    voice: str
    messages: list[dict[str, Any]] = field(default_factory=list)
    current_turn_task: asyncio.Task | None = None


class LocalProcessor:
    """`processors.PipelineProcessor` for the local (on-device) pipeline (issue #81, ADR-001
    decision 7, design doc section 7)."""

    pipeline_name: str = "local"

    # Same cap and same rationale as cascade_processor.CascadeProcessor._MAX_TOOL_ROUNDS: a
    # model that keeps calling tools regardless of context is a bug, not something this
    # processor should let become an unbounded request against the companion process.
    _MAX_TOOL_ROUNDS = 8

    def __init__(
        self,
        *,
        tools: dict[str, Tool],
        sessions: SessionManager,
        persona_catalog,
        persona_prompt_loaders: dict,
        model_catalog,
        default_voice: str = "en_US-amy-medium",
        runtime_factory=HttpLocalRuntimeClient,
    ):
        self.tools = tools
        self._sessions = sessions
        self.persona_catalog = persona_catalog
        self.persona_prompt_loaders = persona_prompt_loaders
        self.model_catalog = model_catalog
        self.default_voice = default_voice
        # Overridable so tests can substitute a fake runtime client without a real HTTP
        # companion process -- the ONLY constructor seam this processor needs for that (compare
        # cascade's own tests, which monkeypatch `_get_chat_client`/`_transcribe`/`_speak`
        # directly since those methods build SDK clients inline).
        self._runtime_factory = runtime_factory
        self._vad_threshold = _vad_cfg.get("threshold", 0.5)
        self._vad_silence_ms = _vad_cfg.get("silence_duration_ms", 200)

    def resolve_model(self, persona, requested_model_id: str | None) -> ResolvedModel:
        """`processors.PipelineProcessor`'s model-resolution hook -- delegates to
        `resolve_local_model`, which is where "local selectable without the runtime configured"
        is actually rejected (see that function's own docstring/tests)."""
        return resolve_local_model(persona, requested_model_id, self.model_catalog, pipeline_name=self.pipeline_name)

    async def handle(self, request: web.Request, persona, resolved_model: ResolvedModel) -> web.StreamResponse:
        """Owns this connection's entire lifetime -- mirrors `CascadeProcessor.handle` verbatim
        (WebSocket upgrade, session creation, this pipeline's own relay loop, teardown). Reached
        only through `RTMiddleTier._websocket_handler`'s dispatch seam, once `dispatch_processor`
        has picked `self` for `resolved_model`'s own pipeline (`"local"`)."""
        ws = web.WebSocketResponse(
            heartbeat=_WS_HEARTBEAT_SEC,
            autoping=True,
            autoclose=True,
            compress=_WS_COMPRESS,
        )
        await ws.prepare(request)

        session_id = self._sessions.create_session(
            ws, persona=persona, model_id=resolved_model.id,
            model_deployment=resolved_model.deployment, model_reasoning=resolved_model.reasoning,
            model_pipeline=resolved_model.pipeline,
        )
        try:
            identifiers = order_state_singleton.get_session_identifiers(session_id)
            await self._sessions.emit_session_identifiers(ws, "extension.session_metadata", identifiers)
            await self._run_session(ws, session_id, persona, resolved_model)
        finally:
            self._sessions.detach_session(ws, self._sessions.get_session_id(ws),
                                          reason=f"handler exit code={ws.close_code}")
        return ws

    async def _run_session(self, ws: web.WebSocketResponse, session_id: str, persona, resolved_model: ResolvedModel) -> None:
        prompt_loader = self.persona_prompt_loaders.get(persona.id)
        state = _LocalSessionState(
            session_id=session_id,
            persona_id=persona.id,
            runtime=self._runtime_factory(resolved_model.deployment),
            voice=self.default_voice,
        )
        if prompt_loader is not None:
            # get_local_system_prompt (issue #81), not get_system_prompt -- falls back to the
            # same cloud system prompt when a pack has no local_system_prompt.yaml of its own
            # (see prompt_loader.py's own docstring for that fallback).
            state.messages.append({"role": "system", "content": prompt_loader.get_local_system_prompt()})
        detector = _TurnDetector(threshold=self._vad_threshold, silence_duration_ms=self._vad_silence_ms)

        self._start_greeting(ws, session_id, state, prompt_loader)

        async for msg in ws:
            if msg.type == web.WSMsgType.TEXT:
                try:
                    data = json.loads(msg.data)
                except (ValueError, TypeError):
                    logger.warning("Local: ignoring malformed client message (session=%s)", session_id)
                    continue
                try:
                    await self._handle_client_message(ws, session_id, state, detector, data)
                except Exception:
                    logger.exception("Local: unhandled error processing a client message (session=%s)", session_id)
            elif msg.type in (web.WSMsgType.ERROR, web.WSMsgType.CLOSE, web.WSMsgType.CLOSING):
                break
        await self._cancel_current_turn(state, "connection closing")

    async def _handle_client_message(self, ws, session_id, state, detector, data) -> None:
        msg_type = data.get("type")
        if msg_type == "input_audio_buffer.append":
            try:
                pcm = base64.b64decode(data.get("audio", ""))
            except (ValueError, TypeError):
                return
            event = detector.feed(pcm)
            if event == "speech_started":
                await self._cancel_current_turn(state, "guest started speaking (barge-in)")
                await ws.send_json({"type": "input_audio_buffer.speech_started"})
            elif event == "speech_stopped":
                turn_audio = detector.take_buffer()
                detector.reset()
                self._start_turn(ws, session_id, state, turn_audio)
        elif msg_type == "input_audio_buffer.clear":
            detector.reset()
        elif msg_type == "extension.set_voice":
            voice = data.get("voice")
            if voice:
                state.voice = voice
                self._sessions.set_voice(session_id, voice)
        # session.update / extension.resume / anything else: same documented no-op scope cut as
        # cascade_processor._handle_client_message.

    def _start_turn(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState, turn_audio: bytes) -> None:
        async def _run() -> None:
            try:
                await self._process_turn(ws, session_id, state, turn_audio)
            except Exception:
                logger.exception("Local: unhandled error processing a turn (session=%s)", session_id)
        state.current_turn_task = _spawn(_run())

    def _start_greeting(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState, prompt_loader) -> None:
        async def _run() -> None:
            try:
                await self._send_greeting(ws, session_id, state, prompt_loader)
            except Exception:
                logger.exception("Local: unhandled error sending the greeting (session=%s)", session_id)
        state.current_turn_task = _spawn(_run())

    async def _cancel_current_turn(self, state: _LocalSessionState, reason: str) -> None:
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
            logger.exception("Local: in-flight turn raised while being cancelled (session=%s)", state.session_id)
        logger.info("Local: cancelled in-flight turn: %s (session=%s)", reason, state.session_id)
        state.current_turn_task = None

    async def _process_turn(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState, turn_audio: bytes) -> None:
        if not turn_audio:
            return
        try:
            transcript = await self._transcribe(state, turn_audio)
        except LocalRuntimeError:
            logger.exception("Local transcription failed (session=%s)", session_id)
            return
        if not transcript.strip():
            return
        await ws.send_json({
            "type": "conversation.item.input_audio_transcription.completed",
            "transcript": transcript,
        })
        state.messages.append({"role": "user", "content": transcript})
        await self._run_turn_and_speak(ws, session_id, state)

    async def _send_greeting(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState, prompt_loader) -> None:
        """Local-pipeline parity with the realtime/cascade connect-time greeting -- same
        approach as `CascadeProcessor._send_greeting`: feed the greeting instruction text into
        this pipeline's own normal chat-tool-loop + TTS turn machinery once, up front, so the
        model says the greeting verbatim through the SAME turn-taking path as any other turn."""
        if prompt_loader is None:
            return
        try:
            text = prompt_loader.get_greeting()["item"]["content"][0]["text"]
        except (KeyError, IndexError, TypeError):
            logger.warning("Local: greeting.yaml has an unexpected shape; skipping the greeting (session=%s)",
                            session_id)
            return
        state.messages.append({"role": "user", "content": text})
        await self._run_turn_and_speak(ws, session_id, state)

    async def _run_turn_and_speak(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState) -> None:
        response_id = new_middle_tier_item_id()
        await ws.send_json({"type": "response.created", "response": {"id": response_id}})

        try:
            final_text = await self._run_chat_tool_loop(ws, session_id, state)
        except LocalRuntimeError:
            logger.exception("Local chat failed (session=%s)", session_id)
            await ws.send_json({"type": "response.done", "response": {"id": response_id}})
            return

        if final_text:
            await ws.send_json({"type": "response.audio_transcript.delta", "delta": final_text})
            try:
                await self._speak(ws, state, final_text)
            except LocalRuntimeError:
                logger.exception("Local TTS failed (session=%s)", session_id)

        await ws.send_json({"type": "response.done", "response": {"id": response_id}})

        identifiers = order_state_singleton.advance_round_trip(session_id)
        await self._sessions.emit_session_identifiers(ws, "extension.round_trip_token", identifiers)

    async def _run_chat_tool_loop(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState) -> str:
        tool_defs = _tool_definitions(self.tools)
        for _round in range(self._MAX_TOOL_ROUNDS):
            result = await state.runtime.chat(state.messages, tool_defs)
            if not result.tool_calls:
                content = result.content or ""
                state.messages.append({"role": "assistant", "content": content})
                return content

            state.messages.append({
                "role": "assistant",
                "content": result.content,
                "tool_calls": [
                    {"id": call.id, "name": call.name, "arguments": call.arguments}
                    for call in result.tool_calls
                ],
            })
            previous_item_id = new_middle_tier_item_id()
            for tool_call in result.tool_calls:
                await self._execute_tool_call(ws, session_id, state, tool_call, previous_item_id)

        logger.warning("Local tool-call loop hit the %d-round cap without a final answer (session=%s)",
                        self._MAX_TOOL_ROUNDS, session_id)
        return ""

    async def _execute_tool_call(self, ws: web.WebSocketResponse, session_id: str, state: _LocalSessionState, tool_call, previous_item_id: str) -> None:
        """Byte-identical tool-execution contract to `CascadeProcessor._execute_tool_call` (and,
        beneath that, `RTMiddleTier`'s own `response.output_item.done` handling): same
        target-call convention, same `ToolResultDirection`-gated send-to-client logic, same
        best-effort ticket-refresh-on-exception fallback. This is issue #81's own "same tools,
        structured results... as realtime/cascade" acceptance line, and the mutation-test seam
        the task calls out ("local tool result dropped -> row fails" -- dropping the
        `send_to_client`-gated `ws.send_json` below, or the `state.messages.append` above it,
        makes the corresponding tests in test_local_processor.py fail)."""
        name = tool_call.name
        tool = self.tools.get(name)
        if tool is None:
            logger.error("Unknown tool requested: %s", name)
            state.messages.append({"role": "tool", "content": "", "tool_call_id": tool_call.id})
            return

        output_text: str
        send_to_client: bool
        client_text: str | None
        try:
            args = json.loads(tool_call.arguments or "{}")
            logger.info("Executing tool '%s' (session=%s)", name, session_id)
            if name in ("update_order", "get_order", "reset_order", "search"):
                result: ToolResult = await tool.target(args, session_id)
            else:
                result = await tool.target(args)
            logger.info("Tool '%s' result direction=%s", name, result.destination)

            ctx_monitor = self._sessions.get_context_monitor(session_id)
            if ctx_monitor:
                ctx_monitor.add_content(tool_call.arguments or "")
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

        state.messages.append({"role": "tool", "content": output_text, "tool_call_id": tool_call.id})
        if send_to_client:
            await ws.send_json({
                "type": "extension.middle_tier_tool_response",
                "previous_item_id": previous_item_id,
                "tool_name": name,
                "tool_result": client_text,
            })

    async def _transcribe(self, state: _LocalSessionState, pcm16_bytes: bytes) -> str:
        return await state.runtime.transcribe(pcm16_bytes)

    async def _speak(self, ws: web.WebSocketResponse, state: _LocalSessionState, text: str) -> None:
        audio_bytes = await state.runtime.speak(text, state.voice)
        chunk_size = 24000  # ~0.5s of 24kHz mono PCM16 per delta frame -- same chunk size as
                            # cascade_processor._speak, an arbitrary but reasonable size for
                            # smooth client-side streamed playback.
        for i in range(0, len(audio_bytes), chunk_size):
            chunk = audio_bytes[i:i + chunk_size]
            await ws.send_json({
                "type": "response.audio.delta",
                "delta": base64.b64encode(chunk).decode("ascii"),
            })
