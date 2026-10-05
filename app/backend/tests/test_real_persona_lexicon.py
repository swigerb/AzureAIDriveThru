"""Issue #304: every shipped persona pack's pronunciation lexicon and spokenName overrides.

Split out of test_menu_utils.py so its own rebrand_baseline.yaml entry tracks exactly this one
brand (the ratchet forbids adding a second brand to a file that already tracks another).

#313 (coordinator fix request, round 4): made brand-neutral -- generic over EVERY shipped pack
discovered from disk (``PersonaCatalog.load``) rather than naming one real persona id directly,
so this file no longer carries a rebrand_baseline.yaml entry at all. Anything needing a known,
deterministic value uses the ``test-zeta`` fixture pack instead of a real pack.
"""

import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))
import menu_utils  # noqa: E402
from menu_utils import apply_lexicon, get_catalog_for_persona  # noqa: E402
from persona_loader import PersonaCatalog  # noqa: E402

_REPO_ROOT = Path(__file__).resolve().parents[3]
_PERSONAS_DIR = _REPO_ROOT / "personas"
_FIXTURES_PERSONAS_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


class ShippedPersonaPronunciationLexiconTests(unittest.TestCase):
    """Exercises every shipped persona pack's own ``pronunciations`` lexicon (the
    ``MenuCatalog``/``cascade_processor`` TTS-input rewrite) through the normal
    ``PersonaCatalog.load`` discovery path -- proving the feature works end-to-end on
    whatever real pack data happens to declare it, without naming any one pack's id."""

    @classmethod
    def setUpClass(cls):
        cls.catalog = PersonaCatalog.load(personas_dir=_PERSONAS_DIR)

    def test_every_shipped_packs_pronunciation_lexicon_is_applied(self):
        any_pack_declares_a_lexicon = False
        for persona_id in self.catalog.ids:
            persona = self.catalog.get(persona_id)
            pronunciations = persona.manifest.pronunciations
            if not pronunciations:
                continue
            any_pack_declares_a_lexicon = True
            for raw_key, spoken_form in pronunciations.items():
                text = f"Example {raw_key} text"
                result = apply_lexicon(text, pronunciations)
                self.assertIn(spoken_form, result, f"{persona_id}: {raw_key!r} was not respelled")
                self.assertNotIn(raw_key, result, f"{persona_id}: raw key {raw_key!r} survived")

        self.assertTrue(
            any_pack_declares_a_lexicon,
            "expected at least one shipped persona pack to declare a pronunciations lexicon "
            "(otherwise this test passes vacuously)",
        )

    def test_every_shipped_packs_item_spoken_name_is_applied_via_spoken(self):
        any_pack_declares_a_spoken_name = False
        for persona_id in self.catalog.ids:
            persona = self.catalog.get(persona_id)
            menu = get_catalog_for_persona(persona)
            for fields in menu.item_fields.values():
                spoken_name = fields.get("spokenName") or ""
                if not spoken_name:
                    continue
                any_pack_declares_a_spoken_name = True
                raw_name = fields["name"]
                result = menu.spoken(raw_name)
                self.assertEqual(
                    result, spoken_name, f"{persona_id}: {raw_name!r} was not spoken as {spoken_name!r}"
                )
                if raw_name != spoken_name:
                    self.assertNotIn(raw_name, result, f"{persona_id}: raw name {raw_name!r} survived")

        self.assertTrue(
            any_pack_declares_a_spoken_name,
            "expected at least one shipped persona pack to declare an item spokenName override "
            "(otherwise this test passes vacuously)",
        )

    def test_unrelated_item_is_unaffected_by_spoken_name_overrides(self):
        # test-zeta's own fixture item has no spokenName override, so spoken() must be a no-op.
        catalog = PersonaCatalog.load(personas_dir=_FIXTURES_PERSONAS_DIR, enabled=["test-zeta"])
        persona = catalog.get("test-zeta")
        menu = menu_utils.get_catalog_for_persona(persona)

        self.assertEqual(menu.spoken("Zeta Cola"), "Zeta Cola")


if __name__ == "__main__":
    unittest.main()
