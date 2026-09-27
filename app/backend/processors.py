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
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol, runtime_checkable

if TYPE_CHECKING:
    from model_catalog import ModelCatalog
    from persona_loader import Persona

__all__ = [
    "ModelSelectionError",
    "PipelineProcessor",
    "ProcessorRegistry",
    "ResolvedModel",
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

    `reasoning` is `None` for the persona's own default realtime model (today's unchanged,
    non-catalog-driven path -- the caller keeps using its existing name-heuristic/config-driven
    reasoning decision); it is the catalog entry's own `reasoning` flag for any other,
    explicitly-selected model, per the design doc's "reasoning sent only for catalog reasoning
    models" (section 7.5).
    """

    id: str
    pipeline: str
    deployment: str
    reasoning: bool | None


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

    Algorithm (design doc sections 5.2, 7.3):
      1. Omitted `?model=` -> the persona's own `models.realtime.default`.
      2. The id must be in the persona's own `models.realtime.allowed` list -- persona-scoped,
         checked regardless of whether the id is even catalogued (a persona can allow a model
         this deployment doesn't stock yet; it just won't be selectable, below).
      3. If the id equals the persona's default: resolve to *default_deployment*
         (`AZURE_OPENAI_REALTIME_DEPLOYMENT`) unconditionally, `reasoning=None` (today's exact,
         unchanged, non-catalog-driven path -- #75 acceptance: "keep behavior identical when no
         model param is given"). This branch never touches `model_catalog`.
      4. Otherwise: the id must be catalogued for *pipeline_name* (not, say, `cascade` -- the
         processor-seam guard) AND have a deployment mapped in `AZURE_AI_MODEL_DEPLOYMENTS`.
         Resolves to that deployment, `reasoning=<catalog entry's own reasoning flag>`.

    Raises `ModelSelectionError` (never returns a partial/best-effort result) if any check fails.
    """
    pipeline_cfg = persona.manifest.models.realtime
    model_id = requested_model_id if requested_model_id is not None else pipeline_cfg.default

    if model_id not in pipeline_cfg.allowed and model_id != pipeline_cfg.default:
        raise ModelSelectionError(
            f"Model {model_id!r} is not allowed for persona {persona.id!r}'s {pipeline_name} pipeline "
            f"(allowed: {pipeline_cfg.allowed})"
        )

    if model_id == pipeline_cfg.default:
        return ResolvedModel(id=model_id, pipeline=pipeline_name, deployment=default_deployment, reasoning=None)

    if not model_catalog.is_selectable(model_id, pipeline_name):
        raise ModelSelectionError(
            f"Model {model_id!r} is not selectable for the {pipeline_name} pipeline (not catalogued "
            f"for it, or has no deployment mapped in AZURE_AI_MODEL_DEPLOYMENTS)"
        )
    entry = model_catalog.get(model_id)
    deployment = model_catalog.deployment_for(model_id)
    assert deployment is not None  # guaranteed by is_selectable() above
    return ResolvedModel(id=model_id, pipeline=pipeline_name, deployment=deployment, reasoning=entry.reasoning)
