/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for Program.cs's top-level startup
/// sequence and `/realtime` pre-upgrade handler, avoiding CA1848/CA1873 allocations. EventIds
/// 8000-8999 are reserved for this file; message templates, levels and placeholder names are
/// copied verbatim from the call sites they replace -- the conformance suite matches on log text,
/// so none of that may change.
///
/// Lives in the global namespace alongside the generated <c>Program</c> type, so method names are
/// deliberately specific: extension methods declared here are in scope for every file in the
/// assembly, and must never collide with a <c>Backend.*.Log</c> method of the same shape.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 8000, Level = LogLevel.Warning, Message =
        "AUTH_MODE is unconfigured: running with a SYNTHETIC Entra identity (oid={Oid}). Every " +
        "request is treated as this principal. This is the Development pass-through -- it must " +
        "NEVER run in Production. Set AUTH_MODE=Entra with valid ENTRA_TENANT_ID and " +
        "ENTRA_CLIENT_ID for a real deployment.")]
    public static partial void StartupDevelopmentAuthMode(this ILogger logger, string oid);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Auth mode: Entra (tenant={TenantId}, issuer={Issuer})")]
    public static partial void StartupEntraAuthMode(this ILogger logger, string? tenantId, string issuer);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Critical, Message = "FATAL: Missing required environment variables: {Missing}")]
    public static partial void StartupMissingEnvironmentVariables(this ILogger logger, string missing);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Critical, Message = "FATAL: Failed to load persona pack(s) - {Message}")]
    public static partial void StartupPersonaPackLoadFailed(this ILogger logger, string message);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Critical, Message = "FATAL: Failed to load config.yaml - {Message}")]
    public static partial void StartupConfigLoadFailed(this ILogger logger, string message);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Critical, Message = "FATAL: Failed to load prompts - {Message}")]
    public static partial void StartupPromptsLoadFailed(this ILogger logger, string message);

    [LoggerMessage(EventId = 8006, Level = LogLevel.Critical, Message = "FATAL: Failed to load model catalog - {Message}")]
    public static partial void StartupModelCatalogLoadFailed(this ILogger logger, string message);

    [LoggerMessage(EventId = 8007, Level = LogLevel.Critical, Message = "FATAL: {Message}")]
    public static partial void StartupServiceGraphFailed(this ILogger logger, string message);

    // The template repeats {Count} (the configured and required env-var counts are the same
    // number by construction), so one parameter feeds both placeholders.
    [LoggerMessage(EventId = 8008, Level = LogLevel.Information, Message =
        "Startup validation passed: personas={Personas}, prompts loaded ({Chars} chars), config valid, {Count}/{Count} env vars set")]
    public static partial void StartupValidationPassed(this ILogger logger, string personas, int chars, int count);

    [LoggerMessage(EventId = 8009, Level = LogLevel.Warning, Message =
        "Static files directory not found (STATIC_FILES_DIR unset and no app/backend/static next " +
        "to the repo root) - '/' will 404. Run `VITE_AUTH_MODE=Development npm run build` in app/frontend first.")]
    public static partial void StartupStaticFilesDirectoryNotFound(this ILogger logger);

    [LoggerMessage(EventId = 8010, Level = LogLevel.Warning, Message = "Rejected WebSocket for unknown/disabled persona: {PersonaId}")]
    public static partial void RealtimeRejectedUnknownPersona(this ILogger logger, string personaId);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Warning, Message =
        "Rejected WebSocket for invalid menu mode (length={MenuModeLength}, persona={PersonaId})")]
    public static partial void RealtimeRejectedInvalidMenuMode(this ILogger logger, int menuModeLength, string personaId);

    [LoggerMessage(EventId = 8012, Level = LogLevel.Warning, Message =
        "Rejected WebSocket for unknown model / unregistered pipeline: {ModelId} ({Reason})")]
    public static partial void RealtimeRejectedUnregisteredPipeline(this ILogger logger, string? modelId, string reason);

    [LoggerMessage(EventId = 8013, Level = LogLevel.Warning, Message = "Rejected WebSocket for unknown/disallowed model: {ModelId} ({Reason})")]
    public static partial void RealtimeRejectedDisallowedModel(this ILogger logger, string? modelId, string reason);

    [LoggerMessage(EventId = 8014, Level = LogLevel.Information, Message = "✅ {Name} reachable (HTTP {StatusCode})")]
    public static partial void ServiceConnectivityReachable(this ILogger logger, string name, int statusCode);

    [LoggerMessage(EventId = 8015, Level = LogLevel.Warning, Message = "⚠️ {Name} unreachable at {Url} — {Message} (non-fatal)")]
    public static partial void ServiceConnectivityUnreachable(this ILogger logger, string name, string url, string message);

    [LoggerMessage(EventId = 8016, Level = LogLevel.Warning, Message = "⚠️ Service connectivity check failed — {Message} (non-fatal)")]
    public static partial void ServiceConnectivityCheckFailed(this ILogger logger, string message);

    [LoggerMessage(EventId = 8017, Level = LogLevel.Information, Message =
        "Deployment {Deployment} is not treated as a reasoning model (reasoning_model={ReasoningModel}); `reasoning` (effort={Effort}) will not be sent")]
    public static partial void StartupDeploymentNotReasoningModel(this ILogger logger, string deployment, string reasoningModel, string? effort);
}
