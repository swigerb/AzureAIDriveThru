"""Shared model catalog loader for the drive-thru voice ordering backend (issue #75, design doc
section 7.2).

Model flexibility on Microsoft Foundry has three layers, each owned by a different piece of
config and (per the squad's split) a different owner:

1. **Catalog** (this module; `config.yaml`'s `models: catalog:` list): the facts about a model
   that are the same on every deployment -- its id, which pipeline it belongs to
   (`realtime` | `cascade` | `local`), a display label, and its capabilities (`reasoning` for
   realtime/cascade, `toolCalling` for cascade, `runtime` for local). Append-only: a new model is
   a new list entry, never a schema change.
2. **Deployment** (this module; `AZURE_AI_MODEL_DEPLOYMENTS` env var, a JSON map of catalog id ->
   Foundry deployment name): which of the catalogued models actually exist on THIS deployment.
   Bicep emits this (issue #93; the deployment list lives in infra/model-deployments.json there).
   A catalog entry with no deployment mapped for it is not selectable (section 7.3).
3. **Persona** (`persona_loader.py`'s `_Models`/`_ModelPipeline`, issue #74, already built): which
   catalogued models a persona pack allows per pipeline, and its own default. Unaffected by this
   module -- a persona's `models.realtime.allowed` id doesn't have to be catalogued/deployed
   unless a guest actually asks for it (see `processors.py::resolve_realtime_model`).

Design doc section 7.3: "Selectable = catalog ∩ deployment ∩ persona-allowed." This module owns
the first two terms of that intersection; `processors.py` combines all three.

The realtime pipeline's own *default* model (a persona's `models.realtime.default`) is the one
deliberate exception to all of the above: it always resolves to `AZURE_OPENAI_REALTIME_DEPLOYMENT`
(today's single, pre-#75 deployment env var), whether or not `AZURE_AI_MODEL_DEPLOYMENTS` maps it
-- so that omitting `?model=` on `/realtime` is byte-for-byte unchanged behavior (#75 acceptance).
That back-compat rule lives in `processors.py`, not here: this module only knows about the catalog
and the deployment map, nothing about a persona's own default.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from typing import Any

from config_loader import get_config

__all__ = ["ModelCatalog", "ModelEntry", "ModelValidationError"]

_PIPELINES = frozenset({"realtime", "cascade", "local"})
_REQUIRED_ENTRY_FIELDS = frozenset({"id", "pipeline", "label"})
_KNOWN_ENTRY_FIELDS = _REQUIRED_ENTRY_FIELDS | frozenset({"reasoning", "toolCalling", "runtime"})

_DEPLOYMENTS_ENV_VAR = "AZURE_AI_MODEL_DEPLOYMENTS"


class ModelValidationError(Exception):
    """Raised when `config.yaml`'s `models.catalog` or the `AZURE_AI_MODEL_DEPLOYMENTS` env var
    is malformed. Always names the offending entry/field so a broken catalog is fixable from the
    error message alone -- never a silent partial load (same fail-fast style as
    `persona_loader.PersonaValidationError`)."""


@dataclass(frozen=True)
class ModelEntry:
    """One `models.catalog` row (design doc section 7.2)."""

    id: str
    pipeline: str  # "realtime" | "cascade" | "local"
    label: str
    reasoning: bool = False
    tool_calling: bool | None = None
    runtime: str | None = None

    @property
    def capabilities(self) -> dict[str, Any]:
        """A generic capabilities view, mirroring the design doc's per-pipeline capability
        flags: `reasoning` always present (defaults False), `toolCalling`/`runtime` only when
        this entry actually declared them."""
        caps: dict[str, Any] = {"reasoning": self.reasoning}
        if self.tool_calling is not None:
            caps["toolCalling"] = self.tool_calling
        if self.runtime is not None:
            caps["runtime"] = self.runtime
        return caps


def _parse_entry(raw: Any, index: int) -> ModelEntry:
    if not isinstance(raw, dict):
        raise ModelValidationError(
            f"config.yaml models.catalog[{index}] must be a mapping, got {type(raw).__name__}"
        )
    unknown = set(raw) - _KNOWN_ENTRY_FIELDS
    if unknown:
        raise ModelValidationError(
            f"config.yaml models.catalog[{index}] has unknown field(s): {sorted(unknown)}"
        )
    missing = _REQUIRED_ENTRY_FIELDS - set(raw)
    if missing:
        raise ModelValidationError(
            f"config.yaml models.catalog[{index}] is missing required field(s): {sorted(missing)}"
        )
    model_id = raw["id"]
    if not isinstance(model_id, str) or not model_id.strip():
        raise ModelValidationError(f"config.yaml models.catalog[{index}]'s 'id' must be a non-empty string")
    pipeline = raw["pipeline"]
    if pipeline not in _PIPELINES:
        raise ModelValidationError(
            f"config.yaml models.catalog[{index}] ({model_id!r}) has unknown pipeline {pipeline!r}; "
            f"expected one of {sorted(_PIPELINES)}"
        )
    label = raw["label"]
    if not isinstance(label, str) or not label.strip():
        raise ModelValidationError(f"config.yaml models.catalog[{index}] ({model_id!r})'s 'label' must be a non-empty string")
    reasoning = raw.get("reasoning", False)
    if not isinstance(reasoning, bool):
        raise ModelValidationError(f"config.yaml models.catalog[{index}] ({model_id!r})'s 'reasoning' must be a bool")
    tool_calling = raw.get("toolCalling")
    if tool_calling is not None and not isinstance(tool_calling, bool):
        raise ModelValidationError(f"config.yaml models.catalog[{index}] ({model_id!r})'s 'toolCalling' must be a bool")
    runtime = raw.get("runtime")
    if runtime is not None and not isinstance(runtime, str):
        raise ModelValidationError(f"config.yaml models.catalog[{index}] ({model_id!r})'s 'runtime' must be a string")
    return ModelEntry(id=model_id, pipeline=pipeline, label=label, reasoning=reasoning, tool_calling=tool_calling, runtime=runtime)


def _parse_deployment_map(raw: str | None) -> dict[str, str]:
    if not raw or not raw.strip():
        return {}
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ModelValidationError(f"{_DEPLOYMENTS_ENV_VAR} is not valid JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise ModelValidationError(f"{_DEPLOYMENTS_ENV_VAR} must be a JSON object (catalog id -> deployment name), got {type(parsed).__name__}")
    for key, value in parsed.items():
        if not isinstance(key, str) or not isinstance(value, str) or not value.strip():
            raise ModelValidationError(
                f"{_DEPLOYMENTS_ENV_VAR} entry {key!r}: {value!r} must map a string catalog id to a non-empty string deployment name"
            )
    return dict(parsed)


class ModelCatalog:
    """The validated, in-memory `models.catalog` (layer 1) plus the deployment map (layer 2).

    Construct via `ModelCatalog.load()` at startup (same fail-fast pattern as
    `PersonaCatalog.load()`); tests can also build one directly from the two constituent dicts.
    """

    def __init__(self, entries: dict[str, ModelEntry], deployments: dict[str, str]):
        self._entries = dict(entries)
        self._deployments = dict(deployments)

    @property
    def ids(self) -> list[str]:
        return sorted(self._entries)

    def __contains__(self, model_id: str) -> bool:
        return model_id in self._entries

    def get(self, model_id: str) -> ModelEntry:
        try:
            return self._entries[model_id]
        except KeyError:
            raise KeyError(
                f"Model {model_id!r} is not in the catalog. Known models: {', '.join(self.ids) or '(none)'}"
            ) from None

    def deployment_for(self, model_id: str) -> str | None:
        """The Foundry deployment name for *model_id*, or `None` if `AZURE_AI_MODEL_DEPLOYMENTS`
        doesn't map it yet. Does NOT apply the realtime-pipeline-default back-compat rule -- see
        this module's own docstring and `processors.py::resolve_realtime_model`."""
        return self._deployments.get(model_id)

    def is_deployed(self, model_id: str) -> bool:
        return self.deployment_for(model_id) is not None

    def is_selectable(self, model_id: str, pipeline: str) -> bool:
        """True iff *model_id* is catalogued, catalogued for exactly *pipeline* (the
        processor-seam guard -- #75's "processor seam bypassed" mutation check), and has a
        deployment mapped (design doc section 7.3's catalog ∩ deployment). Persona-allowed is
        the caller's own job (persona-scoped, not catalog-scoped)."""
        entry = self._entries.get(model_id)
        return entry is not None and entry.pipeline == pipeline and self.is_deployed(model_id)

    @classmethod
    def load(cls, *, config: dict[str, Any] | None = None, environ: os._Environ[str] | dict[str, str] | None = None) -> ModelCatalog:
        cfg = config if config is not None else get_config()
        env = os.environ if environ is None else environ
        models_cfg = cfg.get("models") or {}
        if not isinstance(models_cfg, dict):
            raise ModelValidationError(f"config.yaml's 'models' section must be a mapping, got {type(models_cfg).__name__}")
        raw_catalog = models_cfg.get("catalog") or []
        if not isinstance(raw_catalog, list):
            raise ModelValidationError(f"config.yaml models.catalog must be a list, got {type(raw_catalog).__name__}")
        entries: dict[str, ModelEntry] = {}
        for index, raw_entry in enumerate(raw_catalog):
            entry = _parse_entry(raw_entry, index)
            if entry.id in entries:
                raise ModelValidationError(f"config.yaml models.catalog has a duplicate model id {entry.id!r} (entry {index})")
            entries[entry.id] = entry
        deployments = _parse_deployment_map(env.get(_DEPLOYMENTS_ENV_VAR))
        return cls(entries=entries, deployments=deployments)
