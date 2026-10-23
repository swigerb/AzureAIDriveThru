"""Scoped synthetic boundary capture: no network, credentials or model calls."""
import asyncio
import copy
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from datetime import UTC

import test_cascade_processor as helpers
from azure.ai.inference.models import ChatCompletions, SystemMessage, UserMessage

from model_diagnostics import DiagnosticCapture, DiagnosticGate, digest, sanitize
from rtmt import Tool, ToolResult, ToolResultDirection

OID = "11111111-1111-1111-1111-111111111111"
MARKER = "22222222222222222222222222222222"
NOW = 1792706400


def settings():
    from datetime import datetime
    return {
        "CascadeDiagnostics__Enabled": "true",
        "CascadeDiagnostics__OperatorObjectId": OID,
        "CascadeDiagnostics__RunMarker": MARKER,
        "CascadeDiagnostics__ExpiresUtc": datetime.fromtimestamp(NOW + 600, UTC).isoformat(),
    }


def completion(content=None, calls=None):
    return ChatCompletions({"choices": [{"index": 0, "finish_reason": "tool_calls" if calls else "stop",
                                        "message": {"role": "assistant", "content": content, "tool_calls": calls or []}}],
                            "id": "fake", "model": "fake", "created": 0})


class GateTests(unittest.TestCase):
    def claim(self, gate, env=None, **changes):
        scope = dict(entra=True, principal={"oid": OID}, marker=MARKER, persona="tacobell", session_id="one")
        scope.update(changes)
        return gate.claim(settings() if env is None else env, **scope)

    def test_disabled_default_and_invalid_configuration(self):
        self.assertIsNone(self.claim(DiagnosticGate(lambda: NOW), {}))
        for key, value in [("Enabled", "TRUE"), ("OperatorObjectId", ""),
                           ("RunMarker", "0" * 32), ("ExpiresUtc", "not-date"),
                           ("ExpiresUtc", "2000-01-01T00:00:00Z"),
                           ("ExpiresUtc", "2099-01-01T00:00:00Z")]:
            with self.subTest(key=key, value=value):
                env = settings()
                env["CascadeDiagnostics__" + key] = value
                self.assertIsNone(self.claim(DiagnosticGate(lambda: NOW), env))

    def test_scope_and_once_claim(self):
        gate = DiagnosticGate(lambda: NOW)
        for bad in [dict(entra=False), dict(principal=None), dict(principal={"oid": "bad"}),
                    dict(marker="wrong"), dict(persona="test-other")]:
            with self.subTest(bad=bad):
                self.assertIsNone(self.claim(gate, **bad))
        self.assertIsNotNone(self.claim(gate))
        self.assertIsNone(self.claim(gate, session_id="two"))

    def test_concurrent_process_claim(self):
        from concurrent.futures import ThreadPoolExecutor
        gate = DiagnosticGate(lambda: NOW)
        with ThreadPoolExecutor(max_workers=8) as pool:
            result = list(pool.map(lambda _: self.claim(gate), range(20)))
        self.assertEqual(sum(c is not None for c in result), 1)

    def test_redaction_and_bounds(self):
        for value in ["DEMO-PLUS", "AB123C", "123456", "name@example.com", "https://example.com",
                      "token=anything", "secret:abc", "Bearer_abcdefghijklmnopqrstuvwxyz", "+1(555)1234567"]:
            with self.subTest(value=value):
                self.assertNotIn(value, sanitize("Echo " + value))
        lines = []
        clock = [NOW]
        capture = DiagnosticCapture("one", NOW + 600, lambda: clock[0], lines.append)
        for _ in range(200):
            capture.emit("one")
        self.assertEqual(len(lines), 160)
        expiring = DiagnosticCapture("two", NOW + 1, lambda: clock[0], lines.append)
        clock[0] += 2
        expiring.emit("expired")
        self.assertEqual(len(lines), 160)


class BoundaryTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_resumed_run_session_stops_and_clears_inherited_capture_before_guest_completion(self):
        from unittest.mock import Mock

        from aiohttp import WSMessage, WSMsgType
        processor = helpers._make_processor({})
        lines = []
        capture = DiagnosticCapture("one", NOW + 600, lambda: NOW, lines.append)
        capture.emit("prior")
        state = helpers._CascadeSessionState(session_id="one", persona_id="tacobell", deployment="d", voice="marin", diagnostic=capture)
        state.messages = [SystemMessage(content="PRIVATE-SYSTEM")]
        state.detector = SimpleNamespace(feed=lambda *args, **kwargs: "speech_stopped",
                                         take_buffer=lambda: b"fake", reset=Mock())
        finished = asyncio.Event()
        processor._speak = AsyncMock(side_effect=lambda *args, **kwargs: finished.set())
        processor._transcribe = AsyncMock(return_value="I've checked in now.")
        client = SimpleNamespace(complete=AsyncMock(return_value=completion("Welcome back!")))
        processor._get_chat_client = AsyncMock(return_value=client)
        ws = helpers._make_mock_ws()
        async def frames():
            yield WSMessage(WSMsgType.TEXT, '{"type":"input_audio_buffer.append","audio":"ZmFrZQ=="}', None)
            await asyncio.wait_for(finished.wait(), 2)
        ws.__aiter__.side_effect = frames
        persona = SimpleNamespace(id="tacobell")
        with patch("cascade_processor.order_state_singleton") as order:
            order.advance_round_trip.return_value = SimpleNamespace()
            await processor._run_session(ws, "one", persona, SimpleNamespace(deployment="d"),
                                         resumed_state=state, diagnostic=None)
        self.assertIsNone(state.diagnostic)
        self.assertTrue(capture.stopped)
        client.complete.assert_awaited_once()
        capture.emit("late")
        self.assertEqual(len(lines), 1)
        self.assertTrue(any(call.args[0].get("delta") == "Welcome back!" for call in ws.send_json.await_args_list))

    async def test_actual_loader_greeting_and_guest_request_keep_policy_hash_and_tool_scope(self):
        processor, state, loader = helpers.SendGreetingTests()._scoped_fixture()
        lines = []
        state.diagnostic = DiagnosticCapture("scoped", NOW + 600, lambda: NOW, lines.append)
        greeting = "Welcome to Taco Bell! Are you checking in with the Taco Bell app today?"
        calls = [{"id": "checkin-call", "type": "function",
                  "function": {"name": "get_order", "arguments": "{}"}}]
        responses = iter([completion(greeting), completion(calls=calls), completion("Welcome back! You have 500 points ready.")])
        processor._get_chat_client = AsyncMock(return_value=SimpleNamespace(
            complete=AsyncMock(side_effect=lambda **kwargs: next(responses))))
        processor._transcribe = AsyncMock(return_value="I've checked in now.")
        processor.tools["get_order"] = Tool(
            target=AsyncMock(return_value=ToolResult('{"checkedIn":true,"balance":500}', ToolResultDirection.TO_SERVER)),
            schema={"name": "get_order"})
        with patch("cascade_processor.order_state_singleton") as order:
            order.advance_round_trip.return_value = SimpleNamespace()
            await processor._send_greeting(helpers._make_mock_ws(), "scoped", state, loader)
            await processor._process_turn(helpers._make_mock_ws(), "scoped", state, b"fake")
        requests = [json.loads(line) for line in lines if json.loads(line)["stage"] == "request"]
        self.assertEqual(len(requests), 3)
        self.assertEqual(requests[0]["messages"][0]["contentHash"], digest(loader.get_system_prompt()))
        self.assertEqual(requests[0]["messages"][1]["contentHash"], digest(loader.get_greeting()["response"]["instructions"]))
        self.assertEqual(requests[0]["tools"], [])
        self.assertEqual(requests[0]["toolChoice"], "none")
        self.assertEqual([m["role"] for m in requests[1]["messages"]], ["system", "assistant", "user"])
        self.assertEqual(requests[1]["tools"][0]["name"], "get_order")
        self.assertEqual(requests[1]["toolChoice"], "default")
        self.assertEqual(requests[2]["messages"][-1]["contentLength"], len('{"checkedIn":true,"balance":500}'))
        records = [json.loads(line) for line in lines]
        committed = next(r for r in records if r["stage"] == "guest.committed")
        self.assertIsNone(committed["responseId"])
        self.assertEqual(committed["guestIndex"], 1)
        self.assertEqual(requests[1]["guestIndex"], 1)
        self.assertIsNotNone(requests[1]["responseId"])
        self.assertNotIn("I've checked in now.", "\n".join(lines))
        self.assertNotIn('"checkedIn"', "\n".join(lines))

    async def run_path(self, diagnostic):
        processor = helpers._make_processor({})
        ws = helpers._make_mock_ws()
        state = helpers._CascadeSessionState(session_id="one", persona_id="tacobell", deployment="fake", voice="marin",
                                             diagnostic=diagnostic)
        state.messages.extend([SystemMessage(content="PRIVATE-SYSTEM"), UserMessage(content="PRIVATE-GUEST")])
        processor.tools["get_order"] = Tool(
            target=AsyncMock(return_value=ToolResult("PRIVATE-TOOL", ToolResultDirection.TO_SERVER)),
            schema={"name": "get_order"})
        calls = [{"id": "PRIVATE-CALL-ID", "type": "function",
                  "function": {"name": "get_order", "arguments": '{"private":"PRIVATE-ARGS"}'}}]
        replies = iter([completion("Let me check that.", calls), completion("Welcome back! You have 500 points ready.")])
        requests = []

        async def complete(**kwargs):
            requests.append(copy.deepcopy(kwargs))
            return next(replies)
        processor._get_chat_client = AsyncMock(return_value=SimpleNamespace(complete=complete))
        result = await processor._run_chat_tool_loop(ws, "one", state)
        return result, requests, state

    async def test_actual_tool_loop_snapshots_match_and_private_inputs_never_copied(self):
        baseline, expected, _ = await self.run_path(None)
        lines = []
        capture = DiagnosticCapture("one", NOW + 600, lambda: NOW, lines.append)
        actual, requests, state = await self.run_path(capture)
        self.assertEqual(actual, baseline)
        self.assertEqual([[m.as_dict() for m in r["messages"]] for r in requests],
                         [[m.as_dict() for m in r["messages"]] for r in expected])
        text = "\n".join(lines)
        self.assertNotIn("PRIVATE-", text)
        self.assertNotIn("arguments", text)
        records = [json.loads(s) for s in lines]
        self.assertEqual([r["stage"] for r in records], ["request", "choice", "request", "choice"])
        self.assertEqual(records[0]["messages"][0]["contentHash"], digest("PRIVATE-SYSTEM"))
        self.assertEqual([m["role"] for m in records[2]["messages"]], ["system", "user", "assistant", "tool"])
        self.assertEqual(records[1]["toolCalls"][0]["idHash"], records[2]["messages"][-1]["toolCallIdHash"])
        self.assertEqual(records[-1]["finishReason"], "stop")

    async def test_sanitizer_failure_never_changes_completion_and_disables_capture(self):
        lines = []
        def timeout(_):
            raise TimeoutError("NEVER-COPY")
        capture = DiagnosticCapture("one", NOW + 600, lambda: NOW, lines.append, timeout)
        baseline, _, _ = await self.run_path(None)
        actual, _, _ = await self.run_path(capture)
        self.assertEqual(actual, baseline)
        self.assertTrue(capture.stopped)
        self.assertEqual(lines, [])

    async def test_session_mismatch_does_not_capture_other_session(self):
        lines = []
        capture = DiagnosticCapture("different", NOW + 600, lambda: NOW, lines.append)
        await self.run_path(capture)
        self.assertTrue(capture.stopped)
        self.assertEqual(lines, [])

    async def test_actual_delayed_request_cancel_records_no_stale_assistant(self):
        lines = []
        capture = DiagnosticCapture("one", NOW + 600, lambda: NOW, lines.append)
        processor = helpers._make_processor({})
        state = helpers._CascadeSessionState(session_id="one", persona_id="tacobell", deployment="fake", voice="marin",
                                             diagnostic=capture)
        state.messages.append(UserMessage(content="PRIVATE-GUEST"))
        entered = asyncio.Event()
        async def complete(**kwargs):
            entered.set()
            await asyncio.Event().wait()
        processor._get_chat_client = AsyncMock(return_value=SimpleNamespace(complete=complete))
        state.current_turn_task = asyncio.create_task(
            processor._run_turn_and_speak(helpers._make_mock_ws(), "one", state))
        await asyncio.wait_for(entered.wait(), 2)
        await processor._cancel_current_turn(state, "guest interruption")
        stages = [json.loads(line)["stage"] for line in lines]
        self.assertEqual(stages, ["response.created", "request", "request.cancelled", "cancelled"])
        self.assertEqual([m.as_dict()["role"] for m in state.messages], ["user"])

    async def test_real_handle_only_binds_authenticated_fresh_scope(self):
        from entra_auth import PRINCIPAL_KEY
        processor = helpers._make_processor({})
        processor.entra_mode = True
        processor._diagnostic_gate = DiagnosticGate(lambda: NOW)
        request = {PRINCIPAL_KEY: {"oid": OID}}
        class Request(dict):
            query = {"diagnostic_run": MARKER}
        request = Request(request)
        persona = SimpleNamespace(id="tacobell", manifest=SimpleNamespace(features=SimpleNamespace(dayparts=False)))
        processor._negotiate_session = AsyncMock(return_value=("one", None, None))
        processor._run_session = AsyncMock()
        ws = helpers._make_mock_ws()
        ws.prepare = AsyncMock()
        with patch("cascade_processor.web.WebSocketResponse", return_value=ws), patch.dict("os.environ", settings()):
            await processor.handle(request, persona, SimpleNamespace())
        capture = processor._run_session.await_args.kwargs["diagnostic"]
        self.assertIsNotNone(capture)
        self.assertEqual(capture.session_id, "one")
        processor._run_session.reset_mock()
        processor._negotiate_session.return_value = ("one", SimpleNamespace(), None)
        with patch("cascade_processor.web.WebSocketResponse", return_value=ws), patch.dict("os.environ", settings()):
            await processor.handle(request, persona, SimpleNamespace())
        self.assertIsNone(processor._run_session.await_args.kwargs["diagnostic"])
