using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Configuration;
using Backend.Models;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Cascade;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="CascadeAudioTurnPipeline"/>, extracted from
/// <see cref="CascadeProcessor.RunSessionAsync"/>'s own <c>TranscribeAsync</c>, <c>SpeakAsync</c>
/// and <c>SendFailedResponseDoneAsync</c> local closures. Reuses <see cref="RoutingFoundryHandler"/>
/// from <see cref="CascadeProcessorTests"/> (same assembly, `internal` visible) rather than
/// duplicating it.
/// </summary>
public sealed class CascadeAudioTurnPipelineTests
{
    private const string Endpoint = "https://fake-foundry.example.com";

    private static ModelCatalog NewCatalog() => ModelCatalog.FromConfig(
        AppConfig.Load(),
        environment: new Dictionary<string, string>
        {
            ["AZURE_AI_MODEL_DEPLOYMENTS"] = """{"gpt-4o-transcribe":"transcribe-dep","gpt-4o-mini-tts":"tts-dep"}""",
        });

    private static CascadeAudioTurnPipeline NewPipeline(
        RoutingFoundryHandler handler, FakeWebSocket browserSocket, CascadeProcessor.CascadeSessionState state,
        TurnDetector? detector = null, double echoCooldownSeconds = 0) =>
        new(
            browserSocket,
            DeltaFixture.Load(),
            state,
            detector ?? new TurnDetector(0.5, 500, 24000),
            NewCatalog(),
            new FoundryAudioClient(new HttpClient(handler), Endpoint, new StaticBearerTokenProvider("fake-token")),
            new RateLimitSettings(),
            echoCooldownSeconds,
            () => "session-1",
            TimeProvider.System,
            notifyClientAsync: (_, _) => Task.CompletedTask,
            nowSeconds: () => 0,
            NullLogger.Instance,
            CancellationToken.None);

    private static CascadeProcessor.CascadeSessionState NewState() => new()
    {
        SessionId = "session-1",
        Deployment = "chat-dep",
        Voice = "marin",
    };

    [Fact]
    public async Task TranscribeAsync_ReturnsTranscriptTextFromResponse()
    {
        RoutingFoundryHandler handler = new RoutingFoundryHandler().EnqueueTranscript("I'd like fries");
        CascadeAudioTurnPipeline pipeline = NewPipeline(handler, new FakeWebSocket([]), NewState());

        string text = await pipeline.TranscribeAsync([1, 2, 3, 4], TestContext.Current.CancellationToken);

        Assert.Equal("I'd like fries", text);
        Assert.Equal(1, handler.TranscribeRequestCount);
    }

    [Fact]
    public async Task SpeakAsync_SendsAudioDeltaFramesChunkedToTheClient()
    {
        byte[] pcm = new byte[CascadeProcessor.TtsChunkBytes + 100];
        RoutingFoundryHandler handler = new RoutingFoundryHandler().EnqueueSpeech(pcm);
        var socket = new FakeWebSocket([]);
        CascadeAudioTurnPipeline pipeline = NewPipeline(handler, socket, NewState());

        await pipeline.SpeakAsync("Coming right up", "resp_1", TestContext.Current.CancellationToken);

        // Two chunks: one full CascadeProcessor.TtsChunkBytes-sized delta, one 100-byte remainder.
        Assert.Equal(2, socket.SentMessages.Count);
        foreach ((byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage) sent in socket.SentMessages)
        {
            JsonObject frame = JsonNode.Parse(sent.Data)!.AsObject();
            Assert.Equal("response.audio.delta", frame["type"]!.GetValue<string>());
            // Realtime API delta shape: each chunk names the response it belongs to.
            Assert.Equal("resp_1", frame["response_id"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task SpeakAsync_WithEchoCooldown_ArmsTheTurnDetector()
    {
        byte[] pcm = new byte[24000]; // 0.5s of 24kHz mono PCM16
        RoutingFoundryHandler handler = new RoutingFoundryHandler().EnqueueSpeech(pcm);
        var detector = new TurnDetector(0.5, 500, 24000);
        CascadeAudioTurnPipeline pipeline = NewPipeline(handler, new FakeWebSocket([]), NewState(), detector, echoCooldownSeconds: 0.2);

        await pipeline.SpeakAsync("Coming right up", "resp_1", TestContext.Current.CancellationToken);

        // 0.5s audio + 0.2s cooldown starting at nowSeconds()=0 means a 0.0 timestamp is still
        // inside the cooldown window -- Feed() with LOUD audio at that timestamp still returns
        // null (dropped as echo) only because the cooldown check runs before the RMS/threshold
        // check; an un-armed detector would instead report "speech_started" for audio this loud.
        byte[] loudPcm = new byte[3200];
        for (int i = 0; i < loudPcm.Length; i += 2)
        {
            loudPcm[i] = 0xFF;
            loudPcm[i + 1] = 0x7F; // short.MaxValue, little-endian
        }
        string? result = detector.Feed(loudPcm, now: 0.0);
        Assert.Null(result);
    }

    [Fact]
    public async Task SendFailedResponseDoneAsync_SendsErrorThenFailedResponseDone()
    {
        var socket = new FakeWebSocket([]);
        CascadeAudioTurnPipeline pipeline = NewPipeline(new RoutingFoundryHandler(), socket, NewState());

        await pipeline.SendFailedResponseDoneAsync("resp-1", "boom", TestContext.Current.CancellationToken);

        Assert.Equal(2, socket.SentMessages.Count);
        JsonObject errorFrame = JsonNode.Parse(socket.SentMessages[0].Data)!.AsObject();
        Assert.Equal("error", errorFrame["type"]!.GetValue<string>());
        Assert.Equal("boom", errorFrame["error"]!["message"]!.GetValue<string>());

        JsonObject doneFrame = JsonNode.Parse(socket.SentMessages[1].Data)!.AsObject();
        Assert.Equal("response.done", doneFrame["type"]!.GetValue<string>());
        Assert.Equal("failed", doneFrame["response"]!["status"]!.GetValue<string>());
        Assert.Equal("resp-1", doneFrame["response"]!["id"]!.GetValue<string>());
    }
}
