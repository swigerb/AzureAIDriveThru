using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Rick's #280 review, item 3: rtmt.py's upstream `ws_connect` uses a genuine two-phase
/// `aiohttp.ClientTimeout(total=ws_connect_timeout_total, connect=ws_connect_timeout_connect)` --
/// a stalled TCP connect fails at the short `connect` bound (10s default) even though the overall
/// budget is the long `total` bound (30s default). This test proves the C# port now fails at
/// approximately the SHORT `ws_connect_timeout_connect` bound, not the long
/// `ws_connect_timeout_total` one.
///
/// Per Rick's re-review (option (a)): a black-hole IP is not deterministic on every runner --
/// some networks (egress-deny containers, future runner image changes) fail the TCP SYN fast
/// with an unreachable/refused error instead of silently dropping it, which would make the test
/// pass vacuously without ever exercising <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout"/>.
/// Instead this test starts a real loopback <see cref="TcpListener"/> on <c>127.0.0.1</c> that
/// accepts the TCP connection and then never writes a single byte back. TCP connect therefore
/// always succeeds immediately (it's loopback), but the subsequent TLS handshake (the
/// `ClientHello`/`ServerHello` exchange <c>wss://</c> requires) stalls forever waiting for a
/// server response that never comes -- exactly the "connect never completes" shape
/// <c>ConnectTimeout</c> exists to bound (per its own docs, it bounds "the connection
/// establishing", which includes the TLS handshake, not any later HTTP-level wait). This is
/// deterministic on every runner because it never leaves the loopback interface.
/// </summary>
public sealed class UpstreamConnectTimeoutTests
{
    private static RealtimeProcessor CreateProcessor(ConnectionConfig connectionConfig, int port) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            new RealtimeProcessorOptions(
                "gpt-realtime-2.1",
                $"https://127.0.0.1:{port}",
                "sk-not-used",
                new RealtimeSessionConfig(),
                ClientServerFilter.DefaultAllowedVoices,
                1.5,
                5.0,
                new RateLimitSettings(),
                connectionConfig),
            new RealtimeProcessorDependencies(
                new Dictionary<string, PromptLoader>(),
                new StubToolExecutor([]),
                NullLogger<RealtimeProcessor>.Instance,
                NullLogger<RateLimitRecovery>.Instance,
                NullLogger<NudgeScheduler>.Instance));

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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Accept the TLS-stalling connection(s) in the background. We never read or write to the
        // accepted socket -- the client's TLS ClientHello is sent into the void, which is exactly
        // what stalls the handshake. Keep accepting (rather than a single accept) so a retried
        // connect attempt, if the runtime ever makes one, doesn't get connection-refused instead.
        var acceptedSockets = new List<TcpClient>();
        var acceptLoopCts = new CancellationTokenSource();
#pragma warning disable xUnit1051 // intentionally the accept-loop's own teardown token, not test cancellation
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!acceptLoopCts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(acceptLoopCts.Token);
                    lock (acceptedSockets)
                    {
                        acceptedSockets.Add(client);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on test teardown.
            }
        });
#pragma warning restore xUnit1051

        try
        {
            var connectionConfig = new ConnectionConfig(
                wsHeartbeatSeconds: 15.0,
                wsCompression: false,
                wsConnectTimeoutSeconds: 6,
                wsConnectTimeoutConnectSeconds: 1);

            var processor = CreateProcessor(connectionConfig, port);
            var socket = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());

            var stopwatch = Stopwatch.StartNew();
            await processor.RunSessionAsync(
                socket,
                PersonaCatalog.Load().Default,
                new ResolvedModel("gpt-realtime-2.1", "realtime", "gpt-realtime-2.1", false),
                "s1",
                TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(socket.CloseCalled);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.ClosedWithStatus);
            Assert.Equal("Upstream connection failed", socket.ClosedWithDescription);
            // Two-sided bound: the lower bound proves the connect-phase timeout actually fired
            // (rather than some unrelated fast failure passing vacuously), and the upper bound
            // proves it fired at the SHORT ~1s connect bound rather than the 6s total bound.
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800),
                $"expected the connect-phase timeout (~1s) to fire, not an immediate/fast failure; took {stopwatch.Elapsed}");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
                $"expected the connect-phase timeout (~1s) to fire, not the total timeout (6s); took {stopwatch.Elapsed}");
        }
        finally
        {
            acceptLoopCts.Cancel();
            listener.Stop();
            try
            {
                await acceptLoop;
            }
            catch
            {
                // Best-effort cleanup; already past assertions.
            }

            lock (acceptedSockets)
            {
                foreach (var client in acceptedSockets)
                {
                    client.Dispose();
                }
            }
        }
    }
}
