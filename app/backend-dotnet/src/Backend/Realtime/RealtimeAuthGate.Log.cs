using Microsoft.Extensions.Logging;

namespace Backend.Realtime;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for <see cref="RealtimeAuthGate"/>
/// (issue #336: CA1848/CA1873 on hot `/realtime` handshake paths). EventIds 7000-7999 are
/// reserved for this file; message templates, levels and placeholder names are copied verbatim
/// from the call sites they replace -- the conformance suite and ops dashboards match on log
/// text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Realtime handshake: GET /realtime (host={Host}, origin={Origin})")]
    public static partial void RealtimeHandshake(this ILogger logger, string host, string origin);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Rejected WebSocket from disallowed origin: host={Host} origin={Origin}")]
    public static partial void RejectedDisallowedOrigin(this ILogger logger, string host, string origin);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Rejected WebSocket with invalid/expired session token")]
    public static partial void RejectedInvalidSessionToken(this ILogger logger);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Rejected WebSocket: session token oid does not match Entra principal")]
    public static partial void RejectedOidMismatch(this ILogger logger);
}
