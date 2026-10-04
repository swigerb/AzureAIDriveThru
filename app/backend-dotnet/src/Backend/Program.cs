using System.Net.WebSockets;
using Backend;
using Backend.Auth;
using Backend.Configuration;
using Backend.Health;
using Backend.Models;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Search;
using Backend.Sessions;
using Backend.Tools;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Console;

// Host wiring (issue #12 S2): config, persona-pack loading, health, auth token endpoint, static
// files, one event loop per session -- mirrors app/backend/app.py's create_app() startup sequence
// and route table (docs/dotnet_mapping.md). Persona/model per-session binding (#74/#75) is wired
// below: /realtime resolves persona+model and dispatches to a registered pipeline processor
// before the WebSocket upgrade; the realtime processor's own relay loop (owning frames after the
// upgrade) is issue #13's job, not this wave's.

var runningInProduction = ParseBool(Environment.GetEnvironmentVariable("RUNNING_IN_PRODUCTION"));

var builder = WebApplication.CreateBuilder(args);

if (ConformanceHooks.HooksEnabled)
{
    // #233: see ConformanceHooks.ApplyConsoleTimestampFormat's doc comment for the full
    // rationale, including PR #264 review's finding that Configure<SimpleConsoleFormatterOptions>
    // alone has no effect because CreateBuilder's default console registration leaves
    // ConsoleLoggerOptions.FormatterName unset. AddSimpleConsole both sets FormatterName to
    // "simple" (so these options are actually consulted) and reuses the existing
    // ConsoleLoggerProvider registration rather than adding a second one, so log lines still
    // aren't duplicated.
    builder.Logging.AddSimpleConsole(ConformanceHooks.ApplyConsoleTimestampFormat);
}

var host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
// Default port aligned with app/backend/app.py's `int(os.environ.get("PORT", 8000))` (PR #96
// review nit) -- both backends bind the same default when PORT is unset.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8000";
builder.WebHost.UseUrls($"http://{host}:{port}");

var startupChecks = new StartupChecks();

var app = builder.Build();
var logger = app.Logger;

// ── 1. Required environment variables (app.py's _REQUIRED_ENV_VARS) ────────────────────────
string[] requiredEnvVars =
[
    "AZURE_OPENAI_EASTUS2_ENDPOINT",
    "AZURE_OPENAI_REALTIME_DEPLOYMENT",
    "AZURE_SEARCH_ENDPOINT",
    "AZURE_SEARCH_INDEX",
];
var missingVars = requiredEnvVars.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
if (missingVars.Count > 0)
{
    logger.LogCritical("FATAL: Missing required environment variables: {Missing}", string.Join(", ", missingVars));
    return 1;
}
startupChecks.Pass("env_vars");

// ── 2. Persona pack catalog (issue #70/#12): refuse to start on an invalid pack ─────────────
PersonaCatalog personaCatalog;
try
{
    personaCatalog = PersonaCatalog.Load();
}
catch (PersonaValidationException exc)
{
    logger.LogCritical("FATAL: Failed to load persona pack(s) - {Message}", exc.Message);
    return 1;
}
startupChecks.Pass("personas_loaded");

// ── 3. Config (config.yaml) ──────────────────────────────────────────────────────────────────
AppConfig appConfig;
try
{
    appConfig = AppConfig.Load();
}
catch (ConfigValidationException exc)
{
    logger.LogCritical("FATAL: Failed to load config.yaml - {Message}", exc.Message);
    return 1;
}

// ── 4. Prompts for the default persona (Python: PromptLoader(brand="sonic") hardcoded; here
// persona-aware via the catalog's DefaultPersonaId -- per-session persona selection is wave 7) ──
var personasDir = Environment.GetEnvironmentVariable("PERSONAS_DIR") ?? Path.Combine(RepoRootLocator.Find(), "personas");
PromptLoader promptLoader;
try
{
    promptLoader = new PromptLoader(personasDir, personaCatalog.DefaultPersonaId);
}
catch (PromptLoadException exc)
{
    logger.LogCritical("FATAL: Failed to load prompts - {Message}", exc.Message);
    return 1;
}
startupChecks.Pass("prompts_loaded");

// ── 5. Model catalog (issue #75, design doc section 7.2): config.yaml's models.catalog +
// AZURE_AI_MODEL_DEPLOYMENTS. Fail-fast if any enabled persona's own pipeline default isn't
// catalogued for that pipeline (Rick's PR #106 review item 1) -- an unusable default should stop
// startup, not surface as a confusing 404 on the first ?model=-omitted request. ──────────────
ModelCatalog modelCatalog;
try
{
    modelCatalog = ModelCatalog.FromConfig(appConfig);
    modelCatalog.ValidatePersonaDefaults(personaCatalog, logger);
}
catch (ModelValidationException exc)
{
    logger.LogCritical("FATAL: Failed to load model catalog - {Message}", exc.Message);
    return 1;
}
// No dedicated StartupChecks entry: app.py's own `_startup_checks` dict (Health/StartupChecks.cs's
// port) has no "model catalog" key either -- reaching this line at all already proves the catalog
// loaded (same "validated at module load" reasoning as config_loaded), and /health's shape must
// stay byte-for-byte identical to Python's, so no new field is added here.

// ── 5b. Realtime session config (issue #13): config.yaml's `model`/`audio` sections plus their
// env overrides, exactly mirroring rtmt.py's configure_realtime_model. ─────────────────────────
var modelSection = appConfig.TryGetSection("model");
var audioSection = appConfig.TryGetSection("audio");
var realtimeDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_DEPLOYMENT")!;

var voiceChoiceOverride = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_VOICE_CHOICE");
var voiceChoice = !string.IsNullOrEmpty(voiceChoiceOverride) ? voiceChoiceOverride : ReadString(modelSection, "default_voice") ?? "marin";

var transcriptionModelOverride = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL");
var transcriptionModel = !string.IsNullOrEmpty(transcriptionModelOverride) ? transcriptionModelOverride : ReadString(modelSection, "transcription_model") ?? "whisper-1";

var reasoningEffortOverride = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_REASONING_EFFORT");
var reasoningEffortSource = !string.IsNullOrEmpty(reasoningEffortOverride) ? reasoningEffortOverride : ReadString(modelSection, "reasoning_effort");
var reasoningEffort = ReasoningRules.NormalizeReasoningEffort(reasoningEffortSource);

var reasoningModelOverride = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_REASONING_MODEL");
var reasoningModelSource = !string.IsNullOrEmpty(reasoningModelOverride) ? reasoningModelOverride : ReadString(modelSection, "reasoning_model");
var reasoningModel = ReasoningRules.ParseReasoningModel(reasoningModelSource);

var configuredVoices = ReadStringList(modelSection, "allowed_voices");
IReadOnlySet<string> allowedVoices = configuredVoices is { Count: > 0 }
    ? new HashSet<string>(configuredVoices, StringComparer.Ordinal)
    : ClientServerFilter.DefaultAllowedVoices;
if (!allowedVoices.Contains(voiceChoice))
{
    // rtmt.py's configure_realtime_model raises ValueError for exactly this at startup (#57 FU2)
    // -- a default voice GA would reject on every bootstrap session.update is a fail-fast, not a
    // per-session error.
    logger.LogCritical(
        "FATAL: The default voice '{Voice}' (AZURE_OPENAI_REALTIME_VOICE_CHOICE / model.default_voice) is not in model.allowed_voices ({AllowedVoices})",
        voiceChoice, string.Join(", ", allowedVoices.OrderBy(v => v, StringComparer.Ordinal)));
    return 1;
}

var sessionConfig = new RealtimeSessionConfig
{
    Deployment = realtimeDeployment,
    // Deliberately left null: rtmt.py's per-message session.update rebuild
    // (_process_message_to_server) omits system_message too, relying on GA to keep the
    // bootstrap value -- the persona system prompt is only ever passed explicitly to the
    // bootstrap session.update builder call inside RealtimeProcessor.RunSessionAsync.
    SystemMessage = null,
    Temperature = ReadDouble(modelSection, "temperature") ?? 0.6,
    MaxTokens = ReadInt(modelSection, "max_response_output_tokens") ?? 4096,
    VoiceChoice = voiceChoice,
    TranscriptionModel = transcriptionModel,
    ReasoningEffort = reasoningEffort,
    ParallelToolCalls = ReadBool(modelSection, "parallel_tool_calls"),
    ReasoningModel = reasoningModel,
};
if (sessionConfig.ReasoningEffort is not null && !sessionConfig.IsReasoningModel(Overridable<bool?>.Unset))
{
    logger.LogInformation(
        "Deployment {Deployment} is not treated as a reasoning model (reasoning_model={ReasoningModel}); `reasoning` (effort={Effort}) will not be sent",
        realtimeDeployment, reasoningModel is null ? "auto" : reasoningModel.Value.ToString(), sessionConfig.ReasoningEffort);
}

// ── 5c. Per-persona prompts + this session's own SessionToolExecutor (issue #13/#14 coordination
// seam, now landed): `toolExecutorFactory` below builds one `SessionToolExecutor` per session,
// once persona binding resolves, composing that session's own `OrderToolExecutor` (its own fresh
// `OrderState`, never shared) and `SearchTool`. `toolExecutor` (the shared `StubToolExecutor`)
// stays wired as the fallback the factory is layered over -- see RealtimeProcessor's constructor
// doc -- so a persona with no catalogued tool schemas still gets a harmless default. ─────────────
var promptLoaders = new Dictionary<string, PromptLoader>(StringComparer.Ordinal)
{
    [personaCatalog.DefaultPersonaId] = promptLoader,
};
var allToolNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var personaId in personaCatalog.Ids)
{
    if (!promptLoaders.TryGetValue(personaId, out var loader))
    {
        loader = new PromptLoader(personasDir, personaId);
        promptLoaders[personaId] = loader;
    }
    foreach (var schema in loader.ToolSchemas)
    {
        if (schema.TryGetValue("name", out var nameObj) && nameObj?.ToString() is { Length: > 0 } toolName)
        {
            allToolNames.Add(toolName);
        }
    }
}
var toolExecutor = new StubToolExecutor(allToolNames);

// #14: order/search domain wiring -- one shared HttpClient (thread-safe for concurrent use,
// reused across every session's own SearchTool instance, mirroring the realtime relay's own
// single upstream endpoint config being shared while OrderState/MenuCatalog stay session-bound).
var businessRulesConfig = BusinessRulesConfig.FromAppConfig(appConfig);
var searchConfig = SearchConfig.FromAppConfig(appConfig);
var searchEndpointConfig = SearchEndpointConfig.FromEnvironment();
var searchHttpClient = new HttpClient();

IToolExecutor BuildSessionToolExecutor(Persona sessionPersona, PromptLoader? sessionPromptLoader, string? sessionMenuMode)
{
    var menu = PersonaOrderFactory.GetMenuCatalog(sessionPersona);
    var orderState = PersonaOrderFactory.CreateOrderState(sessionPersona);
    var orderTools = new OrderToolExecutor(
        orderState, menu, sessionPromptLoader, businessRulesConfig.MaxItemQuantity, businessRulesConfig.MaxOrderItems,
        sessionMenuMode);
    var searchTool = new SearchTool(
        searchHttpClient, searchEndpointConfig, searchConfig, menu, sessionPromptLoader,
        sessionPersona.Search.IndexName, sessionPersona.Id, bearerTokenProvider: null, logger: logger,
        menuMode: sessionMenuMode);
    return new SessionToolExecutor(orderTools, searchTool);
}

var upstreamEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_EASTUS2_ENDPOINT")!;
var upstreamApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_EASTUS2_API_KEY") ?? string.Empty;
var echoCooldownSeconds = ReadDouble(audioSection, "echo_cooldown_seconds") ?? 1.5;
// Issue #13 Wave 2: the realtime relay's one clock (echo-suppression cooldowns, the greeting-gate
// timeout, NowSeconds()) -- a single TimeProvider.System instance, passed straight into
// RealtimeProcessor below. Tests construct RealtimeProcessor directly with their own
// FakeTimeProvider instead, so no DI container registration is needed here.
var timeProvider = TimeProvider.System;
// Issue #13 Wave 4: the rate-limit retry ladder's own config (resilience.rate_limit in
// config.yaml, RATE_LIMIT_RECOVERY_ENABLED env override) -- Beth's #147 auth wiring in this same
// file is unrelated to this, kept as a minimal one-line addition plus the matching constructor arg
// below.
var rateLimitSettings = RateLimitSettings.FromAppConfig(appConfig);

// ── 6. Processor registry (issue #75, design doc section 7.4): only "realtime" is registered
// this wave -- its own model resolution is fully ported (Models/ModelDispatch.cs's
// ResolveRealtimeModel). Issue #13 lands the real upstream relay (RunSessionAsync, called
// directly from the /realtime handler below); ProcessAsync stays a deliberate stub since the
// realtime pipeline never posts to a session's generic mailbox. A model catalogued for
// "cascade" 404s at dispatch time until its own processor lands (#155 dropped "local" entirely,
// so it is no longer a pipeline at all). ──────────────────────────────────────────────────────
var processorRegistry = new ProcessorRegistry();
var realtimeProcessor = new RealtimeProcessor(
    modelCatalog,
    realtimeDeployment,
    upstreamEndpoint,
    upstreamApiKey,
    sessionConfig,
    promptLoaders,
    toolExecutor,
    allowedVoices,
    echoCooldownSeconds,
    logger: logger,
    toolExecutorFactory: BuildSessionToolExecutor,
    timeProvider: timeProvider,
    rateLimitSettings: rateLimitSettings);
// PR #140 R5: bearerTokenProvider is left at its default (null) here deliberately --
// RealtimeProcessor.ResolveUpstreamAuthHeaderAsync falls back to the lazily-constructed real
// DefaultAzureCredentialTokenProvider itself, so a DefaultAzureCredential (which probes several
// credential sources) is only ever actually constructed for a connection that has no api-key
// configured and genuinely needs a managed-identity token. SearchTool's own bearer fallback
// (DefaultAzureCredentialSearchTokenProvider) is analogously lazy, constructed inside
// BuildSessionToolExecutor's SearchTool only if AZURE_SEARCH_API_KEY is unset.
processorRegistry.Register(realtimeProcessor);

// ── 6b. Cascade processor (issue #13 Wave 5 / #82): STT -> chat-completions-with-tools -> TTS,
// for personas/models that opt into config.yaml's `models.cascade` instead of the Realtime API.
// Reuses the SAME `promptLoaders`/`toolExecutor`/`BuildSessionToolExecutor` factory realtime does
// (above) so tool/order/search behavior is identical regardless of pipeline. `foundryEndpoint` is
// genuinely optional here (app.py's own `os.environ.get("AZURE_AI_FOUNDRY_ENDPOINT")` -- no
// fail-fast): a deployment that hasn't registered any `models.cascade` entry yet simply never
// dispatches a session to this processor (ModelDispatch 404s first), so an empty endpoint is
// harmless until a persona/model actually selects cascade. `audioEndpoint` reuses the SAME
// `AZURE_OPENAI_EASTUS2_ENDPOINT` realtime already requires (app.py's own `audio_endpoint=
// llm_endpoint`), and `defaultVoice` reuses the SAME `voiceChoice` resolved above (app.py's
// `AZURE_OPENAI_REALTIME_VOICE_CHOICE` override or `model.default_voice`) -- one voice default for
// both pipelines. A dedicated HttpClient (not the shared `searchHttpClient`) keeps cascade's own
// outbound REST calls (chat/STT/TTS) isolated from search's.
var cascadeHttpClient = new HttpClient();
// Mirrors app.py's own `conformance_hooks.cascade_credential() or AsyncDefaultAzureCredential()`:
// a non-null ConformanceHooks.CascadeFakeToken (CONFORMANCE_TEST_HOOKS=1 AND
// CONFORMANCE_CASCADE_FAKE_TOKEN set) substitutes the fixed-token StaticBearerTokenProvider so
// cascade's chat/STT/TTS REST calls can be exercised against the conformance harness's fakes with
// no real Azure AD identity available; otherwise CascadeProcessor's own default (bearerTokenProvider:
// null) lazily falls back to the real DefaultAzureCredentialTokenProvider exactly as before.
var cascadeBearerTokenProvider = ConformanceHooks.CascadeFakeToken is { } cascadeFakeToken
    ? new StaticBearerTokenProvider(cascadeFakeToken)
    : null;
var cascadeProcessor = new CascadeProcessor(
    modelCatalog,
    Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT") ?? string.Empty,
    upstreamEndpoint,
    appConfig,
    promptLoaders,
    toolExecutor,
    cascadeHttpClient,
    allowedVoices,
    voiceChoice,
    logger: logger,
    bearerTokenProvider: cascadeBearerTokenProvider,
    toolExecutorFactory: BuildSessionToolExecutor,
    timeProvider: timeProvider);
processorRegistry.Register(cascadeProcessor);

var assetCacheConfig = AssetCacheConfig.FromConfig(appConfig);

logger.LogInformation(
    "Startup validation passed: personas={Personas}, prompts loaded ({Chars} chars), config valid, {Count}/{Count} env vars set",
    string.Join(", ", personaCatalog.Ids), promptLoader.SystemPrompt.Length, requiredEnvVars.Length, requiredEnvVars.Length);

// ── Session token service (rtmt.py's create_hmac_token/validate_hmac_token) ─────────────────
var appSecret = AppSecretProvider.Load(app.Configuration, logger, runningInProduction);
var tokenService = new SessionTokenService(appSecret);

// config.yaml's `security` section (rtmt.py's module-level `_security_cfg`) -- gates the
// /realtime Origin and session-token checks below.
var securityConfig = SecurityConfig.FromConfig(appConfig);

var sessionRegistry = new SessionRegistry();

// ── Routes ───────────────────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => HealthEndpoint.Handle(startupChecks, personaCatalog));

app.MapGet("/api/auth/session", () => Results.Json(new { token = tokenService.Create(expirySeconds: 900) }));

// `/api/personas`, `/api/personas/{id}`, `/personas/{id}/menu.json`,
// `/personas/{id}/assets/{*assetPath}` (issue #74/#12, design doc section 5.2): the persona
// discovery/asset HTTP surface, matching the wire contract Python defines and conformance pins.
// See Personas/PersonaRoutes.cs for the ported route handlers.
PersonaRoutes.Map(app, personaCatalog, modelCatalog, assetCacheConfig);

app.UseWebSockets();

// One sequential event loop per session (issue #12), reachable at /realtime. Persona+model
// binding (issue #74/#75) happens once, before the WebSocket upgrade -- an unknown/disabled
// persona or an unselectable model 404s here exactly like rtmt.py's `_websocket_handler`, in the
// same plain-text (not JSON) shape, distinct from the JSON 404s the HTTP persona routes above
// return. The resolved processor's ProcessAsync (RealtimeProcessor is a stub this wave -- #13
// lands the real upstream relay) then owns the session's mailbox loop as before.
app.MapGet("/realtime", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest();
    }

    // Pre-upgrade auth gate (PR #96 review, required item 1): Origin validation, then (only when
    // config.yaml's security.require_session_token is true) HMAC session-token validation --
    // same order and rejection statuses as rtmt.py's _websocket_handler. See
    // Realtime/RealtimeAuthGate.cs for the ported logic and its own unit tests, and
    // tests/conformance's Scenarios/Http and Scenarios/Security OriginValidationTests for the
    // over-the-wire proof against this backend.
    var rejection = RealtimeAuthGate.Check(
        context.Request.Headers["Origin"].ToString(),
        context.Request.Headers["Host"].ToString(),
        context.Request.Query["token"].ToString(),
        securityConfig,
        tokenService,
        logger);
    if (rejection is not null)
    {
        return rejection;
    }

    // Persona resolution (issue #74): omitted ?persona= binds to DEFAULT_PERSONA; unknown/disabled
    // 404s as plain text -- rtmt.py's _websocket_handler rejects here with `web.Response(status=
    // 404, text=...)`, not the JSON shape the HTTP persona routes use, since this is still a
    // pre-upgrade HTTP response on the /realtime endpoint itself, not a persona-discovery route.
    var requestedPersonaId = context.Request.Query["persona"].ToString();
    var personaId = string.IsNullOrEmpty(requestedPersonaId) ? personaCatalog.DefaultPersonaId : requestedPersonaId;
    if (!personaCatalog.Contains(personaId))
    {
        // rtmt.py's `_websocket_handler` logs this rejection at warning level before returning
        // the 404 (Rick's PR #122 review item 2) -- mirrored here, not just the response body.
        logger.LogWarning("Rejected WebSocket for unknown/disabled persona: {PersonaId}", personaId);
        return Results.Text($"Unknown or disabled persona: '{personaId}'", statusCode: 404);
    }
    var persona = personaCatalog.Get(personaId);

    // Menu mode binding (issue 165): only a persona that declares features.dayparts has a menu
    // mode at all -- a persona without that feature never shows a toggle in Settings, so a stray
    // ?mode= for it is silently ignored here (forced to null) rather than rejected, since there
    // is no reachable client path that would ever legitimately send one. For a
    // dayparts-declaring persona, an explicit, unrecognized ?mode= value IS rejected loudly --
    // same "no silent fallback" philosophy as persona/model above -- while an OMITTED ?mode=
    // defaults to "lunch" (the original reference app's own default, decision D3) further down,
    // not here. TryGetValue (not Query["mode"].ToString()) preserves the distinct "absent" vs
    // "present but empty" cases, same as requestedModelId below.
    var requestedMenuMode = context.Request.Query.TryGetValue("mode", out var modeQueryValues)
        ? modeQueryValues.ToString()
        : null;
    if (persona.Features.Dayparts)
    {
        if (requestedMenuMode is not (null or "breakfast" or "lunch"))
        {
            // Rick's PR 166 round-1 review, required item 7: never log the raw, attacker-supplied
            // ?mode= value verbatim -- it is unbounded length and may carry CR/LF as a
            // log-injection attempt. Only the persona id and the value's length go to the log;
            // the 400 response body below still echoes the value via PyRepr, which is fine (same
            // as the persona/model 404s above), since that goes out over HTTP to the same client
            // that sent it, not into the shared log stream.
            logger.LogWarning(
                "Rejected WebSocket for invalid menu mode (length={MenuModeLength}, persona={PersonaId})",
                requestedMenuMode?.Length ?? 0, persona.Id);
            return Results.Text(
                $"Invalid menu mode: {PyRepr(requestedMenuMode)} (expected 'breakfast' or 'lunch')", statusCode: 400);
        }
    }
    else
    {
        requestedMenuMode = null;
    }
    // Default normalization (decision D3): an omitted/valid ?mode= for a dayparts-declaring
    // persona defaults to "lunch" here (there's no separate session-creation step to defer this
    // to, unlike the Python port) -- a persona with no features.dayparts stays null, so
    // OrderToolExecutor/SearchTool's own menu-mode gates/filters are a pure no-op for it.
    var menuMode = persona.Features.Dayparts ? (requestedMenuMode ?? "lunch") : null;

    // Model dispatch + resolution (issue #75): resolves which pipeline processor owns this
    // session, then validates the requested (or defaulted) model against that persona/pipeline.
    // Both stages 404 as plain text on failure, same as rtmt.py.
    //
    // Rick's PR #122 review item 2: `requested_model_id` is `None` only when `?model=` is
    // genuinely absent from the query string -- an explicit `?model=` (empty value) is a real,
    // distinct model id to Python (`requested_model_id if requested_model_id is not None else
    // default`, processors.py), not treated the same as omitted. `Query["model"].ToString()`
    // collapses both cases to `""`, so read via `TryGetValue` to preserve the distinction, same
    // as `request.query.get("model")` returning `None` vs `""`.
    var requestedModelId = context.Request.Query.TryGetValue("model", out var modelQueryValues)
        ? modelQueryValues.ToString()
        : null;
    string pipelineName;
    IPipelineProcessor processor;
    ResolvedModel resolvedModel;
    try
    {
        (pipelineName, processor) = ModelDispatch.DispatchProcessor(persona, requestedModelId, modelCatalog, processorRegistry);
    }
    catch (ModelSelectionException exc)
    {
        // Rick's PR #122 review item 2: the 404 BODY is always the fixed, Python-matching shape
        // below -- rtmt.py's own dispatch_processor except-clause returns the identical body
        // regardless of the underlying reason (unknown model vs. an unregistered pipeline for a
        // catalogued one); the detailed reason goes to the log, not the client.
        logger.LogWarning(
            "Rejected WebSocket for unknown model / unregistered pipeline: {ModelId} ({Reason})",
            requestedModelId, exc.Message);
        return Results.Text($"Unknown or disallowed model: {PyRepr(requestedModelId)}", statusCode: 404);
    }

    try
    {
        resolvedModel = processor.ResolveModel(persona, requestedModelId);
    }
    catch (ModelSelectionException exc)
    {
        // Same fixed body/log split as the dispatch-stage catch above, mirroring rtmt.py's
        // second `except ModelSelectionError` clause (resolve_model, not dispatch_processor).
        logger.LogWarning(
            "Rejected WebSocket for unknown/disallowed model: {ModelId} ({Reason})",
            requestedModelId, exc.Message);
        return Results.Text($"Unknown or disallowed model: {PyRepr(requestedModelId)}", statusCode: 404);
    }

    var sessionId = Guid.NewGuid().ToString("n");
    using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    var metadata = new SessionMetadata(persona.Id, resolvedModel.Id, pipelineName);
    var actor = sessionRegistry.GetOrAdd(sessionId, id => new SessionActor(id, processor, metadata));
    try
    {
        if (processor is RealtimeProcessor realtimeProc)
        {
            // Issue #13: the realtime pipeline owns its own bidirectional relay directly (session
            // bootstrap, voice lock, greeting gate, tool-call dispatch, echo suppression/barge-in,
            // rejected-session-update recovery, rate-limit notice) instead of draining the generic
            // per-session mailbox -- see RealtimeProcessor.RunSessionAsync's own doc comment.
            await realtimeProc.RunSessionAsync(socket, persona, resolvedModel, sessionId, context.RequestAborted, menuMode)
                .ConfigureAwait(false);
        }
        else if (processor is CascadeProcessor cascadeProc)
        {
            // Issue #13 Wave 5: the cascade pipeline likewise owns its own session loop directly
            // (greeting, local VAD-driven turn detection, STT -> chat-tool-loop -> TTS, barge-in,
            // rate-limit ladder) instead of draining the generic per-session mailbox -- see
            // CascadeProcessor.RunSessionAsync's own doc comment.
            await cascadeProc.RunSessionAsync(socket, persona, resolvedModel, sessionId, context.RequestAborted, menuMode)
                .ConfigureAwait(false);
        }
        else
        {
            // #13 (Rick's #12 review note): reassemble fragmented frames instead of the previous
            // single-ReceiveAsync-into-a-4096-byte-buffer loop, which silently truncated/misdelivered
            // any message spanning multiple WebSocket frames or exceeding 4096 bytes. See
            // Realtime/WebSocketFrameReader.cs. Only reachable once a non-realtime pipeline (e.g.
            // "cascade") registers its own processor -- none does yet.
            while (socket.State == WebSocketState.Open)
            {
                var frame = await WebSocketFrameReader.ReadMessageAsync(socket, context.RequestAborted).ConfigureAwait(false);
                if (frame is null)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted).ConfigureAwait(false);
                    break;
                }
                actor.Post(new RawFrameEvent(frame.Payload, frame.MessageType));
            }
        }
    }
    finally
    {
        await sessionRegistry.RemoveAsync(sessionId).ConfigureAwait(false);
    }

    return Results.Empty;
});

// ── Static files (shared with the Python backend -- frontend unchanged by the port) ─────────
var staticDir = Environment.GetEnvironmentVariable("STATIC_FILES_DIR")
    ?? TryFindStaticDir();
if (staticDir is not null && Directory.Exists(staticDir))
{
    var fileProvider = new PhysicalFileProvider(staticDir);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider, RequestPath = "" });
    app.MapGet("/", (HttpContext context) =>
    {
        var indexPath = Path.Combine(staticDir, "index.html");
        if (!File.Exists(indexPath))
        {
            return Results.NotFound();
        }
        // Matches app/backend/app.py's _index_handler: the shell itself is always revalidated so a
        // stale cached copy never masks a new deploy, unlike the hashed JS/CSS bundles served by
        // UseStaticFiles above (those get the browser's normal caching).
        context.Response.Headers.CacheControl = "no-cache";
        return Results.File(indexPath, "text/html");
    });
}
else
{
    logger.LogWarning(
        "Static files directory not found (STATIC_FILES_DIR unset and no app/backend/static next " +
        "to the repo root) - '/' will 404. Run `VITE_AUTH_MODE=Development npm run build` in app/frontend first.");
}

app.Run();
return 0;

static bool ParseBool(string? value) =>
    value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

// config.yaml section readers for issue #13's RealtimeSessionConfig wiring: YamlDotNet's untyped
// Deserialize<object?>() returns every scalar as a plain string (Configuration/SecurityConfig.cs's
// doc comment has the same gotcha), so these all parse from string rather than pattern-matching a
// native numeric/bool type. A YAML `null`/absent key/absent section all fall through to `null`,
// matching Python's dict.get(key) returning None for the same three cases.
static string? ReadString(IDictionary<object, object>? section, string key) =>
    section is not null && section.TryGetValue(key, out var raw) && raw is not null ? raw.ToString() : null;

static double? ReadDouble(IDictionary<object, object>? section, string key) =>
    section is not null && section.TryGetValue(key, out var raw) && raw is not null
        ? Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture)
        : null;

static int? ReadInt(IDictionary<object, object>? section, string key) =>
    section is not null && section.TryGetValue(key, out var raw) && raw is not null
        ? Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture)
        : null;

static bool? ReadBool(IDictionary<object, object>? section, string key)
{
    if (section is null || !section.TryGetValue(key, out var raw) || raw is null)
    {
        return null;
    }
    return raw is bool boolValue ? boolValue : bool.Parse(raw.ToString()!);
}

static List<string>? ReadStringList(IDictionary<object, object>? section, string key)
{
    if (section is null || !section.TryGetValue(key, out var raw) || raw is not IEnumerable<object> list)
    {
        return null;
    }
    return list.Select(v => v?.ToString() ?? string.Empty).ToList();
}

// Rick's PR #122 review item 2: matches Python's `!r` for the two shapes `requested_model_id`
// can take here -- `None` (bare, no quotes) when `?model=` was absent, `'value'` (single-quoted)
// for any string value, including `''` for an explicit but empty `?model=`.
static string PyRepr(string? value) => value is null ? "None" : $"'{value}'";

static string? TryFindStaticDir()
{
    try
    {
        return Path.Combine(RepoRootLocator.Find(), "app", "backend", "static");
    }
    catch (InvalidOperationException)
    {
        return null;
    }
}

/// <summary>Placeholder mailbox event: the raw bytes of one WebSocket frame. Wave 3+ (#13)
/// replaces this with real, typed protocol events once a pipeline actually parses them.</summary>
internal sealed record RawFrameEvent(byte[] Payload, WebSocketMessageType MessageType) : SessionEvent;

/// <summary>Exposes Program's top-level statements to WebApplicationFactory-based integration
/// tests (Microsoft.AspNetCore.Mvc.Testing requires a public partial Program type).</summary>
public partial class Program;
