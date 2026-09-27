using System.Net.WebSockets;
using Backend;
using Backend.Auth;
using Backend.Configuration;
using Backend.Health;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Microsoft.Extensions.FileProviders;

// Host wiring (issue #12 S2): config, persona-pack loading, health, auth token endpoint, static
// files, one event loop per session -- mirrors app/backend/app.py's create_app() startup sequence
// and route table (docs/dotnet_mapping.md). Persona/model per-session binding (#74/#75) is wired
// below: /realtime resolves persona+model and dispatches to a registered pipeline processor
// before the WebSocket upgrade; the realtime processor's own relay loop (owning frames after the
// upgrade) is issue #13's job, not this wave's.

var runningInProduction = ParseBool(Environment.GetEnvironmentVariable("RUNNING_IN_PRODUCTION"));

var builder = WebApplication.CreateBuilder(args);

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
PromptLoader promptLoader;
try
{
    var personasDir = Environment.GetEnvironmentVariable("PERSONAS_DIR") ?? Path.Combine(RepoRootLocator.Find(), "personas");
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

// ── 6. Processor registry (issue #75, design doc section 7.4): only "realtime" is registered
// this wave -- its own model resolution is fully ported (Models/ModelDispatch.cs's
// ResolveRealtimeModel), but ProcessAsync is a deliberate stub; the real upstream relay is #13. A
// model catalogued for "cascade"/"local" 404s at dispatch time until their own processors land. ──
var processorRegistry = new ProcessorRegistry();
processorRegistry.Register(new RealtimeProcessor(
    modelCatalog,
    Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_DEPLOYMENT")!,
    logger));

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
        return Results.Text($"Unknown or disabled persona: '{personaId}'", statusCode: 404);
    }
    var persona = personaCatalog.Get(personaId);

    // Model dispatch + resolution (issue #75): resolves which pipeline processor owns this
    // session, then validates the requested (or defaulted) model against that persona/pipeline.
    // Both stages 404 as plain text on failure, same as rtmt.py.
    var requestedModelId = context.Request.Query["model"].ToString();
    string pipelineName;
    IPipelineProcessor processor;
    ResolvedModel resolvedModel;
    try
    {
        (pipelineName, processor) = ModelDispatch.DispatchProcessor(
            persona, string.IsNullOrEmpty(requestedModelId) ? null : requestedModelId, modelCatalog, processorRegistry);
        resolvedModel = processor.ResolveModel(persona, string.IsNullOrEmpty(requestedModelId) ? null : requestedModelId);
    }
    catch (ModelSelectionException exc)
    {
        return Results.Text(exc.Message, statusCode: 404);
    }

    var sessionId = Guid.NewGuid().ToString("n");
    using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    var metadata = new SessionMetadata(persona.Id, resolvedModel.Id, pipelineName);
    var actor = sessionRegistry.GetOrAdd(sessionId, id => new SessionActor(id, processor, metadata));
    try
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted).ConfigureAwait(false);
                break;
            }
            actor.Post(new RawFrameEvent(buffer[..received.Count].ToArray(), received.MessageType));
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
        "to the repo root) - '/' will 404. Run `npm run build` in app/frontend first.");
}

app.Run();
return 0;

static bool ParseBool(string? value) =>
    value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

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
