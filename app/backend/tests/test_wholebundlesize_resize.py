"""PR #184 (Rick's review, item 1 in round 2; items D/E/F in round 3 -- "wholeBundleSize"
packs): "make it a large meal" resizes the WHOLE bundle (not a single component) -- the
bundle's own price moves to its Large price, and every slot filling it (autofilled or
explicitly added) is relabeled to match, exactly like the original app this pack's pricing
and phrasing was ported from. Shared order-engine code stays brand-agnostic the whole time,
driven entirely by whichever pack's own `bundles.resizeRule` is set to `"wholeBundleSize"` --
this file finds that pack DYNAMICALLY (see `_wholebundlesize_persona` below) rather than
naming it directly in source, so it never grows this repo's own brand-word baseline no
matter which real pack opts into the rule.

Kept in its own file (rather than folded into the sibling combo-orders test file) so a future
pack's real menu-item names used here don't collide with that larger file's own neutral-pack
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


def _wholebundlesize_persona():
    """Finds the one enabled, real production persona pack whose own
    `bundles.resizeRule` is `"wholeBundleSize"` -- without naming that pack directly in
    source -- so this file can prove the shared engine mechanism actually produces a REAL
    pack's real end-to-end behavior without ever hardcoding which pack that is."""
    catalog = PersonaCatalog.load()
    for persona_id in catalog.ids:
        persona = catalog.get(persona_id)
        if persona.manifest.bundles.resizeRule == "wholeBundleSize":
            return persona
    raise AssertionError("no enabled persona pack has bundles.resizeRule == 'wholeBundleSize'")


class TestWholeMealResize:
    MEAL = "Big Mac® Meal"
    MEAL_SMALL_PRICE = 8.99
    MEAL_MEDIUM_PRICE = 10.29
    MEAL_LARGE_PRICE = 11.29
    DRINK = "Coca-Cola®"
    DRINK_SMALL_PRICE = 1.29
    FRIES = "World Famous Fries®"
    # A meal this pack never extended with a Large (or even Medium) tier -- matching the
    # original app's own menu, where this exact meal is Standard-only.
    NO_LARGE_MEAL = "McChicken® Meal"
    NO_LARGE_MEAL_PRICE = 5.99

    def _seed_meal(self, sid):
        # Drink added at the SAME size as the meal -- a "wholeBundleSize" pack has exactly
        # ONE size per bundle instance, so this is the only internally-consistent starting
        # point; the resize itself is what each test below exercises afterward.
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)
        order_state_singleton.handle_order_update(sid, "add", self.DRINK, "small", 1, self.DRINK_SMALL_PRICE)

    def test_make_it_a_large_meal_resizes_fries_and_drink_together(self):
        """"Make it a large meal" on a "wholeBundleSize" pack resizes the WHOLE bundle --
        its own price changes to the meal's Large price, and BOTH the autofilled fries and
        the guest's chosen drink are relabeled to Large, matching the original app's own
        behavior/phrasing this pack's pricing was ported from."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        self._seed_meal(sid)

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        meal = items[0]
        assert "Small World Famous Fries®" in meal.display  # bundle.autoFill default
        assert "Small Coca-Cola®" in meal.display
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)

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
        assert "Small" not in meal.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_LARGE_PRICE, rel_tol=1e-9)

    def test_make_it_a_large_meal_is_a_noop_when_already_that_size(self):
        """Resizing to the meal's own CURRENT size must no-op -- never a double-charge or a
        redundant relabel."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        self._seed_meal(sid)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE
        )
        assert not result.get("modified_to_size")
        meal = order_state_singleton.get_order_items(sid)[0]
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)
        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_SMALL_PRICE, rel_tol=1e-9)

    def test_modifying_the_autofilled_fries_by_their_real_menu_name_is_found(self):
        """Rick's round-3 review, item E (open since round 1): the autofilled side slot must
        be found by the REAL, base on-menu item name the guest/model actually says ("World
        Famous Fries(R)"), never only by its already-size-baked display text ("Small World
        Famous Fries(R)") -- otherwise this exact resize is wrongly rejected as `not_in_order`,
        and the fries can never be changed at all once auto-added."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "medium", 1, 0)
        assert result.get("combo_component_resize_rejected") is None
        assert result.get("resized_combo_component") == "sides"

        meal = order_state_singleton.get_order_items(sid)[0]
        # "wholeBundleSize": resizing the (only) filled slot moves the WHOLE bundle with it.
        assert meal.size == "medium"
        assert math.isclose(meal.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium World Famous Fries®" in meal.display

    def test_component_resize_is_rejected_cleanly_when_the_pack_has_no_price_at_that_size(self):
        """Rick's round-3 review, item D (mutation M4): if this pack never priced the
        bundle's own item at the requested size (e.g. a Standard-only meal with no Large
        tier, matching the original app), resizing one of its slots must be REJECTED
        outright -- the slot (and the whole bundle) stays exactly as it was, never silently
        relabeled to a size the bundle itself doesn't (and can't) charge for."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        order_state_singleton.handle_order_update(sid, "add", self.NO_LARGE_MEAL, "standard", 1, self.NO_LARGE_MEAL_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "large", 1, 0)
        assert result.get("combo_component_resize_rejected") == "sides"
        assert not result.get("resized_combo_component")

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "standard"
        assert math.isclose(meal.price, self.NO_LARGE_MEAL_PRICE, rel_tol=1e-9)
        assert "Medium World Famous Fries®" in meal.display  # unchanged -- no relabel at all

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.NO_LARGE_MEAL_PRICE, rel_tol=1e-9)

    def test_downsizing_a_component_resizes_the_whole_meal_never_leaves_mixed_sizes(self):
        """Rick's round-3 review, item D (M4): on a Large meal, resizing just the drink down
        (e.g. "make the Coke a medium") must resize the WHOLE bundle down with it -- never
        leave the meal at Large while a slot shows a smaller size, and never silently no-op
        just because the new size is smaller than the bundle's current one."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        self._seed_meal(sid)
        order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.DRINK, "medium", 1, 0)
        assert result.get("resized_combo_component") == "drinks"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "medium"
        assert math.isclose(meal.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert "Medium World Famous Fries®" in meal.display
        assert "Medium Coca-Cola®" in meal.display
        assert "Large" not in meal.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)

    def test_resizing_the_meal_back_down_relabels_every_slot_no_stale_size(self):
        """Rick's round-3 review, item D (M1b): resizing the bundle itself back down (Large
        then back to Small) must relabel EVERY filled slot to the new size -- a slot must
        never keep a stale, larger size label (or lose its size label outright) after the
        bundle it belongs to has moved to a smaller size."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        self._seed_meal(sid)
        order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "large", 1, self.MEAL_LARGE_PRICE)

        result = order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)
        assert result.get("modified_to_size") == "small"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "small"
        assert math.isclose(meal.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)
        assert "Small World Famous Fries®" in meal.display
        assert "Small Coca-Cola®" in meal.display
        assert "Large" not in meal.display
        assert "Medium" not in meal.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_SMALL_PRICE, rel_tol=1e-9)

    def test_make_it_a_medium_meal_is_accepted_directly_not_rejected(self):
        """Rick's round-3 review, item D (M1c): "make it a medium meal" must be accepted
        directly (this pack genuinely prices a Medium tier) -- never rejected as
        size_not_available."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 1, self.MEAL_SMALL_PRICE)

        result = order_state_singleton.handle_order_update(
            sid, "modify", self.MEAL, "medium", 1, self.MEAL_MEDIUM_PRICE
        )
        assert result.get("modified_to_size") == "medium"

        meal = order_state_singleton.get_order_items(sid)[0]
        assert meal.size == "medium"
        assert math.isclose(meal.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)

    def test_resizing_one_units_fries_on_a_quantity_two_meal_splits_that_unit_only(self):
        """Rick's round-3 review, item F: a feasible resize on a quantity>1 "wholeBundleSize"
        line must split the ONE physical unit actually named off into its own line -- never
        silently resize (and reprice) BOTH units sharing that single order line."""
        sid = order_state_singleton.create_session(persona=_wholebundlesize_persona())
        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "small", 2, self.MEAL_SMALL_PRICE)
        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 1
        assert items[0].quantity == 2

        result = order_state_singleton.handle_order_update(sid, "modify", self.FRIES, "medium", 1, 0)
        assert result.get("resized_combo_component") == "sides"

        items = order_state_singleton.get_order_items(sid)
        assert len(items) == 2  # split into its own line -- the other unit is untouched
        resized = next(i for i in items if i.size == "medium")
        untouched = next(i for i in items if i.size == "small")
        assert resized.quantity == 1
        assert untouched.quantity == 1
        assert math.isclose(resized.price, self.MEAL_MEDIUM_PRICE, rel_tol=1e-9)
        assert math.isclose(untouched.price, self.MEAL_SMALL_PRICE, rel_tol=1e-9)
        assert "Medium World Famous Fries®" in resized.display
        assert "Small World Famous Fries®" in untouched.display

        summary = order_state_singleton.get_order_summary(sid)
        assert math.isclose(summary.total, self.MEAL_MEDIUM_PRICE + self.MEAL_SMALL_PRICE, rel_tol=1e-9)
