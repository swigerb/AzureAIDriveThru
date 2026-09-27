# .NET backend mapping (`app/backend-dotnet`)

This document tracks how `app/backend-dotnet` (C#, .NET 11) maps to `app/backend` (Python), the
reference implementation, per ADR-001's "one contract, two backends" decision. It exists so a
change to one backend has an obvious place to look for its counterpart in the other, and so scope
deliberately deferred or reduced in the C# port is written down instead of discovered by surprise.

Wave 2 (issue #12) builds the C# **skeleton**: host, config loading, persona pack loading, health,
the auth token endpoint, a `/realtime` pre-upgrade auth gate (Origin + optional session-token
validation), static file serving, and one event loop per session. It does **not** wire up the
realtime relay, order state machine, search grounding, or per-session persona/model selection --
those are later waves (#13+, #74/#75 for wave 7's persona/model binding).

## Module mapping

| Python (`app/backend/`) | C# (`app/backend-dotnet/src/Backend/`) | Notes |
| --- | --- | --- |
| `app.py` (create_app, startup validation, route table) | `Program.cs` | Same startup order: required env vars -> persona catalog -> config -> prompts for the default persona. Fails fast (process exit code 1) on the first problem, same as Python's `sys.exit(1)`. |
| `persona_loader.py`'s `PersonaCatalog` | `Personas/PersonaCatalog.cs` | Same env vars (`PERSONAS_DIR`, `PERSONAS`, `DEFAULT_PERSONA`), same two-layer validation (JSON Schema, then a strict typed model), same fail-fast checks: missing dir, empty dir, unlisted enabled persona, id/folder mismatch, malformed JSON, missing/invalid menu file, missing prompts dir, default persona not enabled. |
| `persona_loader.py`'s Pydantic models | `Personas/PersonaModels.cs`, `Personas/MenuModels.cs` | C# records with `required` init-only properties and `[JsonPropertyName]`, deserialized with `JsonUnmappedMemberHandling.Disallow` (belt-and-suspenders against schema/model drift), matching Pydantic's `extra="forbid"`. |
| (JSON Schema validation, ad hoc in `persona_loader.py`) | `Personas/PersonaSchemaValidator.cs` | Wraps `JsonSchema.Net`; both backends validate against the exact same `personas/persona.schema.json` / `personas/menu.schema.json` files -- neither backend has its own copy. |
| `config_loader.py` | `Configuration/AppConfig.cs` | Both backends load the SAME `app/backend/config.yaml` (not duplicated). Exposes the raw parsed sections (`IReadOnlyDictionary<string, object?>`) rather than a fully strongly-typed model of every field -- later waves can bind specific sections (`audio`, `business_rules`, ...) as they need them. |
| (none yet -- design doc section 7, Python's own #75, still open) | `Models/ModelCatalog.cs` | See "Known ambiguity: `models.catalog`" below. |
| `prompt_loader.py` | `Prompts/PromptLoader.cs` | Loads/validates `system_prompt.yaml`, `greeting.yaml`, `tool_schemas.yaml`, `error_messages.yaml`, `hints.yaml` for one persona. See "Deliberate scope reductions" below for what's excluded. |
| `rtmt.py`'s `create_hmac_token` / `validate_hmac_token` | `Auth/SessionTokenService.cs` | Byte-for-byte compatible: same payload JSON spacing (`{"exp": N}`), same URL-safe base64 (padding kept), same HMAC-SHA256-as-lowercase-hex signature, same "split on the last `.`" framing, constant-time signature comparison. See spike #44. PR #96 review nit: an earlier draft lowercased the *presented* signature before comparing, silently accepting uppercase hex that Python's `hmac.compare_digest` rejects -- fixed, covered by `Validate_RejectsUppercaseSignature`. |
| `app.py`'s `load_app_secret()` | `Auth/AppSecretProvider.cs` | Reads `APP_SESSION_SECRET`; warns if short; generates a random 32-byte secret if unset (warning only when running in production). |
| `config.yaml`'s `security` section (rtmt.py's module-level `_security_cfg`) | `Configuration/SecurityConfig.cs` | Typed, tolerant view of `security.allowed_origins` (list, default `[]`) and `security.require_session_token` (bool, default `false`) -- handles the YamlDotNet string-scalar gotcha below the same way `PromptLoader.ParsePriority` does. |
| `rtmt.py`'s `_origin_matches_host` | `Realtime/OriginValidator.cs` | Exact, case-insensitive authority match only (never a suffix/substring match) -- mirrors `urllib.parse.urlsplit(origin).netloc` comparison semantics via `Uri.Authority`. |
| `rtmt.py`'s `_websocket_handler`'s pre-upgrade Origin + token checks ("Task 3"/"Task 4") | `Realtime/RealtimeAuthGate.cs` | PR #96 review, required item 1 -- see "`/realtime` auth enforcement (PR #96)" below for the full decision record. |
| (module-level `_startup_checks` dict + `/health` handler) | `Health/StartupChecks.cs`, `Health/HealthEndpoint.cs` | Same JSON shape: `{status, version, checks, personas}`, 200 if every check passed else 503. |
| (aiohttp route table's WebSocket handler + per-session state) | `Sessions/SessionActor.cs`, `Sessions/SessionRegistry.cs`, `Sessions/IPipelineProcessor.cs` | One `Channel<SessionEvent>`-backed sequential event loop per session (issue #12's "one event loop per session"), held in a shared `SessionRegistry`. `IPipelineProcessor` is an explicit, currently-unbound seam for wave 7's persona/model-specific pipeline -- the skeleton proves the actor/registry mechanics without any relay logic. |
| (repo-relative path resolution, implicit via `os.path` calls) | `RepoRootLocator.cs` | Walks up from the running assembly looking for a directory containing both `personas/` and `azure.yaml`. A dev/CI convenience only -- production containers are expected to set `PERSONAS_DIR`, `CONFIG_PATH`, and `STATIC_FILES_DIR` explicitly. |

## Deliberate scope reductions (this wave)

- **DEV_MODE hot-reload** (`prompt_loader.py`'s file-watching reload behaviour) is explicitly
  marked not required in C# by the design doc's per-backend loading table. Not ported.
- **Jinja2 template rendering** (`prompt_loader.py`'s `render_error`, `get_upsell_hint`,
  `get_delta_template`, `render_template`) is excluded this wave: nothing in the wave-2 skeleton
  (no real session/order processing yet) consumes rendered error messages or hints. `PromptLoader`
  still loads and validates `error_messages.yaml` / `hints.yaml` as raw, unrendered dictionaries so
  a broken file still fails startup; rendering is deferred to whichever wave first needs it
  (wave 3+, issue #13).
- **Non-blocking connectivity check** (`app.py`'s best-effort, log-only ping to the configured
  Azure OpenAI/Search endpoints at startup) is not ported. It does not gate `/health` in Python
  either, so its absence changes no observable behaviour this wave; revisit once real
  OpenAI/Search clients exist.
- **Per-session persona/model selection** (`?persona=`/`?model=` query params or equivalent) is
  wave 7 (#74/#75), not this wave. `PersonaCatalog.DefaultPersonaId` and `PromptLoader` are wired
  up for the *default* persona only; `/realtime` and `SessionActor` do not yet know how to bind a
  session to a specific persona or model pipeline.
- **`/api/personas` is not mapped this wave** (PR #96 review, required item 2). An earlier draft
  mapped it with the shape `{personas: [ids], defaultPersona, models}`, which diverges from the
  wire contract pinned in `docs/persona-architecture.md` section 5.2:
  `{default, personas: [{id, displayName, logoUrl, theme}], backends}`. Python defines this
  endpoint first (#74) and conformance will pin its exact shape there; this backend maps it in the
  persona-binding half of #12 (or whenever #74 lands) rather than shipping a second, divergent
  contract in the meantime. `Models/ModelCatalog.cs` is left in place, unwired -- it is still the
  right home for wave 7's model-catalog binding once #75 settles the ambiguity below.

## `/realtime` auth enforcement (PR #96)

PR #96's first draft accepted any WebSocket unconditionally. Rick's review (required item 1)
required either enforcing Python's pre-upgrade Origin + HMAC-token checks with the same rejection
statuses, or leaving `/realtime` unmapped until #13.

**Chosen: enforce both** (`Realtime/RealtimeAuthGate.cs`, wired into `Program.cs`'s `/realtime`
handler before `AcceptWebSocketAsync`). Rationale:

- **Origin check** -- unconditional in Python (`_origin_matches_host` against the Host header,
  plus an `allowed_origins` allow-list; 403 "Origin not allowed" on mismatch). The conformance
  suite already has real, over-the-wire scenarios for this
  (`Scenarios/Http/OriginValidationTests.cs` and `Scenarios/Security/OriginValidationTests.cs`),
  which run against whichever backend `CONFORMANCE_BACKEND` selects with no dotnet-specific
  filter -- so dotnet parity is provable today, not just plausible.
- **Session-token check** -- gated in Python by `config.yaml`'s `security.require_session_token`
  (default `false`); when enabled, an invalid/missing HMAC token gets 401 "Invalid or expired
  token". No conformance scenario currently flips `require_session_token=true` (neither
  `BackendEnvironment.cs` nor `DotnetBackendEnvironment.cs` override it), so this half is not
  independently provable over the wire yet. It's ported anyway, rather than skipped, because: (a)
  `SessionTokenService` already exists, so this is genuinely "a few lines" per Rick's review; (b)
  the config-gated behavior defaults to a no-op identical to Python's default, so it changes no
  observable behavior for any scenario that runs today; and (c) implementing the *same*
  config-gated logic Python has isn't new/unproven behavior, it's mirroring -- the alternative
  (enforcing Origin only) would leave the two backends' `/realtime` route with different auth
  *shapes*, not just different current defaults. `Realtime/RealtimeAuthGateTests.cs` unit-tests
  both branches directly (pure-function style, matching `Health/HealthEndpointTests.cs`); only
  the Origin half is additionally proven over the wire by conformance this wave.

## Known ambiguity: `models.catalog`

The design doc (section 7) says the shared model catalog lives in `config.yaml` under
`models.catalog`, but `config.yaml`'s actual top-level sections today are `model` (singular,
pre-existing), `vad`, `business_rules`, `cache`, `audio`, `search`, `connection`, `compression`,
`logging`, `context`, `security`, `resume`, `resilience` -- there is no top-level `models` (plural)
section yet, and Python's own issue defining this section (#75) is still open.

`Models/ModelCatalog.cs` tolerates this by checking **both** a top-level `models.catalog` and a
nested `model.catalog` (under the existing singular section), and tolerates the section being
**entirely absent** (an empty catalog is valid today -- `/api/personas`'s `models` field is `[]`
against the real `config.yaml`). Once #75 lands in Python and settles which shape wins, this class
should be revisited to match it exactly and this ambiguity note removed.

## Gotchas encountered this wave

- **YamlDotNet's untyped `Deserialize<object?>()` returns every scalar as `System.String`** --
  never `int`/`long`/`bool`, even for an unquoted YAML integer like `priority: 1`. This is easy to
  assume otherwise (many other YAML libraries type-infer scalars). It silently broke
  `PromptLoader`'s priority-based `system_prompt.yaml` section ordering: the sort predicate
  (`p is int priority`) always fell through to a default, so sections were never actually sorted
  by their declared priority. Fixed with a tolerant `ParsePriority(object?)` helper in
  `Prompts/PromptLoader.cs` that accepts `int`, `long`, and numeric strings. Checked
  `Configuration/AppConfig.cs` and `Models/ModelCatalog.cs` for the same class of bug -- both only
  do string-keyed presence/lookup checks on parsed YAML, no numeric comparisons, so neither was
  affected. Worth remembering for any future YAML-parsed numeric comparison in this codebase.
- **`/health`'s `checks.config_loaded` key** -- Python's `_startup_checks` dict
  (`app/backend/app.py`) hardcodes `"config_loaded": True` at module-definition time, with the
  comment "validated at module load by get_config()": `get_config()` runs earlier in the same
  module, so reaching the dict literal at all already proves config loaded (a failure there raises
  before the module ever finishes importing). `Health/StartupChecks.cs` mirrors this exactly:
  `config_loaded` is a fixed `true` in the dictionary initializer, not a toggle `Program.cs` sets --
  `AppConfig.Load()` throwing and returning exit code 1 before `app.Run()` is ever called is what
  actually enforces the fail-fast guarantee. (First draft of `StartupChecks` omitted this key
  entirely, which `Scenarios/Http/HealthEndpointExtendedTests.cs` caught once
  `DotnetBackendLauncher` existed to run it for real.)
- **Default port** (PR #96 review nit) -- an earlier draft defaulted `PORT` to `8765`; Python's
  `app.py` defaults to `int(os.environ.get("PORT", 8000))`. Aligned to `8000` so the two backends
  behave identically when `PORT` is unset (both still fully overridable via the `PORT` env var).

## Build/test conventions

- `app/backend-dotnet/Directory.Build.props`: `net11.0`, nullable reference types enabled,
  warnings-as-errors, code analyzers enabled, `RestorePackagesWithLockFile=true`.
- `app/backend-dotnet/Directory.Packages.props`: Central Package Management -- all package
  versions are pinned here; individual `.csproj` files reference packages without a `Version`
  attribute. All packages resolve through the configured Microsoft NuGet proxy feed only.
- `app/backend-dotnet/tests/Backend.Tests` uses `xunit.v3.mtp-off` (VSTest execution), matching
  `tests/conformance/tests/Conformance.Tests` exactly, **not** `xunit.v3.mtp-v2` -- the latter's
  `dotnet new` scaffolding writes a `"test": {"runner": "Microsoft.Testing.Platform"}` entry into
  the single repo-root `global.json`, which would silently change execution semantics for the
  existing, unrelated conformance suite. If re-scaffolding a test project in this repo, always
  diff `global.json` afterwards and revert that entry if it appears.

## Persona pack fail-fast validation (mutation-checked)

`Backend.Tests`'s `Personas/PersonaCatalogTests.cs` mirrors
`app/backend/tests/test_persona_loader.py`'s `TestMutationSchemaViolations` scenarios one for one
against a throwaway copy of the real `personas/sonic` pack (never the shared tree): missing
required top-level field, wrong type for a nested field, an extra unknown top-level field, an
extra unknown nested field (`ui.theme.light.accents`), id/folder mismatch, malformed JSON, missing
menu file, a menu schema violation, missing prompts directory, a missing schema file, an
enabled-but-undiscovered persona id, and a `DEFAULT_PERSONA` that is not enabled. Every scenario
asserts the process refuses to start (`PersonaValidationException`, uncaught out of `Program.cs`,
process exit code 1) rather than silently degrading. This was also verified once end-to-end by hand
(`dotnet run` against a mutated pack copy, confirmed non-zero exit and a `crit:`-level log line
naming the exact violation).

## Spikes #44 and #23

See the comments left on those issues directly for this wave's position. Summary:

- **#44 (session token cross-backend compatibility)**: `SessionTokenService` already implements
  the exact same algorithm as Python's `rtmt.py` (payload JSON spacing, base64url with padding,
  HMAC-SHA256-as-lowercase-hex, "split on last dot" framing), even though nothing in this wave
  requires a token minted by one backend to validate on the other. Recommend closing #44 by
  formalizing "keep the algorithm identical, cross-validation is a nice-to-have, not a
  requirement" as the answer, since it costs nothing and keeps the door open.
- **#23 (FakeSearch HTTPS)**: Not exercised this wave (no Azure Search client exists in
  `app/backend-dotnet` yet -- search integration is a later wave). Recommend, when that wave
  starts, reaching for a plain `HttpClient` REST call against the Search data-plane REST API
  rather than fighting `Azure.Search.Documents`'s HTTPS-only transport assumption against the
  conformance harness's HTTP-only `FakeSearchServer`; this is a lightweight recommendation, not a
  blocking decision, since nothing in this wave depends on it.

## Not yet covered by this wave (tracked, not forgotten)

- `DotnetBackendLauncher` (`tests/conformance/src/Conformance.Harness/DotnetBackendLauncher.cs`,
  implementing `IBackendUnderTest`) -- added this wave, wired into `BackendLauncherFactory`'s
  `CONFORMANCE_BACKEND=dotnet` branch (replacing the old always-throw placeholder). Run locally
  with `CONFORMANCE_BACKEND=dotnet dotnet test Conformance.slnx --filter "(FullyQualifiedName~HealthEndpoint|FullyQualifiedName~AuthSession|FullyQualifiedName~StaticIndexHtml|FullyQualifiedName~OriginValidationTests)&FullyQualifiedName!~Security.OriginValidationTests.Exact_origin_is_accepted"`
  (repo root needs a built frontend at `app/backend/static` -- `npm run build` in `app/frontend`
  -- for the static-file scenario). **11/11 passing** against the real C# skeleton today (up from
  6/6 before PR #96's revision):
  `HealthEndpointTests.Health_endpoint_returns_200`,
  `HealthEndpointExtendedTests.Health_endpoint_reports_version_and_per_check_breakdown`,
  `StaticIndexHtmlTests.Root_route_serves_index_html_with_cache_control_no_cache`,
  `AuthSessionTests.Auth_session_endpoint_returns_a_token`, both
  `AuthSessionTokenFormatTests` cases, all 3 of `Scenarios/Http/OriginValidationTests.cs`
  (`Exact_host_origin_is_accepted`, `Unrelated_foreign_origin_is_rejected_with_403`,
  `Lookalike_suffix_origin_is_rejected`), and 2 of 3 in
  `Scenarios/Security/OriginValidationTests.cs`
  (`Lookalike_suffix_origin_is_rejected_with_403`, `Missing_origin_is_accepted_unchanged`) -- these
  6 Origin scenarios are the over-the-wire proof backing the "`/realtime` auth enforcement" section
  above. **Deliberately excluded**:
  `Scenarios/Security/OriginValidationTests.Exact_origin_is_accepted`, which additionally asserts a
  `session.created` protocol frame arrives after the handshake -- that requires a real
  session/protocol pipeline, which is explicitly out of scope this wave (`SessionActor` doesn't
  route to any pipeline yet; wave 3+, #13) and is not part of what Rick's review asked this
  revision to prove. Not run: the rest of the ~458-scenario suite (realtime relay, session resume,
  ordering, rate-limiting, browser) -- all of that needs real relay/pipeline logic that does not
  exist until a later wave (#13+). This factory change does NOT add `dotnet` to the CI
  `conformance` matrix (`.github/workflows/conformance.yml`) -- that axis's owner is separate
  (#76) per the wave plan; CI's `conformance` job still runs `CONFORMANCE_BACKEND=python` only,
  and `DotnetPlaceholderPolicy`/its tests are left in place unchanged for whoever wires that leg
  in. (PR #96's required item 3 instead added a separate, additive `dotnet-tests` job that runs
  only `app/backend-dotnet`'s own xUnit suite -- it does not touch this conformance matrix.)
- A richer `Models/ModelCatalog.cs` shape (display name, pipeline, deployment mapping) once #75
  defines what `models.catalog` actually looks like in Python.
- Session-level persona/model binding (`SessionActor`/`/realtime` accepting a `?persona=`/`?model=`
  selection) -- wave 7.
