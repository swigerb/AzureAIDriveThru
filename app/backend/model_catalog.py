"""Shared model catalog loader for the drive-thru voice ordering backend (issue #75, design doc
section 7.2).

Model flexibility on Microsoft Foundry has three layers, each owned by a different piece of
config and (per the squad's split) a different owner:

1. **Catalog** (this module; `config.yaml`'s `models: catalog:` list): the facts about a model
   that are the same on every deployment -- its id, which pipeline it belongs to
   (`realtime` | `cascade`), a display label, and its capabilities (`reasoning` for
   realtime/cascade, `toolCalling` for cascade). Append-only: a new model is
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

Rick's PR #106 review item 1: there is NO default-path special case. Every model a persona can
bind to -- including its own pipeline default -- resolves through the catalog exactly like any
other requested id: it must be catalogued for the right pipeline (`is_catalogued_for`), and its
`reasoning`/`toolCalling` capabilities always come from that catalog entry, never from
a deployment-name heuristic. The ONLY back-compat carve-out is on the *deployment* term, and only
for the realtime pipeline's default: if `AZURE_AI_MODEL_DEPLOYMENTS` doesn't map it yet, it falls
back to `AZURE_OPENAI_REALTIME_DEPLOYMENT` (today's single, pre-#75 deployment env var) so that
omitting `?model=` on `/realtime` keeps working on a deployment that hasn't populated the map yet
-- with a logged warning, since that's a transitional, not a steady, state. That fallback rule
lives in `processors.py::resolve_realtime_model`, not here: this module only knows about the
catalog and the deployment map, nothing about a persona's own default.

Startup fail-fast (Rick's PR #106 review item 1, second half): `validate_persona_defaults` checks
every enabled persona's own pipeline defaults are catalogued for the right pipeline -- an enabled
persona whose default model isn't in the catalog must stop the app from starting, the same
fail-fast style as a malformed catalog entry itself, never a confusing 404 on the first
`?model=`-omitted request. A non-default `allowed` id that isn't catalogued only logs a warning:
it only 404s if a guest actually asks for it (see `processors.py::resolve_realtime_model`).
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass
from typing import Any

from config_loader import get_config

__all__ = ["CascadeAudioConfig", "ModelCatalog", "ModelEntry", "ModelValidationError"]

logger = logging.getLogger(__name__)

_PIPELINES = frozenset({"realtime", "cascade"})
_REQUIRED_ENTRY_FIELDS = frozenset({"id", "pipeline", "label"})
_KNOWN_ENTRY_FIELDS = _REQUIRED_ENTRY_FIELDS | frozenset({"reasoning", "toolCalling"})

_DEPLOYMENTS_ENV_VAR = "AZURE_AI_MODEL_DEPLOYMENTS"


@dataclass(frozen=True)
class CascadeAudioConfig:
    """`config.yaml` `models.cascade` (issue #82, design doc section 7.2): the catalog ids for
    the cascade pipeline's own transcription (speech-to-text) and TTS (text-to-speech) models.

    These are NOT `models.catalog` rows -- they're not user-selectable chat models, just the
    two fixed audio deployments every cascade session uses regardless of which cascade *chat*
    model (`ModelEntry` with `pipeline: cascade`) the session is bound to. Their actual Foundry
    deployment names still come from the SAME `AZURE_AI_MODEL_DEPLOYMENTS` map as chat models
    (`ModelCatalog.deployment_for`) -- there is no separate audio deployment map."""

    transcription: str
    tts: str


def _parse_cascade_audio_config(raw: Any) -> CascadeAudioConfig | None:
    if raw is None:
        return None
    if not isinstance(raw, dict):
        raise ModelValidationError(f"config.yaml models.cascade must be a mapping, got {type(raw).__name__}")
    unknown = set(raw) - {"transcription", "tts"}
    if unknown:
        raise ModelValidationError(f"config.yaml models.cascade has unknown field(s): {sorted(unknown)}")
    missing = {"transcription", "tts"} - set(raw)
    if missing:
        raise ModelValidationError(f"config.yaml models.cascade is missing required field(s): {sorted(missing)}")
    transcription, tts = raw["transcription"], raw["tts"]
    if not isinstance(transcription, str) or not transcription.strip():
        raise ModelValidationError("config.yaml models.cascade's 'transcription' must be a non-empty string")
    if not isinstance(tts, str) or not tts.strip():
        raise ModelValidationError("config.yaml models.cascade's 'tts' must be a non-empty string")
    return CascadeAudioConfig(transcription=transcription, tts=tts)


class ModelValidationError(Exception):
    """Raised when `config.yaml`'s `models.catalog` or the `AZURE_AI_MODEL_DEPLOYMENTS` env var
    is malformed. Always names the offending entry/field so a broken catalog is fixable from the
    error message alone -- never a silent partial load (same fail-fast style as
    `persona_loader.PersonaValidationError`)."""


@dataclass(frozen=True)
class ModelEntry:
    """One `models.catalog` row (design doc section 7.2)."""

    id: str
    pipeline: str  # "realtime" | "cascade"
    label: str
    reasoning: bool = False
    tool_calling: bool | None = None

    @property
    def capabilities(self) -> dict[str, Any]:
        """A generic capabilities view, mirroring the design doc's per-pipeline capability
        flags: `reasoning` always present (defaults False), `toolCalling` only when this entry
        actually declared it."""
        caps: dict[str, Any] = {"reasoning": self.reasoning}
        if self.tool_calling is not None:
            caps["toolCalling"] = self.tool_calling
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
    return ModelEntry(id=model_id, pipeline=pipeline, label=label, reasoning=reasoning, tool_calling=tool_calling)


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

    def __init__(self, entries: dict[str, ModelEntry], deployments: dict[str, str],
                 cascade_audio: CascadeAudioConfig | None = None):
        self._entries = dict(entries)
        self._deployments = dict(deployments)
        self._cascade_audio = cascade_audio

    @property
    def cascade_audio(self) -> CascadeAudioConfig | None:
        """`models.cascade`'s transcription/tts catalog ids (issue #82), or `None` if
        `config.yaml` doesn't declare one -- e.g. a deployment with the cascade pipeline
        unregistered, or a test fixture catalog that doesn't need it. `CascadeProcessor`
        resolves each id's actual deployment name via `deployment_for`, same as any chat
        model."""
        return self._cascade_audio

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
        """True iff *model_id* is available to select right now -- `AZURE_AI_MODEL_DEPLOYMENTS`
        maps it (`deployment_for`). Unknown model ids are simply not deployed."""
        return self.deployment_for(model_id) is not None

    def is_catalogued_for(self, model_id: str, pipeline: str) -> bool:
        """True iff *model_id* is catalogued at all AND catalogued for exactly *pipeline* (the
        processor-seam guard -- #75's "processor seam bypassed" mutation check). Deliberately
        does NOT check deployment -- see `is_selectable` for the full catalog ∩ deployment
        check, and `processors.py::resolve_realtime_model` for why the two need to be checked
        independently (the realtime default's deployment-fallback branch)."""
        entry = self._entries.get(model_id)
        return entry is not None and entry.pipeline == pipeline

    def is_selectable(self, model_id: str, pipeline: str) -> bool:
        """True iff *model_id* is catalogued for exactly *pipeline* (`is_catalogued_for`) AND has
        a deployment mapped (design doc section 7.3's catalog ∩ deployment). Persona-allowed is
        the caller's own job (persona-scoped, not catalog-scoped). Rick's PR #106 review item 1:
        no default-path exception here -- a pipeline default that only reaches its deployment via
        the `AZURE_OPENAI_REALTIME_DEPLOYMENT` back-compat fallback is still NOT selectable by
        this definition, so `/api/personas`'s picker (item 3) never offers a model that would
        404 if a guest explicitly asked for it by id."""
        return self.is_catalogued_for(model_id, pipeline) and self.is_deployed(model_id)

    def validate_persona_defaults(self, persona_catalog: Any) -> None:
        """Rick's PR #106 review item 1: startup fails if any enabled persona's own pipeline
        default model isn't in the catalog for that pipeline -- an unusable default should stop
        startup, not surface as a confusing 404 on the first `?model=`-omitted request. Only the
        DEFAULT is fail-fast; a non-default `allowed` id that isn't catalogued only logs a
        warning here, since it only 404s if a guest actually asks for it by id (see
        `processors.py::resolve_realtime_model`). *persona_catalog* is a `persona_loader.
        PersonaCatalog` (typed loosely here to avoid a module import cycle -- persona_loader.py
        has no reason to import this module, and this module has no reason to import it either,
        so this keeps both directions import-free)."""
        for persona_id in persona_catalog.ids:
            persona = persona_catalog.get(persona_id)
            for pipeline_name in ("realtime", "cascade"):
                pipeline_cfg = getattr(persona.manifest.models, pipeline_name, None)
                if pipeline_cfg is None:
                    continue
                if not self.is_catalogued_for(pipeline_cfg.default, pipeline_name):
                    raise ModelValidationError(
                        f"Persona {persona_id!r}'s {pipeline_name} default model "
                        f"{pipeline_cfg.default!r} is not in config.yaml's models.catalog for "
                        f"pipeline {pipeline_name!r}. Known models: {', '.join(self.ids) or '(none)'}"
                    )
                for allowed_id in pipeline_cfg.allowed:
                    if allowed_id == pipeline_cfg.default:
                        continue
                    if not self.is_catalogued_for(allowed_id, pipeline_name):
                        logger.warning(
                            "Persona %r's %s allowed model %r is not in config.yaml's "
                            "models.catalog for pipeline %r -- it will 404 if a guest ever "
                            "requests it explicitly by id.",
                            persona_id, pipeline_name, allowed_id, pipeline_name,
                        )

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
        cascade_audio = _parse_cascade_audio_config(models_cfg.get("cascade"))
        return cls(
            entries=entries,
            deployments=deployments,
            cascade_audio=cascade_audio,
        )
