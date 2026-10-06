using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="FramePump"/>. These
/// three moved here verbatim from <see cref="RealtimeProcessor.Log"/> as part of issue #338's
/// extraction -- same message templates, same levels, same EventIds (1038-1040, grandfathered
/// from RealtimeProcessor's reserved 1000-1999 range rather than renumbered), because the
/// conformance suite matches on log text and moving a log call must never change either.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1038, Level = LogLevel.Error, Message = "Unhandled exception draining a realtime relay loop")]
    public static partial void UnhandledRelayDrainException(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1039, Level = LogLevel.Warning, Message = "Error forwarding fast-path audio append frame (session={SessionId})")]
    public static partial void FastPathAudioForwardFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1040, Level = LogLevel.Warning, Message = "Unexpected failure closing a superseded stale connection's output")]
    public static partial void SupersededCloseFailed(this ILogger logger, Exception ex);
}
