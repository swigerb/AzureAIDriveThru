"""Tests for issue #74 (P2-5): per-session persona binding in the Python backend.

Scope covered here:
  * a TEST-ONLY fixture persona pack (tests/fixtures/personas/{test-alpha,test-beta}) --
    NOT the real personas/ directory, since only the deployment's single default persona
    pack exists there today -- proves
    multi-persona isolation is real, not just plausible from reading the code;
  * /realtime?persona=<id> binding: unknown id -> 404 before the WS upgrade, omitted
    falls back to the deployment default, a valid id binds the whole session (persona
    id, voice, menu/search index) for its entire lifetime;
  * resume() rejects a session resuming under any persona other than the one it was
    originally bound to ("persona_mismatch"), design doc section 5.1/5.2;
  * /api/personas and /api/personas/{id} response shapes match design doc section 5.2;
  * no mid-conversation persona switching: OrderState exposes no setter for an existing
    session's persona id.

Rick's #92 note ("remove the hardcoded PromptLoader with a fixed brand argument") and the
#97 fold-in
(explicit per-session event-loop confinement for OrderState) are covered by
test_app.py / test_performance.py respectively -- not duplicated here.
"""

import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from test_session_bootstrap import BROWSER_SESSION_UPDATE, _RealtimeHarness

import default_persona
import menu_utils
from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from session_manager import SessionManager

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _load_fixture_catalog(default: str = "test-alpha") -> PersonaCatalog:
    """The TEST-ONLY fixture pack: two personas with genuinely distinct search index
    names, voices, tax rates, happy-hour config (test-beta has none, decision 5) and
    menu items -- built once per test rather than module-global so tests can't leak
    the (module-level) MenuCatalog cache into each other in surprising ways."""
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-alpha", "test-beta"],
        default_persona_id=default,
    )


def _ws():
    ws = MagicMock()
    ws.close = AsyncMock()
    return ws


# ═══════════════════════════════════════════════════════════════════════════════
# FIXTURE PACK SANITY
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaCatalogFixtureTests(unittest.TestCase):
    """The fixture pack itself loads and the two personas are genuinely distinct --
    if this fails, every other test in this file is testing nothing."""

    def test_fixture_catalog_loads_both_personas(self):
        catalog = _load_fixture_catalog()
        self.assertEqual(catalog.ids, ["test-alpha", "test-beta"])
        self.assertEqual(catalog.default_persona_id, "test-alpha")

    def test_fixture_personas_have_different_search_index_and_voice(self):
        catalog = _load_fixture_catalog()
        alpha, beta = catalog.get("test-alpha"), catalog.get("test-beta")
        self.assertNotEqual(alpha.manifest.search.indexName, beta.manifest.search.indexName)
        self.assertNotEqual(alpha.manifest.voice.default, beta.manifest.voice.default)

    def test_fixture_beta_has_no_happy_hour_alpha_does(self):
        """Decision 5: `pricing.happyHour: null` means none, per persona."""
        catalog = _load_fixture_catalog()
        self.assertIsNone(catalog.get("test-beta").manifest.pricing.happyHour)
        self.assertIsNotNone(catalog.get("test-alpha").manifest.pricing.happyHour)

    def test_unknown_persona_id_is_absent_from_the_catalog(self):
        catalog = _load_fixture_catalog()
        self.assertNotIn("does-not-exist", catalog)


# ═══════════════════════════════════════════════════════════════════════════════
# MENU / CATALOG ISOLATION
# ═══════════════════════════════════════════════════════════════════════════════

class MenuCatalogIsolationTests(unittest.TestCase):
    """#74: each persona's menu comes from its OWN bound MenuCatalog -- one persona's
    session can never resolve, price, or categorize another persona's items."""

    def setUp(self):
        catalog = _load_fixture_catalog()
        self.alpha = catalog.get("test-alpha")
        self.beta = catalog.get("test-beta")

    def test_alpha_menu_resolves_its_own_item_and_price(self):
        mc = menu_utils.get_catalog_for_persona(self.alpha)
        item = mc.resolve_menu_item("Alpha Cola")
        self.assertIsNotNone(item)
        self.assertEqual(item["category"], "drinks")
        self.assertEqual(mc.price_for("Alpha Cola", "small"), 1.99)

    def test_beta_menu_does_not_resolve_alphas_item(self):
        mc = menu_utils.get_catalog_for_persona(self.beta)
        self.assertIsNone(mc.resolve_menu_item("Alpha Cola"))

    def test_alpha_menu_does_not_resolve_betas_item(self):
        mc = menu_utils.get_catalog_for_persona(self.alpha)
        self.assertIsNone(mc.resolve_menu_item("Beta Root Beer"))

    def test_alpha_and_beta_use_their_own_size_vocabularies(self):
        mc_alpha = menu_utils.get_catalog_for_persona(self.alpha)
        mc_beta = menu_utils.get_catalog_for_persona(self.beta)
        self.assertEqual(mc_alpha.normalize_size("s"), "Small")
        self.assertEqual(mc_beta.normalize_size("reg"), "Regular")


# ═══════════════════════════════════════════════════════════════════════════════
# ORDER_STATE PERSONA BINDING
# ═══════════════════════════════════════════════════════════════════════════════

class OrderStatePersonaBindingTests(unittest.TestCase):
    """#74: order_state.py sessions bound to different personas get their own menu
    catalog, happy-hour behavior, and persona id -- no cross-session leakage through
    module globals (the whole point of removing the brand-specific module-level singletons)."""

    def setUp(self):
        catalog = _load_fixture_catalog()
        self.alpha = catalog.get("test-alpha")
        self.beta = catalog.get("test-beta")
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self, persona=None) -> str:
        sid = order_state_singleton.create_session(persona=persona)
        self._sessions_created.append(sid)
        return sid

    def test_two_sessions_get_their_own_bound_persona_id(self):
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        self.assertEqual(order_state_singleton.get_persona_id(sid_a), "test-alpha")
        self.assertEqual(order_state_singleton.get_persona_id(sid_b), "test-beta")

    def test_two_sessions_get_different_menu_catalogs(self):
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        menu_a = order_state_singleton.get_menu_catalog(sid_a)
        menu_b = order_state_singleton.get_menu_catalog(sid_b)
        self.assertIsNotNone(menu_a.resolve_menu_item("Alpha Cola"))
        self.assertIsNone(menu_b.resolve_menu_item("Alpha Cola"))

    def test_beta_session_never_reports_happy_hour(self):
        sid_b = self._new_session(self.beta)
        self.assertFalse(order_state_singleton.is_happy_hour_for_session(sid_b))

    def test_default_bound_session_binds_to_the_real_default_persona(self):
        """No persona argument at all -- the deployment default binding path (#74, Rick's PR
        #102 review item 2) -- resolves to a REAL persona id (the deployment's own default),
        never ``None``: there is no more unbound-session state to be "exactly as
        before" about -- every session, including this one, is bound through the identical
        mandatory-catalog path."""
        sid = self._new_session()
        self.assertEqual(
            order_state_singleton.get_persona_id(sid),
            default_persona.get_default_persona().id,
        )


class NoMidConversationPersonaSwitchTests(unittest.TestCase):
    """Design decision (5.1): no mid-conversation persona switching, ever -- not even
    across a transport drop/resume (see ResumePersonaMismatchTests below for that
    vector). This test guards the OTHER vector: there must be no public API at all
    that could rebind an existing session's persona once created."""

    def test_order_state_exposes_no_persona_rebind_method(self):
        self.assertFalse(
            hasattr(order_state_singleton, "set_persona_id"),
            "a persona-rebind setter on OrderState would allow mid-conversation "
            "persona switching, which #74's design explicitly forbids",
        )

    def test_persona_id_is_never_reassigned_by_normal_session_activity(self):
        catalog = _load_fixture_catalog()
        alpha = catalog.get("test-alpha")
        sid = order_state_singleton.create_session(persona=alpha)
        try:
            self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
            order_state_singleton.get_menu_catalog(sid)
            order_state_singleton.is_happy_hour_for_session(sid)
            self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
        finally:
            order_state_singleton.delete_session(sid)


# ═══════════════════════════════════════════════════════════════════════════════
# RESUME PERSONA MISMATCH (design doc 5.1/5.2, resume() docstring)
# ═══════════════════════════════════════════════════════════════════════════════

class ResumePersonaMismatchTests(unittest.TestCase):
    """SessionManager.resume()'s own persona guard, exercised directly (no WS
    transport needed -- resume() only needs a resume id and a mock socket)."""

    def setUp(self):
        self.catalog = _load_fixture_catalog()
        self.sm = SessionManager()
        self.addCleanup(self._end_all)

    def _end_all(self):
        for sid in list(order_state_singleton.sessions):
            self.sm.end_session(sid, "test teardown")

    def test_resume_with_the_same_persona_is_accepted(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha")
        self.assertTrue(outcome.accepted)
        self.assertEqual(outcome.session_id, sid)

    def test_resume_with_a_different_persona_is_rejected(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-beta")
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")
        # Rejected BEFORE the presented resume id was consumed -- the guest's real
        # credential (retried against the correct persona) still works.
        self.assertIn(sid, order_state_singleton.sessions)

    def test_resume_of_a_default_bound_session_requesting_a_persona_is_rejected(self):
        """Symmetric case: a session bound to the real deployment default (no persona arg,
        #74's mandatory-catalog path) can't be "claimed" into a different persona via resume
        either -- the mismatch check is unconditional now, not skipped for default-bound
        sessions."""
        sid = self.sm.create_session(_ws())   # binds to the real default persona
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha")
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")

    def test_resume_with_no_persona_requested_matches_a_default_bound_session(self):
        """requested_persona_id=None means "the caller means the deployment default"
        (#74/Rick's review item 2 -- the mismatch check is unconditional, never skipped) --
        a session itself bound to that same real default therefore matches."""
        sid = self.sm.create_session(_ws())   # binds to the real default persona
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id=None)
        self.assertTrue(outcome.accepted)

    def test_resume_with_no_persona_requested_mismatches_a_non_default_bound_session(self):
        """requested_persona_id=None resolves to the real deployment default, so a session
        bound to some OTHER persona (here the fixture's "test-alpha", not the real default)
        is correctly rejected as a mismatch -- there is no more "skip the check" state."""
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id=None)
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")


# ═══════════════════════════════════════════════════════════════════════════════
# /api/personas AND /api/personas/{id} WIRE SHAPE (design doc 5.2)
# ═══════════════════════════════════════════════════════════════════════════════

class ApiPersonasResponseShapeTests(unittest.TestCase):
    """app.py's pure response-body builders, exercised directly against the fixture
    catalog (avoids needing a full create_app() with real Azure OpenAI/Search env)."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import (
            _persona_detail_body,
            _persona_summary_body,
            _personas_index_body,
        )
        self.catalog = _load_fixture_catalog()
        self._index_body = _personas_index_body
        self._detail_body = _persona_detail_body
        self._summary_body = _persona_summary_body

    def test_personas_index_lists_default_and_both_personas(self):
        body = self._index_body(self.catalog)
        self.assertEqual(body["default"], "test-alpha")
        ids = [p["id"] for p in body["personas"]]
        self.assertEqual(ids, ["test-alpha", "test-beta"])
        self.assertEqual(body["backends"], [{"id": "python", "url": "/realtime"}])

    def test_persona_summary_has_the_designed_fields(self):
        summary = self._summary_body(self.catalog.get("test-alpha"))
        self.assertEqual(summary["id"], "test-alpha")
        self.assertEqual(summary["displayName"], "Test Alpha Drive-In")
        self.assertEqual(summary["logoUrl"], "/personas/test-alpha/assets/assets/logo.svg")
        self.assertIn("light", summary["theme"])

    def test_persona_detail_has_voice_locales_features_menu_and_models(self):
        detail = self._detail_body(self.catalog.get("test-beta"))
        self.assertEqual(detail["id"], "test-beta")
        self.assertEqual(detail["voice"], {"default": "cedar"})
        self.assertEqual(detail["locales"]["default"], "en")
        self.assertEqual(detail["features"], {"dayparts": False})
        self.assertEqual(detail["menuUrl"], "/personas/test-beta/menu.json")
        self.assertEqual(detail["models"]["realtime"]["default"], "gpt-realtime-mini")
        self.assertNotIn("cascade", detail["models"])
        self.assertNotIn("local", detail["models"])


# ═══════════════════════════════════════════════════════════════════════════════
# END-TO-END /realtime?persona=<id> (real middle tier, fake GA realtime backend)
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaWebSocketHandlerTests(_RealtimeHarness):
    """Drives the real RTMiddleTier._websocket_handler / _forward_messages through
    aiohttp's TestClient, with persona_catalog set to the fixture pack -- proves the
    end-to-end wiring, not just the unit pieces above."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.catalog = _load_fixture_catalog(default="test-alpha")
        self.rtmt.persona_catalog = self.catalog
        self.rtmt.persona_prompt_loaders = {}
        # The fixture personas' voices (marin, cedar) must be allow-listed like any
        # real deployment's model.allowed_voices would list them.
        self.rtmt.allowed_voices = frozenset({"marin", "cedar", "shimmer"})

    async def test_unknown_persona_is_rejected_with_404_before_the_ws_upgrade(self):
        resp = await self.client.get("/realtime", params={"persona": "does-not-exist"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0,
                          "a rejected persona must never create a session")

    async def test_valid_persona_binds_the_whole_session(self):
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_persona_id(sid), "test-beta")
        await browser.close()

    async def test_omitted_persona_falls_back_to_the_deployment_default(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
        await browser.close()

    async def test_bound_persona_voice_flows_into_the_bootstrap_session_update(self):
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        voices = [
            u["session"]["audio"]["output"]["voice"]
            for u in self._session_updates()
            if "audio" in u.get("session", {})
        ]
        self.assertIn("cedar", voices)
        self.assertNotIn("marin", voices, "test-beta's session must never pick up test-alpha's voice")
        await browser.close()

    async def test_two_concurrent_sessions_each_keep_their_own_persona(self):
        """The isolation guarantee end to end: two simultaneous connections, one per
        persona, must never cross-contaminate."""
        browser_a = await self.client.ws_connect("/realtime?persona=test-alpha")
        browser_b = await self.client.ws_connect("/realtime?persona=test-beta")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 2)
        persona_ids = {
            order_state_singleton.get_persona_id(sid)
            for sid in self.rtmt._sessions._session_map.values()
        }
        self.assertEqual(persona_ids, {"test-alpha", "test-beta"})
        await browser_a.close()
        await browser_b.close()


if __name__ == "__main__":
    unittest.main()
