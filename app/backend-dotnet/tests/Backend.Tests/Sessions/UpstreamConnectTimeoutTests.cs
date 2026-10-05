using System.Diagnostics;
using System.Net.WebSockets;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Xunit;

namespace Backend.Tests.Sessions;

/// <summary>
/// Rick's #280 review, item 3: rtmt.py's upstream `ws_connect` uses a genuine two-phase
/// `aiohttp.ClientTimeout(total=ws_connect_timeout_total, connect=ws_connect_timeout_connect)` --
/// a stalled TCP connect fails at the short `connect` bound (10s default) even though the overall
/// budget is the long `total` bound (30s default). This test proves the C# port now fails at
/// approximately the SHORT `ws_connect_timeout_connect` bound, not the long
/// `ws_connect_timeout_total` one.
///
/// <c>10.255.255.1</c> is a private (RFC 1918), almost-certainly-unassigned address: a SYN sent
/// to it is silently dropped (no RST, no ICMP unreachable) by the default gateway of most
/// container/sandbox/CI networks, which makes the TCP connect itself hang rather than fail fast --
/// exactly the "connect never completes" shape <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout"/>
/// exists to bound (per its own docs, it only bounds "the connection establishing" -- i.e. the
/// TCP/TLS handshake -- not any later HTTP-level wait, so a target that refuses the connection
/// outright, or one that accepts the TCP connection and then stalls the HTTP upgrade, would NOT
/// exercise this knob the way a true network black hole does).
/// </summary>
public sealed class UpstreamConnectTimeoutTests
{
    // Documented ("TEST-NET"-style) black-hole target -- see the class doc comment. Port is
    // arbitrary since nothing ever answers.
    private const string BlackHoleEndpoint = "http://10.255.255.1:81";

    private static RealtimeProcessor CreateProcessor(ConnectionConfig connectionConfig) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: BlackHoleEndpoint,
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>(),
            toolExecutor: new StubToolExecutor([]),
            connectionConfig: connectionConfig);

    /// <summary>The core assertion: with a tiny `ws_connect_timeout_connect` and a much larger
    /// `ws_connect_timeout_total`, the session gives up close to the SHORT bound instead of
    /// waiting out the long one. Mutation check (per the fix-request): in
    /// <c>RealtimeProcessor.RunSessionAsync</c>, change the <c>ConnectAsync</c> call back to the
    /// two-argument overload (dropping the <c>connectPhaseInvoker</c>/`SocketsHttpHandler.ConnectTimeout`
    /// wiring) and this test goes red -- it then waits out the full
    /// <see cref="ConnectionConfig.WsConnectTimeoutSeconds"/> (6s here) instead of stopping at
    /// ~<see cref="ConnectionConfig.WsConnectTimeoutConnectSeconds"/> (1s here).</summary>
    [Fact]
    public async Task StalledConnect_FailsAtShortConnectBound_NotLongTotalBound()
    {
        var connectionConfig = new ConnectionConfig(
            wsHeartbeatSeconds: 15.0,
            wsCompression: false,
            wsConnectTimeoutSeconds: 6,
            wsConnectTimeoutConnectSeconds: 1);

        var processor = CreateProcessor(connectionConfig);
        var socket = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());

        var stopwatch = Stopwatch.StartNew();
        await processor.RunSessionAsync(
            socket,
            PersonaCatalog.Load().Default,
            new ResolvedModel("gpt-realtime-2.1", "realtime", "gpt-realtime-2.1", false),
            "s1",
            CancellationToken.None);
        stopwatch.Stop();

        Assert.True(socket.CloseCalled);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.ClosedWithStatus);
        Assert.Equal("Upstream connection failed", socket.ClosedWithDescription);
        // Generous one-sided bound: well clear of the 1s connect timeout (scheduler/CI jitter
        // headroom) but nowhere near the 6s total bound, proving the SHORT bound is what fired.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"expected the connect-phase timeout (~1s) to fire, not the total timeout (6s); took {stopwatch.Elapsed}");
    }
}
