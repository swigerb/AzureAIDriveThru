using Microsoft.Extensions.Logging;

namespace Backend.Realtime;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="NudgeScheduler"/>
/// (issue #336: CA1848/CA1873 on hot paths). EventIds 5000-5999 are reserved for this file;
/// message templates, levels and placeholder names are copied verbatim from the call sites they
/// replace -- the conformance suite and ops dashboards match on log text, so none of that may
/// change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Resume nudge timer fired but was already cancelled (session={SessionId})")]
    public static partial void NudgeTimerFiredAfterCancel(this ILogger logger, string? sessionId);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Resume nudge skipped: a rate-limit retry is in progress (session={SessionId})")]
    public static partial void NudgeSkippedRateLimitBusy(this ILogger logger, string? sessionId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Guest silent {Seconds}s after resume; nudging (session={SessionId})")]
    public static partial void NudgeFiring(this ILogger logger, double seconds, string? sessionId);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Resume nudge not sent: {Message} (session={SessionId})")]
    public static partial void NudgeSendFailed(this ILogger logger, Exception ex, string message, string? sessionId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Resume nudge cancelled: {Reason} (session={SessionId})")]
    public static partial void NudgeCancelled(this ILogger logger, string reason, string? sessionId);
}
