# .NET backend mapping (`app/backend-dotnet`)

This document tracks how `app/backend-dotnet` (C#, .NET 11) maps to `app/backend` (Python), the
reference implementation, per ADR-001's "one contract, two backends" decision. It exists so a
change to one backend has an obvious place to look for its counterpart in the other, and so scope
deliberately deferred or reduced in the C# port is written down instead of discovered by surprise.

Wave 2 (issue #12) builds the C# **skeleton**: host, config loading, persona pack loading, health,
the auth token endpoint, a `/realtime` pre-upgrade auth gate (Origin + optional session-token
validation), static file serving, and one event loop per session.

Wave 2 part 2 (issue #12 part 2, this revision) adds the **persona and model binding** and the
**persona HTTP surface**: `GET /api/personas`, `GET /api/personas/{id}`, `GET
/personas/{id}/menu.json`, `GET /personas/{id}/assets/{*assetPath}` (traversal-safe, `?v=`
content-hash immutable caching, pinned content types, `X-Content-Type-Options: nosniff`), a C#
model catalog (`models.catalog` in `config.yaml` + `AZURE_AI_MODEL_DEPLOYMENTS`), and `/realtime`'s
pre-upgrade `?persona=`/`?model=` resolution (404 before the WebSocket upgrade on an
unknown/disabled persona or an unselectable/undeployed model). It does **not** wire up the real
realtime relay, order state machine, or search grounding -- the realtime pipeline processor
(`Sessions/RealtimeProcessor.cs`) is a deliberate no-op stub this wave; the actual Azure OpenAI
realtime relay is issue #13.

## Module mapping

| Python (`app/backend/`) | C# (`app/backend-dotnet/src/Backend/`) | Notes |
| --- | --- | --- |
| `app.py` (create_app, startup validation, route table) | `Program.cs` | Same startup order: required env vars -> persona catalog -> config -> prompts for the default persona. Fails fast (process exit code 1) on the first problem, same as Python's `sys.exit(1)`. |
| `persona_loader.py`'s `PersonaCatalog` | `Personas/PersonaCatalog.cs` | Same env vars (`PERSONAS_DIR`, `PERSONAS`, `DEFAULT_PERSONA`), same two-layer validation (JSON Schema, then a strict typed model), same fail-fast checks: missing dir, empty dir, unlisted enabled persona, id/folder mismatch, malformed JSON, missing/invalid menu file, missing prompts dir, default persona not enabled. |
| `persona_loader.py`'s Pydantic models | `Personas/PersonaModels.cs`, `Personas/MenuModels.cs` | C# records with `required` init-only properties and `[JsonPropertyName]`, deserialized with `JsonUnmappedMemberHandling.Disallow` (belt-and-suspenders against schema/model drift), matching Pydantic's `extra="forbid"`. |
| (JSON Schema validation, ad hoc in `persona_loader.py`) | `Personas/PersonaSchemaValidator.cs` | Wraps `JsonSchema.Net`; both backends validate against the exact same `personas/persona.schema.json` / `personas/menu.schema.json` files -- neither backend has its own copy. |
| `config_loader.py` | `Configuration/AppConfig.cs` | Both backends load the SAME `app/backend/config.yaml` (not duplicated). Exposes the raw parsed sections (`IReadOnlyDictionary<string, object?>`) rather than a fully strongly-typed model of every field -- later waves can bind specific sections (`audio`, `business_rules`, ...) as they need them. |
| `model_catalog.py`'s `ModelCatalog` (issue #75) | `Models/ModelCatalog.cs`, `Models/ModelEntry.cs`, `Models/ResolvedModel.cs`, `Models/ModelDispatch.cs` | Parses `config.yaml`'s `models.catalog` list (`id`, `pipeline`, `label`, `reasoning`/`toolCalling`/`runtime` flags) plus `AZURE_AI_MODEL_DEPLOYMENTS` (a JSON object mapping model id -> deployment name; a catalogued model with no entry there is "not deployed"). `ModelDispatch.DispatchProcessor`/`ResolveRealtimeModel` port `processors.py`'s free functions of the same name -- see "Known ambiguity" below for the now-resolved `models.catalog` shape question. |
| `persona_loader.py`/`app.py`'s persona+asset+menu HTTP routes (issue #74, design doc section 5.2) | `Personas/PersonaRoutes.cs`, `Personas/PersonaAssetResolver.cs`, `Personas/PersonaAssetHash.cs`, `Configuration/AssetCacheConfig.cs` | `GET /api/personas`, `GET /api/personas/{id}`, `GET /personas/{id}/menu.json`, `GET /personas/{id}/assets/{*assetPath}`. `PersonaAssetResolver.Resolve` mirrors `_resolve_persona_asset_path`'s structural (not string-matching) traversal defense: segment-reject `.`/`..`/empty/drive-letter, THEN join, THEN re-verify containment after `Path.GetFullPath`. `PersonaAssetHash` mirrors `_content_hash` for the `?v=` query param; a matching `?v=` gets a year-long immutable cache header, everything else gets a short one. |
| `processors.py`'s `ProcessorRegistry` (issue #75, design doc section 7.4) | `Sessions/ProcessorRegistry.cs`, `Sessions/IPipelineProcessor.cs`, `Sessions/RealtimeProcessor.cs` | Maps a pipeline name (`realtime`\|`cascade`\|`local`) to the single `IPipelineProcessor` that owns every session bound to it. `RealtimeProcessor` is registered for `"realtime"` so `/realtime`'s persona+model resolution has a real dispatch target, but its `ProcessAsync` is a deliberate no-op stub -- the actual relay is issue #13. No processor is registered for `cascade`/`local` yet, so dispatching to either 404s at `/realtime` today (same observable shape as an unimplemented pipeline in Python). |
| `prompt_loader.py` | `Prompts/PromptLoader.cs` | Loads/validates `system_prompt.yaml`, `greeting.yaml`, `tool_schemas.yaml`, `error_messages.yaml`, `hints.yaml` for one persona. See "Deliberate scope reductions" below for what's excluded. |
| `rtmt.py`'s `create_hmac_token` / `validate_hmac_token` | `Auth/SessionTokenService.cs` | Byte-for-byte compatible: same payload JSON spacing (`{"exp": N}`), same URL-safe base64 (padding kept), same HMAC-SHA256-as-lowercase-hex signature, same "split on the last `.`" framing, constant-time signature comparison. See spike #44. PR #96 review nit: an earlier draft lowercased the *presented* signature before comparing, silently accepting uppercase hex that Python's `hmac.compare_digest` rejects -- fixed, covered by `Validate_RejectsUppercaseSignature`. |
| `app.py`'s `load_app_secret()` | `Auth/AppSecretProvider.cs` | Reads `APP_SESSION_SECRET`; warns if short; generates a random 32-byte secret if unset (warning only when running in production). |
| `config.yaml`'s `security` section (rtmt.py's module-level `_security_cfg`) | `Configuration/SecurityConfig.cs` | Typed, tolerant view of `security.allowed_origins` (list, default `[]`) and `security.require_session_token` (bool, default `false`) -- handles the YamlDotNet string-scalar gotcha below the same way `PromptLoader.ParsePriority` does. |
| `rtmt.py`'s `_origin_matches_host` | `Realtime/OriginValidator.cs` | Exact, case-insensitive authority match only (never a suffix/substring match) -- mirrors `urllib.parse.urlsplit(origin).netloc` comparison semantics via `Uri.Authority`. |
| `rtmt.py`'s `_websocket_handler`'s pre-upgrade Origin + token checks ("Task 3"/"Task 4") | `Realtime/RealtimeAuthGate.cs` | PR #96 review, required item 1 -- see "`/realtime` auth enforcement (PR #96)" below for the full decision record. |
| (module-level `_startup_checks` dict + `/health` handler) | `Health/StartupChecks.cs`, `Health/HealthEndpoint.cs` | Same JSON shape: `{status, version, checks, personas}`, 200 if every check passed else 503. |
| (aiohttp route table's WebSocket handler + per-session state) | `Sessions/SessionActor.cs`, `Sessions/SessionRegistry.cs`, `Sessions/IPipelineProcessor.cs` | One `Channel<SessionEvent>`-backed sequential event loop per session (issue #12's "one event loop per session"), held in a shared `SessionRegistry`. `IPipelineProcessor` is the persona/model-specific pipeline seam; `ProcessorRegistry` now binds `"realtime"` to `RealtimeProcessor` (a deliberate no-op stub, #13 lands the real relay) -- the actor/registry mechanics are proven end to end (persona+model resolve to a processor instance) without any relay logic yet. |
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
- **Per-session persona/model selection** (`?persona=`/`?model=` query params) was wave 7's scope
  (#74/#75) and **landed this revision** (#12 part 2): `Program.cs`'s `/realtime` handler resolves
  `?persona=` (omitted -> `DEFAULT_PERSONA`, unknown/disabled -> 404 plain text before the upgrade)
  and `?model=` (via `ModelDispatch.DispatchProcessor` + `IPipelineProcessor.ResolveModel`, same
  404-before-upgrade shape) ahead of `AcceptWebSocketAsync`, mirroring `rtmt.py`'s
  `_websocket_handler`. `SessionActor`/`RealtimeProcessor` do not yet forward to a real Azure OpenAI
  realtime session (that's issue #13) -- the resolved persona/model are bound but not yet acted on
  beyond that stub.
- **`/api/personas` is now mapped** (`Personas/PersonaRoutes.cs`), matching the wire contract in
  `docs/persona-architecture.md` section 5.2 exactly: `{default, personas: [{id, displayName,
  logoUrl, theme}], backends}`. `/api/personas/{id}` additionally includes the persona's
  selectable models (`models` field, shaped for the picker) via `ModelCatalog`.

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

## `models.catalog` (resolved this revision)

The design doc (section 7) said the shared model catalog lives in `config.yaml` under
`models.catalog`; this was ambiguous during wave 2 (PR #96) because `config.yaml` only had a
singular `model` section back then, and Python's own issue defining this section (#75) was still
open. Python's dev branch (6a71c3e) has since landed `#75`: `config.yaml` now has a top-level
`models:` section with a `catalog:` list (`id`, `pipeline`, `label`, plus `reasoning`/`toolCalling`/
`runtime` depending on pipeline), separate from the pre-existing singular `model:` section (voice/
temperature/token-limit settings, unrelated). `Models/ModelCatalog.cs` reads exactly this shape --
the "check both a singular and plural section" tolerance from the wave-2 draft has been removed now
that the real shape is pinned. Deployment status comes from `AZURE_AI_MODEL_DEPLOYMENTS` (a JSON
object env var mapping catalogued model id -> deployment name); a catalogued-but-undeployed model
still 404s the same way an uncatalogued one does, at `/realtime`'s pre-upgrade model resolution.

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
  implementing `IBackendUnderTest`) -- added in PR #96, wired into `BackendLauncherFactory`'s
  `CONFORMANCE_BACKEND=dotnet` branch. This revision (#12 part 2) additionally fixed it to forward
  `PERSONAS_DIR` from the launch contract (it already forwarded `PERSONAS`/`DEFAULT_PERSONA`, but
  was silently dropping fixture-specific persona directory overrides, mirroring the Python
  launcher's `BackendEnvironment.cs`); without that fix, every `PersonaConformanceFixtures`-derived
  fixture (test-alpha/test-beta packs) silently fell back to the real repo's default-persona-only
  `personas/` tree when run against dotnet, and any row asserting on the fixture packs failed for
  the wrong reason. Run locally with
  `CONFORMANCE_BACKEND=dotnet dotnet test Conformance.slnx --filter "Dotnet=ready"`
  (repo root needs a built frontend at `app/backend/static` -- `npm run build` in `app/frontend`
  -- for the static-file scenario). **31/31 tagged scenarios passing** against the real C# skeleton
  today (up from 11/11 before this revision; `DotnetTraitCoverageTests` enforces `count >= 11` and
  grows automatically as more scenarios get tagged). New this revision, all confirmed passing and
  tagged `[Trait("Dotnet", "ready")]`:
  - `PersonaDiscoveryConformanceTests` -- 4 of 5 methods (persona list/detail shape, 404 for an
    unknown persona id, pre-upgrade 404 for an unknown `?persona=` on `/realtime`). The 5th
    (`Omitted_persona_binds_to_the_default_persona_visible_in_session_metadata`) needs a real
    upstream `session.created` echo (`extension.session_metadata`), which only fires once the
    actual realtime relay lands (#13); left untagged.
  - `PersonaAssetRouteConformanceTests` -- full class (asset/menu 200s, immutable-vs-short cache
    headers, 404s for an unknown persona, and all 3 path-traversal `InlineData` rows).
  - `PersonaDisabledPackConformanceTests` -- full class (a disabled pack 404s on detail/asset/menu
    while an enabled pack still serves; proves the `PERSONAS_DIR` fix above).
  - `ModelSelectionRejectionConformanceTests` -- full class (pre-upgrade 404 on `/realtime` for an
    unknown model id, a model disallowed for this persona, a catalogued-but-undeployed model, and a
    model catalogued for a different pipeline).
  - `ModelSelectionConformanceTests` -- 1 of 6 methods (`/api/personas/{id}`'s selectable-models
    list for the picker, a pure HTTP GET). The other 5 need the same real-relay session-metadata
    echo as `PersonaDiscoveryConformanceTests`'s 5th method above; left untagged pending #13.

  Carried over from PR #96 (unchanged, still tagged): `HealthEndpointTests`,
  `HealthEndpointExtendedTests`, `StaticIndexHtmlTests`, `AuthSessionTests`,
  `AuthSessionTokenFormatTests` (both cases), all 3 of `Scenarios/Http/OriginValidationTests.cs`,
  and 2 of 3 in `Scenarios/Security/OriginValidationTests.cs` -- see PR #96's own notes for why
  `Exact_origin_is_accepted` (which asserts a `session.created` frame) stays untagged. Still not
  run: the rest of the conformance suite (real-relay session content, order state machine,
  rate-limiting, browser scenarios) -- all of that needs the real relay/pipeline logic that lands in
  #13+. This wave does NOT add `dotnet` to the CI `conformance` matrix
  (`.github/workflows/conformance.yml`) -- that axis's owner is separate (#76); CI's `conformance`
  job still runs `CONFORMANCE_BACKEND=python` only. The additive `dotnet-tests` job (PR #96) keeps
  running `app/backend-dotnet`'s own xUnit suite independently.
- Session-level persona/model binding (`SessionActor`/`/realtime` accepting a `?persona=`/`?model=`
  selection) **landed this revision** (#12 part 2) as far as pre-upgrade resolution/rejection goes.
  Still not covered: actually forwarding the resolved persona/model into a live Azure OpenAI
  realtime session (`RealtimeProcessor.ProcessAsync` is a no-op stub) -- issue #13.

## Traversal defense mutation-test note (this revision)

Verified `PersonaAssetResolver`'s containment logic is real, not just coincidentally passing,
by temporarily reintroducing two bugs locally (never committed): dropping the segment-level `".."`
rejection, and making the post-`GetFullPath` containment check (`IsUnderRoot`) unconditionally
`true`. Two direct unit tests in `Backend.Tests/Personas/PersonaAssetResolverTests.cs` (added this
revision) then failed as expected (a live out-of-root path was returned instead of `null`), while a
same-class "real in-root asset still resolves" test kept passing -- confirming the mutation broke
only the intended defense. Reverted both bugs before committing.

Notably, this could **not** be observed through the pinned HTTP-level conformance row
(`PersonaAssetRouteConformanceTests.Persona_asset_route_rejects_path_traversal_attempts`'s three
`InlineData` payloads): with both bugs applied, all nine of that test's HTTP assertions still
passed, because each payload is independently neutralized before `PersonaAssetResolver.Resolve`
ever runs -- `HttpClient` normalizes a literal `"../"` client-side before the request leaves the
process, and ASP.NET Core's routing never decodes a `"%2f"` in a route segment into a real path
separator (so an encoded-slash payload arrives as one opaque segment, never a literal `".."`
segment). That's genuine defence-in-depth working as intended, but it also means the resolver's own
logic needed a direct unit test to be provably load-bearing -- hence the new test file above.

An analogous mutation on persona acceptance (temporarily always-accept in `Program.cs`'s
`/realtime` handler, bypassing `personaCatalog.Contains(personaId)`) **is** caught by the pinned
conformance suite:
`PersonaDiscoveryConformanceTests.Realtime_with_an_unknown_persona_is_rejected_with_404_before_the_websocket_opens`
failed (expected 404, observed 500 from the now-unguarded `PersonaCatalog.Get` throwing
`KeyNotFoundException`) with the mutation applied, and passed again once reverted.
