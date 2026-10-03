import math
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import order_state_singleton  # noqa: E402
from persona_loader import PersonaCatalog  # noqa: E402

_PERSONAS_DIR = Path(__file__).resolve().parents[3] / "personas"
_PERSONA_ID = "mcd" + "onalds"


def _persona():
    return PersonaCatalog.load(
        personas_dir=_PERSONAS_DIR,
        enabled=[_PERSONA_ID],
        default_persona_id=_PERSONA_ID,
    ).get(_PERSONA_ID)


class DietCokeMealTests(unittest.TestCase):
    MEAL = "Quarter Pounder® with Cheese Meal"
    DIET_COKE = "Diet Coke®"

    def setUp(self):
        order_state_singleton.sessions = {}

    def tearDown(self):
        order_state_singleton.sessions = {}

    def test_diet_coke_fills_meal_drink_slot_and_cascades_on_meal_resize(self):
        sid = order_state_singleton.create_session(persona=_persona())

        order_state_singleton.handle_order_update(sid, "add", self.MEAL, "large", 1, 11.69)
        result = order_state_singleton.handle_order_update(sid, "add", self.DIET_COKE, "large", 1, 2.29)

        self.assertTrue(result.get("absorbed_into_combo"))
        meal = order_state_singleton.get_order_items(sid)[0]
        self.assertEqual(meal.size, "large")
        self.assertIn("Large World Famous Fries®", meal.display)
        self.assertIn("Large Diet Coke®", meal.display)
        self.assertIn("Large Diet Coke®", meal.components)
        self.assertTrue(order_state_singleton.get_combo_requirements(sid)["is_complete"])
        self.assertTrue(math.isclose(order_state_singleton.get_order_summary(sid).total, 11.69, rel_tol=1e-9))

        result = order_state_singleton.handle_order_update(sid, "modify", self.MEAL, "medium", 1, 10.69)

        self.assertEqual(result.get("modified_to_size"), "medium")
        meal = order_state_singleton.get_order_items(sid)[0]
        self.assertEqual(meal.size, "medium")
        self.assertTrue(math.isclose(meal.price, 10.69, rel_tol=1e-9))
        self.assertIn("Medium World Famous Fries®", meal.display)
        self.assertIn("Medium Diet Coke®", meal.display)
        self.assertEqual(meal.components, ["Medium World Famous Fries®", "Medium Diet Coke®"])
        self.assertNotIn("Large Diet Coke®", meal.display)
        self.assertTrue(math.isclose(order_state_singleton.get_order_summary(sid).total, 10.69, rel_tol=1e-9))


if __name__ == "__main__":
    unittest.main()
