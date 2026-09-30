"""Processor interface / model-resolution tests for the drive-thru voice ordering backend (issue
#75, P2-6; revised per Rick's PR #106 review).

Covers:
  - `resolve_realtime_model`'s full algorithm (Rick's review item 1 -- NO default-path special
    case): EVERY id, including the persona's own pipeline default, must be catalogued for the
    right pipeline; `reasoning` always comes from that catalog entry; the ONLY back-compat
    carve-out is the *deployment* term for the default, which falls back to
    `default_deployment` with a logged warning when `AZURE_AI_MODEL_DEPLOYMENTS` doesn't map it.
    Every rejection case raises `ModelSelectionError` (not allowed, not catalogued, catalogued
    for the WRONG pipeline -- the processor-seam guard -- or catalogued but not deployed and not
    the default).
  - `PipelineProcessor`/`ProcessorRegistry` shape: a registry looks up by `pipeline_name`, refuses
    two processors registering the same name, and `RTMiddleTier` (rtmt.py) actually satisfies the
    `PipelineProcessor` Protocol (including its `handle` method, item 5).
  - `dispatch_processor` (Rick's review item 5, the REAL processor seam): routes a model to its
    OWN pipeline's registered processor, purely from the shared catalog -- never assumes "the
    pipeline is mine". `TestDispatchProcessor.
    test_cascade_model_routes_to_a_registered_cascade_processor_never_realtime` is the proof #82
    needs: a fake cascade processor receives cascade models without a fake "RTMiddleTier"
    processor ever being asked.
"""

import logging
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
    dispatch_processor,
    resolve_cascade_model,
    resolve_realtime_model,
)


@dataclass
class _FakePipelineCfg:
    default: str
    allowed: list


@dataclass
class _FakeModels:
    realtime: _FakePipelineCfg
    cascade: _FakePipelineCfg | None = None


@dataclass
class _FakeManifest:
    models: _FakeModels


@dataclass
class _FakePersona:
    id: str
    manifest: _FakeManifest


def _persona(
    default: str,
    allowed: list,
    cascade_default: str | None = None,
    cascade_allowed: list | None = None,
) -> _FakePersona:
    cascade_cfg = _FakePipelineCfg(default=cascade_default, allowed=cascade_allowed or []) if cascade_default is not None else None
    return _FakePersona(
        id="test-persona",
        manifest=_FakeManifest(
            models=_FakeModels(realtime=_FakePipelineCfg(default=default, allowed=allowed), cascade=cascade_cfg)
        ),
    )


class _FakeProcessor:
    """A minimal `PipelineProcessor` for a pipeline that doesn't have a real implementation yet
    (mirrors what a future #82 `CascadeProcessor` would look like from the
    dispatch seam's point of view). Records every call so a test can assert it was (or, more
    importantly, was NOT) reached."""

    def __init__(self, pipeline_name: str):
        self.pipeline_name = pipeline_name
        self.resolve_model_calls: list = []
        self.handle_calls: list = []

    def resolve_model(self, persona, requested_model_id):
        self.resolve_model_calls.append((persona, requested_model_id))
        raise NotImplementedError

    async def handle(self, request, persona, resolved_model):
        self.handle_calls.append((request, persona, resolved_model))
        raise NotImplementedError


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


# ═══════════════════════════════════════════════════════════════════════════════
# resolve_realtime_model -- Rick's PR #106 review item 1: no default-path special case
# ═══════════════════════════════════════════════════════════════════════════════

class TestOmittedOrDefaultModel:
    """The persona's own pipeline default, whether omitted or named explicitly, goes through
    EXACTLY the same catalog check as any other id now -- this whole class is the mutation
    check Rick asked for ("default bypasses catalog -> test fails"): reintroducing the old
    "the default never touches the catalog" special case makes every test below fail, either
    because it starts accepting an uncatalogued default it should reject, or because it starts
    returning `reasoning=None`/skips the deployment-map lookup it should now always perform."""

    def test_omitted_model_resolves_to_persona_default_via_the_catalog(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment"}')
        resolved = resolve_realtime_model(persona, None, catalog, default_deployment="fallback-deployment")
        assert resolved.id == "gpt-realtime-2.1"
        assert resolved.pipeline == "realtime"
        assert resolved.deployment == "prod-deployment"
        # Rick's item 1: reasoning is ALWAYS the catalog's own flag, even for the default.
        assert resolved.reasoning is True

    def test_explicit_default_model_id_is_the_same_path_as_omitting_it(self):
        persona = _persona(default="gpt-realtime-mini", allowed=["gpt-realtime-mini"])
        catalog = _catalog('{"gpt-realtime-mini": "mini-prod"}')
        resolved = resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="fallback-deployment")
        assert resolved.deployment == "mini-prod"
        assert resolved.reasoning is False

    def test_default_model_not_catalogued_at_all_now_raises(self):
        """THE test that would have failed the rejected PR: the persona's own default must be
        catalogued for its pipeline just like any other id -- no more "the default never
        touches the catalog" exception. `ModelCatalog.validate_persona_defaults` is what stops
        this from ever reaching production (fails startup instead) -- this proves the request
        path itself no longer has a silent bypass either, so the two defenses agree."""
        persona = _persona(default="not-catalogued-at-all", allowed=["not-catalogued-at-all"])
        with pytest.raises(ModelSelectionError, match="not in .*models.catalog"):
            resolve_realtime_model(persona, None, _catalog(), default_deployment="fallback-deployment")

    def test_default_with_no_deployment_mapped_falls_back_with_a_warning(self):
        """The ONE remaining back-compat carve-out (item 1): only the deployment term, and
        only for the default, falls back to `default_deployment` -- logged as a warning since
        it's transitional, never silent."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        catalog = _catalog()  # no deployments mapped at all
        resolved = resolve_realtime_model(persona, None, catalog, default_deployment="fallback-deployment")
        assert resolved.deployment == "fallback-deployment"
        assert resolved.reasoning is True  # reasoning is NOT part of the fallback -- catalog only

    def test_default_deployment_fallback_logs_a_warning(self, caplog):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        with caplog.at_level(logging.WARNING, logger="processors"):
            resolve_realtime_model(persona, None, _catalog(), default_deployment="fallback-deployment")
        assert any("AZURE_AI_MODEL_DEPLOYMENTS" in record.getMessage() for record in caplog.records)

    def test_non_default_model_with_no_deployment_mapped_is_rejected_not_fallen_back(self):
        """The fallback is default-ONLY -- an explicitly-requested, non-default, catalogued
        model with no deployment mapped must still 404, never silently reuse
        `default_deployment`."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment"}')  # mini not mapped
        with pytest.raises(ModelSelectionError, match="no deployment mapped"):
            resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="fallback-deployment")


class TestExplicitNonDefaultModel:
    def test_selectable_model_resolves_to_its_own_deployment_and_reasoning_flag(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment", "gpt-realtime-mini": "mini-deployment-42"}')
        resolved = resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="default-deployment")
        assert resolved.id == "gpt-realtime-mini"
        assert resolved.deployment == "mini-deployment-42"
        assert resolved.reasoning is False

    def test_selectable_reasoning_model_carries_reasoning_true(self):
        persona = _persona(default="gpt-realtime-mini", allowed=["gpt-realtime-mini", "gpt-realtime-2.1"])
        catalog = _catalog('{"gpt-realtime-mini": "mini-prod", "gpt-realtime-2.1": "reasoning-deployment"}')
        resolved = resolve_realtime_model(persona, "gpt-realtime-2.1", catalog, default_deployment="default-deployment")
        assert resolved.reasoning is True


class TestRejections:
    def test_not_in_persona_allowed_list_raises(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment", "gpt-realtime-mini": "mini-deployment"}')
        with pytest.raises(ModelSelectionError, match="not allowed"):
            resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="d")

    def test_unknown_model_id_raises(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "totally-made-up"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment"}')
        with pytest.raises(ModelSelectionError, match="not in .*models.catalog"):
            resolve_realtime_model(persona, "totally-made-up", catalog, default_deployment="d")

    def test_catalogued_but_not_deployed_raises(self):
        """In the catalog, allowed by the persona, but AZURE_AI_MODEL_DEPLOYMENTS doesn't map it
        yet -- must reject, never silently fall back to the default deployment (that fallback is
        default-id-only, see TestOmittedOrDefaultModel)."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-realtime-mini"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment"}')
        with pytest.raises(ModelSelectionError, match="no deployment mapped"):
            resolve_realtime_model(persona, "gpt-realtime-mini", catalog, default_deployment="d")

    def test_wrong_pipeline_raises_even_if_persona_allows_and_it_is_deployed(self):
        """The processor-seam guard: a persona could (mistakenly, or by a future authoring bug)
        list a `cascade`-catalogued id in its own `realtime.allowed` -- this must still be
        rejected for the realtime pipeline even though the id is catalogued AND has a deployment
        mapped, because its catalog `pipeline` is not "realtime"."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1", "gpt-5-mini"])
        catalog = _catalog('{"gpt-realtime-2.1": "prod-deployment", "gpt-5-mini": "some-cascade-deployment"}')
        with pytest.raises(ModelSelectionError, match="not in .*models.catalog for the realtime pipeline"):
            resolve_realtime_model(persona, "gpt-5-mini", catalog, default_deployment="d")


# ═══════════════════════════════════════════════════════════════════════════════
# PipelineProcessor / ProcessorRegistry shape (Rick's PR #106 review item 5)
# ═══════════════════════════════════════════════════════════════════════════════

# ═══════════════════════════════════════════════════════════════════════════════
# resolve_cascade_model (issue #82) -- same catalog/persona algorithm as realtime, but no
# AZURE_OPENAI_REALTIME_DEPLOYMENT-style back-compat fallback for the default.
# ═══════════════════════════════════════════════════════════════════════════════

_CASCADE_CATALOG_CFG = {
    "models": {
        "catalog": [
            {"id": "gpt-realtime-2.1", "pipeline": "realtime", "label": "GPT Realtime 2.1", "reasoning": True},
            {"id": "gpt-5-mini", "pipeline": "cascade", "label": "GPT-5 mini", "toolCalling": True},
            {"id": "phi-4", "pipeline": "cascade", "label": "Phi-4 (Foundry)", "toolCalling": True},
        ]
    }
}


def _cascade_catalog(deployments: str | None = None) -> ModelCatalog:
    env = {"AZURE_AI_MODEL_DEPLOYMENTS": deployments} if deployments else {}
    return ModelCatalog.load(config=_CASCADE_CATALOG_CFG, environ=env)


class TestResolveCascadeModel:
    def test_omitted_model_resolves_to_persona_cascade_default(self):
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini", "phi-4"])
        catalog = _cascade_catalog('{"gpt-5-mini": "gpt-5-mini-prod"}')
        resolved = resolve_cascade_model(persona, None, catalog)
        assert resolved.id == "gpt-5-mini"
        assert resolved.pipeline == "cascade"
        assert resolved.deployment == "gpt-5-mini-prod"

    def test_explicit_non_default_allowed_model_resolves(self):
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini", "phi-4"])
        catalog = _cascade_catalog('{"gpt-5-mini": "gpt-5-mini-prod", "phi-4": "phi-4-prod"}')
        resolved = resolve_cascade_model(persona, "phi-4", catalog)
        assert resolved.id == "phi-4"
        assert resolved.deployment == "phi-4-prod"

    def test_persona_with_no_cascade_block_raises(self):
        """Cascade isn't enabled for every persona -- unlike realtime, which every persona has."""
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"])  # no cascade_default given
        with pytest.raises(ModelSelectionError, match="no models.cascade configured"):
            resolve_cascade_model(persona, "gpt-5-mini", _cascade_catalog())

    def test_not_in_persona_allowed_list_raises(self):
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini"])
        catalog = _cascade_catalog('{"gpt-5-mini": "a", "phi-4": "b"}')
        with pytest.raises(ModelSelectionError, match="not allowed"):
            resolve_cascade_model(persona, "phi-4", catalog)

    def test_wrong_pipeline_raises(self):
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini", "gpt-realtime-2.1"])
        catalog = _cascade_catalog('{"gpt-5-mini": "a", "gpt-realtime-2.1": "b"}')
        with pytest.raises(ModelSelectionError, match="not in .*models.catalog for the cascade pipeline"):
            resolve_cascade_model(persona, "gpt-realtime-2.1", catalog)

    def test_default_with_no_deployment_mapped_is_rejected_not_fallen_back(self):
        """#82's key divergence from realtime: there is NO back-compat deployment fallback for
        the cascade default -- an undeployed default 404s exactly like any other undeployed
        id, since (unlike realtime) there is no pre-existing bare env var to preserve."""
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini"])
        with pytest.raises(ModelSelectionError, match="no deployment mapped"):
            resolve_cascade_model(persona, None, _cascade_catalog())  # no deployments mapped at all

    def test_non_default_model_with_no_deployment_mapped_raises(self):
        persona = _persona("gpt-realtime-2.1", ["gpt-realtime-2.1"], cascade_default="gpt-5-mini", cascade_allowed=["gpt-5-mini", "phi-4"])
        catalog = _cascade_catalog('{"gpt-5-mini": "a"}')  # phi-4 not mapped
        with pytest.raises(ModelSelectionError, match="no deployment mapped"):
            resolve_cascade_model(persona, "phi-4", catalog)


class TestProcessorRegistryAndProtocol:
    def test_registry_looks_up_by_pipeline_name(self):
        proc = _FakeProcessor("realtime")
        registry = ProcessorRegistry([proc])
        assert "realtime" in registry
        assert registry.get("realtime") is proc
        assert registry.get("cascade") is None
        assert "cascade" not in registry

    def test_registry_rejects_duplicate_pipeline_names(self):
        with pytest.raises(ValueError, match="Duplicate"):
            ProcessorRegistry([_FakeProcessor("realtime"), _FakeProcessor("realtime")])

    def test_rtmt_satisfies_pipeline_processor_protocol(self):
        from azure.core.credentials import AzureKeyCredential

        from rtmt import RTMiddleTier

        rtmt = RTMiddleTier("https://fake.openai.azure.com", "fake-deployment", AzureKeyCredential("k"))
        assert isinstance(rtmt, PipelineProcessor)
        assert rtmt.pipeline_name == "realtime"
        # Item 5: `handle` is now part of the Protocol too, not just `resolve_model`.
        assert callable(rtmt.handle)
        # Item 5: a fresh RTMiddleTier always has a safe-default registry containing itself,
        # so a deployment with no other pipeline registered yet keeps working unmodified.
        assert rtmt.processor_registry.get("realtime") is rtmt


# ═══════════════════════════════════════════════════════════════════════════════
# dispatch_processor -- Rick's PR #106 review item 5: the REAL processor seam
# ═══════════════════════════════════════════════════════════════════════════════

class TestDispatchProcessor:
    def test_dispatches_an_explicit_model_to_its_own_pipelines_processor(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        realtime_proc = _FakeProcessor("realtime")
        registry = ProcessorRegistry([realtime_proc])
        result = dispatch_processor(persona, "gpt-realtime-2.1", _catalog(), registry)
        assert result is realtime_proc

    def test_omitted_model_dispatches_via_the_personas_realtime_default(self):
        """Design doc 5.2: an omitted `?model=` always means the realtime pipeline (today's
        only WS endpoint), resolved from the PERSONA's own default -- never "whichever
        pipeline the caller happens to be"."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        realtime_proc = _FakeProcessor("realtime")
        registry = ProcessorRegistry([realtime_proc])
        result = dispatch_processor(persona, None, _catalog(), registry)
        assert result is realtime_proc

    def test_unknown_model_id_raises(self):
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        registry = ProcessorRegistry([_FakeProcessor("realtime")])
        with pytest.raises(ModelSelectionError, match="not in .*models.catalog"):
            dispatch_processor(persona, "totally-made-up", _catalog(), registry)

    def test_cascade_model_with_no_registered_cascade_processor_raises(self):
        """#82 hasn't landed yet in THIS registry -- a cascade-catalogued model must still
        404 via `ModelSelectionError`, never silently fall through to whatever processor
        happens to be registered (realtime)."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        registry = ProcessorRegistry([_FakeProcessor("realtime")])
        with pytest.raises(ModelSelectionError, match="no .*processor registered"):
            dispatch_processor(persona, "gpt-5-mini", _catalog(), registry)

    def test_cascade_model_routes_to_a_registered_cascade_processor_never_realtime(self):
        """THE proof Rick's item 5 requires, and the mutation check ("cascade model routed to
        RTMiddleTier -> dispatch test fails"): once a (fake) cascade processor is registered
        alongside a (fake) realtime one, a cascade-catalogued model is routed to the CASCADE
        processor -- never the realtime one -- straight from the shared catalog's own
        `pipeline` field, without ever asking the realtime processor to resolve or handle it.
        A future real `CascadeProcessor` (#82) is reached the exact same way, proving
        `RTMiddleTier.handle` is never entered for it."""
        persona = _persona(default="gpt-realtime-2.1", allowed=["gpt-realtime-2.1"])
        realtime_proc = _FakeProcessor("realtime")
        cascade_proc = _FakeProcessor("cascade")
        registry = ProcessorRegistry([realtime_proc, cascade_proc])

        result = dispatch_processor(persona, "gpt-5-mini", _catalog(), registry)

        assert result is cascade_proc
        assert result is not realtime_proc
        # Dispatch alone doesn't call resolve_model/handle (that's the caller's job, after
        # dispatch) -- but critically, NEITHER processor has been touched at all yet, and
        # certainly not the realtime one.
        assert realtime_proc.resolve_model_calls == []
        assert realtime_proc.handle_calls == []
