namespace Backend.Cascade;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="CascadeRateLimit"/>. EventIds
/// 10010-10011 come from the sessions area's 10000-10999 range (<c>CascadeDiagnosticCapture.Log.cs</c>
/// uses 10000-10007). Both templates are the verbatim text of the inline <c>LogWarning</c>/
/// <c>LogInformation</c> calls they replaced, at the same level; the conformance suite matches on log
/// text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 10010, Level = LogLevel.Warning, Message = "Cascade {OpName} rate-limited; retries exhausted after {Attempt} attempt(s) (session={SessionId})")]
    public static partial void CascadeRateLimitRetriesExhausted(this ILogger logger, string opName, int attempt, string sessionId);

    [LoggerMessage(EventId = 10011, Level = LogLevel.Information, Message = "Cascade {OpName} rate-limited; retry {Attempt} after {Delay:F2}s (session={SessionId})")]
    public static partial void CascadeRateLimitRetrying(this ILogger logger, string opName, int attempt, double delay, string sessionId);
}
