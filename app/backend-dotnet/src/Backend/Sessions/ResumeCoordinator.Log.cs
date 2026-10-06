using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: log declarations for <see cref="ResumeCoordinator"/>, moved verbatim (same
/// EventIds, templates and levels) out of RealtimeProcessor.Log.cs's 1000-1999 range when the
/// resume/supersede-announce handling was extracted into its own collaborator.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Dropped extension.resume arriving after the first-frame decision ({Reason}, session={SessionId})")]
    public static partial void DroppedLateResume(this ILogger logger, string reason, string sessionId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "extension.resume rejected (reason={Reason}, session={SessionId})")]
    public static partial void ExtensionResumeRejected(this ILogger logger, string? reason, string sessionId);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "Session resumed (resumedSessionId={ResumedSessionId}, session={SessionId})")]
    public static partial void SessionResumedWithId(this ILogger logger, string? resumedSessionId, string sessionId);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning, Message = "Could not read order state while building a session-resumed announcement (session={SessionId})")]
    public static partial void OrderStateReadForAnnouncementFailed(this ILogger logger, Exception ex, string sessionId);
}
