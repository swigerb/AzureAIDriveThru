using Microsoft.Extensions.Logging;

namespace Backend.Realtime;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="RateLimitRecovery"/>,
/// avoiding CA1848/CA1873 allocations on hot paths. EventIds 6000-6999 are reserved for this file;
/// message templates, levels and placeholder names are copied verbatim from the call sites they
/// replace -- the conformance suite matches on log text, so none of that may
/// change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Warning, Message = "Rate-limit error while a response is in flight (code={Code}); waiting for its response.done (session={SessionId})")]
    public static partial void RateLimitErrorWhileInFlight(this ILogger logger, string? code, string? sessionId);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Rate-limit retry cancelled: {Reason} (session={SessionId})")]
    public static partial void RateLimitRetryCancelled(this ILogger logger, string reason, string? sessionId);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "Model response rate-limited ({Source}): code={Code} type={Type} retry_hint={Hint} (session={SessionId})")]
    public static partial void ModelResponseRateLimited(this ILogger logger, string source, string? code, string? type, string hint, string? sessionId);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Rate-limit failure while a retry is already pending; same attempt (session={SessionId})")]
    public static partial void RateLimitFailureRetryPending(this ILogger logger, string? sessionId);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Information, Message = "Rate-limit failure after the final retry; waiting for the guest's next turn (session={SessionId})")]
    public static partial void RateLimitFailureAfterExhausted(this ILogger logger, string? sessionId);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Warning, Message = "Rate-limit retries exhausted after {Attempt} attempt(s); asking the guest to repeat (session={SessionId})")]
    public static partial void RateLimitRetriesExhausted(this ILogger logger, int attempt, string? sessionId);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Information, Message = "Rate-limit retry {Attempt} skipped: superseded before it could run (session={SessionId})")]
    public static partial void RateLimitRetrySupersededSkip(this ILogger logger, int attempt, string? sessionId);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Information, Message = "Rate-limit retry {Attempt} skipped: another response is already running (session={SessionId})")]
    public static partial void RateLimitRetryInFlightSkip(this ILogger logger, int attempt, string? sessionId);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Information, Message = "Rate-limit retry {Attempt}: response.create after {Delay:F2}s (session={SessionId})")]
    public static partial void RateLimitRetrySending(this ILogger logger, int attempt, double delay, string? sessionId);

    [LoggerMessage(EventId = 6009, Level = LogLevel.Information, Message = "Rate-limit retry {Attempt} not sent: {Message} (session={SessionId})")]
    public static partial void RateLimitRetryNotSent(this ILogger logger, int attempt, string message, string? sessionId);

    [LoggerMessage(EventId = 6010, Level = LogLevel.Information, Message = "Could not send {Type} to the browser: {Message} (session={SessionId})")]
    public static partial void RateLimitNotifyFailed(this ILogger logger, string? type, string message, string? sessionId);
}
