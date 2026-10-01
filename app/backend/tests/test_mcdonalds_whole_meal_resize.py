"""PR #184 round 2 (Rick's review, item 1 -- "wholeBundleSize" packs): McDonald's "make it a
large meal" ports the original McDonald's app's own meal-size pricing/phrasing -- resizing the
MEAL (not a single component) changes the meal's own price and relabels fries/drink together,
via the real production McDonald's persona pack. Shared order-engine code stays brand-agnostic
(driven entirely by the pack's own `bundles.resizeRule: "wholeBundleSize"`); this dedicated test
file is the one place allowed to name a real pack directly, to prove the engine's generic
mechanism actually produces that pack's real behavior end-to-end.

Kept in its own file (rather than folded into the sibling combo-orders test file) so its
real-pack-name baseline exception doesn't collide with that larger file's own separate
baseline entry -- see `rebrand_baseline.yaml`.
"""

import math
import sys
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

import pytest

from order_state import order_state_singleton
from persona_loader import PersonaCatalog


@pytest.fixture(autouse=True)
def _reset_order_state():
    """Ensure each test starts with a clean OrderState."""
    order_state_singleton.sessions = {}
    yield
    order_state_singleton.sessions = {}


def _mcdonalds_persona():
    catalog = PersonaCatalog.load(enabled=["mcdonalds"], default_persona_id="mcdonalds")
    return catalog.get("mcdonalds")


class TestMcDonaldsWholeMealResize:
    MEAL = "Big Mac® Meal"
    MEAL_STANDARD_PRICE = 8.99
    MEAL_LARGE_PRICE = 10.09
    DRINK = "Coca-Cola®"
    DRINK_MEDIUM_PRICE = 1.79

    def _seed_meal(self, sid):
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "standard", 1, self.MEAL_STANDARD_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "medium", 1, self.DRINK_MEDIUM_PRICE)

    def test_make_it_a_large_meal_resizes_fries_and_drink_together(self):
        """"Make it a large meal" on a "wholeBundleSize" pack resizes the WHOLE bundle --
        its own price changes to the meal's Large price, and BOTH the autofilled fries and
        the guest's chosen drink are relabeled to Large, matching the original McDonald's
        app's own behavior/phrasing."""
        sid = order_state_singleton.create_session(persona=_mcdonalds_persona())
        self._seed_meal(sid)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        meal = items[0]
        assert "Medium World Famous Fries®" in meal.display  # bundle.autoFill default
        assert "Medium Coca-Cola®" in meal.display
        assert math.isclose(meal.price, self.MEAL_STANDARD_PRICE, rel_tol=1e-9)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE
        )
        assert result.get("modified_to_size") == "large"

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        meal = items[0]
        assert meal.size == "large"
        assert math.isclose(meal.price, self.MEAL_LARGE_PRICE, rel_tol=1e-9)
        assert "Large World Famous Fries®" in meal.display
        assert "Large Coca-Cola®" in meal.display
        assert "Medium" not in meal.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_LARGE_PRICE, rel_tol=1e-9)

    def test_make_it_a_large_meal_is_a_noop_when_already_that_size(self):
        """Resizing to the meal's own CURRENT size must no-op -- never a double-charge or a
        redundant relabel."""
        sid = order_state_singleton.create_session(persona=_mcdonalds_persona())
        self._seed_meal(sid)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "standard", 1, self.MEAL_STANDARD_PRICE
        )
        assert not result.get("modified_to_size")
        meal = order_state_singleton.get_order_items(sid)[0]
        assert math.isclose(meal.price, self.MEAL_STANDARD_PRICE, rel_tol=1e-9)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_STANDARD_PRICE, rel_tol=1e-9)
