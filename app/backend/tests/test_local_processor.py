"""Unit/conformance tests for the local PipelineProcessor (issue #81, P2-12).

Covers the pieces that can be proven fast and deterministically without a real companion
local-runtime process (the wire contract itself -- HttpLocalRuntimeClient's request/response
shapes -- is covered by test_local_runtime.py's real-aiohttp-server-backed tests):
  - `_tool_definitions`: tools.py's own flat schemas passed straight through, unedited.
  - `LocalProcessor._execute_tool_call`: THE tool-calling wire-protocol contract issue #81
    requires be "the SAME" as realtime's/cascade's -- same `ToolResultDirection`-gated
    send-to-client logic, same `extension.middle_tier_tool_response` shape, same best-effort
    ticket-refresh-on-exception fallback. This is also where the task's four conformance rows
    live:
      * `test_tool_calling_round_trip_executes_the_tool_and_returns_the_final_answer` (tool
        calling)
      * `test_to_client_and_server_result_carries_priced_order_json_through_unchanged` (pricing
        -- a `ToolResult` carrying priced order JSON must reach the client byte-for-byte)
      * `test_not_on_menu_rejection_reason_flows_through_unchanged` (not_on_menu -- the
        `{"reason": "not_on_menu", ...}` shape tools.py's own `update_order` produces must
        reach the client verbatim through this pipeline too)
      * the persona-binding tests further below (system prompt AND model allow-list, both
        bound per-persona)
    `test_to_server_only_result_is_not_sent_to_client` is the mutation-test proof ("local tool
    result dropped -> row fails" from the task): reverting the `send_to_client` gate to
    always-True, or dropping the `extension.middle_tier_tool_response` send, makes the
    corresponding assertions in these tests fail.
  - `LocalProcessor._run_chat_tool_loop`: the multi-round tool-call loop and its round cap,
    against a `FakeLocalRuntimeClient` (this module's own stand-in for the companion process --
    issue #81's own "a fake local runtime" conformance ask).
  - `LocalProcessor.resolve_model`: delegates to `resolve_local_model` -- proven already at the
    function level in test_processors.py; `test_local_selectable_without_runtime_is_rejected`
    below is the SAME guarantee proven through the processor's own instance method (the "plus a
    row proving local is NOT selectable when the runtime isn't configured" the task calls out).
  - Greeting-on-connect parity with cascade (`_send_greeting`).
  - Persona binding: two personas' distinct `local_system_prompt`/`system_prompt` content AND
    distinct `models.local.allowed` lists are each honored independently by the SAME
    `LocalProcessor` instance -- proves neither is accidentally shared/global state.
"""

import sys
import unittest
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp import web

from local_processor import LocalProcessor, _LocalSessionState, _tool_definitions
from local_runtime import LocalChatResult, LocalRuntimeError, LocalToolCall
from processors import ModelSelectionError, resolve_local_model
from prompt_loader import PromptLoader
from rtmt import Tool, ToolResult, ToolResultDirection

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _make_mock_ws():
    ws = MagicMock(spec=web.WebSocketResponse)
    ws.closed = False
    ws.send_json = AsyncMock()
    return ws


class _EmptyWs:
    """A minimal stand-in for a `web.WebSocketResponse` that has already been closed by the
    client -- just enough async-iterator protocol for `async for msg in ws:` to complete
    immediately with zero messages, without MagicMock's non-async default `__aiter__`/`__anext__`
    getting in the way."""

    def __aiter__(self):
        return self

    async def __anext__(self):
        raise StopAsyncIteration


def _sent_types(ws) -> list[str]:
    return [call.args[0]["type"] for call in ws.send_json.await_args_list]


# ═══════════════════════════════════════════════════════════════════════════════
# FakeLocalRuntimeClient -- issue #81's own "a fake local runtime" conformance ask
# ═══════════════════════════════════════════════════════════════════════════════

class FakeLocalRuntimeClient:
    """Stands in for the whole companion local-runtime process (implements the same
    `LocalRuntimeClient` protocol `HttpLocalRuntimeClient` does -- see local_runtime.py), driven
    by a scripted queue of `/v1/chat`-shaped responses instead of a real HTTP round trip. This
    is what `test_local_processor.py`'s rows exercise `LocalProcessor` against; the wire
    contract itself (request/response shapes over real HTTP) is proven separately in
    test_local_runtime.py."""

    def __init__(self, chat_script=None, transcript: str = "one shake please", speech: bytes = b"\x01\x00" * 12):
        self._chat_script = list(chat_script or [])
        self.transcript = transcript
        self.speech = speech
        self.chat_calls: list[tuple[list, list]] = []
        self.transcribe_calls: list[bytes] = []
        self.speak_calls: list[tuple[str, str]] = []

    async def transcribe(self, pcm16_bytes: bytes) -> str:
        self.transcribe_calls.append(pcm16_bytes)
        return self.transcript

    async def chat(self, messages, tools) -> LocalChatResult:
        self.chat_calls.append(([dict(m) for m in messages], list(tools)))
        return self._chat_script.pop(0)

    async def speak(self, text: str, voice: str) -> bytes:
        self.speak_calls.append((text, voice))
        return self.speech


def _content_result(text: str) -> LocalChatResult:
    return LocalChatResult(content=text, tool_calls=[])


def _tool_call_result(name: str, call_id: str = "call_1", arguments: str = "{}") -> LocalChatResult:
    return LocalChatResult(content=None, tool_calls=[LocalToolCall(id=call_id, name=name, arguments=arguments)])


# ═══════════════════════════════════════════════════════════════════════════════
# _tool_definitions -- flat schemas passed through unedited
# ═══════════════════════════════════════════════════════════════════════════════

class ToolDefinitionsTests(unittest.TestCase):
    def test_returns_tool_schemas_unedited(self):
        schema = {"type": "function", "name": "get_order", "description": "Get the current order", "parameters": {}}
        tools = {"get_order": Tool(target=AsyncMock(), schema=schema)}
        self.assertEqual(_tool_definitions(tools), [schema])


# ═══════════════════════════════════════════════════════════════════════════════
# LocalProcessor._execute_tool_call -- THE tool-calling wire-protocol contract
# ═══════════════════════════════════════════════════════════════════════════════

def _make_processor(tools: dict, *, persona_prompt_loaders: dict | None = None) -> LocalProcessor:
    sessions = MagicMock()
    sessions.get_context_monitor.return_value = None
    sessions.emit_session_identifiers = AsyncMock()
    return LocalProcessor(
        tools=tools,
        sessions=sessions,
        persona_catalog=MagicMock(),
        persona_prompt_loaders=persona_prompt_loaders or {},
        model_catalog=MagicMock(),
    )


def _fake_tool_call(name: str, arguments: str = "{}", call_id: str = "call_1") -> LocalToolCall:
    return LocalToolCall(id=call_id, name=name, arguments=arguments)


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
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("search"), "prev-item-1")

        ws.send_json.assert_not_called()
        self.assertEqual(len(state.messages), 1)
        self.assertEqual(state.messages[0]["content"], "search results here")
        self.assertEqual(state.messages[0]["tool_call_id"], "call_1")

    async def test_tool_calling_round_trip_executes_the_tool_and_returns_the_final_answer(self):
        """CONFORMANCE ROW (tool calling): a tool_call from /v1/chat gets executed against the
        SAME shared `tools` dict the realtime/cascade pipelines use, its result is appended to
        the model's own conversation, and the wire's `extension.middle_tier_tool_response` shape
        is exactly {previous_item_id, tool_name, tool_result} -- the ONE thing the frontend
        actually reads off this event."""
        target = AsyncMock(return_value=ToolResult("server text", ToolResultDirection.TO_BOTH, client_text='{"order":[]}'))
        tools = {"update_order": Tool(target=target, schema={"name": "update_order"})}
        processor = _make_processor(tools)
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("update_order", call_id="call_42"), "prev-item-7")

        ws.send_json.assert_called_once_with({
            "type": "extension.middle_tier_tool_response",
            "previous_item_id": "prev-item-7",
            "tool_name": "update_order",
            "tool_result": '{"order":[]}',
        })
        target.assert_awaited_once_with({}, "s1")  # update_order gets session_id (per rtmt.py's contract)
        self.assertEqual(state.messages[0]["content"], "server text")

    async def test_to_client_and_server_result_carries_priced_order_json_through_unchanged(self):
        """CONFORMANCE ROW (pricing): a priced order ticket (get_order's own JSON, dollars and
        cents included) must reach the client byte-for-byte through this pipeline -- proves the
        local pipeline never re-serializes/re-formats a tool's structured result on its way to
        the frontend."""
        priced_json = '{"items":[{"name":"Route 44 Cherry Limeade","price":3.49}],"total":3.49}'
        target = AsyncMock(return_value=ToolResult(priced_json, ToolResultDirection.TO_BOTH, client_text=priced_json))
        tools = {"get_order": Tool(target=target, schema={"name": "get_order"})}
        processor = _make_processor(tools)
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("get_order"), "prev-item-3")

        sent = ws.send_json.await_args.args[0]
        self.assertEqual(sent["tool_result"], priced_json)
        self.assertEqual(state.messages[0]["content"], priced_json)

    async def test_not_on_menu_rejection_reason_flows_through_unchanged(self):
        """CONFORMANCE ROW (not_on_menu): the same {"reason": "not_on_menu", ...} shape
        tools.py's real update_order produces for an item that isn't on the menu must reach the
        client verbatim -- this pipeline must never swallow or reword a rejection reason."""
        rejection_json = '{"status":"rejected","reason":"not_on_menu","item":"a hot dog"}'
        target = AsyncMock(return_value=ToolResult(rejection_json, ToolResultDirection.TO_BOTH, client_text=rejection_json))
        tools = {"update_order": Tool(target=target, schema={"name": "update_order"})}
        processor = _make_processor(tools)
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("update_order"), "prev-item-4")

        sent = ws.send_json.await_args.args[0]
        self.assertIn("not_on_menu", sent["tool_result"])
        self.assertEqual(sent["tool_result"], rejection_json)

    async def test_unhandled_tool_exception_sends_neutral_output_and_best_effort_ticket(self):
        target = AsyncMock(side_effect=RuntimeError("boom"))
        tools = {"update_order": Tool(target=target, schema={"name": "update_order"})}
        processor = _make_processor(tools)
        with patch("local_processor.order_state_singleton") as mock_order_state:
            mock_order_state.get_order_summary_json.return_value = '{"order":["fries"]}'
            ws = _make_mock_ws()
            state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

            await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("update_order"), "prev-item-9")

        ws.send_json.assert_called_once_with({
            "type": "extension.middle_tier_tool_response",
            "previous_item_id": "prev-item-9",
            "tool_name": "get_order",  # NOT "update_order" -- the best-effort fallback is always get_order
            "tool_result": '{"order":["fries"]}',
        })
        self.assertIn("Something went wrong", state.messages[0]["content"])

    async def test_unknown_tool_name_is_a_noop_with_an_empty_tool_message(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._execute_tool_call(ws, "s1", state, _fake_tool_call("totally_unknown"), "prev-item-1")

        ws.send_json.assert_not_called()
        self.assertEqual(state.messages[0]["content"], "")


# ═══════════════════════════════════════════════════════════════════════════════
# LocalProcessor._run_chat_tool_loop
# ═══════════════════════════════════════════════════════════════════════════════

class RunChatToolLoopTests(unittest.IsolatedAsyncioTestCase):
    async def test_resolves_after_one_tool_round(self):
        target = AsyncMock(return_value=ToolResult("ok", ToolResultDirection.TO_SERVER))
        tools = {"get_order": Tool(target=target, schema={"name": "get_order"})}
        processor = _make_processor(tools)
        runtime = FakeLocalRuntimeClient(chat_script=[
            _tool_call_result("get_order"),
            _content_result("Your order is ready."),
        ])
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=runtime, voice="en_US-amy-medium")

        final_text = await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(final_text, "Your order is ready.")
        self.assertEqual(len(runtime.chat_calls), 2)

    async def test_hits_the_round_cap_and_returns_empty_string_instead_of_looping_forever(self):
        """The mutation-test-provable safety seam: if a model (or a broken fake companion
        process) keeps calling tools forever, this processor must still terminate the turn
        deterministically."""
        target = AsyncMock(return_value=ToolResult("ok", ToolResultDirection.TO_SERVER))
        tools = {"get_order": Tool(target=target, schema={"name": "get_order"})}
        processor = _make_processor(tools)
        runtime = FakeLocalRuntimeClient(chat_script=[_tool_call_result("get_order")] * LocalProcessor._MAX_TOOL_ROUNDS)
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=runtime, voice="en_US-amy-medium")

        final_text = await processor._run_chat_tool_loop(ws, "s1", state)

        self.assertEqual(final_text, "")
        self.assertEqual(len(runtime.chat_calls), LocalProcessor._MAX_TOOL_ROUNDS)

    async def test_chat_failure_propagates_as_local_runtime_error(self):
        processor = _make_processor({})
        runtime = MagicMock()
        runtime.chat = AsyncMock(side_effect=LocalRuntimeError("companion process unreachable"))
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=runtime, voice="en_US-amy-medium")

        with self.assertRaises(LocalRuntimeError):
            await processor._run_chat_tool_loop(ws, "s1", state)


# ═══════════════════════════════════════════════════════════════════════════════
# Issue #81 part 1, item 4: an unreachable runtime sends the pack's generic_error
# notice instead of silence (conformance twin: LocalRuntimeUnreachableTests)
# ═══════════════════════════════════════════════════════════════════════════════

class _UnreachableRuntime:
    """Every call fails the way `HttpLocalRuntimeClient` does when nothing listens."""

    def __init__(self):
        self.speak_calls: list[tuple[str, str]] = []

    async def transcribe(self, pcm16_bytes: bytes) -> str:
        raise LocalRuntimeError("connection refused")

    async def chat(self, messages, tools) -> LocalChatResult:
        raise LocalRuntimeError("connection refused")

    async def speak(self, text: str, voice: str) -> bytes:
        self.speak_calls.append((text, voice))
        raise LocalRuntimeError("connection refused")


class RuntimeErrorNoticeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.loader = PromptLoader(brand="test-alpha", prompts_dir=FIXTURES_DIR / "test-alpha" / "prompts")
        self.expected_notice = self.loader.render_error("generic_error")
        self.processor = _make_processor({}, persona_prompt_loaders={"test-alpha": self.loader})
        self.runtime = _UnreachableRuntime()
        self.state = _LocalSessionState(session_id="s1", persona_id="test-alpha", runtime=self.runtime, voice="en_US-amy-medium")

    def _transcript_deltas(self, ws) -> list[str]:
        return [call.args[0]["delta"] for call in ws.send_json.await_args_list
                if call.args[0]["type"] == "response.audio_transcript.delta"]

    async def test_chat_failure_sends_the_packs_generic_error_as_the_transcript(self):
        ws = _make_mock_ws()
        self.state.messages.append({"role": "user", "content": "one shake please"})

        with patch("local_processor.order_state_singleton"):
            await self.processor._run_turn_and_speak(ws, "s1", self.state)

        self.assertEqual(_sent_types(ws), ["response.created", "response.audio_transcript.delta", "response.done"])
        self.assertEqual(self._transcript_deltas(ws), [self.expected_notice])
        self.assertEqual(self.state.messages[-1], {"role": "assistant", "content": self.expected_notice})
        self.assertEqual(self.runtime.speak_calls, [], "the runtime that just failed must not be asked to speak")

    async def test_transcription_failure_sends_the_packs_generic_error_framed_as_a_response(self):
        ws = _make_mock_ws()

        await self.processor._process_turn(ws, "s1", self.state, b"\x00\x10" * 480)

        self.assertEqual(_sent_types(ws), ["response.created", "response.audio_transcript.delta", "response.done"])
        self.assertEqual(self._transcript_deltas(ws), [self.expected_notice])
        created, _, done = (call.args[0] for call in ws.send_json.await_args_list)
        self.assertEqual(created["response"]["id"], done["response"]["id"])
        self.assertEqual(self.state.messages, [], "no guest text was heard, so nothing joins the chat history")


# ═══════════════════════════════════════════════════════════════════════════════
# LocalProcessor.resolve_model -- delegates to resolve_local_model
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class _FakePipelineCfg:
    default: str
    allowed: list


@dataclass
class _FakeModels:
    realtime: _FakePipelineCfg
    local: _FakePipelineCfg | None = None


@dataclass
class _FakeManifest:
    models: _FakeModels


@dataclass
class _FakePersona:
    id: str
    manifest: _FakeManifest


def _local_persona(local_default: str | None, local_allowed: list | None = None, *, persona_id: str = "test-persona") -> _FakePersona:
    local_cfg = _FakePipelineCfg(default=local_default, allowed=local_allowed or []) if local_default is not None else None
    return _FakePersona(
        id=persona_id,
        manifest=_FakeManifest(models=_FakeModels(realtime=_FakePipelineCfg(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"]), local=local_cfg)),
    )


_LOCAL_CATALOG_CFG = {
    "models": {
        "catalog": [
            {"id": "phi-4-mini-local", "pipeline": "local", "label": "Phi-4 mini (on device)", "runtime": "onnx"},
        ]
    }
}


def _local_catalog(runtime_endpoint: str | None = None):
    from model_catalog import ModelCatalog
    env = {"LOCAL_RUNTIME_ENDPOINT": runtime_endpoint} if runtime_endpoint else {}
    return ModelCatalog.load(config=_LOCAL_CATALOG_CFG, environ=env)


class ResolveModelDelegationTests(unittest.TestCase):
    def test_resolve_model_delegates_to_resolve_local_model(self):
        persona = _local_persona("phi-4-mini-local", ["phi-4-mini-local"])
        catalog = _local_catalog("http://localhost:9001")
        processor = LocalProcessor(tools={}, sessions=MagicMock(), persona_catalog=MagicMock(), persona_prompt_loaders={}, model_catalog=catalog)

        resolved = processor.resolve_model(persona, None)

        self.assertEqual(resolved.id, "phi-4-mini-local")
        self.assertEqual(resolved.pipeline, "local")
        self.assertEqual(resolved.deployment, "http://localhost:9001")

    def test_local_selectable_without_runtime_is_rejected(self):
        """CONFORMANCE ROW: local mode is NOT selectable when LOCAL_RUNTIME_ENDPOINT isn't
        configured -- proven here through LocalProcessor's own resolve_model (not just the bare
        resolve_local_model function, already covered in test_processors.py), so this is a
        genuine processor-level guarantee, not only an algorithm-level one. This is also the
        OTHER mutation-test guard the task calls out: removing/bypassing this check makes local
        mode selectable with no runtime configured, and this test starts failing."""
        persona = _local_persona("phi-4-mini-local", ["phi-4-mini-local"])
        catalog = _local_catalog()  # no LOCAL_RUNTIME_ENDPOINT set
        processor = LocalProcessor(tools={}, sessions=MagicMock(), persona_catalog=MagicMock(), persona_prompt_loaders={}, model_catalog=catalog)

        with self.assertRaisesRegex(ModelSelectionError, "runtime endpoint is not configured"):
            processor.resolve_model(persona, None)

        # Same guarantee, one level down (resolve_local_model itself) -- kept side-by-side so a
        # regression that breaks only the processor's own delegation (not the underlying
        # algorithm) is caught here too.
        with self.assertRaisesRegex(ModelSelectionError, "runtime endpoint is not configured"):
            resolve_local_model(persona, None, catalog)


# ═══════════════════════════════════════════════════════════════════════════════
# Greeting on connect (parity with cascade/realtime)
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
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")
        prompt_loader = MagicMock()
        prompt_loader.get_greeting.return_value = _greeting_payload(
            "Say EXACTLY this greeting and NOTHING else: Welcome to the drive-thru! "
            "What can I get started for you today?"
        )

        with patch("local_processor.order_state_singleton") as mock_order_state:
            mock_order_state.advance_round_trip.return_value = SimpleNamespace()
            await processor._send_greeting(ws, "s1", state, prompt_loader)

        self.assertEqual(len(state.messages), 1)
        self.assertIn("Welcome to the drive-thru!", state.messages[0]["content"])
        processor._speak.assert_awaited_once_with(ws, state, "Welcome to the drive-thru! What can I get started for you today?")
        self.assertEqual(_sent_types(ws), ["response.created", "response.audio_transcript.delta", "response.done"])

    async def test_greeting_is_a_noop_without_a_bound_prompt_loader(self):
        processor = _make_processor({})
        ws = _make_mock_ws()
        state = _LocalSessionState(session_id="s1", persona_id="test-persona", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")

        await processor._send_greeting(ws, "s1", state, None)

        ws.send_json.assert_not_called()
        self.assertEqual(state.messages, [])


# ═══════════════════════════════════════════════════════════════════════════════
# CONFORMANCE ROW: persona binding -- system prompt AND model allow-list are each
# bound per-persona, never shared/global state on the ONE LocalProcessor instance.
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaBindingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        # Real PromptLoaders built straight from the TEST-ONLY fixture packs' own prompts/
        # directories (test_persona_binding.py's own fixture pack) -- proves get_local_system_prompt
        # actually reads each persona's own files, not some shared/default one.
        self.alpha_loader = PromptLoader(brand="test-alpha", prompts_dir=FIXTURES_DIR / "test-alpha" / "prompts")
        self.beta_loader = PromptLoader(brand="test-beta", prompts_dir=FIXTURES_DIR / "test-beta" / "prompts")
        self.processor = _make_processor({}, persona_prompt_loaders={"test-alpha": self.alpha_loader, "test-beta": self.beta_loader})

    async def test_each_persona_gets_its_own_system_prompt(self):
        """Neither fixture pack defines local_system_prompt.yaml, so get_local_system_prompt
        falls back to get_system_prompt for both -- still proves per-persona binding, since the
        two packs' system prompts are genuinely different texts."""
        alpha_state = _LocalSessionState(session_id="s-alpha", persona_id="test-alpha", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")
        beta_state = _LocalSessionState(session_id="s-beta", persona_id="test-beta", runtime=FakeLocalRuntimeClient(), voice="en_US-amy-medium")
        alpha_state.messages.append({"role": "system", "content": self.alpha_loader.get_local_system_prompt()})
        beta_state.messages.append({"role": "system", "content": self.beta_loader.get_local_system_prompt()})

        self.assertNotEqual(alpha_state.messages[0]["content"], beta_state.messages[0]["content"])
        self.assertEqual(alpha_state.messages[0]["content"], self.alpha_loader.get_system_prompt())
        self.assertEqual(beta_state.messages[0]["content"], self.beta_loader.get_system_prompt())

    async def test_run_session_seeds_the_bound_personas_own_local_system_prompt(self):
        """End-to-end through `_run_session` itself (not just the loader directly): the SAME
        `LocalProcessor` instance, handling two different personas' sessions back-to-back, must
        seed each one's `state.messages[0]` from THAT session's own bound persona -- never a
        stale/shared value left over from a previous session. `_start_greeting` is stubbed out
        (it's covered on its own in `SendGreetingTests`) so this test can capture the `state` it
        would have been handed and inspect the seed directly."""
        for persona_id, loader in (("test-alpha", self.alpha_loader), ("test-beta", self.beta_loader)):
            ws = _EmptyWs()  # no client messages -- just prove the system-prompt seed
            persona = SimpleNamespace(id=persona_id)
            resolved_model = SimpleNamespace(deployment="http://localhost:9001")
            self.processor._start_greeting = MagicMock()  # skip the greeting; not under test here

            await self.processor._run_session(ws, f"session-{persona_id}", persona, resolved_model)

            captured_state = self.processor._start_greeting.call_args.args[2]
            self.assertEqual(captured_state.messages[0]["content"], loader.get_local_system_prompt())
            self.assertEqual(captured_state.messages[0]["content"], loader.get_system_prompt())

    def test_each_persona_has_its_own_local_model_allow_list(self):
        """The OTHER half of persona binding: model selection, not just prompt content. The SAME
        `LocalProcessor.resolve_model` must honor whichever persona is asking -- a persona that
        doesn't have local mode enabled at all must still be rejected even when another persona
        (sharing the SAME processor instance/catalog) allows that exact model id."""
        catalog = _local_catalog("http://localhost:9001")
        processor = LocalProcessor(tools={}, sessions=MagicMock(), persona_catalog=MagicMock(), persona_prompt_loaders={}, model_catalog=catalog)
        alpha = _local_persona("phi-4-mini-local", ["phi-4-mini-local"], persona_id="test-alpha")

        # alpha explicitly allows it (and it's the default for both).
        resolved = processor.resolve_model(alpha, "phi-4-mini-local")
        self.assertEqual(resolved.id, "phi-4-mini-local")

        # beta has NO models.local block at all -- local mode isn't enabled for it.
        beta_no_local = _local_persona(None, persona_id="test-beta")
        with self.assertRaisesRegex(ModelSelectionError, "no models.local configured"):
            processor.resolve_model(beta_no_local, "phi-4-mini-local")


if __name__ == "__main__":
    unittest.main()
