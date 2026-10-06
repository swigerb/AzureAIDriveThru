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
            "session-1",
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
        var handler = new RoutingFoundryHandler().EnqueueTranscript("I'd like fries");
        var pipeline = NewPipeline(handler, new FakeWebSocket([]), NewState());

        var text = await pipeline.TranscribeAsync([1, 2, 3, 4], CancellationToken.None);

        Assert.Equal("I'd like fries", text);
        Assert.Equal(1, handler.TranscribeRequestCount);
    }

    [Fact]
    public async Task SpeakAsync_SendsAudioDeltaFramesChunkedToTheClient()
    {
        var pcm = new byte[CascadeProcessor.TtsChunkBytes + 100];
        var handler = new RoutingFoundryHandler().EnqueueSpeech(pcm);
        var socket = new FakeWebSocket([]);
        var pipeline = NewPipeline(handler, socket, NewState());

        await pipeline.SpeakAsync("Coming right up", CancellationToken.None);

        // Two chunks: one full CascadeProcessor.TtsChunkBytes-sized delta, one 100-byte remainder.
        Assert.Equal(2, socket.SentMessages.Count);
        foreach (var sent in socket.SentMessages)
        {
            var frame = JsonNode.Parse(sent.Data)!.AsObject();
            Assert.Equal("response.audio.delta", frame["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task SpeakAsync_WithEchoCooldown_ArmsTheTurnDetector()
    {
        var pcm = new byte[24000]; // 0.5s of 24kHz mono PCM16
        var handler = new RoutingFoundryHandler().EnqueueSpeech(pcm);
        var detector = new TurnDetector(0.5, 500, 24000);
        var pipeline = NewPipeline(handler, new FakeWebSocket([]), NewState(), detector, echoCooldownSeconds: 0.2);

        await pipeline.SpeakAsync("Coming right up", CancellationToken.None);

        // 0.5s audio + 0.2s cooldown starting at nowSeconds()=0 means a 0.0 timestamp is still
        // inside the cooldown window -- Feed() with LOUD audio at that timestamp still returns
        // null (dropped as echo) only because the cooldown check runs before the RMS/threshold
        // check; an un-armed detector would instead report "speech_started" for audio this loud.
        var loudPcm = new byte[3200];
        for (var i = 0; i < loudPcm.Length; i += 2)
        {
            loudPcm[i] = 0xFF;
            loudPcm[i + 1] = 0x7F; // short.MaxValue, little-endian
        }
        var result = detector.Feed(loudPcm, now: 0.0);
        Assert.Null(result);
    }

    [Fact]
    public async Task SendFailedResponseDoneAsync_SendsErrorThenFailedResponseDone()
    {
        var socket = new FakeWebSocket([]);
        var pipeline = NewPipeline(new RoutingFoundryHandler(), socket, NewState());

        await pipeline.SendFailedResponseDoneAsync("resp-1", "boom", CancellationToken.None);

        Assert.Equal(2, socket.SentMessages.Count);
        var errorFrame = JsonNode.Parse(socket.SentMessages[0].Data)!.AsObject();
        Assert.Equal("error", errorFrame["type"]!.GetValue<string>());
        Assert.Equal("boom", errorFrame["error"]!["message"]!.GetValue<string>());

        var doneFrame = JsonNode.Parse(socket.SentMessages[1].Data)!.AsObject();
        Assert.Equal("response.done", doneFrame["type"]!.GetValue<string>());
        Assert.Equal("failed", doneFrame["response"]!["status"]!.GetValue<string>());
        Assert.Equal("resp-1", doneFrame["response"]!["id"]!.GetValue<string>());
    }
}
