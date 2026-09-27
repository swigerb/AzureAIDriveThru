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
import base64
import io
import json
import logging
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

from config_loader import get_config
from conformance_hooks import cascade_chat_kwargs
from order_state import order_state_singleton
from processors import ResolvedModel, resolve_cascade_model
from rtmt import Tool, ToolResult, ToolResultDirection
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


def _tool_definitions(tools: dict[str, Tool]) -> list[ChatCompletionsToolDefinition]:
    """Converts tools.py's flat Realtime-API-style schemas (`{"type": "function", "name": ...,
    "parameters": ...}`) into the nested Chat-Completions-style `ChatCompletionsToolDefinition`
    the azure-ai-inference SDK expects. Same schemas (`search_tool_schema` et al, unedited --
    Beth's #77 scope), just wrapped in this SDK's own shape."""
    definitions = []
    for tool in tools.values():
        schema = tool.schema
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
        model_catalog,
        foundry_endpoint: str,
        audio_endpoint: str,
        credential,
        default_voice: str = "marin",
    ):
        self.tools = tools
        self._sessions = sessions
        self.persona_catalog = persona_catalog
        self.persona_prompt_loaders = persona_prompt_loaders
        self.model_catalog = model_catalog
        self.foundry_endpoint = foundry_endpoint
        self.audio_endpoint = audio_endpoint.rstrip("/") if audio_endpoint else audio_endpoint
        self.credential = credential
        self.default_voice = default_voice
        self._chat_client: ChatCompletionsClient | None = None
        self._vad_threshold = _vad_cfg.get("threshold", 0.5)
        self._vad_silence_ms = _vad_cfg.get("silence_duration_ms", 200)

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
            # Covers an upstream (transcription/chat/TTS) connect failure the same way
            # RTMiddleTier.handle's own finally does -- a no-op if the loop's own exit already
            # detached the session.
            self._sessions.detach_session(ws, self._sessions.get_session_id(ws),
                                          reason=f"handler exit code={ws.close_code}")
        return ws

    async def _run_session(self, ws: web.WebSocketResponse, session_id: str, persona, resolved_model: ResolvedModel) -> None:
        prompt_loader = self.persona_prompt_loaders.get(persona.id)
        state = _CascadeSessionState(
            session_id=session_id,
            persona_id=persona.id,
            deployment=resolved_model.deployment,
            voice=self.default_voice,
        )
        if prompt_loader is not None:
            state.messages.append(SystemMessage(content=prompt_loader.get_system_prompt()))
        detector = _TurnDetector(threshold=self._vad_threshold, silence_duration_ms=self._vad_silence_ms)

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

    async def _handle_client_message(self, ws, session_id, state, detector, data, prompt_loader) -> None:
        msg_type = data.get("type")
        if msg_type == "input_audio_buffer.append":
            try:
                pcm = base64.b64decode(data.get("audio", ""))
            except (ValueError, TypeError):
                return
            event = detector.feed(pcm)
            if event == "speech_started":
                await ws.send_json({"type": "input_audio_buffer.speech_started"})
            elif event == "speech_stopped":
                turn_audio = detector.take_buffer()
                detector.reset()
                await self._process_turn(ws, session_id, state, turn_audio)
        elif msg_type == "input_audio_buffer.clear":
            detector.reset()
        elif msg_type == "extension.set_voice":
            voice = data.get("voice")
            if voice:
                state.voice = voice
                self._sessions.set_voice(session_id, voice)
        # session.update / extension.resume / anything else not listed above: a documented,
        # explicit scope cut for this issue's v1 (see the decision note) -- no-op rather than an
        # error, so an unrecognized/unused message never disrupts the session.

    async def _process_turn(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState, turn_audio: bytes) -> None:
        if not turn_audio:
            return
        try:
            transcript = await self._transcribe(turn_audio)
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

        response_id = new_middle_tier_item_id()
        await ws.send_json({"type": "response.created", "response": {"id": response_id}})

        final_text = await self._run_chat_tool_loop(ws, session_id, state)

        if final_text:
            await ws.send_json({"type": "response.audio_transcript.delta", "delta": final_text})
            try:
                await self._speak(ws, final_text, state.voice)
            except Exception:
                logger.exception("Cascade TTS failed (session=%s)", session_id)

        await ws.send_json({"type": "response.done", "response": {"id": response_id}})

        identifiers = order_state_singleton.advance_round_trip(session_id)
        await self._sessions.emit_session_identifiers(ws, "extension.round_trip_token", identifiers)

    async def _run_chat_tool_loop(self, ws: web.WebSocketResponse, session_id: str, state: _CascadeSessionState) -> str:
        client = await self._get_chat_client()
        tool_defs = _tool_definitions(self.tools)
        for _round in range(self._MAX_TOOL_ROUNDS):
            completion = await client.complete(
                messages=state.messages,
                model=state.deployment,
                tools=tool_defs or None,
                **cascade_chat_kwargs(),
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
