using Backend.Configuration;

namespace Backend.Tests.Configuration;

/// <summary>
/// Issue #13 tail: ConnectionConfig.FromConfig tests -- the typed view of config.yaml's
/// `connection` section's websocket heartbeat/compression/connect-timeout fields (same
/// convention as <see cref="SecurityConfigTests"/>: write a real temp config.yaml and load it
/// through the real code path rather than mocking).
/// </summary>
public sealed class ConnectionConfigTests
{
    [Fact]
    public void FromConfig_EmptyConnectionSection_DefaultsMatchPython()
    {
        // rtmt.py/cascade_processor.py: _conn_cfg = _config.get("connection", {}) -- an absent
        // key inside the section is treated as "use the Python-side default", same reasoning
        // SecurityConfigTests' own empty-section test uses. (AppConfig.Load itself requires the
        // `connection` section to be PRESENT -- see AppConfig.cs's RequiredSections -- so this
        // test can't exercise a fully-absent section the way SecurityConfigTests does for
        // `security`, which isn't required.)
        var config = LoadWithConnection("connection: {}\n");

        var connection = ConnectionConfig.FromConfig(config);

        Assert.Equal(15.0, connection.WsHeartbeatSeconds);
        Assert.False(connection.WsCompression);
        Assert.Equal(30, connection.WsConnectTimeoutSeconds);
    }

    [Fact]
    public void FromConfig_ParsesAllThreeFields()
    {
        var config = LoadWithConnection(
            "connection:\n" +
            "  ws_heartbeat_seconds: 20.0\n" +
            "  ws_compression: true\n" +
            "  ws_connect_timeout_total: 45\n" +
            "  ws_connect_timeout_connect: 12\n");

        var connection = ConnectionConfig.FromConfig(config);

        Assert.Equal(20.0, connection.WsHeartbeatSeconds);
        Assert.True(connection.WsCompression);
        Assert.Equal(45, connection.WsConnectTimeoutSeconds);
    }

    private static AppConfig LoadWithConnection(string? section)
    {
        var yaml =
            "model:\n  foo: bar\n" +
            "business_rules:\n  foo: bar\n" +
            "cache:\n  foo: bar\n" +
            "audio:\n  foo: bar\n" +
            "security:\n  foo: bar\n" +
            (section ?? string.Empty);

        var path = Path.Combine(Path.GetTempPath(), "squanchy-connection-config-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return AppConfig.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
