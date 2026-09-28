"""Voices, greetings, and apology clips (#83, P2-14): every enabled persona pack's own
declared configuration must be internally consistent with the app's real ground truth --
the model catalog's allowed voices, the pack's own displayName, and its own declared
locales -- discovered from :class:`~persona_loader.PersonaCatalog`, never hard-coded to a
single pack.

Does not call Azure: only reads each pack's own manifest/prompts/audio files on disk.
"""
import re
import sys
import unittest
import wave
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
sys.path.append(str(REPO / "app" / "backend"))

from persona_loader import Persona, PersonaCatalog  # noqa: E402
from prompt_loader import PromptLoader  # noqa: E402
from rtmt import _DEFAULT_ALLOWED_VOICES  # noqa: E402

# app/frontend/src/lib/voices.ts is the frontend's own copy of the same GA voice list
# (rtmt.py's docstring: kept in sync by hand, the frontend is deliberately not imported
# into the Python backend); cross-checked separately below.
_FRONTEND_VOICES_TS = REPO / "app" / "frontend" / "src" / "lib" / "voices.ts"


def _greeting_text(persona: Persona) -> str:
    loader = PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir)
    greeting = loader.get_greeting()
    return greeting["item"]["content"][0]["text"]


def _system_prompt_sections(persona: Persona) -> dict[str, str]:
    """Return {section_name: content} for a pack's own system_prompt.yaml, read directly
    (not through PromptLoader, which only exposes the already-assembled prompt string)."""
    data = yaml.safe_load((persona.prompts_dir / "system_prompt.yaml").read_text(encoding="utf-8"))
    return {section["name"]: section["content"] for section in data["sections"]}


def _update_order_actions(persona: Persona) -> set[str]:
    """Return the 'action' enum update_order's tool schema declares for this pack."""
    data = yaml.safe_load((persona.prompts_dir / "tool_schemas.yaml").read_text(encoding="utf-8"))
    for tool in data["tools"]:
        if tool["name"] == "update_order":
            return set(tool["parameters"]["properties"]["action"]["enum"])
    raise AssertionError(f"{persona.id} has no update_order tool schema")


class VoiceConformanceTests(unittest.TestCase):
    """Every pack's configured voice must be a real, currently offered voice."""

    def test_every_pack_voice_is_ga_allow_listed(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                voice = catalog.get(persona_id).manifest.voice.default
                self.assertIn(voice, _DEFAULT_ALLOWED_VOICES,
                              f"{persona_id}'s configured voice {voice!r} is not one of the "
                              f"backend's allowed GA voices {sorted(_DEFAULT_ALLOWED_VOICES)}")

    def test_backend_and_frontend_voice_lists_are_the_same_set(self):
        """Kept in sync by hand (rtmt.py's own docstring) -- catch a silent drift."""
        ts = _FRONTEND_VOICES_TS.read_text(encoding="utf-8")
        frontend_voices = set(re.findall(r'value:\s*"(\w+)"', ts))
        self.assertTrue(frontend_voices, "could not find any voice values in voices.ts")
        self.assertEqual(frontend_voices, set(_DEFAULT_ALLOWED_VOICES))


class GreetingConformanceTests(unittest.TestCase):
    """Every pack's greeting must name that pack's own brand -- never another pack's."""

    def test_every_pack_greeting_names_its_own_brand(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                text = _greeting_text(persona)
                # A trailing apostrophe (a brand's own official styling, e.g. a possessive
                # form) isn't necessarily spoken/written in a short spoken greeting, so match
                # on the name with any trailing apostrophe stripped rather than requiring an
                # exact substring.
                brand = persona.manifest.displayName.rstrip("'")
                self.assertIn(brand, text,
                              f"{persona_id}'s greeting does not name its own brand "
                              f"({persona.manifest.displayName!r}): {text!r}")

    def test_no_pack_greeting_names_a_different_pack(self):
        """A copy-paste from another pack (a real defect class here) would leave a sibling
        pack's brand name in the greeting text."""
        catalog = PersonaCatalog.load()
        brands = {pid: catalog.get(pid).manifest.displayName.rstrip("'") for pid in catalog.ids}
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                text = _greeting_text(catalog.get(persona_id))
                for other_id, other_brand in brands.items():
                    if other_id == persona_id:
                        continue
                    self.assertNotIn(other_brand, text,
                                      f"{persona_id}'s greeting names {other_id}'s brand "
                                      f"({other_brand!r}): {text!r}")


class ApologyClipConformanceTests(unittest.TestCase):
    """Every locale a pack declares support for must have a real, non-silent clip."""

    def test_every_declared_locale_has_a_clip(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                for locale in persona.manifest.locales.supported:
                    with self.subTest(locale):
                        clip = persona.assets_dir / "audio" / f"apology-{locale}.wav"
                        self.assertTrue(clip.is_file(), f"{persona_id} declares locale "
                                        f"{locale!r} but has no {clip.name}")
                        with wave.open(str(clip), "rb") as wav:
                            self.assertGreater(wav.getnframes(), 0, f"{clip} is empty")

    def test_no_clip_exists_for_an_undeclared_locale(self):
        """An orphaned clip (left behind after a locale was dropped) would silently pass
        the check above -- catch it explicitly."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                audio_dir = persona.assets_dir / "audio"
                declared = set(persona.manifest.locales.supported)
                present = {p.stem.removeprefix("apology-") for p in audio_dir.glob("apology-*.wav")}
                self.assertEqual(present, declared,
                                  f"{persona_id}'s apology clips {sorted(present)} don't match its "
                                  f"declared locales {sorted(declared)}")


class PromptSectionConformanceTests(unittest.TestCase):
    """#83 (P2-14) prompt review: every pack's sectioned system_prompt.yaml must share the
    same core section set and the same tool-usage rules, with only genuinely brand/menu-
    specific sections (combos/meals, happy hour, 'modify') differing -- and only when that
    pack's own config says they should."""

    # Sections that encode shared infra/tool-usage rules (never send a price, search before
    # ordering, [SYSTEM HINT]/[OOS] handling, quantity limits, closing-with-total, etc.) --
    # every pack needs these regardless of brand or menu shape.
    _CORE_SECTIONS = frozenset({
        "IDENTITY", "VOICE_STYLE", "TOOL_CALLING_RULES", "MENU_AND_PRICING", "ORDERING",
        "CUSTOMIZATIONS_AND_MODS", "CONVERSATIONAL_FLOW", "BRAND_IDENTITY", "TOOL_HINTS",
        "SUGGESTIVE_SELLING", "ORDER_CHANGE_AFTER_CLOSING", "CLOSING_AN_ORDER",
        "QUANTITY_LIMITS", "TECHNICAL_GUARDRAILS", "PERSONALIZATION", "VISUAL_SYNC",
        "OUT_OF_STOCK", "BOUNDARIES",
    })

    def test_every_pack_has_the_core_section_set(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                names = set(_system_prompt_sections(catalog.get(persona_id)).keys())
                missing = self._CORE_SECTIONS - names
                self.assertFalse(missing, f"{persona_id} is missing core sections {sorted(missing)}")

    def test_happy_hour_section_presence_matches_the_packs_own_pricing_config(self):
        """A HAPPY_HOUR section should exist only when the pack's own persona.json enables
        and announces one -- never hard-coded on, never silently dropped."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                has_section = "HAPPY_HOUR" in _system_prompt_sections(persona)
                is_configured = persona.manifest.pricing.happyHour is not None
                self.assertEqual(has_section, is_configured,
                                  f"{persona_id}: HAPPY_HOUR section present={has_section} but "
                                  f"pricing.happyHour configured={is_configured}")

    def test_combo_or_meal_bundle_section_presence_matches_the_packs_own_bundle_config(self):
        """Combo/meal bundling instructions (e.g. COMBO_LOGIC, COMBO_PIVOT_RULES, or a
        bundle-name-synonym section) should exist only for a pack whose own persona.json
        declares bundle name markers -- a pack that sells no bundles (nameMarkers: []) rightly
        has none."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                names = _system_prompt_sections(persona).keys()
                has_bundle_section = any("COMBO" in n or "MEAL" in n for n in names)
                bundles_configured = bool(persona.manifest.bundles.nameMarkers)
                self.assertEqual(has_bundle_section, bundles_configured,
                                  f"{persona_id}: bundle section present={has_bundle_section} but "
                                  f"bundles.nameMarkers={persona.manifest.bundles.nameMarkers!r}")

    def test_modify_action_is_only_referenced_by_a_pack_whose_schema_supports_it(self):
        """'modify' (in-place resize) is only supported by packs whose own update_order
        schema lists 'modify' in the action enum -- a pack's prompt must not tell the model
        to call an action its own tool schema doesn't accept, and vice versa."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                content = " ".join(_system_prompt_sections(persona).values())
                prompt_references_modify = "action 'modify'" in content
                schema_supports_modify = "modify" in _update_order_actions(persona)
                self.assertEqual(prompt_references_modify, schema_supports_modify,
                                  f"{persona_id}: prompt references action 'modify'="
                                  f"{prompt_references_modify} but schema supports it="
                                  f"{schema_supports_modify}")

    def test_every_pack_documents_the_same_never_send_a_price_rule(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                menu_section = _system_prompt_sections(catalog.get(persona_id))["MENU_AND_PRICING"]
                self.assertIn("price", menu_section.lower())
                self.assertRegex(menu_section.lower(), r"never (pass|send)? ?a price|price.*ignored")

    def test_every_pack_documents_the_same_search_before_ordering_rule(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                menu_section = _system_prompt_sections(catalog.get(persona_id))["MENU_AND_PRICING"]
                self.assertIn("ALWAYS call search BEFORE adding any item", menu_section)

    def test_every_pack_documents_the_same_not_on_menu_handling(self):
        """The exact not-on-menu rejection sentence must be identical across packs -- this is
        shared tool-contract behavior (update_order rejects off-menu items), not brand voice."""
        catalog = PersonaCatalog.load()
        expected = ("update_order REJECTS anything not on our menu (exact name or a real "
                    "alias) — if that happens, apologize briefly and offer the closest real "
                    "menu item instead of retrying the same name")
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                menu_section = _system_prompt_sections(catalog.get(persona_id))["MENU_AND_PRICING"]
                self.assertIn(expected, menu_section)


class MutationIsCaughtTests(unittest.TestCase):
    """Each check must fail closed on a real defect, not just always pass on today's clean data."""

    def test_a_voice_outside_the_allow_list_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.assertIn("not-a-real-voice", _DEFAULT_ALLOWED_VOICES)

    def test_a_pack_missing_a_core_section_is_caught(self):
        real_names = set(_system_prompt_sections(PersonaCatalog.load().get(
            PersonaCatalog.load().default_persona_id)).keys())
        mutated_names = real_names - {"OUT_OF_STOCK"}  # simulate an accidentally deleted section
        missing = PromptSectionConformanceTests._CORE_SECTIONS - mutated_names
        with self.assertRaises(AssertionError):
            self.assertFalse(missing, f"missing core sections {sorted(missing)}")

    def test_a_happy_hour_section_mismatched_with_config_is_caught(self):
        catalog = PersonaCatalog.load()
        # Take the pack that has pricing.happyHour == None; pretend its prompt has a
        # HAPPY_HOUR section anyway (a copy-paste from a pack that does offer one).
        no_happy_hour_id = next(p for p in catalog.ids if catalog.get(p).manifest.pricing.happyHour is None)
        persona = catalog.get(no_happy_hour_id)
        has_section = True  # mutated: as if HAPPY_HOUR had been copy-pasted in
        is_configured = persona.manifest.pricing.happyHour is not None
        with self.assertRaises(AssertionError):
            self.assertEqual(has_section, is_configured)

    def test_a_not_on_menu_sentence_altered_in_one_pack_is_caught(self):
        catalog = PersonaCatalog.load()
        persona = catalog.get(catalog.default_persona_id)
        menu_section = _system_prompt_sections(persona)["MENU_AND_PRICING"]
        mutated = menu_section.replace("REJECTS anything not on our menu", "politely allows anything")
        with self.assertRaises(AssertionError):
            self.assertIn("update_order REJECTS anything not on our menu", mutated)

    def test_a_greeting_naming_the_wrong_brand_is_caught(self):
        catalog = PersonaCatalog.load()
        ids = catalog.ids
        self.assertGreaterEqual(len(ids), 2, "need at least 2 packs to prove a cross-brand mismatch is caught")
        this_persona, other_persona = catalog.get(ids[0]), catalog.get(ids[1])
        corrupted_greeting = _greeting_text(other_persona)  # another pack's greeting, wrongly assigned
        own_brand = this_persona.manifest.displayName.rstrip("'")
        self.assertNotIn(own_brand, corrupted_greeting,
                          "fixture bug: the other pack's greeting happens to name this pack's brand too")

    def test_a_missing_declared_locale_clip_is_caught(self):
        catalog = PersonaCatalog.load()
        persona = catalog.get(catalog.default_persona_id)
        declared = set(persona.manifest.locales.supported) | {"not-a-real-locale"}
        present = {p.stem.removeprefix("apology-") for p in (persona.assets_dir / "audio").glob("apology-*.wav")}
        with self.assertRaises(AssertionError):
            self.assertEqual(present, declared)


if __name__ == "__main__":
    unittest.main()
