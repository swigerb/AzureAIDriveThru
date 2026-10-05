# .NET backend mapping (`app/backend-dotnet`)

This document tracks how `app/backend-dotnet` (C#, .NET 11) maps to `app/backend` (Python), the
reference implementation, per ADR-001's "one contract, two backends" decision. It exists so a
change to one backend has an obvious place to look for its counterpart in the other, and so scope
deliberately deferred or reduced in the C# port is written down instead of discovered by surprise.

The C# backend now has the host/config/persona foundation, persona HTTP surface, model catalog,
pre-upgrade `/realtime` auth and persona/model/mode binding, the Azure OpenAI realtime relay,
order engine, search tool, prompt rendering, tool dispatch, the full rate-limit retry ladder, the
consecutive tool-failure cap, session resume/rehydration/idle-timeout/grace-hold/nudge (issue #15),
context-window monitoring, websocket heartbeat/connect-timeout, the best-effort startup
connectivity check, ADR-002 Entra JwtBearer auth (issue #147), and the shared conformance dotnet
leg. Guest/assistant turn recording (`SessionManager.RecordTurn`) has real production call sites
(upstream `conversation.item.input_audio_transcription.completed` for the guest, a non-tool
`response.done` for the assistant), feeding rehydration text on resume. No deliberate
middle-tier gap versus Python remains; see the deferred list below for what is still out of scope.

## Module mapping

| Python (`app/backend/`) | C# (`app/backend-dotnet/src/Backend/`) | Notes |
| --- | --- | --- |
| `app.py` (create_app, startup validation, route table) | `Program.cs` | Same startup order: required env vars -> persona catalog -> config -> prompts for the default persona. Fails fast (process exit code 1) on the first problem, same as Python's `sys.exit(1)`. |
| `app.py`'s `_check_service_connectivity()` | `Program.cs`'s `CheckServiceConnectivityAsync` | Issue #13 tail: best-effort GET pings to `AZURE_OPENAI_EASTUS2_ENDPOINT`/`AZURE_SEARCH_ENDPOINT` with a 5s total timeout, called right after `prompts_loaded` (step 4, optional). Logs only -- never gates `/health` or fails startup in either backend, same double try/catch shape (outer around the whole check, inner per-endpoint) as Python. |
| `persona_loader.py`'s `PersonaCatalog` | `Personas/PersonaCatalog.cs` | Same env vars (`PERSONAS_DIR`, `PERSONAS`, `DEFAULT_PERSONA`), same two-layer validation (JSON Schema, then a strict typed model), same fail-fast checks: missing dir, empty dir, unlisted enabled persona, id/folder mismatch, malformed JSON, missing/invalid menu file, missing prompts dir, default persona not enabled. |
| `persona_loader.py`'s Pydantic models | `Personas/PersonaModels.cs`, `Personas/MenuModels.cs` | C# records with `required` init-only properties and `[JsonPropertyName]`, deserialized with `JsonUnmappedMemberHandling.Disallow` (belt-and-suspenders against schema/model drift), matching Pydantic's `extra="forbid"`. |
| (JSON Schema validation, ad hoc in `persona_loader.py`) | `Personas/PersonaSchemaValidator.cs` | Wraps `JsonSchema.Net`; both backends validate against the exact same `personas/persona.schema.json` / `personas/menu.schema.json` files -- neither backend has its own copy. |
| `config_loader.py` | `Configuration/AppConfig.cs` | Both backends load the SAME `app/backend/config.yaml` (not duplicated). Exposes the raw parsed sections (`IReadOnlyDictionary<string, object?>`) rather than a fully strongly-typed model of every field -- later waves can bind specific sections (`audio`, `business_rules`, ...) as they need them. |
| `model_catalog.py`'s `ModelCatalog` (issue #75) | `Models/ModelCatalog.cs`, `Models/ModelEntry.cs`, `Models/ResolvedModel.cs`, `Models/ModelDispatch.cs` | Parses `config.yaml`'s `models.catalog` list (`id`, `pipeline`, `label`, `reasoning`/`toolCalling`/`runtime` flags) plus `AZURE_AI_MODEL_DEPLOYMENTS` (a JSON object mapping model id -> deployment name; a catalogued model with no entry there is "not deployed"). `ModelDispatch.DispatchProcessor`/`ResolveRealtimeModel` port `processors.py`'s free functions of the same name -- see "Known ambiguity" below for the now-resolved `models.catalog` shape question. |
| `persona_loader.py`/`app.py`'s persona+asset+menu HTTP routes (issue #74, design doc section 5.2) | `Personas/PersonaRoutes.cs`, `Personas/PersonaAssetResolver.cs`, `Personas/PersonaAssetHash.cs`, `Configuration/AssetCacheConfig.cs` | `GET /api/personas`, `GET /api/personas/{id}`, `GET /personas/{id}/menu.json`, `GET /personas/{id}/assets/{*assetPath}`. `PersonaAssetResolver.Resolve` mirrors `_resolve_persona_asset_path`'s structural (not string-matching) traversal defense: segment-reject `.`/`..`/empty/drive-letter, THEN join, THEN re-verify containment after `Path.GetFullPath`. Rick's PR #122 review item 3: containment is walked one path component at a time (`ResolveRealPath`), resolving any symlink encountered at ANY component -- not just the final leaf -- and re-checking it stays under the pack's assets root after each hop, same as Python's `os.path.realpath` on the full joined path; see "Symlink containment mutation-test note" below. `PersonaAssetHash` mirrors `_content_hash` for the `?v=` query param; a matching `?v=` gets a year-long immutable cache header, everything else gets a short one. |
| `processors.py`'s `ProcessorRegistry` (issue #75, design doc section 7.4) | `Sessions/ProcessorRegistry.cs`, `Sessions/IPipelineProcessor.cs`, `Sessions/RealtimeProcessor.cs` | Maps a pipeline name (`realtime`\|`cascade`) to the single `IPipelineProcessor` that owns every session bound to it. (The `local` pipeline existed here until #155 dropped it entirely, 2026-09-28 -- Microsoft Foundry only from that point on.) `RealtimeProcessor` is registered for `"realtime"` and owns the bidirectional Azure OpenAI Realtime GA relay directly through `RunSessionAsync`; its mailbox-shaped `ProcessAsync` remains a no-op because `Program.cs` hands accepted WebSockets straight to the relay. No processor is registered for `cascade` yet, so dispatching to it 404s at `/realtime` today (same observable shape as an unimplemented pipeline in Python). |
| `prompt_loader.py` | `Prompts/PromptLoader.cs` | Loads/validates `system_prompt.yaml`, `greeting.yaml`, `tool_schemas.yaml`, `error_messages.yaml`, `hints.yaml` for one persona. See "Deliberate scope reductions" below for what's excluded. |
| `rtmt.py`'s `create_hmac_token` / `validate_hmac_token` | `Auth/SessionTokenService.cs` | Byte-for-byte compatible: same payload JSON spacing (`{"exp": N}`), same URL-safe base64 (padding kept), same HMAC-SHA256-as-lowercase-hex signature, same "split on the last `.`" framing, constant-time signature comparison. See spike #44. PR #96 review nit: an earlier draft lowercased the *presented* signature before comparing, silently accepting uppercase hex that Python's `hmac.compare_digest` rejects -- fixed, covered by `Validate_RejectsUppercaseSignature`. |
| `app.py`'s `load_app_secret()` | `Auth/AppSecretProvider.cs` | Reads `APP_SESSION_SECRET`; warns if short; generates a random 32-byte secret if unset (warning only when running in production). |
| `config.yaml`'s `security` section (rtmt.py's module-level `_security_cfg`) | `Configuration/SecurityConfig.cs` | Typed, tolerant view of `security.allowed_origins` (list, default `[]`) and `security.require_session_token` (bool, default `false`) -- handles the YamlDotNet string-scalar gotcha below the same way `PromptLoader.ParsePriority` does. |
| `entra_auth.py`'s `resolve_settings` (ADR-002, issue #147) | `Auth/EntraSettings.cs` | Same `AUTH_MODE` handling (`Entra`\|`Development`, case-insensitive, default `Development`) and the same fail-fast startup validation in Entra mode: `ENTRA_TENANT_ID`/`ENTRA_CLIENT_ID` required and not a placeholder GUID, `ENTRA_INSTANCE` must be `https` unless it's a loopback address (test/dev only). See "Issue #147 (ADR-002)" below for the full decision record, including the one deliberate `RUNNING_IN_PRODUCTION` vs `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` naming deviation. |
| `entra_auth.py`'s deny-by-default middleware + `TokenValidator` (ADR-002, issue #147) | `Auth/EntraAuthentication.cs` | `AddEntraAuthentication` wires ASP.NET Core's `Microsoft.AspNetCore.Authentication.JwtBearer` (issuer/audience/signing-keys-via-OIDC-metadata/lifetime validation) plus one `EntraAccessRequirement`/`EntraAccessRequirementHandler` authorization requirement (role + scope, since JwtBearer's own validation doesn't express either) as `AddAuthorization`'s `FallbackPolicy` -- i.e. every route is auth-required by default, same as Python's deny-by-default middleware, unless `.AllowAnonymous()` (health, persona assets matching the same extension allow-list, static files, `/`). `?access_token=` is read from the query string only for the `/realtime` path (`JwtBearerEvents.OnMessageReceived`), matching ADR-002's WebSocket-can't-set-headers carve-out. |
| `rtmt.py`'s `_origin_matches_host` | `Realtime/OriginValidator.cs` | Exact, case-insensitive match only (never a suffix/substring match) of the raw `netloc` exactly as `urllib.parse.urlsplit(origin).netloc` would extract it -- preserving any userinfo prefix and an explicit port even when it equals the scheme's own default. PR #230 round-2 review (Rick's item 3): an earlier version compared `Uri.Authority`, which silently drops BOTH of those, over-permissively accepting an Origin Python rejects; fixed by a manual scheme-prefix-then-`//`-prefix netloc extraction (see the class's own doc comment), covered by new unit tests (`OriginValidatorTests`) and new tagged conformance rows (`OriginValidationTests.Origin_with_userinfo_is_rejected_with_403`, `.Origin_with_explicit_default_port_is_rejected_against_a_portless_host`). |
| `rtmt.py`'s `_websocket_handler`'s pre-upgrade Origin + token checks ("Task 3"/"Task 4") | `Realtime/RealtimeAuthGate.cs` | PR #96 review, required item 1 -- see "`/realtime` auth enforcement (PR #96)" below for the full decision record. |
| (module-level `_startup_checks` dict + `/health` handler) | `Health/StartupChecks.cs`, `Health/HealthEndpoint.cs` | Same JSON shape: `{status, version, checks, personas}`, 200 if every check passed else 503. |
| (aiohttp route table's WebSocket handler + per-session state) | `Sessions/RealtimeProcessor.cs`, `Sessions/SessionActor.cs`, `Sessions/SessionRegistry.cs`, `Sessions/SessionManager.cs`, `Realtime/NudgeScheduler.cs`, `Sessions/IPipelineProcessor.cs` | `RealtimeProcessor.RunSessionAsync` owns the accepted WebSocket and upstream relay for the `realtime` pipeline: bootstrap, greeting gate, bidirectional frame loops, echo suppression/barge-in, session echoes, round-trip tokens and tool calls. `SessionManager` (issue #15) owns resume-id issuance/consumption, the grace-window detach hold, idle-timeout close (4000), and 4002 supersede; `NudgeScheduler` owns the silent-guest nudge, armed only after the resumed socket's own `session.update` per #181. Both are driven by the injected `TimeProvider`. |
| `session_manager.py`'s `ContextMonitor` | `Sessions/ContextMonitor.cs` | Issue #13 tail: 1:1 port of the char-based token estimator (~4 chars/token) and warning/critical threshold logging. One instance per session, owned by `SessionManager` (`CreateSession`/`CreateContextMonitor`, removed on session end/`RemoveContextMonitor`); `RealtimeProcessor` tracks all 6 non-verbose-gated `ctx_monitor.add_content` call sites from `rtmt.py` (session.update instructions/tools, tool call args/result, response output text/transcript, greeting, resume-nudge, rehydration text) and `CascadeProcessor` tracks its 1 call site (tool call args/result) from `cascade_processor.py`. The 7th Python call site (verbose-only user-transcript tracking) is intentionally not ported -- see `RealtimeProcessor`'s `conversation.item.input_audio_transcription.completed` case comment for why that matches Python's own default (verbose-off) behaviour exactly. Config via `Configuration/SessionsConfig.cs`'s `Context*` fields (config.yaml's `context` section); unit-tested in `ContextMonitorTests.cs` (ported from `test_rtmt.py`'s `ContextMonitorTests`). |
| `rtmt.py`'s/`cascade_processor.py`'s shared `connection:` config reads (`_WS_HEARTBEAT_SEC`, `_WS_COMPRESS`, `_WS_CONNECT_TIMEOUT`) | `Configuration/ConnectionConfig.cs` | Issue #13 tail: `ClientWebSocket.Options.KeepAliveInterval` (upstream, `RealtimeProcessor`) and `WebSocketOptions.KeepAliveInterval` (browser-facing, `Program.cs`'s shared `app.UseWebSockets(...)` -- covers both the realtime and cascade pipelines, since both are accepted through the same `/realtime` handler) are wired from `ws_heartbeat_seconds`. Rick's #280 review, item 3: aiohttp's two-phase `ClientTimeout(total=, connect=)` IS now fully matched -- `ws_connect_timeout_total` still bounds the whole upstream `ConnectAsync` call via an outer `CancellationTokenSource`, and `ws_connect_timeout_connect` is wired into `SocketsHttpHandler.ConnectTimeout` on the `HttpMessageInvoker` passed to `ClientWebSocket.ConnectAsync(Uri, HttpMessageInvoker, CancellationToken)`, which bounds the TCP/TLS connect sub-phase the same way aiohttp's `connect=` does. `ws_compression` is parsed for config-shape parity but is a no-op in both backends (Azure OpenAI declines upstream compression regardless, and Kestrel's WebSocket middleware has no permessage-deflate knob to wire it to). Unit-tested in `ConnectionConfigTests.cs` (config parsing) and `UpstreamConnectTimeoutTests.cs` (behavioural: a stalled connect against a network black hole fails at the short `connect` bound, not the long `total` one). |
| (repo-relative path resolution, implicit via `os.path` calls) | `RepoRootLocator.cs` | Walks up from the running assembly looking for a directory containing both `personas/` and `azure.yaml`. A dev/CI convenience only -- production containers are expected to set `PERSONAS_DIR`, `CONFIG_PATH`, and `STATIC_FILES_DIR` explicitly. |
| `money_utils.py` | `Ordering/Money.cs` | Same `decimal`-based rounding (`ROUND_HALF_UP` equivalent) and `$X.XX` formatting; ports `format_money` 1:1 (see `Ordering/MoneyTests.cs`). |
| `menu_utils.py`'s `MenuCatalog` (`_menu_key`/`strip_modifiers`, size/alias/category maps, machine status, happy-hour eligibility, combo-slot inference) | `Personas/MenuCatalog.cs` | One instance built once per persona and cached (`PersonaOrderFactory.GetMenuCatalog`), same data-driven-only classification contract as #73/#74 (no keyword fallback). `MenuKeyValidator.MenuKey`/`StripModifiers` (issue #128/#137) is the exact `_menu_key`/`strip_modifiers` port; both backends now assert against the SAME shared golden vector file -- see "Shared menu-key golden vectors (#137)" below. |
| `order_state.py`'s `OrderState`/`OrderSummary` (add/remove/modify, bundle autoFill/absorption, extras engine, happy-hour pricing, tax, totals) | `Ordering/OrderState.cs`, `Ordering/OrderModels.cs`, `Ordering/OrderSummaryJson.cs` | One instance per session (confined to that session's own actor, never shared), built via `PersonaOrderFactory.CreateOrderState`. Happy-hour discount is applied multiplicatively at `UpdateSummary()` time, never baked into `item.Price` -- matches Python's own "raw menu price stays the record of truth" design. `HappyHourBanner`/`get_happy_hour_banner_for_session` uses the same raw-line predicate and appends text only when it can name current order lines actually discounted; bundle components are therefore excluded from both pricing and claims. `IsHappyHour()` only reads the freezable `ConformanceHooks` clock when the bound persona actually has a `happyHour` window configured, so personas without one (most fixture packs) never touch it. |
| `tools.py`'s `update_order`/`get_order`/`reset_order` (structured rejections: `not_on_menu`+`suggested_calls`, `size_not_available`, `not_in_order`, `machine_unavailable`, `extras_blocked_category`, `extras_no_base_item`) | `Tools/OrderToolExecutor.cs`, `Tools/IToolExecutor.cs`, `Tools/ToolResult.cs` | `IToolExecutor.ExecuteAsync(toolName, args, ct) -> ToolResult` is the seam #13's realtime relay dispatches every `function_call` through -- see "The `IToolExecutor` contract (for #13/#140)" below. `ToolResult.Destination` (`ToServer`/`ToClient`/`ToBoth`) mirrors `ToolResultDirection`'s three cases exactly (`tools.py`'s `TO_SERVER`/`TO_CLIENT`/`TO_BOTH`). |
| `tools.py`'s `search` (issue #23/#37) | `Search/SearchTool.cs`, `Search/SearchEndpointConfig.cs`, `Search/SearchResultCache.cs`, `Search/SearchApiException.cs`, `Search/SearchAuth.cs` | REST, not the SDK -- see "Spikes #44 and #23" above; #23's recommendation is now implemented, not just noted. Same three-tier error/retry cascade as Python (timeout -> apology; field-mismatch 400 -> minimal-select retry -> apology; semantic-rejected -> no-ranker retry -> apology), same process-wide TTL+max-size cache namespaced per persona id, same double-encoded `sizes` JSON-string parsing and machine-OOS result suffix. `api-key` when configured, else a managed-identity bearer token via `DefaultAzureCredential` (scope `https://search.azure.com/.default`) -- mirrors PR #140 R5's identical fallback for the realtime upstream connect; see `SearchEndpointConfig`'s own doc comment. |
| (composition root for one session's full tool surface) | `Tools/SessionToolExecutor.cs` | Composes `OrderToolExecutor` + `SearchTool` into the one `IToolExecutor` a session actually binds -- dispatches `"search"` to `SearchTool`, everything else to `OrderToolExecutor`. `RealtimeProcessor` now builds one of these per session (via its `toolExecutorFactory` constructor parameter, wired in `Program.cs`) once persona binding resolves, in place of the earlier no-op `StubToolExecutor`. |

## Wave 4 (issue #14): order engine, tools, search

Ports `order_state.py`, `menu_utils.py`'s `MenuCatalog`, and `tools.py` (`update_order`/`get_order`/
`reset_order`/`search`) -- design doc section 6's structured rejections, menu-sourced pricing
(#104: a tool call's own `price` argument is never trusted), bundle autoFill/absorption, the
extras engine, `machine_unavailable`, per-persona happy hour (#113), tax, and Route 44 via persona-driven
size aliases. See the Module mapping table above for the file-by-file breakdown.

PR #207 keeps spoken readbacks in parity too: Python `MenuCatalog.spoken()` and C#
`MenuCatalog.Spoken()` both apply the active persona's `sizes.spokenAs` substitutions only to
model-facing readback/delta text, never to the order-summary JSON, menu item names, or search keys.
Substitutions run longest-key-first with alphanumeric/trademark boundaries, so the
`MUNCHKINS®`/`MUNCHKINS` entries speak as `Munchkins` without leaving the `®` mark or rewriting the
middle of another token. The Issue #188 shared conformance vector asserts that parity for both
Python and C# backends.

### The `IToolExecutor` contract (for #13/#140)

```csharp
public interface IToolExecutor
{
    IReadOnlyList<string> ToolNames { get; }
    Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default);
}
```

One `IToolExecutor` instance (`SessionToolExecutor`) is owned by exactly one session's actor --
same confinement contract as `OrderState`/`SessionActor` generally, so there is no shared/locking
concern across sessions. `RealtimeProcessor` constructs one `SessionToolExecutor` per session once
persona binding resolves (closing over that session's own `OrderState`, `MenuCatalog`,
`PromptLoader`, and `SearchTool`); on every upstream `function_call` frame it calls
`ExecuteAsync(toolName, argsElement, ct)` and branches on `ToolResult.Destination` exactly like
`rtmt.py` branches on `ToolResultDirection`: `ToServer` sends the `function_call_output` text back
upstream only, while `ToClient`/`ToBoth` also emits `extension.middle_tier_tool_response` with
`ToolResult.ToClientText()`'s JSON to the browser.

### Test fixture reuse strategy (`Backend.Tests`'s new `Ordering`/`Tools`/`Search` tests)

Rather than hand-building synthetic `Persona`/`PersonaMenu` C# records (heavy, given the full
required-field schema) or duplicating fixture JSON into `backend-dotnet`, the new
`OrderToolExecutorBundleAndExtrasTests`/`HappyHourPricingTests` load Python's own non-brand-coupled
fixture packs directly from `app/backend/tests/fixtures/personas/{test-alpha,test-beta,test-delta}`
(`TestSupport/DeltaFixture.cs`, via `PersonaCatalog.Load`). Both backends' tests therefore prove
behavior against byte-for-byte identical persona/menu JSON -- a difference can never be explained
away by "the fixture data quietly drifted between two copies."

### Shared menu-key golden vectors (#137)

Rick's PR #137 review note: `app/backend/tests/fixtures/menu_key_vectors.json` (20 `{input, key}`
examples covering trademark/registered-trademark symbols, curly apostrophes, nested/multiple
modifier-suffix groups, NBSP, and hyphen-preservation) is asserted identically by Python's new
`MenuKeyVectorTests` (`test_menu_utils.py`) and C#'s new `MenuKey_MatchesTheSharedGoldenVectorFile`
theory (`MenuKeyValidatorTests.cs`) -- one shared file, both backends, so `_menu_key`/
`MenuKeyValidator.MenuKey` can never quietly drift apart.

### Conformance `[Trait("Dotnet", "ready")]` tagging -- unblocked by #13/#140, landed in PR #149

With #13/#140 merged, `Program.cs` now builds a real per-session `SessionToolExecutor` (an
`OrderToolExecutor` + `SearchTool` pair, via a `toolExecutorFactory` passed into
`RealtimeProcessor`) instead of the shared `StubToolExecutor` alone, so `function_call` frames on
the actual `/realtime` WebSocket now reach the real order engine. Every `Scenarios/Ordering/*`
scenario (`OrderScenarioHelpers.RunOrderStepsAsync`/`CallToolAsync`) was re-run individually against
`CONFORMANCE_BACKEND=dotnet` and 83 previously-untagged test methods (445 - 78 = 367 result rows,
since several are `[Theory]` methods with multiple data rows) now pass and are tagged `Dotnet=ready`
this wave, raising the floor from 75 to 158 distinct methods (78 to 445 result rows). The Search
tool's api-key auth path is exercised (the conformance harness always sets
`AZURE_SEARCH_API_KEY`); the new `DefaultAzureCredential` bearer-token fallback is unit-tested only
(`SearchAuthHeaderTests.cs`), since the harness has no fake credential to script.

**Round 2 (Rick's PR #149 R3/R4 review):** 9 more previously-untagged test methods now pass and are
tagged `Dotnet=ready` -- `HappyHourPricingTests` (2), `PersonaBusinessRuleConformanceTests` (2),
`PersonaSearchIsolationConformanceTests` (2), `RealPackPersonaSmokeTests`/`FixturePackPersonaSmokeTests`
(1 `[Theory]` method each), and `ToolFailureCapAndTicketRefreshTests.A_genuine_tool_exception_refreshes_the_guests_ticket`
(R4 below) -- raising the floor from 158 to 167 distinct methods (445 to 457 result rows; confirmed
by a local `dotnet test Conformance.slnx --filter "Dotnet=ready&Category!=Browser"` run).

**R4: the guest's ticket now refreshes after a genuine tool exception.** `RealtimeProcessor`'s
tool-dispatch catch-all previously only sent a fixed apology `function_call_output`, with an
inaccurate comment claiming tools "never throw for a well-formed call" and that there was "no
fresher order summary to refresh a ticket from" -- both wrong: a malformed argument (e.g. a
non-numeric `quantity`) does throw, and the session's own current order state (not a stale cache)
is always readable. `RealtimeProcessor` now pattern-matches the session's `IToolExecutor` against a
new opt-in `Backend.Tools.IOrderTicketSource` interface (implemented by `SessionToolExecutor`,
delegating to a new `OrderToolExecutor.CurrentOrderSummaryJson` accessor) and, best-effort, sends a
`get_order`-tagged `extension.middle_tier_tool_response` to the browser with the refreshed ticket --
mirroring `rtmt.py`'s post-exception `order_state_singleton.get_order_summary_json(session_id)` read
(the read is wrapped in its own try/catch; a session with no readable order state yet just skips the
refresh, and still gets the `function_call_output` below it either way).

**Now tagged (issue #13 Wave 4b): the other 3 `ToolFailureCapAndTicketRefreshTests.cs` methods
(failure cap).** These probe `rtmt.py`'s consecutive-tool-failure cap: a per-connection failure
streak that suppresses the model's own auto-continue once the cap is reached and resets on guest
speech. The "Scope cut (#13): the tool-failure-cap ladder (`_ToolFailureTracker`) is skipped"
comment is gone; `RealtimeProcessor` now owns a real `Realtime/ToolFailureTracker.cs` (a
round-based, not call-based, counter -- a round with only successes, e.g. the model's own
prescribed `get_order` retry, must NOT reset the streak, only `ResetForNewTurn()` does; this is
Python's own PR #58 "S1" infinite-loop fix, ported faithfully) wired at three seams:
`HandleToolCallDoneAsync`'s catch block records a round failure; `HandleResponseDoneAsync` ends the
round and, at cap, sends a one-shot `tool_choice=none` apology `response.create`
(`ToolFailureCapNotice`, mirroring `_build_tool_failure_cap_notice_msg`'s nuance of checking
`PromptLoader.ErrorMessages` directly rather than calling `RenderError` unconditionally, since
`RenderError`'s own unknown-key fallback is a worse `response.instructions` value than the neutral
hardcoded default) and otherwise suppresses the auto-continue entirely once the one-shot notice has
already fired; and the marker side-effect switch resets the streak on both of Python's two
guest-turn signals -- `input_audio_buffer.speech_started` (already handled for other purposes) and
`conversation.item.input_audio_transcription.completed` (previously had **no** handling at all in
C#, not even passthrough; this is a genuine gap filled, not just a port). All three methods are now
tagged `Dotnet=ready` and pass 3x locally on the dotnet leg alongside the existing
`A_genuine_tool_exception_refreshes_the_guests_ticket` row.

**PR #158 (issue #143/ADR-002, R10 + dev-merge floor recount):** tagged all nine
`Scenarios/Auth` auth-row test classes `Dotnet=ready` (196 raw tagged methods across the merged
tree), but the floor itself only counts 178: `DotnetTraitCoverageTests` now excludes the 18
methods in the five classes (`AuthModeLaunchTests`, `AuthRowLoggingTests`,
`AuthRowRealtimeTokenTests`, `AuthRowRestTokenTests`, `AuthRowSpecialCaseTests`) that route every
test through `RunAuthRowAsync`/`AssertFailsFastAsync`, which unconditionally skip on the dotnet
leg (via `AuthRowCapability.ShouldSkipCurrentBackend`) until issue #147 flips
`DotnetEnforcesAuth`. Skip-only methods can't fail the leg, but counting them toward the floor
would let a real coverage regression elsewhere hide behind them staying tagged, so the floor
convention going forward is: a tagged method only counts once it can actually fail, not merely
once it is tagged. The other four Auth classes (`AuthRowCasesTests`,
`AuthRowRealtimeAssertionsTests`, `DevelopmentPassThroughUnsetModeTests`,
`DevelopmentPassThroughExplicitModeTests`) are real, ungated, already-passing-today tests and do
count. See `DotnetTraitCoverageTests`'s own doc comment for the exact arithmetic.

**Issue #165 (breakfast/lunch menu fidelity + menu mode):** raised the floor twice.
Round 1 added `Scenarios/Ordering/MenuModeConformanceTests.cs` (five tagged, ungated methods: mode
switch x2, out-of-mode rejection, search filter, packs-without-modes-unaffected), 178 -> 183. Round
2 (Rick's PR #166 round-1 review, required items 5 and 7) added
`Scenarios/Ordering/MenuModeRejectionConformanceTests.cs` (five more tagged, ungated methods:
unrecognized/empty/repeated `?mode=` all rejected with a real pre-upgrade HTTP 400, an omitted
`?mode=` defaulting to lunch, and a log-capture pin proving the rejected value never reaches either
backend's own logs verbatim), 183 -> 188. Round 2 also added one more tagged
`[Theory]` method to `MenuModeConformanceTests.cs` (required item 6: a period-less dayparts-pack
item is addable in either mode, mirroring the search-filter fix's own admission of the same
items), 188 -> 189. All eleven methods are real assertions against both
backends today (no `AuthRowCapability` skip-gating applies), so all three raises count normally.

**Issue #164 (Rick's PR #167 round-3 review, required item 12):** PR #167 adds one scenario,
`PersonaDiscoveryConformanceTests.Api_persona_detail_pins_tax_rate_and_ui_blocks_against_disk`
(R7, commit 48edade). It is tagged `Dotnet=ready` and is not skip-gated, so it raises the floor
189 -> 190. The dotnet leg executes 190 distinct passing methods, none of them in the five gated
Auth classes (TRX: 485 passed, 61 not executed). A reflection probe agrees (a floor of 191 fails
with "but found 190").

**Issue #179 (combo drink resize) and round 2 (#184):** `ComboComponentResizeConformanceTests.cs`
adds two tagged, ungated `[Theory]` methods dynamically discovering every real pack with a
genuinely-open combo drink slot, raising the floor 196 -> 198. Round 2 (Rick's required items 1/2)
fixes that pricing assertion to the new pure, path-independent model and adds one more tagged,
ungated method, `WholeBundleSizeResizeConformanceTests.Discovered_whole_bundle_size_pack_resizes_the_meal_and_relabels_its_slots`,
covering any pack whose own `bundles.resizeRule` is `wholeBundleSize` (resizing a slot component
cascades into resizing the whole bundle), raising the floor 198 -> 199. Round 3 (#184, Rick's
item H) adds two more tagged, ungated methods covering path-independence and a two-bundle-instance
resize-lands-on-the-holder check, raising the floor 199 -> 201. Round 4 (#184, Rick's round-3
review) adds two more tagged, ungated methods covering wholeBundleSize first-absorption path
independence and pack-owned wholeBundleSize golden vectors, raising the floor 201 -> 203. Issue
#205 adds one tagged, ungated `componentUpcharge` conformance Fact for the medium-included
component delta rule and the additive `componentUpcharges` wire field, raising the floor 203 -> 204.
See `DotnetTraitCoverageTests`'s own doc comment for the exact arithmetic.

**Issue #21 "flip candidates to check early":** the real tagged-method count had drifted to 208
since the floor was last raised. This pass tags 11 more genuinely-passing, already-ported rows --
all 10 `Scenarios/Security/ClientToServerAllowListTests.cs` methods (browser-to-upstream realtime
allow-list hardening, fully ported in `Backend/Realtime/ClientServerFilter.cs`/
`RealtimeProcessor.cs`), plus `OriginValidationTests.Exact_origin_is_accepted` (its two siblings
were already tagged). All 11 verified green against the C# backend (3 clean runs each, no flakes),
raising the floor 204 -> 219 (208 + 11).

**Issue #21 round 2 (PR #230 review, Rick's item 3):** `ClientServerFilter.cs`'s event-id/base64
regexes and `OriginValidator.cs`'s authority comparison are now faithful ports (see the two table
rows above for the exact gaps fixed), backed by 3 new tagged, ungated conformance rows verified
green against both backends (3 clean runs each against the C# backend, no flakes), each
mutation-checked by temporarily reverting its fix and confirming red -- raising the floor
219 -> 222 (219 + 3). PR #226 (#147, Beth's C# auth work) independently raises this SAME floor
204 -> 222 against the stale pre-#21 baseline; agreed merge order is PR #230 lands first at 222,
then PR #226 rebases and re-targets its own floor to 222 + 18 = 240.

**Issue #13 Wave 4b (tool-failure cap) and floor-constant note:** this wave adds 3 newly-tagged
`ToolFailureCapAndTicketRefreshTests.cs` methods. `DotnetTraitCoverageTests`'s own `count >= 222`
constant (already raised by #230, above, by the time this branch rebased onto it) was deliberately
left untouched at that point. Rather than project the real count by arithmetic (unreliable once
multiple in-flight PRs are each independently tagging methods against a moving dev base), it was
measured directly at rebase time by temporarily asserting on the actual
`CountFloorEligibleDotnetReadyTestMethods()` value on this branch: **239** floor-eligible tagged
methods, comfortably above the 222 floor.

**Issue #13 Wave 4/4b floor raise (PR #235 merged to dev, PR #237 rebased onto it):** now that #235
has landed on `dev`, PR #237 re-measured the count the same way immediately after rebasing onto
`origin/dev` (still **239** -- confirming no drift since the first measurement) and raised the floor
constant itself 222 -> 239. Per squad coordination, any PR still rebasing on top of this (`#226`,
`#244`) must re-measure fresh at its own rebase time rather than add its own historical delta (e.g.
#226's planned "+18") to 239 -- those deltas were computed against the stale 222 baseline and risk
double-counting methods (such as this wave's 3 tool-failure-cap rows) already folded into 239.

**Issue #147 (ADR-002: Entra JwtBearer auth, PR #226) ungates all previously-skip-gated Auth
methods.** `AuthRowCapability.DotnetEnforcesAuth` flips `false` -> `true` now that
`app/backend-dotnet` enforces ADR-002 auth end to end (see "Issue #147 (ADR-002)" above for the
full decision record), so every method in the five previously-gated classes
(`AuthModeLaunchTests`, `AuthRowLoggingTests`, `AuthRowRealtimeTokenTests`, `AuthRowRestTokenTests`,
`AuthRowSpecialCaseTests`) now counts toward the floor, since each can genuinely fail against the
dotnet leg instead of only ever skipping. Per the squad-coordination note directly above, this PR
re-measured fresh at its own rebase time (onto the post-#241 `origin/dev`, which itself includes
the Browser conformance leg) rather than projecting by historical delta -- see "Issue #147 round 5
(rebase onto #241, Rick's security re-review)" below for the exact final measured count and
arithmetic.

- **DEV_MODE hot-reload** (`prompt_loader.py`'s file-watching reload behaviour) is explicitly
  marked not required in C# by the design doc's per-backend loading table. Not ported.
- **Jinja2-style template rendering** is implemented for the templates this repo actually ships:
  `PromptLoader.RenderError`, `RenderTemplate`, `GetDeltaTemplate` and `GetUpsellHint` support
  plain `{{ variable }}` interpolation and raw-template fallback for missing variables, and
  `OrderToolExecutor` uses them for structured rejections and delta text. It is not a full Jinja2
  engine because the current `error_messages.yaml` and `hints.yaml` files do not use control flow.
- **Non-blocking connectivity check** (`app.py`'s best-effort, log-only ping to the configured
  Azure OpenAI/Search endpoints at startup) **landed in the issue #13 tail**:
  `Program.cs`'s `CheckServiceConnectivityAsync` pings `AZURE_OPENAI_EASTUS2_ENDPOINT`/
  `AZURE_SEARCH_ENDPOINT` with a 5s total `HttpClient` timeout right after `prompts_loaded`,
  logging success/failure only -- it never gates `/health` or fails startup in either backend, so
  this is purely additive logging parity.
- **Per-session persona/model selection** (`?persona=`/`?model=` query params) was wave 7's scope
  (#74/#75) and **landed this revision** (#12 part 2): `Program.cs`'s `/realtime` handler resolves
  `?persona=` (omitted -> `DEFAULT_PERSONA`, unknown/disabled -> 404 plain text before the upgrade)
  and `?model=` (via `ModelDispatch.DispatchProcessor` + `IPipelineProcessor.ResolveModel`, same
  404-before-upgrade shape) ahead of `AcceptWebSocketAsync`, mirroring `rtmt.py`'s
  `_websocket_handler`. `SessionActor`/`RealtimeProcessor` do not yet forward to a real Azure OpenAI
  realtime session (that's issue #13) -- the resolved persona/model are bound but not yet acted on
  beyond that stub. Rick's PR #122 review item 2: the model-rejection 404 body is byte-for-byte
  identical to Python's regardless of which of the two model except-clauses fired (`Unknown or
  disallowed model: {model_id!r}`, single-quoted Python `repr` via a small `PyRepr` helper) -- the
  underlying reason (unknown catalog id, wrong pipeline, disallowed for this persona, undeployed)
  goes to the log at warning level only, never the client, matching `rtmt.py` exactly. See
  "Model dispatch/rejection body parity (PR #122)" below.
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

## Issue #147 (ADR-002): Entra JwtBearer auth lands on the dotnet leg

PR for issue #147 ports `entra_auth.py`'s whole deny-by-default auth model to C#, matching
`AUTH_MODE`'s two modes exactly:

- **`Development` (default)** -- unchanged pass-through, identical to today: no JwtBearer
  middleware is registered at all, every route stays reachable exactly as before.
- **`Entra`** -- `EntraSettings.Resolve` fails fast at startup (process exit code 1, same as
  Python's `sys.exit(1)`) if `ENTRA_TENANT_ID`/`ENTRA_CLIENT_ID` are unset or a placeholder GUID,
  or if `ENTRA_INSTANCE` isn't `https` (loopback exempted, test/dev only). Once validated,
  `AddEntraAuthentication` (`Auth/EntraAuthentication.cs`) registers `JwtBearer` (issuer, audience,
  signing keys via the tenant's OIDC discovery/JWKS metadata, lifetime) and one
  `EntraAccessRequirement`/`EntraAccessRequirementHandler` authorization requirement (the
  `DriveThru.User` app role plus the configured API scope) as `AddAuthorization`'s
  `FallbackPolicy` -- every route requires a valid, role-bearing Entra token by default, unless
  explicitly `.AllowAnonymous()`'d: `/health`, persona assets matching the same extension
  allow-list Python has (`.svg .png .jpg .webp .ico .wav .mp3`), static files, and `/`.

**One deliberate naming deviation from Python**, called out explicitly rather than silently
matched: Python's fail-fast gate asks "is this a real deployment?" via `RUNNING_IN_PRODUCTION`
(an app-specific env var `entra_auth.py` itself defines and checks). ASP.NET Core already has its
own idiomatic, host-wide equivalent -- `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` ==
`Production` -- and every other fail-fast/log-level decision in this codebase already keys off
that pair (`AppSecretProvider`, `PersonaCatalog`'s dev-only diagnostics), so `EntraSettings.Resolve`
uses the same signal rather than inventing a parallel `RUNNING_IN_PRODUCTION` env var with no other
consumer in the C# codebase. The observable fail-fast *behavior* (production + unconfigured =>
process exit 1) is unchanged; only the env var that answers "are we in production" differs, and
only there.

**`?access_token=` is honored only on `/realtime`** (`JwtBearerEvents.OnMessageReceived`, scoped
to that one path) -- browsers can't set an `Authorization` header on a WebSocket upgrade, so ADR-002
carves out this one query-string fallback; every other route requires the real header. The
layered session token (`/api/auth/session`, `Auth/SessionTokenService.cs`) now binds to the
Entra-validated principal's own `oid` claim: minting requires a valid Entra bearer, and
`Realtime/RealtimeAuthGate.cs`'s pre-upgrade check (forced on in Entra mode, independent of
`config.yaml`'s `security.require_session_token`) rejects a session token whose `oid` doesn't match
the connecting principal's `oid` -- exactly `rtmt.py`'s own binding.

**Issue #163 (anonymous asset extension rule, tracked separately):** Unity's #163 is concurrently
finalizing the exact anonymous-asset-extension carve-out rule (today: a fixed
`.svg .png .jpg .webp .ico .wav .mp3` set, ported byte-for-byte from `entra_auth.py`'s
`ANONYMOUS_ASSET_EXTENSIONS`). This PR matches Python's *current* behavior exactly and does not
pre-empt #163's outcome; whichever extension-rule change #163 lands should be mirrored here as a
small follow-up to `EntraAuthentication.cs`'s `AnonymousAssetExtensions` set, not re-litigated.

**Conformance: `AuthRowCapability.DotnetEnforcesAuth` flips `false` -> `true`.** All 18
previously-skip-gated methods (`AuthModeLaunchTests`=7, `AuthRowLoggingTests`=1,
`AuthRowRealtimeTokenTests`=1, `AuthRowRestTokenTests`=1, `AuthRowSpecialCaseTests`=8) now run for
real against the dotnet backend instead of unconditionally skipping, raising
`DotnetTraitCoverageTests`' floor 204 -> 222. `AuthRowCapabilityTests.cs`'s own pinned "still off"
unit test is replaced with its mirror-image ("now on"), matching the pattern
`Enforces_is_true_for_python_now_that_its_switch_is_on` already established for issue #144.

Two small, in-scope fixes were needed to make the newly-ungated rows genuinely pass (not just
stop skipping), both discovered by running the real Auth conformance rows end to end against a
live dotnet backend with the frontend built (`app/backend/static/index.html` present, matching
what `PythonBackendLauncher`/CI's own frontend-build step already require of both legs):

- **Row 14 (token-leak logging) needed a real `/realtime` access line to exist as its own positive
  control**, proving the leak-checks below it aren't passing vacuously because nothing was logged
  at all. `Realtime/RealtimeAuthGate.cs`'s `Check` now logs one `LogInformation` line per handshake
  attempt (`"Realtime handshake: GET /realtime (host=..., origin=...)"`, deliberately never
  including `token`/`principalOid`) before its Origin/session-token checks run, mirroring aiohttp's
  own access logger wrapping `app.py`'s whole request pipeline.
- Row 12 (`/` on the anonymous allow-list) initially 401'd instead of 200 in a from-scratch
  checkout with no frontend build yet -- traced to `Program.cs` only registering `MapGet("/")` (and
  its `.AllowAnonymous()`) when a static files directory is actually found on disk, combined with
  ASP.NET Core's `FallbackPolicy` applying even to completely unmatched routes. Building the
  frontend (`npm ci && VITE_AUTH_MODE=Development npm run build` in `app/frontend`, the same
  prerequisite `PythonBackendLauncher` already enforces before even launching Python) resolves this
  for a normal checkout; no route-registration code change was needed once that prerequisite is met.

**Validation performed:** `Backend.Tests` 471/471 passing (36 new `EntraSettingsTests`, 28 new
`EntraAccessRequirementHandlerTests`, plus oid/gate additions to existing suites). Conformance:
`CONFORMANCE_BACKEND=dotnet dotnet test Conformance.slnx --filter "Scenarios.Auth"` 76/76 passing;
the full CI dotnet-leg filter `dotnet test Conformance.slnx --filter "Dotnet=ready&Category!=Browser"`
573/573 passing; `CONFORMANCE_BACKEND=python` with `Category!=Browser` 872/876 passing (4 unrelated
pre-existing skips), confirming issue #144's python leg stays green. Mutation check: temporarily
dropped the role requirement from `EntraAccessRequirementHandler.HandleRequirementAsync` (kept the
scope check only) -- 3 unit tests and 6 conformance Auth-row tests failed as expected; reverted and
reconfirmed fully green.

### Issue #163 / PR #222 follow-up: mirroring Python's auth-hardening fixes

Unity's PR #222 (fix #163) landed on the Python side after this PR's initial merge, deciding the
anonymous-asset-extension rule as **case-insensitive** and adding several other auth-hardening
fixes to `entra_auth.py`/`persona_loader.py`. Each was audited against the C# port and mirrored
where applicable:

- **Case-insensitive anonymous asset extension (#163's actual decision).** Already correct in C#:
  `EntraAuthentication.cs`'s extension check already used an ordinal-ignore-case comparison, so no
  code change was needed -- only the stale doc comment (which still said "Unity is concurrently
  finalizing...") was updated to state the now-decided rule plainly. New conformance coverage:
  `AuthRowSpecialCaseTests.Row_12_anonymous_allow_list_path_is_case_insensitive_on_extension`
  (requests `assets/LOGO.SVG`, expects 200 with no token), directly answering PR #222's explicit
  ask for conformance coverage of this rule.
- **Case-insensitive `Bearer` scheme matching.** Already native: ASP.NET Core's own
  `JwtBearerHandler` scheme-prefix matching is ordinal-ignore-case out of the box, so Python's fix
  (which hardened a hand-rolled string compare) has no C# equivalent gap. Doc comment added to
  `OnMessageReceived` noting this explicitly. New conformance coverage:
  `AuthRowRestTests.Scheme_case_insensitive_Bearer_scheme_is_accepted` (theory over `bearer`/
  `BEARER`/`BeArEr`, each expected to authenticate successfully against `/api/personas`), again
  directly answering PR #222's ask.
- **Lower-cased tenant/client IDs.** Genuine gap, fixed: `EntraSettings.Resolve` now lower-cases
  `tenantId`/`clientId` after `ValidateEntraIds` succeeds but before constructing the returned
  record, matching Python's normalization so a mixed-case `ENTRA_TENANT_ID`/`ENTRA_CLIENT_ID`
  can't produce a token-validation mismatch against lower-case claims. New unit test:
  `EntraSettingsTests.Resolve_NormalizesTenantAndClientIds_ToLowerCase` (uses hex-letter GUIDs so
  the assertion isn't a case-insensitive-comparison no-op).
- **JWKS/OIDC outage resilience (backchannel timeout half).** Genuine gap, fixed:
  `ConfigureJwtBearer` now sets `options.BackchannelTimeout = TimeSpan.FromSeconds(10)` so a hung
  OIDC-metadata/JWKS endpoint fails a token-validation attempt within 10s instead of hanging on
  .NET's much longer default `HttpClient` timeout. New unit test:
  `ConfigureJwtBearerTests.BackchannelTimeout_IsTenSeconds`. The other half of Python's #222 fix in
  this area -- an explicit negative-discovery-cache TTL and early malformed-token rejection before
  attempting signature validation -- is already covered by ASP.NET Core's built-in
  `ConfigurationManager<OpenIdConnectConfiguration>` (which already caches negatively and the
  `JwtBearerHandler` already short-circuits structurally invalid tokens before any network call);
  no additional C# code was needed for those.
- **Non-string `scp` claim guard.** Genuine gap, fixed, though the *mechanism* differs from Python
  by necessity: .NET's JWT handler materializes a JSON-array-shaped `scp` claim as multiple
  separate `Claim("scp", ...)` entries rather than a single non-string value, so Python's exact
  failure mode (`AttributeError` from calling `.split()` on a list) is structurally impossible
  here. The previous C# code (`FindFirst("scp")?.Value`) would have silently used only the first
  such claim and ignored the rest -- not a crash, but a silent under/over-grant depending on claim
  ordering. `EntraAccessRequirementHandler.HandleRequirementAsync` now uses `FindAll("scp")` and
  requires *exactly one* matching claim to treat it as a valid scope string; zero or multiple
  claims now fail closed ("no valid scope"), matching Python's non-string -> empty-scopes
  fail-closed semantic even though the underlying claim shape differs. New unit test:
  `EntraAccessRequirementHandlerTests.MultipleScpClaims_TreatedAsNonStringScope_Fails`.
- **Persona-asset symlink rejection -- not yet applicable, scope decision.** Python's #222 hardens
  `persona_loader.py`'s `_validate_persona_assets`, a **load-time** classification pass that
  rejects symlinked persona assets when a persona's catalog is first loaded. The C# port
  (`Personas/PersonaCatalog.cs`) has no load-time asset-classification equivalent at all -- it
  validates schema shape, menu-item/asset-name collisions, and directory existence, but never
  inspects individual asset files for type/symlink-ness. This Python feature predates and is
  unrelated to #147's original Auth-only scope, so it was never ported. Adding an isolated
  symlink check with no surrounding classification pass to hang it off of would be architecturally
  inconsistent; deferring is the right call. Note this is **not a live security gap**: the
  *request-time* defense Python's own #222 description calls "already existing" (rejecting a
  symlink when an asset is actually served, not just at catalog-load time) already has a complete,
  independently-built C# analog in `Personas/PersonaAssetResolver.cs`, predating this PR. Follow-up:
  whoever eventually ports `persona_loader.py`'s load-time `_validate_persona_assets` classification
  pass to C# should fold this symlink check in at that time.
- **Not applicable to C#, no code change:** structured-logging claim/token escaping (Python's `%r`
  vs `%s` fix) -- .NET's idiomatic `ILogger` already logs claim/token values as separate structured
  parameters rather than string-interpolating them into message text throughout this codebase, so
  the anti-log-injection goal is already met natively; access-log route-name fallback -- no generic
  access-log middleware exists anywhere in the C# backend to apply this to; `PRINCIPAL_KEY`
  (aiohttp-specific warning-avoidance plumbing) -- ASP.NET Core's native `HttpContext.User`/
  `ClaimsPrincipal` already solves the underlying problem architecturally.

**Validation performed:** `Backend.Tests` 474/474 passing (471 + 3 new: lower-casing, backchannel
timeout, multi-`scp`-claim). Conformance Auth scenarios (`CONFORMANCE_BACKEND=dotnet`, filtered to
`Scenarios.Auth`) 80/80 passing (76 + 4 new/updated). Full local conformance suite
(`CONFORMANCE_BACKEND=dotnet`, unfiltered) showed pre-existing, unrelated flakiness in
Realtime/Sessions/RateLimit/transport-timing scenario classes (confirmed to reproduce even when run
in isolation, outside any parallelization contention) -- zero failures in any Auth-scenario class;
CI remains the authoritative gate per squad convention.

### Issue #223 (Rick's review of PR #225) follow-up: dotfile extension edge case + JWKS/OIDC outage resilience

Rick's review of Python PR #225 (itself still open/in-progress on the Python side as of this
writing) surfaced two more auth-hardening items for #147 parity:

- **Dotfile persona-asset names have NO extension (genuine gap, fixed).** Python's
  `_is_anonymous` uses `PurePosixPath(asset_path).suffix`, which treats a leading dot as a "hidden
  file" marker, not an extension delimiter: `PurePosixPath(".png").suffix == ""` and
  `PurePosixPath("demo/.png").suffix == ""` (NOT `".png"`), so a dotfile-named asset is never
  anonymous -- it falls through to requiring a valid bearer token like any other
  unrecognized-extension asset. .NET's `Path.GetExtension(".png")` instead returns `".png"`,
  which would have incorrectly classified such a file as anonymous. `EntraAuthentication.cs`'s
  `IsAnonymousAssetRequest` now calls a new private `GetPythonStyleSuffix` helper that exactly
  replicates `PurePosixPath.suffix`'s algorithm (last path segment only, so `"demo/.png"` reduces
  to `".png"` first; a dot at index 0 or at the very end of the name yields no suffix; a
  multi-dot name like `"logo.tar.gz"` yields only the final `".gz"`) instead of the naive
  `Path.GetExtension`. New unit tests in `EntraAccessRequirementHandlerTests.cs`:
  `PersonaAsset_Dotfile_IsNeverAnonymous_StaysPending` (theory over `.png`, `demo/.png`, `.svg`),
  `PersonaAsset_TrailingDot_IsNeverAnonymous_StaysPending`,
  `PersonaAsset_MultiDotName_ClassifiedByLastSuffixOnly_Succeeds`, and
  `PersonaAsset_MultiDotName_TrailingNonMatchingSuffix_StaysPending`. New conformance coverage:
  `AuthRowSpecialCaseTests.Row_12_dotfile_asset_has_no_extension_still_401s` (requests
  `assets/.png` with no token, asserting 401 -- deliberately does not require the asset to exist
  on disk, since the auth decision happens before file resolution). Mutation-verified twice: once
  at the unit level (reverting to naive `Path.GetExtension` broke 3 of the new unit tests) and
  once at the conformance level (same revert broke the new conformance row, returning 404 instead
  of 401 because the mutated code incorrectly let the request through as anonymous).
- **JWKS/OIDC discovery-fetch failures must surface as 401, never 500, with a cooldown (genuine
  gap, fixed).** Reading ASP.NET Core's actual `JwtBearerHandler.HandleAuthenticateAsync` source
  confirmed a real risk: its outer exception handler calls `Events.AuthenticationFailed` and then,
  if that event handler leaves `context.Result` unset, **re-throws** the original exception --
  meaning any failure from the OIDC discovery fetch (`ConfigurationManager.GetConfigurationAsync`,
  invoked lazily on first token-validation attempt) would crash the request pipeline as an
  unhandled exception instead of a clean 401. New `Auth/DiscoveryFailureGate.cs`: a thread-safe,
  `TimeProvider`-injectable cooldown tracker (`RecordFailure()`/`IsInCooldown()`, default 30s).
  `EntraAuthentication.ConfigureJwtBearer` now wires a new `OnAuthenticationFailed` handler that
  recognizes discovery/backchannel failures (`HttpRequestException`, `IOException`,
  `JsonException`, `OperationCanceledException`, walking `InnerException` since
  `ConfigurationManager<T>` wraps the real cause in its own `InvalidOperationException` on a
  cold-start failure) and calls `context.Fail(...)` -- turning what would otherwise be an
  unhandled 500 into a clean 401. `OnMessageReceived` now also short-circuits to an immediate
  `context.Fail(...)` when a bearer token is present and the gate is in cooldown, avoiding a
  second doomed network call within the cooldown window. **Provenance note:** the 30s cooldown
  duration and the "401 not 500, with a cooldown" contract came from the coordinator's description
  of Rick's review, not from a byte-for-byte-portable Python source -- PR #225's own diff (as
  inspected) adds a test (`test_discovery_body_read_failure_engages_negative_cache_cooldown`) that
  references a `TokenValidator(..., discovery_failure_cooldown=60.0)` constructor parameter that
  does not actually exist anywhere in that diff or in Python's current `dev` branch, meaning PR
  #225 itself has not yet landed a working implementation of this specific mechanism to mirror.
  This C# implementation should be revisited once PR #225 merges, in case its final cooldown value
  or mechanism differs from the 30s default chosen here. New unit tests in
  `EntraAccessRequirementHandlerTests.cs`'s `ConfigureJwtBearerTests`:
  `OnAuthenticationFailed_BackchannelFailure_SetsResult_AndRecordsGateFailure` (theory over all
  four exception types), `OnAuthenticationFailed_BackchannelFailure_WrappedInInvalidOperationException_IsStillRecognized`,
  `OnAuthenticationFailed_UnrelatedTokenValidationException_LeavesResultUnset` (negative case),
  `OnMessageReceived_ShortCircuits_WhenGateInCooldown_AndBearerTokenPresent`,
  `OnMessageReceived_ShortCircuits_WhenGateInCooldown_AndRealtimeQueryTokenPresent`,
  `OnMessageReceived_DoesNotShortCircuit_WhenGateNotInCooldown`, and
  `OnMessageReceived_DoesNotShortCircuit_WhenNoTokenPresent_EvenInCooldown`; plus 6 new tests in
  `DiscoveryFailureGateTests.cs` covering the gate's pure cooldown logic with a local fake
  `TimeProvider`. Mutation-verified: gutting `OnAuthenticationFailed` to do nothing broke 5 of the
  10 related unit tests. No conformance-level coverage was added for this item -- simulating a
  JWKS/discovery outage over the wire would require new fault-injection support in the fake Entra
  issuer, which is a larger, separate piece of harness work outside this round's scope; the unit
  tests above give direct, mutation-verified coverage of the actual fix.

**Lesson learned validating this round's conformance coverage:** running
`dotnet test tests/conformance/...` without first setting `CONFORMANCE_BACKEND=dotnet` silently
exercises the **Python** reference backend, not the C# port -- a mutation to the C# fix will look
"caught" by a passing test for the wrong reason (the Python backend was correct all along). Always
pair `CONFORMANCE_BACKEND=dotnet` with a `PATH` that resolves the .NET 11 RC SDK (the system-wide
`dotnet` lacks the `global.json`-pinned version; `$env:USERPROFILE\.dotnet` must be prepended to
`PATH`, not just invoked directly, since `DotnetBackendLauncher` shells out to a bare `dotnet`)
when locally verifying any dotnet-leg conformance change.

**Validation performed:** `Backend.Tests` 496/496 passing (474 + 22 new: dotfile/multi-dot
persona-asset cases, `ConfigureJwtBearerTests` coverage of the new `OnAuthenticationFailed`/
`OnMessageReceived` wiring, `DiscoveryFailureGateTests`). Conformance Auth scenarios
(`CONFORMANCE_BACKEND=dotnet`, filtered to `Scenarios.Auth`, with `PATH` resolving the .NET 11 RC
SDK) 101/101 passing (includes the 1 new dotfile row added this round; the rest of the growth
since the #163/#222 round's 80/80 reflects other squad work merged to `dev` in the interim, not
anything from this round). Full local unfiltered conformance suite continues to show the same pre-existing,
unrelated Realtime/Sessions/Browser/transport-timing flakiness noted in the #163/#222 section
above (reproduces independent of this round's changes); zero failures in any Auth-scenario class;
CI remains the authoritative gate per squad convention.

### Issue #147 round 3 (coordinator note citing Rick's PR #226 review): making the coverage floor honest about skip-gated rows

`DotnetTraitCoverageTests.At_least_222_scenarios_are_tagged_dotnet_ready_and_not_skip_gated`
previously excluded skip-gated `Scenarios/Auth` classes from its reflection-based count via a
hand-maintained `HashSet<string>` of type full names (`AuthRowGatedTypeNames`), emptied when this
issue flipped `AuthRowCapability.DotnetEnforcesAuth` to `true`. Rick's concern: this floor is only
ever a *lower bound* (`count >= 222`), so if `DotnetEnforcesAuth` were ever flipped back to `false`
-- a real regression that would silently move the 18 gated methods from `Passed` back to `Skipped`
on the dotnet leg -- the raw count could still clear 222 purely from unrelated scenario growth
elsewhere in the suite, and the floor test would stay green despite the regression. A
reflection-only count can't tell "tagged and genuinely passing" from "tagged but unconditionally
skip-gated" apart; only a hand-maintained exclusion list could, and nothing forced that list to
stay in sync with the real capability flag.

Fixed by replacing the list with two changes:

- A new `[AuthRowCapabilityGated]` marker attribute (`Conformance.Harness.AuthRowCapabilityGatedAttribute`,
  declared alongside `AuthRowCapability` itself) applied directly to the five gated classes
  (`AuthModeLaunchTests`, `AuthRowLoggingTests`, `AuthRowRealtimeTokenTests`,
  `AuthRowRestTokenTests`, `AuthRowSpecialCaseTests`). `CountFloorEligibleDotnetReadyTestMethods`
  now excludes an attribute-carrying type's methods only while
  `AuthRowCapability.Enforces("dotnet")` resolves `false` -- no separate list to remember to update
  when the flag changes; the exclusion is derived from the real capability function every time the
  floor runs.
- The floor `[Fact]` now also asserts `AuthRowCapability.Enforces("dotnet")` directly (with a
  message naming the exact regression it guards against), *in addition to* the `count >= 222`
  check -- so a `DotnetEnforcesAuth` regression fails this test immediately and unambiguously,
  independent of how much slack the raw count has from unrelated growth.

**Mutation check:** temporarily set `AuthRowCapability.DotnetEnforcesAuth = false` --
`DotnetTraitCoverageTests`'s new capability assertion failed with the expected message, and (as a
second, independent confirmation) the 18 previously-gated methods correctly reported `Skipped`
again when the Auth conformance filter was re-run (`Failed: 2, Passed: 34, Skipped: 66, Total: 102`
-- the two failures being this floor test and `AuthRowCapabilityTests`'s own existing
`Enforces_is_true_for_dotnet_now_that_its_switch_is_on` pin); reverted, and the suite returned to
102/102 passing with 0 skipped.

**Note on the pending floor-number rebase (resolved -- see "Issue #147 round 4" below):** per the
coordinator's cross-PR sequencing note, PR #230 (issue #21) was expected to merge to `dev` before
this PR rebased and raised the floor constant/doc comment from 222 to the final combined number.
That rebase has now happened; see the next section for the exact arithmetic and final count (248,
not the naively expected 222 + 18 = 240).

### Issue #147 round 4 (coordinator-directed rebase onto PR #230's merged floor of 222): the exact final count

PR #230 (issue #21, both rounds) merged to `dev` first, raising its own floor 204 -> 222 (see the
two "Issue #21" entries above). Per the coordinator's instruction, `squad/147-csharp-entra` was
then rebased onto `origin/dev` (`git rebase origin/dev`, all 7 commits replaying cleanly after
resolving doc-comment/floor-constant conflicts in this file and `DotnetTraitCoverageTests.cs` --
the `AuthRowGatedTypeNames` structural conflict only occurred in this PR's very first commit, since
the round-3 `AuthRowCapabilityGatedAttribute` rewrite above is a later commit that replayed cleanly
against the first commit's own resolution), and the floor's new value was computed by the test's
own `CountFloorEligibleDotnetReadyTestMethods` reflection logic -- not hand arithmetic, per the
coordinator's explicit instruction -- by temporarily raising the assertion threshold, reading the
actual count from the failure message, then reverting.

**Result: 248, not the naively expected 222 + 18 = 240.** The gap is fully explained by two
compounding, independently-verified factors:

1. **This PR's own two follow-up rounds already added 3 more tagged, ungated rows to the same five
   previously-gated classes**, raising their total from 18 (as of this PR's first commit,
   `AuthModeLaunchTests`=7, `AuthRowLoggingTests`=1, `AuthRowRealtimeTokenTests`=1,
   `AuthRowRestTokenTests`=1, `AuthRowSpecialCaseTests`=8) to 21 at the current tip
   (`AuthRowRestTokenTests`=2 after the case-insensitive-Bearer-scheme theory method;
   `AuthRowSpecialCaseTests`=10 after the two Row-12 case-insensitive-extension/dotfile-suffix
   methods). Verified by diffing `[Theory]`/`[Fact]` counts in those five files between this PR's
   first commit and its tip: 18 -> 21 (+3).
2. **`dev`'s own non-Auth tagged-method count had already organically drifted 5 rows ahead of its
   own stated 222 floor** by the time this PR rebased onto it (227, not 222) -- the exact same
   "floor is a lower bound, the real count can run ahead of it between raises" shape the "Issue #21
   'flip candidates to check early'" entry above already documents for `dev` itself (204 floor, 208
   actual, at that time). Verified with a temporary diagnostic `[Fact]` that printed the
   per-type method breakdown after the rebase: summing every non-`Scenarios/Auth`-gated type's
   contribution gives 227; summing the five gated types gives 21; 227 + 21 = 248 exactly, matching
   the measured `CountFloorEligibleDotnetReadyTestMethods()` result. The diagnostic method was
   reverted before committing -- it exists nowhere in the final diff.

`DotnetTraitCoverageTests.At_least_222_scenarios_are_tagged_dotnet_ready_and_not_skip_gated` is
renamed to `At_least_248_scenarios_are_tagged_dotnet_ready_and_not_skip_gated` (`count >= 248`),
and every doc-comment/error-message reference to "222" or "18" in that class that described this
PR's own floor target (as opposed to `dev`'s pre-rebase baseline, which stays "222" in the
historical narrative) is updated to 248/21 to match.

**Validation after rebase:** `Backend.Tests` unaffected by this rebase (no production code
changed, only test/doc files) -- still 496/496 passing. Conformance suite rebuilt clean
(`Backend.slnx` and `Conformance.slnx` both 0 warnings/0 errors). Full dotnet CI filter
(`CONFORMANCE_BACKEND=dotnet`, `--filter "Dotnet=ready&Category!=Browser"`, `PATH` resolving the
.NET 11 RC SDK) and the `Scenarios.Auth`-filtered run both pass cleanly against the new floor of
248, including `DotnetTraitCoverageTests` itself and `AuthRowCapabilityTests`'s own pinned
`Enforces_is_true_for_dotnet_now_that_its_switch_is_on` Fact. Pushed with `--force-with-lease`
(rewriting history via the rebase); CI reconfirmed green on the rebased tip.

### Issue #147 round 5 (rebase onto #241, Rick's security re-review): fixing the 3 outstanding findings

Rick's PR #226 security re-review passed the core model outright (startup fail-fast, alg/issuer/
audience/tid/300s skew, the anonymous allow-list including encoding tricks, `?access_token` only on
`/realtime`, session-token oid binding, the CI Docker step, the package) and flagged 3 remaining
findings. A prior, cancelled session had already landed most of the fix for all 3 (the cooldown
decorator, the claim-shape checks, and the `LastKnownGoodLifetime` shrink) but left one genuine
regression undiscovered and the doc/floor bookkeeping unfinished; this round finished that work.

**Finding #1 (HIGH, JWKS/OIDC outage cooldown never arms on the real pipeline):** already correctly
implemented by the prior session in `Auth/CooldownAwareConfigurationManager.cs` -- a decorator
around the inner `ConfigurationManager<OpenIdConnectConfiguration>` whose `GetBaseConfigurationAsync`
records a failure via `DiscoveryFailureGate.RecordFailure()` only inside the `catch` for a fetch the
request actually awaited, and only when `_hasConfiguration` is false (never from a background
`RequestRefresh`, which never throws through this code path at all, and never merely because
`IsLastKnownGoodValid` happens to be false). `Auth/DiscoveryFailureGate.cs` is the thread-safe
30-second cooldown tracker consulted by `ConfigureJwtBearer`'s `OnMessageReceived` to fail fast
(401, no fetch attempted) while in cooldown and no configuration is held. No changes needed here
this round; verified via a fresh read of both files plus a mutation check (see below).

**Finding #2 (MEDIUM, claim-shape parity with Python) -- found and fixed a genuine pipeline bug:**
the prior session's `nbf`-missing and `roles`/`scp`-shape checks in `EntraAuthentication.cs`'s
`OnTokenValidated`, and the matching `AuthRowCases.cs` rows (17: missing nbf, 18: malformed roles
shape, 19: malformed scp shape) plus `FakeEntraIssuer.cs`'s `OmitNbf`/`MalformedRolesShape`/
`MalformedScopeShape` overrides, were all already in place and unit-tested
(`EntraAccessRequirementHandlerTests`) -- but running the conformance suite against the real
pipeline for the first time this round (`CONFORMANCE_BACKEND=dotnet`, `Scenarios.Auth`) surfaced
Row 18 (malformed `roles` shape: a bare JSON string, not an array) returning 200 instead of 403 on
both `AuthRowRestTokenTests` and `AuthRowRealtimeTokenTests`. Root-caused with an isolated
`JsonWebTokenHandler.ValidateTokenAsync` scratch test: the implementation's
`JsonWebToken.TryGetPayloadValue<JsonElement>("roles", out ...)` silently returns `false` -- not an
exception, a false negative -- for ANY claim whose underlying JSON value is a scalar (string, in
this case), because IdentityModel's `JsonWebToken` only stores a genuine `JsonElement` internally
for array/object-valued claims; scalar-valued claims are stored as their natively-mapped CLR type
(`string` here, confirmed via `TryGetPayloadValue<string>` succeeding on the exact same claim in the
exact same scratch test). The code's first cut misread "TryGetPayloadValue returned false" as
"claim absent, shape OK," so a bare-string `roles` claim always sailed through unflagged. Fixed by
replacing the `TryGetPayloadValue<JsonElement>` calls for `roles`/`scp` with a new
`EntraAuthentication.ParseRawPayload(JsonWebToken)` helper that base64url-decodes
`JsonWebToken.EncodedPayload` and parses it directly as a `System.Text.Json.JsonDocument`, then uses
`JsonElement.TryGetProperty` -- reading the TRUE raw JSON shape regardless of which CLR type
IdentityModel happened to map a given value to, matching Python's own `claims.get(...)`/
`isinstance(...)` checks against the real parsed JSON payload. `jwt.TryGetPayloadValue<long>` for
`nbf` is unaffected (numeric claims DO round-trip through IdentityModel's native-type path
correctly; only the `JsonElement`-typed generic accessor has this gap), confirmed because Row 17
(missing nbf) was never among the conformance failures. `TryAllIssuerSigningKeys = false` (so an
unknown kid -> 401) was already set correctly by the prior session; re-verified unchanged this
round. Files touched: `Auth/EntraAuthentication.cs` only (the `OnTokenValidated` shape-check block
and the new `ParseRawPayload` helper); no test files needed changes since the existing Row 17/18/19
conformance rows already existed and now genuinely pass end to end.

**Finding #3 (LOW-MED, rotated-out signing keys stay valid up to 1h):** already correctly fixed by
the prior session -- `ConfigureJwtBearer` constructs its own `CooldownAwareConfigurationManager`
wrapping a `ConfigurationManager<OpenIdConnectConfiguration>` with
`LastKnownGoodLifetime = lastKnownGoodLifetime ?? TimeSpan.FromSeconds(300)` (an optional parameter
defaulting to the production 300s, overridable only by tests), replacing IdentityModel's 1-hour
default while keeping warm-outage resilience (the LKG fallback itself is not disabled, just bounded
to a much shorter grace period). No changes needed; verified via a mutation check (see below). No
conformance-level key-rotation row was added -- see "Known gaps" below for why.

**Validation performed this round:**
- `Backend.slnx` builds clean (0 warnings, 0 errors) after the finding-#2 fix.
- `Backend.Tests`, `FullyQualifiedName~Backend.Tests.Auth`: 126/126 passing (includes the 3
  pipeline-level `EntraPipelineCooldownTests` scenarios for findings #1/#3, and
  `CooldownAwareConfigurationManagerTests.ConfigureJwtBearer_DefaultsLastKnownGoodLifetimeTo300Seconds`
  pinning finding #3's production default).
- `Backend.Tests`, full suite: 672/674 (later re-run) / 673/674 (earlier re-run) passing -- the one
  consistently-failing test, `Backend.Tests.Cascade.BrowserSocketCancellationTests.RunSessionAsync_
  BargeInDuringABackpressuredTtsWrite_DoesNotAbortTheBrowserSocket`, reproduces identically in
  isolation and on a clean `git stash` of this round's entire diff (confirmed by re-running it
  against the pristine pre-round working tree) -- a pre-existing, unrelated WebSocket-timing/
  Cascade test, not a regression from this round's auth work.
- Conformance, `CONFORMANCE_BACKEND=dotnet`, `Scenarios.Auth`: 87/87 passing (confirms Row 18 fix
  end to end, plus Rows 17/19 and everything else in the Auth scenario tree).
- Conformance, `CONFORMANCE_BACKEND=dotnet`, full CI filter (`Dotnet=ready&Category!=Browser`):
  627/627 passing.
- Conformance, `CONFORMANCE_BACKEND=python`, `Category!=Browser` (full suite, both tagged and
  untagged): 911/918 passing, 6 skipped, 1 failed
  (`DotnetBackendLauncherPortRaceTests.StartAsync_recovers_when_the_assigned_port_is_already_bound_
  by_someone_else`) -- this test launches a real dotnet-leg backend process regardless of
  `CONFORMANCE_BACKEND`, and fails identically (same `AUTH_MODE=Development ... refused in
  Production` fatal) on a clean `git stash` of this round's entire diff, i.e. it was already broken
  before this round started and is unrelated to findings #1/#2/#3 or auth claim-shape handling; not
  fixed here as it is out of this round's scope (harness/launcher config, not `Auth/*` production
  code), but flagged honestly as a pre-existing gap.
- `DotnetTraitCoverageTests`'s floor was re-measured fresh (temporarily raised to an unreachable
  bound, actual count read off the assertion-failure message, then set to the real value, per the
  coordinator's explicit "measure, don't compute" instruction): **281** (up from 260). The ~21-count
  rise is explained by the finding-#2 conformance rows (17/18/19) each being exercised by both
  `AuthRowRestTokenTests.Row_asserts_on_every_REST_path` and
  `AuthRowRealtimeTokenTests.Row_asserts_on_realtime` as individual `[Theory]` cases, which
  `CountFloorEligibleDotnetReadyTestMethods` counts as distinct cases, plus unrelated organic growth
  elsewhere on `origin/dev` since the round-4/PR#253 260 measurement.
- **Mutation checks (temporarily break, confirm red, revert, confirm green) for all 3 findings:**
  1. Finding #1: commented out `CooldownAwareConfigurationManager.GetBaseConfigurationAsync`'s
     `_gate.RecordFailure()` call -- `EntraPipelineCooldownTests.ColdOutage_AtMostOneFetchPerWindow_
     AndFastFailures` (and 7 other cases in the same filtered run) went red
     (`Assert.True() Failure: Expected True, Actual False`); reverted; full 18/18 green again.
  2. Finding #2: widened the mutated `rolesShapeOk` check to also accept `JsonValueKind.String`
     (restoring the exact shape of the original bug class) -- `AuthRowRestTokenTests.Row_asserts_on_
     every_REST_path(caseIndex: 21)` and `AuthRowRealtimeTokenTests.Row_asserts_on_realtime
     (caseIndex: 21)` (Row 18) both went red (2/51 failed); reverted; full 51/51 green again.
  3. Finding #3: changed the production default back to IdentityModel's
     `LastKnownGoodLifetime = lastKnownGoodLifetime ?? TimeSpan.FromHours(1)` --
     `CooldownAwareConfigurationManagerTests.ConfigureJwtBearer_DefaultsLastKnownGoodLifetimeTo300Seconds`
     went red (`Expected: 00:05:00, Actual: 01:00:00`); reverted; green again.

**Known gaps (honest, not glossed over):**
- No conformance-level key-rotation row for finding #3. `EntraPipelineCooldownTests.KeyRotation_
  RotatedOutSigningKeyIsRejectedAfterLastKnownGoodLifetimeElapses` proves the mechanism pipeline-
  wide, but it does so only by constructing the host in-process with a test-shrunk
  `lastKnownGoodLifetime` parameter (300ms instead of 300s) passed directly to
  `ConfigureJwtBearer`. The conformance suite launches the real, external dotnet backend process
  with no such override hook in its production startup path (`lastKnownGoodLifetime` is an optional
  parameter used only by tests that call `ConfigureJwtBearer` directly) -- a true black-box
  conformance row would need to wait out the real 300-second production window, impractical for a
  CI-speed suite. This mirrors the precedent already set for finding #1's own cooldown window when
  #223 first landed it (also Backend.Tests-only, for the same 30-second-real-wait reason).
- The pre-existing `DotnetBackendLauncherPortRaceTests` and `BrowserSocketCancellationTests`
  failures noted above were confirmed unrelated to this round's changes (reproduce identically with
  this round's diff stashed out) but were not investigated further or fixed, being out of this
  round's stated scope (C# backend auth + its tests + conformance + this doc only).

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
- **#23 (FakeSearch HTTPS)**: implemented by `Search/SearchTool.cs` using plain `HttpClient` REST
  calls against the Search data-plane API. That keeps conformance on the HTTP-only
  `FakeSearchServer` path and avoids the Azure.Search.Documents HTTPS-only transport issue.

## Conformance coverage history and remaining gaps

- `DotnetBackendLauncher` (`tests/conformance/src/Conformance.Harness/DotnetBackendLauncher.cs`,
  implementing `IBackendUnderTest`) -- wired into `BackendLauncherFactory`'s
  `CONFORMANCE_BACKEND=dotnet` branch. It forwards `PERSONAS_DIR`, `PERSONAS` and
  `DEFAULT_PERSONA`, builds `app/backend-dotnet` once per test process, then launches the compiled
  `Backend.dll`. Run locally with
  `CONFORMANCE_BACKEND=dotnet dotnet test Conformance.slnx --filter "Dotnet=ready&Category!=Browser"`
  (repo root needs a built frontend at `app/backend/static` -- `npm run build` in `app/frontend`
  -- for static-file scenarios). `DotnetTraitCoverageTests` now enforces at least 222 floor-eligible
  tagged methods; the early tagged set included:
  - `PersonaDiscoveryConformanceTests` -- 4 of 5 methods (persona list/detail shape, 404 for an
    unknown persona id, pre-upgrade 404 for an unknown `?persona=` on `/realtime`). The 5th
    (`Omitted_persona_binds_to_the_default_persona_visible_in_session_metadata`) needs a real
    upstream `session.created` echo (`extension.session_metadata`), which only fires once the
    actual realtime relay lands (#13); left untagged.
  - `PersonaAssetRouteConformanceTests` -- full class (asset/menu 200s, immutable-vs-short cache
    headers, 404s for an unknown persona). Rick's PR #122 review item 1: the traversal `[Theory]`
    now has 4 `InlineData` rows (was 3, all blind) -- see "Traversal defense mutation-test note"
    below for why the previous rows could never observe a resolver regression, and what changed.
  - `PersonaDisabledPackConformanceTests` -- full class (a disabled pack 404s on detail/asset/menu
    while an enabled pack still serves; proves the `PERSONAS_DIR` fix above).
  - `ModelSelectionRejectionConformanceTests` -- full class, now 6 methods (was 4): pre-upgrade 404
    on `/realtime` for an unknown model id, a model disallowed for this persona, a
    catalogued-but-undeployed model, a model catalogued for a different pipeline, plus two new
    rows added for Rick's PR #122 review item 2 asserting the exact 404 BODY text (not just the
    status) for an unknown persona and an unknown model. See "Model dispatch/rejection body parity
    (PR #122)" below.
  - `ModelSelectionConformanceTests` -- originally only the pure HTTP selectable-models row. The
    later realtime rows became taggable once the relay landed.

  Carried over from PR #96 (unchanged, still tagged): `HealthEndpointTests`,
  `HealthEndpointExtendedTests`, `StaticIndexHtmlTests`, `AuthSessionTests`,
  `AuthSessionTokenFormatTests` (both cases), all 3 of `Scenarios/Http/OriginValidationTests.cs`,
  and (as of issue #21) all 3 of `Scenarios/Security/OriginValidationTests.cs` (the third,
  `Exact_origin_is_accepted`, was the one genuinely-untagged row left in that class). The
  conformance workflow now has a `backend: [python, dotnet]` matrix; the dotnet leg runs
  `Dotnet=ready&Category!=Browser`.
- Session-level persona/model/mode binding is now forwarded into the live Azure OpenAI realtime
  session through `RealtimeProcessor.RunSessionAsync`; `ProcessAsync` is intentionally unused for
  accepted WebSockets.
- Issue #14's order engine/tools/search wave landed the full `IToolExecutor`/`SessionToolExecutor`
  composition. PR #149 wires `RealtimeProcessor` to construct and dispatch to a real
  per-session `SessionToolExecutor` (via `Program.cs`'s `toolExecutorFactory`), so every
  `tests/conformance/.../Scenarios/Ordering/*` scenario now runs against the real order engine. See
  "Conformance `[Trait("Dotnet", "ready")]` tagging -- unblocked by #13/#140, landed in PR #149"
  above for the full reasoning and the 3 remaining untagged failure-cap methods.

### Issue #21 (Wave 6 early): Browser-on-C# enablement

The `Category=Browser` subset (`tests/conformance/tests/Conformance.Tests/Scenarios/Browser`) ran
only against Python until now. No code change was needed to make the fixture itself
backend-agnostic: `BrowserConformanceFixture`'s inner `BrowserTimersBackendFixture` was already a
plain `ConformanceFixture` subclass going through the same `IBackendUnderTest`/
`BackendLauncherFactory` abstraction (`CONFORMANCE_BACKEND=dotnet`) every other scenario uses, and
both backends already serve the same built frontend from `app/backend/static`
(`Program.cs`'s `STATIC_FILES_DIR`/`TryFindStaticDir()` fallback matches Python's `app.py`
exactly) -- `StaticIndexHtmlTests` already proved this. Confirmed empirically: running the whole
Browser suite with `CONFORMANCE_BACKEND=dotnet` and zero code changes produced a clean 10/18 pass
split with no infrastructure failures, only capability-gap failures (below).

Added a dedicated `conformance-browser-dotnet` CI job (`.github/workflows/conformance.yml`),
mirroring `conformance-browser`'s (Python) structure plus the `conformance` matrix job's "dotnet
restore and build app/backend-dotnet" step, filtered to `--filter "Category=Browser&Dotnet=ready"`
with `CONFORMANCE_BACKEND=dotnet`. Added to `conformance-gate`'s `needs:` array. Kept as its own job
(not folded into the `conformance` matrix, which still explicitly excludes
`Category=Browser` per PR #54's blocker B2) so a real-browser failure/timeout never blocks the fast
black-box dotnet leg, same rationale as `conformance-browser`.

Ran every Browser test 3x against `CONFORMANCE_BACKEND=dotnet` locally (Playwright against the
runner's installed Edge/Chrome channel, built frontend via `VITE_AUTH_MODE=Development`). Tagged
10 floor-eligible `[Trait("Dotnet", "ready")]` methods, all passing 3/3 with zero flakes:
- `BrowserFixtureEnvironmentTests` -- full class (4 methods), class-level tag. Pure reflection over
  the fixture's static shape, never starts a backend, so it is backend-agnostic by construction.
- `ScrollPositionBrowserTests` -- full class (2 methods, 3 persona rows each), class-level tag.
  Only loads the built frontend from static files and reads `window.scrollY`/DOM layout; never
  touches the realtime relay or order pipeline.
- `PersonaSwitchBrowserTests` -- 4 of 7 methods, method-level tags: `Settled_tap_...`,
  `Immediate_tap_...` (smoke cases A/B), `Mic_tap_while_the_switch_fetch_is_still_pending_...`
  (case C, held-fetch race inside a still-open socket), `Held_old_close_past_the_new_sockets_open_...`
  (case D, held onclose-handler race). None of these four depend on the idle-timeout sweep or
  resume.

Deliberately left untagged (capability gaps, not flakes -- confirmed by reading
`RealtimeProcessor.cs`'s own class doc comment, which lists these as deferred to #15):
- `OrderResumeBrowserTests` -- all 5 methods. Every scenario needs `extension.resume`/session
  rehydration, the 4002 supersede-close, or the 4000 idle-timeout close.
- `PersonaSwitchBrowserTests.Idle_closed_socket_then_persona_switch_then_tap_recovers_the_new_personas_session`
  and its held-fetch sibling (cases E/E2) -- both wait for the backend's own idle-timeout close to
  land first; against `CONFORMANCE_BACKEND=dotnet` both time out after 40s with "Timed out ...
  waiting for the test-alpha socket's own idle-timeout close to land".
- `PersonaSwitchBrowserTests.Reload_then_resume_then_switch_through_the_confirm_dialog_delivers_only_to_the_new_personas_session`
  -- needs a reload to resume a non-empty order, i.e. `extension.resume` itself.

Mutation-checked `PersonaSwitchBrowserTests.Settled_tap_...` against C#: temporarily forced
`RealtimeProcessor.cs`'s `BuildGreetingFrame()` to always take its generic-fallback branch instead
of the persona-specific `promptLoader.Greeting` prompt, rebuilt, and confirmed the test goes red
(`Assert.Contains() Failure: Sub-string not found` on the greeting-substring assertion) -- proving
this tagged row genuinely exercises the C# persona-prompt wiring, not just transport plumbing.
Reverted and reconfirmed green.

Floor interaction (`DotnetTraitCoverageTests.CountFloorEligibleDotnetReadyTestMethods`): this
method has no `Category` filter of its own -- it counts every effectively-`Dotnet=ready`-tagged
method in the assembly regardless of category, even though the prose in its own doc comment
describes the count as mirroring the dotnet matrix leg's `Category!=Browser` filter. That means
these 10 newly-tagged Browser rows DO inflate the underlying count (213 -> 223 in this revision),
even though the `conformance` matrix job's dotnet leg still never runs them (filtered out by its
own `Category!=Browser`). The `>= 204` floor assertion is unaffected either way (both 213 and 223
satisfy it) and this file was not edited for this round, per the floor-coordination freeze across
#230/#226/#235 -- flagging here for whoever picks the floor coordination back up: either exclude
`Category=Browser` methods from `CountFloorEligibleDotnetReadyTestMethods` to make the count
actually mirror the matrix leg's filter, or accept that Browser rows are double-counted against a
floor they don't contribute to in the job that floor describes.

## Issue #13 (S3): `RealtimeProcessor` browser&lt;-&gt;Azure OpenAI Realtime GA relay

`RealtimeProcessor.ProcessAsync` is no longer a no-op stub: it dials the upstream Azure OpenAI
Realtime GA WebSocket, bootstraps the session (persona instructions/voice/tools, catalog-resolved
deployment, reasoning-effort precedence identical to `rtmt.py`'s `_build_session`), then relays
frames bidirectionally with voice-lock, a minimal-session-update fallback on a rejected bootstrap,
the greeting gate, mic-audio echo suppression, barge-in (`response.cancel`) handling, and the
client/server key allow-list scrub (`ClientServerFilter`). Tool calls flow through a new
`IToolExecutor` seam (`Tools/IToolExecutor.cs`) with a `StubToolExecutor` thin adapter until #14's
real tool/order-state implementation lands (interface shape agreed with Summer via comments on
#14/#140, then re-aligned in this revision to drop the `sessionId` parameter per #14's merged PR
#149 contract).

**Conformance `Dotnet=ready`: 33 -&gt; 78 test methods, all passing**
(`CONFORMANCE_BACKEND=dotnet dotnet test Conformance.slnx --filter "Dotnet=ready"` is green,
78/78 -- 77/77 as of this issue's original submission, +1 from PR #140 round 2 R3 below). The 44
newly-tagged this revision, all confirmed real-backend scenarios (not harness self-tests):

- `PersonaDiscoveryConformanceTests` -- the 5th method (session-metadata echo), previously blocked
  on a real `session.created`/`extension.session_metadata` frame -- now tagged.
- `ModelSelectionConformanceTests` -- the remaining explicit-model, reasoning-only-for-catalog-model,
  and omitted-model/pipeline-metadata rows, all previously blocked on the same real-relay echo.
- `SessionUpdateFallbackTests.cs` (all 3 classes): a rejected reasoning bootstrap recovers via
  exactly one minimal fallback with no error reaching the browser, unrelated errors never trigger a
  fallback, and rejecting the fallback itself sends no second fallback (loop guard).
- `GreetingTimeoutFallbackTests`, `GreetingWithoutAudioUnmutesTests`, `HeartbeatPongSurvivalTests`,
  `SessionBootstrapGaShapeTests`, `SmokeSessionBootstrapTests` (class-level).
- `Scenarios/BargeIn/ResponseCancelRelayTests` (both methods) -- real backend-dependent barge-in
  relay, distinct from the harness-only `ResponseCancelTests` (left untagged).
- `ResponseDoneRoundTripTests`, `SessionUpdatedClientVisibilityTests` (session.updated never leaks
  `instructions`/`tools`), `UpdateOrderToolCallTests` (proves #13's tool-call wire plumbing through
  `StubToolExecutor`, not #14's real order logic).
- `VoiceLockTests`, and 6 of `VoicePickerTests`'s methods (the resume/session-end methods and the
  file's own `HasTurnDetection` static-predicate self-test stay untagged; the former pend #15, the
  latter is not a real-backend scenario regardless of backend).
- `ReasoningByDeploymentTests.cs` (all 4 classes, class-level) -- reasoning-effort precedence for
  the default/DZ deployments and the explicit-off switch, exactly mirroring `rtmt.py`.
- `Scenarios/Security/ScrubHardeningTests`, `Scenarios/Security/ResponseCreateHooksGateTests`
  (class-level, both now fully passing).
- `Scenarios/Transport/CloseCodeTests.Guest_initiated_end_session_closes_with_1000_session_ended`
  -- a guest-initiated `extension.end_session` now closes the browser socket with the fixed
  1000/"session_ended" shape (`rtmt.py`'s `_forward_messages` branch), needing no session-registry
  state unlike the file's other two scenarios (4002 supersede, 4000 idle timeout -- both still #15).
- `Scenarios/Security/AllowListBypassHardeningTests` (all 5 methods, class-level) -- the fast-path
  anchoring, per-type top-level-key rebuild, and (per fix 4 below) the duplicate-top-level-key
  drop-frame-and-stay-alive contract are all confirmed matching `rtmt.py`'s hardening.

**Bugs found and fixed via the conformance sweep** (none were pre-existing scope reductions --
these are genuine parity gaps against `rtmt.py`):
1. `SessionIdentifiers` was missing a `pipeline` field in `extension.session_metadata` --
   `rtmt.py`'s `emit_session_identifiers` always includes `pipeline` (`"realtime"` here); added
   `SessionIdentifiers.Pipeline`.
2. Both the rejected-bootstrap fallback (`HandleErrorAsync`) and the browser-forwarded
   `session.update` path (`ProcessClientMessage`) called `RealtimeSessionBuilder.BuildSession`
   without a `systemMessage:` argument, so `instructions` (and by extension `tools`) were never
   re-stamped on those two paths -- only the initial bootstrap call site passed it. `rtmt.py`'s
   `_build_session` unconditionally re-stamps `instructions`/`tools` on every session.update
   regardless of what the client sent (see `ClientToServerAllowListTests`'s doc comment). Fixed
   both call sites.
3. A client/upstream frame with a duplicate top-level JSON key (e.g. two `"type"` fields) crashed
   the relay loop for that connection: `System.Text.Json.Nodes.JsonNode.Parse(...) as JsonObject`
   throws `ArgumentException` (not `JsonException`) on a duplicate top-level key -- a documented
   .NET behavior difference from `JsonDocument`, which tolerates duplicates. Both malformed-frame
   guard clauses (`RelayBrowserToUpstreamAsync`, `RelayUpstreamToBrowserAsync`) only caught
   `JsonException`; widened to `catch (Exception ex) when (ex is JsonException or ArgumentException)`.
4. Fix 3 above was necessary but not sufficient: `JsonObject`'s backing dictionary is built
   *lazily*, on the first property access, not during `JsonNode.Parse` itself -- so the duplicate-key
   `ArgumentException` was actually being thrown one line later, inside `GetString(message, "type")`,
   which sat just *outside* the widened try/catch. That escaped exception killed the relay loop's
   task outright, which is exactly why `AllowListBypassHardeningTests`'s duplicate-key scenario kept
   timing out waiting for the post-frame liveness probe even after fix 3 landed -- the socket never
   processed another frame because the loop that reads it had already died. Fixed by moving the
   first property access (`GetString(message, "type")`) inside the same try block as `Parse`, on
   both the client-&gt;server and server-&gt;browser sides. Reproduced and confirmed in isolation with a
   throwaway `dotnet run probe.cs` script before touching the real code: `JsonNode.Parse` on a
   duplicate-key payload returns successfully, but the very next property access on the result
   throws `"An item with the same key has already been added"`.

All 5 `AllowListBypassHardeningTests` methods pass and are tagged after this fix (the other 4 had
apparently never been run against the tagged/untagged boundary before -- they turned out to already
be passing once actually attempted, unrelated to fix 4; only the duplicate-key one needed the code
change).

Also still gapped, believed to depend on #14's real tool-executor/order-state landing (not
investigated further this revision, `StubToolExecutor` is deliberately inert beyond the one
scripted `UpdateOrderToolCallTests` scenario): `HappyHourPricingTests`,
`PersonaBusinessRuleConformanceTests`, `PersonaSearchIsolationConformanceTests`,
`FixturePackPersonaSmokeTests`, `RealPackPersonaSmokeTests`. And believed to depend on #15 (session
resume): `CloseCodeTests`'s remaining 4002-supersede scenario, `IdleCloseCodeTests`,
`IdleTimeoutTests`, `VoicePickerTests`'s
`Resumed_*`/session-end methods. `WholeSessionLeakTests` (0/1, confirmed this revision: its own
doc comment requires a disconnect+resume and a silence nudge, i.e. #15's resume/rehydration
machinery -- not a bug, correctly deferred).
The `RateLimit` ladder (backoff retries of `response.create`, the `response.created` hook, the
greeting-retry interplay `EchoSuppressor` already models) is now implemented: Wave 4 of #13 ported
`rate_limit.py`'s `RateLimitRecovery` verbatim into
`app/backend-dotnet/src/Backend/Realtime/RateLimitRecovery.cs`, driven by the Wave-2 `TimeProvider`
seam, replacing the old one-shot `extension.rate_limited` notice (previously the only behaviour
here, per PR #140 round 2 / Rick's review) with the full silent-first-retry /
notify-non-final-then-final / guest-speech-resets-the-ladder behaviour. Scenarios that only exercise
the ladder itself are now tagged `Dotnet=ready` (see the PR that landed this change for the exact
list and count); `RateLimitIdleInteractionTests` remains untagged -- it additionally needs #15's
idle-timeout machinery, not yet ported. The **#17 go-live blocker** tag on the ladder is resolved by
this change. A handful of failures (`CapturedProcessOutputTests`, `CapturedProcessOutputWaitTests`,
`WindowsJobObjectTests`) are pre-existing harness self-tests unrelated to `CONFORMANCE_BACKEND` and
out of scope.

### Issue #15 (S5: C# sessions and resilience) -- session resume/rehydration, idle timeout, grace hold, nudge, 4002 supersede

Closes the gap this document previously tracked throughout the paragraphs above as "still #15" /
"believed to depend on #15". Ported `session_manager.py`'s resume-token issuance/consumption,
grace-window detach hold, idle-timeout close, rehydration-text generation, and the silent-guest nudge
(including #181's "arm only after the resumed socket's own `client`/`session.update`" rule) into new
`Sessions/SessionManager.cs` and `Realtime/NudgeScheduler.cs`, both driven by the injected
`TimeProvider` and using the same CTS-identity/generation-counter pattern from the #235 rate-limit
race fix for every scheduled timer, so no stale continuation can fire after cancellation. The
single-use *resume id* itself (the opaque baton handed out in `extension.session_metadata`/
`extension.session_resumed` that a reconnecting socket presents) always rotates on every hand-off --
no row anywhere asserts resume-id *content* continuity, and a used resume id is immediately unknown
(see `ResumeHandshakeTests`'s single-use and stray-late-resume rows). The underlying
`SessionIdentifiers` (`session_token`/`round_trip_index`/`round_trip_token`), by contrast, **do**
carry over from the original session on every successful resume (`RealtimeProcessor.cs` assigns
`state.Identifiers = outcome.Identifiers` from the resumed-from session) -- matching Python's own
continuity contract, and asserted directly by
`ResumeHandshakeTests.Resuming_carries_over_the_original_sessions_token_and_round_trip_state`.
Rehydration itself is carried via the server-held `SessionManager` state (transcript history -- now
populated by real `RecordTurn` call sites on both the guest and assistant turn-completion paths, see
above -- and voice/model bindings). Tool-failure
and rate-limit ladder state is deliberately **not** carried across a resume: a resumed connection
starts both trackers fresh, matching every scenario's observable behaviour (no row asserts ladder
state surviving a resume).

All target rows now pass against the real C# backend (confirmed 3+ clean runs) and are tagged
`Dotnet=ready`: the remaining `ResumeHandshakeTests` methods, all of
`ResumeRehydrationAndNudgeTests`, `ResumeSurvivesGraceWindowTests`, `IdleTimeoutTests` (including
`IdleCloseCodeTests`'s 4000 row and `CloseCodeTests`'s 4002-supersede row), the `VoicePickerTests`
and `ModelSelectionConformanceTests` resume rows, `ResumeRehydrationClientVisibilityTests`,
`ResumeMarginRegressionTests`, `WholeSessionLeakTests`, and `RateLimitIdleInteractionTests` (closing
the dependency called out two paragraphs above), plus five additional tests closing gaps from
Rick's #244 review (guest-speech idle activity, late-resume-after-timeout rejection, rehydrated
turn-text content, a stuck/never-acking peer during supersede, and `session_token`/round-trip
continuity across resume -- see this section's own notes above). `DotnetTraitCoverageTests`'s floor
raised 239 to 275 (a fresh reflection-based recount at this PR's own rebase point, not a projected
arithmetic delta -- see that file's own doc comment).

**Bug found and fixed via the conformance sweep (and self-corrected after an initial wrong fix)**:
`rtmt.py`'s `reject_late_resume()` re-announces fresh `extension.session_metadata` (with a newly
rotated resume id) after rejecting a late `extension.resume`, gated on the nonlocal `announced`
flag. A first reading of `rtmt.py` found only `announce_fresh()` (the fresh-connection path) setting
`announced = True` and concluded a resumed connection never sets it -- so the first C# fix gated the
re-announce on `MetadataAnnounced` being set *only* by the fresh-connection branch, and a new
conformance test was written asserting a resumed connection's stray late resume gets **no**
re-announce. That test passed against the C# backend but **failed when the same PR's CI ran it
against the real Python backend** (`CONFORMANCE_BACKEND=python`), because `handle_resume()`'s
*success* path (sending `extension.session_resumed`) also sets `announced = True`, immediately
before that send -- a line missed on the first read. So Python's `announced` doesn't distinguish
fresh vs. resumed; it tracks "has this socket's first-frame decision completed and a resume-id
baton been handed out," true on either path. `reject_late_resume()` therefore re-announces (with a
rotated id) for **any** connection already holding a baton, fresh or resumed alike -- it only stays
silent for a connection torn down before its first-frame decision ever completed. Corrected the C#
port: `RealtimeSessionState.MetadataAnnounced` is now also set `true` in the resumed branch of
`AnnounceAfterFirstFrameDecisionAsync`, right before sending `extension.session_resumed`, mirroring
`handle_resume()` exactly.

**Coverage gap found via mutation testing, closed with a corrected test (validated against both
backends)**: removing the `MetadataAnnounced` guard entirely (always re-announcing unconditionally)
did not fail any of the 42 rows above -- no existing scenario exercised a stray late resume *on an
already-resumed connection* specifically (the existing
`A_late_resume_attempt_is_rejected_and_the_session_continues` only covers the fresh-connection
case). Added (then corrected, per the paragraph above)
`ResumeHandshakeTests.A_stray_late_resume_on_an_already_resumed_connection_still_gets_a_rotated_re_announce`,
which performs a real resume, sends a second stray `extension.resume` on the resumed socket, asserts
the rejection still arrives, asserts a fresh `extension.session_metadata` frame **does** follow with
a rotated resume id different from the one issued at resume time, and that the socket stays open.
Confirmed this test fails when the `MetadataAnnounced` guard is removed entirely (mutation check)
and passes with it restored, against **both** backends: C# (3 clean full-suite runs of all 44 target
rows) and the real Python reference (44/44, including this test, run locally with
`CONFORMANCE_BACKEND=python`) -- closing the loop that the first (wrong) fix attempt had skipped.

ADR-002 (ready, dev 96b6f6f) adds Entra auth in front of `/realtime`; that's issue #147 (after #13)
and was explicitly out of scope this revision, but the WebSocket upgrade handler in
`RealtimeProcessor`/`Sessions/SessionActor.cs` keeps its existing pre-upgrade validation ordering
so a future auth check slots in ahead of persona/model resolution without restructuring.

`origin/dev` was merged into this branch this revision (merge commit `7e99761`, no rebase/force-push,
picking up `383e212`/`4a52af4` -- none of which touch files this issue's work modifies). The
`Dotnet=ready` count (72/72 at that point) and the full C# unit-test suite (230/230) were both
reconfirmed green after the merge; the duplicate-key fix (bug 4 above) and its 5 newly-tagged
`AllowListBypassHardeningTests` scenarios landed afterward, bringing the final count to 77/77.

### PR #140 round 2 (Rick's CHANGES REQUIRED review, R1-R7)

Beth was locked out for this round; applied by Unity. All seven items from Rick's review:

- **R1**: `RealtimeSessionBuilderTests.cs`'s two old-brand-name greeting strings (added since #153
  hardened the brand ratchet to scan `.cs` files) replaced with a neutral
  `"You are a drive-thru assistant."`; `origin/dev` (1e5fe29) merged cleanly.
- **R2**: `EchoSuppressor` and `SessionUpdateGuard` were mutated from both relay loops with no
  synchronization. Both now take a private `lock (_sync)` around every public member (state
  mutation under the lock, the flush send itself outside it in `EchoSuppressor`); doc comments
  updated from "not thread-safe by design" to describe the synchronization. New Barrier-released,
  10k-iteration stress tests for both. Mutation: removing the locks and running the tests 8 times
  fails `SessionUpdateGuard`'s `Stamp_And_Track_Are_Threadsafe_Against_Correlate_And_OnSessionUpdated`
  (6 of 8 runs), so only `SessionUpdateGuard`'s lock is mutation-pinned. `EchoSuppressor`'s stress
  tests are no-throw smoke tests: with the lock removed they still pass 8 of 8, because its shared
  state is compound bool/double updates where nothing throws, and `Close()` runs only after both
  loops drain. `EchoSuppressor`'s lock is accepted by inspection, not by mutation.
- **R3**: a duplicate key nested inside `session` (not just a top-level duplicate) parsed
  "successfully" as far as `JsonNode.Parse` was concerned -- `JsonObject`'s backing dictionary is
  lazy, so the failure surfaced later, outside the try/catch guarding the parse, killing the loop
  with nothing logged. Both relay loops now parse with
  `JsonDocumentOptions.AllowDuplicateProperties = false` (throws immediately, any depth) and wrap
  the rest of the per-frame body in its own try/catch that logs a warning with the session id
  (never the payload); `SwallowAsync` now logs non-cancellation exceptions at Error instead of
  discarding them. New nested-duplicate-key conformance case in `AllowListBypassHardeningTests`;
  round 2's N2 extracts the shared strict-parse helper (`Backend/Realtime/RelayJson.cs`) and adds
  `Backend.Tests/Realtime/RelayJsonTests.cs`, which is the actual mutation pin for the strict
  option, since the conformance case alone cannot tell whether strict parsing is on.
- **R4**: `WebSocketFrameReader` grew its `ArrayBufferWriter` without bound. Added a
  `maxMessageBytes` parameter (default 4 MiB, matching aiohttp's `WebSocketResponse` default
  `max_msg_size`); overflow closes with `WebSocketCloseStatus.MessageTooBig` (1009) via
  `CloseOutputAsync` (no close-handshake wait) and returns null. New `FakeWebSocket` test double
  and `WebSocketFrameReaderTests`; mutation-verified.
- **R5**: the upstream connect only ever sent `api-key`, and the deployed C# app has none
  configured (by design, #152's managed-identity RBAC). Added `Azure.Identity`; when the api-key
  is empty, `RealtimeProcessor` now acquires a bearer token per connect from
  `DefaultAzureCredential` (scope `https://cognitiveservices.azure.com/.default`, lazily
  constructed only if actually needed) and sends `Authorization: Bearer <token>` instead, matching
  rtmt.py's own fallback. Existing conformance scenarios are unaffected (their configured api-key
  keeps them on the unchanged api-key path). The class doc and inline comment that conflated this
  with #147 are corrected: #147 is the **inbound** Entra check on the browser-facing `/realtime`
  upgrade; this is the **outbound** credential for the upstream Azure OpenAI connection itself, and
  applies regardless of #147's status. New `UpstreamAuthHeaderTests` with a fake token provider;
  mutation-verified.
- **R6**: `DotnetTraitCoverageTests`'s floor raised from 11 to 75 (distinct methods; 78 result rows).
  The test counts tagged test *methods*: one `[Theory]`,
  `PersonaAssetRouteConformanceTests.Persona_asset_route_rejects_path_traversal_attempts`, has 4
  `[InlineData]` rows, so `dotnet test`'s own pass count for
  `--filter "Dotnet=ready&Category!=Browser"` is 78 result rows against the same 75 tagged methods.
- **R7**: PR body changed to `Refs #13` (was implicitly closing) and marked ready for review; a
  checklist of everything still cut or deferred posted on #13 (audio-append fast path and
  `TimeProvider` -- both explicit #13 acceptance items; the `RateLimit` ladder and the tool-failure
  cap `_ToolFailureTracker`, both flagged as **#17 go-live blockers**; context-window monitoring;
  heartbeat/dead-peer detection and connect timeout; the #14/#15 scenario lists above). The
  `RateLimit` scope-cut wording above was reworded from "out of scope for #13" (Rick: it isn't --
  #13's acceptance is all S1.2 scenarios green on both backends, and nothing blocks it) to
  "deferred, tracked on #13 (not blocked)", additionally cross-referenced to #15 (S5, already
  milestoned, already ports `rate_limit.py`) rather than filing a new duplicate issue.

## Traversal defense mutation-test note (PR #122 review item 1)

Rick's PR #122 review flagged that the pinned conformance rows were **blind on both backends**:
the first draft's three `InlineData` payloads all targeted `../../app.py` -> resolves to
`personas/app.py`, a path that does not exist -- so every row 404'd for "no such file" reasons
regardless of whether the resolver's traversal defense was even present. This was proven
empirically this revision, not just reasoned about: with the OLD rows, temporarily gutting
`PersonaAssetResolver`'s segment-rejection loop AND forcing its post-`GetFullPath` containment
check (`IsUnderRoot`) to always return `true` left all three rows green (still 404, now for the
right-shaped-wrong-reason: file-not-found instead of blocked-traversal).

Fixed by repointing every row at the default persona pack's own `persona.json` (one level above
`assets/` -- a file that DOES exist) and adding a fourth row, `..%5cpersona.json`, alongside the
existing literal `../persona.json`, `..%2fpersona.json`, and `..%2Fpersona.json`. Even so,
per-backend HTTP framework behavior means not all four rows are mutation-sensitive on both legs --
this is inherent to the two frameworks' routing, not a gap in the test:

- **Literal `../persona.json`**: both `HttpClient` (RFC 3986 dot-segment removal in the `Uri`
  constructor) and aiohttp's request-line normalization collapse this to the pack's own
  `persona.json` path one level up before it is ever routed -- no matching route on either
  backend, so it 404s for "no such route" reasons regardless of resolver logic. Structurally blind
  on **both** legs.
- **`%2f`/`%2F`**: ASP.NET Core routing deliberately never decodes `%2f`/`%2F` into a literal `/`
  in a route value (documented anti-ambiguity behavior) -- so on the **dotnet** leg the
  `assetPath` route value arrives as one opaque unsplit string that never matches a real file,
  blind regardless of the resolver's own checks. aiohttp's `match_info`, however, DOES decode
  `%2f` into a real `/`, so on the **Python** leg these rows exercise a genuine `..` segment and
  ARE mutation-sensitive.
- **`%5c`**: an ordinary percent-encoded byte (backslash isn't a URI path separator, so neither
  framework has a security reason to block decoding it) -- BOTH ASP.NET Core routing and aiohttp
  decode it to a literal backslash, which both resolvers' own segment-split logic then treats as a
  genuine `..` segment. This is the one row mutation-sensitive on **both** backends.

Confirmed empirically this revision, on live servers, with both baseline (clean, all-green) and
mutated runs, then a clean revert verified via `git status --porcelain`/`git diff --stat` showing
zero diff before recommitting:

- **Dotnet leg**: baseline 10/10 green. With the same `PersonaAssetResolver` mutation as above
  (segment-rejection loop gutted, `IsUnderRoot` forced `true`), exactly the `%5c` row went red
  (200 instead of 404); literal/`%2f`/`%2F` stayed green as predicted. Reverted, 10/10 green again.
- **Python leg**: baseline 10/10 green. With `app.py`'s `_resolve_persona_asset_path` mutated
  analogously (segment-rejection short-circuited, the `relative_to()` containment `ValueError`
  swallowed), `%2f`, `%2F`, AND `%5c` all went red (200); the literal row stayed green (it never
  reaches the handler). Reverted, 10/10 green again.

## Symlink containment mutation-test note (PR #122 review item 3)

Rick's PR #122 review flagged that `PersonaAssetResolver`'s containment check only re-verified the
FINAL resolved path stayed under the pack's assets root, not every path COMPONENT along the way --
so a symlinked directory placed inside `assets/` (pointing anywhere on disk) could let a
non-traversal-looking request escape the pack, since no segment-level check ever runs
`Path.GetFullPath`/`realpath` per component. Fixed by resolving the real path one component at a
time (`ResolveRealPath`/`TryResolveLinkTarget`) and re-checking containment after each hop, same as
Python's `os.path.realpath` applied to the joined path (which resolves every symlink in one call).

`Backend.Tests/Personas/PersonaAssetResolverTests.cs`'s new
`Symlinked_directory_inside_assets_cannot_escape_the_pack_root` test creates a real symlinked
directory -- the ONE symlink this revision creates anywhere, and only inside a throwaway directory
under `$env:TEMP`/`Path.GetTempPath()`, created and deleted by the test itself, never inside the
repo or worktree -- pointing outside the pack, then asserts a request through it resolves to
`null` rather than a live out-of-root path. Verified this is genuinely load-bearing, not
coincidentally passing: temporarily reverting to final-path-only containment checking made this
exact test fail as expected (a live out-of-root path was returned instead of `null`), while
same-class in-root-asset tests kept passing. Reverted before committing; confirmed via
`git status --porcelain` showing zero diff.

## Model dispatch/rejection body parity (PR #122 review items 2 and 4)

Rick's PR #122 review item 2: the `/realtime` model-rejection 404 body must match Python's
`Unknown or disallowed model: {model_id!r}` exactly, including the `None` (no quotes) form for a
`None` `requested_model_id` -- not a generic/summarized message. `Program.cs` now returns this
exact body via a small `PyRepr` helper (`'x'` for a string, bare `None` for `null`), logging the
detailed underlying reason (unknown catalog id vs. unregistered pipeline vs. disallowed vs.
undeployed) at warning level instead of exposing it to the client, mirroring `rtmt.py`'s own
except-clause behavior. Two new conformance rows in `ModelSelectionRejectionConformanceTests`
assert the exact body text over the wire against both backends (see the tagged-scenario list
above). The bare `None` form itself has no conformance row: it is provably unreachable at runtime
on either backend, since omitting `?model=` always resolves to the persona's OWN pipeline default,
and both backends' startup-time `ValidatePersonaDefaults`/`validate_persona_defaults` fail-fast
gate refuses to start at all if any enabled persona's own default isn't catalogued for its own
pipeline -- documented inline in the test file so this isn't mistaken for a coverage gap later.

Rick's PR #122 review item 4: a `SessionActorTests.cs` comment referenced a `ModelDispatchTests.cs`
file that did not exist. Added it (`Backend.Tests/Models/ModelDispatchTests.cs`, 6 tests) covering
`ModelDispatch.DispatchProcessor`/`ResolveRealtimeModel` directly: an unknown model throws, a
catalogued model with no registered processor throws, an omitted model dispatches to the persona's
own default, a default model with no deployment entry falls back to the legacy
`AZURE_OPENAI_REALTIME_DEPLOYMENT` env var and logs a warning, a NON-default model with no
deployment entry throws (does NOT get the fallback), and reasoning comes from the catalog entry.
Mutation-checked: temporarily removing the `modelId != pipelineCfg.Default` guard in
`ResolveRealtimeModel`'s fallback branch made exactly one test fail
(`A_non_default_model_with_no_deployment_throws`, "no exception was thrown"), with the other 5
staying green -- confirming that test (and only that test) is load-bearing for the guard. Reverted
before committing; confirmed via `git status --porcelain` showing zero diff. This also resolved the
stale comment: `RealtimeProcessor.ResolveModel` is a one-line delegation to
`ModelDispatch.ResolveRealtimeModel`, so `ModelDispatchTests.cs` alone gives full coverage and no
separate `RealtimeProcessorTests.cs` is needed; the comment now says so.
