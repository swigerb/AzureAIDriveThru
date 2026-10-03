import asyncio
import math
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import MenuCatalog
from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from rtmt import ToolResultDirection
from tools import update_order


class Issue188MenuExpansionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.persona = PersonaCatalog.load(enabled=["dun" + "kin"], default_persona_id="dun" + "kin").get("dun" + "kin")
        cls.menu = MenuCatalog.from_persona(cls.persona)

    def setUp(self):
        order_state_singleton.sessions = {}

    def _session(self) -> str:
        return order_state_singleton.create_session(persona=self.persona)

    def _add(self, session_id: str, item_name: str, size: str, quantity: int = 1):
        return asyncio.run(
            update_order(
                {
                    "action": "add",
                    "item_name": item_name,
                    "size": size,
                    "quantity": quantity,
                    "price": 0.01,
                },
                session_id,
            )
        )

    def test_large_hot_regular_coffee_modifiers_are_free_and_paid_swirl_is_extra(self):
        with patch("order_state.is_happy_hour", return_value=False):
            session_id = self._session()

            coffee = self._add(session_id, "Original Blend Coffee (Cream, Sugar)", "large")
            self.assertEqual(coffee.destination, ToolResultDirection.TO_BOTH)
            swirl = self._add(session_id, "Flavor Swirl Add-On", "standard")
            self.assertEqual(swirl.destination, ToolResultDirection.TO_BOTH)

            summary = order_state_singleton.get_order_summary(session_id)
            self.assertEqual([item.item for item in summary.items], ["Original Blend Coffee (Cream, Sugar)", "Flavor Swirl Add-On"])
            self.assertTrue(math.isclose(summary.items[0].price, 2.99, rel_tol=1e-9))
            self.assertTrue(math.isclose(summary.items[1].price, 0.75, rel_tol=1e-9))
            self.assertTrue(math.isclose(summary.total, 3.74, rel_tol=1e-9))

    def test_medium_iced_black_coffee_is_orderable_and_happy_hour_discounted(self):
        with patch("order_state.is_happy_hour", return_value=True):
            session_id = self._session()

            result = self._add(session_id, "Original Blend Iced Coffee (Black)", "medium")

            self.assertEqual(result.destination, ToolResultDirection.TO_BOTH)
            summary = order_state_singleton.get_order_summary(session_id)
            self.assertEqual(summary.items[0].item, "Original Blend Iced Coffee (Black)")
            self.assertTrue(math.isclose(summary.items[0].price, 3.59, rel_tol=1e-9))
            self.assertTrue(math.isclose(summary.total, 3.59 * 0.75, rel_tol=1e-9))

    def test_munchkins_flavors_and_counts_price_from_menu(self):
        with patch("order_state.is_happy_hour", return_value=False):
            session_id = self._session()

            glazed_result = self._add(session_id, "Glazed MUNCHKINS® Donut Hole Treats", "10 count", 2)
            chocolate_result = self._add(session_id, "Chocolate Glazed MUNCHKINS® Donut Hole Treats", "25 count", 1)

            summary = order_state_singleton.get_order_summary(session_id)
            self.assertEqual(len(summary.items), 2)
            self.assertEqual(summary.items[0].quantity, 2)
            self.assertEqual(summary.items[0].size, "10 count")
            self.assertEqual(summary.items[1].size, "25 count")
            self.assertTrue(math.isclose(summary.total, (2 * 3.99) + 8.99, rel_tol=1e-9))
            self.assertIn("Munchkins Donut Hole Treats", glazed_result.text)
            self.assertIn("Munchkins Donut Hole Treats", chocolate_result.text)
            self.assertIn("MUNCHKINS®", summary.items[0].item)
            self.assertIn("MUNCHKINS®", chocolate_result.to_client_text())
            readback = order_state_singleton.get_grouped_order_for_readback(session_id)
            self.assertIn("Glazed Munchkins Donut Hole Treats", readback)
            self.assertNotIn("MUNCHKINS", readback)
            self.assertNotIn("®", readback)

    def test_munchkins_spoken_substitution_respects_boundaries_and_trademark(self):
        text = "MUNCHKINSHIP PREMUNCHKINS MUNCHKINSON MUNCHKINS® MUNCHKINS"

        spoken = self.menu.spoken(text)

        self.assertEqual(spoken, "MUNCHKINSHIP PREMUNCHKINS MUNCHKINSON Munchkins Munchkins")

    def test_happy_hour_flags_match_pack_banner(self):
        self.assertFalse(self.menu.is_happy_hour_discounted("Original Blend Coffee"))
        self.assertTrue(self.menu.is_happy_hour_discounted("Original Blend Iced Coffee"))
        self.assertTrue(self.menu.is_happy_hour_discounted("Caramel Craze Latte"))
        self.assertTrue(self.menu.is_happy_hour_discounted("Strawberry Dragonfruit Refresher"))
        self.assertFalse(self.menu.is_happy_hour_discounted("Glazed Donut"))
        self.assertFalse(self.menu.is_happy_hour_discounted("Bacon Egg & Cheese on Croissant"))
        self.assertFalse(self.menu.is_happy_hour_discounted("Hash Browns"))

    def test_munchkins_size_aliases_resolve_to_count_sizes(self):
        item = self.menu.resolve_menu_item("Glazed Munchkins")
        self.assertIsNotNone(item)
        self.assertEqual(item["sizes"], ("10 count", "25 count", "50 count"))
        self.assertEqual(self.menu.canonical_size_key("10-count"), "10 count")
        self.assertEqual(self.menu.canonical_size_key("25 ct"), "25 count")
        self.assertEqual(self.menu.canonical_size_key("50"), "50 count")


if __name__ == "__main__":
    unittest.main()
