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
import sys
import unittest
import wave
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from azure.core.exceptions import HttpResponseError

from cascade_processor import (
    _AUDIO_SAMPLE_RATE,
    CascadeProcessor,
    CascadeRateLimitExhausted,
    _CascadeSessionState,
    _pcm16_to_wav_bytes,
    _tool_definitions,
    _TurnDetector,
)
from rate_limit import RATE_LIMITED_EVENT, RateLimitSettings
from rtmt import Tool, ToolResult, ToolResultDirection


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
    sessions.emit_session_identifiers = AsyncMock()
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
            ws, "Welcome to the drive-thru! What can I get started for you today?", "marin"
        )
        self.assertEqual(_sent_types(ws), ["response.created", "response.audio_transcript.delta", "response.done"])

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


if __name__ == "__main__":
    unittest.main()
