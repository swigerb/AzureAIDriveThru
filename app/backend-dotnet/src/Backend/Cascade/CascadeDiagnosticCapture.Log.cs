namespace Backend.Cascade;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="CascadeDiagnosticGate"/> and
/// <see cref="CascadeDiagnosticCapture"/>. EventIds 10000-10007 come from the sessions area's
/// 10000-10999 range (<c>CascadeRateLimit.Log.cs</c> uses 10010-10011). Every
/// template is the verbatim text of the inline <c>LogWarning</c>/<c>LogInformation</c> call it
/// replaced, at the same level; the conformance suite matches on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 10000, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostics disabled: invalid or expired configuration.")]
    public static partial void CascadeDiagnosticsInvalidConfiguration(this ILogger logger);

    [LoggerMessage(EventId = 10001, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic request rejected: scope mismatch.")]
    public static partial void CascadeDiagnosticScopeMismatch(this ILogger logger);

    [LoggerMessage(EventId = 10002, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic request rejected: process claim already used.")]
    public static partial void CascadeDiagnosticClaimAlreadyUsed(this ILogger logger);

    [LoggerMessage(EventId = 10003, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic capture stopped: text frame limit reached.")]
    public static partial void CascadeDiagnosticStoppedFrameLimit(this ILogger logger);

    [LoggerMessage(EventId = 10004, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic capture stopped: expiry or event limit reached.")]
    public static partial void CascadeDiagnosticStoppedExpiryOrEventLimit(this ILogger logger);

    [LoggerMessage(EventId = 10005, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic capture stopped: payload limit reached.")]
    public static partial void CascadeDiagnosticStoppedPayloadLimit(this ILogger logger);

    [LoggerMessage(EventId = 10006, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostic capture stopped: sanitization timed out.")]
    public static partial void CascadeDiagnosticStoppedSanitizationTimeout(this ILogger logger);

    [LoggerMessage(EventId = 10007, Level = LogLevel.Information, Message = "Synthetic cascade diagnostic {Diagnostic}")]
    public static partial void CascadeDiagnosticCaptured(this ILogger logger, string diagnostic);
}
