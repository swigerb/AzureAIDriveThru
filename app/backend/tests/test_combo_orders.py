"""Tests for combo ordering: adding combos, converting standalone items to combos,
component absorption pricing, combo + happy hour interaction, and Route 44 sizing.

#104: unit prices now always come from the resolved menu record for the requested item/size --
the price passed into `handle_order_update()` is a realistic tool-call *input* value (what a
realtime tool call would plausibly send), but it is informational only: the server always charges
the real menu price and only logs a debug/warning message if the two differ. Every price literal
below is the real per-size menu price from `personas/sonic/menu/menuItems.json`, so assertions
comparing `summary.total`/`item.price` reflect what the guest is actually charged.
"""

import math
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

import pytest

from default_persona import get_default_persona
from menu_utils import get_catalog_for_persona
from order_state import order_state_singleton


def _default_menu():
    """The default (env-driven) persona's own MenuCatalog -- #74 replaces the old
    module-level menu_utils free functions/MENU_CATEGORY_MAP these tests used to
    call directly; every session (including the default one) now resolves its menu
    through this exact same path, so this fixture proves nothing is skipped."""
    return get_catalog_for_persona(get_default_persona())


@pytest.fixture(autouse=True)
def _reset_order_state():
    """Ensure each test starts with a clean OrderState."""
    order_state_singleton.sessions = {}
    yield
    order_state_singleton.sessions = {}


# ---------------------------------------------------------------------------
# Adding a combo from scratch
# ---------------------------------------------------------------------------

class TestAddCombo:
    """Adding a combo item directly (no conversion from standalone)."""

    def test_add_combo_creates_line_item(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].item == "SONIC® Cheeseburger Combo"
        assert items[0].price == 9.19

    def test_add_combo_total_is_combo_price(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19
        )
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 10.19, rel_tol=1e-9)

    def test_combo_needs_side_and_drink(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]
        assert len(req["missing_items"]) == 2

    def test_combo_with_side_and_drink_is_complete(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]
        # Total should be only the combo price (side+drink absorbed)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)


# ---------------------------------------------------------------------------
# Converting a standalone entree to a combo
# ---------------------------------------------------------------------------

class TestComboConversion:
    """Converting an existing standalone item to a combo via 'make it a combo'."""

    def test_conversion_removes_standalone(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        items = order_state_singleton.get_order_items(sid)
        item_names = [i.item for i in items]
        assert "SONIC® Cheeseburger" not in item_names
        assert "SONIC® Cheeseburger Combo" in item_names
        assert len(items) == 1
        assert "combo_converted_from" in result

    def test_conversion_price_is_combo_only(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        summary = order_state_singleton.get_order_summary(sid)
        # Only the combo price, standalone was removed
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    def test_conversion_carries_mods(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger (No Onions)", "standard", 1, 5.29
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        items = order_state_singleton.get_order_items(sid)
        assert "(No Onions)" in items[0].item
        assert "mods_carried" in result

    def test_conversion_with_existing_side_absorbs_it(self):
        """Guest has burger + tots, says 'make it a combo' → burger removed, tots absorbed."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        items = order_state_singleton.get_order_items(sid)
        item_names = [i.item for i in items]
        assert "SONIC® Cheeseburger" not in item_names
        assert "Tots" not in item_names
        assert "SONIC® Cheeseburger Combo" in item_names
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    def test_conversion_full_flow_burger_to_combo_with_components(self):
        """Full 'Brian's bug' scenario: burger → make it a combo → tots → drink."""
        sid = order_state_singleton.create_session()
        # Guest orders a cheeseburger
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        # AI suggests combo, guest accepts
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        # Guest picks side and drink
        order_state_singleton.handle_order_update(sid, "add", "Tots", "large", 1, 3.49)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].item == "SONIC® Cheeseburger Combo"
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]


# ---------------------------------------------------------------------------
# Component absorption pricing
# ---------------------------------------------------------------------------

class TestAbsorptionPricing:
    """Side and drink absorbed into combo should contribute $0 to total."""

    def test_absorbed_side_is_zero_price(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.39
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        assert result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        # Only combo price, no tots price added
        assert math.isclose(summary.total, 8.39, rel_tol=1e-9)

    def test_absorbed_drink_is_zero_price(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.39
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "large", 1, 3.39
        )
        assert result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.39, rel_tol=1e-9)

    def test_second_side_not_absorbed_charged_full(self):
        """Extra side beyond combo slot is at full price."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.39
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        # Second side should NOT be absorbed
        result = order_state_singleton.handle_order_update(
            sid, "add", "Onion Rings", "medium", 1, 3.89
        )
        assert not result.get("absorbed_into_combo", False)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.39 + 3.89, rel_tol=1e-9)

    def test_two_combos_need_two_sides_two_drinks(self):
        """Two combos absorb two sides and two drinks."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 2, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Groovy Fries", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "medium", 1, 2.89)

        items = order_state_singleton.get_order_items(sid)
        # Only the combo (qty 2) should remain
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19 * 2, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]

    @patch("order_state.is_happy_hour", return_value=False)
    def test_combo_plus_standalone_drink_at_full_price(self, _mock_hh):
        """Combo with components filled + extra standalone drink is full price."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        # Extra standalone drink
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "large", 1, 3.39)
        items = order_state_singleton.get_order_items(sid)
        ocean = next(i for i in items if i.item == "Ocean Water®")
        assert ocean.price == 3.39
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19 + 3.39, rel_tol=1e-9)


# ---------------------------------------------------------------------------
# Combo + Happy Hour interaction
# ---------------------------------------------------------------------------

class TestComboHappyHour:
    """Happy hour 50% drink discount must NOT apply to absorbed combo drinks,
    but MUST still apply to standalone drinks."""

    @patch("order_state.is_happy_hour", return_value=True)
    def test_standalone_drink_gets_happy_hour_discount(self, _mock_hh):
        """Standalone drink during happy hour is 50% off."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "large", 1, 3.39
        )
        summary = order_state_singleton.get_order_summary(sid)
        expected = 3.39 * 0.5
        assert math.isclose(summary.total, expected, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_combo_price_not_discounted_during_happy_hour(self, _mock_hh):
        """Combo price itself is NOT discounted during happy hour."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        summary = order_state_singleton.get_order_summary(sid)
        # Combo is not a "drink" so no discount
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_absorbed_drink_not_double_discounted(self, _mock_hh):
        """A drink absorbed into a combo (at $0) must not cause negative pricing."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)
        summary = order_state_singleton.get_order_summary(sid)
        # Only combo price; absorbed drink is not on the order, no happy hour effect
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_combo_plus_extra_standalone_drink_discounted(self, _mock_hh):
        """Combo + standalone extra drink: only the extra drink gets 50% off."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        # Extra standalone drink
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "large", 1, 3.39)
        summary = order_state_singleton.get_order_summary(sid)
        # Combo full price + extra drink at 50%
        expected = 9.19 + (3.39 * 0.5)
        assert math.isclose(summary.total, expected, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=False)
    def test_no_happy_hour_no_discount(self, _mock_hh):
        """Outside happy hour, drinks are full price."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "large", 1, 3.39
        )
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 3.39, rel_tol=1e-9)


# ---------------------------------------------------------------------------
# Route 44 sizing still works with combos
# ---------------------------------------------------------------------------

class TestRoute44WithCombos:
    """Route 44 sizing must still work correctly alongside combo logic."""

    def test_route_44_drink_standalone_display(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "rt44", 1, 3.79
        )
        items = order_state_singleton.get_order_items(sid)
        assert items[0].display == "Route 44 Cherry Limeade"

    def test_route_44_drink_absorbed_into_combo(self):
        """RT44 drink absorbed into combo still works (no line item but combo complete)."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "rt44", 1, 3.79
        )
        assert result.get("absorbed_into_combo") is True
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_route_44_standalone_gets_happy_hour(self, _mock_hh):
        """Route 44 standalone drink gets happy hour discount."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "route 44", 1, 3.79
        )
        summary = order_state_singleton.get_order_summary(sid)
        expected = 3.79 * 0.5
        assert math.isclose(summary.total, expected, rel_tol=1e-9)
        items = order_state_singleton.get_order_items(sid)
        assert items[0].display == "Route 44 Cherry Limeade"

    def test_route_44_size_aliases_all_work(self):
        """All RT44 aliases produce 'Route 44' display."""
        aliases = ["rt44", "rt 44", "route 44", "44", "44oz"]
        for alias in aliases:
            order_state_singleton.sessions = {}
            sid = order_state_singleton.create_session()
            order_state_singleton.handle_order_update(
                sid, "add", "Cherry Limeade", alias, 1, 3.79
            )
            items = order_state_singleton.get_order_items(sid)
            assert items[0].display == "Route 44 Cherry Limeade", (
                f"Failed for alias '{alias}'"
            )

    def test_mini_size_still_works(self):
        """Mini size (Sonic-specific) still works."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "mini", 1, 1.59
        )
        items = order_state_singleton.get_order_items(sid)
        assert items[0].display == "Mini Cherry Limeade"


# ---------------------------------------------------------------------------
# Menu item existence validation
# ---------------------------------------------------------------------------

class TestBundleSlotsByPackData:
    """PR #99 decision 1 (Rick): bundle absorption must key off each item's actual
    pack `bundle.slots`, not off the literal word "combo" in the item name. These
    scenarios cover the specific bundles Rick called out: French Toast Sticks Combo
    and Crispy Tenders Dinner absorb a drink only (no side slot), while Wacky Packs
    and the $6 Meal absorb a side AND a drink, same as a regular Combo."""

    def test_french_toast_sticks_combo_absorbs_drink_only(self):
        """French Toast Sticks Combo's bundle.slots is ["drinks"] only, so a
        standalone drink added afterward must be absorbed for free."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "French Toast Sticks Combo", "standard", 1, 5.19
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 1, 2.89
        )
        assert result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 5.19, rel_tol=1e-9)

    def test_french_toast_sticks_combo_does_not_absorb_a_side(self):
        """Tots added after a French Toast Sticks Combo must be charged in full --
        this is the bug Rick flagged: the old name-based ("combo" in name) engine
        wrongly gave every "Combo"-named item one free side slot."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "French Toast Sticks Combo", "standard", 1, 5.19
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        assert not result.get("absorbed_into_combo", False)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 5.19 + 2.79, rel_tol=1e-9)
        items = order_state_singleton.get_order_items(sid)
        tots = next(i for i in items if i.item == "Tots")
        assert tots.price == 2.79

    def test_crispy_tenders_dinner_absorbs_drink_only(self):
        """Crispy Tenders Dinner - 3 piece has bundle.slots = ["drinks"]: drink
        absorbed free, but a side is not."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Crispy Tenders Dinner - 3 piece", "standard", 1, 8.89
        )
        drink_result = order_state_singleton.handle_order_update(
            sid, "add", "Ocean Water®", "medium", 1, 2.89
        )
        assert drink_result.get("absorbed_into_combo") is True
        side_result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        assert not side_result.get("absorbed_into_combo", False)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.89 + 2.79, rel_tol=1e-9)

    def test_corn_dog_wacky_pack_absorbs_side_and_drink(self):
        """Wacky Packs have bundle.slots = ["sides", "drinks"], same as a regular
        Combo, even though the name doesn't contain the word "combo"."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Corn Dog Wacky Pack®", "standard", 1, 4.59
        )
        side_result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        drink_result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 1, 2.89
        )
        assert side_result.get("absorbed_into_combo") is True
        assert drink_result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 4.59, rel_tol=1e-9)

    def test_six_dollar_meal_absorbs_side_and_drink(self):
        """$6 All-American Smasher™ Meal has bundle.slots = ["sides", "drinks"]
        despite not containing "combo" in its name."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "$6 All-American Smasher™ Meal", "standard", 1, 6.0
        )
        side_result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        drink_result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 1, 2.89
        )
        assert side_result.get("absorbed_into_combo") is True
        assert drink_result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 6.0, rel_tol=1e-9)

    # Pre-existing gap (unrelated to #77): this test never fixed the clock, so it silently
    # depended on real wall-clock time never landing inside the persona's own 14:00-16:00
    # happy-hour window -- Cherry Limeade is `happyHourDiscounted:true`, so a run that happens
    # to execute during that window got it silently half-priced, breaking the "no discount"
    # assertion below. Matches every other happy-hour-adjacent test in this file/`test_extras_rules.py`.
    @patch("order_state.is_happy_hour", return_value=False)
    def test_item_without_bundle_absorbs_nothing(self, _mock_hh):
        """A plain (non-bundle) item must not absorb any side or drink."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        side_result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        drink_result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 1, 2.89
        )
        assert not side_result.get("absorbed_into_combo", False)
        assert not drink_result.get("absorbed_into_combo", False)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 5.29 + 2.79 + 2.89, rel_tol=1e-9)

    def test_regular_combo_still_absorbs_side_and_drink(self):
        """Regression: a regular "... Combo" item (bundle.slots = ["sides",
        "drinks"]) must behave exactly as before the bundle-slot fix."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 9.19
        )
        side_result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        drink_result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 1, 2.89
        )
        assert side_result.get("absorbed_into_combo") is True
        assert drink_result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 9.19, rel_tol=1e-9)

    def test_get_combo_requirements_uses_per_component_bundle_capacity(self):
        """get_combo_requirements() must track side/drink capacity per bundle,
        not one shared "combo_count" -- a drinks-only bundle must not report a
        missing side as satisfied by another bundle's side slot, and vice versa."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "French Toast Sticks Combo", "standard", 1, 5.19
        )
        order_state_singleton.handle_order_update(
            sid, "add", "Corn Dog Wacky Pack®", "standard", 1, 4.59
        )
        # French Toast Sticks Combo needs 1 drink (no side); Wacky Pack needs 1
        # side + 1 drink. Total capacity: 1 side, 2 drinks.
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "medium", 2, 2.89
        )
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]


# ---------------------------------------------------------------------------
# #179 live bug: combo drink resize via remove-then-add, via explicit modify, and
# via an add of the same item at a different size while the slot is already full.
# ---------------------------------------------------------------------------

class TestComboComponentResize:
    """Reproduces the exact #179 sequence (SuperSONIC Double Cheeseburger Combo + Large
    Tots + Medium Diet Coke, then the model resizes the drink to Large) and proves every
    way of reaching a resize -- remove-then-add, the explicit `modify` action, and an
    `add` of the same item at a different size while the slot is already full -- end up at
    the exact same correct state: one combo line, priced per the real menu upsize delta,
    no standalone duplicate drink line."""

    COMBO = "SuperSONIC® Double Cheeseburger Combo"
    COMBO_PRICE = 10.19
    DRINK = "Diet Coke®"
    DRINK_MEDIUM_PRICE = 2.49
    DRINK_LARGE_PRICE = 2.99
    DRINK_UPSIZE_DELTA = 0.50  # Large ($2.99) - Medium ($2.49), the pack's own menu data
    SIDE_LARGE_PRICE = 3.49

    def _seed_combo_with_large_side_and_medium_drink(self, sid):
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "standard", 1, self.COMBO_PRICE)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "large", 1, self.SIDE_LARGE_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "medium", 1, self.DRINK_MEDIUM_PRICE)

    def test_remove_then_add_resizes_drink_in_place(self):
        """The exact #179 sequence: `remove Diet Coke Medium` must vacate the combo's
        drink slot (not no-op), and the following `add Diet Coke Large` must refill that
        slot as a resize (not a standalone duplicate), charging only the real upsize."""
        sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(sid)

        remove_result = order_state_singleton.handle_order_update(
            sid, "remove", self.DRINK, "medium", 1, 0.0
        )
        assert remove_result.get("vacated_combo_component") == "drinks"
        # The slot is empty again -- the combo is incomplete until a drink refills it.
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1  # still just the combo line; nothing orphaned by the vacate

        add_result = order_state_singleton.handle_order_update(
            sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE
        )
        assert add_result.get("resized_combo_component") == "drinks"

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1, "must be exactly one combo line, no standalone drink duplicate"
        combo_item = items[0]
        assert "Large Diet Coke®" in combo_item.display
        assert "Medium Diet Coke®" not in combo_item.display
        assert not any(i.item == self.DRINK and i is not combo_item for i in items)

        summary = order_state_singleton.get_order_summary(sid)
        # Side was absorbed free either way (first-ever fill of that slot); only the
        # drink's real upsize delta is charged on top of the combo's base price.
        assert math.isclose(summary.total, self.COMBO_PRICE + self.DRINK_UPSIZE_DELTA, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]

    def test_explicit_modify_resizes_drink_in_place_identically(self):
        """The explicit resize/modify path must price identically to remove-then-add --
        the free reference survives either way."""
        sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(sid)

        modify_result = order_state_singleton.handle_order_update(
            sid, "modify", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE
        )
        assert modify_result.get("resized_combo_component") == "drinks"

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        combo_item = items[0]
        assert "Large Diet Coke®" in combo_item.display
        assert "Medium Diet Coke®" not in combo_item.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE + self.DRINK_UPSIZE_DELTA, rel_tol=1e-9)

    def test_add_same_item_different_size_while_slot_full_resizes_not_duplicates(self):
        """An `add` of the SAME item at a DIFFERENT size while the slot is already full
        (no `remove` call at all) must also resolve to an in-place resize, never a
        silent duplicate standalone line -- the model doesn't always call `remove` first."""
        sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(sid)

        add_result = order_state_singleton.handle_order_update(
            sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE
        )
        assert add_result.get("resized_combo_component") == "drinks"
        assert not add_result.get("absorbed_into_combo", False)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE + self.DRINK_UPSIZE_DELTA, rel_tol=1e-9)

    def test_downsize_credits_the_real_difference(self):
        """Resizing DOWN must credit the real (negative) delta, symmetric with upsizing --
        establish the free reference at Large, then downsize to Medium."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "standard", 1, self.COMBO_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.DRINK, "medium", 1, self.DRINK_MEDIUM_PRICE
        )
        assert result.get("resized_combo_component") == "drinks"
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE - self.DRINK_UPSIZE_DELTA, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=False)
    def test_different_item_while_slot_full_remains_a_standalone_add(self, _mock_hh):
        """Swapping to a GENUINELY DIFFERENT item while the drink slot is already full is
        neither a resize nor a fresh absorption -- it must fall through to a normal,
        full-price standalone add (unchanged, pre-existing behavior), not silently
        overwrite the combo's existing drink."""
        sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(sid)

        result = order_state_singleton.handle_order_update(
            sid, "add", "Ocean Water®", "large", 1, 3.39
        )
        assert not result.get("resized_combo_component")
        assert not result.get("absorbed_into_combo", False)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2
        combo_item = next(i for i in items if i.item == self.COMBO)
        assert "Medium Diet Coke®" in combo_item.display
        ocean_water = next(i for i in items if i.item == "Ocean Water®")
        assert ocean_water.price == 3.39

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE + 3.39, rel_tol=1e-9)

    def test_modify_rejects_an_item_that_is_neither_a_raw_line_nor_an_absorbed_component(self):
        """`modify` on an item that's genuinely not in the order at all (not a raw line,
        not absorbed into any combo slot) must stay a documented no-op, same as before
        #179 -- this guards against the new absorbed-component branch becoming a
        false-positive match for anything."""
        sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(sid)

        result = order_state_singleton.handle_order_update(
            sid, "modify", "Ocean Water®", "large", 1, 3.39
        )
        assert not result.get("resized_combo_component")
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE, rel_tol=1e-9)


class TestComboMenuItems:
    """Verify combo items exist in the menu JSON and are categorized correctly."""

    def test_combo_items_in_menu_category_map(self):
        combos_in_map = {k: v for k, v in _default_menu().category_map.items() if "combo" in k}
        # issue #72 Part 2: full export import grew Combos to 28 items; 25 of those 28 literally
        # contain "combo" in their name (the other 3 -- "$6 All-American Smasher™ Meal" and the
        # two "Crispy Tenders Dinner - N piece" items -- are Combos-category bundles that don't
        # use the word "combo").
        assert len(combos_in_map) == 25, f"Expected 25 combo entries, got {len(combos_in_map)}"
        # All combos should map to "combos" category
        for name, cat in combos_in_map.items():
            assert cat == "combos", f"{name} mapped to '{cat}' instead of 'combos'"

    def test_combo_infer_category(self):
        menu = _default_menu()
        assert menu.infer_category("SONIC® Cheeseburger Combo") == "combos"
        assert menu.infer_category("Fish Sandwich Combo") == "combos"
        assert menu.infer_category("SuperSONIC® Double Cheeseburger Combo") == "combos"

    def test_combo_not_classified_as_drink(self):
        """Combos must not be classified as drinks (would break happy hour)."""
        menu = _default_menu()
        assert menu.infer_combo_component("SONIC® Cheeseburger Combo") == ""
        assert menu.infer_combo_component("Fish Sandwich Combo") == ""
        assert menu.infer_combo_component("SuperSONIC® Double Cheeseburger Combo") == ""
