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
"""

import io
import sys
import unittest
import wave
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp import web

from cascade_processor import (
    _AUDIO_SAMPLE_RATE,
    CascadeProcessor,
    _CascadeSessionState,
    _pcm16_to_wav_bytes,
    _tool_definitions,
    _TurnDetector,
)
from rtmt import Tool, ToolResult, ToolResultDirection


def _make_mock_ws():
    ws = MagicMock(spec=web.WebSocketResponse)
    ws.closed = False
    ws.send_json = AsyncMock()
    return ws


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
        tools = {"get_order": Tool(target=AsyncMock(), schema=schema)}
        definitions = _tool_definitions(tools)
        self.assertEqual(len(definitions), 1)
        as_dict = definitions[0].as_dict()
        self.assertEqual(as_dict["function"]["name"], "get_order")
        self.assertEqual(as_dict["function"]["description"], "Get the current order")
        self.assertEqual(as_dict["function"]["parameters"], schema["parameters"])


# ═══════════════════════════════════════════════════════════════════════════════
# CascadeProcessor._execute_tool_call -- THE tool-calling wire-protocol contract
# ═══════════════════════════════════════════════════════════════════════════════

def _make_processor(tools: dict) -> CascadeProcessor:
    sessions = MagicMock()
    sessions.get_context_monitor.return_value = None
    return CascadeProcessor(
        tools=tools,
        sessions=sessions,
        persona_catalog=MagicMock(),
        persona_prompt_loaders={},
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
            model_catalog=catalog, foundry_endpoint="https://fake", audio_endpoint="https://fake",
            credential=MagicMock(),
        )
        resolved = processor.resolve_model(persona, None)
        self.assertEqual(resolved.id, "gpt-5-mini")
        self.assertEqual(resolved.pipeline, "cascade")
        self.assertEqual(resolved.deployment, "gpt-5-mini-prod")


if __name__ == "__main__":
    unittest.main()
