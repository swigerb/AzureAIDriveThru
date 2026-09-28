"""Voices, greetings, and apology clips (#83, P2-14): every enabled persona pack's own
declared configuration must be internally consistent with the app's real ground truth --
the model catalog's allowed voices, the pack's own displayName, and its own declared
locales -- discovered from :class:`~persona_loader.PersonaCatalog`, never hard-coded to a
single pack.

Does not call Azure: only reads each pack's own manifest/prompts/audio files on disk.

Every check below lives in a small, pure, brand-agnostic helper that returns a list of
errors (empty == valid) -- the same pattern test_demo_data_conformance.py uses. Each real
per-pack test asserts the helper's result is ``[]``; each corresponding test in
``MutationIsCaughtTests`` calls the SAME helper on a deliberately corrupted value and
asserts the result is non-empty. This means breaking a real check (or deleting it) also
breaks its mutation test, instead of the mutation test re-stating an unrelated assertion
on a hand-made value that would keep passing either way.
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

# Section names that document combo/meal BUNDLE instructions specifically -- deliberately an
# explicit allow-list, not a "COMBO"/"MEAL" substring match: a no-bundle pack can legitimately
# have an unrelated daypart section named e.g. MEAL_PERIODS or MEALTIME_RULES
# (features.dayparts), and a bundle pack could just as legitimately name its own section
# BUNDLES or VALUE_BOXES. If a pack's bundle instructions introduce a new section name, add it
# here.
_BUNDLE_SECTIONS = frozenset({
    "COMBO_LOGIC", "COMBO_PIVOT_RULES", "COMBO_MEAL_SYNONYMS", "EXTRA_VALUE_MEALS",
})

# The two key clauses of the not-on-menu tool-contract sentence, pinned rather than the whole
# sentence (em dash and connective phrasing included) so a pack can still reword the
# connective wording in its own voice while the contract itself -- update_order rejects
# off-menu items, and the model must offer a real substitute -- stays enforced everywhere.
_NOT_ON_MENU_CLAUSES = (
    "update_order REJECTS anything not on our menu",
    "offer the closest real menu item",
)


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


# ===========================================================================
# Pure validation helpers (brand-agnostic; no persona/pack literals anywhere)
# ===========================================================================


def voice_errors(persona_id: str, voice: str, allowed: frozenset[str]) -> list[str]:
    """Every error found in one pack's configured default voice against the backend's own
    allowed GA voice list. Empty list == valid."""
    if voice not in allowed:
        return [f"{persona_id}'s configured voice {voice!r} is not one of the backend's "
                f"allowed GA voices {sorted(allowed)}"]
    return []


def greeting_brand_errors(persona_id: str, text: str, brands: dict[str, str]) -> list[str]:
    """Every error found in one pack's greeting text: it must name its own brand
    (``brands[persona_id]``), and must never name a sibling pack's brand -- a copy-paste from
    another pack (a real defect class here) would leave a sibling brand name in the text.
    Empty list == valid."""
    errors: list[str] = []
    own_brand = brands[persona_id]
    if own_brand not in text:
        errors.append(f"{persona_id}'s greeting does not name its own brand ({own_brand!r}): {text!r}")
    for other_id, other_brand in brands.items():
        if other_id == persona_id:
            continue
        if other_brand in text:
            errors.append(f"{persona_id}'s greeting names {other_id}'s brand ({other_brand!r}): {text!r}")
    return errors


def clip_locale_errors(persona_id: str, present: set[str], declared: set[str]) -> list[str]:
    """Every error found comparing a pack's actual apology-clip locales on disk against its
    declared ``locales.supported`` -- either direction (a missing declared-locale clip, or an
    orphaned clip left behind after a locale was dropped) is an error. Empty list == valid."""
    if present != declared:
        return [f"{persona_id}'s apology clips {sorted(present)} don't match its declared "
                f"locales {sorted(declared)}"]
    return []


def core_section_errors(section_names: set[str]) -> list[str]:
    """Every core section missing from a pack's own section-name set. Empty list == valid."""
    missing = _CORE_SECTIONS - section_names
    if missing:
        return [f"missing core sections {sorted(missing)}"]
    return []


def happy_hour_section_errors(has_section: bool, is_configured: bool) -> list[str]:
    """A HAPPY_HOUR section must be present exactly when the pack's own
    ``pricing.happyHour`` is configured (not ``None``) -- never hard-coded on, never silently
    dropped. Empty list == valid."""
    if has_section != is_configured:
        return [f"HAPPY_HOUR section present={has_section} but pricing.happyHour "
                f"configured={is_configured}"]
    return []


def bundle_section_errors(section_names: set[str], bundles_configured: bool) -> list[str]:
    """A combo/meal bundle section (one of ``_BUNDLE_SECTIONS``) must be present exactly when
    the pack's own ``bundles.nameMarkers`` is non-empty. Empty list == valid."""
    has_bundle_section = bool(section_names & _BUNDLE_SECTIONS)
    if has_bundle_section != bundles_configured:
        return [
            f"bundle section present={has_bundle_section} but bundles.nameMarkers "
            f"configured={bundles_configured} (if this pack's bundle instructions use a new "
            "section name, add it to _BUNDLE_SECTIONS in "
            "app/backend/tests/test_persona_pack_conformance.py)"
        ]
    return []


def modify_reference_errors(prompt_references_modify: bool, schema_supports_modify: bool) -> list[str]:
    """A prompt may only tell the model to call action 'modify' when the pack's own
    update_order tool schema actually supports it, and vice versa. Empty list == valid."""
    if prompt_references_modify != schema_supports_modify:
        return [f"prompt references action 'modify'={prompt_references_modify} but schema "
                f"supports it={schema_supports_modify}"]
    return []


def not_on_menu_errors(menu_section: str) -> list[str]:
    """Every key not-on-menu clause (``_NOT_ON_MENU_CLAUSES``) missing from a pack's own
    MENU_AND_PRICING section. Empty list == valid."""
    return [
        f"MENU_AND_PRICING is missing the required not-on-menu clause {clause!r}"
        for clause in _NOT_ON_MENU_CLAUSES
        if clause not in menu_section
    ]


class VoiceConformanceTests(unittest.TestCase):
    """Every pack's configured voice must be a real, currently offered voice."""

    def test_every_pack_voice_is_ga_allow_listed(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                voice = catalog.get(persona_id).manifest.voice.default
                self.assertEqual(voice_errors(persona_id, voice, _DEFAULT_ALLOWED_VOICES), [])

    def test_backend_and_frontend_voice_lists_are_the_same_set(self):
        """Kept in sync by hand (rtmt.py's own docstring) -- catch a silent drift."""
        ts = _FRONTEND_VOICES_TS.read_text(encoding="utf-8")
        frontend_voices = set(re.findall(r'value:\s*"(\w+)"', ts))
        self.assertTrue(frontend_voices, "could not find any voice values in voices.ts")
        self.assertEqual(frontend_voices, set(_DEFAULT_ALLOWED_VOICES))


class GreetingConformanceTests(unittest.TestCase):
    """Every pack's greeting must name that pack's own brand -- never another pack's."""

    def test_every_pack_greeting_names_its_own_brand_and_no_sibling_brand(self):
        catalog = PersonaCatalog.load()
        # A trailing apostrophe (a brand's own official styling, e.g. a possessive form) isn't
        # necessarily spoken/written in a short spoken greeting, so match on the name with any
        # trailing apostrophe stripped rather than requiring an exact substring.
        brands = {pid: catalog.get(pid).manifest.displayName.rstrip("'") for pid in catalog.ids}
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                text = _greeting_text(catalog.get(persona_id))
                self.assertEqual(greeting_brand_errors(persona_id, text, brands), [])


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
                self.assertEqual(clip_locale_errors(persona_id, present, declared), [])


class PromptSectionConformanceTests(unittest.TestCase):
    """#83 (P2-14) prompt review: every pack's sectioned system_prompt.yaml must share the
    same core section set and the same tool-usage rules, with only genuinely brand/menu-
    specific sections (combos/meals, happy hour, 'modify') differing -- and only when that
    pack's own config says they should."""

    def test_every_pack_has_the_core_section_set(self):
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                names = set(_system_prompt_sections(catalog.get(persona_id)).keys())
                self.assertEqual(core_section_errors(names), [])

    def test_happy_hour_section_presence_matches_the_packs_own_pricing_config(self):
        """A HAPPY_HOUR section should exist only when the pack's own persona.json sets
        pricing.happyHour to something other than None -- never hard-coded on, never silently
        dropped."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                has_section = "HAPPY_HOUR" in _system_prompt_sections(persona)
                is_configured = persona.manifest.pricing.happyHour is not None
                self.assertEqual(happy_hour_section_errors(has_section, is_configured), [])

    def test_combo_or_meal_bundle_section_presence_matches_the_packs_own_bundle_config(self):
        """Combo/meal bundling instructions (one of ``_BUNDLE_SECTIONS``) should exist only for
        a pack whose own persona.json declares bundle name markers -- a pack that sells no
        bundles (nameMarkers: []) rightly has none."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                names = set(_system_prompt_sections(persona).keys())
                bundles_configured = bool(persona.manifest.bundles.nameMarkers)
                self.assertEqual(bundle_section_errors(names, bundles_configured), [])

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
                self.assertEqual(
                    modify_reference_errors(prompt_references_modify, schema_supports_modify), []
                )

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
        """The two key not-on-menu clauses must be identical across packs -- this is shared
        tool-contract behavior (update_order rejects off-menu items; offer a real substitute),
        not brand voice, so only those two clauses are pinned rather than the whole sentence
        (a pack may still reword the connective phrasing in its own voice)."""
        catalog = PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                menu_section = _system_prompt_sections(catalog.get(persona_id))["MENU_AND_PRICING"]
                self.assertEqual(not_on_menu_errors(menu_section), [])


class MutationIsCaughtTests(unittest.TestCase):
    """Each check above must fail closed on a real defect, not just always pass on today's
    clean data. Every assertion here calls the SAME helper the corresponding real test above
    calls, on a deliberately corrupted value, and proves the helper reports an error --
    breaking (or deleting) the real check breaks its mutation test too."""

    def test_a_voice_outside_the_allow_list_is_rejected(self):
        errors = voice_errors("mutant-pack", "not-a-real-voice", _DEFAULT_ALLOWED_VOICES)
        self.assertTrue(errors, "voice_errors did not flag a voice outside the allow list")

    def test_a_greeting_naming_the_wrong_brand_is_caught(self):
        catalog = PersonaCatalog.load()
        ids = catalog.ids
        self.assertGreaterEqual(len(ids), 2, "need at least 2 packs to prove a cross-brand mismatch is caught")
        brands = {pid: catalog.get(pid).manifest.displayName.rstrip("'") for pid in ids}
        corrupted_greeting = _greeting_text(catalog.get(ids[1]))  # pack B's greeting, wrongly assigned to pack A
        errors = greeting_brand_errors(ids[0], corrupted_greeting, brands)
        self.assertTrue(errors, f"greeting_brand_errors did not flag {ids[1]}'s greeting assigned to {ids[0]}")

    def test_a_missing_declared_locale_clip_is_caught(self):
        catalog = PersonaCatalog.load()
        persona = catalog.get(catalog.default_persona_id)
        declared = set(persona.manifest.locales.supported) | {"not-a-real-locale"}
        present = {p.stem.removeprefix("apology-") for p in (persona.assets_dir / "audio").glob("apology-*.wav")}
        errors = clip_locale_errors(persona.id, present, declared)
        self.assertTrue(errors, "clip_locale_errors did not flag a missing declared-locale clip")

    def test_a_pack_missing_a_core_section_is_caught(self):
        catalog = PersonaCatalog.load()
        real_names = set(_system_prompt_sections(catalog.get(catalog.default_persona_id)).keys())
        mutated_names = real_names - {"OUT_OF_STOCK"}  # simulate an accidentally deleted section
        errors = core_section_errors(mutated_names)
        self.assertTrue(errors, "core_section_errors did not flag a dropped core section")

    def test_a_happy_hour_section_mismatched_with_config_is_caught(self):
        # A synthetic happyHour=None manifest with a HAPPY_HOUR section copy-pasted in anyway
        # -- built directly rather than via next(...) over real packs, which would raise
        # StopIteration if no pack happened to have happyHour=None today.
        errors = happy_hour_section_errors(has_section=True, is_configured=False)
        self.assertTrue(errors, "happy_hour_section_errors did not flag a mismatched section")

    def test_a_bundle_section_named_meal_periods_for_a_no_bundle_pack_is_not_flagged(self):
        """A no-bundle pack (nameMarkers: []) may legitimately have an unrelated MEAL_PERIODS
        daypart section (features.dayparts) -- that must NOT be mistaken for a combo/meal
        bundle section (item 2 of Rick's review)."""
        errors = bundle_section_errors({"MEAL_PERIODS"}, bundles_configured=False)
        self.assertEqual(errors, [], "a MEAL_PERIODS daypart section wrongly matched _BUNDLE_SECTIONS")

    def test_a_not_on_menu_sentence_altered_in_one_pack_is_caught(self):
        catalog = PersonaCatalog.load()
        persona = catalog.get(catalog.default_persona_id)
        menu_section = _system_prompt_sections(persona)["MENU_AND_PRICING"]
        mutated = menu_section.replace("REJECTS anything not on our menu", "politely allows anything")
        errors = not_on_menu_errors(mutated)
        self.assertTrue(errors, "not_on_menu_errors did not flag an altered not-on-menu sentence")


if __name__ == "__main__":
    unittest.main()
