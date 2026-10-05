"""Unit tests for the cascade PipelineProcessor (issue #82, P2-13).

Covers the pieces that can be proven fast and deterministically without a live network call to
a Foundry/Azure OpenAI endpoint (those integration paths -- STT/chat/TTS over the wire -- are
covered by the C# conformance suite's fake upstreams, per the task's own acceptance criteria):
  - `_TurnDetector`: the local RMS-energy VAD that segments guest turns (there is no upstream
    Realtime API doing this for the cascade pipeline -- see the module docstring).
  - `_pcm16_to_wav_bytes`: the WAV-container packaging fed to the transcription endpoint.
  - `_tool_definitions`: the flat Realtime-style tool schema -> nested Chat-Completions-style
    `ChatCompletionsToolDefinition` conversion.
  - `CascadeProcessor._execute_tool_call`: THE tool-calling wire-protocol contract this issue
    requires be "the SAME" as the realtime pipeline's (rtmt.py's `response.output_item.done`
    case) -- same `ToolResultDirection`-gated send-to-client logic, same
    `extension.middle_tier_tool_response` shape, same best-effort ticket-refresh-on-exception
    fallback. `test_to_server_only_result_is_not_sent_to_client` is the mutation-test proof
    ("cascade tool result dropped -> row fails" from the task, expressed as a fast unit test:
    reverting the `send_to_client` gate to always-True or dropping the
    `extension.middle_tier_tool_response` send makes the corresponding assertions below fail).
  - `CascadeProcessor._run_chat_tool_loop`: the multi-round tool-call loop and its round cap.
  - `CascadeProcessor.resolve_model`: delegates to `resolve_cascade_model` (already unit-tested
    in tests/test_processors.py) -- just a thin proof the processor wires it up.
  - Rick's PR #118 review item 5 (cascade turn-taking parity): `_send_greeting` (greeting on
    connect), `_cancel_current_turn`/the `_handle_client_message` `speech_started` branch
    (barge-in cancels the in-flight chat completion and TTS -- `test_new_speech_started_
    cancels_an_in_flight_turn_before_it_speaks` is the mutation-test proof: reverting
    `_start_turn` to being awaited inline, or dropping the `_cancel_current_turn` call from the
    `speech_started` branch, makes that turn run to completion and `_speak` gets called anyway),
    and `_with_rate_limit_retry` (a 429 from chat/STT/TTS goes through the same
    `extension.rate_limited` notice path as the realtime pipeline's own `RateLimitRecovery` --
    `test_exhausted_retries_send_the_final_notice_and_raise` is the mutation-test proof: a 429
    that bypasses the notice path, or stops raising so the turn silently continues, makes those
    assertions fail).
"""

import asyncio
import base64
import io
import json
import sys
import unittest
import wave
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from azure.ai.inference.models import UserMessage
from azure.core.exceptions import HttpResponseError

import session_manager
from cascade_processor import (
    _AUDIO_SAMPLE_RATE,
    CascadeProcessor,
    CascadeRateLimitExhausted,
    _CascadeSessionState,
    _pcm16_to_wav_bytes,
    _resolve_persona_voice,
    _tool_definitions,
    _TurnDetector,
)
from rate_limit import RATE_LIMITED_EVENT, RateLimitSettings
from rtmt import _DEFAULT_ALLOWED_VOICES, Tool, ToolResult, ToolResultDirection


def _make_mock_ws():
    ws = MagicMock(spec=web.WebSocketResponse)
    ws.closed = False
    ws.send_json = AsyncMock()
    return ws


def _sent_types(ws) -> list[str]:
    return [call.args[0]["type"] for call in ws.send_json.await_args_list]


def _http_429(retry_after=None, message="Too Many Requests"):
    """A fake azure-core `HttpResponseError` shaped like a real 429 -- `status_code` and (when
    given) a `Retry-After`-bearing `headers` mapping, exactly what `_http_status_of`/
    `_retry_hint_of` (cascade_processor.py) read off a genuine one."""
    err = HttpResponseError(message=message)
    err.status_code = 429
    if retry_after is not None:
        err.headers = {"Retry-After": str(retry_after)}
    return err


def _silence(num_samples: int) -> bytes:
    return (b"\x00\x00") * num_samples


def _loud_tone(num_samples: int, amplitude: int = 20000) -> bytes:
    # A simple full-scale-ish alternating +amplitude/-amplitude signal -- well above any
    # sane 0..1-normalized threshold cutoff, and trivial to reason about RMS-wise (RMS == amplitude).
    import struct
    frames = [amplitude if i % 2 == 0 else -amplitude for i in range(num_samples)]
    return struct.pack(f"<{num_samples}h", *frames)



# ═══════════════════════════════════════════════════════════════════════════════
# _TurnDetector (local VAD)
# ═══════════════════════════════════════════════════════════════════════════════

class TurnDetectorTests(unittest.TestCase):
    def test_silence_never_triggers_speech_started(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        event = detector.feed(_silence(2400))
        self.assertIsNone(event)
        self.assertFalse(detector.is_speaking)

    def test_loud_audio_triggers_speech_started_once(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        first = detector.feed(_loud_tone(2400))
        second = detector.feed(_loud_tone(2400))
        self.assertEqual(first, "speech_started")
        self.assertIsNone(second)  # already speaking -- no repeat event
        self.assertTrue(detector.is_speaking)

    def test_trailing_silence_triggers_speech_stopped_after_the_configured_duration(self):
        # 200ms of silence at 24kHz = 4800 samples.
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.feed(_loud_tone(2400))
        not_yet = detector.feed(_silence(2000))
        self.assertIsNone(not_yet)  # not enough trailing silence yet
        stopped = detector.feed(_silence(3000))  # 2000 + 3000 = 5000 >= 4800
        self.assertEqual(stopped, "speech_stopped")

    def test_reset_clears_speaking_state_and_buffer(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.feed(_loud_tone(2400))
        detector.reset()
        self.assertFalse(detector.is_speaking)
        self.assertEqual(detector.take_buffer(), b"")

    def test_take_buffer_returns_and_clears_accumulated_audio(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        chunk = _loud_tone(100)
        detector.feed(chunk)
        buffered = detector.take_buffer()
        self.assertEqual(buffered, chunk)
        self.assertEqual(detector.take_buffer(), b"")  # already drained


# ═══════════════════════════════════════════════════════════════════════════════
# _pcm16_to_wav_bytes
# ═══════════════════════════════════════════════════════════════════════════════

class Pcm16ToWavBytesTests(unittest.TestCase):
    def test_wav_container_round_trips_the_same_pcm_bytes(self):
        pcm = _loud_tone(480)
        wav_bytes = _pcm16_to_wav_bytes(pcm, sample_rate=_AUDIO_SAMPLE_RATE)
        with wave.open(io.BytesIO(wav_bytes), "rb") as wav_file:
            self.assertEqual(wav_file.getnchannels(), 1)
            self.assertEqual(wav_file.getsampwidth(), 2)
            self.assertEqual(wav_file.getframerate(), _AUDIO_SAMPLE_RATE)
            self.assertEqual(wav_file.readframes(wav_file.getnframes()), pcm)


# ═══════════════════════════════════════════════════════════════════════════════
# _tool_definitions (flat Realtime-style schema -> nested Chat-Completions-style)
# ═══════════════════════════════════════════════════════════════════════════════

class ToolDefinitionsTests(unittest.TestCase):
    def test_converts_flat_schema_into_chat_completions_tool_definition(self):
        schema = {
            "type": "function",
            "name": "get_order",
            "description": "Get the current order",
            "parameters": {"type": "object", "properties": {}, "required": []},
        }
        definitions = _tool_definitions([schema])
        self.assertEqual(len(definitions), 1)
        as_dict = definitions[0].as_dict()
        self.assertEqual(as_dict["function"]["name"], "get_order")
        self.assertEqual(as_dict["function"]["description"], "Get the current order")
        self.assertEqual(as_dict["function"]["parameters"], schema["parameters"])


# ═══════════════════════════════════════════════════════════════════════════════
# CascadeProcessor._execute_tool_call -- THE tool-calling wire-protocol contract
# ═══════════════════════════════════════════════════════════════════════════════

def _make_processor(tools: dict, persona_tool_schemas: dict | None = None) -> CascadeProcessor:
    sessions = MagicMock()
    sessions.get_context_monitor.return_value = None
    sessions.emit_session_identifiers = AsyncMock()
    return CascadeProcessor(
        tools=tools,
        sessions=sessions,
        persona_catalog=MagicMock(),
        persona_prompt_loaders={},
        persona_tool_schemas=persona_tool_schemas if persona_tool_schemas is not None else {},
        model_catalog=MagicMock(),
        foundry_endpoint="https://fake.services.ai.azure.com",
        audio_endpoint="https://fake.openai.azure.com",
        credential=MagicMock(),
    )


def _fake_tool_call(name: str, arguments: str = "{}", call_id: str = "call_1"):
    return SimpleNamespace(id=call_id, function=SimpleNamespace(name=name, arguments=arguments))


class ExecuteToolCallTests(unittest.IsolatedAsyncioTestCase):
    async def test_to_server_only_result_is_not_sent_to_client(self):
        """A TO_SERVER-only tool result (e.g. `search`) must reach the model (appended to
        `state.messages`) but must NEVER be sent to the client -- this is the mutation-test
        seam: flipping the `send_to_client` gate (or unconditionally emitting
        extension.middle_tier_tool_response) makes this assertion fail."""
        target = AsyncMock(return_value=ToolResult("search results here", ToolResultDirection.TO_SERVER))
        tools = {"search": Tool(target=target, schema={"name": "search"})}
        processor = _make_processor(tools)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("search"), "prev-item-1")

        ws.send_json.assert_not_called()
        self.assertEqual(len(state.messages), 1)
        self.assertEqual(state.messages[0].content, "search results here")
        self.assertEqual(state.messages[0].tool_call_id, "call_1")

    async def test_to_client_result_is_sent_with_the_exact_wire_shape(self):
        """The realtime pipeline's contract, verbatim: {previous_item_id, tool_name,
        tool_result} -- the ONE thing Unity's frontend (#110) actually reads off this event."""
        target = AsyncMock(return_value=ToolResult("server text", ToolResultDirection.TO_BOTH, client_text='{"order":[]}'))
        tools = {"update_order": Tool(target=target, schema={"name": "update_order"})}
        processor = _make_processor(tools)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("update_order", call_id="call_42"), "prev-item-7")

        ws.send_json.assert_called_once_with({
            "type": "extension.middle_tier_tool_response",
            "previous_item_id": "prev-item-7",
            "tool_name": "update_order",
            "tool_result": '{"order":[]}',
        })
        target.assert_awaited_once_with({}, "s1")  # update_order gets session_id (per rtmt.py's contract)
        self.assertEqual(state.messages[0].content, "server text")

    async def test_unhandled_tool_exception_sends_neutral_output_and_best_effort_ticket(self):
        target = AsyncMock(side_effect=RuntimeError("boom"))
        tools = {"update_order": Tool(target=target, schema={"name": "update_order"})}
        processor = _make_processor(tools)
        with unittest.mock.patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_order_summary_json.return_value = '{"order":["fries"]}'
            ws = _make_mock_ws()
            state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

            await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("update_order"), "prev-item-9")

        ws.send_json.assert_called_once_with({
            "type": "extension.middle_tier_tool_response",
            "previous_item_id": "prev-item-9",
            "tool_name": "get_order",  # NOT "update_order" -- the best-effort fallback is always get_order
            "tool_result": '{"order":["fries"]}',
        })
        self.assertIn("Something went wrong", state.messages[0].content)

    async def test_unknown_tool_name_is_a_noop_with_an_empty_tool_message(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("totally_unknown"), "prev-item-1")

        ws.send_json.assert_not_called()
        self.assertEqual(state.messages[0].content, "")


# ═══════════════════════════════════════════════════════════════════════════════
# CascadeProcessor._run_chat_tool_loop
# ═══════════════════════════════════════════════════════════════════════════════

def _completion_with_tool_call(name: str, call_id: str = "call_1"):
    message = SimpleNamespace(content=None, tool_calls=[_fake_tool_call(name, call_id=call_id)])
    return SimpleNamespace(choices=[SimpleNamespace(message=message)])


def _completion_with_final_text(text: str):
    message = SimpleNamespace(content=text, tool_calls=[])
    return SimpleNamespace(choices=[SimpleNamespace(message=message)])


class RunChatToolLoopTests(unittest.IsolatedAsyncioTestCase):
    async def test_resolves_after_one_tool_round(self):
        target = AsyncMock(return_value=ToolResult("ok", ToolResultDirection.TO_SERVER))
        tools = {"get_order": Tool(target=target, schema={"name": "get_order"})}
        processor = _make_processor(tools)
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(side_effect=[
            _completion_with_tool_call("get_order"),
            _completion_with_final_text("Your order is ready."),
        ])
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        final_text = await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(final_text, "Your order is ready.")
        self.assertEqual(fake_client.complete.await_count, 2)

    async def test_hits_the_round_cap_and_returns_empty_string_instead_of_looping_forever(self):
        """The mutation-test-provable safety seam: if a model (or a broken fake upstream) keeps
        calling tools forever, this processor must still terminate the turn deterministically."""
        target = AsyncMock(return_value=ToolResult("ok", ToolResultDirection.TO_SERVER))
        tools = {"get_order": Tool(target=target, schema={"name": "get_order"})}
        processor = _make_processor(tools)
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(return_value=_completion_with_tool_call("get_order"))
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        final_text = await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(final_text, "")
        self.assertEqual(fake_client.complete.await_count, CascadeProcessor._MAX_TOOL_ROUNDS)


class RunChatToolLoopPersonaToolSchemasTests(unittest.IsolatedAsyncioTestCase):
    """Issue #170 R4 (Rick's PR #175 round-2 review, required item 3): the cascade
    pipeline's own tool definitions must come from THIS session's bound persona
    (`CascadeProcessor.persona_tool_schemas[state.persona_id]`), never unconditionally
    from `self.tools` (the deployment default's own schemas) -- the same class of bug
    #170 fixed for the realtime pipeline's `_build_session`, recurring here because
    `_tool_definitions` used to be called with `self.tools` directly. Mutation-test
    proof: reverting `_run_chat_tool_loop` to call `_tool_definitions(self.tools)`
    (ignoring `state.persona_id`) makes `test_passes_the_bound_personas_tool_
    descriptions_to_the_chat_client` fail (the sent definition would carry the
    default's description instead of the bound persona's own)."""

    async def test_passes_the_bound_personas_tool_descriptions_to_the_chat_client(self):
        default_schema = {
            "type": "function", "name": "search",
            "description": "Search the DEFAULT PERSONA's menu.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        }
        bound_schema = {
            "type": "function", "name": "search",
            "description": "Search the BOUND PERSONA's own menu.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        }
        tools = {"search": Tool(target=AsyncMock(), schema=default_schema)}
        processor = _make_processor(tools, persona_tool_schemas={"bound-persona": [bound_schema]})
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(return_value=_completion_with_final_text("All done."))
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="bound-persona", deployment="d", voice="marin")

        await processor._run_chat_tool_loop(ws, "s1", state)

        sent_tools = fake_client.complete.call_args.kwargs["tools"]
        descriptions = [t.as_dict()["function"]["description"] for t in sent_tools]
        self.assertIn("Search the BOUND PERSONA's own menu.", descriptions)
        self.assertNotIn("Search the DEFAULT PERSONA's menu.", descriptions)

    async def test_falls_back_to_the_default_schemas_with_no_persona_tool_schemas_entry(self):
        """A persona id missing from `persona_tool_schemas` (shouldn't happen for a real
        persona, but must not crash the turn) falls back to `self.tools`' own schemas,
        same fallback shape as `_forward_messages` (rtmt.py)."""
        default_schema = {
            "type": "function", "name": "search",
            "description": "Search the DEFAULT PERSONA's menu.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        }
        tools = {"search": Tool(target=AsyncMock(), schema=default_schema)}
        processor = _make_processor(tools, persona_tool_schemas={})
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(return_value=_completion_with_final_text("All done."))
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="no-entry-persona", deployment="d", voice="marin")

        with self.assertLogs(level="WARNING"):
            await processor._run_chat_tool_loop(ws, "s1", state)

        sent_tools = fake_client.complete.call_args.kwargs["tools"]
        descriptions = [t.as_dict()["function"]["description"] for t in sent_tools]
        self.assertIn("Search the DEFAULT PERSONA's menu.", descriptions)


# ═══════════════════════════════════════════════════════════════════════════════
# CascadeProcessor.resolve_model
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class _FakePipelineCfg:
    default: str
    allowed: list


class ResolveModelDelegationTests(unittest.TestCase):
    def test_resolve_model_delegates_to_resolve_cascade_model(self):
        from model_catalog import ModelCatalog

        persona = SimpleNamespace(
            id="test-persona",
            manifest=SimpleNamespace(models=SimpleNamespace(cascade=_FakePipelineCfg(default="gpt-5-mini", allowed=["gpt-5-mini"]))),
        )
        catalog = ModelCatalog.load(
            config={"models": {"catalog": [{"id": "gpt-5-mini", "pipeline": "cascade", "label": "GPT-5 mini", "toolCalling": True}]}},
            environ={"AZURE_AI_MODEL_DEPLOYMENTS": '{"gpt-5-mini": "gpt-5-mini-prod"}'},
        )
        processor = CascadeProcessor(
            tools={}, sessions=MagicMock(), persona_catalog=MagicMock(), persona_prompt_loaders={},
            persona_tool_schemas={},
            model_catalog=catalog, foundry_endpoint="https://fake", audio_endpoint="https://fake",
            credential=MagicMock(),
        )
        resolved = processor.resolve_model(persona, None)
        self.assertEqual(resolved.id, "gpt-5-mini")
        self.assertEqual(resolved.pipeline, "cascade")
        self.assertEqual(resolved.deployment, "gpt-5-mini-prod")


# ═══════════════════════════════════════════════════════════════════════════════
# Rick's #118 review item 5: greeting on connect
# ═══════════════════════════════════════════════════════════════════════════════

def _greeting_payload(text: str) -> dict:
    return {
        "type": "conversation.item.create",
        "item": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": text}]},
    }


class SendGreetingTests(unittest.IsolatedAsyncioTestCase):
    async def test_greeting_feeds_the_instruction_text_and_speaks_the_final_answer(self):
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(return_value="Welcome to the drive-thru! What can I get started for you today?")
        processor._speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")
        prompt_loader = MagicMock()
        prompt_loader.get_greeting.return_value = _greeting_payload(
            "Say EXACTLY this greeting and NOTHING else: Welcome to the drive-thru! "
            "What can I get started for you today?"
        )

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.advance_round_trip.return_value = SimpleNamespace()
            await processor._send_greeting(ws, "s1", state, prompt_loader)

        self.assertEqual(len(state.messages), 1)
        self.assertIn("Welcome to the drive-thru!", state.messages[0].content)
        processor._speak.assert_awaited_once_with(
            ws, "Welcome to the drive-thru! What can I get started for you today?", state
        )
        self.assertEqual(_sent_types(ws), ["response.created", "response.audio_transcript.delta", "response.done"])
        processor._sessions.mark_greeting_sent.assert_called_once_with("s1")

    async def test_greeting_is_a_noop_without_a_bound_prompt_loader(self):
        """A persona with no PromptLoader bound (shouldn't happen for a real persona, but this
        processor must not crash the connection over it) skips the greeting silently."""
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")

        await processor._send_greeting(ws, "s1", state, None)

        ws.send_json.assert_not_called()
        self.assertEqual(state.messages, [])

    async def test_greeting_is_skipped_when_greeting_yaml_has_an_unexpected_shape(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="test-persona", deployment="d", voice="marin")
        prompt_loader = MagicMock()
        prompt_loader.get_greeting.return_value = {"item": {"content": []}}  # missing [0]["text"]

        await processor._send_greeting(ws, "s1", state, prompt_loader)

        ws.send_json.assert_not_called()
        self.assertEqual(state.messages, [])


# ═══════════════════════════════════════════════════════════════════════════════
# Rick's #118 review item 5: barge-in cancels the in-flight chat completion and TTS
# ═══════════════════════════════════════════════════════════════════════════════

class CancelCurrentTurnTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancels_an_in_flight_task_and_clears_it(self):
        processor = _make_processor({})
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        started = asyncio.Event()

        async def _never_finishes():
            started.set()
            await asyncio.Event().wait()

        task = asyncio.ensure_future(_never_finishes())
        await started.wait()
        state.current_turn_task = task

        await processor._cancel_current_turn(state, "test barge-in")

        self.assertTrue(task.cancelled())
        self.assertIsNone(state.current_turn_task)

    async def test_is_a_noop_when_no_turn_is_in_flight(self):
        processor = _make_processor({})
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        await processor._cancel_current_turn(state, "nothing running")

        self.assertIsNone(state.current_turn_task)

    async def test_is_a_noop_when_the_turn_already_finished(self):
        processor = _make_processor({})
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        async def _quick():
            return None

        task = asyncio.ensure_future(_quick())
        await task
        state.current_turn_task = task

        await processor._cancel_current_turn(state, "already done")

        self.assertIsNone(state.current_turn_task)


class BargeInEndToEndTests(unittest.IsolatedAsyncioTestCase):
    async def test_new_speech_started_cancels_an_in_flight_turn_before_it_speaks(self):
        """The mutation-test seam for Rick's #118 review item 5 (barge-in): if
        `_handle_client_message`'s `speech_started` branch stops calling `_cancel_current_turn`
        (or `_start_turn` goes back to being awaited inline instead of spawned as a background
        task), the slow turn below runs to completion once the guest's second utterance is fed
        in and `_speak` gets called anyway -- these assertions are what would then fail."""
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)

        transcribe_started = asyncio.Event()
        release_transcribe = asyncio.Event()

        async def _slow_transcribe(pcm16_bytes):
            transcribe_started.set()
            await release_transcribe.wait()
            return "should never get this far"

        processor._transcribe = _slow_transcribe
        processor._speak = AsyncMock()

        # Kick off a turn exactly the way _handle_client_message's speech_stopped branch does.
        processor._start_turn(ws, "s1", state, _loud_tone(2400))
        await asyncio.wait_for(transcribe_started.wait(), timeout=2)
        self.assertIsNotNone(state.current_turn_task)

        # A NEW speech_started arrives mid-turn (barge-in) -- must cancel the in-flight task
        # before it can ever reach chat completion or TTS.
        append_msg = {
            "type": "input_audio_buffer.append",
            "audio": base64.b64encode(_loud_tone(2400)).decode("ascii"),
        }
        await processor._handle_client_message(ws, "s1", state, detector, append_msg, None)

        self.assertIsNone(state.current_turn_task)
        processor._speak.assert_not_awaited()
        release_transcribe.set()  # let the (already-cancelled) coroutine's own await resolve harmlessly


# ═══════════════════════════════════════════════════════════════════════════════
# Rick's PR #253 review item 1: `extension.set_voice` must go through the SAME
# `_sanitize_voice` gate realtime's own handler (rtmt.py) already applies -- only a value
# present in `self.allowed_voices` may ever be adopted into `state.voice`/the session store.
# Before this fix, `_handle_client_message` accepted ANY truthy value (an unrecognized
# string, a dict, an int, ...) straight through, silently breaking TTS on the next turn
# instead of being dropped with a warning like realtime does. Mutation-test seam: reverting
# the handler back to `if voice: state.voice = voice` makes
# `test_an_unknown_voice_string_is_dropped_not_adopted` and
# `test_a_non_string_voice_is_dropped_not_adopted` fail (state.voice/set_voice would then
# reflect the bad value), while `test_a_valid_allowed_voice_is_adopted` keeps passing either
# way -- proving the gate rejects bad input without breaking the legitimate case.
# ═══════════════════════════════════════════════════════════════════════════════

class HandleClientMessageSetVoiceTests(unittest.IsolatedAsyncioTestCase):
    async def test_an_unknown_voice_string_is_dropped_not_adopted(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)

        await processor._handle_client_message(
            ws, "s1", state, detector, {"type": "extension.set_voice", "voice": "not-a-real-voice"}, None)

        self.assertEqual(state.voice, "marin")
        processor._sessions.set_voice.assert_not_called()

    async def test_a_non_string_voice_is_dropped_not_adopted(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)

        for bad_voice in ({"x": 1}, 42, True, ["coral"]):
            with self.subTest(bad_voice=bad_voice):
                await processor._handle_client_message(
                    ws, "s1", state, detector, {"type": "extension.set_voice", "voice": bad_voice}, None)

                self.assertEqual(state.voice, "marin")
        processor._sessions.set_voice.assert_not_called()

    async def test_a_valid_allowed_voice_is_adopted(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)

        await processor._handle_client_message(
            ws, "s1", state, detector, {"type": "extension.set_voice", "voice": "coral"}, None)

        self.assertEqual(state.voice, "coral")
        processor._sessions.set_voice.assert_called_once_with("s1", "coral")


# ═══════════════════════════════════════════════════════════════════════════════
# Rick's #118 review item 5: a 429 from chat/STT/TTS goes through the same
# extension.rate_limited notice path as the realtime pipeline.
# ═══════════════════════════════════════════════════════════════════════════════

class WithRateLimitRetryTests(unittest.IsolatedAsyncioTestCase):
    def _settings(self, **overrides) -> RateLimitSettings:
        defaults = dict(enabled=True, retry_delay_seconds=1.5, second_retry_delay_seconds=4.0, max_retries=2)
        defaults.update(overrides)
        return RateLimitSettings(**defaults)

    async def test_first_failure_retries_silently_without_notifying_the_client(self):
        processor = _make_processor({})
        processor._rate_limit_settings = self._settings()
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=[_http_429(), "ok"])

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock) as mock_sleep:
            result = await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        self.assertEqual(result, "ok")
        ws.send_json.assert_not_called()
        mock_sleep.assert_awaited_once_with(1.5)

    async def test_second_failure_notifies_the_client_with_attempt_one(self):
        processor = _make_processor({})
        processor._rate_limit_settings = self._settings()
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=[_http_429(), _http_429(), "ok"])

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock):
            result = await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        self.assertEqual(result, "ok")
        ws.send_json.assert_called_once_with({"type": RATE_LIMITED_EVENT, "attempt": 1})

    async def test_exhausted_retries_send_the_final_notice_and_raise(self):
        """Mutation-test seam ('a 429 bypasses the rate-limit notice path -> row fails'): if
        this retry ladder stops sending `extension.rate_limited` on exhaustion, or stops raising
        so the caller's turn silently continues, these assertions fail."""
        processor = _make_processor({})
        processor._rate_limit_settings = self._settings()
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=[_http_429(), _http_429(), _http_429()])

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock):
            with self.assertRaises(CascadeRateLimitExhausted):
                await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        self.assertEqual(
            ws.send_json.await_args_list[-1].args[0],
            {"type": RATE_LIMITED_EVENT, "attempt": 2, "final": True},
        )

    async def test_non_429_error_propagates_without_retrying(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=RuntimeError("boom"))

        with self.assertRaises(RuntimeError):
            await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        ws.send_json.assert_not_called()

    async def test_429_propagates_unchanged_when_rate_limit_recovery_is_disabled(self):
        processor = _make_processor({})
        processor._rate_limit_settings = self._settings(enabled=False)
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=_http_429())

        with self.assertRaises(HttpResponseError):
            await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        ws.send_json.assert_not_called()

    async def test_retry_after_header_within_bounds_is_used_verbatim_as_the_delay(self):
        processor = _make_processor({})
        processor._rate_limit_settings = self._settings()
        ws = _make_mock_ws()
        op = AsyncMock(side_effect=[_http_429(retry_after=3), "ok"])

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock) as mock_sleep:
            await processor._with_rate_limit_retry(ws, "s1", op, "chat completion")

        mock_sleep.assert_awaited_once_with(3.0)  # within FIRST_RETRY_BOUNDS (0.5s, 5.0s)


class RunChatToolLoopRateLimitTests(unittest.IsolatedAsyncioTestCase):
    async def test_a_429_from_chat_completion_is_retried_then_resolves_normally(self):
        processor = _make_processor({})
        processor._rate_limit_settings = RateLimitSettings(
            enabled=True, retry_delay_seconds=0.01, second_retry_delay_seconds=0.01, max_retries=2
        )
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(side_effect=[_http_429(), _completion_with_final_text("All set.")])
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock):
            final_text = await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(final_text, "All set.")
        ws.send_json.assert_not_called()  # the first failure is a silent retry


# ═══════════════════════════════════════════════════════════════════════════════
# #247: a barge-in (or any failure) mid-tool-round must never leave an orphaned
# assistant `tool_calls` message -- every id must have a matching `ToolMessage`, or the
# WHOLE round must be gone from `state.messages`.
# ═══════════════════════════════════════════════════════════════════════════════

class RunChatToolLoopOrphanedToolCallTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancellation_mid_round_truncates_the_round_from_history(self):
        """Mutation-test seam: drop the `try/except BaseException: del state.messages[...]`
        truncation (or narrow it to `except Exception`, which never catches
        `asyncio.CancelledError`) and this assertion fails -- the assistant `tool_calls`
        message stays in `state.messages` with no matching `ToolMessage` for `call_1`."""
        processor = _make_processor({"get_order": Tool(target=AsyncMock(), schema={"name": "get_order"})})
        processor._execute_tool_call = AsyncMock(side_effect=asyncio.CancelledError())
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(return_value=_completion_with_tool_call("get_order"))
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        state.messages.append(UserMessage(content="one large fries"))
        pre_cancel_message_count = len(state.messages)

        with self.assertRaises(asyncio.CancelledError):
            await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(len(state.messages), pre_cancel_message_count)
        self.assertTrue(all(not hasattr(m, "tool_calls") or not m.tool_calls for m in state.messages))

    async def test_a_second_tool_calls_message_unaffected_by_an_earlier_rounds_cancellation(self):
        """A round that completed normally (every id answered) before a LATER round is
        cancelled must be left untouched -- only the in-flight round's partial state is
        truncated."""
        processor = _make_processor({
            "get_order": Tool(target=AsyncMock(return_value=ToolResult("ok", ToolResultDirection.TO_SERVER)),
                               schema={"name": "get_order"}),
        })
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(side_effect=[
            _completion_with_tool_call("get_order", call_id="call_1"),
            _completion_with_tool_call("get_order", call_id="call_2"),
        ])
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        # The first round executes for real (call_1 gets answered); the second round's tool
        # call is where cancellation strikes.
        real_execute = processor._execute_tool_call
        call_count = 0

        async def _execute_then_cancel_on_second_round(*args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                return await real_execute(*args, **kwargs)
            raise asyncio.CancelledError()

        processor._execute_tool_call = _execute_then_cancel_on_second_round

        with self.assertRaises(asyncio.CancelledError):
            await processor._run_chat_tool_loop(ws, "s1", state)

        # First round's assistant tool_calls message + its matching ToolMessage(call_1) survive.
        self.assertEqual(len(state.messages), 2)
        self.assertEqual(state.messages[0].tool_calls[0].id, "call_1")
        self.assertEqual(state.messages[1].tool_call_id, "call_1")


# ═══════════════════════════════════════════════════════════════════════════════
# #262: a non-429 chat-completion or TTS failure after `response.created` must still
# close the turn (`response.done`) for the browser -- never silently hang.
# ═══════════════════════════════════════════════════════════════════════════════

class RunTurnAndSpeakFailureTests(unittest.IsolatedAsyncioTestCase):
    async def test_non_429_chat_completion_failure_still_sends_a_failed_response_done(self):
        """Mutation-test seam: remove the `except Exception:` branch around
        `_run_chat_tool_loop` (or let it silently fall through) and `response.done` is never
        sent -- the browser is stuck on "response in progress" forever."""
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(side_effect=RuntimeError("500 from Foundry"))
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        await processor._run_turn_and_speak(ws, "s1", state)

        sent_types = _sent_types(ws)
        self.assertEqual(sent_types, ["response.created", "error", "response.done"])
        done_msg = ws.send_json.await_args_list[-1].args[0]
        self.assertEqual(done_msg["response"]["status"], "failed")
        self.assertEqual(done_msg["response"]["output"], [])

    async def test_non_429_tts_failure_still_sends_a_failed_response_done(self):
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(return_value="Sure thing.")
        processor._speak = AsyncMock(side_effect=RuntimeError("500 from the TTS endpoint"))
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        await processor._run_turn_and_speak(ws, "s1", state)

        sent_types = _sent_types(ws)
        self.assertEqual(
            sent_types,
            ["response.created", "response.audio_transcript.delta", "error", "response.done"],
        )
        done_msg = ws.send_json.await_args_list[-1].args[0]
        self.assertEqual(done_msg["response"]["status"], "failed")

    async def test_barge_in_during_chat_completion_is_not_treated_as_a_failure(self):
        """Cancellation (barge-in) must propagate unchanged -- NOT get rewritten into a
        failed `response.done` -- `_cancel_current_turn` owns that turn's cleanup."""
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(side_effect=asyncio.CancelledError())
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        with self.assertRaises(asyncio.CancelledError):
            await processor._run_turn_and_speak(ws, "s1", state)

        self.assertEqual(_sent_types(ws), ["response.created"])  # no response.done at all


class ProcessTurnRateLimitTests(unittest.IsolatedAsyncioTestCase):
    async def test_exhausted_rate_limit_during_transcription_ends_the_turn_before_any_response(self):
        processor = _make_processor({})
        processor._rate_limit_settings = RateLimitSettings(
            enabled=True, retry_delay_seconds=0.01, second_retry_delay_seconds=0.01, max_retries=1
        )
        processor._transcribe = AsyncMock(side_effect=[_http_429(), _http_429()])
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock):
            await processor._process_turn(ws, "s1", state, _loud_tone(2400))

        sent_types = _sent_types(ws)
        self.assertIn(RATE_LIMITED_EVENT, sent_types)
        self.assertNotIn("response.created", sent_types)  # the turn never got that far

    async def test_exhausted_rate_limit_during_chat_completion_still_sends_response_done(self):
        """Even when the ladder is spent mid-turn, `response.done` must still be sent -- the
        session must not hang waiting for a response that will never arrive."""
        processor = _make_processor({})
        processor._rate_limit_settings = RateLimitSettings(
            enabled=True, retry_delay_seconds=0.01, second_retry_delay_seconds=0.01, max_retries=0
        )
        fake_client = MagicMock()
        fake_client.complete = AsyncMock(side_effect=_http_429())
        processor._get_chat_client = AsyncMock(return_value=fake_client)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        with patch("cascade_processor.asyncio.sleep", new_callable=AsyncMock):
            await processor._run_turn_and_speak(ws, "s1", state)

        self.assertEqual(_sent_types(ws), ["response.created", RATE_LIMITED_EVENT, "response.done"])


# ═══════════════════════════════════════════════════════════════════════════════
# #248: per-persona default voice (issue #165-sibling parity gap vs rtmt.py)
# ═══════════════════════════════════════════════════════════════════════════════

def _fake_persona(voice_default, persona_id: str = "p"):
    return SimpleNamespace(id=persona_id, manifest=SimpleNamespace(voice=SimpleNamespace(default=voice_default)))


class ResolvePersonaVoiceTests(unittest.TestCase):
    """`_resolve_persona_voice` is the exact port of `RTMiddleTier._forward_messages`'s own
    `persona_voice = _sanitize_voice(bound_persona.manifest.voice.default, self.allowed_voices);
    if persona_voice is not None: voice = persona_voice` -- see that function's own doc comment
    for why `CascadeProcessor` never has a "persona is unbound" branch to port."""

    def test_personas_own_voice_is_used_when_it_differs_from_the_deployment_default(self):
        persona = _fake_persona("cedar")
        voice = _resolve_persona_voice(persona, default_voice="marin", allowed_voices=_DEFAULT_ALLOWED_VOICES)
        self.assertEqual(voice, "cedar")

    def test_falls_back_to_the_deployment_default_when_the_personas_voice_is_not_allowed(self):
        """Mutation-test seam: a persona whose configured voice was removed from
        `model.allowed_voices` (or never existed) must not reach TTS with an invalid voice --
        it must fall back to the deployment-wide default exactly like realtime's own
        `_sanitize_voice` callers everywhere else."""
        persona = _fake_persona("not-a-real-voice")
        voice = _resolve_persona_voice(persona, default_voice="marin", allowed_voices=_DEFAULT_ALLOWED_VOICES)
        self.assertEqual(voice, "marin")

    def test_falls_back_to_the_deployment_default_when_the_persona_has_no_voice_configured(self):
        persona = _fake_persona(None)
        voice = _resolve_persona_voice(persona, default_voice="marin", allowed_voices=_DEFAULT_ALLOWED_VOICES)
        self.assertEqual(voice, "marin")

    def test_falls_back_to_the_deployment_default_when_the_personas_voice_matches_it_anyway(self):
        """The common real-pack case today (every shipped persona pack currently declares "marin") --
        proven separately so a regression that always returns the persona's own value (even
        when it happens to equal the default) is indistinguishable from this row alone; the
        "differs" test above is the one that actually proves per-persona lookup is happening."""
        persona = _fake_persona("marin")
        voice = _resolve_persona_voice(persona, default_voice="marin", allowed_voices=_DEFAULT_ALLOWED_VOICES)
        self.assertEqual(voice, "marin")


class RunSessionVoiceTests(unittest.IsolatedAsyncioTestCase):
    """Proves `CascadeProcessor._run_session` actually calls `_resolve_persona_voice` (not just
    that the helper itself is correct) and seeds `_CascadeSessionState.voice` with its result --
    the greeting's `_speak` call (already covered by `SendGreetingTests`) reads `state.voice`, so
    a regression that stops wiring the two together would still pass every other test in this
    file (they all construct `_CascadeSessionState` directly with an explicit `voice=` already)."""

    async def test_run_session_seeds_state_voice_from_the_bound_personas_own_voice(self):
        processor = _make_processor({})
        processor.default_voice = "marin"
        processor.allowed_voices = _DEFAULT_ALLOWED_VOICES
        processor._start_greeting = MagicMock()
        ws = _make_mock_ws()
        ws.__aiter__.return_value = iter([])
        persona = _fake_persona("cedar", persona_id="test-delta")
        resolved_model = SimpleNamespace(deployment="d")

        await processor._run_session(ws, "s1", persona, resolved_model)

        seeded_state = processor._start_greeting.call_args.args[2]
        self.assertEqual(seeded_state.voice, "cedar")


# ═══════════════════════════════════════════════════════════════════════════════
# #126: echo suppression cooldown (_TurnDetector)
# ═══════════════════════════════════════════════════════════════════════════════

class EchoCooldownTests(unittest.TestCase):
    """Issue #126 point 3: a `speech_started` right after the assistant's own TTS starts
    playing must not be mistaken for barge-in. Mutation-test seam: dropping the cooldown check
    in `feed()` (or never calling `start_echo_cooldown` from `_speak`, covered separately below)
    makes `test_loud_audio_during_the_cooldown_window_is_swallowed_not_detected` fail; letting
    the cooldown block detection FOREVER (instead of only until its deadline) makes
    `test_real_barge_in_is_still_detected_once_the_cooldown_window_ends` fail."""

    def test_loud_audio_during_the_cooldown_window_is_swallowed_not_detected(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(1.0, now=0.0)
        event = detector.feed(_loud_tone(2400), now=0.5)
        self.assertIsNone(event)
        self.assertFalse(detector.is_speaking)

    def test_suppressed_audio_is_dropped_and_never_reaches_the_stt_buffer(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(1.0, now=0.0)
        detector.feed(_loud_tone(2400), now=0.5)
        detector.feed(_silence(2400), now=0.9)
        self.assertEqual(detector.take_buffer(), b"")
        self.assertEqual(detector.feed(_loud_tone(2400), now=1.0), "speech_started")
        self.assertEqual(len(detector.take_buffer()), 2400 * 2)

    def test_guest_reply_right_after_playback_plus_tail_is_accepted(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(2.0 + 0.3, now=0.0)
        self.assertEqual(detector.feed(_loud_tone(2400), now=2.5), "speech_started")

    def test_echo_tail_is_capped_at_300ms_and_zero_is_passed_through_as_disabled(self):
        from cascade_processor import _echo_tail_seconds
        self.assertEqual(_echo_tail_seconds(1.5), 0.3)
        self.assertEqual(_echo_tail_seconds(0.1), 0.1)
        self.assertEqual(_echo_tail_seconds(0), 0.0)

    def test_real_barge_in_is_still_detected_once_the_cooldown_window_ends(self):
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(1.0, now=0.0)
        event = detector.feed(_loud_tone(2400), now=1.5)
        self.assertEqual(event, "speech_started")

    def test_start_echo_cooldown_never_shrinks_an_already_armed_longer_window(self):
        """A second (shorter) `start_echo_cooldown` call must never regress an existing, still
        in-effect cooldown -- `max()`, not overwrite."""
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(2.0, now=0.0)
        detector.start_echo_cooldown(0.1, now=0.0)
        event = detector.feed(_loud_tone(2400), now=1.0)
        self.assertIsNone(event)

    def test_feed_without_a_now_argument_never_applies_a_cooldown(self):
        """Existing callers that never pass `now` (none left in production code, but this
        documents the contract) must behave exactly like before this issue -- no cooldown ever
        suppresses detection."""
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        detector.start_echo_cooldown(10.0, now=0.0)
        event = detector.feed(_loud_tone(2400))
        self.assertEqual(event, "speech_started")


class SpeakArmsEchoCooldownTests(unittest.IsolatedAsyncioTestCase):
    """Proves `CascadeProcessor._speak` actually arms `state.detector`'s echo cooldown for
    roughly the real playback duration of the synthesized audio (not this method's own fast
    send loop) plus `_ECHO_COOLDOWN_SECONDS` -- the other half of the #126 mutation-test seam
    above: a `_speak` that stops calling `start_echo_cooldown` would make every
    `EchoCooldownTests` row irrelevant in production even though they'd still pass in isolation."""

    async def test_speak_arms_the_cooldown_for_the_audio_duration_plus_the_configured_buffer(self):
        processor = _make_processor({})
        processor.model_catalog.deployment_for.return_value = "tts-deploy"
        processor._bearer_token = AsyncMock(return_value="tok")
        audio_bytes = b"\x00\x00" * _AUDIO_SAMPLE_RATE  # exactly 1.0s of 24kHz mono PCM16.

        fake_resp = MagicMock()
        fake_resp.raise_for_status = MagicMock()
        fake_resp.read = AsyncMock(return_value=audio_bytes)
        fake_post_cm = MagicMock()
        fake_post_cm.__aenter__ = AsyncMock(return_value=fake_resp)
        fake_post_cm.__aexit__ = AsyncMock(return_value=False)
        fake_http = MagicMock()
        fake_http.post = MagicMock(return_value=fake_post_cm)
        fake_session_cm = MagicMock()
        fake_session_cm.__aenter__ = AsyncMock(return_value=fake_http)
        fake_session_cm.__aexit__ = AsyncMock(return_value=False)

        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        state.detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)

        with patch("cascade_processor.aiohttp.ClientSession", return_value=fake_session_cm), \
             patch("cascade_processor._ECHO_COOLDOWN_SECONDS", 0.3):
            with patch.object(state.detector, "start_echo_cooldown") as mock_start:
                await processor._speak(ws, "hello", state)

        mock_start.assert_called_once()
        (duration_seconds,), _ = mock_start.call_args
        self.assertAlmostEqual(duration_seconds, 1.0 + 0.3)

    async def test_speak_with_zero_configured_cooldown_never_arms_suppression(self):
        processor = _make_processor({})
        processor.model_catalog.deployment_for.return_value = "tts-deploy"
        processor._bearer_token = AsyncMock(return_value="tok")
        fake_resp = MagicMock()
        fake_resp.raise_for_status = MagicMock()
        fake_resp.read = AsyncMock(return_value=b"\x00\x00" * _AUDIO_SAMPLE_RATE)
        fake_post_cm = MagicMock()
        fake_post_cm.__aenter__ = AsyncMock(return_value=fake_resp)
        fake_post_cm.__aexit__ = AsyncMock(return_value=False)
        fake_http = MagicMock()
        fake_http.post = MagicMock(return_value=fake_post_cm)
        fake_session_cm = MagicMock()
        fake_session_cm.__aenter__ = AsyncMock(return_value=fake_http)
        fake_session_cm.__aexit__ = AsyncMock(return_value=False)
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        state.detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        with patch("cascade_processor.aiohttp.ClientSession", return_value=fake_session_cm), \
             patch("cascade_processor._ECHO_COOLDOWN_SECONDS", 0.0):
            with patch.object(state.detector, "start_echo_cooldown") as mock_start:
                await processor._speak(ws, "hello", state)
        mock_start.assert_not_called()

    async def test_speak_is_a_noop_on_the_cooldown_when_state_has_no_detector(self):
        """`_run_turn_and_speak` is also reachable from a code path with no detector attached
        (defensive -- never happens for a real connection); `_speak` must not crash."""
        processor = _make_processor({})
        processor.model_catalog.deployment_for.return_value = "tts-deploy"
        processor._bearer_token = AsyncMock(return_value="tok")
        fake_resp = MagicMock()
        fake_resp.raise_for_status = MagicMock()
        fake_resp.read = AsyncMock(return_value=b"\x00\x00")
        fake_post_cm = MagicMock()
        fake_post_cm.__aenter__ = AsyncMock(return_value=fake_resp)
        fake_post_cm.__aexit__ = AsyncMock(return_value=False)
        fake_http = MagicMock()
        fake_http.post = MagicMock(return_value=fake_post_cm)
        fake_session_cm = MagicMock()
        fake_session_cm.__aenter__ = AsyncMock(return_value=fake_http)
        fake_session_cm.__aexit__ = AsyncMock(return_value=False)

        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        self.assertIsNone(state.detector)

        with patch("cascade_processor.aiohttp.ClientSession", return_value=fake_session_cm):
            await processor._speak(ws, "hello", state)  # must not raise


# ═══════════════════════════════════════════════════════════════════════════════
# #126: resume/rehydration bookkeeping -- `record_turn`/`mark_greeting_sent` calls that make
# `SessionManager.recent_turns()`/`ResumeOutcome.conversation_started` meaningful for cascade
# ═══════════════════════════════════════════════════════════════════════════════

class RecordTurnTests(unittest.IsolatedAsyncioTestCase):
    """Before #126, cascade never called `record_turn` or `mark_greeting_sent` at all -- a
    resumed cascade session's rehydration would always be empty, and `conversation_started`
    would always read False (always re-greeting instead of rehydrating silently). Mutation-test
    seam: dropping either call below makes its own assertion fail while every other test in this
    file still passes (none of them assert on `self._sessions.record_turn`/`mark_greeting_sent`
    already)."""

    async def test_process_turn_records_the_guest_turn(self):
        processor = _make_processor({})
        processor._with_rate_limit_retry = AsyncMock(return_value="a burger please")
        processor._run_turn_and_speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        await processor._process_turn(ws, "s1", state, _loud_tone(100))

        processor._sessions.record_turn.assert_called_once_with("s1", "guest", "a burger please")

    async def test_run_turn_and_speak_records_the_assistant_turn(self):
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(return_value="Sure thing!")
        processor._speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.advance_round_trip.return_value = SimpleNamespace()
            await processor._run_turn_and_speak(ws, "s1", state)

        processor._sessions.record_turn.assert_called_once_with("s1", "assistant", "Sure thing!")

    async def test_send_greeting_marks_the_greeting_sent_before_speaking(self):
        processor = _make_processor({})
        processor._run_chat_tool_loop = AsyncMock(return_value="Welcome!")
        processor._speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        prompt_loader = MagicMock()
        prompt_loader.get_greeting.return_value = _greeting_payload("Say EXACTLY this: hi")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.advance_round_trip.return_value = SimpleNamespace()
            await processor._send_greeting(ws, "s1", state, prompt_loader)

        processor._sessions.mark_greeting_sent.assert_called_once_with("s1")


# ═══════════════════════════════════════════════════════════════════════════════
# #126: cascade's own resume handshake (`_negotiate_session`) -- SAME `SessionManager.resume`/
# grace-held order state/4002-supersede semantics the realtime pipeline already uses
# ═══════════════════════════════════════════════════════════════════════════════

def _fake_resume_first_frame(resume_id="abc123"):
    return SimpleNamespace(type=web.WSMsgType.TEXT, data=json.dumps({"type": "extension.resume", "resume_id": resume_id}))


class NegotiateSessionTests(unittest.IsolatedAsyncioTestCase):
    async def test_no_first_frame_within_the_timeout_creates_a_fresh_session_with_a_resume_id(self):
        """The actual root-cause bug fix: before #126, `handle()` never passed `extra=` at all,
        so a cascade session's `extension.session_metadata` never carried a `resumeId`, making
        every cascade session permanently unresumable regardless of `SessionManager`'s own
        already-complete resume support."""
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 0.01
        processor._sessions.create_session.return_value = "new-session-1"
        processor._sessions.issue_resume_id.return_value = "resume-xyz"
        ws = _make_mock_ws()
        ws.receive = AsyncMock(side_effect=asyncio.TimeoutError())
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=0, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            session_id, resumed_state, leftover = await processor._negotiate_session(ws, persona, resolved_model, None)

        self.assertEqual(session_id, "new-session-1")
        self.assertIsNone(resumed_state)
        self.assertIsNone(leftover)
        processor._sessions.create_session.assert_called_once()
        processor._sessions.emit_session_identifiers.assert_awaited_once()
        call_kwargs = processor._sessions.emit_session_identifiers.await_args.kwargs
        self.assertEqual(call_kwargs["extra"], {"resumeId": "resume-xyz"})

    async def test_a_non_resume_first_frame_is_replayed_as_leftover_not_dropped(self):
        """A real first frame (e.g. `extension.set_voice`, sent before any resume id exists)
        peeked while checking for a resume attempt must never be silently dropped."""
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 2.0
        processor._sessions.create_session.return_value = "new-session-1"
        processor._sessions.issue_resume_id.return_value = None
        ws = _make_mock_ws()
        set_voice_frame = SimpleNamespace(type=web.WSMsgType.TEXT, data=json.dumps({"type": "extension.set_voice", "voice": "cedar"}))
        ws.receive = AsyncMock(return_value=set_voice_frame)
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=0, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            session_id, resumed_state, leftover = await processor._negotiate_session(ws, persona, resolved_model, None)

        self.assertIsNone(resumed_state)
        self.assertIs(leftover, set_voice_frame)

    async def test_resume_accepted_mid_conversation_rehydrates_and_suppresses_the_greeting(self):
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 2.0
        processor._sessions.nudge_after_seconds = 30.0
        processor._sessions.rehydration_text.return_value = "ORDER + HISTORY"
        processor._sessions.get_voice.return_value = "cedar"
        processor._start_greeting = MagicMock()
        outcome = session_manager.ResumeOutcome(
            True, session_id="resumed-1", reason=None, resume_id="rotated-id", stale_ws=None, conversation_started=True,
        )
        processor._sessions.resume.return_value = outcome
        ws = _make_mock_ws()
        ws.receive = AsyncMock(return_value=_fake_resume_first_frame())
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=2, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            mock_order_state.get_order_summary_json.return_value = "{}"
            session_id, resumed_state, leftover = await processor._negotiate_session(ws, persona, resolved_model, None)

        self.assertEqual(session_id, "resumed-1")
        self.assertIsNone(leftover)
        self.assertIsNotNone(resumed_state)
        self.assertEqual(resumed_state.voice, "cedar")
        self.assertTrue(resumed_state.nudge_eligible)
        self.assertEqual(resumed_state.messages[-1].content, "ORDER + HISTORY")
        processor._start_greeting.assert_not_called()  # #247/#126: never re-greet mid-conversation
        self.assertIn("extension.session_resumed", _sent_types(ws))

    async def test_resume_accepted_before_any_greeting_still_runs_the_normal_greeting(self):
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 2.0
        processor._start_greeting = MagicMock()
        outcome = session_manager.ResumeOutcome(
            True, session_id="resumed-1", reason=None, resume_id="rotated-id", stale_ws=None, conversation_started=False,
        )
        processor._sessions.resume.return_value = outcome
        processor._sessions.get_voice.return_value = None
        ws = _make_mock_ws()
        ws.receive = AsyncMock(return_value=_fake_resume_first_frame())
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=0, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            mock_order_state.get_order_summary_json.return_value = "{}"
            session_id, resumed_state, leftover = await processor._negotiate_session(ws, persona, resolved_model, None)

        processor._start_greeting.assert_called_once()
        self.assertFalse(resumed_state.nudge_eligible)

    async def test_resume_with_a_stale_attached_socket_closes_it_with_4002(self):
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 2.0
        processor._start_greeting = MagicMock()
        stale_ws = _make_mock_ws()
        stale_ws.close = AsyncMock()
        outcome = session_manager.ResumeOutcome(
            True, session_id="resumed-1", reason=None, resume_id="rotated-id", stale_ws=stale_ws, conversation_started=False,
        )
        processor._sessions.resume.return_value = outcome
        processor._sessions.get_voice.return_value = None
        ws = _make_mock_ws()
        ws.receive = AsyncMock(return_value=_fake_resume_first_frame())
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=0, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            mock_order_state.get_order_summary_json.return_value = "{}"
            await processor._negotiate_session(ws, persona, resolved_model, None)
            # `_close_superseded` is spawned as a background task -- give the event loop a turn.
            await asyncio.sleep(0)

        stale_ws.close.assert_awaited_once()
        self.assertEqual(stale_ws.close.await_args.kwargs.get("code"), session_manager.SUPERSEDED_CLOSE_CODE)

    async def test_resume_rejected_falls_through_to_a_fresh_session(self):
        processor = _make_processor({})
        processor._sessions.first_frame_timeout_seconds = 2.0
        processor._sessions.create_session.return_value = "fresh-1"
        processor._sessions.issue_resume_id.return_value = "new-resume-id"
        outcome = session_manager.ResumeOutcome(False, reason="expired")
        processor._sessions.resume.return_value = outcome
        ws = _make_mock_ws()
        ws.receive = AsyncMock(return_value=_fake_resume_first_frame())
        persona = _fake_persona("marin")
        resolved_model = SimpleNamespace(id="m1", deployment="d", reasoning=None, pipeline="cascade")

        with patch("cascade_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_session_identifiers.return_value = SimpleNamespace(
                session_token="tok", round_trip_index=0, round_trip_token="rtt", persona_id="p", model_id="m1", pipeline="cascade")
            session_id, resumed_state, leftover = await processor._negotiate_session(ws, persona, resolved_model, None)

        self.assertEqual(session_id, "fresh-1")
        self.assertIsNone(resumed_state)
        self.assertIsNone(leftover)
        sent_types = _sent_types(ws)
        self.assertIn("extension.resume_rejected", sent_types)
        rejected = ws.send_json.await_args_list[sent_types.index("extension.resume_rejected")].args[0]
        self.assertEqual(rejected["reason"], "expired")


# ═══════════════════════════════════════════════════════════════════════════════
# #126: cascade's own one-shot resume nudge
# ═══════════════════════════════════════════════════════════════════════════════

class NudgeSchedulingTests(unittest.IsolatedAsyncioTestCase):
    async def test_a_late_resumes_first_mic_chunk_arms_the_nudge_exactly_once(self):
        processor = _make_processor({})
        processor._sessions.nudge_after_seconds = 9999  # never actually fires in this test
        ws = _make_mock_ws()
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin",
                                      detector=detector, nudge_eligible=True)

        with patch.object(processor, "_schedule_nudge") as mock_schedule:
            await processor._handle_client_message(
                ws, "s1", state, detector,
                {"type": "input_audio_buffer.append", "audio": base64.b64encode(_silence(10)).decode()},
                None,
            )
            await processor._handle_client_message(
                ws, "s1", state, detector,
                {"type": "input_audio_buffer.append", "audio": base64.b64encode(_silence(10)).decode()},
                None,
            )

        mock_schedule.assert_called_once()
        self.assertTrue(state.nudge_armed)

    async def test_nudge_does_not_arm_when_the_connection_was_not_resume_eligible(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin", detector=detector)

        with patch.object(processor, "_schedule_nudge") as mock_schedule:
            await processor._handle_client_message(
                ws, "s1", state, detector,
                {"type": "input_audio_buffer.append", "audio": base64.b64encode(_silence(10)).decode()},
                None,
            )

        mock_schedule.assert_not_called()

    async def test_barge_in_cancels_a_pending_nudge(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        detector = _TurnDetector(threshold=0.5, silence_duration_ms=200, sample_rate=24000)
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin", detector=detector)
        state.nudge_task = asyncio.ensure_future(asyncio.sleep(100))

        with patch.object(processor, "_cancel_current_turn", AsyncMock()):
            await processor._handle_client_message(
                ws, "s1", state, detector,
                {"type": "input_audio_buffer.append", "audio": base64.b64encode(_loud_tone(2400)).decode()},
                None,
            )

        self.assertIsNone(state.nudge_task)

    async def test_nudge_fires_the_prompt_text_through_the_normal_turn_machinery_when_idle(self):
        """Mutation-test seam: dropping the `current_turn_task` in-flight check would let a
        nudge fire while the assistant is already speaking -- stacking a second response on top
        of the first, exactly the bug #126's brief calls out."""
        processor = _make_processor({})
        processor._sessions.nudge_after_seconds = 0.01
        processor._sessions.nudge_text.return_value = "ask if they need anything else"
        processor._run_turn_and_speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin", role_name="carhop")

        processor._schedule_nudge(ws, "s1", state)
        await state.nudge_task

        processor._run_turn_and_speak.assert_awaited_once_with(ws, "s1", state)
        self.assertEqual(state.messages[-1].content, "ask if they need anything else")

    async def test_a_firing_nudge_is_tracked_as_the_current_turn_so_barge_in_cancels_and_awaits_it(self):
        processor = _make_processor({})
        processor._sessions.nudge_after_seconds = 0.01
        processor._sessions.nudge_text.return_value = "anything else?"
        started = asyncio.Event()
        cancelled = []

        async def slow_turn(*_a):
            started.set()
            try:
                await asyncio.sleep(100)
            except asyncio.CancelledError:
                cancelled.append(True)
                raise

        processor._run_turn_and_speak = slow_turn
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        processor._schedule_nudge(ws, "s1", state)
        nudge = state.nudge_task
        await started.wait()
        self.assertIs(state.current_turn_task, nudge)
        self.assertIsNone(state.nudge_task)

        await processor._cancel_current_turn(state, "barge-in")

        self.assertTrue(nudge.done())
        self.assertEqual(cancelled, [True])

    async def test_nudge_skips_firing_when_a_turn_is_still_in_flight(self):
        processor = _make_processor({})
        processor._sessions.nudge_after_seconds = 0.01
        processor._run_turn_and_speak = AsyncMock()
        ws = _make_mock_ws()
        state = _CascadeSessionState(session_id="s1", persona_id="p", deployment="d", voice="marin")
        state.current_turn_task = asyncio.ensure_future(asyncio.sleep(100))

        processor._schedule_nudge(ws, "s1", state)
        await state.nudge_task

        processor._run_turn_and_speak.assert_not_awaited()
        state.current_turn_task.cancel()


# ═══════════════════════════════════════════════════════════════════════════════
# #248: RATE_LIMIT_RECOVERY_ENABLED must be read from the environment, same as rtmt.py
# ═══════════════════════════════════════════════════════════════════════════════

class RateLimitSettingsReadsEnvironmentTests(unittest.TestCase):
    """Summer's review note on #248: `RateLimitSettings.from_config(_config)` (no `environ`
    argument) means `(environ or {}).get(ENABLED_ENV)` always sees `{}` -- `RATE_LIMIT_RECOVERY_
    ENABLED` silently never reaches cascade sessions, unlike `rtmt.py:1341`'s own
    `RateLimitSettings.from_config(_config, os.environ)`. Mutation-test proof: reverting
    `CascadeProcessor.__init__`'s `RateLimitSettings.from_config(_config, os.environ)` back to
    `RateLimitSettings.from_config(_config)` makes `test_env_var_override_disables_rate_limit_
    recovery_for_a_new_processor` fail (the env var patch below would have no effect)."""

    def test_env_var_override_disables_rate_limit_recovery_for_a_new_processor(self):
        with patch.dict("os.environ", {"RATE_LIMIT_RECOVERY_ENABLED": "false"}):
            processor = _make_processor({})
        self.assertFalse(processor._rate_limit_settings.enabled)

    def test_env_var_override_enables_rate_limit_recovery_even_if_config_disables_it(self):
        with patch("cascade_processor._config", {"resilience": {"rate_limit": {"enabled": False}}}):
            with patch.dict("os.environ", {"RATE_LIMIT_RECOVERY_ENABLED": "true"}):
                processor = _make_processor({})
        self.assertTrue(processor._rate_limit_settings.enabled)

    def test_no_env_var_set_falls_back_to_config_yamls_own_default(self):
        with patch.dict("os.environ", {}, clear=False):
            import os as _os
            _os.environ.pop("RATE_LIMIT_RECOVERY_ENABLED", None)
            processor = _make_processor({})
        self.assertTrue(processor._rate_limit_settings.enabled)  # config.yaml's default: enabled


if __name__ == "__main__":
    unittest.main()
