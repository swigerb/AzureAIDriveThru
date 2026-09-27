"""Tests for issue #74 (P2-5): per-session persona binding in the Python backend.

Scope covered here:
  * a TEST-ONLY fixture persona pack (tests/fixtures/personas/{test-alpha,test-beta}) --
    NOT the real personas/ directory, since only the deployment's single default persona
    pack exists there today -- proves multi-persona isolation is real, not just plausible
    from reading the code;
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

import asyncio
import os
import sys
import unittest
from datetime import datetime
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch
from zoneinfo import ZoneInfo

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from test_session_bootstrap import BROWSER_SESSION_UPDATE, _RealtimeHarness

import default_persona
import menu_utils
import tools
from order_state import order_state_singleton
from persona_loader import Persona, PersonaCatalog
from prompt_loader import PromptLoader
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


def _run(coro):
    return asyncio.run(coro)


def _make_mock_search_client(records):
    """Create a mock SearchClient that returns an async iterable of *records* -- same shape
    as test_tool_calling.py's helper of the same name, duplicated locally so this file has no
    cross-test-module import dependency for something this small."""
    client = AsyncMock()

    async def _fake_search(**kwargs):
        async def _async_iter():
            for r in records:
                yield r
        return _async_iter()

    client.search = _fake_search
    return client


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


# ═══════════════════════════════════════════════════════════════════════════════
# PER-SESSION BUSINESS RULES (Rick's PR #102 review, round 3, required item 1): every
# session applies its OWN bound persona's machine-outage/extras-gate/invalid-modifier/
# greeting/role-name rules -- none of these read a shared, brand-only module constant
# anymore. test-alpha and test-beta are configured with deliberately OPPOSITE
# extras allow/block categories and their own machine status/invalid-modifier/
# greeting/roleName data (see their persona.json/menuItems.json/prompts/greeting.yaml)
# so the same order-composition/query/machine-key scenario proves real per-session
# isolation, not just "two personas that happen to behave the same."
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaBusinessRuleIsolationTests(unittest.TestCase):
    """Mutation check (see decision note): reintroducing any of tools.py's deleted
    MOCK_MACHINE_STATUS/ALLOWED_EXTRA_CATEGORIES/BLOCKED_EXTRA_CATEGORIES/INVALID_MODS
    module constants (even just for ONE of the checks below) collapses test-alpha's and
    test-beta's results back to being identical, failing at least one assertion here."""

    def setUp(self):
        self.catalog = _load_fixture_catalog()
        self.alpha = self.catalog.get("test-alpha")
        self.beta = self.catalog.get("test-beta")
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)
        tools._search_cache.clear()

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self, persona) -> str:
        sid = order_state_singleton.create_session(persona=persona)
        self._sessions_created.append(sid)
        return sid

    # ── machines / OOS annotation ──────────────────────────────────────────────

    def test_same_machine_key_is_down_for_alpha_but_operational_for_beta(self):
        """Both packs key their soda fountain as `soda_machine` -- alpha's persona.json marks
        it "down", beta's marks it "operational". A shared module-level MOCK_MACHINE_STATUS
        could only ever pick ONE of these for both personas at once."""
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        menu_a = order_state_singleton.get_menu_catalog(sid_a)
        menu_b = order_state_singleton.get_menu_catalog(sid_b)

        result_a = _run(tools.search(
            _make_mock_search_client([{"id": "1", "name": "Alpha Cola", "category": "drinks", "sizes": "N/A"}]),
            "cfg", "id", "description", "embedding", False, {"query": "cola"}, menu=menu_a,
        ))
        result_b = _run(tools.search(
            _make_mock_search_client([{"id": "1", "name": "Beta Root Beer", "category": "drinks", "sizes": "N/A"}]),
            "cfg", "id", "description", "embedding", False, {"query": "root beer"}, menu=menu_b,
        ))
        self.assertIn("OOS", result_a.text)
        self.assertNotIn("OOS", result_b.text)

    # ── extras allow/block gate ─────────────────────────────────────────────────

    def test_extras_gate_gives_opposite_outcomes_for_the_same_base_category(self):
        """alpha allows extras on "drinks" and blocks them on "mains"; beta is configured the
        exact opposite. Both sessions add their OWN "mains"-category base item, then try to
        add their OWN isExtra item -- alpha's extra must be rejected (mains blocked), beta's
        must be accepted (mains allowed) -- proving the SAME category name drives opposite
        real behavior per persona, not a shared module-level allow/block list."""
        sid_a = self._new_session(self.alpha)
        _run(tools.update_order({"action": "add", "item_name": "Alpha Burger", "size": "small", "quantity": 1, "price": 3.99}, sid_a))
        _run(tools.update_order({"action": "add", "item_name": "Alpha Flavor Shot", "size": "small", "quantity": 1, "price": 0.59}, sid_a))
        alpha_items = [oi.item for oi in order_state_singleton.get_order_items(sid_a)]
        self.assertNotIn("Alpha Flavor Shot", alpha_items, "alpha blocks extras on 'mains' -- must be rejected")

        sid_b = self._new_session(self.beta)
        _run(tools.update_order({"action": "add", "item_name": "Beta Double Burger", "size": "regular", "quantity": 1, "price": 4.49}, sid_b))
        _run(tools.update_order({"action": "add", "item_name": "Beta Cheese Sauce", "size": "regular", "quantity": 1, "price": 0.79}, sid_b))
        beta_items = [oi.item for oi in order_state_singleton.get_order_items(sid_b)]
        self.assertIn("Beta Cheese Sauce", beta_items, "beta allows extras on 'mains' -- must be accepted")

    # ── invalid modifiers ────────────────────────────────────────────────────────

    def test_invalid_modifiers_are_rejected_per_persona_own_rules_only(self):
        """alpha's own rule blocks "onion" on its "drinks" category; beta's own (different)
        rule blocks "mustard" on its "mains" category. Each rule only fires for its OWN
        persona/category pairing -- proving invalid_modifiers is read off the bound
        MenuCatalog, not a shared module-level dict."""
        menu_a = menu_utils.get_catalog_for_persona(self.alpha)
        menu_b = menu_utils.get_catalog_for_persona(self.beta)

        self.assertIsNotNone(tools.validate_customization("Alpha Cola", "onion", menu=menu_a))
        self.assertIsNotNone(tools.validate_customization("Beta Double Burger", "mustard", menu=menu_b))
        # Cross-checks: alpha's rule never fires for beta's category/word and vice versa.
        self.assertIsNone(tools.validate_customization("Beta Double Burger", "onion", menu=menu_b))
        self.assertIsNone(tools.validate_customization("Alpha Cola", "mustard", menu=menu_a))

    # ── greeting text ────────────────────────────────────────────────────────────

    def test_greeting_text_comes_from_each_persona_own_pack(self):
        """The SAME shared SessionManager instance, given each persona's own PromptLoader-built
        greeting override, renders each pack's own greeting.yaml text -- never a hardcoded
        brand-specific string (the deleted _DEFAULT_GREETING_MSG)."""
        sm = SessionManager()
        loader_a = PromptLoader(brand=self.alpha.id, prompts_dir=self.alpha.prompts_dir)
        loader_b = PromptLoader(brand=self.beta.id, prompts_dir=self.beta.prompts_dir)
        msg_a = sm.build_greeting_msg(loader_a.get_greeting_json_str())
        msg_b = sm.build_greeting_msg(loader_b.get_greeting_json_str())
        self.assertIn("Test Alpha Drive-In", msg_a)
        self.assertIn("Test Beta Burger Co.", msg_b)
        self.assertNotIn("Test Beta", msg_a)
        self.assertNotIn("Test Alpha", msg_b)

    # ── role name (nudge text / transcript-replay label) ────────────────────────

    def test_nudge_and_rehydration_role_label_use_each_persona_own_role_name(self):
        """Both were rendered from a hardcoded "carhop" string before this round; now both are
        templated on the bound persona's own roleName ("alpha-hop"/"beta-runner"), matching
        the greeting check above in proving no shared brand-only text remains."""
        sm = SessionManager()
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        sm.record_turn(sid_a, "carhop", "one alpha cola coming up")
        sm.record_turn(sid_b, "carhop", "one beta root beer coming up")

        nudge_a = sm.build_nudge_item(role_name=self.alpha.manifest.roleName)
        nudge_b = sm.build_nudge_item(role_name=self.beta.manifest.roleName)
        self.assertIn("alpha-hop", nudge_a)
        self.assertIn("beta-runner", nudge_b)

        rehydration_a = sm.build_rehydration_item(sid_a, role_name=self.alpha.manifest.roleName)
        rehydration_b = sm.build_rehydration_item(sid_b, role_name=self.beta.manifest.roleName)
        self.assertIn("Alpha-hop: one alpha cola coming up", rehydration_a)
        self.assertIn("Beta-runner: one beta root beer coming up", rehydration_b)


# ═══════════════════════════════════════════════════════════════════════════════
# HAPPY-HOUR BANNER FROM THE BOUND PACK (issue #113): update_order/get_order must append
# EACH session's own bound persona's `pricing.happyHour.banner`/`announce` -- never the
# hardcoded default-pack string tools.py used to carry. alpha and beta are checked at the
# SAME mocked clock instant (via order_state.conformance_hooks.now, not the coarser
# `order_state.is_happy_hour` patch used elsewhere in this suite) so the difference in
# outcome can only come from each session's own bound pack, not from a shared window/
# clock fake. See test_tool_calling.py::HappyHourBannerWordingTests for the default
# persona's own regression proof (unchanged banner text, same file, not duplicated here).
# ═══════════════════════════════════════════════════════════════════════════════

# alpha's window is 14:00-16:00 America/Chicago (its persona.json, same hours as the
# deployment default persona's own window).
_ALPHA_TZ = ZoneInfo("America/Chicago")
_IN_ALPHA_WINDOW = datetime(2026, 1, 1, 15, 0, tzinfo=_ALPHA_TZ)
_OUTSIDE_ALPHA_WINDOW = datetime(2026, 1, 1, 20, 0, tzinfo=_ALPHA_TZ)


class HappyHourBannerFromBoundPackTests(unittest.TestCase):
    """#113 acceptance: test-alpha (inside its window) gets `[ALPHA HAPPY HOUR ACTIVE]`,
    never the default pack's text; test-beta (`happyHour: null`) never gets a banner,
    even at the identical instant alpha would announce. A mutation that re-hardcodes the
    old default-pack string back into tools.py fails every "ALPHA" assertion below; a
    mutation that ignores `pricing.happyHour: null`/`announce` fails the beta/no-announce
    ones."""

    def setUp(self):
        catalog = _load_fixture_catalog()
        self.alpha = catalog.get("test-alpha")
        self.beta = catalog.get("test-beta")
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)
        tools._search_cache.clear()
        # #77 (add-time `machine_unavailable`): alpha's own persona.json intentionally marks
        # `soda_machine` "down" for test_same_machine_key_is_down_for_alpha_but_operational_for_beta
        # (a SEARCH-time OOS-annotation test, above). None of the happy-hour tests below are
        # about machine state at all, and #77 added a NEW add-time gate that would otherwise
        # reject every "Alpha Cola" add here as `machine_unavailable` -- so bring soda_machine
        # up for just this test class (the shared, memoized MenuCatalog instance, restored in
        # cleanup) rather than touch the committed fixture pack real search tests still rely on.
        menu_a = menu_utils.get_catalog_for_persona(self.alpha)
        original_machines = dict(menu_a.machines)
        menu_a.machines["soda_machine"] = ("operational", original_machines["soda_machine"][1])
        self.addCleanup(menu_a.machines.update, original_machines)

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self, persona) -> str:
        sid = order_state_singleton.create_session(persona=persona)
        self._sessions_created.append(sid)
        return sid

    @patch("order_state.conformance_hooks.now", return_value=_IN_ALPHA_WINDOW)
    def test_alpha_update_order_gets_its_own_banner_never_the_default_packs(self, _mock_now):
        sid = self._new_session(self.alpha)
        result = _run(tools.update_order(
            {"action": "add", "item_name": "Alpha Cola", "size": "small", "quantity": 1, "price": 1.99}, sid
        ))
        self.assertIn("[ALPHA HAPPY HOUR ACTIVE]", result.text)
        self.assertNotIn("slushes and fountain drinks", result.text)

    @patch("order_state.conformance_hooks.now", return_value=_IN_ALPHA_WINDOW)
    def test_alpha_get_order_also_uses_its_own_bound_pack(self, _mock_now):
        sid = self._new_session(self.alpha)
        _run(tools.update_order(
            {"action": "add", "item_name": "Alpha Cola", "size": "small", "quantity": 1, "price": 1.99}, sid
        ))
        result = _run(tools.get_order({}, sid))
        self.assertIn("[ALPHA HAPPY HOUR ACTIVE]", result.text)

    @patch("order_state.conformance_hooks.now", return_value=_OUTSIDE_ALPHA_WINDOW)
    def test_alpha_gets_no_banner_outside_its_own_window(self, _mock_now):
        sid = self._new_session(self.alpha)
        result = _run(tools.update_order(
            {"action": "add", "item_name": "Alpha Cola", "size": "small", "quantity": 1, "price": 1.99}, sid
        ))
        self.assertNotIn("HAPPY HOUR", result.text)

    @patch("order_state.conformance_hooks.now", return_value=_IN_ALPHA_WINDOW)
    def test_beta_never_announces_even_at_the_same_instant_alpha_would(self, _mock_now):
        """test-beta's `pricing.happyHour: null` (decision 5) -- checked at the exact same
        mocked instant test-alpha announces at above, proving beta's silence is its OWN
        pack's data, not a coincidence of the clock."""
        sid = self._new_session(self.beta)
        result = _run(tools.update_order(
            {"action": "add", "item_name": "Beta Root Beer", "size": "regular", "quantity": 1, "price": 2.29}, sid
        ))
        self.assertNotIn("HAPPY HOUR", result.text)
        result = _run(tools.get_order({}, sid))
        self.assertNotIn("HAPPY HOUR", result.text)

    @patch("order_state.conformance_hooks.now", return_value=_IN_ALPHA_WINDOW)
    def test_alpha_own_price_multiplier_applies_not_a_shared_default(self, _mock_now):
        """test-alpha's `priceMultiplier` is 0.5 (the default persona's own pack is also 0.5, so
        this alone wouldn't prove isolation) -- combined with its own `happyHourDiscounted:
        true` fixture item, this proves the discount actually applied comes from ALPHA's bound
        persona, via the same per-session `_happy_hour_discount` order_state.py has read since
        #74 (unchanged by this issue); #113 only changed the banner/announce lookup above."""
        sid = self._new_session(self.alpha)
        _run(tools.update_order(
            {"action": "add", "item_name": "Alpha Cola", "size": "small", "quantity": 1, "price": 1.99}, sid
        ))
        summary = order_state_singleton.get_order_summary(sid)
        self.assertAlmostEqual(summary.total, 1.99 * 0.5, places=2)

    def _alpha_with_announce_off(self) -> Persona:
        """test-alpha with only `pricing.happyHour.announce` flipped to false: same window,
        multiplier, banner text and menu. Built in memory so no extra pack is committed."""
        manifest = self.alpha.manifest
        happy_hour = manifest.pricing.happyHour.model_copy(update={"announce": False})
        pricing = manifest.pricing.model_copy(update={"happyHour": happy_hour})
        return Persona(self.alpha.id, self.alpha.pack_dir, manifest.model_copy(update={"pricing": pricing}))

    @patch("order_state.conformance_hooks.now", return_value=_IN_ALPHA_WINDOW)
    def test_announce_false_discounts_but_never_shows_the_banner(self, _mock_now):
        """A pack with happy hour on but `announce: false`, inside its own window: the
        discount still applies, and neither update_order nor get_order carries the banner."""
        quiet_alpha = self._alpha_with_announce_off()
        self.assertTrue(quiet_alpha.manifest.pricing.happyHour.banner)
        sid = self._new_session(quiet_alpha)
        result = _run(tools.update_order(
            {"action": "add", "item_name": "Alpha Cola", "size": "small", "quantity": 1, "price": 1.99}, sid
        ))
        self.assertNotIn(quiet_alpha.manifest.pricing.happyHour.banner, result.text)
        self.assertNotIn("HAPPY HOUR", result.text)
        result = _run(tools.get_order({}, sid))
        self.assertNotIn(quiet_alpha.manifest.pricing.happyHour.banner, result.text)
        self.assertNotIn("HAPPY HOUR", result.text)
        summary = order_state_singleton.get_order_summary(sid)
        self.assertAlmostEqual(summary.total, 1.99 * 0.5, places=2)

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

    def test_backends_python_falls_back_to_same_origin_when_backend_uri_is_unset(self):
        """Rick's PR #102 review item 4: BACKEND_URI/BACKEND_DOTNET_URI unset (local dev)
        -- python still gets an entry (same-origin ""), never omitted; dotnet, with no
        configured URI, is omitted entirely rather than reported with a placeholder."""
        with patch.dict("os.environ", {}, clear=False):
            for var in ("BACKEND_URI", "BACKEND_DOTNET_URI"):
                os.environ.pop(var, None)
            body = self._index_body(self.catalog)
        self.assertEqual(body["backends"], [{"id": "python", "url": ""}])

    def test_backends_url_is_each_backend_public_base_url_not_realtime(self):
        """`backends[].url` (design doc 5.2/10.1) is each backend's PUBLIC BASE URL, built
        from the #93 azd outputs BACKEND_URI/BACKEND_DOTNET_URI -- never the `/realtime`
        WebSocket path itself, so the F11 header switch can link across hostnames."""
        with patch.dict(
            "os.environ",
            {
                "BACKEND_URI": "https://python-backend.example.azurecontainerapps.io",
                "BACKEND_DOTNET_URI": "https://dotnet-backend.example.azurecontainerapps.io",
            },
        ):
            body = self._index_body(self.catalog)
        self.assertEqual(
            body["backends"],
            [
                {"id": "python", "url": "https://python-backend.example.azurecontainerapps.io"},
                {"id": "dotnet", "url": "https://dotnet-backend.example.azurecontainerapps.io"},
            ],
        )

    def test_backends_omits_dotnet_when_its_uri_is_unset(self):
        with patch.dict("os.environ", {"BACKEND_URI": "https://python-backend.example.azurecontainerapps.io"}):
            os.environ.pop("BACKEND_DOTNET_URI", None)
            body = self._index_body(self.catalog)
        self.assertEqual(
            body["backends"],
            [{"id": "python", "url": "https://python-backend.example.azurecontainerapps.io"}],
        )

    def test_persona_summary_has_the_designed_fields(self):
        from app import _content_hash
        persona = self.catalog.get("test-alpha")
        summary = self._summary_body(persona)
        self.assertEqual(summary["id"], "test-alpha")
        self.assertEqual(summary["displayName"], "Test Alpha Drive-In")
        # Rick's PR #102 review item 2: logoUrl carries a `?v=<content-hash>` of the actual
        # on-disk logo file, so this asserts the exact hash of the fixture file, not just a
        # prefix/shape check -- a stronger guarantee that the URL really is pinned to the
        # file's current bytes.
        expected_hash = _content_hash(persona.assets_dir / "logo.svg")
        self.assertEqual(summary["logoUrl"], f"/personas/test-alpha/assets/logo.svg?v={expected_hash}")
        self.assertIn("light", summary["theme"])

    def test_persona_detail_has_voice_locales_features_menu_and_models(self):
        from app import _content_hash
        persona = self.catalog.get("test-beta")
        detail = self._detail_body(persona)
        self.assertEqual(detail["id"], "test-beta")
        self.assertEqual(detail["voice"], {"default": "cedar"})
        self.assertEqual(detail["locales"]["default"], "en")
        self.assertEqual(detail["features"], {"dayparts": False})
        # Rick's PR #102 review item 2: same content-hash versioning as logoUrl above.
        expected_hash = _content_hash(persona.menu_path)
        self.assertEqual(detail["menuUrl"], f"/personas/test-beta/menu.json?v={expected_hash}")
        self.assertEqual(detail["models"]["realtime"]["default"], "gpt-realtime-mini")
        self.assertNotIn("cascade", detail["models"])
        self.assertNotIn("local", detail["models"])


# ═══════════════════════════════════════════════════════════════════════════════
# PERSONA ASSET + MENU.JSON ROUTES (issue #74, design doc section 5.2, Rick's PR #102
# review item 5): served only for ENABLED packs, path traversal rejected, correct
# content types + immutable cache headers.
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaAssetPathResolutionTests(unittest.TestCase):
    """Unit-level tests of `_resolve_persona_asset_path` -- deterministic, exercises the
    exact input string with no HTTP client / URL-normalization ambiguity. Covers every
    traversal variant Rick's item 5 calls out by name: `../`, encoded variants, absolute
    paths, and symlink escape (simulated, since creating real symlinks needs elevated
    privileges on Windows CI runners)."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import _resolve_persona_asset_path
        self._resolve = _resolve_persona_asset_path
        self.persona = _load_fixture_catalog().get("test-alpha")

    def test_resolves_a_real_top_level_asset(self):
        resolved = self._resolve(self.persona, "logo.svg")
        self.assertIsNotNone(resolved)
        self.assertTrue(resolved.is_file())

    def test_resolves_a_real_nested_asset(self):
        resolved = self._resolve(self.persona, "sub/icon.png")
        self.assertIsNotNone(resolved)

    def test_none_for_a_filename_that_does_not_exist(self):
        self.assertIsNone(self._resolve(self.persona, "does-not-exist.svg"))

    def test_none_for_dotdot_traversal(self):
        self.assertIsNone(self._resolve(self.persona, "../persona.json"))
        self.assertIsNone(self._resolve(self.persona, "../../../../etc/passwd"))
        self.assertIsNone(self._resolve(self.persona, "sub/../../persona.json"))

    def test_none_for_backslash_dotdot_traversal(self):
        """Windows-style separators must be normalized before segment-splitting, not
        smuggled past the `/`-based check."""
        self.assertIsNone(self._resolve(self.persona, "..\\persona.json"))

    def test_none_for_percent_encoded_dotdot(self):
        """aiohttp's router percent-decodes match_info before handlers see it, so an
        encoded variant like %2e%2e%2f arrives here already looking like a literal
        `../` -- confirms the structural check still catches it once decoded."""
        self.assertIsNone(self._resolve(self.persona, "%2e%2e/persona.json".replace("%2e", ".")))

    def test_none_for_absolute_posix_path(self):
        self.assertIsNone(self._resolve(self.persona, "/etc/passwd"))

    def test_none_for_absolute_windows_path(self):
        self.assertIsNone(self._resolve(self.persona, "C:/Windows/win.ini"))
        self.assertIsNone(self._resolve(self.persona, "C:\\Windows\\win.ini"))

    def test_none_for_empty_or_null_byte_path(self):
        self.assertIsNone(self._resolve(self.persona, ""))
        self.assertIsNone(self._resolve(self.persona, "logo.svg\x00.png"))

    def test_none_when_resolved_path_escapes_assets_dir_even_without_dotdot_segments(self):
        """Simulates a symlink escape: a segment-only check would pass (no `..` in the
        string), but `.resolve()` following the link lands outside `assets_dir`, so the
        `relative_to(assets_root)` guard must still reject it. Patches `Path.resolve` so
        only the specific asset-file candidate resolves outside the pack -- everything
        else (including `assets_dir` itself) resolves normally -- since creating a real
        symlink needs elevated privileges on Windows CI runners."""
        escaped_target = self.persona.pack_dir.parent.resolve() / "persona.schema.json"
        real_resolve = Path.resolve

        def fake_resolve(path_self, strict=False):
            if path_self.name == "logo.svg":
                return escaped_target
            return real_resolve(path_self, strict=strict)

        with patch.object(Path, "resolve", fake_resolve):
            self.assertIsNone(self._resolve(self.persona, "logo.svg"))


class PersonaAssetAndMenuRouteTests(unittest.IsolatedAsyncioTestCase):
    """HTTP-level conformance tests: registers the real `register_persona_routes` (app.py)
    on a standalone `web.Application` against the fixture catalog -- no `create_app()` /
    Azure OpenAI/Search dependency needed, but actual route matching, percent-decoding
    and status codes are exercised end-to-end via aiohttp's TestClient."""

    async def asyncSetUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from aiohttp import web
        from aiohttp.test_utils import TestClient, TestServer

        from app import register_persona_routes

        # Only test-alpha enabled: test-beta's pack exists on disk (FIXTURES_DIR) but is a
        # "disabled pack" for this catalog -- its routes must 404 exactly like an unknown
        # persona id, never fall through to serving its files (Rick's item 5).
        self.catalog = PersonaCatalog.load(
            personas_dir=FIXTURES_DIR, enabled=["test-alpha"], default_persona_id="test-alpha",
        )
        app = web.Application()
        register_persona_routes(app, self.catalog)
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()

    async def test_logo_asset_returns_200_with_immutable_cache_header_when_v_matches(self):
        from app import _STATIC_IMMUTABLE_MAX_AGE, _content_hash
        persona = self.catalog.get("test-alpha")
        expected_hash = _content_hash(persona.assets_dir / "logo.svg")
        resp = await self.client.get(f"/personas/test-alpha/assets/logo.svg?v={expected_hash}")
        self.assertEqual(resp.status, 200)
        self.assertIn("<svg", await resp.text())
        self.assertEqual(
            resp.headers["Cache-Control"], f"public, max-age={_STATIC_IMMUTABLE_MAX_AGE}, immutable",
        )
        # Non-blocking (Rick's PR #102 review): explicit content type + nosniff.
        self.assertEqual(resp.content_type, "image/svg+xml")
        self.assertEqual(resp.headers["X-Content-Type-Options"], "nosniff")

    async def test_logo_asset_gets_short_cache_header_when_v_is_missing(self):
        """Rick's PR #102 review item 2: an unversioned request must NEVER get the
        year-long immutable policy -- a later pack update could silently serve stale
        content for a year to anyone still holding that plain URL."""
        from app import _STATIC_DEFAULT_MAX_AGE
        resp = await self.client.get("/personas/test-alpha/assets/logo.svg")
        self.assertEqual(resp.status, 200)
        self.assertEqual(resp.headers["Cache-Control"], f"public, max-age={_STATIC_DEFAULT_MAX_AGE}")
        self.assertNotIn("immutable", resp.headers["Cache-Control"])

    async def test_logo_asset_gets_short_cache_header_when_v_is_stale(self):
        """A `v` that doesn't match the file's CURRENT hash (e.g. minted before a pack
        edit) must fall back to the short policy too, not just a missing `v`."""
        from app import _STATIC_DEFAULT_MAX_AGE
        resp = await self.client.get("/personas/test-alpha/assets/logo.svg?v=0000000000000000")
        self.assertEqual(resp.status, 200)
        self.assertEqual(resp.headers["Cache-Control"], f"public, max-age={_STATIC_DEFAULT_MAX_AGE}")
        self.assertNotIn("immutable", resp.headers["Cache-Control"])

    async def test_nested_asset_path_returns_200(self):
        resp = await self.client.get("/personas/test-alpha/assets/sub/icon.png")
        self.assertEqual(resp.status, 200)

    async def test_menu_json_returns_200_as_application_json_with_immutable_cache_header_when_v_matches(self):
        from app import _STATIC_IMMUTABLE_MAX_AGE, _content_hash
        persona = self.catalog.get("test-alpha")
        expected_hash = _content_hash(persona.menu_path)
        resp = await self.client.get(f"/personas/test-alpha/menu.json?v={expected_hash}")
        self.assertEqual(resp.status, 200)
        self.assertEqual(resp.content_type, "application/json")
        self.assertEqual(
            resp.headers["Cache-Control"], f"public, max-age={_STATIC_IMMUTABLE_MAX_AGE}, immutable",
        )
        self.assertEqual(resp.headers["X-Content-Type-Options"], "nosniff")
        self.assertIn("menuItems", await resp.json())

    async def test_menu_json_gets_short_cache_header_when_v_is_missing_or_stale(self):
        from app import _STATIC_DEFAULT_MAX_AGE
        for query in ("", "?v=0000000000000000"):
            with self.subTest(query=query):
                resp = await self.client.get(f"/personas/test-alpha/menu.json{query}")
                self.assertEqual(resp.status, 200)
                self.assertEqual(resp.headers["Cache-Control"], f"public, max-age={_STATIC_DEFAULT_MAX_AGE}")
                self.assertNotIn("immutable", resp.headers["Cache-Control"])

    async def test_asset_404s_for_unknown_persona_id(self):
        resp = await self.client.get("/personas/does-not-exist/assets/logo.svg")
        self.assertEqual(resp.status, 404)

    async def test_menu_404s_for_unknown_persona_id(self):
        resp = await self.client.get("/personas/does-not-exist/menu.json")
        self.assertEqual(resp.status, 404)

    async def test_asset_404s_for_a_disabled_persona_even_though_its_pack_exists_on_disk(self):
        resp = await self.client.get("/personas/test-beta/assets/logo.svg")
        self.assertEqual(resp.status, 404)

    async def test_menu_404s_for_a_disabled_persona(self):
        resp = await self.client.get("/personas/test-beta/menu.json")
        self.assertEqual(resp.status, 404)

    async def test_asset_404s_for_a_filename_that_does_not_exist_in_the_pack(self):
        resp = await self.client.get("/personas/test-alpha/assets/does-not-exist.svg")
        self.assertEqual(resp.status, 404)

    async def test_percent_encoded_dotdot_traversal_is_rejected(self):
        """The encoded variant Rick's item 5 calls out by name -- aiohttp's router
        percent-decodes match_info before the handler runs, so this must land in the
        same structural-rejection path as a literal `../`, not slip through as 200."""
        resp = await self.client.get(
            "/personas/test-alpha/assets/%2e%2e%2f%2e%2e%2f%2e%2e%2fpersona.json", allow_redirects=False,
        )
        self.assertIn(resp.status, (400, 404))

    async def test_literal_dotdot_traversal_out_of_the_pack_is_rejected(self):
        resp = await self.client.get(
            "/personas/test-alpha/assets/../../../persona.schema.json", allow_redirects=False,
        )
        self.assertIn(resp.status, (400, 404))
        # If the route even matched (rather than the underlying HTTP client/router
        # normalizing the dots away into a different, non-matching path), it must not be
        # a 200 -- the fixture pack's own schema file must never be served this way.
        self.assertNotEqual(resp.status, 200)


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

    async def test_session_metadata_carries_the_bound_persona_id(self):
        """Rick's PR #102 review item 3 (#74 acceptance, design doc 5.2): every
        `extension.session_metadata` message reports the persona this session is bound
        to -- both for an explicitly requested persona and for the omitted-persona
        default-binding path."""
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        events = await self._browser_events(browser, duration=0.5)
        meta = next(e for e in events if e.get("type") == "extension.session_metadata")
        self.assertEqual(meta["persona"], "test-beta")
        await browser.close()

        default_browser = await self.client.ws_connect("/realtime")
        await default_browser.send_json(BROWSER_SESSION_UPDATE)
        default_events = await self._browser_events(default_browser, duration=0.5)
        default_meta = next(e for e in default_events if e.get("type") == "extension.session_metadata")
        self.assertEqual(default_meta["persona"], "test-alpha")
        await default_browser.close()

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
