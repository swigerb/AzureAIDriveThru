"""Unit tests for scripts/ab_compare.py (issue #18 A/B harness).

Every test here runs against fakes: a fake ``subprocess.run`` for the ``az`` CLI calls, a fake
aiohttp-shaped HTTP/WebSocket layer for the realtime session, and the real persona fixtures
already checked into ``personas/`` (read-only). Nothing here opens a socket or shells out to
``az`` for real, and nothing talks to live Azure -- see the module docstring in
``ab_compare.py``.
"""
from __future__ import annotations

import asyncio
import base64
import json
import os
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

import ab_compare as abc  # noqa: E402

# Any real persona works for these tests -- picked at import time so no persona id is ever
# hardcoded here. Requires at least one persona pack with a demo script to exist on disk.
PERSONA_ID = abc.discover_personas()[0]


# ── Persona fixtures (real data on disk, read-only) ─────────────────────────

class PersonaDataTests(unittest.TestCase):
    def test_discover_personas_finds_every_pack_with_a_demo_script(self):
        personas = abc.discover_personas()
        self.assertIn(PERSONA_ID, personas)
        self.assertEqual(personas, sorted(personas))

    def test_load_guest_lines_returns_only_text_turns(self):
        lines = abc.load_guest_lines(PERSONA_ID)
        self.assertTrue(lines)
        self.assertTrue(all(isinstance(line, str) and line for line in lines))

    def test_load_expected_ticket_matches_dummy_order_total(self):
        ticket = abc.load_expected_ticket(PERSONA_ID)
        raw = json.loads((REPO_ROOT / "personas" / PERSONA_ID / "assets" / "demo" / "dummyOrder.json").read_text())
        expected_total = round(sum(row["price"] * row["quantity"] for row in raw), 2)
        self.assertAlmostEqual(ticket.total, expected_total, places=2)
        self.assertEqual(len(ticket.items), len(raw))

    def test_load_expected_ticket_unknown_persona_raises(self):
        with self.assertRaises(FileNotFoundError):
            abc.load_expected_ticket("not-a-real-persona")


# ── Entra token acquisition (fake subprocess) ───────────────────────────────

class FakeCompletedProcess(SimpleNamespace):
    pass


class EntraTokenTests(unittest.TestCase):
    def test_get_entra_token_parses_access_token(self):
        def fake_runner(cmd, **kwargs):
            self.assertIn("api://client-123/access_as_user", cmd)
            return FakeCompletedProcess(stdout=json.dumps({"accessToken": "fake-token"}), stderr="")

        token = abc.get_entra_token("client-123", runner=fake_runner)
        self.assertEqual(token, "fake-token")

    def test_get_entra_token_missing_cli_raises_ab_compare_error(self):
        def fake_runner(cmd, **kwargs):
            raise FileNotFoundError("az not found")

        with self.assertRaises(abc.ABCompareError):
            abc.get_entra_token("client-123", runner=fake_runner)

    def test_get_entra_token_no_access_token_in_payload_raises(self):
        def fake_runner(cmd, **kwargs):
            return FakeCompletedProcess(stdout=json.dumps({}), stderr="")

        with self.assertRaises(abc.ABCompareError):
            abc.get_entra_token("client-123", runner=fake_runner)


# ── realtime_url (mirrors useRealtime.tsx's getSocketUrl) ───────────────────

class RealtimeUrlTests(unittest.TestCase):
    def test_https_base_becomes_wss_with_all_four_params(self):
        url = abc.realtime_url(
            "https://example.test", persona_id="fake-persona", model_id="gpt-realtime-2.1-mini",
            session_token="sess-tok", access_token="entra-tok",
        )
        self.assertTrue(url.startswith("wss://example.test/realtime?"))
        self.assertIn("persona=fake-persona", url)
        self.assertIn("model=gpt-realtime-2.1-mini", url)
        self.assertIn("token=sess-tok", url)
        self.assertIn("access_token=entra-tok", url)


# ── Ticket parsing (extension.middle_tier_tool_response tool_result) ────────

class ParseTicketTests(unittest.TestCase):
    def test_parses_json_string_tool_result(self):
        payload = json.dumps({
            "items": [{"item": "Thing", "size": "Large", "quantity": 2, "price": 1.0, "display": "Large Thing"}],
            "finalTotal": 2.16,
        })
        parsed = abc._parse_ticket(payload)
        self.assertIsNotNone(parsed)
        items, total = parsed
        self.assertEqual(items, [abc.ExpectedItem(item="Thing", size="Large", quantity=2)])
        self.assertAlmostEqual(total, 2.16)

    def test_non_order_tool_result_returns_none(self):
        self.assertIsNone(abc._parse_ticket(json.dumps({"combo_requirements": True})))

    def test_malformed_json_returns_none(self):
        self.assertIsNone(abc._parse_ticket("not json"))


# ── Fake aiohttp-shaped WebSocket/HTTP layer for run_order ──────────────────

class FakeWSMsgType:
    TEXT = "TEXT"
    CLOSE = "CLOSE"
    CLOSED = "CLOSED"
    ERROR = "ERROR"


class FakeMessage(SimpleNamespace):
    pass


class FakeWebSocket:
    """A scripted sequence of inbound frames; records every outbound send_json call."""

    def __init__(self, frames: list[dict]):
        self._frames = list(frames)
        self.sent: list[dict] = []

    async def send_json(self, payload: dict) -> None:
        self.sent.append(payload)

    async def receive(self):
        if not self._frames:
            return FakeMessage(type=FakeWSMsgType.CLOSED, data=None)
        frame = self._frames.pop(0)
        return FakeMessage(type=FakeWSMsgType.TEXT, data=json.dumps(frame))

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc_info):
        return False


class GatedFakeWebSocket(FakeWebSocket):
    """Releases scripted frames the way the real server does: batch 0 on connect, batch 1 (the
    greeting) after the client's session.update, and each later batch only once the guest's
    speech is followed by trailing silence (server VAD end of turn)."""

    def __init__(self, batches: list[list[dict]]):
        super().__init__(list(batches[0]))
        self._pending = [list(b) for b in batches[1:]]
        self._in_speech = False

    async def send_json(self, payload: dict) -> None:
        await super().send_json(payload)
        if payload["type"] == "session.update" and self._pending:
            self._frames += self._pending.pop(0)
        elif payload["type"] == "input_audio_buffer.append":
            silent = set(base64.b64decode(payload["audio"])) <= {0}
            if not silent:
                self._in_speech = True
            elif self._in_speech and self._pending:
                self._in_speech = False
                self._frames += self._pending.pop(0)

    async def receive(self):
        if not self._frames:
            if self._pending:
                await asyncio.sleep(10)  # nothing until the client speaks; caller's wait_for times out
            return FakeMessage(type=FakeWSMsgType.CLOSED, data=None)
        return await super().receive()

class FakeHttp:
    def __init__(self, session_token: str = "sess-tok"):
        self._session_token = session_token
        self.requested_urls: list[str] = []

    def get(self, url: str, headers=None):
        self.requested_urls.append(url)
        return _FakeGetCtx(self._session_token)


class _FakeGetCtx:
    def __init__(self, session_token: str):
        self._session_token = session_token

    async def __aenter__(self):
        return _FakeResponse(self._session_token)

    async def __aexit__(self, *exc_info):
        return False


class _FakeResponse:
    def __init__(self, session_token: str):
        self._session_token = session_token

    def raise_for_status(self):
        return None

    async def json(self):
        return {"token": self._session_token}


def _patch_wsmsgtype(monkeypatch_target):
    """ab_compare imports aiohttp lazily inside functions; patch sys.modules so those imports
    resolve to a minimal fake exposing only what run_order touches."""
    import types
    fake_aiohttp = types.ModuleType("aiohttp")
    fake_aiohttp.WSMsgType = FakeWSMsgType
    sys.modules[monkeypatch_target] = fake_aiohttp


class RunOrderTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self._real_aiohttp = sys.modules.get("aiohttp")
        _patch_wsmsgtype("aiohttp")

    def tearDown(self):
        if self._real_aiohttp is not None:
            sys.modules["aiohttp"] = self._real_aiohttp
        else:
            sys.modules.pop("aiohttp", None)

    async def test_run_order_measures_latency_and_parses_ticket(self):
        ticket_json = json.dumps({
            "items": [
                {"item": "Test Combo Item", "size": "Standard", "quantity": 2,
                 "price": 6.59, "display": "Test Combo Item"},
            ],
            "finalTotal": 14.21,
        })
        batches = [[{"type": "extension.session_metadata"}],
                   [{"type": "response.created"}, {"type": "response.done"}]]  # greeting
        for _ in abc.load_guest_clip_paths(PERSONA_ID):
            batches.append([
                {"type": "response.created"},
                {"type": "response.output_audio.delta", "delta": "aa"},
                {"type": "extension.middle_tier_tool_response", "tool_name": "update_order", "tool_result": ticket_json},
                {"type": "response.done"},
            ])
        fake_ws = GatedFakeWebSocket(batches)
        def fake_ws_connect(url):
            return fake_ws

        result = await abc.run_order(
            FakeHttp(), fake_ws_connect,
            backend="python", base_url="https://python.example", persona_id=PERSONA_ID,
            model_id="gpt-realtime-2.1-mini", rep=0, access_token="entra-tok", timeout_s=2.0,
            clip_loader=lambda _path: b"\x01\x00" * 4800,
        )

        self.assertIsNone(result.error)
        self.assertIsNotNone(result.cold_start_s)
        self.assertEqual(len(result.turns), len(abc.load_guest_clip_paths(PERSONA_ID)))
        self.assertTrue(all(t.first_audio_latency_s is not None for t in result.turns))
        self.assertEqual(result.ticket_total, 14.21)
        self.assertEqual(result.ticket_items, [abc.ExpectedItem(item="Test Combo Item", size="Standard", quantity=2)])

        # Browser-faithful: the frontend's session.update, then streamed audio -- never client text turns.
        sent_types = [m["type"] for m in fake_ws.sent]
        self.assertEqual(sent_types[0], "session.update")
        self.assertIn("input_audio_buffer.append", sent_types)
        self.assertNotIn("conversation.item.create", sent_types)
        self.assertNotIn("response.create", sent_types)

    async def test_run_order_records_error_without_raising(self):
        fake_ws = FakeWebSocket([])  # no extension.session_metadata ever arrives -> timeout

        def fake_ws_connect(url):
            return fake_ws

        result = await abc.run_order(
            FakeHttp(), fake_ws_connect,
            backend="python", base_url="https://python.example", persona_id=PERSONA_ID,
            model_id="gpt-realtime-2.1-mini", rep=0, access_token="entra-tok", timeout_s=0.2,
        )
        self.assertIsNotNone(result.error)
        self.assertEqual(result.turns, [])

    async def test_run_order_missing_session_token_is_recorded_as_error(self):
        class BrokenHttp(FakeHttp):
            def get(self, url, headers=None):
                class Ctx:
                    async def __aenter__(self_inner):
                        return _FakeResponse(session_token="")

                    async def __aexit__(self_inner, *exc_info):
                        return False
                return Ctx()

        def fake_ws_connect(url):
            raise AssertionError("should never connect without a session token")

        result = await abc.run_order(
            BrokenHttp(), fake_ws_connect,
            backend="python", base_url="https://python.example", persona_id=PERSONA_ID,
            model_id="gpt-realtime-2.1-mini", rep=0, access_token="entra-tok", timeout_s=0.2,
        )
        self.assertIsNotNone(result.error)


# ── Correctness comparison (OrderRunResult.tool_correct) ────────────────────

class ToolCorrectnessTests(unittest.TestCase):
    def _r(self, **kw):
        base = dict(backend="python", persona_id=PERSONA_ID, model_id="gpt-realtime-2.1-mini", rep=0, cold_start_s=0.1)
        base.update(kw)
        return abc.OrderRunResult(**base)

    def test_priced_ticket_without_error_is_correct(self):
        item = abc.ExpectedItem(item="Test Item", size="Standard", quantity=1)
        self.assertTrue(self._r(ticket_items=[item], ticket_total=4.29).tool_correct)

    def test_empty_ticket_is_incorrect(self):
        self.assertFalse(self._r(ticket_items=[], ticket_total=0.0).tool_correct)

    def test_missing_total_is_incorrect(self):
        item = abc.ExpectedItem(item="Test Item", size="Standard", quantity=1)
        self.assertFalse(self._r(ticket_items=[item], ticket_total=None).tool_correct)

    def test_run_with_error_is_never_correct(self):
        self.assertFalse(self._r(error="boom").tool_correct)

# ── Metrics parsing (az monitor metrics list -o json) ───────────────────────

class MetricsParsingTests(unittest.TestCase):
    def test_parses_average_cpu_and_memory(self):
        payload = {
            "value": [
                {"name": {"value": "UsageNanoCores"}, "timeseries": [{"data": [{"average": 100.0}, {"average": 200.0}]}]},
                {"name": {"value": "WorkingSetBytes"}, "timeseries": [{"data": [{"average": 1e6}]}]},
            ]
        }
        metrics = abc._parse_metrics_payload(payload)
        self.assertAlmostEqual(metrics.avg_cpu_nanocores, 150.0)
        self.assertAlmostEqual(metrics.avg_memory_bytes, 1e6)

    def test_empty_payload_yields_none(self):
        metrics = abc._parse_metrics_payload({"value": []})
        self.assertIsNone(metrics.avg_cpu_nanocores)
        self.assertIsNone(metrics.avg_memory_bytes)

    def test_fetch_container_app_metrics_uses_injected_runner(self):
        calls = []

        def fake_runner(cmd, **kwargs):
            calls.append(cmd)
            return FakeCompletedProcess(stdout=json.dumps({"value": []}), stderr="")

        metrics = abc.fetch_container_app_metrics(
            "/subscriptions/x/resourceGroups/y/providers/.../containerApps/z",
            "2026-01-01T00:00:00Z", "2026-01-01T01:00:00Z", runner=fake_runner,
        )
        self.assertEqual(len(calls), 1)
        self.assertTrue(os.path.basename(calls[0][0]).lower().startswith("az"))
        self.assertIsNone(metrics.avg_cpu_nanocores)


# ── Report building / rendering ──────────────────────────────────────────

class ReportTests(unittest.TestCase):
    def _result(self, backend, total, error=None, items=None):
        return abc.OrderRunResult(
            backend=backend, persona_id=PERSONA_ID, model_id="gpt-realtime-2.1-mini", rep=0,
            cold_start_s=0.5,
            turns=[abc.TurnResult(guest_text="hi", first_audio_latency_s=0.3, total_turn_time_s=1.2)],
            ticket_items=items if items is not None else [abc.ExpectedItem(item="Test Item", size="Standard", quantity=1)],
            ticket_total=total, error=error,
        )

    def test_build_report_groups_by_persona_backend_model(self):
        results = [self._result("python", 4.29), self._result("dotnet", 4.29, error="boom")]
        report = abc.build_report(results)
        self.assertEqual(len(report["latency"]), 2)
        self.assertEqual(len(report["correctness"]), 2)
        by_backend = {row["backend"]: row for row in report["correctness"]}
        self.assertEqual(by_backend["python"]["correct"], 1)
        self.assertEqual(by_backend["dotnet"]["correct"], 0)
        self.assertEqual(by_backend["dotnet"]["error_messages"], ["boom"])

    def test_parity_matches_identical_tickets_across_backends(self):
        report = abc.build_report([self._result("python", 4.29), self._result("dotnet", 4.29)])
        self.assertEqual(report["parity"], [{"persona": PERSONA_ID, "model": "gpt-realtime-2.1-mini", "match": True,
                                             "ticket_total": 4.29, "python_tickets": 1, "dotnet_tickets": 1}])

    def test_parity_flags_different_tickets(self):
        report = abc.build_report([self._result("python", 4.29), self._result("dotnet", 5.29)])
        self.assertFalse(report["parity"][0]["match"])
    def test_render_markdown_includes_both_tables(self):
        expected_total = abc.load_expected_ticket(PERSONA_ID).total
        report = abc.build_report([self._result("python", expected_total)])
        md = abc.render_markdown(report)
        self.assertIn("## Latency", md)
        self.assertIn("## Correctness matrix", md)
        self.assertIn(PERSONA_ID, md)

    def test_render_markdown_includes_resource_usage_when_present(self):
        expected_total = abc.load_expected_ticket(PERSONA_ID).total
        report = abc.build_report(
            [self._result("python", expected_total)],
            metrics={"python": abc.ResourceMetrics(avg_cpu_nanocores=250_000_000.0, avg_memory_bytes=1.5e8)},
        )
        md = abc.render_markdown(report)
        self.assertIn("## Resource usage", md)
        self.assertIn("python", md)


if __name__ == "__main__":
    unittest.main()
