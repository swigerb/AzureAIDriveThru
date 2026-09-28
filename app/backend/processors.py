"""Processor interface for the drive-thru voice ordering backend (issue #75 / #82, design doc
section 7.4).

A "processor" is one pipeline (`realtime` | `cascade` | `local`, design doc section 7.1) behind
one shared interface, so #82's cascade pipeline and #81's local pipeline can plug in beside the
realtime path (`RTMiddleTier`) without `app.py`/`rtmt.py`'s session-handling code caring which one
is actually live for a given persona/model. This mirrors the C# skeleton's
`Backend/Sessions/IPipelineProcessor.cs`, which is intentionally the same shape and (per its own
docstring) still unbound to more than one implementation as of this wave -- this module is the
Python side of that same seam, and settles the `models.catalog` shape `ModelCatalog.cs` was
waiting on (see `docs/dotnet_mapping.md`'s "Known ambiguity" note).

`RTMiddleTier` (rtmt.py) is the first (and, until #82/#81 land, only) implementation:
`pipeline_name = "realtime"`. Nothing outside this module hardcodes that string when deciding
whether a model is usable for realtime -- `resolve_realtime_model` below always checks a model's
catalog `pipeline` field against the *caller's own* `pipeline_name`, not a literal. A future
`CascadeProcessor`/`LocalProcessor` (#82/#81) would implement the same `PipelineProcessor` shape
and register alongside `RTMiddleTier` in a `ProcessorRegistry`.

Rick's PR #106 review item 5: `dispatch_processor` below is the REAL processor seam. Once a
persona and a requested model id are known, it (a) resolves which pipeline the model belongs to
straight from the shared catalog -- never from which processor happens to be asking -- and
(b) looks that pipeline's processor up in the registry, 404-ing (`ModelSelectionError`) if none is
registered for it yet. `rtmt.py::_websocket_handler` calls this BEFORE calling `resolve_model`/
`handle` on whatever it returns, so a cascade-pipeline model is routed to a (future, #82) cascade
processor without `RTMiddleTier`/`handle` ever being entered -- see
`tests/test_processors.py::TestDispatchProcessor` for the fake-cascade-processor proof.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol, runtime_checkable

if TYPE_CHECKING:
    from aiohttp import web

    from model_catalog import ModelCatalog
    from persona_loader import Persona

logger = logging.getLogger(__name__)

__all__ = [
    "ModelSelectionError",
    "PipelineProcessor",
    "ProcessorRegistry",
    "ResolvedModel",
    "dispatch_processor",
    "resolve_cascade_model",
    "resolve_local_model",
    "resolve_realtime_model",
]


class ModelSelectionError(Exception):
    """Raised when a requested model isn't selectable for a persona/pipeline/deployment
    combination -- unknown, not in the persona's own `allowed` list, catalogued for a different
    pipeline (the processor-seam guard), or catalogued but not (yet) deployed. The caller
    (`rtmt.py::_websocket_handler`) turns this into the same plain HTTP 404 an unknown/disabled
    persona already gets -- never a silent fallback to some other model (design doc section
    5.2)."""


@dataclass(frozen=True)
class ResolvedModel:
    """The outcome of validating a session's requested (or defaulted) model id against its
    persona's allow-list and the shared catalog/deployment map -- design doc section 7.3:
    "Selectable = catalog ∩ deployment ∩ persona-allowed."

    Rick's PR #106 review item 1: `reasoning` is ALWAYS the catalog entry's own `reasoning`
    flag -- for every model, including the persona's own pipeline default. There is no
    default-path special case here any more; `reasoning=None` was exactly what Rick's review
    rejected (the default silently falling back to a deployment-name heuristic instead of the
    catalog). Design doc's "reasoning sent only for catalog reasoning models" (section 7.5).
    """

    id: str
    pipeline: str
    deployment: str
    reasoning: bool


@runtime_checkable
class PipelineProcessor(Protocol):
    """The seam #82's cascade pipeline (and #81's local pipeline) plug into. Deliberately
    minimal -- this wave defines only the shape (same note as the C# skeleton's
    `IPipelineProcessor`): a pipeline identifies itself and can resolve a persona's requested
    model against its own allow-list/catalog/deployment data. Owning a connection's full
    lifetime (the realtime relay loop, resume handling, teardown, ...) is each processor's own
    business beyond this shape, exactly as `RTMiddleTier` already does today."""

    #: "realtime" | "cascade" | "local" -- must match a persona.json `models` key and a
    #: `ModelCatalog` entry's `pipeline`.
    pipeline_name: str

    def resolve_model(self, persona: Persona, requested_model_id: str | None) -> ResolvedModel:
        """Validate *requested_model_id* (or the persona's own pipeline default, when `None`)
        for *persona* on this processor's own pipeline. Raises `ModelSelectionError` on an
        unknown, disallowed, cross-wired or undeployed model."""
        ...

    async def handle(self, request: web.Request, persona: Persona, resolved_model: ResolvedModel) -> web.StreamResponse:
        """Rick's PR #106 review item 5: own this connection's ENTIRE lifetime from here on --
        the WebSocket upgrade, session creation, the relay/processing loop, and teardown. Called
        by `rtmt.py::_websocket_handler` only AFTER the shared origin/token/persona checks have
        passed and `dispatch_processor` has picked THIS processor for *resolved_model*'s own
        pipeline -- never called directly for a model whose pipeline doesn't match
        `pipeline_name` (that's exactly what `dispatch_processor` prevents). `RTMiddleTier.handle`
        is today's existing upgrade+relay path, moved here verbatim; a future `CascadeProcessor`
        (#82) implements its own."""
        ...


class ProcessorRegistry:
    """Looks up the `PipelineProcessor` for a pipeline name. With only `RTMiddleTier` registered
    today this looks like unnecessary indirection -- that's deliberate: it is the concrete seam a
    mutation test can bypass (dispatch straight to the realtime processor regardless of which
    pipeline a resolved model actually belongs to) to prove it's load-bearing once #82/#81 add a
    second entry."""

    def __init__(self, processors: list[PipelineProcessor] | tuple[PipelineProcessor, ...]):
        by_pipeline: dict[str, PipelineProcessor] = {}
        for processor in processors:
            if processor.pipeline_name in by_pipeline:
                raise ValueError(f"Duplicate pipeline processor registered for {processor.pipeline_name!r}")
            by_pipeline[processor.pipeline_name] = processor
        self._by_pipeline = by_pipeline

    def __contains__(self, pipeline_name: str) -> bool:
        return pipeline_name in self._by_pipeline

    def get(self, pipeline_name: str) -> PipelineProcessor | None:
        return self._by_pipeline.get(pipeline_name)


def resolve_realtime_model(
    persona: Persona,
    requested_model_id: str | None,
    model_catalog: ModelCatalog,
    default_deployment: str,
    *,
    pipeline_name: str = "realtime",
) -> ResolvedModel:
    """Resolve a `/realtime?model=` request for `persona`'s realtime pipeline.

    Algorithm (design doc sections 5.2, 7.3; Rick's PR #106 review item 1 -- no default-path
    special case):
      1. Omitted `?model=` -> the persona's own `models.realtime.default`.
      2. The id must be in the persona's own `models.realtime.allowed` list -- persona-scoped,
         checked regardless of whether the id is even catalogued (a persona can allow a model
         this deployment doesn't stock yet; it just won't be selectable, below).
      3. EVERY id -- including the default -- must be catalogued for *pipeline_name* (not, say,
         `cascade` -- the processor-seam guard). `reasoning` always comes from that catalog
         entry; there is no id for which this is skipped.
      4. The id's deployment comes from `AZURE_AI_MODEL_DEPLOYMENTS`. The ONLY back-compat
         exception is on this term, and only for the persona's own default: if the map doesn't
         cover it (yet), it falls back to *default_deployment*
         (`AZURE_OPENAI_REALTIME_DEPLOYMENT`) so that omitting `?model=` keeps working on a
         deployment that hasn't populated the map yet -- logged as a warning, since that's a
         transitional, not a steady, state. Any OTHER id with no deployment mapped is rejected
         outright (never a silent fallback for an explicitly-requested, non-default model).

    Raises `ModelSelectionError` (never returns a partial/best-effort result) if any check fails.
    """
    pipeline_cfg = persona.manifest.models.realtime
    model_id = requested_model_id if requested_model_id is not None else pipeline_cfg.default
    is_default = model_id == pipeline_cfg.default

    if model_id not in pipeline_cfg.allowed and not is_default:
        raise ModelSelectionError(
            f"Model {model_id!r} is not allowed for persona {persona.id!r}'s {pipeline_name} pipeline "
            f"(allowed: {pipeline_cfg.allowed})"
        )

    if not model_catalog.is_catalogued_for(model_id, pipeline_name):
        raise ModelSelectionError(
            f"Model {model_id!r} is not in config.yaml's models.catalog for the {pipeline_name} pipeline"
        )
    entry = model_catalog.get(model_id)

    deployment = model_catalog.deployment_for(model_id)
    if deployment is None:
        if not is_default:
            raise ModelSelectionError(
                f"Model {model_id!r} is catalogued for the {pipeline_name} pipeline but has no "
                f"deployment mapped in AZURE_AI_MODEL_DEPLOYMENTS"
            )
        logger.warning(
            "Model %r (persona %r's %s default) has no AZURE_AI_MODEL_DEPLOYMENTS entry -- "
            "falling back to AZURE_OPENAI_REALTIME_DEPLOYMENT (%r). Populate the deployment map "
            "to remove this warning.",
            model_id, persona.id, pipeline_name, default_deployment,
        )
        deployment = default_deployment

    return ResolvedModel(id=model_id, pipeline=pipeline_name, deployment=deployment, reasoning=entry.reasoning)


def resolve_cascade_model(
    persona: Persona,
    requested_model_id: str | None,
    model_catalog: ModelCatalog,
    *,
    pipeline_name: str = "cascade",
) -> ResolvedModel:
    """Resolve a cascade-pipeline session's requested model (issue #82).

    Same catalog ∩ deployment ∩ persona-allowed algorithm as `resolve_realtime_model` (design
    doc sections 5.2, 7.3), EXCEPT there is no `AZURE_OPENAI_REALTIME_DEPLOYMENT`-style
    back-compat fallback for the pipeline default: unlike the realtime pipeline (which predates
    #75 and had to keep an existing bare env var working), the cascade pipeline is new in this
    issue, so there is no old behavior to preserve here. EVERY cascade model -- including a
    persona's own `models.cascade.default` -- must have a real `AZURE_AI_MODEL_DEPLOYMENTS`
    entry; an undeployed default is rejected exactly like an undeployed non-default id, never a
    silent fallback (Rick's PR #106 review item 1's "no default-path special case", taken one
    step further here since cascade has no legacy fallback to even offer).

    Raises `ModelSelectionError` if *persona* has no `models.cascade` block at all (the pipeline
    isn't enabled for this persona), or any of the usual unknown/disallowed/cross-wired/
    undeployed checks fail.
    """
    pipeline_cfg = persona.manifest.models.cascade
    if pipeline_cfg is None:
        raise ModelSelectionError(f"Persona {persona.id!r} has no models.cascade configured -- cascade is not enabled for it")

    model_id = requested_model_id if requested_model_id is not None else pipeline_cfg.default
    is_default = model_id == pipeline_cfg.default

    if model_id not in pipeline_cfg.allowed and not is_default:
        raise ModelSelectionError(
            f"Model {model_id!r} is not allowed for persona {persona.id!r}'s {pipeline_name} pipeline "
            f"(allowed: {pipeline_cfg.allowed})"
        )

    if not model_catalog.is_catalogued_for(model_id, pipeline_name):
        raise ModelSelectionError(
            f"Model {model_id!r} is not in config.yaml's models.catalog for the {pipeline_name} pipeline"
        )
    entry = model_catalog.get(model_id)

    deployment = model_catalog.deployment_for(model_id)
    if deployment is None:
        raise ModelSelectionError(
            f"Model {model_id!r} is catalogued for the {pipeline_name} pipeline but has no "
            f"deployment mapped in AZURE_AI_MODEL_DEPLOYMENTS"
        )

    return ResolvedModel(id=model_id, pipeline=pipeline_name, deployment=deployment, reasoning=entry.reasoning)


def resolve_local_model(
    persona: Persona,
    requested_model_id: str | None,
    model_catalog: ModelCatalog,
    *,
    pipeline_name: str = "local",
) -> ResolvedModel:
    """Resolve a local-pipeline session's requested model (issue #81, design doc section 7.3).

    Same catalog ∩ deployment ∩ persona-allowed algorithm as `resolve_cascade_model`, with one
    difference in what "deployed" means: the local pipeline has no Foundry deployment at all --
    there is no per-model entry to look up, only one process-wide companion runtime. So instead
    of `AZURE_AI_MODEL_DEPLOYMENTS`/`deployment_for`, this checks
    `model_catalog.local_runtime_endpoint`, which is non-`None` iff the `LOCAL_RUNTIME_ENDPOINT`
    env var is configured (`model_catalog.py`). An unconfigured runtime is rejected exactly like
    an undeployed cascade model, including for the persona's own default -- there is no
    back-compat fallback for local, same as cascade: issue #81's whole point is that local mode
    "only activates when the local runtime endpoint is configured", with no exception for the
    default model. This is also the mutation-test guard for "local selectable without a
    configured runtime" -- remove this check (or the `model_catalog.py` gating it composes
    with) and `test_processors.py`/`test_local_processor.py`'s runtime-not-configured row fails.

    `ResolvedModel.deployment` carries the local runtime's base URL
    (`model_catalog.local_runtime_endpoint`) rather than a Foundry deployment name --
    `LocalProcessor` builds its `HttpLocalRuntimeClient` directly from it, mirroring how
    cascade/realtime use `.deployment` as "the place this model actually lives."

    Raises `ModelSelectionError` if *persona* has no `models.local` block at all (local mode
    isn't enabled for this persona), if the runtime endpoint isn't configured, or any of the
    usual unknown/disallowed/cross-wired checks fail.
    """
    pipeline_cfg = persona.manifest.models.local
    if pipeline_cfg is None:
        raise ModelSelectionError(f"Persona {persona.id!r} has no models.local configured -- local mode is not enabled for it")

    model_id = requested_model_id if requested_model_id is not None else pipeline_cfg.default
    is_default = model_id == pipeline_cfg.default

    if model_id not in pipeline_cfg.allowed and not is_default:
        raise ModelSelectionError(
            f"Model {model_id!r} is not allowed for persona {persona.id!r}'s {pipeline_name} pipeline "
            f"(allowed: {pipeline_cfg.allowed})"
        )

    if not model_catalog.is_catalogued_for(model_id, pipeline_name):
        raise ModelSelectionError(
            f"Model {model_id!r} is not in config.yaml's models.catalog for the {pipeline_name} pipeline"
        )
    entry = model_catalog.get(model_id)

    endpoint = model_catalog.local_runtime_endpoint
    if endpoint is None:
        raise ModelSelectionError(
            f"Model {model_id!r} is catalogued for the {pipeline_name} pipeline but the local "
            f"runtime endpoint is not configured (set LOCAL_RUNTIME_ENDPOINT to activate local mode)"
        )

    return ResolvedModel(id=model_id, pipeline=pipeline_name, deployment=endpoint, reasoning=entry.reasoning)


def dispatch_processor(
    persona: Persona,
    requested_model_id: str | None,
    model_catalog: ModelCatalog,
    registry: ProcessorRegistry,
) -> PipelineProcessor:
    """Rick's PR #106 review item 5: the REAL processor seam. Determines which pipeline
    *requested_model_id* (or, when omitted, `persona`'s own realtime default -- design doc 5.2:
    an omitted `?model=` always means the realtime pipeline, not "whichever pipeline the last
    request happened to use") belongs to, straight from the shared catalog, and returns that
    pipeline's registered processor -- never the caller's own processor by default. This is what
    makes `rtmt.py::_websocket_handler` route a cascade model to a (future, #82) cascade
    processor without `RTMiddleTier.handle` ever being entered, even though the handler itself is
    a method on `RTMiddleTier`: the routing decision is made HERE, before any processor-specific
    code runs, not by asking `self` to resolve its own model.

    Raises `ModelSelectionError` (turned into the same plain 404 as any other unknown/disallowed
    model by the caller) when:
      * the id isn't catalogued at all (unknown model), or
      * its pipeline has no processor registered yet (e.g. `local` before #81 lands -- this is
        how an unimplemented pipeline still correctly 404s today instead of crashing; `cascade`
        left this list once #82 registered `CascadeProcessor`).
    """
    model_id = requested_model_id if requested_model_id is not None else persona.manifest.models.realtime.default
    try:
        pipeline_name = model_catalog.get(model_id).pipeline
    except KeyError as exc:
        raise ModelSelectionError(f"Model {model_id!r} is not in config.yaml's models.catalog") from exc

    processor = registry.get(pipeline_name)
    if processor is None:
        raise ModelSelectionError(
            f"Model {model_id!r} belongs to the {pipeline_name!r} pipeline, which has no "
            f"processor registered yet"
        )
    return processor
