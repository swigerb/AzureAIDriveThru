"""Tests for issue #75 (P2-6): model catalog, processor interface, per-session realtime
model selection in the drive-thru voice ordering backend.

Scope covered here (design doc section 7):
  * `/realtime?model=<id>` resolved once, before the WebSocket upgrade, against
    `RTMiddleTier.resolve_model()`: an unknown id, a persona-disallowed id, and an
    id catalogued but not (yet) deployed all get a plain HTTP 404 -- never a silent
    fallback -- exactly like an unknown/disabled `?persona=` already does;
  * omitted `?model=` is the persona's own default and is byte-for-byte unchanged
    from #75-era behaviour: the default deployment, no catalog lookup at all;
  * a valid, explicit, non-default model binds the whole session (id, deployment,
    reasoning flag) for its entire lifetime, and is reported in both
    `extension.session_metadata` and (indirectly) drives the upstream `?model=`
    query param used to connect;
  * `resume()` rejects a resume attempt naming a different model than the one the
    session was originally bound to ("model_mismatch"), mirroring the existing
    `persona_mismatch` check;
  * `reasoning` is sent to the upstream only for the catalog's own reasoning models
    -- never for the default/no-override path, which keeps using the deployment-name
    heuristic exactly as it always has;
  * `/api/personas/{id}`'s `models` block narrows each pipeline's `allowed` list to
    what's actually selectable (catalog ∩ deployment ∩ persona-allowed) when a
    `ModelCatalog` is supplied, but stays unfiltered (today's shape) when it isn't.

Reuses the same TEST-ONLY fixture persona pack as test_persona_binding.py
(tests/fixtures/personas/{test-alpha,test-beta}) and the `_RealtimeHarness` from
test_session_bootstrap.py, so these tests drive the real `RTMiddleTier._websocket_handler`
end to end, not just the unit pieces in test_model_catalog.py/test_processors.py.
"""

import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from test_persona_binding import _load_fixture_catalog
from test_session_bootstrap import BROWSER_SESSION_UPDATE, _RealtimeHarness

from model_catalog import ModelCatalog
from order_state import order_state_singleton
from session_manager import SessionManager

_CATALOG_CFG = {
    "models": {
        "catalog": [
            {"id": "gpt-realtime-2.1", "pipeline": "realtime", "label": "GPT Realtime 2.1", "reasoning": True},
            {"id": "gpt-realtime-mini", "pipeline": "realtime", "label": "GPT Realtime mini", "reasoning": False},
            {"id": "gpt-5-mini", "pipeline": "cascade", "label": "GPT-5 mini", "toolCalling": True},
        ]
    }
}


def _catalog(deployments: str | None = None) -> ModelCatalog:
    env = {"AZURE_AI_MODEL_DEPLOYMENTS": deployments} if deployments else {}
    return ModelCatalog.load(config=_CATALOG_CFG, environ=env)


def _ws():
    ws = MagicMock()
    ws.close = AsyncMock()
    return ws


# ═══════════════════════════════════════════════════════════════════════════════
# /realtime?model= WEBSOCKET-HANDLER-LEVEL VALIDATION (design doc 5.2/7.3)
# ═══════════════════════════════════════════════════════════════════════════════

class ModelWebSocketHandlerTests(_RealtimeHarness):
    """Drives the real RTMiddleTier._websocket_handler / _forward_messages through
    aiohttp's TestClient, with persona_catalog + model_catalog set to fixtures --
    proves the end-to-end wiring, not just resolve_realtime_model() in isolation."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.catalog = _load_fixture_catalog(default="test-alpha")
        self.rtmt.persona_catalog = self.catalog
        self.rtmt.persona_prompt_loaders = {}
        self.rtmt.allowed_voices = frozenset({"marin", "cedar", "shimmer"})
        # test-alpha's fixture allows gpt-realtime-2.1 (its own default) AND
        # gpt-realtime-mini; only the latter is deployed here, so it is the one
        # non-default, actually-selectable model these tests exercise.
        self.rtmt.model_catalog = _catalog('{"gpt-realtime-mini": "mini-deployment-42"}')

    async def test_unknown_model_is_rejected_with_404_before_the_ws_upgrade(self):
        resp = await self.client.get("/realtime", params={"model": "totally-made-up"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0,
                          "a rejected model must never create a session")

    async def test_persona_disallowed_model_is_rejected_with_404(self):
        """gpt-5-mini is catalogued (for the cascade pipeline) but not in either
        fixture persona's realtime `allowed` list -- must be rejected exactly like an
        unknown one, persona-scoped regardless of what the catalog itself contains."""
        resp = await self.client.get("/realtime", params={"persona": "test-beta", "model": "gpt-5-mini"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0)

    async def test_catalogued_but_undeployed_model_is_rejected_with_404(self):
        """Allowed by the persona and catalogued, but AZURE_AI_MODEL_DEPLOYMENTS doesn't
        map it in this test's catalog -- must still be rejected, never silently fall back
        to the default deployment."""
        self.rtmt.model_catalog = _catalog()  # no deployments at all
        resp = await self.client.get("/realtime", params={"model": "gpt-realtime-mini"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0)

    async def test_omitted_model_falls_back_to_the_persona_default_unchanged(self):
        """#75 acceptance: "keep behavior identical when no model param is given" --
        the omitted-?model= path must resolve to the harness's own configured default
        deployment ("gpt-realtime-test"), never anything catalog-derived (resolve_model's
        default branch never touches the catalog at all)."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_model_id(sid), "gpt-realtime-2.1")
        self.assertEqual(order_state_singleton.get_model_deployment(sid), "gpt-realtime-test")
        self.assertIsNone(order_state_singleton.get_model_reasoning(sid))
        await browser.close()

    async def test_explicit_non_default_model_binds_the_whole_session(self):
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_model_id(sid), "gpt-realtime-mini")
        self.assertEqual(order_state_singleton.get_model_deployment(sid), "mini-deployment-42")
        self.assertEqual(order_state_singleton.get_model_reasoning(sid), False)
        await browser.close()

    async def test_session_metadata_carries_the_bound_model_id(self):
        """Design doc 5.2/7.5: extension.session_metadata reports the bound model,
        alongside the persona it already reports."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        events = await self._browser_events(browser, duration=0.5)
        meta = next(e for e in events if e.get("type") == "extension.session_metadata")
        self.assertEqual(meta["model"], "gpt-realtime-mini")
        self.assertEqual(meta["persona"], "test-alpha")
        await browser.close()

        default_browser = await self.client.ws_connect("/realtime?persona=test-alpha")
        await default_browser.send_json(BROWSER_SESSION_UPDATE)
        default_events = await self._browser_events(default_browser, duration=0.5)
        default_meta = next(e for e in default_events if e.get("type") == "extension.session_metadata")
        self.assertEqual(default_meta["model"], "gpt-realtime-2.1")
        await default_browser.close()

    async def test_explicit_model_selects_its_own_upstream_deployment(self):
        """The chosen deployment must go into the upstream ?model= query param (design
        doc 7.4) -- not just session metadata. The fake GA server's own URL already
        pins "gpt-realtime-test" as the harness's configured default deployment; a
        session bound to gpt-realtime-mini must instead connect upstream with
        model=mini-deployment-42."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await self._until(lambda: len(self.fake.connect_model_params) >= 1)
        self.assertEqual(self.fake.connect_model_params[-1], "mini-deployment-42")
        await browser.close()

    async def test_reasoning_is_not_sent_for_an_explicit_non_reasoning_model(self):
        """Design doc 7.5: `reasoning` in the bootstrap session.update must be absent
        for an explicitly-selected model the catalog marks reasoning=False
        (gpt-realtime-mini), even though it's a NON-default model for test-alpha (whose
        own default, gpt-realtime-2.1, is itself a reasoning model in the catalog --
        proving this isn't just "the default path never sends reasoning either)."""
        self.rtmt.model_catalog = _catalog(
            '{"gpt-realtime-mini": "mini-deployment-42", "gpt-realtime-2.1": "reasoning-deployment"}'
        )
        # reasoning_effort configured so this actually exercises the override (=False)
        # winning over "reasoning is switched on" -- not just the effort gate itself.
        self.rtmt.reasoning_effort = "low"
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        bootstrap = next(u for u in self._session_updates() if "audio" in u.get("session", {}))
        self.assertNotIn("reasoning", bootstrap["session"])
        await browser.close()

    async def test_reasoning_is_sent_for_an_explicit_reasoning_model(self):
        """The other half of the pair above: test-beta's default is gpt-realtime-mini
        (reasoning=False); explicitly requesting its other allowed model,
        gpt-realtime-2.1 (reasoning=True in the catalog), must carry `reasoning` in the
        bootstrap session.update."""
        self.rtmt.model_catalog = _catalog(
            '{"gpt-realtime-mini": "mini-deployment-42", "gpt-realtime-2.1": "reasoning-deployment"}'
        )
        # reasoning_effort must be configured (as a real deployment's config.yaml
        # model.reasoning_effort would be) for `reasoning_enabled()`'s own gate to pass
        # at all -- reasoning_override alone only decides WHICH models get it once
        # reasoning is switched on in the first place.
        self.rtmt.reasoning_effort = "low"
        browser = await self.client.ws_connect("/realtime?persona=test-beta&model=gpt-realtime-2.1")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        bootstrap = next(u for u in self._session_updates() if "audio" in u.get("session", {}))
        self.assertIn("reasoning", bootstrap["session"])
        await browser.close()


class ModelResumeMismatchTests(unittest.TestCase):
    """SessionManager.resume()'s own model guard, exercised directly (no WS transport
    needed -- resume() only needs a resume id and a mock socket), mirroring
    ResumePersonaMismatchTests in test_persona_binding.py exactly."""

    def setUp(self):
        self.catalog = _load_fixture_catalog()
        self.sm = SessionManager()
        self.addCleanup(self._end_all)

    def _end_all(self):
        for sid in list(order_state_singleton.sessions):
            self.sm.end_session(sid, "test teardown")

    def test_resume_with_the_same_model_is_accepted(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha, model_id="gpt-realtime-mini",
                                      model_deployment="mini-deployment-42", model_reasoning=False)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha",
                                  requested_model_id="gpt-realtime-mini")
        self.assertTrue(outcome.accepted)
        self.assertEqual(outcome.session_id, sid)

    def test_resume_with_a_different_model_is_rejected(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha, model_id="gpt-realtime-mini",
                                      model_deployment="mini-deployment-42", model_reasoning=False)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha",
                                  requested_model_id="gpt-realtime-2.1")
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "model_mismatch")
        # Rejected BEFORE the presented resume id was consumed -- a legitimate retry
        # against the correct model still works.
        self.assertIn(sid, order_state_singleton.sessions)

    def test_resume_with_no_model_requested_matches_a_default_bound_session(self):
        alpha = self.catalog.get("test-alpha")  # binds to test-alpha's own default model
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha", requested_model_id=None)
        self.assertTrue(outcome.accepted)

    def test_resume_with_no_model_requested_mismatches_a_non_default_bound_session(self):
        """requested_model_id=None resolves to the bound persona's own default model, so
        a session explicitly bound to some OTHER model is correctly rejected."""
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha, model_id="gpt-realtime-mini",
                                      model_deployment="mini-deployment-42", model_reasoning=False)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha", requested_model_id=None)
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "model_mismatch")


# ═══════════════════════════════════════════════════════════════════════════════
# /api/personas/{id} SELECTABLE-SET FILTERING (design doc 7.3)
# ═══════════════════════════════════════════════════════════════════════════════

class ApiPersonasModelFilteringTests(unittest.TestCase):
    """app.py's _model_pipelines_body()/_persona_detail_body(), exercised directly
    with an explicit ModelCatalog -- the opt-in filtering path create_app() wires up,
    left untouched (model_catalog=None) by every pre-existing call/test."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import _persona_detail_body
        self.catalog = _load_fixture_catalog()
        self._detail_body = _persona_detail_body

    def test_unfiltered_when_no_catalog_is_given_matches_todays_shape(self):
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(persona)
        self.assertEqual(sorted(detail["models"]["realtime"]["allowed"]), ["gpt-realtime-2.1", "gpt-realtime-mini"])

    def test_undeployed_non_default_model_is_dropped_when_catalog_given(self):
        """gpt-realtime-mini is persona-allowed but has no deployment mapped here --
        it must be dropped from the selectable set, while the persona's own default
        (gpt-realtime-2.1) is always kept even though it's not catalogued at all."""
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(persona, _catalog())  # no deployments
        self.assertEqual(detail["models"]["realtime"]["allowed"], ["gpt-realtime-2.1"])

    def test_deployed_non_default_model_is_kept_when_catalog_given(self):
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(persona, _catalog('{"gpt-realtime-mini": "mini-deployment-42"}'))
        self.assertEqual(sorted(detail["models"]["realtime"]["allowed"]), ["gpt-realtime-2.1", "gpt-realtime-mini"])


if __name__ == "__main__":
    unittest.main()
