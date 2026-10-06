"""#77 (P2-8, "Shared bundle and extras engines"): direct coverage for the engine surfaces that
were implemented but had no dedicated Python test yet -- ``modify``, the add-time
``machine_unavailable`` structured rejection via the REAL ``tools.update_order()`` call path,
``bundle_autofill`` (menu.schema.json ``bundle.autoFill``), ``meal_number_candidates``/
``rewrite_search_query`` (``strategies.searchQueryRewrite: "meal_numbers"``), and
``try_split_combined_name`` (``extras.splitCombinedNames``).

test_combo_orders.py already proves the bundle engine is data-driven against a real production
pack's own data (several different ``bundle.slots`` shapes); this file instead uses a dedicated,
non-brand-coupled fixture persona -- ``test-delta`` (tests/fixtures/personas/test-delta) -- built
specifically to opt into every one of these #77 features at once, so the "works for ANY pack, not
just one brand's data" acceptance criterion has its own direct proof, independent of any real
production menu. ``test-alpha``/``test-beta`` (used elsewhere for persona-isolation proofs)
deliberately do NOT opt into any of these -- reused here only as the "this persona does NOT opt
in" control.

Mutation checks (see decision note beth-77.md): disabling meal-number lookup (e.g. hardcoding
``meal_number_candidates`` to always return ``[]``), accepting a machine-down item on add, or
letting a blocked/off-menu name silently pass would each fail a test below.

#165 addendum: test-delta also carries a breakfast/lunch pair sharing meal number 2 (see
``MealNumberLookupTests`` above), so this same fixture doubles as the dedicated coverage for
``menu_utils.MenuCatalog.item_available_now``, ``order_state.create_session``'s mode-defaulting
normalization, and ``tools.update_order``'s add-time ``item_out_of_mode`` gate -- see
``ItemAvailableNowUnitTests``, ``CreateSessionMenuModeDefaultingTests``, and
``UpdateOrderItemOutOfModeGateTests`` near the end of this file.
"""

import asyncio
import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import menu_utils  # noqa: E402
import tools  # noqa: E402
from order_state import order_state_singleton  # noqa: E402
from persona_loader import PersonaCatalog  # noqa: E402
from rtmt import ToolResultDirection  # noqa: E402

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _run(coro):
    return asyncio.run(coro)


def _load_delta_catalog() -> PersonaCatalog:
    """The #77-dedicated fixture pack: opts into bundle autoFill, numbered meals +
    searchQueryRewrite, splitCombinedNames, and a currently-down machine -- all at once, so every
    #77 engine surface has one non-brand-coupled pack proving it works for "ANY pack"."""
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-delta"],
        default_persona_id="test-delta",
    )


def _load_alpha_catalog() -> PersonaCatalog:
    """test-alpha: the "does NOT opt in" control -- ``searchQueryRewrite: "none"`` and
    ``splitCombinedNames: false`` (see its own persona.json), reused here only to prove #77's new
    strategies don't leak into a persona that never asked for them."""
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-alpha"],
        default_persona_id="test-alpha",
    )


class DeltaFixtureTestCase(unittest.TestCase):
    """Shared setUp/session-cleanup for every #77 engine test below."""

    def setUp(self):
        self.delta = _load_delta_catalog().get("test-delta")
        self.menu = menu_utils.get_catalog_for_persona(self.delta)
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self) -> str:
        sid = order_state_singleton.create_session(persona=self.delta)
        self._sessions_created.append(sid)
        return sid

    def _new_session_with_mode(self, menu_mode: str | None) -> str:
        """Same as :meth:`_new_session` but binds an explicit ``menu_mode`` -- the #165 session
        parameter ``rtmt.py``'s websocket handshake would normally resolve from ``?mode=``."""
        sid = order_state_singleton.create_session(persona=self.delta, menu_mode=menu_mode)
        self._sessions_created.append(sid)
        return sid


# ═══════════════════════════════════════════════════════════════════════════════
# meal_number_candidates / rewrite_search_query ("strategies.searchQueryRewrite: meal_numbers")
# ═══════════════════════════════════════════════════════════════════════════════


class MealNumberLookupTests(DeltaFixtureTestCase):
    def test_single_match_for_a_number_only_one_item_claims(self):
        self.assertEqual(self.menu.meal_number_candidates("1"), ["Delta Meal"])

    def test_both_matches_for_a_number_two_items_share(self):
        """A breakfast and a lunch meal sharing meal number "2" (design doc section 3.3 row 23,
        "a breakfast/lunch overlap") must BOTH come back -- never just the first one found."""
        self.assertEqual(
            self.menu.meal_number_candidates("2"),
            ["Delta Breakfast Meal", "Delta Lunch Meal"],
        )

    def test_empty_for_a_number_no_item_claims(self):
        self.assertEqual(self.menu.meal_number_candidates("99"), [])

    def test_rewrite_search_query_appends_the_single_candidate_name(self):
        self.assertEqual(self.menu.rewrite_search_query("number 1 please"), "number 1 please Delta Meal")

    def test_rewrite_search_query_appends_every_shared_candidate(self):
        rewritten = self.menu.rewrite_search_query("I'll get a number 2")
        self.assertEqual(rewritten, "I'll get a number 2 Delta Breakfast Meal Delta Lunch Meal")

    def test_rewrite_search_query_is_a_noop_with_no_digit_in_the_query(self):
        self.assertEqual(self.menu.rewrite_search_query("cola"), "cola")

    def test_rewrite_search_query_is_a_noop_for_an_unknown_number(self):
        self.assertEqual(self.menu.rewrite_search_query("number 99"), "number 99")

    def test_a_persona_that_does_not_opt_in_never_rewrites(self):
        """test-alpha's own ``strategies.searchQueryRewrite`` is "none" -- mutation check: if the
        strategy dispatch were ever collapsed to "always rewrite", this would start failing."""
        alpha_menu = menu_utils.get_catalog_for_persona(_load_alpha_catalog().get("test-alpha"))
        self.assertEqual(alpha_menu.rewrite_search_query("number 1"), "number 1")


# ═══════════════════════════════════════════════════════════════════════════════
# bundle_autofill (menu.schema.json ``bundle.autoFill``), wired through
# order_state.handle_order_update's add path
# ═══════════════════════════════════════════════════════════════════════════════


class BundleAutoFillTests(DeltaFixtureTestCase):
    def test_autofill_fills_every_unabsorbed_slot_on_add(self):
        """Adding "Delta Meal" with nothing pre-existing in the order to absorb: BOTH of its
        ``bundle.autoFill`` slots (sides, drinks) get their own default filler the instant the
        bundle itself is added -- no follow-up add call needed."""
        sid = self._new_session()
        result_info = order_state_singleton.handle_order_update(
            sid, "add", "Delta Meal", "regular", 1, 5.99,
        )
        self.assertEqual(
            sorted(result_info.get("autofilled", [])),
            sorted(["Delta Fries (Regular)", "Delta Cola (Regular)"]),
        )
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertIn("Delta Fries (Regular)", items[0].components)
        self.assertIn("Delta Cola (Regular)", items[0].components)

    def test_autofill_uses_the_requested_size_not_just_the_bundle_default(self):
        sid = self._new_session()
        result_info = order_state_singleton.handle_order_update(
            sid, "add", "Delta Meal", "large", 1, 7.49,
        )
        self.assertEqual(
            sorted(result_info.get("autofilled", [])),
            sorted(["Delta Fries (Large)", "Delta Cola (Large)"]),
        )

    def test_absorption_of_a_real_preexisting_standalone_item_wins_over_autofill(self):
        """A real standalone "Delta Fries" already in the order gets ABSORBED into the bundle's
        "sides" slot (the pre-#77 absorption behavior, unchanged); auto-fill only ever fires for a
        slot that absorption did NOT just cover -- here, only "drinks"."""
        sid = self._new_session()
        order_state_singleton.handle_order_update(sid, "add", "Delta Fries", "regular", 1, 1.99)
        result_info = order_state_singleton.handle_order_update(
            sid, "add", "Delta Meal", "regular", 1, 5.99,
        )
        self.assertEqual(result_info.get("autofilled", []), ["Delta Cola (Regular)"])
        items = order_state_singleton.get_order_items(sid)
        bundle_line = next(i for i in items if i.item == "Delta Meal")
        # The absorbed real fries display, not the synthetic auto-fill filler text.
        self.assertIn("Delta Fries", bundle_line.components[0])
        self.assertIn("Delta Cola (Regular)", bundle_line.components)
        # The standalone fries line itself is gone -- absorbed, not double-counted.
        self.assertFalse(any(i.item == "Delta Fries" for i in items))

    def test_a_bundle_item_with_no_autofill_data_stays_absorb_only(self):
        """Regression guard: an item whose OWN pack never populates ``bundle.autoFill`` (every
        real pack today) must get an empty autofill map, not a KeyError/crash -- checked directly
        against MenuCatalog since no fixture item currently has bundle slots without autoFill."""
        self.assertEqual(self.menu.bundle_autofill("Delta Burger"), {})


# ═══════════════════════════════════════════════════════════════════════════════
# try_split_combined_name (``extras.splitCombinedNames``) + its `suggested_calls` wiring in
# tools.update_order's not_on_menu rejection
# ═══════════════════════════════════════════════════════════════════════════════


class SplitCombinedNameTests(DeltaFixtureTestCase):
    def test_menu_catalog_splits_a_known_base_plus_known_extra(self):
        self.assertEqual(
            self.menu.try_split_combined_name("Delta Latte with Extra Shot"),
            ("Delta Latte", "Delta Extra Shot"),
        )

    def test_menu_catalog_returns_none_when_the_extra_part_is_not_a_real_extra(self):
        self.assertIsNone(self.menu.try_split_combined_name("Delta Latte with Whipped Cream"))

    def test_menu_catalog_returns_none_when_the_base_part_is_not_on_the_menu(self):
        self.assertIsNone(self.menu.try_split_combined_name("Nonexistent Thing with Extra Shot"))

    def test_menu_catalog_returns_none_with_no_connector_word_at_all(self):
        self.assertIsNone(self.menu.try_split_combined_name("Delta Latte Extra Shot"))

    def test_update_order_offers_suggested_calls_for_a_combined_off_menu_name(self):
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Latte with Extra Shot",
            "size": "regular", "quantity": 1, "price": 4.24,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(result.text["reason"], "not_on_menu")
        self.assertEqual(
            result.text["suggested_calls"],
            [
                {"action": "add", "item_name": "Delta Latte"},
                {"action": "add", "item_name": "Delta Extra Shot"},
            ],
        )
        # Nothing was actually added -- the split is only ever a suggestion to the model.
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    def test_a_persona_that_does_not_opt_in_gets_no_suggested_calls(self):
        """test-alpha's own ``extras.splitCombinedNames`` is false -- mutation check: if the
        ``if self.split_combined_names:`` guard in try_split_combined_name were ever removed,
        this would start failing (alpha would start getting suggestions it never opted into)."""
        alpha = _load_alpha_catalog().get("test-alpha")
        sid = order_state_singleton.create_session(persona=alpha)
        try:
            result = _run(tools.update_order({
                "action": "add", "item_name": "Alpha Cola with Alpha Flavor Shot",
                "size": "small", "quantity": 1, "price": 2.58,
            }, sid))
            self.assertEqual(result.text["reason"], "not_on_menu")
            self.assertNotIn("suggested_calls", result.text)
        finally:
            order_state_singleton.delete_session(sid)


# ═══════════════════════════════════════════════════════════════════════════════
# `modify` (resize an existing order line in place) via the real tools.update_order() path
# ═══════════════════════════════════════════════════════════════════════════════


class ModifyActionTests(DeltaFixtureTestCase):
    def test_modify_resizes_in_place_and_reprices_from_the_menu_never_the_tool_calls_own_price(self):
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "Delta Latte",
            "size": "regular", "quantity": 1, "price": 3.49,
        }, sid))
        result = _run(tools.update_order({
            "action": "modify", "item_name": "Delta Latte",
            "size": "large", "quantity": 1, "price": 999.99,  # deliberately bogus -- must be ignored
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1, "modify must resize the existing line, never add a second one")
        self.assertEqual(items[0].size, "large")
        self.assertTrue(math.isclose(items[0].price, 4.29, rel_tol=1e-9))
        self.assertIn("Large", items[0].display)

    def test_modify_preserves_already_absorbed_components(self):
        """Resizing a meal must not drop what already came bundled with it (design doc section
        3.3 row 21: "its own already-absorbed components carry over unchanged")."""
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "Delta Meal",
            "size": "regular", "quantity": 1, "price": 5.99,
        }, sid))
        _run(tools.update_order({
            "action": "modify", "item_name": "Delta Meal",
            "size": "large", "quantity": 1, "price": 7.49,
        }, sid))
        items = order_state_singleton.get_order_items(sid)
        bundle_line = next(i for i in items if i.item == "Delta Meal")
        self.assertEqual(bundle_line.size, "large")
        self.assertIn("Delta Fries (Regular)", bundle_line.components)
        self.assertIn("Delta Cola (Regular)", bundle_line.components)

    def test_modify_of_an_item_not_in_the_order_is_rejected_not_reported_as_changed(self):
        """rick-2's PR #116 review: a `modify` for an on-menu item that isn't in the order must
        come back as the structured `not_in_order` rejection, never the success delta ("Changed
        ... your total is now ..."), and must leave the order unchanged."""
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "Delta Meal",
            "size": "regular", "quantity": 1, "price": 5.99,
        }, sid))
        before = [(i.item, i.size, i.quantity, i.price) for i in order_state_singleton.get_order_items(sid)]
        result = _run(tools.update_order({
            "action": "modify", "item_name": "Delta Latte",
            "size": "large", "quantity": 1, "price": 4.29,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(
            {k: v for k, v in result.text.items() if k != "message"},
            {"status": "rejected", "item_added": False, "reason": "not_in_order", "item_name": "Delta Latte"},
        )
        self.assertIn("Delta Latte", result.text["message"])
        self.assertIn("nothing was changed", result.text["message"])
        after = [(i.item, i.size, i.quantity, i.price) for i in order_state_singleton.get_order_items(sid)]
        self.assertEqual(after, before)

    def test_modify_on_an_empty_order_is_rejected_and_adds_nothing(self):
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "modify", "item_name": "Delta Latte",
            "size": "large", "quantity": 1, "price": 4.29,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(result.text["reason"], "not_in_order")
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    def test_modify_never_re_checks_the_machine_gate_for_an_item_already_in_the_order(self):
        """Rationale in tools.py's own comment: resizing an item already successfully in the
        order doesn't newly require the machine it already required when it was added -- seeded
        directly via handle_order_update (bypassing tools.py's add-time gate) to simulate "this
        was added before the machine went down", exactly the scenario the comment describes."""
        sid = self._new_session()
        order_state_singleton.handle_order_update(sid, "add", "Delta Shake", "regular", 1, 2.99)
        result = _run(tools.update_order({
            "action": "modify", "item_name": "Delta Shake",
            "size": "regular", "quantity": 1, "price": 2.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "Delta Shake")


# ═══════════════════════════════════════════════════════════════════════════════
# Add-time `machine_unavailable` structured rejection via the real tools.update_order() path
# ═══════════════════════════════════════════════════════════════════════════════


class MachineUnavailableRejectionTests(DeltaFixtureTestCase):
    def test_add_of_a_currently_down_machine_item_is_rejected(self):
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Shake",
            "size": "regular", "quantity": 1, "price": 2.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        # Exact key set, matching not_on_menu/size_not_available field-for-field (Rick's PR #100
        # review, required item 1; docs/persona-architecture.md section 6) -- no stray
        # "available_sizes" or other leaked field from a neighboring rejection branch.
        self.assertEqual(set(result.text.keys()), {"status", "item_added", "reason", "item_name", "message"})
        self.assertEqual(result.text["status"], "rejected")
        self.assertFalse(result.text["item_added"])
        self.assertEqual(result.text["reason"], "machine_unavailable")
        self.assertEqual(result.text["item_name"], "Delta Shake")
        self.assertIn("Delta machine is down", result.text["message"])
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    def test_a_persona_with_the_same_machine_key_operational_is_unaffected(self):
        """test-beta keys its fountain machine "soda_machine" too, but reports it operational --
        proving the gate reads THIS persona's own live status, never a shared/cached one."""
        beta = PersonaCatalog.load(
            personas_dir=FIXTURES_DIR, enabled=["test-beta"], default_persona_id="test-beta",
        ).get("test-beta")
        sid = order_state_singleton.create_session(persona=beta)
        try:
            result = _run(tools.update_order({
                "action": "add", "item_name": "Beta Root Beer",
                "size": "regular", "quantity": 1, "price": 1.99,
            }, sid))
            self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
            self.assertEqual(len(order_state_singleton.get_order_items(sid)), 1)
        finally:
            order_state_singleton.delete_session(sid)

    def test_remove_action_bypasses_the_machine_gate(self):
        """Mirrors the existing not_on_menu "remove bypasses the on-menu gate" guard -- removing
        a name that would be rejected on add must still be a harmless no-op, not a rejection."""
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "remove", "item_name": "Delta Shake",
            "size": "regular", "quantity": 1,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)


# ═══════════════════════════════════════════════════════════════════════════════
# Issue 165: Breakfast/Lunch menu mode -- menu_utils.MenuCatalog.item_available_now,
# order_state.create_session's default-to-"lunch" normalization, and tools.update_order's
# add-time item_out_of_mode gate, all against the SAME test-delta fixture's breakfast/lunch pair
# (shares meal number 2, see MealNumberLookupTests above) and its period-less "Delta Meal"/
# "Delta Burger"-equivalent items.
# ═══════════════════════════════════════════════════════════════════════════════


class ItemAvailableNowUnitTests(DeltaFixtureTestCase):
    """Direct, session-free coverage of menu_utils.MenuCatalog.item_available_now -- the single
    function both tools.update_order's add-time gate and (indirectly, via order_state's session
    binding) every other #165 mode-aware behavior reads."""

    def test_a_none_active_mode_is_always_available_regardless_of_the_items_own_period(self):
        """A persona with no features.dayparts at all (or an unbound session) never gates --
        this is the permanent no-op branch."""
        self.assertTrue(self.menu.item_available_now("Delta Breakfast Meal", None))
        self.assertTrue(self.menu.item_available_now("Delta Lunch Meal", None))

    def test_an_item_with_no_menu_period_is_available_in_every_mode(self):
        self.assertTrue(self.menu.item_available_now("Delta Meal", "breakfast"))
        self.assertTrue(self.menu.item_available_now("Delta Meal", "lunch"))

    def test_an_item_matching_the_active_mode_is_available(self):
        self.assertTrue(self.menu.item_available_now("Delta Breakfast Meal", "breakfast"))
        self.assertTrue(self.menu.item_available_now("Delta Lunch Meal", "lunch"))

    def test_an_item_from_the_other_daypart_is_not_available(self):
        self.assertFalse(self.menu.item_available_now("Delta Breakfast Meal", "lunch"))
        self.assertFalse(self.menu.item_available_now("Delta Lunch Meal", "breakfast"))

    def test_an_unresolved_item_name_is_available_since_the_on_menu_gate_owns_that_rejection(self):
        self.assertTrue(self.menu.item_available_now("Nonexistent Thing", "lunch"))


class CreateSessionMenuModeDefaultingTests(DeltaFixtureTestCase):
    """order_state.create_session's #165 normalization: a dayparts persona always resolves to a
    real mode ("lunch" default, #164 decision D3), never a silently-unbound session; a
    non-dayparts persona is always forced to None regardless of what's passed."""

    def test_an_explicit_breakfast_mode_is_kept(self):
        sid = self._new_session_with_mode("breakfast")
        self.assertEqual(order_state_singleton.get_menu_mode(sid), "breakfast")

    def test_an_explicit_lunch_mode_is_kept(self):
        sid = self._new_session_with_mode("lunch")
        self.assertEqual(order_state_singleton.get_menu_mode(sid), "lunch")

    def test_an_omitted_mode_defaults_to_lunch(self):
        sid = self._new_session_with_mode(None)
        self.assertEqual(order_state_singleton.get_menu_mode(sid), "lunch")

    def test_an_unrecognized_mode_value_also_defaults_to_lunch(self):
        sid = self._new_session_with_mode("brunch")
        self.assertEqual(order_state_singleton.get_menu_mode(sid), "lunch")

    def test_a_persona_with_no_dayparts_feature_is_always_none_even_if_a_mode_was_requested(self):
        alpha = _load_alpha_catalog().get("test-alpha")
        sid = order_state_singleton.create_session(persona=alpha, menu_mode="breakfast")
        try:
            self.assertIsNone(order_state_singleton.get_menu_mode(sid))
        finally:
            order_state_singleton.delete_session(sid)


class UpdateOrderItemOutOfModeGateTests(DeltaFixtureTestCase):
    """tools.update_order's add-time item_out_of_mode structured rejection (docs/
    persona-architecture.md section 6), exercised through the REAL tool call path, exactly like
    every other add-time gate in this file."""

    def test_add_of_the_breakfast_meal_succeeds_when_the_session_is_bound_to_breakfast(self):
        sid = self._new_session_with_mode("breakfast")
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Breakfast Meal",
            "size": "regular", "quantity": 1, "price": 4.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        self.assertEqual(len(order_state_singleton.get_order_items(sid)), 1)

    def test_add_of_the_breakfast_meal_is_rejected_when_the_session_is_bound_to_lunch(self):
        sid = self._new_session_with_mode("lunch")
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Breakfast Meal",
            "size": "regular", "quantity": 1, "price": 4.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(set(result.text.keys()), {"status", "item_added", "reason", "item_name", "message"})
        self.assertEqual(result.text["status"], "rejected")
        self.assertFalse(result.text["item_added"])
        self.assertEqual(result.text["reason"], "item_out_of_mode")
        self.assertEqual(result.text["item_name"], "Delta Breakfast Meal")
        self.assertTrue(result.text["message"])
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    def test_add_of_the_lunch_meal_is_rejected_when_the_session_is_bound_to_breakfast(self):
        sid = self._new_session_with_mode("breakfast")
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Lunch Meal",
            "size": "regular", "quantity": 1, "price": 6.49,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)
        self.assertEqual(result.text["reason"], "item_out_of_mode")
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    def test_add_of_a_period_less_item_succeeds_regardless_of_bound_mode(self):
        sid = self._new_session_with_mode("breakfast")
        result = _run(tools.update_order({
            "action": "add", "item_name": "Delta Meal",
            "size": "regular", "quantity": 1, "price": 5.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)

    def test_modify_is_never_gated_by_menu_mode_even_after_the_bound_mode_would_reject_a_fresh_add(self):
        """The gate only runs for action=="add" -- a modify on an item already in the order
        (added while the session was still in its own mode) must never be re-gated."""
        sid = self._new_session_with_mode("breakfast")
        _run(tools.update_order({
            "action": "add", "item_name": "Delta Breakfast Meal",
            "size": "regular", "quantity": 1, "price": 4.99,
        }, sid))
        result = _run(tools.update_order({
            "action": "modify", "item_name": "Delta Breakfast Meal",
            "size": "regular", "quantity": 2, "price": 4.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "Delta Breakfast Meal")

    def test_a_persona_with_no_dayparts_feature_never_gates_any_item(self):
        """Packs without modes unaffected: test-alpha declares no features.dayparts, so its own
        items (which carry no menuPeriod at all) must add cleanly no matter what menu_mode was
        requested at session creation."""
        alpha = _load_alpha_catalog().get("test-alpha")
        sid = order_state_singleton.create_session(persona=alpha, menu_mode="breakfast")
        try:
            result = _run(tools.update_order({
                "action": "add", "item_name": "Alpha Burger",
                "size": "small", "quantity": 1, "price": 3.99,
            }, sid))
            self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        finally:
            order_state_singleton.delete_session(sid)


def _load_zeta_catalog() -> PersonaCatalog:
    """#325 regression fixture: "test-zeta" (tests/fixtures/personas/test-zeta), the SAME
    fixture pack the dotnet/python conformance legs both discover from (#283), carries one
    standalone, trademark-marked item ("ZORBS\u00ae Bite Treats") and one standalone item whose
    OWN canonical name contains a paren group ("Zeta Snack Mix (Family Size)", B2) specifically so
    this bug class -- the model's own spelling/mark/paren choice leaking into the stored ticket
    instead of the menu's canonical name -- has a dedicated, non-brand-coupled proof independent
    of any real pack's own marked item names."""
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-zeta"],
        default_persona_id="test-zeta",
    )


class CanonicalItemNameTests(unittest.TestCase):
    """#325: ``update_order`` must store the MENU's own canonical spelling for every add/modify/
    remove/bundle-slot path -- never whatever casing or trademark marks the model's own
    ``item_name`` happened to use for that turn. Uses "test-zeta"'s "ZORBS\u00ae Bite Treats" (the
    exact fixture item named in #325's own write-up) rather than a real pack's marked item, so
    this proof is independent of any one persona's own data."""

    def setUp(self):
        self.zeta = _load_zeta_catalog().get("test-zeta")
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self) -> str:
        sid = order_state_singleton.create_session(persona=self.zeta)
        self._sessions_created.append(sid)
        return sid

    def test_add_with_the_unmarked_spelling_stores_the_menus_canonical_marked_name(self):
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "add", "item_name": "ZORBS Bite Treats",
            "size": "10 count", "quantity": 1, "price": 3.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "ZORBS\u00ae Bite Treats")

    def test_two_adds_with_different_spellings_merge_into_one_line(self):
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "ZORBS\u00ae Bite Treats",
            "size": "10 count", "quantity": 1, "price": 3.99,
        }, sid))
        result = _run(tools.update_order({
            "action": "add", "item_name": "ZORBS Bite Treats",
            "size": "10 count", "quantity": 1, "price": 3.99,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].quantity, 2)
        self.assertEqual(items[0].item, "ZORBS\u00ae Bite Treats")

    def test_remove_with_the_unmarked_spelling_matches_a_line_added_with_the_marked_spelling(self):
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "ZORBS\u00ae Bite Treats",
            "size": "10 count", "quantity": 1, "price": 3.99,
        }, sid))
        result = _run(tools.update_order({
            "action": "remove", "item_name": "ZORBS Bite Treats",
            "size": "10 count", "quantity": 1,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        self.assertEqual(order_state_singleton.get_order_items(sid), [])

    # ── B2 (Rick's PR #326 review): a canonical name that ITSELF contains a paren group ──

    def test_add_with_the_exact_canonical_paren_name_stores_it_unchanged_not_duplicated(self):
        """The model echoing the canonical name back verbatim -- including its own "(Family
        Size)" group -- must store that EXACT name, not "Zeta Snack Mix (Family Size) (Family
        Size)" (the duplication bug: the old code blindly reattached everything from the
        model's own first "(" onward, even when that text was just the canonical name's own
        paren group, not a guest customization)."""
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "add", "item_name": "Zeta Snack Mix (Family Size)",
            "size": "regular", "quantity": 1, "price": 3.49,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "Zeta Snack Mix (Family Size)")

    def test_add_with_the_paren_less_spelling_merges_into_the_same_line(self):
        """The model omitting the canonical name's own paren group entirely ("Zeta Snack Mix",
        no "(Family Size)") still resolves on-menu (menu_utils._menu_key strips modifiers for
        lookup) and must merge into the SAME line as the full canonical spelling -- not two
        separate lines that differ only by whether the model happened to say the item's own
        paren group."""
        sid = self._new_session()
        _run(tools.update_order({
            "action": "add", "item_name": "Zeta Snack Mix (Family Size)",
            "size": "regular", "quantity": 1, "price": 3.49,
        }, sid))
        result = _run(tools.update_order({
            "action": "add", "item_name": "Zeta Snack Mix",
            "size": "regular", "quantity": 1, "price": 3.49,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].quantity, 2)
        self.assertEqual(items[0].item, "Zeta Snack Mix (Family Size)")

    def test_a_genuine_customization_stacks_on_top_of_the_canonical_paren_name(self):
        """A REAL guest-added modifier group beyond the canonical name's own "(Family Size)" --
        e.g. "(Extra Spicy)" -- must be reattached on top of the canonical spelling, and must
        NOT be misread as the item's own "(Family Size)" group re-appearing: the stored name is
        the canonical name plus ONLY the genuinely new group, once."""
        sid = self._new_session()
        result = _run(tools.update_order({
            "action": "add", "item_name": "Zeta Snack Mix (Family Size) (Extra Spicy)",
            "size": "regular", "quantity": 1, "price": 3.49,
        }, sid))
        self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
        items = order_state_singleton.get_order_items(sid)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].item, "Zeta Snack Mix (Family Size) (Extra Spicy)")


if __name__ == "__main__":
    unittest.main()
