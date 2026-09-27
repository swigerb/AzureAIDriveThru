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


def _discovered_persona_ids(personas_dir: Path) -> list[str]:
    """Every immediate subfolder of ``personas_dir`` that contains a ``persona.json``, ordinal
    sorted -- the same disk-discovery convention ``PersonaCatalog.load`` itself documents, used
    here as an independent (catalog-free) ground truth so pack-count assertions stay correct as
    more real packs land alongside the original pack, instead of hardcoding today's exact set."""
    return sorted(
        p.name for p in personas_dir.iterdir()
        if p.is_dir() and (p / "persona.json").is_file()
    )


# ===========================================================================
# Valid pack loads
# ===========================================================================


class TestValidPackLoads:
    def test_real_sonic_pack_loads(self):
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        # Assert against the packs actually present on disk (more real packs may land
        # alongside this one) rather than hardcoding the full set -- this pack must always
        # be present, and it must remain the default whenever it's enabled (persona_loader.py's
        # documented resolution order), regardless of how many other packs also load.
        assert catalog.ids == _discovered_persona_ids(_REAL_PERSONAS_DIR)
        assert "sonic" in catalog.ids
        assert catalog.default_persona_id == "sonic"

    def test_every_discovered_real_pack_validates(self):
        """Pack-count agnostic: whatever packs land alongside the original one under personas/,
        every single one of them must load as a valid, complete Persona -- not just one."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        for persona_id in catalog.ids:
            persona = catalog.get(persona_id)
            assert isinstance(persona, Persona)
            assert persona.prompts_dir.is_dir(), f"{persona_id}: prompts_dir missing"
            assert (persona.prompts_dir / "system_prompt.yaml").is_file(), (
                f"{persona_id}: system_prompt.yaml missing"
            )
            assert persona.menu_path.is_file(), f"{persona_id}: menu file missing"
            assert persona.assets_dir.is_dir(), f"{persona_id}: assets_dir missing"

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
        # #77: `machines` moved from a bare status string to a `{status, label}` object (the OOS
        # label now lives in persona.json instead of a hardcoded Python dict) -- `.status` carries
        # the exact same value the bare string used to.
        assert manifest.machines["ice_cream_machine"].status == "down"

    def test_sonic_dark_primary_matches_index_css(self):
        """Regression guard: dark-mode primary must match index.css's `.dark` block / personaTheme.ts's
        SONIC_THEME.dark.primary ("341 100% 55%"), not the design doc's abridged example value. A prior
        draft of persona.json had "347 100% 71%" here, which would have silently diverged the pack from
        the actual rendered dark-mode color."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.dark is not None
        assert manifest.ui.theme.dark.primary == "341 100% 55%"

    def test_sonic_theme_accents_and_surface_blocks_are_populated(self):
        """Issue #117: the real pack now owns its full accent AND shadcn-slot ("surface") palette
        (previously only in the frontend's deleted SONIC_THEME constant / hard-coded into
        index.css). `accents`/`surface` stay optional in the schema for any OTHER pack that doesn't
        curate one -- see `test_theme_accents_block_loads_when_present` -- but this pack's real
        persona.json must set both, with byte-identical values to what index.css used to hard-code."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        manifest = catalog.get("sonic").manifest
        light = manifest.ui.theme.light
        assert light.accents is not None
        assert light.accents.primaryHex == "#E40046"
        assert light.accents.neutral == "#C9CFD4"
        assert light.surface is not None
        assert light.surface.cardForeground == "208 40% 18%"
        assert light.surface.chart5 == "97 100% 26%"
        assert manifest.ui.theme.dark is not None
        assert manifest.ui.theme.dark.surface is not None
        assert manifest.ui.theme.dark.surface.chart2 == "208 60% 50%"
        assert manifest.ui.theme.dark.surface.border == "210 15% 15%"

    def test_theme_accents_block_loads_when_present(self, personas_copy):
        """When a persona pack does supply the optional `accents` block (role-named per
        personaTheme.ts's PersonaAccentPalette: primaryHex, primaryStrong, ..., neutral), it
        validates and its values are exposed on the manifest -- proves the block is wired through
        both persona.schema.json and the Pydantic models, not just accepted by one of the two."""
        def mutator(d):
            d["ui"]["theme"]["light"]["accents"] = {
                "primaryHex": "#112233",
                "primaryStrong": "#001122",
                "primaryLight": "#223344",
                "primaryTintOnDark": "#334455",
                "secondaryHex": "#445566",
                "secondaryStrong": "#556677",
                "secondaryTintOnDark": "#667788",
                "accent": "#778899",
                "accentLight": "#8899AA",
                "ink": "#99AABB",
                "surfaceTint": "#AABBCC",
                "surfaceDark": "#BBCCDD",
                "surfaceDarkAlt": "#CCDDEE",
                "success": "#DDEEFF",
                "neutral": "#EEFF00",
            }
            # Dark mode is allowed to override only a subset of accent keys.
            d["ui"]["theme"]["dark"]["accents"] = {"primaryHex": "#FF0011"}
        _mutate_persona_json(personas_copy, "sonic", mutator)
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.light.accents is not None
        assert manifest.ui.theme.light.accents.primaryHex == "#112233"
        assert manifest.ui.theme.light.accents.neutral == "#EEFF00"
        assert manifest.ui.theme.dark.accents.primaryHex == "#FF0011"
        assert manifest.ui.theme.dark.accents.secondaryHex is None

    def test_theme_surface_block_loads_when_present(self, personas_copy):
        """Issue #117: `surface` (personaTheme.ts's PersonaSurfaceTokens/PersonaSurfaceDarkTokens --
        the shadcn UI slot palette index.css used to hard-code to this pack's own values) validates
        and round-trips through both persona.schema.json and the Pydantic models, same as `accents`."""
        def mutator(d):
            d["ui"]["theme"]["light"]["surface"] = {"cardForeground": "0 0% 20%", "border": "0 0% 85%"}
            d["ui"]["theme"]["dark"]["surface"] = {"border": "0 0% 15%", "chart2": "0 0% 50%"}
        _mutate_persona_json(personas_copy, "sonic", mutator)
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        manifest = catalog.get("sonic").manifest
        assert manifest.ui.theme.light.surface is not None
        assert manifest.ui.theme.light.surface.cardForeground == "0 0% 20%"
        assert manifest.ui.theme.light.surface.border == "0 0% 85%"
        assert manifest.ui.theme.light.surface.secondary is None
        assert manifest.ui.theme.dark.surface.border == "0 0% 15%"
        assert manifest.ui.theme.dark.surface.chart2 == "0 0% 50%"
        assert manifest.ui.theme.dark.surface.muted is None

    def test_valid_copy_loads_identically(self, personas_copy):
        """A byte-identical copy of the real pack, loaded from a different directory, validates
        the same way -- proves the loader is driven by content, not some real-path special case.
        Compares against the same disk-discovered set as the real personas/ dir (pack-count
        agnostic) rather than hardcoding a literal id list."""
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert catalog.ids == _discovered_persona_ids(_REAL_PERSONAS_DIR)

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

    def test_menu_item_key_collision_refuses_to_start(self, personas_copy):
        """#128: two menu items that normalize to the same ``_menu_key`` (e.g. two differently
        parenthesized variants of the same base name) must fail startup for every enabled
        persona, not just silently let the second one loaded win. Uses whichever real pack sorts
        first (brand-agnostic, like ``test_every_discovered_real_pack_validates`` above) rather
        than hardcoding a brand name."""
        persona_id = _discovered_persona_ids(personas_copy)[0]
        menu_path = personas_copy / persona_id / "menu" / "menuItems.json"
        data = json.loads(menu_path.read_text(encoding="utf-8"))
        first_item = data["menuItems"][0]["items"][0]
        colliding_item = dict(first_item)
        colliding_item["name"] = f"{first_item['name']} (Party Size)"
        data["menuItems"][0]["items"].append(colliding_item)
        menu_path.write_text(json.dumps(data), encoding="utf-8")
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert persona_id in message
        assert first_item["name"] in message
        assert colliding_item["name"] in message
        assert "same lookup key" in message

    def test_menu_alias_collision_with_another_items_alias_refuses_to_start(self, personas_copy):
        """#128: an alias declared on two different items must fail startup -- an ambiguous alias
        must never resolve silently to whichever item happened to load last."""
        persona_id = _discovered_persona_ids(personas_copy)[0]
        menu_path = personas_copy / persona_id / "menu" / "menuItems.json"
        data = json.loads(menu_path.read_text(encoding="utf-8"))
        items = [item for category in data["menuItems"] for item in category["items"]]
        items[0].setdefault("aliases", []).append("duplicate test alias")
        items[1].setdefault("aliases", []).append("duplicate test alias")
        menu_path.write_text(json.dumps(data), encoding="utf-8")
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert persona_id in message
        assert "duplicate test alias" in message
        assert items[0]["name"] in message
        assert items[1]["name"] in message

    def test_menu_alias_colliding_with_another_items_own_key_refuses_to_start(self, personas_copy):
        """#128: an alias that happens to normalize to a DIFFERENT item's own name (not just
        another alias) must also fail startup -- this is the half of the rule that isn't a
        plain alias-vs-alias duplicate."""
        persona_id = _discovered_persona_ids(personas_copy)[0]
        menu_path = personas_copy / persona_id / "menu" / "menuItems.json"
        data = json.loads(menu_path.read_text(encoding="utf-8"))
        items = [item for category in data["menuItems"] for item in category["items"]]
        target_name = items[1]["name"]
        items[0].setdefault("aliases", []).append(target_name)
        menu_path.write_text(json.dumps(data), encoding="utf-8")
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert persona_id in message
        assert items[0]["name"] in message
        assert target_name in message

    def test_missing_prompts_dir_refuses_to_start(self, personas_copy):
        shutil.rmtree(personas_copy / "sonic" / "prompts")
        with pytest.raises(PersonaValidationError, match="prompts directory not found"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_missing_schema_file_refuses_to_start(self, personas_copy):
        (personas_copy / "persona.schema.json").unlink()
        with pytest.raises(PersonaValidationError, match="schema file not found"):
            PersonaCatalog.load(personas_dir=personas_copy)


# ===========================================================================
# Fixture schema copies stay in sync with the real schemas (Rick's PR #102 review item 3)
# ===========================================================================

_FIXTURES_PERSONAS_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


class TestFixtureSchemasMatchRealSchemas:
    """`PersonaCatalog.load(personas_dir=...)` hardcodes its schema lookup to
    ``<personas_dir>/persona.schema.json`` and ``<personas_dir>/menu.schema.json`` (see
    ``PersonaCatalog.load`` above) -- the schema files must physically exist inside whatever
    directory is passed as ``personas_dir``. That's why ``app/backend/tests/fixtures/personas/``
    carries its own copies of ``personas/persona.schema.json`` and ``personas/menu.schema.json``
    rather than the test fixtures somehow pointing at the real ``personas/`` directory's copies.

    Rick's PR #102 review item 3 asked to either (a) prove those copies stay byte-identical to
    the real schemas, or (b) make the fixtures reference the real files directly and delete the
    copies. This repo chose (a):

      - (b)'s only real-code-free way to share one file at two paths is a symlink/junction --
        explicitly forbidden by this project's git/worktree rules (Windows dev machines / CI
        runners must not depend on privileged symlink creation).
      - The alternative to a symlink for (b) is teaching ``PersonaCatalog.load`` to accept a
        second, separate "schema directory" distinct from ``personas_dir`` (or falling back to
        the real ``personas/`` directory when the passed one lacks its own copies) -- a
        production-code contract change whose only purpose would be serving this test's
        fixture layout, not something the schema-validation feature itself needs. That's a much
        larger and riskier change than the review's actual concern (byte-for-byte fixture drift).
      - Fixture packs are also deliberately self-contained (see ``TwoPersonaConformanceFixture``'s
        own doc comment): no reliance on a real ``personas/`` directory existing at a fixed
        relative path from wherever a test happens to run, which matters for the .NET
        conformance harness too, not just these Python unit tests.

    So (a): this test class fails loudly the moment either fixture copy drifts from its real
    counterpart, which is the actual risk Rick's review flagged -- the fixtures silently
    validating against a stale/relaxed contract that no longer matches what packs are really
    held to in production.
    """

    @pytest.mark.parametrize("schema_filename", ["persona.schema.json", "menu.schema.json"])
    def test_fixture_schema_copy_is_byte_identical_to_the_real_schema(self, schema_filename):
        real_bytes = (_REAL_PERSONAS_DIR / schema_filename).read_bytes()
        fixture_bytes = (_FIXTURES_PERSONAS_DIR / schema_filename).read_bytes()
        assert fixture_bytes == real_bytes, (
            f"app/backend/tests/fixtures/personas/{schema_filename} has drifted from "
            f"personas/{schema_filename} -- copy the real file over the fixture copy "
            "(never edit the fixture copy independently; see this test class's docstring "
            "for why they're two files instead of one shared file)."
        )


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
