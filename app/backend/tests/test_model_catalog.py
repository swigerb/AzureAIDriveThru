"""Model catalog loader tests for the drive-thru voice ordering backend (issue #75, P2-6;
revised per Rick's PR #106 review).

Covers:
  - A valid `models.catalog` config + `AZURE_AI_MODEL_DEPLOYMENTS` env var loads cleanly and
    `.ids`/`.get()`/`__contains__`/`deployment_for()`/`is_deployed()`/`is_catalogued_for()`/
    `is_selectable()` all behave.
  - `ModelValidationError` on: non-list catalog, non-mapping entry, missing required field, unknown
    field, unknown pipeline value, wrong-typed `reasoning`/`toolCalling`/`runtime`, duplicate id,
    malformed `AZURE_AI_MODEL_DEPLOYMENTS` JSON, non-object `AZURE_AI_MODEL_DEPLOYMENTS`,
    non-string map entries.
  - An absent `models`/`models.catalog` section, or an unset/empty `AZURE_AI_MODEL_DEPLOYMENTS`,
    loads an empty-but-valid catalog rather than failing (both are optional layers until a persona
    or `?model=` actually needs them).
  - Rick's PR #106 review item 1 (startup fail-fast): `validate_persona_defaults` raises when an
    enabled persona's own pipeline default isn't catalogued for that pipeline, only warns for a
    non-default `allowed` id, and passes silently when every default is catalogued correctly.
"""

import logging
import sys
from dataclasses import dataclass, field
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from model_catalog import ModelCatalog, ModelEntry, ModelValidationError

_VALID_CATALOG_CFG = {
    "models": {
        "catalog": [
            {"id": "gpt-realtime-2.1", "pipeline": "realtime", "label": "GPT Realtime 2.1", "reasoning": True},
            {"id": "gpt-realtime-mini", "pipeline": "realtime", "label": "GPT Realtime mini", "reasoning": False},
            {"id": "gpt-5-mini", "pipeline": "cascade", "label": "GPT-5 mini", "toolCalling": True},
        ]
    }
}


class TestValidLoad:
    def test_loads_entries_and_ids(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        assert catalog.ids == ["gpt-5-mini", "gpt-realtime-2.1", "gpt-realtime-mini"]
        assert "gpt-realtime-2.1" in catalog
        assert "unknown-model" not in catalog

    def test_get_returns_model_entry(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        entry = catalog.get("gpt-realtime-2.1")
        assert isinstance(entry, ModelEntry)
        assert entry.pipeline == "realtime"
        assert entry.label == "GPT Realtime 2.1"
        assert entry.reasoning is True
        assert entry.capabilities == {"reasoning": True}

    def test_cascade_entry_capabilities_include_tool_calling(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        entry = catalog.get("gpt-5-mini")
        assert entry.capabilities == {"reasoning": False, "toolCalling": True}

    def test_get_unknown_model_raises_key_error(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        with pytest.raises(KeyError, match="not in the catalog"):
            catalog.get("does-not-exist")

    def test_deployment_map_from_env(self):
        catalog = ModelCatalog.load(
            config=_VALID_CATALOG_CFG,
            environ={"AZURE_AI_MODEL_DEPLOYMENTS": '{"gpt-realtime-mini": "my-mini-deployment"}'},
        )
        assert catalog.deployment_for("gpt-realtime-mini") == "my-mini-deployment"
        assert catalog.is_deployed("gpt-realtime-mini") is True
        assert catalog.deployment_for("gpt-realtime-2.1") is None
        assert catalog.is_deployed("gpt-realtime-2.1") is False

    def test_is_selectable_requires_catalog_pipeline_and_deployment(self):
        catalog = ModelCatalog.load(
            config=_VALID_CATALOG_CFG,
            environ={"AZURE_AI_MODEL_DEPLOYMENTS": '{"gpt-realtime-mini": "d1", "gpt-5-mini": "d2"}'},
        )
        assert catalog.is_selectable("gpt-realtime-mini", "realtime") is True
        # Catalogued, deployed, but for a DIFFERENT pipeline -- the processor-seam guard.
        assert catalog.is_selectable("gpt-5-mini", "realtime") is False
        assert catalog.is_selectable("gpt-5-mini", "cascade") is True
        # Catalogued, right pipeline, but no deployment mapped.
        assert catalog.is_selectable("gpt-realtime-2.1", "realtime") is False
        # Not catalogued at all.
        assert catalog.is_selectable("nope", "realtime") is False

    def test_is_catalogued_for_ignores_deployment(self):
        """Rick's PR #106 review item 1: `is_catalogued_for` is the catalog-only half of
        `is_selectable` -- it must say True for a catalogued model even with NO deployment
        mapped at all, since `resolve_realtime_model` needs that distinction for the realtime
        default's deployment-fallback branch."""
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        assert catalog.is_catalogued_for("gpt-realtime-2.1", "realtime") is True
        # Catalogued, but for a DIFFERENT pipeline -- the processor-seam guard.
        assert catalog.is_catalogued_for("gpt-5-mini", "realtime") is False
        assert catalog.is_catalogued_for("gpt-5-mini", "cascade") is True
        # Not catalogued at all.
        assert catalog.is_catalogued_for("nope", "realtime") is False


class TestOptionalSections:
    def test_missing_models_section_loads_empty_catalog(self):
        catalog = ModelCatalog.load(config={}, environ={})
        assert catalog.ids == []

    def test_missing_catalog_key_loads_empty_catalog(self):
        catalog = ModelCatalog.load(config={"models": {}}, environ={})
        assert catalog.ids == []

    def test_unset_deployments_env_yields_no_deployments(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        assert catalog.is_deployed("gpt-realtime-2.1") is False

    def test_blank_deployments_env_yields_no_deployments(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={"AZURE_AI_MODEL_DEPLOYMENTS": "   "})
        assert catalog.is_deployed("gpt-realtime-2.1") is False


class TestCatalogValidationErrors:
    def test_catalog_not_a_list_raises(self):
        with pytest.raises(ModelValidationError, match="must be a list"):
            ModelCatalog.load(config={"models": {"catalog": {"id": "x"}}}, environ={})

    def test_entry_not_a_mapping_raises(self):
        with pytest.raises(ModelValidationError, match="must be a mapping"):
            ModelCatalog.load(config={"models": {"catalog": ["not-a-dict"]}}, environ={})

    def test_missing_required_field_raises(self):
        with pytest.raises(ModelValidationError, match="missing required field"):
            ModelCatalog.load(config={"models": {"catalog": [{"id": "x", "pipeline": "realtime"}]}}, environ={})

    def test_unknown_field_raises(self):
        with pytest.raises(ModelValidationError, match="unknown field"):
            ModelCatalog.load(
                config={"models": {"catalog": [{"id": "x", "pipeline": "realtime", "label": "X", "bogus": 1}]}},
                environ={},
            )

    def test_unknown_pipeline_raises(self):
        with pytest.raises(ModelValidationError, match="unknown pipeline"):
            ModelCatalog.load(
                config={"models": {"catalog": [{"id": "x", "pipeline": "quantum", "label": "X"}]}}, environ={}
            )

    def test_non_bool_reasoning_raises(self):
        with pytest.raises(ModelValidationError, match="'reasoning' must be a bool"):
            ModelCatalog.load(
                config={"models": {"catalog": [{"id": "x", "pipeline": "realtime", "label": "X", "reasoning": "yes"}]}},
                environ={},
            )

    def test_non_bool_tool_calling_raises(self):
        with pytest.raises(ModelValidationError, match="'toolCalling' must be a bool"):
            ModelCatalog.load(
                config={"models": {"catalog": [{"id": "x", "pipeline": "cascade", "label": "X", "toolCalling": "yes"}]}},
                environ={},
            )

    def test_non_string_runtime_raises(self):
        with pytest.raises(ModelValidationError, match="'runtime' must be a string"):
            ModelCatalog.load(
                config={"models": {"catalog": [{"id": "x", "pipeline": "local", "label": "X", "runtime": 5}]}},
                environ={},
            )

    def test_duplicate_id_raises(self):
        with pytest.raises(ModelValidationError, match="duplicate model id"):
            ModelCatalog.load(
                config={
                    "models": {
                        "catalog": [
                            {"id": "x", "pipeline": "realtime", "label": "X"},
                            {"id": "x", "pipeline": "realtime", "label": "X again"},
                        ]
                    }
                },
                environ={},
            )

    def test_empty_id_raises(self):
        with pytest.raises(ModelValidationError, match="non-empty string"):
            ModelCatalog.load(config={"models": {"catalog": [{"id": "  ", "pipeline": "realtime", "label": "X"}]}}, environ={})


class TestDeploymentMapValidationErrors:
    def test_malformed_json_raises(self):
        with pytest.raises(ModelValidationError, match="not valid JSON"):
            ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={"AZURE_AI_MODEL_DEPLOYMENTS": "{not json"})

    def test_non_object_json_raises(self):
        with pytest.raises(ModelValidationError, match="must be a JSON object"):
            ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={"AZURE_AI_MODEL_DEPLOYMENTS": "[1, 2, 3]"})

    def test_non_string_value_raises(self):
        with pytest.raises(ModelValidationError, match="non-empty string deployment name"):
            ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={"AZURE_AI_MODEL_DEPLOYMENTS": '{"gpt-realtime-mini": 5}'})

    def test_empty_string_value_raises(self):
        with pytest.raises(ModelValidationError, match="non-empty string deployment name"):
            ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={"AZURE_AI_MODEL_DEPLOYMENTS": '{"gpt-realtime-mini": ""}'})


class TestRealConfigYaml:
    """The actual config.yaml shipped in this repo must itself load cleanly and contain the
    expected realtime rows -- a regression guard distinct from the synthetic-config unit tests
    above."""

    def test_real_config_yaml_loads(self):
        from config_loader import get_config

        catalog = ModelCatalog.load(config=get_config(), environ={})
        assert "gpt-realtime-2.1" in catalog
        assert "gpt-realtime-mini" in catalog
        assert catalog.get("gpt-realtime-2.1").pipeline == "realtime"
        assert catalog.get("gpt-realtime-2.1").reasoning is True
        assert catalog.get("gpt-realtime-mini").reasoning is False


# ═══════════════════════════════════════════════════════════════════════════════
# validate_persona_defaults -- Rick's PR #106 review item 1 (startup fail-fast)
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class _FakePipelineCfg:
    default: str
    allowed: list = field(default_factory=list)


@dataclass
class _FakeModels:
    realtime: _FakePipelineCfg | None = None
    cascade: _FakePipelineCfg | None = None
    local: _FakePipelineCfg | None = None


@dataclass
class _FakeManifest:
    models: _FakeModels


@dataclass
class _FakePersona:
    id: str
    manifest: _FakeManifest


class _FakePersonaCatalog:
    """A minimal stand-in for `persona_loader.PersonaCatalog`, exposing only the `.ids`/`.get()`
    shape `validate_persona_defaults` actually uses -- avoids pulling in the full persona
    loader/fixture machinery just to test the catalog-side validation in isolation."""

    def __init__(self, personas: list[_FakePersona]):
        self._by_id = {p.id: p for p in personas}

    @property
    def ids(self) -> list[str]:
        return list(self._by_id)

    def get(self, persona_id: str) -> _FakePersona:
        return self._by_id[persona_id]


class TestValidatePersonaDefaults:
    def test_passes_silently_when_every_default_is_catalogued(self):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        personas = _FakePersonaCatalog([
            _FakePersona(id="alpha", manifest=_FakeManifest(models=_FakeModels(
                realtime=_FakePipelineCfg(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
            ))),
        ])
        catalog.validate_persona_defaults(personas)  # must not raise

    def test_raises_when_a_personas_default_is_not_catalogued(self):
        """THE mutation check Rick asked for at startup: an enabled persona's own default
        model must be catalogued, or the app must refuse to start rather than let it 404 on
        the first request."""
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        personas = _FakePersonaCatalog([
            _FakePersona(id="alpha", manifest=_FakeManifest(models=_FakeModels(
                realtime=_FakePipelineCfg(default="not-catalogued-at-all", allowed=["not-catalogued-at-all"])
            ))),
        ])
        with pytest.raises(ModelValidationError, match="alpha.*realtime default model"):
            catalog.validate_persona_defaults(personas)

    def test_raises_when_a_personas_default_is_catalogued_for_the_wrong_pipeline(self):
        """The processor-seam guard applies to startup validation too: a realtime default that
        is only catalogued as a `cascade` model must still fail fast."""
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        personas = _FakePersonaCatalog([
            _FakePersona(id="alpha", manifest=_FakeManifest(models=_FakeModels(
                realtime=_FakePipelineCfg(default="gpt-5-mini", allowed=["gpt-5-mini"])
            ))),
        ])
        with pytest.raises(ModelValidationError, match="realtime default model"):
            catalog.validate_persona_defaults(personas)

    def test_only_warns_for_a_non_default_uncatalogued_allowed_id(self, caplog):
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        personas = _FakePersonaCatalog([
            _FakePersona(id="alpha", manifest=_FakeManifest(models=_FakeModels(
                realtime=_FakePipelineCfg(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "not-catalogued-at-all"])
            ))),
        ])
        with caplog.at_level(logging.WARNING, logger="model_catalog"):
            catalog.validate_persona_defaults(personas)  # must not raise
        assert any("not-catalogued-at-all" in record.getMessage() for record in caplog.records)

    def test_skips_pipelines_the_persona_does_not_use(self):
        """A persona with only a `realtime` block (no `cascade`/`local`) must not be penalized
        for pipelines it never declares."""
        catalog = ModelCatalog.load(config=_VALID_CATALOG_CFG, environ={})
        personas = _FakePersonaCatalog([
            _FakePersona(id="alpha", manifest=_FakeManifest(models=_FakeModels(
                realtime=_FakePipelineCfg(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"]),
                cascade=None,
                local=None,
            ))),
        ])
        catalog.validate_persona_defaults(personas)  # must not raise

