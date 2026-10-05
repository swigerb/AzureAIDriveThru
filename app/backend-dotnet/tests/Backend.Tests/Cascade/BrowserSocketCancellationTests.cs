using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Xunit;

namespace Backend.Tests.Cascade;

/// <summary>
/// #236 Rick re-review item 1 (HIGH, blocking): <see cref="CascadeProcessorTests"/>'s own
/// <c>DelayedFakeWebSocket</c> is a hand-rolled fake whose <c>SendAsync</c> never touches a real
/// network transport -- it cannot reproduce .NET's <c>ManagedWebSocket</c> aborting the WHOLE
/// socket when the <see cref="CancellationToken"/> passed to an in-flight <c>SendAsync</c> is
/// cancelled. This file instead wraps a REAL loopback TCP connection with
/// <see cref="WebSocket.CreateFromStream(System.IO.Stream, bool, string?, TimeSpan)"/> (the exact
/// same <c>ManagedWebSocket</c> implementation Kestrel itself hands <see cref="CascadeProcessor"/>
/// for a server-accepted connection), with deliberately tiny socket buffers and a guest side that
/// stops reading mid-turn, so a large TTS reply's <c>response.audio.delta</c> send genuinely
/// blocks on real backpressure -- a barge-in landing in that exact window is the only way to
/// observe the bug Rick reproduced end-to-end with the real <c>Backend.dll</c> (and matched to
/// every first-round CI failure of this PR: a greeting cancelled mid-send right after
/// <c>response.done</c>).
/// </summary>
public sealed class BrowserSocketCancellationTests
{
    private const string Endpoint = "https://fake-foundry.example.com";

    private static (Persona Persona, PromptLoader Loader) LoadDeltaFixture()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        return (DeltaFixture.Load(), new PromptLoader(personasDir, "test-delta"));
    }

    private static ModelCatalog NewCatalog() => ModelCatalog.FromConfig(
        AppConfig.Load(),
        environment: new Dictionary<string, string>
        {
            ["AZURE_AI_MODEL_DEPLOYMENTS"] = """{"gpt-4o-transcribe":"transcribe-dep","gpt-4o-mini-tts":"tts-dep"}""",
        });

    private static CascadeProcessor NewProcessor(RoutingFoundryHandler handler, IToolExecutor toolExecutor, PromptLoader loader) =>
        new(
            NewCatalog(), Endpoint, Endpoint, AppConfig.Load(),
            promptLoaders: new Dictionary<string, PromptLoader> { ["test-delta"] = loader },
            toolExecutor: toolExecutor,
            httpClient: new HttpClient(handler),
            bearerTokenProvider: new StaticBearerTokenProvider("fake-token"),
            // #126: this file's barge-in-during-backpressure scenario predates cascade's own
            // echo-suppression feature and deliberately uses REAL wall-clock delays between the
            // greeting's TTS and the guest's own next turn -- well within what would be a real
            // 1.5s echo cooldown, but not testing echo suppression at all. 0 keeps the existing
            // barge-in behaviour this file actually tests unaffected.
            echoCooldownSeconds: 0);

    private static byte[] Pcm16(short sampleValue, int count)
    {
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            bytes[i * 2] = (byte)(sampleValue & 0xFF);
            bytes[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
        }
        return bytes;
    }

    private static ArraySegment<byte> AppendFrame(byte[] pcm16Bytes) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(pcm16Bytes),
        }.ToJsonString());

    /// <summary>Opens a real loopback TCP pair and wraps each end as a real
    /// <c>ManagedWebSocket</c>: <paramref name="BrowserSocket"/> is what <see cref="CascadeProcessor"/>
    /// treats as the server-accepted connection to the browser (<c>isServer: true</c>, matching
    /// Kestrel), <paramref name="GuestSocket"/> is the test's stand-in for the guest's own browser
    /// WebSocket client. Both ends get a tiny socket buffer so a large payload genuinely
    /// backpressures if the guest side stops reading.</summary>
    private static async Task<(WebSocket BrowserSocket, WebSocket GuestSocket, TcpClient ServerTcp, TcpClient ClientTcp)> OpenLoopbackWebSocketPairAsync()
    {
        const int TinyBufferBytes = 512;
        // Sized BEFORE the TCP handshake (the listener's buffers are inherited by the accepted
        // socket; the client's are set before ConnectAsync), never after: TCP never shrinks a
        // receive window it has already advertised, so shrinking SO_RCVBUF on an established
        // connection leaves the peer free to send in-window data the receiver must then drop.
        // That turned this harness into a TCP retransmit-timeout crawl (Linux: TCPZeroWindowDrop/
        // RcvPruned/TCPTimeouts, ~2.3KB delivered per exponentially-backed-off RTO), which stalled
        // the test for reasons unrelated to CascadeProcessor -- and, by letting the guest's
        // follow-up mic frame slip into the stale oversized window, hid the real barge-in
        // write/write deadlock this test now also guards against (see the test's own doc comment).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SendBufferSize = TinyBufferBytes;
        listener.Server.ReceiveBufferSize = TinyBufferBytes;
        listener.Start();
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            var clientTcp = new TcpClient();
            clientTcp.Client.SendBufferSize = TinyBufferBytes;
            clientTcp.Client.ReceiveBufferSize = TinyBufferBytes;
            await clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
            var serverTcp = await acceptTask.ConfigureAwait(false);

            // Deliberately tiny: makes a multi-chunk TTS reply back up almost immediately once the
            // guest side stops draining, without needing a multi-megabyte payload to force it.
            serverTcp.Client.SendBufferSize = TinyBufferBytes;
            serverTcp.Client.ReceiveBufferSize = TinyBufferBytes;

            var browserSocket = WebSocket.CreateFromStream(
                serverTcp.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
            var guestSocket = WebSocket.CreateFromStream(
                clientTcp.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
            return (browserSocket, guestSocket, serverTcp, clientTcp);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<List<JsonObject>> DrainUntilAsync(WebSocket guestSocket, string targetType, CancellationToken ct)
    {
        var frames = new List<JsonObject>();
        while (true)
        {
            var frame = await WebSocketFrameReader.ReadMessageAsync(guestSocket, ct).ConfigureAwait(false);
            if (frame is null)
            {
                return frames;
            }
            var json = (JsonObject)JsonNode.Parse(frame.Payload)!;
            frames.Add(json);
            if (json["type"]!.GetValue<string>() == targetType)
            {
                return frames;
            }
        }
    }

    /// <summary>Proves #236 Rick re-review item 1's fix: a barge-in landing while a large TTS
    /// reply's send is genuinely backpressure-blocked on the real browser socket must NOT abort
    /// that socket -- it must only cancel the current turn. Before the fix, <c>SendTextAsync</c>
    /// passed the per-turn, barge-in-cancellable token straight into the browser socket's own
    /// <c>SendAsync</c>; cancelling it while a write was in flight made .NET's
    /// <c>ManagedWebSocket</c> abort the WHOLE connection -- silently killing the guest's session
    /// (the receive loop exits, every later send becomes a no-op, nothing is logged). This test
    /// would have failed against that code: reverting the fix (passing <c>turnCt</c> instead of
    /// the session-level <c>ct</c> to the actual <c>socket.SendAsync</c> call) makes the socket
    /// abort here and the second guest turn below never completes -- see the mutation-check note
    /// in the PR body.
    ///
    /// #126/PR #290: it also guards against a barge-in write/write deadlock. Step 4 below has the
    /// guest WRITE another mic frame (~12.8KB) before it resumes reading, exactly like a real
    /// browser that keeps streaming mic audio while the assistant's reply backs up. If barge-in
    /// handling awaits the cancelled turn inline on the receive loop, the server stops reading
    /// while its own turn's write waits on the guest, and the guest's write waits on the server:
    /// neither side ever reads again.</summary>
    [Fact]
    public async Task RunSessionAsync_BargeInDuringABackpressuredTtsWrite_DoesNotAbortTheBrowserSocket()
    {
        var (persona, loader) = LoadDeltaFixture();
        var bigAnswerPcm = Pcm16(12345, count: 400_000); // 800 KB of PCM -> ~33 chunks of TtsChunkBytes(24000) each
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!") // greeting's own small chat+TTS turn
            .EnqueueSpeech([1, 2, 3, 4])
            .EnqueueTranscript("tell me about the menu") // guest turn 1: a big spoken answer that will back up the socket
            .EnqueueChatMessage("assistant", "Here is a very long answer about the menu.")
            .EnqueueSpeech(bigAnswerPcm)
            .EnqueueTranscript("ok thanks") // guest turn 2: short, proving the socket still works after the barge-in
            .EnqueueChatMessage("assistant", "You're welcome!")
            .EnqueueSpeech([5, 6]);

        var toolExecutor = new StubToolExecutor(["search"]);
        var processor = NewProcessor(handler, toolExecutor, loader);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = overallCts.Token;

        var (browserSocket, guestSocket, serverTcp, clientTcp) = await OpenLoopbackWebSocketPairAsync();
        using var _server = serverTcp;
        using var _client = clientTcp;

        var sessionTask = processor.RunSessionAsync(browserSocket, persona, resolvedModel, sessionId: "sess-real-socket", ct);

        // 1) Drain the greeting's turn fully (small payload -- fits easily even in the tiny buffer).
        await DrainUntilAsync(guestSocket, "extension.round_trip_token", ct);

        // 2) Kick off guest turn 1 (the big spoken answer) -- then deliberately STOP reading, so its
        //    TTS chunks back up against the 512-byte socket buffers almost immediately.
        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800); // config.yaml vad.silence_duration_ms=200 @ 24kHz
        await guestSocket.SendAsync(AppendFrame(loudChunk), WebSocketMessageType.Text, true, ct);
        await guestSocket.SendAsync(AppendFrame(silentChunk), WebSocketMessageType.Text, true, ct);

        // Give the (STT -> chat -> TTS) pipeline real wall-clock time to reach the big reply's
        // chunk-send loop and genuinely back up on the unread socket -- a generous margin, not a
        // tight race: with a 512-byte buffer, an 800 KB base64'd payload backs up within the very
        // first chunk, so this is not flaky even under CPU stress.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // 3) The real barge-in: a second speech_started lands while that big send is still
        //    genuinely blocked on backpressure.
        await guestSocket.SendAsync(AppendFrame(loudChunk), WebSocketMessageType.Text, true, ct);

        // Let the barge-in's turn cancellation settle (the cancelled turn itself can't finish
        // until its in-flight write drains, which is fine: the receive loop must keep reading).
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        // The core assertion: the socket we handed to CascadeProcessor must still be Open, not
        // Aborted, even though a send was genuinely in flight when the barge-in's cancellation fired.
        Assert.Equal(WebSocketState.Open, browserSocket.State);

        // 4) Keep streaming mic audio (BEFORE reading anything -- see the write/write deadlock note
        //    in this test's doc comment), then resume draining (flushes whatever was left of turn
        //    1's cut-short reply, harmlessly), and prove the socket is still fully functional: a
        //    fresh guest turn must complete end-to-end (STT -> chat -> TTS -> response.done -> round trip).
        var silentChunk2 = Pcm16(0, count: 4800);
        await guestSocket.SendAsync(AppendFrame(silentChunk2), WebSocketMessageType.Text, true, ct);

        var finalFrames = await DrainUntilAsync(guestSocket, "extension.round_trip_token", ct);
        var roundTrips = finalFrames.Where(f => f["type"]!.GetValue<string>() == "extension.round_trip_token").ToList();
        Assert.NotEmpty(roundTrips);
        Assert.Equal(2, roundTrips[^1]["roundTripIndex"]!.GetValue<int>()); // greeting=1, resumed guest turn=2 (turn 1 never got this far)
        Assert.Contains(finalFrames, f => f["type"]!.GetValue<string>() == "response.audio_transcript.delta"
            && f["delta"]!.GetValue<string>() == "You're welcome!");

        await guestSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        await sessionTask;
    }
}
