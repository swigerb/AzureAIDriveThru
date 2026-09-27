"""Persona pack loader for Sonic AI Drive-Thru (issue #70, ADR-001 / design doc section 4.4).

Discovers, parses and validates every enabled persona pack under ``PERSONAS_DIR`` at process
startup. A pack is ``personas/<id>/persona.json`` plus its ``prompts/``, ``menu/`` and ``assets/``
subfolders (design doc section 4.1). Validation is fail-fast and two-layered, exactly matching the
design's "Parse and validate" row:

1. The raw JSON is checked against the shared JSON Schemas (``personas/persona.schema.json`` and
   ``personas/menu.schema.json``) so the same contract both backends and CI use is enforced here
   too, not just in CI.
2. The validated JSON is then loaded into ``pydantic`` models with ``extra="forbid"``, so an
   unknown field is rejected even if a future schema edit accidentally widens the JSON Schema.

Any invalid enabled pack raises ``PersonaValidationError`` naming the persona id, the offending
file, and the field path -- never a partial/degraded startup (ADR-001 decision 2, "fail fast").

This module intentionally does NOT change any ordering, pricing, or prompt *behavior* yet (#70 is
"skeleton and loader, Sonic only, no behavior change"). ``PromptLoader`` and ``menu_utils`` read
the pack's ``prompts/`` and ``menu/`` files, but the pack's *rule* blocks (``sizes``, ``extras``,
``pricing``, ...) are validated and exposed for later waves (#71/#73/#74) to wire in -- they are
not yet consulted by ``tools.py``/``order_state.py``.

Usage:
    from persona_loader import PersonaCatalog

    catalog = PersonaCatalog.load()          # env-driven: PERSONAS_DIR, PERSONAS, DEFAULT_PERSONA
    sonic = catalog.get("sonic")
    sonic.prompts_dir                        # .../personas/sonic/prompts
    sonic.menu_path                          # .../personas/sonic/menu/menuItems.json
"""

from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any

import jsonschema
from pydantic import BaseModel, ConfigDict, Field, ValidationError

import menu_utils

__all__ = [
    "PersonaCatalog",
    "Persona",
    "PersonaValidationError",
]

logger = logging.getLogger("persona-loader")

# personas/ sits at the repo root (design doc section 4.1): app/backend/persona_loader.py is two
# levels below it (app/backend/ -> app/ -> repo root).
_REPO_ROOT = Path(__file__).resolve().parents[2]
_DEFAULT_PERSONAS_DIR = _REPO_ROOT / "personas"


class PersonaValidationError(Exception):
    """Raised when an enabled persona pack fails schema or model validation.

    Always names the persona id, the file, and (when available) the offending field path, so a
    broken pack is fixable from the error message alone -- never a silent partial load.
    """


# ---------------------------------------------------------------------------
# Pydantic models mirroring personas/persona.schema.json (extra="forbid" everywhere, per
# ADR-001 decision 2 / design doc section 4.4 "Parse and validate").
# ---------------------------------------------------------------------------


class _Locales(BaseModel):
    model_config = ConfigDict(extra="forbid")
    default: str
    supported: list[str]


class _Store(BaseModel):
    model_config = ConfigDict(extra="forbid")
    timezone: str


class _Voice(BaseModel):
    model_config = ConfigDict(extra="forbid")
    default: str


class _Search(BaseModel):
    model_config = ConfigDict(extra="forbid")
    indexName: str
    contentFields: list[str]


class _HappyHour(BaseModel):
    model_config = ConfigDict(extra="forbid")
    startHour: int
    endHour: int
    priceMultiplier: str
    announce: bool
    banner: str


class _Pricing(BaseModel):
    model_config = ConfigDict(extra="forbid")
    taxRate: str
    happyHour: _HappyHour | None


class _Sizes(BaseModel):
    model_config = ConfigDict(extra="forbid")
    canonical: dict[str, str]
    aliases: dict[str, str]
    spokenAs: dict[str, str]
    hidden: list[str]
    default: str | None


class _Bundles(BaseModel):
    model_config = ConfigDict(extra="forbid")
    nameMarkers: list[str]
    convertStandalone: bool
    missingPartText: dict[str, str]


class _Extras(BaseModel):
    model_config = ConfigDict(extra="forbid")
    allowedBaseCategories: list[str]
    blockedBaseCategories: list[str]
    splitCombinedNames: bool


class _Machine(BaseModel):
    """#77: a single machine's own status + guest-facing OOS label, straight from the pack.

    Replaces the old bare ``{"soda_machine": "down"}`` string-status map -- the label used to be
    a Python-side, name-keyed dict (``tools.py``'s ``_MACHINE_OOS_LABELS``) that only one pack's
    two machine keys ever populated, so a second pack's machine silently degraded to a generic
    "<key> is down" string. Every persona now owns its own label for its own machines."""

    model_config = ConfigDict(extra="forbid")
    status: str
    label: str


class _ModelPipeline(BaseModel):
    model_config = ConfigDict(extra="forbid")
    default: str
    allowed: list[str]


class _Models(BaseModel):
    model_config = ConfigDict(extra="forbid")
    realtime: _ModelPipeline
    cascade: _ModelPipeline | None = None
    local: _ModelPipeline | None = None


class _Strategies(BaseModel):
    model_config = ConfigDict(extra="forbid")
    searchQueryRewrite: str


class _Features(BaseModel):
    model_config = ConfigDict(extra="forbid")
    dayparts: bool


class _ThemeAccents(BaseModel):
    """Optional extended brand accent palette (issue #80 F2, personaTheme.ts::PersonaAccentPalette).

    Role-named, not brand-specific, and every key is optional: a pack may omit the block entirely,
    provide the full light-mode palette, or override only a subset for dark mode.
    """

    model_config = ConfigDict(extra="forbid")
    primaryHex: str | None = None
    primaryStrong: str | None = None
    primaryLight: str | None = None
    primaryTintOnDark: str | None = None
    secondaryHex: str | None = None
    secondaryStrong: str | None = None
    secondaryTintOnDark: str | None = None
    accent: str | None = None
    accentLight: str | None = None
    ink: str | None = None
    surfaceTint: str | None = None
    surfaceDark: str | None = None
    surfaceDarkAlt: str | None = None
    success: str | None = None
    neutral: str | None = None


class _ThemeTokens(BaseModel):
    model_config = ConfigDict(extra="forbid")
    primary: str | None = None
    secondary: str | None = None
    background: str | None = None
    foreground: str | None = None
    accents: _ThemeAccents | None = None


class _Font(BaseModel):
    model_config = ConfigDict(extra="forbid")
    family: str
    importUrl: str | None = None


class _Theme(BaseModel):
    model_config = ConfigDict(extra="forbid")
    light: _ThemeTokens
    dark: _ThemeTokens | None = None
    font: _Font | None = None


class _UiAssets(BaseModel):
    model_config = ConfigDict(extra="forbid")
    logo: str
    favicon: str
    apologyClip: str | None = None


class _Hero(BaseModel):
    model_config = ConfigDict(extra="forbid")
    headline: str
    callouts: list[str] = Field(default_factory=list)


class _Ui(BaseModel):
    model_config = ConfigDict(extra="forbid")
    title: str
    theme: _Theme
    assets: _UiAssets
    strings: dict[str, dict[str, str]]
    hero: _Hero
    legal: str


class PersonaManifest(BaseModel):
    """Typed view of ``persona.json`` -- the same contract as ``personas/persona.schema.json``."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: int
    id: str
    displayName: str
    roleName: str
    locales: _Locales
    store: _Store
    voice: _Voice
    search: _Search
    pricing: _Pricing
    sizes: _Sizes
    bundles: _Bundles
    extras: _Extras
    invalidModifiers: dict[str, list[str]]
    machines: dict[str, _Machine]
    models: _Models
    strategies: _Strategies
    features: _Features
    ui: _Ui


class Persona:
    """A loaded, validated persona pack: the parsed manifest plus resolved on-disk paths."""

    def __init__(self, persona_id: str, pack_dir: Path, manifest: PersonaManifest):
        self.id = persona_id
        self.pack_dir = pack_dir
        self.manifest = manifest

    @property
    def prompts_dir(self) -> Path:
        return self.pack_dir / "prompts"

    @property
    def menu_path(self) -> Path:
        return self.pack_dir / "menu" / "menuItems.json"

    @property
    def assets_dir(self) -> Path:
        return self.pack_dir / "assets"


# ---------------------------------------------------------------------------
# PersonaCatalog: discovery + fail-fast validation across every enabled pack
# ---------------------------------------------------------------------------


class PersonaCatalog:
    """Loads and validates every enabled persona pack. Construct via :meth:`load`."""

    def __init__(self, personas_dir: Path, default_persona_id: str, personas: dict[str, Persona]):
        self.personas_dir = personas_dir
        self.default_persona_id = default_persona_id
        self._personas = personas

    @property
    def ids(self) -> list[str]:
        """Persona ids, sorted, for a stable ``/health`` response."""
        return sorted(self._personas.keys())

    def get(self, persona_id: str) -> Persona:
        try:
            return self._personas[persona_id]
        except KeyError:
            raise KeyError(
                f"Persona '{persona_id}' is not enabled. Enabled personas: {', '.join(self.ids) or '(none)'}"
            ) from None

    def __contains__(self, persona_id: str) -> bool:
        return persona_id in self._personas

    @classmethod
    def load(
        cls,
        *,
        personas_dir: Path | str | None = None,
        enabled: list[str] | None = None,
        default_persona_id: str | None = None,
    ) -> PersonaCatalog:
        """Discover, parse and validate every enabled persona pack.

        Env vars (design doc section 4.4), each overridable by an explicit argument (used by
        tests): ``PERSONAS_DIR`` (default ``<repo>/personas``, ``/app/personas`` in the
        container), ``PERSONAS`` (comma list; default: every folder under ``PERSONAS_DIR`` that
        has a ``persona.json``), ``DEFAULT_PERSONA`` (must be one of the enabled ids; defaults to
        ``sonic`` when enabled, else the first enabled id alphabetically).

        Raises ``PersonaValidationError`` naming the pack, file and field on the first invalid
        enabled pack -- this is the "refuse to start on an invalid pack" contract.
        """
        base_dir = Path(personas_dir) if personas_dir is not None else Path(
            os.environ.get("PERSONAS_DIR") or _DEFAULT_PERSONAS_DIR
        )
        base_dir = base_dir.resolve()

        if not base_dir.is_dir():
            raise PersonaValidationError(
                f"PERSONAS_DIR '{base_dir}' does not exist or is not a directory."
            )

        persona_schema = _load_json_schema(base_dir / "persona.schema.json")
        menu_schema = _load_json_schema(base_dir / "menu.schema.json")

        if enabled is not None:
            enabled_ids = list(enabled)
        else:
            env_value = os.environ.get("PERSONAS")
            if env_value:
                enabled_ids = [p.strip() for p in env_value.split(",") if p.strip()]
            else:
                enabled_ids = sorted(
                    p.name for p in base_dir.iterdir() if p.is_dir() and (p / "persona.json").is_file()
                )

        if not enabled_ids:
            raise PersonaValidationError(
                f"No personas enabled. Checked PERSONAS_DIR '{base_dir}' — expected at least one "
                "persona folder with a persona.json, or an explicit PERSONAS env var."
            )

        personas: dict[str, Persona] = {}
        for persona_id in enabled_ids:
            personas[persona_id] = _load_one_persona(base_dir, persona_id, persona_schema, menu_schema)

        default_id = default_persona_id or os.environ.get("DEFAULT_PERSONA")
        if default_id is None:
            default_id = "sonic" if "sonic" in personas else sorted(personas)[0]

        if default_id not in personas:
            raise PersonaValidationError(
                f"DEFAULT_PERSONA '{default_id}' is not in the enabled persona list "
                f"({', '.join(sorted(personas)) or '(none)'})."
            )

        logger.info(
            "Loaded %d persona pack(s) from %s: %s (default=%s)",
            len(personas),
            base_dir,
            ", ".join(sorted(personas)),
            default_id,
        )

        return cls(personas_dir=base_dir, default_persona_id=default_id, personas=personas)


def _load_json_schema(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise PersonaValidationError(f"Required schema file not found: {path}")
    try:
        with path.open("r", encoding="utf-8") as f:
            return json.load(f)
    except json.JSONDecodeError as exc:
        raise PersonaValidationError(f"Malformed JSON in schema file {path}: {exc}") from exc


def _load_one_persona(
    base_dir: Path,
    persona_id: str,
    persona_schema: dict[str, Any],
    menu_schema: dict[str, Any],
) -> Persona:
    pack_dir = base_dir / persona_id
    manifest_path = pack_dir / "persona.json"

    if not manifest_path.is_file():
        raise PersonaValidationError(
            f"Persona '{persona_id}' is enabled but {manifest_path} does not exist."
        )

    raw = _load_json_file(manifest_path)

    # Layer 1: validate against the shared JSON Schema (the same contract CI and a future C#
    # backend validate against -- design doc section 4.4).
    try:
        jsonschema.validate(raw, persona_schema)
    except jsonschema.exceptions.ValidationError as exc:
        field_path = "/".join(str(p) for p in exc.absolute_path) or "(root)"
        raise PersonaValidationError(
            f"Persona '{persona_id}': {manifest_path} failed schema validation "
            f"at field '{field_path}': {exc.message}"
        ) from exc

    # Layer 2: parse into the typed, extra="forbid" pydantic model (belt-and-suspenders against a
    # schema/model drift, and gives typed access to later waves). "$schema" is an optional,
    # purely-editor-hint property (JSON Schema self-reference) allowed by persona.schema.json but
    # not part of the typed contract, so it is dropped before model validation.
    model_input = {k: v for k, v in raw.items() if k != "$schema"}
    try:
        manifest = PersonaManifest.model_validate(model_input)
    except ValidationError as exc:
        field_path = ".".join(str(p) for p in exc.errors()[0]["loc"]) if exc.errors() else "(root)"
        first_error = exc.errors()[0]["msg"] if exc.errors() else str(exc)
        raise PersonaValidationError(
            f"Persona '{persona_id}': {manifest_path} failed model validation "
            f"at field '{field_path}': {first_error}"
        ) from exc

    if manifest.id != persona_id:
        raise PersonaValidationError(
            f"Persona '{persona_id}': {manifest_path} declares id '{manifest.id}', which does not "
            f"match its folder name '{persona_id}'."
        )

    # Validate the pack's menu file too (menu/menuItems.json), so a broken menu also fails startup
    # rather than surfacing later as a runtime KeyError.
    menu_path = pack_dir / "menu" / "menuItems.json"
    if not menu_path.is_file():
        raise PersonaValidationError(
            f"Persona '{persona_id}': menu file not found at {menu_path}."
        )
    menu_raw = _load_json_file(menu_path)
    try:
        jsonschema.validate(menu_raw, menu_schema)
    except jsonschema.exceptions.ValidationError as exc:
        field_path = "/".join(str(p) for p in exc.absolute_path) or "(root)"
        raise PersonaValidationError(
            f"Persona '{persona_id}': {menu_path} failed schema validation "
            f"at field '{field_path}': {exc.message}"
        ) from exc

    # #128: fail fast if two menu items normalize to the same lookup key, or an alias collides
    # with another item's own key or another item's own alias -- see
    # menu_utils.validate_menu_key_collisions's own doc comment for the exact rule (design doc
    # section 6). Runs here, eagerly, for every ENABLED persona at process startup -- the same
    # fail-fast guarantee as the schema/model checks above, not just lazily on first session bind
    # (menu_utils._load_menu_data calls this same function again when a MenuCatalog is actually
    # built, so the rule holds no matter which path constructs a persona's catalog first).
    try:
        menu_utils.validate_menu_key_collisions(menu_raw, persona_id, menu_path)
    except menu_utils.MenuKeyCollisionError as exc:
        raise PersonaValidationError(str(exc)) from exc

    prompts_dir = pack_dir / "prompts"
    if not prompts_dir.is_dir():
        raise PersonaValidationError(
            f"Persona '{persona_id}': prompts directory not found at {prompts_dir}."
        )

    return Persona(persona_id=persona_id, pack_dir=pack_dir, manifest=manifest)


def _load_json_file(path: Path) -> Any:
    try:
        with path.open("r", encoding="utf-8") as f:
            return json.load(f)
    except json.JSONDecodeError as exc:
        raise PersonaValidationError(f"Malformed JSON in {path}: {exc}") from exc
