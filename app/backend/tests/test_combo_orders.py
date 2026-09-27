"""Tests for combo ordering: adding combos, converting standalone items to combos,
component absorption pricing, combo + happy hour interaction, and Route 44 sizing.

PR #99 review decision 3 (Rick): the `8.49`-style prices passed into `handle_order_update()`
throughout this file are deliberately left at their pre-#72-Part-2 values. They are tool-call
*input* prices -- standing in for whatever price a realtime tool call would pass at the time --
not values looked up from the persona pack, so they are independent of the 7 combo prices Rick's
review corrected in `personas/sonic/menu/menuItems.json` (and the matching golden/frontend rows).
Only `TestBundleSlotsByPackData` below intentionally uses the corrected pack prices, because those
scenarios are asserting against the real bundle-slot data those specific items carry.
"""

import math
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

import pytest

from order_state import order_state_singleton


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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].item == "SONIC® Cheeseburger Combo"
        assert items[0].price == 8.49

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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]
        assert len(req["missing_items"]) == 2

    def test_combo_with_side_and_drink_is_complete(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]
        # Total should be only the combo price (side+drink absorbed)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)


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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        summary = order_state_singleton.get_order_summary(sid)
        # Only the combo price, standalone was removed
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    def test_conversion_carries_mods(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger (No Onions)", "standard", 1, 5.29
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        items = order_state_singleton.get_order_items(sid)
        item_names = [i.item for i in items]
        assert "SONIC® Cheeseburger" not in item_names
        assert "Tots" not in item_names
        assert "SONIC® Cheeseburger Combo" in item_names
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    def test_conversion_full_flow_burger_to_combo_with_components(self):
        """Full 'Brian's bug' scenario: burger → make it a combo → tots → drink."""
        sid = order_state_singleton.create_session()
        # Guest orders a cheeseburger
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger", "standard", 1, 5.29
        )
        # AI suggests combo, guest accepts
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        # Guest picks side and drink
        order_state_singleton.handle_order_update(sid, "add", "Tots", "large", 1, 3.49)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].item == "SONIC® Cheeseburger Combo"
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)
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
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.49
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Tots", "medium", 1, 2.79
        )
        assert result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        # Only combo price, no tots price added
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    def test_absorbed_drink_is_zero_price(self):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.49
        )
        result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "large", 1, 3.39
        )
        assert result.get("absorbed_into_combo") is True
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    def test_second_side_not_absorbed_charged_full(self):
        """Extra side beyond combo slot is at full price."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "Fish Sandwich Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        # Second side should NOT be absorbed
        result = order_state_singleton.handle_order_update(
            sid, "add", "Onion Rings", "medium", 1, 3.89
        )
        assert not result.get("absorbed_into_combo", False)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49 + 3.89, rel_tol=1e-9)

    def test_two_combos_need_two_sides_two_drinks(self):
        """Two combos absorb two sides and two drinks."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 2, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Groovy Fries", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "medium", 1, 2.89)

        items = order_state_singleton.get_order_items(sid)
        # Only the combo (qty 2) should remain
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49 * 2, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]

    @patch("order_state.is_happy_hour", return_value=False)
    def test_combo_plus_standalone_drink_at_full_price(self, _mock_hh):
        """Combo with components filled + extra standalone drink is full price."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        # Extra standalone drink
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "large", 1, 3.39)
        items = order_state_singleton.get_order_items(sid)
        ocean = next(i for i in items if i.item == "Ocean Water®")
        assert ocean.price == 3.39
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49 + 3.39, rel_tol=1e-9)


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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        summary = order_state_singleton.get_order_summary(sid)
        # Combo is not a "drink" so no discount
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_absorbed_drink_not_double_discounted(self, _mock_hh):
        """A drink absorbed into a combo (at $0) must not cause negative pricing."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)
        summary = order_state_singleton.get_order_summary(sid)
        # Only combo price; absorbed drink is not on the order, no happy hour effect
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_combo_plus_extra_standalone_drink_discounted(self, _mock_hh):
        """Combo + standalone extra drink: only the extra drink gets 50% off."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)
        # Extra standalone drink
        order_state_singleton.handle_order_update(sid, "add", "Ocean Water®", "large", 1, 3.39)
        summary = order_state_singleton.get_order_summary(sid)
        # Combo full price + extra drink at 50%
        expected = 8.49 + (3.39 * 0.5)
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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
        )
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = order_state_singleton.handle_order_update(
            sid, "add", "Cherry Limeade", "rt44", 1, 3.79
        )
        assert result.get("absorbed_into_combo") is True
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

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

    def test_item_without_bundle_absorbs_nothing(self):
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
            sid, "add", "SONIC® Cheeseburger Combo", "standard", 1, 8.49
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
        assert math.isclose(summary.total, 8.49, rel_tol=1e-9)

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


class TestComboMenuItems:
    """Verify combo items exist in the menu JSON and are categorized correctly."""

    def test_combo_items_in_menu_category_map(self):
        from menu_utils import MENU_CATEGORY_MAP
        combos_in_map = {k: v for k, v in MENU_CATEGORY_MAP.items() if "combo" in k}
        # issue #72 Part 2: full export import grew Combos to 28 items; 25 of those 28 literally
        # contain "combo" in their name (the other 3 -- "$6 All-American Smasher™ Meal" and the
        # two "Crispy Tenders Dinner - N piece" items -- are Combos-category bundles that don't
        # use the word "combo").
        assert len(combos_in_map) == 25, f"Expected 25 combo entries, got {len(combos_in_map)}"
        # All combos should map to "combos" category
        for name, cat in combos_in_map.items():
            assert cat == "combos", f"{name} mapped to '{cat}' instead of 'combos'"

    def test_combo_infer_category(self):
        from menu_utils import infer_category
        assert infer_category("SONIC® Cheeseburger Combo") == "combos"
        assert infer_category("Fish Sandwich Combo") == "combos"
        assert infer_category("SuperSONIC® Double Cheeseburger Combo") == "combos"

    def test_combo_not_classified_as_drink(self):
        """Combos must not be classified as drinks (would break happy hour)."""
        from order_state import _infer_combo_component
        assert _infer_combo_component("SONIC® Cheeseburger Combo") == ""
        assert _infer_combo_component("Fish Sandwich Combo") == ""
        assert _infer_combo_component("SuperSONIC® Double Cheeseburger Combo") == ""
