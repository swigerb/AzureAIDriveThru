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
from persona_loader import PersonaCatalog

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _default_menu():
    """The default (env-driven) persona's own MenuCatalog -- #74 replaces the old
    module-level menu_utils free functions/MENU_CATEGORY_MAP these tests used to
    call directly; every session (including the default one) now resolves its menu
    through this exact same path, so this fixture proves nothing is skipped."""
    return get_catalog_for_persona(get_default_persona())


def _delta_persona():
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-delta"],
        default_persona_id="test-delta",
    ).get("test-delta")


def _epsilon_persona():
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-epsilon"],
        default_persona_id="test-epsilon",
    ).get("test-epsilon")


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
        assert math.isclose(summary.total, 9.19 + 0.70 + 0.50, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]


# ---------------------------------------------------------------------------
# Component absorption pricing
# ---------------------------------------------------------------------------

class TestAbsorptionPricing:
    """Side and drink absorbed into combos contribute only any above-medium upcharge."""

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
        # Medium tots are included with no upcharge.
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
        assert math.isclose(summary.total, 8.39 + 0.50, rel_tol=1e-9)

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


class TestComponentUpcharge:
    """Issue #205: combos include medium components and upcharge larger sizes per unit."""

    @patch("order_state.is_happy_hour", return_value=False)
    def test_medium_side_and_drink_are_included(self, _mock_hh):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 1, 2.89)

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 10.19, rel_tol=1e-9)
        item = order_state_singleton.get_order_items(sid)[0]
        assert item.componentUpcharges == [0.0, 0.0]

    @patch("order_state.is_happy_hour", return_value=False)
    def test_large_drink_adds_upcharge_and_wire_component_price(self, _mock_hh):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 1, 2.79)
        result = order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)

        assert result["combo_component_upcharge_display"] == "$0.50"
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 10.69, rel_tol=1e-9)
        item = order_state_singleton.get_order_items(sid)[0]
        assert item.components == ["Medium Tots", "Large Cherry Limeade"]
        assert item.componentUpcharges == [0.0, 0.5]

    @patch("order_state.is_happy_hour", return_value=False)
    def test_large_tots_and_large_drink_add_both_upcharges(self, _mock_hh):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "large", 1, 3.49)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "large", 1, 3.39)

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, 11.39, rel_tol=1e-9)
        item = order_state_singleton.get_order_items(sid)[0]
        assert item.componentUpcharges == [0.70, 0.5]

    @patch("order_state.is_happy_hour", return_value=False)
    def test_route_44_upcharge_and_downsize_no_credit(self, _mock_hh):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "small", 1, 2.19)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "rt44", 1, 3.79)
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, 11.09, rel_tol=1e-9)

        order_state_singleton.handle_order_update(sid, "modify", "Cherry Limeade", "mini", 1, 1.59)
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, 10.19, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=False)
    def test_ordering_large_up_front_matches_resizing_later(self, _mock_hh):
        sid_a = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid_a, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid_a, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid_a, "add", "Cherry Limeade", "large", 1, 3.39)
        total_a = order_state_singleton.get_order_summary(sid_a).total

        sid_b = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid_b, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19)
        order_state_singleton.handle_order_update(sid_b, "add", "Tots", "medium", 1, 2.79)
        order_state_singleton.handle_order_update(sid_b, "add", "Cherry Limeade", "medium", 1, 2.89)
        order_state_singleton.handle_order_update(sid_b, "modify", "Cherry Limeade", "large", 1, 3.39)
        total_b = order_state_singleton.get_order_summary(sid_b).total

        assert math.isclose(total_a, 10.69, rel_tol=1e-9)
        assert math.isclose(total_a, total_b, rel_tol=1e-9)

        order_state_singleton.handle_order_update(sid_b, "modify", "Cherry Limeade", "medium", 1, 2.89)
        assert math.isclose(order_state_singleton.get_order_summary(sid_b).total, 10.19, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=False)
    def test_quantity_two_component_upcharges_split_and_repeated_resize_targets_remaining_unit(self, _mock_hh):
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", "SuperSONIC® Double Cheeseburger Combo", "standard", 2, 10.19)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "medium", 2, 2.79)
        order_state_singleton.handle_order_update(sid, "add", "Cherry Limeade", "medium", 2, 2.89)

        result = order_state_singleton.handle_order_update(sid, "modify", "Cherry Limeade", "large", 1, 3.39)
        assert result["combo_component_upcharge_display"] == "$0.50"
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2
        assert sorted((item.quantity, item.price, item.componentUpcharges) for item in items) == [
            (1, 10.19, [0.0, 0.0]),
            (1, 10.69, [0.0, 0.5]),
        ]
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, 20.88, rel_tol=1e-9)

        result = order_state_singleton.handle_order_update(sid, "modify", "Cherry Limeade", "large", 1, 3.39)
        assert result["combo_component_resized_from_size"] == "medium"
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2
        assert all(item.quantity == 1 for item in items)
        assert all(math.isclose(item.price, 10.69, rel_tol=1e-9) for item in items)
        assert all(item.componentUpcharges == [0.0, 0.5] for item in items)
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, 21.38, rel_tol=1e-9)
        readback = order_state_singleton.get_grouped_order_for_readback(sid)
        assert "two SuperSONIC" in readback
        assert "fifty cents upcharge" in readback

    @patch("order_state.is_happy_hour", return_value=False)
    @pytest.mark.parametrize(
        "steps",
        [
            [
                ("add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19),
                ("add", "Tots", "large", 1, 3.49),
                ("add", "Cherry Limeade", "large", 1, 3.39),
            ],
            [
                ("add", "Tots", "large", 1, 3.49),
                ("add", "Cherry Limeade", "large", 1, 3.39),
                ("add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19),
            ],
            [
                ("add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19),
                ("add", "Cherry Limeade", "medium", 1, 2.89),
                ("add", "Tots", "medium", 1, 2.79),
                ("modify", "Tots", "large", 1, 3.49),
                ("modify", "Cherry Limeade", "large", 1, 3.39),
            ],
            [
                ("add", "SuperSONIC® Double Cheeseburger Combo", "standard", 1, 10.19),
                ("add", "Tots", "medium", 1, 2.79),
                ("add", "Cherry Limeade", "medium", 1, 2.89),
                ("modify", "Cherry Limeade", "large", 1, 3.39),
                ("modify", "Tots", "large", 1, 3.49),
            ],
        ],
    )
    def test_component_upcharge_total_is_path_independent_across_fill_and_resize_orders(self, _mock_hh, steps):
        sid = order_state_singleton.create_session()
        for step in steps:
            order_state_singleton.handle_order_update(sid, *step)

        summary = order_state_singleton.get_order_summary(sid)
        item = order_state_singleton.get_order_items(sid)[0]
        assert math.isclose(summary.total, 11.39, rel_tol=1e-9)
        assert item.componentUpcharges == [0.70, 0.5]

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
        # Combo price plus the full-price component upcharge; absorbed drink is not happy-hour discounted.
        assert math.isclose(summary.total, 9.19 + 0.50, rel_tol=1e-9)

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
        assert math.isclose(summary.total, 9.19 + 0.90, rel_tol=1e-9)

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
    `add` of the same item at a different size while the slot is already full -- end up
    at the exact same correct state: one combo line, no standalone duplicate drink line.

    PR #184 round 2 (Rick's review, item 1): pricing is now a PURE function of the
    bundle's own final state, not a stateful delta/"free reference" computation -- a
    side or drink is included in the combo AT ANY SIZE on an "includedAnySize" pack
    (this persona's own `bundles.resizeRule`, the implicit default), so the combo's own price
    never changes no matter what size fills its slots, in either direction (upsize or
    downsize), and ordering a size up front costs exactly the same as resizing into it
    afterward -- see `test_same_total_regardless_of_order_path` below for the general
    property this guarantees."""

    COMBO = "SuperSONIC® Double Cheeseburger Combo"
    COMBO_PRICE = 10.19
    DRINK = "Diet Coke®"
    DRINK_MEDIUM_PRICE = 2.49
    DRINK_LARGE_PRICE = 2.99
    SIDE_LARGE_PRICE = 3.49

    def _seed_combo_with_large_side_and_medium_drink(self, sid):
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "standard", 1, self.COMBO_PRICE)
        order_state_singleton.handle_order_update(sid, "add", "Tots", "large", 1, self.SIDE_LARGE_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "medium", 1, self.DRINK_MEDIUM_PRICE)

    def test_remove_then_add_resizes_drink_in_place(self):
        """The exact #179 sequence: `remove Diet Coke Medium` must vacate the combo's
        drink slot (not no-op), and the following `add Diet Coke Large` must refill that
        slot as a resize (not a standalone duplicate), with NO price change at all --
        the combo is included AT ANY SIZE on this ("includedAnySize") pack."""
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
        assert math.isclose(summary.total, self.COMBO_PRICE + 0.70 + 0.50, rel_tol=1e-9)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]

    def test_explicit_modify_resizes_drink_in_place_identically(self):
        """The explicit resize/modify path must price identically to remove-then-add --
        flat combo pricing either way."""
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
        assert math.isclose(summary.total, self.COMBO_PRICE + 0.70 + 0.50, rel_tol=1e-9)

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
        assert math.isclose(summary.total, self.COMBO_PRICE + 0.70 + 0.50, rel_tol=1e-9)

    def test_downsize_is_also_free_no_credit_below_base_price(self):
        """Resizing DOWN must NOT credit anything below the combo's own base price --
        Rick's review, item 1 ("no credit below the base price on downsizing"):
        establish a Large drink, then downsize to Medium; the combo's own price never
        moves either way on an "includedAnySize" pack."""
        sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "standard", 1, self.COMBO_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.DRINK, "medium", 1, self.DRINK_MEDIUM_PRICE
        )
        assert result.get("resized_combo_component") == "drinks"
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_PRICE, rel_tol=1e-9)

    def test_same_total_regardless_of_order_path(self):
        """Rick's review, item 1: ordering a size up front and ordering the base size then
        resizing to that size must total EXACTLY the same -- no path dependence."""
        up_front_sid = order_state_singleton.create_session()
        order_state_singleton.handle_order_update(up_front_sid, "add", self.COMBO, "standard", 1, self.COMBO_PRICE)
        order_state_singleton.handle_order_update(up_front_sid, "add", "Tots", "large", 1, self.SIDE_LARGE_PRICE)
        order_state_singleton.handle_order_update(up_front_sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE)
        up_front_total = order_state_singleton.get_order_summary(up_front_sid).total

        resized_sid = order_state_singleton.create_session()
        self._seed_combo_with_large_side_and_medium_drink(resized_sid)  # drink starts Medium
        order_state_singleton.handle_order_update(
            resized_sid, "modify", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE
        )
        resized_total = order_state_singleton.get_order_summary(resized_sid).total

        assert math.isclose(up_front_total, resized_total, rel_tol=1e-9)
        assert math.isclose(up_front_total, self.COMBO_PRICE + 0.70 + 0.50, rel_tol=1e-9)

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
        assert math.isclose(summary.total, self.COMBO_PRICE + 0.70 + 3.39, rel_tol=1e-9)

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
        assert math.isclose(summary.total, self.COMBO_PRICE + 0.70, rel_tol=1e-9)


# ---------------------------------------------------------------------------
# PR #184 round 2 (Rick's review, items 2 & 3): per-combo-instance slot state --
# determinism across two SEPARATE combo lines, per-physical-unit independence within
# one quantity-N line, and removal clearing a combo's slot state with no extra code.
# ---------------------------------------------------------------------------

class TestComboInstanceDeterminismAndLifecycle:
    COMBO = "Delta Classic Meal"
    COMBO_REGULAR_PRICE = 5.49
    COMBO_LARGE_PRICE = 6.99

    def _new_session(self):
        return order_state_singleton.create_session(persona=_delta_persona())

    def test_two_separate_combos_resize_targets_the_one_that_holds_the_item(self):
        """Rick's review, item 2: with TWO SEPARATE combo lines (not one quantity-2 line),
        resizing a drink by name must target whichever instance actually holds that exact
        drink -- determinism, not an arbitrary/first-match pick."""
        sid = self._new_session()
        # Two distinct combo instances (different sizes) -- each its own OrderItem line.
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "regular", 1, self.COMBO_REGULAR_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "large", 1, self.COMBO_LARGE_PRICE)
        order_state_singleton.handle_order_update(sid, "add", "Delta Fries", "regular", 1, 1.99)
        order_state_singleton.handle_order_update(sid, "add", "Delta Onion Rings", "regular", 1, 1.79)
        order_state_singleton.handle_order_update(sid, "add", "Delta Iced Tea", "regular", 1, 1.89)
        order_state_singleton.handle_order_update(sid, "add", "Delta Latte", "regular", 1, 3.49)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2
        regular_combo = next(i for i in items if i.item == self.COMBO and i.size == "regular")
        large_combo = next(i for i in items if i.item == self.COMBO and i.size == "large")
        # The most recently added instance is scanned first for a vacant slot.
        assert "Delta Iced Tea" in large_combo.display
        assert "Delta Latte" in regular_combo.display

        # Resize the drink that only the regular line holds; the large line stays untouched.
        result = order_state_singleton.handle_order_update(
            sid, "modify", "Delta Latte", "large", 1, 4.29
        )
        assert result.get("resized_combo_component") == "drinks"
        items = order_state_singleton.get_order_items(sid)
        regular_combo = next(i for i in items if i.item == self.COMBO and i.size == "regular")
        large_combo = next(i for i in items if i.item == self.COMBO and i.size == "large")
        assert "Large Delta Latte" in regular_combo.display
        assert "Delta Iced Tea" in large_combo.display
        assert "Delta Latte" not in large_combo.display
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_REGULAR_PRICE + self.COMBO_LARGE_PRICE, rel_tol=1e-9)

    def test_quantity_two_combo_slots_are_independent_per_physical_unit(self):
        """Rick's review, item 2: one quantity-2 combo line has TWO independent slots per
        component -- filling only one unit's side must leave the combo incomplete (the
        second unit still needs its own side), not silently count as "done" for both."""
        sid = self._new_session()
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "regular", 2, self.COMBO_REGULAR_PRICE)
        order_state_singleton.handle_order_update(sid, "add", "Delta Fries", "regular", 1, 1.99)
        order_state_singleton.handle_order_update(sid, "add", "Delta Latte", "regular", 1, 3.49)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].quantity == 2
        # Only one of the two units' side/drink slots is filled so far.
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]

        order_state_singleton.handle_order_update(sid, "add", "Delta Onion Rings", "regular", 1, 1.79)
        order_state_singleton.handle_order_update(sid, "add", "Delta Iced Tea", "regular", 1, 1.89)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_REGULAR_PRICE * 2, rel_tol=1e-9)

    def test_removing_combo_clears_its_slot_state_new_combo_starts_fresh(self):
        """Rick's review, item 3: removing a combo line must clear ITS slot state with no
        extra cleanup code (state lives ON the OrderItem) -- a brand-new combo added
        afterward (even the identical item/size) must never inherit the old one's filled
        slots or completeness."""
        sid = self._new_session()
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "regular", 1, self.COMBO_REGULAR_PRICE)
        order_state_singleton.handle_order_update(sid, "add", "Delta Fries", "regular", 1, 1.99)
        order_state_singleton.handle_order_update(sid, "add", "Delta Latte", "regular", 1, 3.49)
        req = order_state_singleton.get_combo_requirements(sid)
        assert req["is_complete"]

        order_state_singleton.handle_order_update(sid, "remove", self.COMBO, "regular", 1, 0.0)
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 0

        # A brand-new combo instance -- same item, same size -- must start incomplete again.
        order_state_singleton.handle_order_update(sid, "add", self.COMBO, "regular", 1, self.COMBO_REGULAR_PRICE)
        req = order_state_singleton.get_combo_requirements(sid)
        assert not req["is_complete"]
        new_combo = order_state_singleton.get_order_items(sid)[0]
        assert "Delta Fries" not in new_combo.display
        assert "Delta Latte" not in new_combo.display
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.COMBO_REGULAR_PRICE, rel_tol=1e-9)

    @patch("order_state.is_happy_hour", return_value=True)
    def test_happy_hour_does_not_discount_a_resized_combo_drink(self, _mock_hh):
        """Happy hour's 50% drink discount must never apply to a combo's absorbed drink,
        even immediately after that slot was explicitly resized -- a resize never promotes
        the slot to a separately priced/discounted order line."""
        sid = order_state_singleton.create_session(persona=_epsilon_persona())
        order_state_singleton.handle_order_update(sid, "add", "Epsilon Snack Meal", "medium", 1, 5.99)
        order_state_singleton.handle_order_update(sid, "add", "Epsilon Cola", "medium", 1, 1.50)

        result = order_state_singleton.handle_order_update(
            sid, "modify", "Epsilon Cola", "large", 1, 2.00
        )
        assert result.get("resized_combo_component") == "drinks"
        summary = order_state_singleton.get_order_summary(sid)
        # Still just the flat combo price -- no discount, no upcharge, no standalone line.
        assert math.isclose(summary.total, 6.99, rel_tol=1e-9)


# "Make it a large meal" ("wholeBundleSize" packs) is covered in its own sibling test file,
# kept separate so its real-pack-name baseline exception doesn't collide with this file's
# existing brand-specific baseline entry.


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
