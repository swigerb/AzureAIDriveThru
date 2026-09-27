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


if __name__ == "__main__":
    unittest.main()
