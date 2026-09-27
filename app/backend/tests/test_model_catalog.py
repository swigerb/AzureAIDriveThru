"""Model catalog loader tests for the drive-thru voice ordering backend (issue #75, P2-6).

Covers:
  - A valid `models.catalog` config + `AZURE_AI_MODEL_DEPLOYMENTS` env var loads cleanly and
    `.ids`/`.get()`/`__contains__`/`deployment_for()`/`is_deployed()`/`is_selectable()` all behave.
  - `ModelValidationError` on: non-list catalog, non-mapping entry, missing required field, unknown
    field, unknown pipeline value, wrong-typed `reasoning`/`toolCalling`/`runtime`, duplicate id,
    malformed `AZURE_AI_MODEL_DEPLOYMENTS` JSON, non-object `AZURE_AI_MODEL_DEPLOYMENTS`,
    non-string map entries.
  - An absent `models`/`models.catalog` section, or an unset/empty `AZURE_AI_MODEL_DEPLOYMENTS`,
    loads an empty-but-valid catalog rather than failing (both are optional layers until a persona
    or `?model=` actually needs them).
"""

import sys
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
