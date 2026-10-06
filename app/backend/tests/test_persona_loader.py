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
import os
import shutil
import stat
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import default_persona
from persona_loader import (
    Persona,
    PersonaCatalog,
    PersonaValidationError,
    _is_symlink_or_junction,
    default_personas_dir,
    resolve_personas_dir,
)

_REPO_ROOT = Path(__file__).resolve().parents[3]
_REAL_PERSONAS_DIR = _REPO_ROOT / "personas"
_FIXTURES_PERSONAS_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


@pytest.fixture
def personas_copy(tmp_path):
    """A throwaway copy of the real personas/ tree so mutation tests never touch the real pack."""
    dest = tmp_path / "personas"
    shutil.copytree(_REAL_PERSONAS_DIR, dest)
    return dest


@pytest.fixture
def fixture_personas_copy(tmp_path):
    """A throwaway copy of the neutral, non-branded fixture pack (test-alpha/test-beta/etc, the
    same family app/backend/tests/fixtures/personas/ provides for other suites), for schema-
    violation tests that must not name a real brand pack in their own source: naming a real
    brand in a new test line grows the checked-in rebrand-baseline word-count ratchet (#76)
    every time, whereas this fixture never mentions a real brand."""
    dest = tmp_path / "personas"
    shutil.copytree(_FIXTURES_PERSONAS_DIR, dest)
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

    def test_every_real_pack_keeps_d1_hero_invariants(self):
        """Issue 164 R5 (PR 167 round 1 review): every original's hero keeps the same
        Foundry-branding sentence shape (D1), across however many real packs exist -- only
        each original's own subject/product words differ."""
        import re

        callout_pattern = re.compile(r"^.+ ordering powered by Microsoft Foundry$")
        description_suffix = ", now voice activated with Microsoft Foundry + Azure AI Search grounding."

        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        for persona_id in catalog.ids:
            hero = catalog.get(persona_id).manifest.ui.hero
            assert callout_pattern.match(hero.headline), (
                f"{persona_id}: headline {hero.headline!r} does not match the shared D1 shape"
            )
            assert hero.description.endswith(description_suffix), (
                f"{persona_id}: description does not end with the shared D1 suffix"
            )
            assert len(hero.callouts) >= 2, f"{persona_id}: expected at least 2 hero callouts"
            foundry_callout = hero.callouts[1]
            assert foundry_callout.title == "FOUNDRY INFUSION", (
                f"{persona_id}: callouts[1].title is {foundry_callout.title!r}"
            )
            assert foundry_callout.detail == (
                "Microsoft Foundry + Azure OpenAI keep conversations flowing"
            ), f"{persona_id}: callouts[1].detail is {foundry_callout.detail!r}"
            # hero.poweredBy and app.footer are shared translation.json keys (not persona.json
            # fields), so D1's "exact in en" invariant holds as long as no pack shadows them
            # in its own ui.strings override block.
            for locale_strings in catalog.get(persona_id).manifest.ui.strings.values():
                assert "hero.poweredBy" not in locale_strings, (
                    f"{persona_id}: must not override the shared hero.poweredBy translation"
                )
                assert "app.footer" not in locale_strings, (
                    f"{persona_id}: must not override the shared app.footer translation"
                )

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

    def test_component_upcharge_without_included_size_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["bundles"]["resizeRule"] = "componentUpcharge"
            d["bundles"].pop("includedSize", None)
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(
                personas_dir=fixture_personas_copy,
                enabled=["test-alpha"],
                default_persona_id="test-alpha",
            )
        message = str(exc_info.value)
        assert "test-alpha" in message
        assert "includedSize" in message

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

    def test_models_local_pipeline_refuses_to_start(self, fixture_personas_copy):
        """Issue #155 dropped the `local` pipeline entirely: `_Models` only declares `realtime` and
        `cascade`, extra="forbid", so a pack that still lists a `models.local` entry (a stale copy
        from before the removal, or a hand-authored mistake) must be rejected the same way any other
        unknown field is, not silently ignored. Uses the neutral test-alpha fixture pack (not a real
        brand pack) so this doesn't grow the checked-in rebrand-baseline word-count ratchet (#76)."""
        def mutator(d):
            d["models"]["local"] = {"default": "phi-4-mini-local", "allowed": ["phi-4-mini-local"]}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

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

    def test_unknown_text_role_refuses_to_start(self, fixture_personas_copy):
        """Issue 164 R2 (PR 167 round 1 review): `ui.textRoles` values are constrained to the
        schema's textRole enum (primary/primaryDeep/secondary/accent/ink), extra="forbid" on
        the containing model. Uses the neutral test-alpha fixture pack, not a real brand pack,
        per R6 (keeps the rebrand-baseline word-count ratchet (#76) from growing)."""
        def mutator(d):
            d["ui"]["textRoles"] = {"badge": "not-a-real-role"}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

    def test_unknown_spotlight_row_tone_refuses_to_start(self, fixture_personas_copy):
        """Issue 164 R4 (PR 167 round 1 review): a spotlight card-one row's `tone` is
        constrained to primary/secondary/accent/ink -- a pack cannot invent its own role name."""
        def mutator(d):
            d["ui"]["hero"]["spotlight"][0]["rows"][0]["tone"] = "not-a-real-tone"
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

    def test_unknown_spotlight_accent_tone_refuses_to_start(self, fixture_personas_copy):
        """Issue 164 R4 (PR 167 round 1 review): a spotlight card-two `accentTone` override is
        constrained the same way as `tone` -- it cannot be an invented role name either."""
        def mutator(d):
            d["ui"]["hero"]["spotlight"][1]["accentTone"] = "not-a-real-tone"
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

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
# Issue #304: ``pronunciations`` -- the optional, pack-level phonetic respelling lexicon
# consulted only by the cascade pipeline's TTS input -- must validate both its keys and its
# values as non-empty, non-whitespace strings, same as every other pack must refuse to start
# on a schema or model violation rather than silently loading with a degraded field.
# ===========================================================================


class TestPronunciationsValidation:
    """Uses the neutral test-alpha fixture pack (not a real brand pack) for every mutation so
    this file's own brand-word baseline never needs to grow just because pronunciations
    validation gets dedicated coverage -- same convention as the other mutation tests above."""

    def test_pack_with_no_pronunciations_field_loads_with_an_empty_dict(self, fixture_personas_copy):
        """Pre-#304 packs (and any pack with no brand words at pronunciation risk) need no
        persona.json change at all -- the field defaults to {}."""
        def mutator(d):
            d.pop("pronunciations", None)
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        catalog = PersonaCatalog.load(
            personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")
        assert catalog.get("test-alpha").manifest.pronunciations == {}

    def test_valid_pronunciations_entry_loads(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"Munchkins": "Munch-kins"}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        catalog = PersonaCatalog.load(
            personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")
        assert catalog.get("test-alpha").manifest.pronunciations == {"Munchkins": "Munch-kins"}

    def test_empty_string_key_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"": "Munch-kins"}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

    def test_whitespace_only_key_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"   ": "Munch-kins"}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")

    def test_empty_string_value_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"Munchkins": ""}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")
        assert "test-alpha" in str(exc_info.value)

    def test_whitespace_only_value_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"Munchkins": "   "}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")
        assert "test-alpha" in str(exc_info.value)

    def test_non_string_value_refuses_to_start(self, fixture_personas_copy):
        def mutator(d):
            d["pronunciations"] = {"Munchkins": 123}
        _mutate_persona_json(fixture_personas_copy, "test-alpha", mutator)
        with pytest.raises(PersonaValidationError, match="test-alpha"):
            PersonaCatalog.load(personas_dir=fixture_personas_copy, enabled=["test-alpha"], default_persona_id="test-alpha")


# ===========================================================================
# Issue #144 (design doc section 18.2): every file under assets/ must be
# classifiable as anonymous-or-protected, or startup must refuse to load the pack.
# ===========================================================================


class TestPersonaAssetTypeValidation:
    """`_validate_persona_assets` (called from `_load_one_persona`) mirrors the
    frontend's `authorizedFetch.ts` public/protected asset split: any extension in
    `entra_auth.ANONYMOUS_ASSET_EXTENSIONS` is fine anywhere under `assets/`; a
    `.json` file is fine ONLY directly under `assets/demo/`; anything else refuses
    to start.

    Uses the default persona's own id (``default_persona.get_default_persona().id``)
    rather than a hardcoded brand name, so these tests don't add fresh brand-literal
    occurrences to the rebrand-word-count ratchet (#76) -- `personas_copy` is a
    throwaway copy of the REAL personas/ tree, so the default persona's id is still
    a real, on-disk pack directory name; only the literal spelling in this test's own
    source is avoided.
    """

    _PID = default_persona.get_default_persona().id

    def test_real_default_pack_assets_all_classify(self):
        """The real, shipped default pack (svg/ico/wav under assets/, .json under
        assets/demo/) must already pass -- this is a regression guard, not just a
        happy-path check on synthetic fixtures."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        assert self._PID in catalog.ids

    def test_every_anonymous_extension_passes(self, personas_copy, tmp_path):
        import entra_auth

        for suffix in sorted(entra_auth.ANONYMOUS_ASSET_EXTENSIONS):
            assets_dir = personas_copy / self._PID / "assets"
            (assets_dir / f"extra-asset{suffix}").write_bytes(b"x")
        # Must not raise for any of them.
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert self._PID in catalog.ids

    def test_uppercase_anonymous_extension_still_passes(self, personas_copy):
        """F4 (#163 round-1/round-2 review, decided for #147 C# parity): matching
        is CASE-INSENSITIVE -- an upper-case (or mixed-case) anonymous extension
        must classify identically to its lower-case form. `.JPG` is the
        canonical example the review called out by name."""
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "extra-asset.JPG").write_bytes(b"x")
        (assets_dir / "another-asset.Svg").write_bytes(b"<svg/>")
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert self._PID in catalog.ids

    def test_jpeg_is_not_an_anonymous_extension_and_refuses_to_start(self, personas_copy):
        """`.jpeg` was removed from `ANONYMOUS_ASSET_EXTENSIONS` (Rick's #159 round-1
        review, required item 2) -- the contract is exactly `.svg .png .jpg .webp
        .ico .wav .mp3`. A `.jpeg` asset has no defined classification, so it must
        refuse to start, the same as any other unrecognized extension."""
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "extra-asset.jpeg").write_bytes(b"x")
        with pytest.raises(PersonaValidationError, match="unrecognized file type"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_uppercase_jpeg_also_refuses_to_start(self, personas_copy):
        """F4: case-insensitivity only ever ADDS matches to the fixed 7-extension
        allow-list -- it must never accidentally widen it. `.JPEG` (any case) is
        not, and must never become, an anonymous extension."""
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "extra-asset.JPEG").write_bytes(b"x")
        with pytest.raises(PersonaValidationError, match="unrecognized file type"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_json_directly_under_assets_demo_passes(self, personas_copy):
        demo_dir = personas_copy / self._PID / "assets" / "demo"
        demo_dir.mkdir(parents=True, exist_ok=True)
        (demo_dir / "extraDemoFile.json").write_text("{}", encoding="utf-8")
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert self._PID in catalog.ids

    def test_json_outside_demo_dir_refuses_to_start(self, personas_copy):
        """A `.json` file directly under `assets/` (NOT `assets/demo/`) has no
        defined classification -- must fail loudly, not silently default to
        anonymous or protected."""
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "stray.json").write_text("{}", encoding="utf-8")
        with pytest.raises(PersonaValidationError, match="unrecognized file type"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_json_in_demo_subdirectory_refuses_to_start(self, personas_copy):
        """Must be DIRECTLY under assets/demo/, not a nested subdirectory of it --
        `path.parent == demo_dir` is an exact-parent check, not a prefix check."""
        nested = personas_copy / self._PID / "assets" / "demo" / "nested"
        nested.mkdir(parents=True, exist_ok=True)
        (nested / "buried.json").write_text("{}", encoding="utf-8")
        with pytest.raises(PersonaValidationError, match="unrecognized file type"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_unrecognized_extension_refuses_to_start(self, personas_copy):
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "malicious.exe").write_bytes(b"MZ")
        with pytest.raises(PersonaValidationError, match="unrecognized file type"):
            PersonaCatalog.load(personas_dir=personas_copy)

    def test_error_names_persona_and_offending_file(self, personas_copy):
        assets_dir = personas_copy / self._PID / "assets"
        (assets_dir / "unknown.bin").write_bytes(b"\x00")
        with pytest.raises(PersonaValidationError) as exc_info:
            PersonaCatalog.load(personas_dir=personas_copy)
        message = str(exc_info.value)
        assert self._PID in message
        assert "unknown.bin" in message

    def test_missing_assets_dir_is_not_an_error(self, personas_copy):
        """A persona pack with no assets/ directory at all has nothing to
        classify -- `_validate_persona_assets` must return early, not raise."""
        shutil.rmtree(personas_copy / self._PID / "assets")
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert self._PID in catalog.ids

    def test_nested_subdirectory_with_anonymous_extension_passes(self, personas_copy):
        """Anonymous extensions are allowed anywhere under assets/, not just at the
        top level (mirrors the real pack's assets/audio/*.wav layout)."""
        nested = personas_copy / self._PID / "assets" / "some" / "nested" / "dir"
        nested.mkdir(parents=True, exist_ok=True)
        (nested / "deep.svg").write_text("<svg/>", encoding="utf-8")
        catalog = PersonaCatalog.load(personas_dir=personas_copy)
        assert self._PID in catalog.ids

    def test_symlinked_asset_refuses_to_start(self, personas_copy):
        """N5 (#163 round-1 review): a symlink under assets/ -- even one with an
        otherwise-fine, anonymous-looking extension like `.svg` -- must refuse to
        start, rather than being silently served (potentially from OUTSIDE the
        pack's own directory). This project avoids depending on privileged
        symlink creation in tests (Windows dev machines/CI runners need elevated
        rights for a real symlink -- see `TestFixtureSchemasMatchRealSchemas`
        above), so `Path.is_symlink` is mocked for exactly one target path
        instead of creating a real symlink on disk."""
        assets_dir = personas_copy / self._PID / "assets"
        target = assets_dir / "sneaky.svg"
        target.write_bytes(b"<svg/>")

        real_is_symlink = Path.is_symlink

        def _fake_is_symlink(self):
            if self == target:
                return True
            return real_is_symlink(self)

        with mock.patch.object(Path, "is_symlink", _fake_is_symlink):
            with pytest.raises(PersonaValidationError, match="symlink"):
                PersonaCatalog.load(personas_dir=personas_copy)

    def test_persona_dir_itself_symlink_refuses_to_start(self, personas_copy):
        """#223 (Rick's #222 review): `_validate_persona_assets`'s `rglob("*")`
        only ever yields entries BELOW `assets/` -- it never considers the
        persona pack directory itself. If `personas/<id>/` is itself a symlink
        (e.g. pointing at an arbitrary directory elsewhere on the host), the
        old code never noticed. Mocked the same way as
        `test_symlinked_asset_refuses_to_start` above, for the same
        no-privileged-symlinks-in-CI reason."""
        pack_dir = personas_copy / self._PID

        real_is_symlink = Path.is_symlink

        def _fake_is_symlink(self):
            if self == pack_dir:
                return True
            return real_is_symlink(self)

        with mock.patch.object(Path, "is_symlink", _fake_is_symlink):
            with pytest.raises(PersonaValidationError, match="symlink"):
                PersonaCatalog.load(personas_dir=personas_copy)

    def test_assets_dir_itself_symlink_refuses_to_start(self, personas_copy):
        """#223: same gap as above, one level down -- `assets/` itself (not
        just something below it) being a symlink must also be rejected."""
        assets_dir = personas_copy / self._PID / "assets"

        real_is_symlink = Path.is_symlink

        def _fake_is_symlink(self):
            if self == assets_dir:
                return True
            return real_is_symlink(self)

        with mock.patch.object(Path, "is_symlink", _fake_is_symlink):
            with pytest.raises(PersonaValidationError, match="symlink"):
                PersonaCatalog.load(personas_dir=personas_copy)

    @pytest.mark.skipif(
        not hasattr(os.path, "isjunction"),
        reason="mocks os.path.isjunction, which only exists on Python 3.12+",
    )
    def test_junction_asset_refuses_to_start(self, personas_copy):
        """#223: a Windows directory junction reports `Path.is_symlink() ==
        False` (a junction sets `FILE_ATTRIBUTE_REPARSE_POINT` but not the
        reparse tag `is_symlink`/`os.path.islink` check for), so the N5 check
        alone silently let a junction under `assets/` through. Mocks
        `os.path.isjunction` for exactly one target, the same
        no-privileged-junction-creation-in-CI rationale as the symlink
        mocks above (junction creation needs elevated rights / `mklink /J`).
        Skipped on Python 3.11, which has no `os.path.isjunction` to mock --
        the 3.11 fallback path is covered separately by
        `TestJunctionFallbackOn311` below (#223 round 2, Rick's review)."""
        assets_dir = personas_copy / self._PID / "assets"
        target = assets_dir / "sneaky-junction"
        target.mkdir()

        real_isjunction = os.path.isjunction

        def _fake_isjunction(path):
            if Path(path) == target:
                return True
            return real_isjunction(path)

        with mock.patch("os.path.isjunction", _fake_isjunction):
            with pytest.raises(PersonaValidationError, match="junction"):
                PersonaCatalog.load(personas_dir=personas_copy)

    @pytest.mark.skipif(
        not hasattr(os.path, "isjunction"),
        reason="mocks os.path.isjunction, which only exists on Python 3.12+",
    )
    def test_junction_persona_dir_refuses_to_start(self, personas_copy):
        """#223: the junction gap applies at the pack-dir root too, not just
        under assets/. Skipped on Python 3.11 for the same reason as
        `test_junction_asset_refuses_to_start` above."""
        pack_dir = personas_copy / self._PID

        real_isjunction = os.path.isjunction

        def _fake_isjunction(path):
            if Path(path) == pack_dir:
                return True
            return real_isjunction(path)

        with mock.patch("os.path.isjunction", _fake_isjunction):
            with pytest.raises(PersonaValidationError, match="junction"):
                PersonaCatalog.load(personas_dir=personas_copy)


class TestJunctionFallbackOn311:
    """#223 round 2 (Rick's review): unit tests for `_is_symlink_or_junction`'s
    Python 3.11 fallback path directly (rather than through `PersonaCatalog.load`),
    since that path only runs when `os.path.isjunction` is unavailable (3.11-only)
    and these tests must still exercise it on the 3.12+ machines this repo's
    tests actually run on. `os.path.isjunction` is patched to `None` so
    `getattr(os.path, "isjunction", None)` sees it as absent, exactly matching
    how the real attribute-lookup behaves on 3.11 -- this does not require
    `delattr`/simulating a genuinely different Python version. `create=True`
    is required on that patch (unlike the plain `mock.patch("os.path.isjunction",
    ...)` string-target patches on the two 3.12-only tests above, which are
    skipped on 3.11 instead): `mock.patch.object` refuses to patch an
    attribute that doesn't already exist unless told to create it, and on a
    genuine Python 3.11 interpreter `os.path.isjunction` doesn't exist at all
    -- without `create=True` these tests would themselves raise
    `AttributeError` on 3.11, the opposite of what they're meant to verify
    (Rick's review, #223 round 3).

    `stat.IO_REPARSE_TAG_MOUNT_POINT`/`IO_REPARSE_TAG_SYMLINK` are themselves
    Windows-only `stat` module constants -- absent entirely on POSIX (this
    repo's CI runs these tests on Linux). `stat` is patched with
    `create=True` so these Windows-specific tag values exist for the
    duration of each test regardless of host OS, same spirit as patching
    `os.path.isjunction`/`os.lstat` above. The literal values used are the
    real, fixed Windows NTFS reparse-tag constants (`IO_REPARSE_TAG_MOUNT_POINT
    = 0xA0000003`, `IO_REPARSE_TAG_SYMLINK = 0xA000000C`), so a Windows run of
    this suite (where the real constants already exist) exercises the exact
    same values."""

    _MOUNT_POINT_TAG = getattr(stat, "IO_REPARSE_TAG_MOUNT_POINT", 0xA0000003)
    _SYMLINK_TAG = getattr(stat, "IO_REPARSE_TAG_SYMLINK", 0xA000000C)

    def test_plain_directory_is_not_flagged(self, tmp_path):
        """A normal, non-reparse-point directory must not be flagged by the
        3.11 fallback."""
        plain_dir = tmp_path / "plain"
        plain_dir.mkdir()
        with mock.patch.object(os.path, "isjunction", None, create=True):
            assert _is_symlink_or_junction(plain_dir) is False

    def test_mount_point_reparse_tag_is_flagged(self, tmp_path):
        """A real junction's reparse tag is `IO_REPARSE_TAG_MOUNT_POINT` --
        the fallback must flag exactly this tag."""
        junction_dir = tmp_path / "junction"
        junction_dir.mkdir()
        fake_stat = SimpleNamespace(st_reparse_tag=self._MOUNT_POINT_TAG)
        with mock.patch.object(stat, "IO_REPARSE_TAG_MOUNT_POINT", self._MOUNT_POINT_TAG, create=True):
            with mock.patch.object(os.path, "isjunction", None, create=True):
                with mock.patch("os.lstat", return_value=fake_stat):
                    assert _is_symlink_or_junction(junction_dir) is True

    def test_other_reparse_tag_is_not_flagged(self, tmp_path):
        """#223 round 2: the OLD (pre-review) 3.11 fallback checked only the
        generic `FILE_ATTRIBUTE_REPARSE_POINT` bit, which is set on EVERY
        reparse point -- not just mount-point junctions. That would have
        misflagged e.g. a OneDrive "online-only" cloud-filter placeholder file
        (directly relevant: this repo lives under a OneDrive-synced folder) as
        a forbidden junction. The narrowed fallback compares the specific
        reparse *tag* instead, so a non-mount-point reparse point (symlink
        tag used here as a stand-in for "some other, non-junction reparse
        type") must NOT be flagged."""
        other_dir = tmp_path / "other-reparse-point"
        other_dir.mkdir()
        fake_stat = SimpleNamespace(st_reparse_tag=self._SYMLINK_TAG)
        with mock.patch.object(stat, "IO_REPARSE_TAG_MOUNT_POINT", self._MOUNT_POINT_TAG, create=True):
            with mock.patch.object(os.path, "isjunction", None, create=True):
                with mock.patch("os.lstat", return_value=fake_stat):
                    assert _is_symlink_or_junction(other_dir) is False

    def test_mount_point_tag_absent_on_platform_returns_false(self, tmp_path):
        """On a platform with no `IO_REPARSE_TAG_MOUNT_POINT` concept at all
        (real POSIX; simulated here regardless of host OS), the fallback must
        not crash and must not treat every reparse-tagged path as a match --
        it has nothing meaningful to compare against, so it must return
        `False` rather than e.g. comparing `None == None`."""
        some_dir = tmp_path / "posix-style"
        some_dir.mkdir()
        fake_stat = SimpleNamespace(st_reparse_tag=self._MOUNT_POINT_TAG)
        with mock.patch.object(stat, "IO_REPARSE_TAG_MOUNT_POINT", None, create=True):
            with mock.patch.object(os.path, "isjunction", None, create=True):
                with mock.patch("os.lstat", return_value=fake_stat):
                    assert _is_symlink_or_junction(some_dir) is False

    def test_lstat_oserror_is_not_flagged(self, tmp_path):
        """A path that vanishes between the symlink/isjunction checks and the
        `os.lstat` fallback call (e.g. a race, or a dangling/unreadable
        target) must not raise -- it's treated as "not a junction", same as
        the pre-existing broad-check behavior."""
        missing = tmp_path / "does-not-exist"
        with mock.patch.object(os.path, "isjunction", None, create=True):
            with mock.patch("os.lstat", side_effect=OSError("boom")):
                assert _is_symlink_or_junction(missing) is False


# ===========================================================================
# Fixture schema copies stay in sync with the real schemas (Rick's PR #102 review item 3)
# ===========================================================================


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


# ===========================================================================
# Default personas/ directory resolution: pick by EXISTENCE, not by path depth (#129 review
# round 2). Covers both default_personas_dir (the two-candidate, schema-marker lookup) and
# resolve_personas_dir (adds the PERSONAS_DIR env var override and the "nothing qualifies"
# error naming every path tried).
# ===========================================================================


class TestDefaultPersonasDirResolution:
    def test_checkout_layout_resolves_repo_root_personas(self, tmp_path):
        """app/backend/<module>.py, two levels below a repo root that has personas/persona.schema.json."""
        repo = tmp_path / "repo"
        module = repo / "app" / "backend" / "persona_loader.py"
        module.parent.mkdir(parents=True)
        module.write_text("", encoding="utf-8")
        personas = repo / "personas"
        personas.mkdir()
        (personas / "persona.schema.json").write_text("{}", encoding="utf-8")

        assert default_personas_dir(module) == personas

    def test_image_layout_picks_adjacent_personas_even_over_a_stray_dir_two_levels_up(self, tmp_path):
        """The flattened container image puts the module and personas/ side by side (app/Dockerfile
        copies both straight onto /app). This reproduces that at an ARBITRARY nesting depth --
        not two levels, the old depth rule's lucky number -- with a stray personas/ sitting at
        exactly the old rule's "two levels up" location too, but WITHOUT a persona.schema.json.
        The adjacent, real personas/ (which does have the schema) must still win: existence, not
        depth, decides."""
        module_dir = tmp_path / "srv" / "some" / "nested" / "workdir" / "app"
        module_dir.mkdir(parents=True)
        module = module_dir / "persona_loader.py"
        module.write_text("", encoding="utf-8")

        # The stray "two levels up" directory: exists, but no schema -- must not be chosen.
        stray = module_dir.parent.parent / "personas"
        stray.mkdir(parents=True)

        # The real candidate: right next to the module.
        real_personas = module_dir / "personas"
        real_personas.mkdir()
        (real_personas / "persona.schema.json").write_text("{}", encoding="utf-8")

        assert default_personas_dir(module) == real_personas

    def test_neither_candidate_qualifying_returns_none(self, tmp_path):
        module = tmp_path / "app" / "backend" / "persona_loader.py"
        module.parent.mkdir(parents=True)
        module.write_text("", encoding="utf-8")
        # Neither default candidate directory exists at all.

        assert default_personas_dir(module) is None


class TestResolvePersonasDirEnvVarAndErrors:
    def test_personas_dir_env_var_overrides_the_defaults(self, tmp_path, monkeypatch):
        override = tmp_path / "override-personas"
        override.mkdir()
        (override / "persona.schema.json").write_text("{}", encoding="utf-8")
        monkeypatch.setenv("PERSONAS_DIR", str(override))

        # Any module path -- the env var short-circuits before either default candidate is
        # even considered.
        module = tmp_path / "app" / "backend" / "persona_loader.py"
        assert resolve_personas_dir(module) == override.resolve()

    def test_personas_dir_env_var_without_the_schema_raises(self, tmp_path, monkeypatch):
        override = tmp_path / "override-personas"
        override.mkdir()  # no persona.schema.json
        monkeypatch.setenv("PERSONAS_DIR", str(override))

        module = tmp_path / "app" / "backend" / "persona_loader.py"
        with pytest.raises(PersonaValidationError, match="does not exist or does not contain"):
            resolve_personas_dir(module)

    def test_no_env_var_and_neither_candidate_qualifying_raises_naming_both_paths(self, tmp_path, monkeypatch):
        monkeypatch.delenv("PERSONAS_DIR", raising=False)
        module = tmp_path / "app" / "backend" / "persona_loader.py"
        module.parent.mkdir(parents=True)

        module_dir = module.resolve().parent
        checkout_candidate = module_dir.parent.parent / "personas"
        image_candidate = module_dir / "personas"

        with pytest.raises(PersonaValidationError) as exc_info:
            resolve_personas_dir(module)
        message = str(exc_info.value)
        assert str(checkout_candidate) in message
        assert str(image_candidate) in message


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
