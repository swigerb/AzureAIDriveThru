namespace Backend.Auth;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for <see cref="AppSecretProvider"/>,
/// avoiding CA1848 allocations. EventIds 9000-9099 are reserved for this file; message templates,
/// levels and placeholder names are copied verbatim from the call sites they replace -- the
/// conformance suite matches on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 9000, Level = LogLevel.Warning, Message =
        "APP_SESSION_SECRET is only {Length} bytes; recommend at least {Minimum} for HMAC-SHA256.")]
    public static partial void AppSessionSecretTooShort(this ILogger logger, int length, int minimum);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message =
        "APP_SESSION_SECRET is not set. Falling back to a random per-process secret, " +
        "which will invalidate every session token on the next restart or scale-out " +
        "event. Set APP_SESSION_SECRET in production.")]
    public static partial void AppSessionSecretMissing(this ILogger logger);
}
