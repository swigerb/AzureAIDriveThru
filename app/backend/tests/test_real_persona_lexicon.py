"""Issue #304: the real, shipped persona pack's pronunciation lexicon and spokenName overrides.

Split out of test_menu_utils.py so its own rebrand_baseline.yaml entry tracks exactly this one
brand (the ratchet forbids adding a second brand to a file that already tracks another).
"""
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))
import menu_utils  # noqa: E402

_REPO_ROOT = Path(__file__).resolve().parents[3]
_PERSONAS_DIR = _REPO_ROOT / "personas"

class RealPersonaPronunciationsAndSpokenNameTests(unittest.TestCase):
    """Issue #304: exercise the real, shipped Munchkins-lexicon persona pack's ``pronunciations``
    entry and its menu items' ``spokenName`` overrides through the normal ``MenuCatalog`` loading
    path -- proving the feature works end-to-end on real pack data, not just the primitive above.

    #313 (Rick's re-review, item 1): this class's whole point is to validate the REAL, shipped
    pack's own lexicon data (the synthetic test-zeta fixture has no Munchkins-equivalent
    ``pronunciations`` entry to exercise), so it names the real persona id directly rather than
    string-concatenating around rebrand_scan.py's brand-word guard -- see this file's own
    rebrand_baseline.yaml entry (issue #304)."""

    @classmethod
    def setUpClass(cls):
        from persona_loader import PersonaCatalog

        catalog = PersonaCatalog.load(personas_dir=_PERSONAS_DIR)
        cls.persona = catalog.get("dunkin")
        cls.menu = menu_utils.get_catalog_for_persona(cls.persona)

    def test_persona_declares_the_munchkins_pronunciation(self):
        self.assertEqual(self.persona.manifest.pronunciations.get("Munchkins"), "Munch-kins")

    def test_munchkins_item_spoken_name_is_applied_via_spoken(self):
        spoken = self.menu.spoken("Glazed MUNCHKINS® Donut Hole Treats")
        self.assertEqual(spoken, "Glazed Munch-kins Donut Hole Treats")

    def test_unrelated_item_is_unaffected_by_spoken_name_overrides(self):
        for name in ("Original Blend Iced Coffee",):
            self.assertEqual(self.menu.spoken(name), name)


if __name__ == "__main__":
    unittest.main()

