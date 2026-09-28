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


class MutationIsCaughtTests(unittest.TestCase):
    """Each check must fail closed on a real defect, not just always pass on today's clean data."""

    def test_a_voice_outside_the_allow_list_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.assertIn("not-a-real-voice", _DEFAULT_ALLOWED_VOICES)

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
