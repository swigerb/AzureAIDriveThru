"""Tests for issue #75 (P2-6): model catalog, processor interface, per-session realtime
model selection in the drive-thru voice ordering backend (revised per Rick's PR #106
review).

Scope covered here (design doc section 7):
  * `/realtime?model=<id>` resolved once, before the WebSocket upgrade, via
    `processors.dispatch_processor` + `processor.resolve_model()`: an unknown id, a
    persona-disallowed id, and an id catalogued but not (yet) deployed all get a plain
    HTTP 404 -- never a silent fallback -- exactly like an unknown/disabled `?persona=`
    already does;
  * Rick's PR #106 review item 1 (NO default-path special case): an omitted `?model=`
    resolves to the persona's own default, but that default goes through EXACTLY the
    same catalog check as any other id -- its `reasoning` flag is the catalog's own, and
    its deployment only falls back to the harness's configured default deployment when
    `AZURE_AI_MODEL_DEPLOYMENTS` doesn't map it (never a "the default skips the catalog
    entirely" shortcut);
  * a valid, explicit, non-default model binds the whole session (id, deployment,
    pipeline, reasoning flag) for its entire lifetime, and is reported in both
    `extension.session_metadata` and (indirectly) drives the upstream `?model=`
    query param used to connect;
  * `resume()` rejects a resume attempt naming a different model than the one the
    session was originally bound to ("model_mismatch"), mirroring the existing
    `persona_mismatch` check, in BOTH directions (an explicit mismatch, and an omitted
    `?model=` that resolves to a default which doesn't match a non-default-bound session);
  * `reasoning` is sent to the upstream only for the catalog's own reasoning models --
    including the default, now that item 1 removed its special case;
  * `/api/personas/{id}`'s `models` block ALWAYS narrows each pipeline's `allowed` list to
    what's actually selectable (catalog ∩ deployment ∩ persona-allowed), shaped as
    `{id, label, reasoning}` per entry (Rick's PR #106 review item 3) -- there is no more
    unfiltered fallback shape, and no more "the default is always kept" carve-out.

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
from processors import ProcessorRegistry
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
    proves the end-to-end wiring, not just resolve_realtime_model()/dispatch_processor()
    in isolation."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.catalog = _load_fixture_catalog(default="test-alpha")
        self.rtmt.persona_catalog = self.catalog
        self.rtmt.persona_prompt_loaders = {}
        self.rtmt.allowed_voices = frozenset({"marin", "cedar", "shimmer"})
        # test-alpha's fixture allows gpt-realtime-2.1 (its own default) AND
        # gpt-realtime-mini; only the latter is deployed here, so it is the one
        # non-default, actually-selectable model these tests exercise. gpt-realtime-2.1
        # (the default) is deliberately left undeployed too, to exercise item 1's
        # deployment-fallback branch for the default.
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

    async def test_wrong_pipeline_model_is_rejected_with_404_even_if_persona_allowed_it(self):
        """The processor-seam guard reaches all the way to the WS handler: gpt-5-mini is
        catalogued only for `cascade`, so requesting it against `/realtime` must 404 even
        if a (misconfigured) persona listed it in its own `realtime.allowed`."""
        self.catalog.get("test-beta").manifest.models.realtime.allowed.append("gpt-5-mini")
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

    async def test_default_model_not_catalogued_is_rejected_with_404(self):
        """Rick's PR #106 review item 1's mutation check, exercised end-to-end: a persona
        whose default isn't catalogued at all must 404 the same as any other model --
        `ModelCatalog.validate_persona_defaults` is what stops this shape from ever
        reaching a real deployment (fails startup instead), but the request path itself
        must independently refuse it too, with no silent bypass."""
        self.catalog.get("test-alpha").manifest.models.realtime.default = "not-catalogued-at-all"
        self.catalog.get("test-alpha").manifest.models.realtime.allowed.append("not-catalogued-at-all")
        resp = await self.client.get("/realtime", params={"persona": "test-alpha"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0)

    async def test_omitted_model_falls_back_to_the_persona_default_via_the_catalog(self):
        """Rick's PR #106 review item 1: the omitted-?model= path resolves the persona's
        own default through the SAME catalog check as any other id now -- its deployment
        falls back to the harness's own configured default deployment
        ("gpt-realtime-test") only because AZURE_AI_MODEL_DEPLOYMENTS doesn't map it here,
        but its `reasoning` flag is unconditionally the catalog's own (gpt-realtime-2.1 is
        a reasoning model), never None/a deployment-name heuristic."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_model_id(sid), "gpt-realtime-2.1")
        self.assertEqual(order_state_singleton.get_model_deployment(sid), "gpt-realtime-test")
        self.assertEqual(order_state_singleton.get_model_reasoning(sid), True)
        self.assertEqual(order_state_singleton.get_model_pipeline(sid), "realtime")
        await browser.close()

    async def test_explicit_non_default_model_binds_the_whole_session(self):
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_model_id(sid), "gpt-realtime-mini")
        self.assertEqual(order_state_singleton.get_model_deployment(sid), "mini-deployment-42")
        self.assertEqual(order_state_singleton.get_model_reasoning(sid), False)
        self.assertEqual(order_state_singleton.get_model_pipeline(sid), "realtime")
        await browser.close()

    async def test_session_metadata_carries_the_bound_model_id_and_pipeline(self):
        """Design doc 5.2/7.5, Rick's PR #106 review item 3: extension.session_metadata
        reports the bound model AND its pipeline, alongside the persona it already
        reports."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-mini")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        events = await self._browser_events(browser, duration=0.5)
        meta = next(e for e in events if e.get("type") == "extension.session_metadata")
        self.assertEqual(meta["model"], "gpt-realtime-mini")
        self.assertEqual(meta["persona"], "test-alpha")
        self.assertEqual(meta["pipeline"], "realtime")
        await browser.close()

        default_browser = await self.client.ws_connect("/realtime?persona=test-alpha")
        await default_browser.send_json(BROWSER_SESSION_UPDATE)
        default_events = await self._browser_events(default_browser, duration=0.5)
        default_meta = next(e for e in default_events if e.get("type") == "extension.session_metadata")
        self.assertEqual(default_meta["model"], "gpt-realtime-2.1")
        self.assertEqual(default_meta["pipeline"], "realtime")
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
        proving this isn't just "the default path never sends reasoning either")."""
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

    async def test_reasoning_is_sent_for_the_default_model_when_it_is_a_reasoning_model(self):
        """Rick's PR #106 review item 1, the flip side of the pair above: the OMITTED
        `?model=` path (test-alpha's own default, gpt-realtime-2.1, reasoning=True in
        the catalog) must ALSO carry `reasoning` -- proving the default is no longer a
        special case that skips the catalog's reasoning flag."""
        self.rtmt.reasoning_effort = "low"
        browser = await self.client.ws_connect("/realtime?persona=test-alpha")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        bootstrap = next(u for u in self._session_updates() if "audio" in u.get("session", {}))
        self.assertIn("reasoning", bootstrap["session"])
        await browser.close()


class CascadeDispatchNeverEntersRTMiddleTierTests(_RealtimeHarness):
    """Rick's PR #106 review item 5, exercised end to end (not just the
    `dispatch_processor` unit tests in test_processors.py::TestDispatchProcessor): a real
    RTMiddleTier, with a FAKE cascade processor registered alongside it, must route a
    cascade-catalogued `?model=` to that fake processor's own `resolve_model`/`handle` --
    and never call RTMiddleTier's own (unrelated) resolve_model/handle for it. This is
    also the mutation check ("cascade model routed to RTMiddleTier -> dispatch test
    fails"): if `_websocket_handler` were changed to skip `dispatch_processor` and always
    use `self`, this test starts failing because the fake is never reached."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.catalog = _load_fixture_catalog(default="test-alpha")
        self.rtmt.persona_catalog = self.catalog
        self.rtmt.persona_prompt_loaders = {}
        self.rtmt.allowed_voices = frozenset({"marin", "cedar", "shimmer"})
        self.rtmt.model_catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment", "gpt-5-mini": "cascade-deployment"}')
        # test-alpha doesn't allow gpt-5-mini by default -- add it so this test exercises
        # dispatch routing, not the (separately tested) persona-allow-list guard.
        self.catalog.get("test-alpha").manifest.models.realtime.allowed.append("gpt-5-mini")

        self.fake_cascade = _FakeCascadeProcessor()
        self.rtmt.processor_registry = ProcessorRegistry([self.rtmt, self.fake_cascade])

    async def test_cascade_model_reaches_the_fake_cascade_processor(self):
        resp = await self.client.get("/realtime", params={"persona": "test-alpha", "model": "gpt-5-mini"})
        # The fake cascade processor's `handle` returns a plain 200 text response
        # (see _FakeCascadeProcessor below) instead of upgrading to a WebSocket --
        # proof RTMiddleTier's own websocket/session machinery was never invoked for it.
        self.assertEqual(resp.status, 200)
        self.assertEqual(await resp.text(), "handled-by-fake-cascade-processor")
        self.assertEqual(len(self.fake_cascade.handle_calls), 1)
        _persona, resolved_model = self.fake_cascade.handle_calls[0]
        self.assertEqual(resolved_model.id, "gpt-5-mini")
        self.assertEqual(resolved_model.pipeline, "cascade")
        # RTMiddleTier's own realtime session machinery was never entered for it.
        self.assertEqual(self.rtmt._sessions.active_session_count, 0)

    async def test_realtime_model_still_reaches_rtmiddletier_unaffected(self):
        """Registering a second (cascade) processor must not disturb realtime's own
        dispatch -- a realtime model still goes to RTMiddleTier itself."""
        browser = await self.client.ws_connect("/realtime?persona=test-alpha&model=gpt-realtime-2.1")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        self.assertEqual(len(self.fake_cascade.handle_calls), 0)
        await browser.close()


class _FakeCascadeProcessor:
    """A minimal stand-in for a future #82 `CascadeProcessor` -- just enough of the
    `PipelineProcessor` shape to prove `dispatch_processor` reaches it, and that reaching
    it never touches RTMiddleTier's own websocket/session code at all."""

    pipeline_name = "cascade"

    def __init__(self):
        self.handle_calls: list = []

    def resolve_model(self, persona, requested_model_id):
        from processors import ResolvedModel

        return ResolvedModel(id=requested_model_id, pipeline="cascade", deployment="cascade-deployment", reasoning=False)

    async def handle(self, request, persona, resolved_model):
        from aiohttp import web

        self.handle_calls.append((persona, resolved_model))
        return web.Response(text="handled-by-fake-cascade-processor")


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
        """Regression guard for the bug Rick's own review surfaced: `resume()` must
        resolve the SESSION's own bound persona's default (test-alpha's), never the
        deployment-wide default persona's -- this only passed by accident before the
        fix when the two happened to be the same persona."""
        alpha = self.catalog.get("test-alpha")  # binds to test-alpha's own default model
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha", requested_model_id=None)
        self.assertTrue(outcome.accepted)

    def test_resume_with_no_model_requested_matches_a_non_default_bound_persona(self):
        """THE regression case for the bug: bound to test-beta (whose default is
        gpt-realtime-mini, NOT test-alpha's gpt-realtime-2.1) -- an omitted
        ?model= on resume must resolve against test-beta's OWN default, not whichever
        persona happens to be the deployment-wide default."""
        beta = self.catalog.get("test-beta")
        sid = self.sm.create_session(_ws(), persona=beta)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-beta", requested_model_id=None)
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
# /api/personas/{id} SELECTABLE-SET FILTERING (design doc 7.3, Rick's PR #106 review item 3)
# ═══════════════════════════════════════════════════════════════════════════════

class ApiPersonasModelFilteringTests(unittest.TestCase):
    """app.py's _model_pipelines_body()/_persona_detail_body(), exercised directly with an
    explicit ModelCatalog. Rick's PR #106 review item 3: model_catalog is now a REQUIRED
    argument -- there is no more unfiltered/"today's shape" fallback call, and no more
    "the default is always kept even if unselectable" carve-out."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import _persona_detail_body
        self.catalog = _load_fixture_catalog()
        self._detail_body = _persona_detail_body

    def test_model_catalog_argument_is_now_required(self):
        """Rick's PR #106 review item 3: there is no more "unfiltered when no catalog is
        given" shape -- calling without one is a programming error, not a supported
        fallback."""
        persona = self.catalog.get("test-alpha")
        with self.assertRaises(TypeError):
            self._detail_body(persona)

    def test_everything_is_dropped_including_the_default_when_nothing_is_deployed(self):
        """gpt-realtime-2.1 (test-alpha's own default) and gpt-realtime-mini are both
        catalogued and persona-allowed, but NEITHER is deployed here -- Rick's PR #106
        review item 1/3: the default gets NO carve-out any more, so it is dropped from
        the picker's list exactly like any other unselectable model, not "always kept"."""
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(persona, _catalog())  # no deployments
        self.assertEqual(detail["models"]["realtime"]["models"], [])

    def test_only_the_deployed_model_is_kept_even_though_the_default_is_not(self):
        """gpt-realtime-mini is deployed here but is NOT test-alpha's default
        (gpt-realtime-2.1 is); the default, still undeployed, is correctly dropped --
        proving selectability, not "is it the default", is what decides membership."""
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(persona, _catalog('{"gpt-realtime-mini": "mini-deployment-42"}'))
        self.assertEqual(
            detail["models"]["realtime"]["models"],
            [{"id": "gpt-realtime-mini", "label": "GPT Realtime mini", "reasoning": False}],
        )

    def test_both_models_are_kept_with_id_label_reasoning_shape_when_both_are_deployed(self):
        persona = self.catalog.get("test-alpha")
        detail = self._detail_body(
            persona,
            _catalog('{"gpt-realtime-2.1": "prod-deployment", "gpt-realtime-mini": "mini-deployment-42"}'),
        )
        self.assertEqual(
            detail["models"]["realtime"]["models"],
            [
                {"id": "gpt-realtime-2.1", "label": "GPT Realtime 2.1", "reasoning": True},
                {"id": "gpt-realtime-mini", "label": "GPT Realtime mini", "reasoning": False},
            ],
        )


if __name__ == "__main__":
    unittest.main()

