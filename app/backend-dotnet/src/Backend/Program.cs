using System.Net.WebSockets;
using Backend;
using Backend.Auth;
using Backend.Configuration;
using Backend.Health;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Microsoft.Extensions.FileProviders;

// Host wiring (issue #12 S2): config, persona-pack loading, health, auth token endpoint, static
// files, one event loop per session -- mirrors app/backend/app.py's create_app() startup sequence
// and route table (docs/dotnet_mapping.md). Persona/model per-session binding is wave 7 (#74/#75)
// -- /realtime here only proves the SessionActor mechanics, it does not yet route to any
// persona-specific pipeline.

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

// `/api/personas` is deliberately NOT mapped this wave (PR #96 review, required item 2): the
// shape sketched here (`{personas: [ids], defaultPersona, models}`) diverges from the wire
// contract pinned in docs/persona-architecture.md section 5.2
// (`{default, personas: [{id, displayName, logoUrl, theme}], backends}`). Python is the
// reference and defines the endpoint first (#74), with conformance pinning its exact shape;
// this backend follows in the persona-binding half of #12 rather than shipping a
// divergent shape that would become a second contract.

app.UseWebSockets();

// One sequential event loop per session (issue #12), reachable at /realtime. No pipeline is
// bound yet (wave 7 seam) -- this proves the actor/registry mechanics, not any relay behaviour.
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

    var sessionId = Guid.NewGuid().ToString("n");
    using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    var actor = sessionRegistry.GetOrAdd(sessionId, id => new SessionActor(id));
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
