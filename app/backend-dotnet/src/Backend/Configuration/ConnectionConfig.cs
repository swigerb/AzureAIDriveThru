using System.Globalization;

namespace Backend.Configuration;

/// <summary>
/// Port of app/backend/rtmt.py's/cascade_processor.py's shared module-level
/// <c>connection:</c> config.yaml section reads (issue #13 tail): websocket heartbeat interval,
/// compression toggle, and upstream connect timeout. Both Python files read the SAME section
/// independently (each with its own `_WS_HEARTBEAT_SEC`/`_WS_COMPRESS`/`_WS_CONNECT_TIMEOUT`
/// module-level constants) -- this one C# class is shared by both <see cref="Sessions.RealtimeProcessor"/>
/// (browser-facing <c>WebSocketOptions.KeepAliveInterval</c> wired in Program.cs, upstream
/// <c>ClientWebSocket</c> connect timeout) and <see cref="Sessions.CascadeProcessor"/> (which has
/// no upstream WebSocket of its own -- cascade's "upstream" calls are plain REST, so only the
/// browser-facing heartbeat applies there, wired the same way Program.cs wires it for realtime).
///
/// .NET's <see cref="System.Net.WebSockets.ClientWebSocket"/>/Kestrel's WebSocket middleware have
/// no two-phase "connect" vs "total" timeout split the way aiohttp's
/// <c>ClientTimeout(total=, connect=)</c> does -- there is exactly one knob
/// (<see cref="System.Net.WebSockets.ClientWebSocket.ConnectAsync(Uri, HttpMessageInvoker?, CancellationToken)"/>'s
/// own cancellation token). This port uses <see cref="WsConnectTimeoutSeconds"/> (mapped from
/// `ws_connect_timeout_total`, the more lenient of Python's two bounds, so a slow-but-eventually-
/// successful connect under .NET is never cut off earlier than it would be under Python's own
/// `connect=10` sub-phase would allow) as the single combined timeout wrapping the whole connect
/// call. `ws_connect_timeout_connect` has no separate C# equivalent; this difference is
/// documented in docs/dotnet_mapping.md. `ws_compression` is likewise a documented no-op here:
/// Azure OpenAI Realtime declines permessage-deflate from the upstream side regardless (rtmt.py's
/// own upstream `ws_connect` always passes `compress=0`, never `_WS_COMPRESS`), and ASP.NET
/// Core's `WebSocketOptions` has no browser-facing compression knob to wire it to in the first
/// place (Kestrel's WebSocket middleware doesn't negotiate permessage-deflate at all) -- so the
/// two backends already behave identically here (no compression, either way) without needing to
/// read this field at all. It's still parsed below for config-shape parity with Python's own
/// reads of the same section (and in case a Kestrel version in the future adds the knob).
/// </summary>
public sealed class ConnectionConfig
{
    public double WsHeartbeatSeconds { get; }
    public bool WsCompression { get; }
    public double WsConnectTimeoutSeconds { get; }

    public ConnectionConfig(
        double wsHeartbeatSeconds = 15.0,
        bool wsCompression = false,
        double wsConnectTimeoutSeconds = 30)
    {
        WsHeartbeatSeconds = wsHeartbeatSeconds;
        WsCompression = wsCompression;
        WsConnectTimeoutSeconds = wsConnectTimeoutSeconds;
    }

    public static ConnectionConfig FromConfig(AppConfig config)
    {
        var connection = config.TryGetSection("connection");
        return new ConnectionConfig(
            wsHeartbeatSeconds: GetDouble(connection, "ws_heartbeat_seconds", 15.0),
            wsCompression: GetBool(connection, "ws_compression", false),
            wsConnectTimeoutSeconds: GetDouble(connection, "ws_connect_timeout_total", 30));
    }

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback) =>
        section is not null && section.TryGetValue(key, out var raw) &&
        double.TryParse(raw?.ToString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(IDictionary<object, object>? section, string key, bool fallback)
    {
        if (section is null || !section.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }
        if (raw is bool direct)
        {
            return direct;
        }
        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }
}
