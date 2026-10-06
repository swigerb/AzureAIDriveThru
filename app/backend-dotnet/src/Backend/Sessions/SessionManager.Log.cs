using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="SessionManager"/>,
/// avoiding CA1848/CA1873 allocations on hot paths. EventIds 3000-3999 are reserved for this file;
/// message templates, levels and placeholder names are copied verbatim from the call sites they
/// replace -- the conformance suite matches on log text, so none of that may
/// change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Resume rejected for session {SessionId}: bound persona {Bound} != requested persona {Requested} (persona_mismatch)")]
    public static partial void ResumeRejectedPersonaMismatch(this ILogger logger, string sessionId, string? bound, string? requested);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Resume rejected for session {SessionId}: bound model {Bound} != requested model {Requested} (model_mismatch)")]
    public static partial void ResumeRejectedModelMismatch(this ILogger logger, string sessionId, string? bound, string? requested);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Resume rejected for session {SessionId}: bound menu mode {Bound} != requested menu mode {Requested} (mode_mismatch)")]
    public static partial void ResumeRejectedModeMismatch(this ILogger logger, string sessionId, string? bound, string? requested);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Session {SessionId} resumed{Superseded}")]
    public static partial void SessionResumed(this ILogger logger, string sessionId, string superseded);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Session {SessionId} ended ({Reason})")]
    public static partial void SessionEnded(this ILogger logger, string sessionId, string reason);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Session {SessionId} detached ({Reason}); holding order for {Seconds:F0}s")]
    public static partial void SessionDetached(this ILogger logger, string sessionId, string reason, double seconds);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "Closing idle session {SessionId} (idle beyond {Seconds}s)")]
    public static partial void ClosingIdleSession(this ILogger logger, string sessionId, double seconds);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Idle/grace sweep error")]
    public static partial void IdleGraceSweepError(this ILogger logger, Exception ex);
}
