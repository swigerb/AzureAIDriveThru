"""Processor interface / model-resolution tests for the drive-thru voice ordering backend (issue
#75, P2-6).

Covers:
  - `resolve_realtime_model`'s full algorithm: omitted `?model=` -> persona default (never
    touches the catalog); explicit-but-equal-to-default is the same unchanged path; an
    explicit, non-default, persona-allowed + catalogued + deployed model resolves to its own
    deployment and catalog `reasoning` flag; every rejection case raises `ModelSelectionError`
    (not allowed, not catalogued, catalogued for the WRONG pipeline -- the processor-seam guard
    -- or catalogued but not deployed).
  - `PipelineProcessor`/`ProcessorRegistry` shape: a registry looks up by `pipeline_name`, refuses
    two processors registering the same name, and `RTMiddleTier` (rtmt.py) actually satisfies the
    `PipelineProcessor` Protocol.
"""

import sys
from dataclasses import dataclass
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from model_catalog import ModelCatalog
from processors import (
    ModelSelectionError,
    PipelineProcessor,
    ProcessorRegistry,
    resolve_realtime_model,
)


@dataclass
class _FakePipelineCfg:
    default: str
    allowed: list


@dataclass
class _FakeModels:
    realtime: _FakePipelineCfg


@dataclass
class _FakeManifest:
    models: _FakeModels


@dataclass
class _FakePersona:
    id: str
    manifest: _FakeManifest


def _persona(default: str, allowed: list) -> _FakePersona:
    return _FakePersona(id="test-persona", manifest=_FakeManifest(models=_FakeModels(realtime=_FakePipelineCfg(default=default, allowed=allowed))))


_CATALOG_CFG = {
    "models": {
        "catalog": [
            {"id": "gpt-realtime-2.1", "pipeline": "realtime", "label": "GPT Realtime 2.1", "reasoning": True},
            {"id": "gpt-realtime-mini", "pipeline": "realtime", "label": "GPT Realtime mini", "reasoning": False},
            {"id": "gpt-5-mini", "pipeline": "cascade", "label": "GPT-5 mini", "toolCalling": True},
        ]
    }
}


def _catalog(deployments: str | None = None) -> ModelCatalog:
    env = {"AZURE_AI_MODEL_DEPLOYMENTS": deployments} if deployments else {}
    return ModelCatalog.load(config=_CATALOG_CFG, environ=env)


class TestOmittedOrDefaultModel:
    def test_omitted_model_resolves_to_persona_default_and_configured_deployment(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        resolved = resolve_realtime_model(persona, None, _catalog(), default_deployment="my-configured-deployment")
        assert resolved.id == "gpt-realtime-2.1"
        assert resolved.pipeline == "realtime"
        assert resolved.deployment == "my-configured-deployment"
        assert resolved.reasoning is None

    def test_explicit_default_model_is_the_same_unchanged_path(self):
        """An explicit `?model=` that just names the persona's own default must behave
        identically to omitting it entirely -- same deployment, reasoning=None -- and must not
        require the id to even be catalogued (today's default realtime model doesn't have to be
        in models.catalog for this to work, matching #75's back-compat requirement)."""
        persona = _persona(default="not-catalogued-at-all", allowed=["not-catalogued-at-all"])
        resolved = resolve_realtime_model(persona, "not-catalogued-at-all", _catalog(), default_deployment="my-configured-deployment")
        assert resolved.deployment == "my-configured-deployment"
        assert resolved.reasoning is None

    def test_default_path_never_touches_the_catalog_or_deployment_map(self):
        """Even an EMPTY catalog (no entries, no deployments) must not break the default path."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        empty_catalog = ModelCatalog.load(config={}, environ={})
        resolved = resolve_realtime_model(persona, None, empty_catalog, default_deployment="d")
        assert resolved.deployment == "d"
        assert resolved.reasoning is None


class TestExplicitNonDefaultModel:
    def test_selectable_model_resolves_to_its_own_deployment_and_reasoning_flag(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
        catalog = _catalog('{"gpt-realtime-mini": "mini-deployment-42"}')
        resolved = resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="default-deployment")
        assert resolved.id == "gpt-realtime-mini"
        assert resolved.deployment == "mini-deployment-42"
        assert resolved.reasoning is False

    def test_selectable_reasoning_model_carries_reasoning_true(self):
        persona = _persona(default="gpt-realtime-mini", allowed=["gpt-realtime-mini", "gpt-realtime-2.1"])
        catalog = _catalog('{"gpt-realtime-2.1": "reasoning-deployment"}')
        resolved = resolve_realtime_model(persona, "gpt-realtime-2.1", catalog, default_deployment="default-deployment")
        assert resolved.reasoning is True


class TestRejections:
    def test_not_in_persona_allowed_list_raises(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        catalog = _catalog('{"gpt-realtime-mini": "mini-deployment"}')
        with pytest.raises(ModelSelectionError, match="not allowed"):
            resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="d")

    def test_unknown_model_id_raises(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "totally-made-up"])
        with pytest.raises(ModelSelectionError, match="not selectable"):
            resolve_realtime_model(persona, "totally-made-up", _catalog(), default_deployment="d")

    def test_catalogued_but_not_deployed_raises(self):
        """In the catalog, allowed by the persona, but AZURE_AI_MODEL_DEPLOYMENTS doesn't map it
        yet -- must reject, never silently fall back to the default deployment."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
        with pytest.raises(ModelSelectionError, match="not selectable"):
            resolve_realtime_model(persona, "gpt-realtime-mini", _catalog(), default_deployment="d")

    def test_wrong_pipeline_raises_even_if_persona_allows_and_it_is_deployed(self):
        """The processor-seam guard: a persona could (mistakenly, or by a future authoring bug)
        list a `cascade`-catalogued id in its own `realtime.allowed` -- this must still be
        rejected for the realtime pipeline even though the id is catalogued AND has a deployment
        mapped, because its catalog `pipeline` is not "realtime"."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-5-mini"])
        catalog = _catalog('{"gpt-5-mini": "some-cascade-deployment"}')
        with pytest.raises(ModelSelectionError, match="not selectable"):
            resolve_realtime_model(persona, "gpt-5-mini", catalog, default_deployment="d")


class TestProcessorRegistryAndProtocol:
    def test_registry_looks_up_by_pipeline_name(self):
        class _FakeProcessor:
            pipeline_name = "realtime"

            def resolve_model(self, persona, requested_model_id):
                raise NotImplementedError

        proc = _FakeProcessor()
        registry = ProcessorRegistry([proc])
        assert "realtime" in registry
        assert registry.get("realtime") is proc
        assert registry.get("cascade") is None
        assert "cascade" not in registry

    def test_registry_rejects_duplicate_pipeline_names(self):
        class _FakeProcessor:
            pipeline_name = "realtime"

            def resolve_model(self, persona, requested_model_id):
                raise NotImplementedError

        with pytest.raises(ValueError, match="Duplicate"):
            ProcessorRegistry([_FakeProcessor(), _FakeProcessor()])

    def test_rtmt_satisfies_pipeline_processor_protocol(self):
        from azure.core.credentials import AzureKeyCredential

        from rtmt import RTMiddleTier

        rtmt = RTMiddleTier("https://fake.openai.azure.com", "fake-deployment", AzureKeyCredential("k"))
        assert isinstance(rtmt, PipelineProcessor)
        assert rtmt.pipeline_name == "realtime"
