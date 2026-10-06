"""Neutral wholeBundleSize fixture coverage for PR #184.

The shared order-engine tests in this file intentionally use the test-only Epsilon fixture, not any
production persona. Epsilon has `bundles.resizeRule: "wholeBundleSize"`, one Small/Medium/Large
bundle, one Standard-only bundle whose side default is Medium, and Small/Medium/Large slot items.
"""

import asyncio
import json
import math
import sys
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

import pytest

from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from rtmt import ToolResultDirection
from tools import update_order

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _run(coro):
    return asyncio.run(coro)


@pytest.fixture(autouse=True)
def _reset_order_state():
    order_state_singleton.sessions = {}
    yield
    order_state_singleton.sessions = {}


def _wholebundlesize_persona():
    catalog = PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-epsilon"],
        default_persona_id="test-epsilon",
    )
    return catalog.get("test-epsilon")


def _new_session() -> str:
    return order_state_singleton.create_session(persona=_wholebundlesize_persona())


class TestWholeMealResize:
    MEAL = "Epsilon Snack Meal"
    MEAL_SMALL_PRICE = 4.99
    MEAL_MEDIUM_PRICE = 5.99
    MEAL_LARGE_PRICE = 6.99
    DRINK = "Epsilon Cola"
    DRINK_SMALL_PRICE = 1.00
    DRINK_MEDIUM_PRICE = 1.50
    DRINK_LARGE_PRICE = 2.00
    FRIES = "Epsilon Fries"
    STANDARD_ONLY_MEAL = "Epsilon Standard Meal"
    STANDARD_ONLY_MEAL_PRICE = 3.49

    def _assert_component_sizes(self, meal, expected_label, *unexpected_labels):
        assert meal.components
        assert all(expected_label in component for component in meal.components)
        for unexpected in unexpected_labels:
            assert all(unexpected not in component for component in meal.components)

    def _seed_meal(self, sid):
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "small", 1, self.DRINK_SMALL_PRICE)

    def test_make_it_a_large_meal_resizes_fries_and_drink_together(self):
        sid = _new_session()
        self._seed_meal(sid)

        meal = order_state_singleton.get_order_items(sid)[0]
        assert "Small Epsilon Fries" in meal.display
        assert "Small Epsilon Cola" in meal.display
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE
        )
        assert result.get("modified_to_size") == "large"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "large"
        assert math.isclose(meal.price, self.MEAL_LARGE_PRICE, rel_tol=1e-9)
        assert "Large Epsilon Fries" in meal.display
        assert "Large Epsilon Cola" in meal.display
        assert "Large Epsilon Fries" in meal.components
        assert "Large Epsilon Cola" in meal.components
        self._assert_component_sizes(meal, "Large", "Medium", "Small")
        assert "Medium" not in meal.display
        assert "Small" not in meal.display
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, self.MEAL_LARGE_PRICE, rel_tol=1e-9)

    def test_make_it_a_large_meal_is_a_noop_when_already_that_size(self):
        sid = _new_session()
        self._seed_meal(sid)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE
        )
        assert not result.get("modified_to_size")
        meal = order_state_singleton.get_order_items(sid)[0]
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)

    def test_modifying_the_autofilled_fries_by_their_real_menu_name_is_found(self):
        sid = _new_session()
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "medium", 1, 0)
        assert result.get("combo_component_resize_rejected") is None
        assert result.get("resized_combo_component") == "sides"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "medium"
        assert math.isclose(meal.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in meal.display

    def test_component_resize_is_rejected_cleanly_when_the_pack_has_no_price_at_that_size(self):
        sid = _new_session()
        order_state_singleton.handle_order_update(
            sid, "add", self.STANDARD_ONLY_MEAL, "standard", 1, self.STANDARD_ONLY_MEAL_PRICE
        )

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "large", 1, 0)
        assert result.get("combo_component_resize_rejected") == "sides"
        assert not result.get("resized_combo_component")

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "standard"
        assert math.isclose(meal.price, self.STANDARD_ONLY_MEAL_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in meal.display
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, self.STANDARD_ONLY_MEAL_PRICE, rel_tol=1e-9)

    def test_downsizing_a_component_resizes_the_whole_meal_never_leaves_mixed_sizes(self):
        sid = _new_session()
        self._seed_meal(sid)
        order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.DRINK, "medium", 1, 0)
        assert result.get("resized_combo_component") == "drinks"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "medium"
        assert math.isclose(meal.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in meal.display
        assert "Medium Epsilon Cola" in meal.display
        assert "Medium Epsilon Fries" in meal.components
        assert "Medium Epsilon Cola" in meal.components
        self._assert_component_sizes(meal, "Medium", "Large", "Small")
        assert "Large" not in meal.display

    def test_resizing_the_meal_back_down_relabels_every_slot_no_stale_size(self):
        sid = _new_session()
        self._seed_meal(sid)
        order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)
        assert result.get("modified_to_size") == "small"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "small"
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)
        assert "Small Epsilon Fries" in meal.display
        assert "Small Epsilon Cola" in meal.display
        assert "Small Epsilon Fries" in meal.components
        assert "Small Epsilon Cola" in meal.components
        self._assert_component_sizes(meal, "Small", "Large", "Medium")
        assert "Large" not in meal.display
        assert "Medium" not in meal.display

    def test_make_it_a_medium_meal_is_accepted_directly_not_rejected(self):
        sid = _new_session()
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "medium", 1, self.MEAL_MEDIUM_PRICE
        )
        assert result.get("modified_to_size") == "medium"
        assert math.isclose(order_state_singleton.get_order_items(sid)[0].price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)

    def test_resizing_one_units_fries_on_a_quantity_two_meal_splits_that_unit_only(self):
        sid = _new_session()
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 2, self.MEAL_SMALL_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "medium", 1, 0)
        assert result.get("resized_combo_component") == "sides"

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2
        resized = next(i for i in items if i.size == "medium")
        untouched = next(i for i in items if i.size == "small")
        assert resized.quantity == 1
        assert untouched.quantity == 1
        assert math.isclose(resized.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert math.isclose(untouched.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in resized.display
        assert "Small Epsilon Fries" in untouched.display
        assert math.isclose(
            order_state_singleton.get_order_summary(sid).total,
            self.MEAL_MEDIUM_PRICE + self.MEAL_SMALL_PRICE,
            rel_tol=1e-9,
        )

    @pytest.mark.parametrize(
        ("drink_size", "drink_price", "drink_label"),
        [("small", DRINK_SMALL_PRICE, "Small"), ("medium", DRINK_MEDIUM_PRICE, "Medium"), ("large", DRINK_LARGE_PRICE, "Large")],
    )
    def test_standard_only_bundle_absorbs_small_medium_or_large_drink(self, drink_size, drink_price, drink_label):
        sid = _new_session()
        order_state_singleton.handle_order_update(
            sid, "add", self.STANDARD_ONLY_MEAL, "standard", 1, self.STANDARD_ONLY_MEAL_PRICE
        )
        result = order_state_singleton.handle_order_update(sid, "add", self.DRINK, drink_size, 1, drink_price)
        assert result.get("absorbed_into_combo") is True

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        meal = items[0]
        assert meal.size == "standard"
        assert f"{drink_label} Epsilon Cola" in meal.display
        assert order_state_singleton.get_combo_requirements(sid)["is_complete"] is True
        assert math.isclose(order_state_singleton.get_order_summary(sid).total, self.STANDARD_ONLY_MEAL_PRICE, rel_tol=1e-9)

    def test_first_absorption_is_path_independent_and_keeps_component_size(self):
        def snapshot(steps):
            sid = _new_session()
            for step in steps:
                order_state_singleton.handle_order_update(sid, *step)
            meal = order_state_singleton.get_order_items(sid)[0]
            return (meal.item, meal.size, meal.price, meal.display, order_state_singleton.get_order_summary(sid).total)

        meal_then_drink = snapshot([
            ("add", self.MEAL, "medium", 1, self.MEAL_MEDIUM_PRICE),
            ("add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE),
        ])
        drink_then_meal = snapshot([
            ("add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE),
            ("add", self.MEAL, "medium", 1, self.MEAL_MEDIUM_PRICE),
        ])

        assert meal_then_drink == drink_then_meal
        assert math.isclose(meal_then_drink[2], self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert math.isclose(meal_then_drink[4], self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in meal_then_drink[3]
        assert "Large Epsilon Cola" in meal_then_drink[3]

    def test_components_list_is_rebuilt_when_default_sized_meal_is_resized_before_drink(self):
        sid = _new_session()
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "medium", 1, self.MEAL_MEDIUM_PRICE)
        order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "large", 1, self.DRINK_LARGE_PRICE)

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "large"
        assert "Large Epsilon Fries" in meal.display
        assert "Large Epsilon Cola" in meal.display
        assert meal.components == ["Large Epsilon Fries", "Large Epsilon Cola"]
        assert "Medium Epsilon Fries" not in meal.components


class TestWholeBundleSizeToolReplies:
    def test_rejected_component_resize_returns_structured_rejection_not_success_text(self):
        sid = _new_session()
        _run(update_order({
            "action": "add", "item_name": TestWholeMealResize.STANDARD_ONLY_MEAL,
            "size": "standard", "quantity": 1, "price": 0,
        }, sid))

        result = _run(update_order({
            "action": "modify", "item_name": TestWholeMealResize.FRIES,
            "size": "large", "quantity": 1, "price": 0,
        }, sid))

        assert result.destination == ToolResultDirection.TO_SERVER
        assert result.text["status"] == "rejected"
        assert result.text["reason"] == "combo_component_resize_rejected"
        assert result.text["item_name"] == TestWholeMealResize.FRIES
        assert "can't be resized by itself" in result.text["message"]
        assert "Changed" not in json.dumps(result.text)

    @pytest.mark.parametrize("requested_size", ["", "standard"])
    def test_missing_or_standard_bundle_size_defaults_to_bundle_default_size(self, requested_size):
        sid = _new_session()
        result = _run(update_order({
            "action": "add", "item_name": TestWholeMealResize.MEAL,
            "size": requested_size, "quantity": 1, "price": 0,
        }, sid))

        assert result.destination == ToolResultDirection.TO_BOTH
        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "medium"
        assert math.isclose(meal.price, TestWholeMealResize.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium Epsilon Fries" in meal.display

    @pytest.mark.parametrize("requested_size", ["", "standard"])
    def test_default_sized_meal_resize_refreshes_component_sub_lines(self, requested_size):
        sid = _new_session()
        _run(update_order({
            "action": "add", "item_name": TestWholeMealResize.MEAL,
            "size": requested_size, "quantity": 1, "price": 0,
        }, sid))
        _run(update_order({
            "action": "modify", "item_name": TestWholeMealResize.MEAL,
            "size": "large", "quantity": 1, "price": 0,
        }, sid))
        _run(update_order({
            "action": "add", "item_name": TestWholeMealResize.DRINK,
            "size": "large", "quantity": 1, "price": 0,
        }, sid))

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "large"
        assert meal.components == ["Large Epsilon Fries", "Large Epsilon Cola"]
        assert "Medium Epsilon Fries" not in meal.components
