"""Persona loader tests for Sonic AI Drive-Thru (issue #70, P2-1).

Covers:
  - The real Sonic persona pack loads successfully and exposes the expected paths/fields.
  - Schema and model violations refuse to start with a clear, file+field-named error
    (``PersonaValidationError``) -- never a partial/degraded load.
  - Mutation checks: starting from a known-good copy of the real pack, each mutation flips
    exactly one thing (missing required field, wrong type, extra/unknown field, id/folder
    mismatch, missing menu file, malformed JSON) and must be individually caught.
  - ``PERSONAS`` / ``DEFAULT_PERSONA`` env var resolution.
"""

import json
import shutil
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from persona_loader import Persona, PersonaCatalog, PersonaValidationError

_REPO_ROOT = Path(__file__).resolve().parents[3]
_REAL_PERSONAS_DIR = _REPO_ROOT / "personas"


@pytest.fixture
def personas_copy(tmp_path):
    """A throwaway copy of the real personas/ tree so mutation tests never touch the real pack."""
    dest = tmp_path / "personas"
    shutil.copytree(_REAL_PERSONAS_DIR, dest)
    return dest


def _mutate_persona_json(personas_dir: Path, persona_id: str, mutator) -> None:
    """Load persona_id's persona.json, apply ``mutator`` to the dict in place, write it back."""
    path = personas_dir / persona_id / "persona.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    mutator(data)
    path.write_text(json.dumps(data), encoding="utf-8")


# ===========================================================================
# Valid pack loads
# ===========================================================================


class TestValidPackLoads:
    def test_real_sonic_pack_loads(self):
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        assert catalog.ids == ["sonic"]
        assert catalog.default_persona_id == "sonic"

    def test_sonic_persona_exposes_expected_paths(self):
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        sonic = catalog.get("sonic")
        assert isinstance(sonic, Persona)
        assert sonic.prompts_dir.is_dir()
        assert (sonic.prompts_dir / "system_prompt.yaml").is_file()
        assert sonic.menu_path.is_file()
        assert sonic.menu_path.name == "menuItems.json"
        assert sonic.assets_dir.is_dir()

    def test_sonic_manifest_fields_match_current_hardcoded_values(self):
        """Spot-check a few fields against today's real hardcoded values (tools.py/config.yaml),
        so the pack is a real data snapshot and not just an arbitrary placeholder."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        manifest = catalog.get("sonic").manifest
        assert manifest.voice.default == "marin"
        assert manifest.search.indexName == "sonic-menu-items"
        assert manifest.pricing.taxRate == "0.08"
        assert manifest.pricing.happyHour.startHour == 14
        assert manifest.pricing.happyHour.endHour == 16
        assert manifest.machines["ice_cream_machine"] == "down"

    def test_sonic_dark_primary_matches_index_css(self):
        """Regression guard: dark-mode primary must match index.css's `.dark` block / personaTheme.ts's
        SONIC_THEME.dark.primary ("341 100% 55%"), not the design doc's abridged example value. A prior
        draft of persona.json had "347 100% 71%" here, which would have silently diverged the pack from
        the actual rendered dark-mode color."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.dark is not None
        assert manifest.ui.theme.dark.primary == "341 100% 55%"

    def test_sonic_theme_accents_block_is_optional_and_absent_today(self):
        """`accents` (issue #80 F2, personaTheme.ts::PersonaAccentPalette) is optional in the schema
        so packs without a curated accent palette still validate -- today's real Sonic persona.json
        doesn't set it yet, and that must keep loading cleanly."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.light.accents is None

    def test_theme_accents_block_loads_when_present(self, personas_copy):
        """When a persona pack does supply the optional `accents` block (role-named per
        personaTheme.ts's PersonaAccentPalette: primaryHex, primaryStrong, ..., neutral), it
        validates and its values are exposed on the manifest -- proves the block is wired through
        both persona.schema.json and the Pydantic models, not just accepted by one of the two."""
        def mutator(d):
            d["ui"]["theme"]["light"]["accents"] = {
                "primaryHex": "#E40046",
                "primaryStrong": "#C31B24",
                "primaryLight": "#FF4D7A",
                "primaryTintOnDark": "#FF6B8A",
                "secondaryHex": "#285780",
                "secondaryStrong": "#137AC9",
                "secondaryTintOnDark": "#74D2E7",
                "accent": "#FEDD00",
                "accentLight": "#FFE84D",
                "ink": "#18344D",
                "surfaceTint": "#F2F8FA",
                "surfaceDark": "#0F1A24",
                "surfaceDarkAlt": "#152231",
                "success": "#328500",
                "neutral": "#C9CFD4",
            }
            # Dark mode is allowed to override only a subset of accent keys.
            d["ui"]["theme"]["dark"]["accents"] = {"primaryHex": "#FF4D7A"}
        _mutate_persona_json(personas_copy, "sonic", mutator)
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.light.accents is not None
        assert manifest.ui.theme.light.accents.primaryHex == "#E40046"
        assert manifest.ui.theme.light.accents.neutral == "#C9CFD4"
        assert manifest.ui.theme.dark.accents.primaryHex == "#FF4D7A"
        assert manifest.ui.theme.dark.accents.secondaryHex is None

    def test_valid_copy_loads_identically(self, personas_copy):
        """A byte-identical copy of the real pack, loaded from a different directory, validates
        the same way -- proves the loader is driven by content, not some real-path special case."""
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert catalog.ids == ["sonic"]

    def test_explicit_enabled_and_default_override(self, personas_copy):
        catalog = PersonaCatalog.load(
            personas_dir=personas_copy, enabled=["sonic"], default_persona_id="sonic"
        )
        assert catalog.default_persona_id == "sonic"

    def test_get_unknown_persona_raises_key_error(self):
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        with pytest.raises(KeyError):
            catalog.get("nonexistent")

    def test_contains(self):
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        assert "sonic" in catalog
        assert "nonexistent" not in catalog


# ===========================================================================
# Startup-level failures: missing dir, no packs, bad default
# ===========================================================================


class TestCatalogLevelFailures:
    def test_missing_personas_dir_raises(self, tmp_path):
        with pytest.raises(PersonaValidationError, match="does not exist"):
            PersonaCatalog.load(personas_dir=tmp_path / "does-not-exist")

    def test_empty_personas_dir_raises(self, tmp_path):
        empty_dir = tmp_path / "personas"
        empty_dir.mkdir()
        shutil.copy(_REAL_PERSONAS_DIR / "persona.schema.json", empty_dir / "persona.schema.json")
        shutil.copy(_REAL_PERSONAS_DIR / "menu.schema.json", empty_dir / "menu.schema.json")
        with pytest.raises(PersonaValidationError, match="No personas enabled"):
            PersonaCatalog.load(personas_dir=empty_dir)

    def test_unlisted_enabled_persona_raises(self, personas_copy):
        with pytest.raises(PersonaValidationError, match="does not exist"):
            PersonaCatalog.load(personas_dir=personas_copy, enabled=["not-a-real-persona"])

    def test_default_persona_not_enabled_raises(self, personas_copy):
        with pytest.raises(PersonaValidationError, match="DEFAULT_PERSONA"):
            PersonaCatalog.load(
                personas_dir=personas_copy, enabled=["sonic"], default_persona_id="wendys"
            )


# ===========================================================================
# Mutation checks: schema violations refuse to start, naming persona+file+field
# ===========================================================================


class TestMutationSchemaViolations:
    """Each test corrupts exactly one thing in a copy of the real, valid persona.json and
    asserts the loader refuses to start with a PersonaValidationError naming the persona id,
    the file, and (for schema/model failures) the field."""

    def test_missing_required_top_level_field_refuses_to_start(self, personas_copy):
        _mutate_persona_json(personas_copy, "sonic", lambda d: d.pop("voice"))
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert "sonic" in message
        assert "persona.json" in message
        assert "voice" in message

    def test_missing_required_nested_field_refuses_to_start(self, personas_copy):
        def mutator(d):
            del d["pricing"]["taxRate"]
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert "sonic" in message
        assert "taxRate" in message or "pricing" in message

    def test_wrong_type_refuses_to_start(self, personas_copy):
        def mutator(d):
            d["pricing"]["happyHour"]["startHour"] = "fourteen"  # must be an integer
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError, match="sonic"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_extra_unknown_field_refuses_to_start(self, personas_copy):
        def mutator(d):
            d["unexpectedTopLevelField"] = "should not be allowed"
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        assert "sonic" in str(exc_info.value)

    def test_extra_unknown_nested_field_refuses_to_start(self, personas_copy):
        def mutator(d):
            d["ui"]["unexpectedNestedField"] = "nope"
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError, match="sonic"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_unknown_field_inside_theme_accents_refuses_to_start(self, personas_copy):
        """`accents` is optional, but once present it's still additionalProperties:false -- an
        unrecognized accent key (e.g. a typo, or a token name from before the primary/secondary/
        accent role-name rename) must be rejected, not silently ignored."""
        def mutator(d):
            d["ui"]["theme"]["light"]["accents"] = {
                "primaryHex": "#E40046",
                "brandRedHex": "#E40046",  # old pre-rename token name, not a valid role name
            }
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError, match="sonic"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_id_folder_mismatch_refuses_to_start(self, personas_copy):
        _mutate_persona_json(personas_copy, "sonic", lambda d: d.update(id="not-sonic"))
        with pytest.raises(PersonaValidationError, match="does not match its folder name"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_malformed_json_refuses_to_start(self, personas_copy):
        path = personas_copy / "sonic" / "persona.json"
        path.write_text("{not valid json", encoding="utf-8")
        with pytest.raises(PersonaValidationError, match="Malformed JSON"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_wrong_type_for_locales_supported_refuses_to_start(self, personas_copy):
        def mutator(d):
            d["locales"]["supported"] = "en"  # must be a list
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError, match="sonic"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_invalid_enum_value_refuses_to_start(self, personas_copy):
        def mutator(d):
            d["machines"]["ice_cream_machine"] = "broken"  # only "down"/"operational" allowed
        _mutate_persona_json(personas_copy, "sonic", mutator)
        with pytest.raises(PersonaValidationError, match="sonic"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_missing_menu_file_refuses_to_start(self, personas_copy):
        (personas_copy / "sonic" / "menu" / "menuItems.json").unlink()
        with pytest.raises(PersonaValidationError, match="menu file not found"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_malformed_menu_json_refuses_to_start(self, personas_copy):
        menu_path = personas_copy / "sonic" / "menu" / "menuItems.json"
        menu_path.write_text("{not valid json", encoding="utf-8")
        with pytest.raises(PersonaValidationError, match="Malformed JSON"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_menu_schema_violation_refuses_to_start(self, personas_copy):
        menu_path = personas_copy / "sonic" / "menu" / "menuItems.json"
        data = json.loads(menu_path.read_text(encoding="utf-8"))
        del data["menuItems"][0]["items"][0]["sizes"]  # required field
        menu_path.write_text(json.dumps(data), encoding="utf-8")
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        assert "sonic" in str(exc_info.value)
        assert "menuItems.json" in str(exc_info.value)

    def test_missing_prompts_dir_refuses_to_start(self, personas_copy):
        shutil.rmtree(personas_copy / "sonic" / "prompts")
        with pytest.raises(PersonaValidationError, match="prompts directory not found"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_missing_schema_file_refuses_to_start(self, personas_copy):
        (personas_copy / "persona.schema.json").unlink()
        with pytest.raises(PersonaValidationError, match="schema file not found"):
            PersonaCatalog.load(personas_dir=personas_copy)


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
