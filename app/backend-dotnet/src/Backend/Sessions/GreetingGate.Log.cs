using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="GreetingGate"/>. These
/// two moved here verbatim from <see cref="RealtimeProcessor.Log"/> as part of issue #338's
/// extraction -- same message templates, same levels, same EventIds (1001-1002, grandfathered
/// from RealtimeProcessor's reserved 1000-1999 range rather than renumbered), because the
/// conformance suite matches on log text and moving a log call must never change either.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "No session.updated within {Timeout}s; sending greeting anyway (session={SessionId})")]
    public static partial void NoSessionUpdatedBeforeGreeting(this ILogger logger, double timeout, string sessionId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Sending greeting (trigger={Trigger}, session={SessionId})")]
    public static partial void SendingGreeting(this ILogger logger, string trigger, string sessionId);
}
