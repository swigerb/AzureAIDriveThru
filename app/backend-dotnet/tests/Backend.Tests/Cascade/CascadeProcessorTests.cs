using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Backend.Tests.Cascade;

/// <summary>Routes a fake Foundry endpoint's three REST routes (`/chat/completions`,
/// `/openai/v1/audio/transcriptions`, `/openai/v1/audio/speech`) by request path, so ONE
/// <see cref="HttpClient"/> (as <see cref="CascadeProcessor"/>'s own constructor takes) can stand
/// in for both <see cref="FoundryChatClient"/>'s and <see cref="FoundryAudioClient"/>'s endpoints
/// at once -- mirroring how both clients really do share one account/base URI in production (see
/// <see cref="FoundryAudioClient"/>'s own class doc).</summary>
internal sealed class RoutingFoundryHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _chatResponses = new();
    private readonly Queue<Func<HttpResponseMessage>> _transcribeResponses = new();
    private readonly Queue<Func<HttpResponseMessage>> _speakResponses = new();

    public List<string> ChatRequestBodies { get; } = [];
    public List<string> SpeakRequestBodies { get; } = [];
    public int TranscribeRequestCount { get; private set; }

    public RoutingFoundryHandler EnqueueChatMessage(string role, string? content, JsonArray? toolCalls = null)
    {
        var message = new JsonObject { ["role"] = role, ["content"] = content };
        if (toolCalls is not null)
        {
            message["tool_calls"] = toolCalls;
        }
        var body = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString();
        _chatResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public RoutingFoundryHandler EnqueueChatRateLimited()
    {
        _chatResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate limited"}""", Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public RoutingFoundryHandler EnqueueTranscript(string text)
    {
        var body = new JsonObject { ["text"] = text }.ToJsonString();
        _transcribeResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public RoutingFoundryHandler EnqueueTranscribeRateLimited()
    {
        _transcribeResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate limited"}""", Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public RoutingFoundryHandler EnqueueSpeech(byte[] pcmBytes)
    {
        _speakResponses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcmBytes) });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            ChatRequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Dequeue(_chatResponses, "chat completion");
        }
        if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
        {
            TranscribeRequestCount++;
            return Dequeue(_transcribeResponses, "transcription");
        }
        if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
        {
            SpeakRequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Dequeue(_speakResponses, "speech");
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Dequeue(Queue<Func<HttpResponseMessage>> queue, string routeName) =>
        queue.Count > 0 ? queue.Dequeue()() : throw new InvalidOperationException($"No queued {routeName} response -- the test under-provisioned its fake responses.");
}

/// <summary>Records each call's raw arguments JSON (via <see cref="JsonElement.GetRawText"/>) and
/// optionally delegates to a caller-supplied handler -- used by #236 Rick re-review items 4 (empty
/// tool-call arguments must arrive here as "{}", not "") and 5 (a cancellation thrown from a tool
/// must propagate through <see cref="CascadeProcessor"/> unmolested, not get logged as a tool
/// failure) to observe/control what the processor actually does around a tool call, which
/// <see cref="StubToolExecutor"/> (a fixed, non-configurable response) can't.</summary>
internal sealed class RecordingToolExecutor : IToolExecutor
{
    private readonly Func<string, JsonElement, CancellationToken, Task<ToolResult>>? _handler;

    public RecordingToolExecutor(
        IEnumerable<string> toolNames, Func<string, JsonElement, CancellationToken, Task<ToolResult>>? handler = null)
    {
        ToolNames = toolNames.Distinct().ToList();
        _handler = handler;
    }

    public IReadOnlyList<string> ToolNames { get; }
    public List<string> ReceivedArgumentsJson { get; } = [];

    public async Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        ReceivedArgumentsJson.Add(args.GetRawText());
        if (_handler is not null)
        {
            return await _handler(toolName, args, ct).ConfigureAwait(false);
        }
        return new ToolResult(
            $"(recording) {toolName} acknowledged.", ToolResultDirection.ToBoth,
            clientText: $"(recording) {toolName} acknowledged.");
    }
}

/// <summary>Captures every Log call so a test can assert on level + exception type without pulling
/// in an extra test package for a single assertion -- same pattern as
/// <c>Models.ModelDispatchTests.RecordingLogger</c> (kept separate per-file rather than shared,
/// matching that file's own precedent).</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}

/// <summary>Replays a pre-queued sequence of receive chunks like <c>Realtime.FakeWebSocket</c>,
/// but awaits a small REAL delay before each one -- giving <see cref="CascadeProcessor"/>'s
/// background-spawned turn tasks (greeting, guest-turn processing) a real chance to run to
/// completion against the in-memory fakes above before the next queued frame (or the final Close)
/// arrives, without the test asserting on any actual wall-clock timing itself. Not shared with
/// <c>Realtime.FakeWebSocket</c> (which is <c>sealed</c> and deliberately synchronous) since this
/// is a materially different fake -- needed only because <see cref="CascadeProcessor"/>, unlike
/// <see cref="RealtimeProcessor"/>, spawns genuinely-concurrent background work per turn. Unlike
/// <c>Realtime.FakeWebSocket</c>, each queued entry is one WHOLE logical message (not a single
/// pre-chunked <c>ReceiveAsync</c> return): <see cref="ReceiveAsync"/> itself slices it into
/// <see cref="WebSocketFrameReader"/>'s own 8192-byte chunk size across as many calls as needed,
/// exactly like a real fragmented WebSocket message would arrive -- required here (and NOT by the
/// realtime fake) because a guest turn's base64 audio payload routinely exceeds 8192 bytes.
internal sealed class DelayedFakeWebSocket(
    IEnumerable<(byte[] Data, WebSocketMessageType MessageType)> messages,
    TimeSpan delay) : WebSocket
{
    private readonly Queue<(byte[] Data, WebSocketMessageType MessageType)> _messages = new(messages);
    private byte[]? _currentData;
    private WebSocketMessageType _currentType;
    private int _currentOffset;
    private WebSocketState _state = WebSocketState.Open;

    public List<(byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage)> SentMessages { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        if (_currentData is null)
        {
            if (_messages.Count == 0)
            {
                throw new InvalidOperationException("DelayedFakeWebSocket has no more messages queued -- the reader kept reading past what the test expected.");
            }
            (_currentData, _currentType) = _messages.Dequeue();
            _currentOffset = 0;
        }

        var remaining = _currentData.Length - _currentOffset;
        var toCopy = Math.Min(remaining, buffer.Count);
        Array.Copy(_currentData, _currentOffset, buffer.Array!, buffer.Offset, toCopy);
        _currentOffset += toCopy;
        var endOfMessage = _currentOffset >= _currentData.Length;
        var messageType = _currentType;
        if (endOfMessage)
        {
            _currentData = null;
        }
        return new WebSocketReceiveResult(toCopy, messageType, endOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        SentMessages.Add((buffer.ToArray(), messageType, endOfMessage));
        return Task.CompletedTask;
    }
}

/// <summary>Issue #13 Wave 5 (#82): integration-lite tests for <see cref="CascadeProcessor"/>
/// itself -- exercising the real greeting path, a full guest turn (STT -&gt; chat-with-tool-call -&gt;
/// tool dispatch -&gt; final chat -&gt; TTS), and the rate-limit ladder wired end-to-end, against a
/// <see cref="DelayedFakeWebSocket"/> and <see cref="RoutingFoundryHandler"/> rather than any real
/// network or upstream service. Uses <see cref="DeltaFixture"/> (the same non-brand-coupled
/// "test-delta" persona pack Python's own #77 tests and this project's Search/order tests already
/// share) rather than a brand-coupled real persona pack.</summary>
public sealed class CascadeProcessorTests
{
    private const string Endpoint = "https://fake-foundry.example.com";
    private static readonly TimeSpan ReceiveDelay = TimeSpan.FromMilliseconds(60);

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

    private static CascadeProcessor NewProcessor(
        RoutingFoundryHandler handler, IToolExecutor toolExecutor, PromptLoader loader, TimeProvider? timeProvider = null,
        ILogger? logger = null) =>
        new(
            NewCatalog(), Endpoint, Endpoint, AppConfig.Load(),
            promptLoaders: new Dictionary<string, PromptLoader> { ["test-delta"] = loader },
            toolExecutor: toolExecutor,
            httpClient: new HttpClient(handler),
            bearerTokenProvider: new StaticBearerTokenProvider("fake-token"),
            timeProvider: timeProvider,
            logger: logger);

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

    private static JsonObject ParseSent((byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage) sent) =>
        (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(sent.Data))!;

    private static byte[] AppendFrame(byte[] pcm16Bytes) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(pcm16Bytes),
        }.ToJsonString());

    private static byte[] SetVoiceFrame(string? voice) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "extension.set_voice",
            ["voice"] = voice,
        }.ToJsonString());

    [Fact]
    public void PipelineName_IsCascade()
    {
        var (_, loader) = LoadDeltaFixture();
        var processor = NewProcessor(new RoutingFoundryHandler(), new StubToolExecutor(["search"]), loader);

        Assert.Equal("cascade", processor.PipelineName);
    }

    [Fact]
    public void ResolveModel_DelegatesToModelDispatch_RejectingAPersonaWithNoCascadeBlock()
    {
        var (persona, loader) = LoadDeltaFixture();
        var processor = NewProcessor(new RoutingFoundryHandler(), new StubToolExecutor(["search"]), loader);

        // test-delta's persona pack has no models.cascade block -- ModelDispatch.ResolveCascadeModel
        // must reject it exactly like it would for a real persona that hasn't opted into cascade,
        // proving ResolveModel genuinely delegates rather than always succeeding with a stub.
        Assert.Throws<ModelSelectionException>(() => processor.ResolveModel(persona, null));
    }

    [Fact]
    public async Task RunSessionAsync_FullHappyPath_GreetingThenGuestTurnWithToolCall()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            // 1) the connect-time greeting's own chat-completion turn:
            .EnqueueChatMessage("assistant", "Welcome to Test Delta Meal Co.! What can I get started for you?")
            // 2) the guest turn's first round: a tool call ...
            .EnqueueChatMessage("assistant", null, toolCalls: new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = "{\"query\":\"burger\"}" },
                },
            })
            // 3) ... then the guest turn's second round, after the tool result is fed back:
            .EnqueueChatMessage("assistant", "We have a great burger today!")
            .EnqueueTranscript("I'd like a burger")
            .EnqueueSpeech([9, 8, 7, 6])
            .EnqueueSpeech([5, 4, 3, 2, 1]);

        var toolExecutor = new StubToolExecutor(["search"]);
        var processor = NewProcessor(handler, toolExecutor, loader);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10); // RMS 20000 > cutoff (threshold 0.5 * 32767)
        var silentChunk = Pcm16(0, count: 4800); // config.yaml vad.silence_duration_ms=200 @ 24kHz = 4800 samples
        var socket = new DelayedFakeWebSocket(
            [
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-abc", TestContext.Current.CancellationToken);

        var frames = socket.SentMessages.Where(m => m.MessageType == WebSocketMessageType.Text).Select(ParseSent).ToList();
        var types = frames.Select(f => f["type"]!.GetValue<string>()).ToList();

        Assert.Equal("extension.session_metadata", types[0]);
        Assert.Equal("test-delta", frames[0]["persona"]!.GetValue<string>());
        Assert.Equal("gpt-5-mini", frames[0]["model"]!.GetValue<string>());
        Assert.Equal("cascade", frames[0]["pipeline"]!.GetValue<string>());

        // The greeting's own turn: response.created -> spoken transcript+audio -> response.done -> round trip.
        var greetingTranscript = frames.First(f => f["type"]!.GetValue<string>() == "response.audio_transcript.delta");
        Assert.Equal("Welcome to Test Delta Meal Co.! What can I get started for you?", greetingTranscript["delta"]!.GetValue<string>());

        // Guest audio: speech_started, then (after enough trailing silence) a transcription-completed frame.
        Assert.Contains(types, t => t == "input_audio_buffer.speech_started");
        var transcribed = frames.Single(f => f["type"]!.GetValue<string>() == "conversation.item.input_audio_transcription.completed");
        Assert.Equal("I'd like a burger", transcribed["transcript"]!.GetValue<string>());

        // Tool dispatch surfaced to the client between the two chat rounds.
        var toolResponse = frames.Single(f => f["type"]!.GetValue<string>() == "extension.middle_tier_tool_response");
        Assert.Equal("search", toolResponse["tool_name"]!.GetValue<string>());
        Assert.Equal("(stub) search acknowledged.", toolResponse["tool_result"]!.GetValue<string>());

        // The guest turn's own final spoken answer.
        var answerTranscripts = frames.Where(f => f["type"]!.GetValue<string>() == "response.audio_transcript.delta").ToList();
        Assert.Equal(2, answerTranscripts.Count); // greeting + guest-turn answer
        Assert.Equal("We have a great burger today!", answerTranscripts[1]["delta"]!.GetValue<string>());

        // Two full turns -> two response.done/round-trip pairs; round trip index ends at 2.
        Assert.Equal(2, types.Count(t => t == "response.done"));
        var roundTrips = frames.Where(f => f["type"]!.GetValue<string>() == "extension.round_trip_token").ToList();
        Assert.Equal(2, roundTrips.Count);
        Assert.Equal(2, roundTrips[1]["roundTripIndex"]!.GetValue<int>());

        // Exactly 3 chat-completion calls total (greeting, tool-call round, final-answer round) and
        // exactly 1 transcription + 2 speech calls (greeting + guest-turn answer).
        Assert.Equal(3, handler.ChatRequestBodies.Count);
        Assert.Equal(1, handler.TranscribeRequestCount);
        Assert.Equal(2, handler.SpeakRequestBodies.Count);

        // The guest turn's final chat-completion request must include the tool's own result as a
        // "tool" role message threaded back in, proving the tool loop actually re-queried the model.
        var finalRequestBody = JsonNode.Parse(handler.ChatRequestBodies[2])!.AsObject();
        var messages = finalRequestBody["messages"]!.AsArray();
        Assert.Contains(messages, m => m!["role"]!.GetValue<string>() == "tool" && m["tool_call_id"]!.GetValue<string>() == "call_1");
    }

    /// <summary>#236 Rick re-review item 4: a tool call with `"arguments": ""` (an empty STRING,
    /// not an omitted field -- some tool calls with no parameters come back this way) must reach
    /// <see cref="IToolExecutor.ExecuteAsync"/> as an empty JSON object, exactly mirroring
    /// Python's `tool_call.function.arguments or "{}"` (cascade_processor.py). Before this fix,
    /// only a MISSING "arguments" field fell back to "{}" (`?? "{}"`); an explicit "" slipped past
    /// that null-coalesce and threw out of <c>JsonDocument.Parse("")</c>, routed by the generic
    /// catch into the "something went wrong" tool-failure branch instead of the no-arg call it
    /// actually was.</summary>
    [Fact]
    public async Task RunSessionAsync_ToolCallWithEmptyStringArguments_IsTreatedAsAnEmptyJsonObject()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!")
            .EnqueueChatMessage("assistant", null, toolCalls: new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = "" }, // empty STRING, not omitted
                },
            })
            .EnqueueChatMessage("assistant", "Done.")
            .EnqueueTranscript("hi")
            .EnqueueSpeech([9, 8])
            .EnqueueSpeech([5, 4]);

        var toolExecutor = new RecordingToolExecutor(["search"]);
        var processor = NewProcessor(handler, toolExecutor, loader);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-empty-args", TestContext.Current.CancellationToken);

        var toolResponse = socket.SentMessages
            .Where(m => m.MessageType == WebSocketMessageType.Text)
            .Select(ParseSent)
            .Single(f => f["type"]!.GetValue<string>() == "extension.middle_tier_tool_response");
        Assert.Equal("(recording) search acknowledged.", toolResponse["tool_result"]!.GetValue<string>());

        Assert.Single(toolExecutor.ReceivedArgumentsJson);
        Assert.Equal("{}", toolExecutor.ReceivedArgumentsJson[0]);
    }

    /// <summary>#236 Rick re-review item 5 (original fix) + item 3 (this re-review's fix, #236
    /// round 3): Python's bare `except Exception:` around a tool call (cascade_processor.py's
    /// `_execute_tool_call`) never catches a barge-in's `asyncio.CancelledError` -- it derives
    /// from `BaseException`, not `Exception`. C#'s `OperationCanceledException` DOES derive from
    /// `Exception`, so the equivalent <c>catch (Exception ex)</c> in
    /// <c>CascadeProcessor.ExecuteToolCallAsync</c> needed its own explicit
    /// <c>catch (OperationCanceledException) when (turnCt.IsCancellationRequested) { throw; }</c>
    /// ahead of it (same fix applied to the TTS and transcription catches) -- otherwise a guest
    /// barging in mid-tool-call got logged as an unhandled tool failure AND a synthetic "something
    /// went wrong" error appended to history, instead of the turn just quietly ending the way a
    /// real barge-in's <c>CancelCurrentTurnAsync</c>/<c>Spawn</c> machinery expects (and silently
    /// swallows, by design -- see <c>Spawn</c>'s own doc comment).
    ///
    /// Rick's re-review flagged the ORIGINAL version of this test (which had the tool executor
    /// itself `throw new OperationCanceledException(...)` directly) as too weak: it proved the
    /// catch block's TYPE match but never actually exercised `turnCt`, so it couldn't distinguish
    /// the `when (turnCt.IsCancellationRequested)` guard from an unconditional
    /// `catch (OperationCanceledException) { throw; }` -- both pass the old test identically. This
    /// version drives a REAL barge-in instead: the tool executor blocks on the genuine `turnCt`
    /// <see cref="IToolExecutor.ExecuteAsync"/> receives, and only a second
    /// <c>input_audio_buffer.append</c> frame carrying loud audio -- processed by the real VAD
    /// detector and routed through the real <c>CancelCurrentTurnAsync</c> -- unblocks it, exactly
    /// as a guest's spoken interruption would in production.</summary>
    [Fact]
    public async Task RunSessionAsync_RealBargeInDuringToolCall_PropagatesWithoutBeingLoggedAsAToolFailure()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!")
            .EnqueueSpeech([1, 2]) // the greeting's own TTS -- must succeed before the guest turn even starts.
            .EnqueueTranscript("hi") // the guest turn's STT -- must succeed so the turn reaches the tool call below.
            .EnqueueChatMessage("assistant", null, toolCalls: new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = "{}" },
                },
            });
            // No further chat/TTS responses queued -- the tool call blocks on its own turnCt until
            // the real barge-in below cancels it, so the guest turn never reaches a second round.

        var toolExecutor = new RecordingToolExecutor(
            ["search"],
            async (_, _, toolCt) =>
            {
                // Blocks on the REAL per-turn token ExecuteToolCallAsync passes through --
                // unlike the previous version of this test, only a genuine
                // CancelCurrentTurnAsync (triggered by the second loud-audio frame below) can
                // unblock this.
                await Task.Delay(Timeout.InfiniteTimeSpan, toolCt).ConfigureAwait(false);
                return default!;
            });
        var logger = new RecordingLogger();
        var processor = NewProcessor(handler, toolExecutor, loader, logger: logger);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (AppendFrame(loudChunk), WebSocketMessageType.Text), // guest turn 1 starts (greeting already finished)
                (AppendFrame(silentChunk), WebSocketMessageType.Text), // -> speech_stopped: turn 1 spawned, blocks in the tool call
                (AppendFrame(loudChunk), WebSocketMessageType.Text), // real barge-in: cancels turn 1's turnCt mid-tool-call
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        // The cancellation must propagate out of ExecuteToolCallAsync and be swallowed silently
        // by Spawn's own catch (OperationCanceledException) -- so RunSessionAsync itself must
        // complete normally, not throw.
        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-real-barge-in", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);

        var frames = socket.SentMessages.Where(m => m.MessageType == WebSocketMessageType.Text).Select(ParseSent).ToList();
        var types = frames.Select(f => f["type"]!.GetValue<string>()).ToList();

        // Only the greeting's turn completed: the guest turn was cut short by the real barge-in
        // before reaching its own tool-response frame or response.done. Two speech_started frames:
        // one from the guest turn's own opening (a no-op cancel against the already-finished
        // greeting), one from the actual barge-in that cut the tool call short.
        Assert.Equal(1, types.Count(t => t == "response.done"));
        Assert.DoesNotContain(types, t => t == "extension.middle_tier_tool_response");
        Assert.Equal(2, types.Count(t => t == "input_audio_buffer.speech_started"));
    }

    /// <summary>#236 Rick re-review item 3 (LOW): the `when (turnCt.IsCancellationRequested)`
    /// guard on each `catch (OperationCanceledException)` matters because an
    /// `OperationCanceledException` can ALSO come from something unrelated to a barge-in -- e.g.
    /// an HttpClient-internal timeout (`TaskCanceledException`, a subtype) where `turnCt` itself
    /// was never cancelled. Without the guard, that would be misclassified as "the turn was barged
    /// in on" and silently swallowed by Spawn, with no log and no `response.done` ever reaching the
    /// guest. This test proves the opposite codepath: a tool executor that throws a
    /// `TaskCanceledException` whose OWN internal token (not `turnCt`) is cancelled must fall
    /// through to the generic `catch (Exception ex)` -- logged as an error, with the turn still
    /// finishing normally (synthetic tool-failure message, final chat round, response.done).</summary>
    [Fact]
    public async Task RunSessionAsync_ToolCallThrowsTimeoutUnrelatedToBargeIn_StillLogsAndSendsResponseDone()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!")
            .EnqueueSpeech([1, 2])
            .EnqueueTranscript("hi")
            .EnqueueChatMessage("assistant", null, toolCalls: new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = "{}" },
                },
            })
            .EnqueueChatMessage("assistant", "Sorry, something went wrong.") // the final round after the synthetic tool-failure message
            .EnqueueSpeech([9, 9]);

        var toolExecutor = new RecordingToolExecutor(
            ["search"],
            (_, _, _) =>
            {
                // Simulates an HttpClient-internal timeout: its OWN CancellationTokenSource is
                // cancelled (unrelated to turnCt, which stays live throughout this test), so
                // `ex is OperationCanceledException` is true but `turnCt.IsCancellationRequested`
                // is false.
                using var unrelatedCts = new CancellationTokenSource();
                unrelatedCts.Cancel();
                throw new TaskCanceledException("simulated HttpClient-internal timeout, unrelated to any barge-in", null, unrelatedCts.Token);
            });
        var logger = new RecordingLogger();
        var processor = NewProcessor(handler, toolExecutor, loader, logger: logger);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-timeout-not-bargein", TestContext.Current.CancellationToken);

        // Must be logged as an error (not silently swallowed), and the turn must still finish:
        // the generic catch's synthetic failure message gets threaded back in and a final chat
        // round + response.done + spoken answer still happen.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is TaskCanceledException);

        var frames = socket.SentMessages.Where(m => m.MessageType == WebSocketMessageType.Text).Select(ParseSent).ToList();
        var types = frames.Select(f => f["type"]!.GetValue<string>()).ToList();
        Assert.Equal(2, types.Count(t => t == "response.done")); // greeting + guest turn
        Assert.Equal("Sorry, something went wrong.", frames.Last(f => f["type"]!.GetValue<string>() == "response.audio_transcript.delta")["delta"]!.GetValue<string>());
    }


    [Fact]
    public async Task RunSessionAsync_TranscriptionRateLimitExhausted_EndsTurnCleanlyWithFinalNotice()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!") // greeting's own chat turn still succeeds
            .EnqueueSpeech([1, 2, 3])
            .EnqueueTranscribeRateLimited()
            .EnqueueTranscribeRateLimited()
            .EnqueueTranscribeRateLimited(); // config.yaml's resilience.rate_limit.max_retries default is 2 -> 3 total attempts

        var fakeTime = new FakeTimeProvider();
        var processor = NewProcessor(handler, new StubToolExecutor(["search"]), loader, fakeTime);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        // The retry ladder's Task.Delay calls are driven by fakeTime, but they're on a background
        // Task.Run'd turn, racing the DelayedFakeWebSocket's own real-time receive delays above --
        // advancing fakeTime in a tight background loop keeps nudging any pending delay forward
        // the instant it's registered, without the test itself needing to observe when that happens.
        var testCt = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();
        var advancer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                fakeTime.Advance(TimeSpan.FromSeconds(10));
                try
                {
                    await Task.Delay(5, testCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, testCt);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-rl", testCt);
        await cts.CancelAsync();
        await advancer;

        var frames = socket.SentMessages.Where(m => m.MessageType == WebSocketMessageType.Text).Select(ParseSent).ToList();
        var types = frames.Select(f => f["type"]!.GetValue<string>()).ToList();

        Assert.DoesNotContain("conversation.item.input_audio_transcription.completed", types);
        var finalNotice = frames.Last(f => f["type"]!.GetValue<string>() == CascadeRateLimit.RateLimitedEvent);
        Assert.True(finalNotice["final"]!.GetValue<bool>());
        Assert.Equal(3, handler.TranscribeRequestCount); // attempt 0 + 2 retries, then exhausted
    }

    /// <summary>#236 Rick re-review item 2 (MEDIUM, blocking): `extension.set_voice` must go
    /// through <see cref="ClientServerFilter.SanitizeVoice"/> with the same default allow-list
    /// RealtimeProcessor uses (<see cref="ClientServerFilter.DefaultAllowedVoices"/>), exactly
    /// mirroring <c>RealtimeProcessor.HandleClientExtensionMessageAsync</c>'s own
    /// `extension.set_voice` branch -- not accept any non-empty string as the old code did. A
    /// known-good voice must be adopted and used for the NEXT turn's TTS request.</summary>
    [Fact]
    public async Task RunSessionAsync_SetVoiceWithAKnownVoice_IsAdoptedForTheNextTurnsTts()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!")
            .EnqueueSpeech([1, 2]) // greeting's own TTS -- happens before extension.set_voice is even processed.
            .EnqueueTranscript("hi")
            .EnqueueChatMessage("assistant", "We have a great burger today!")
            .EnqueueSpeech([3, 4]); // the guest turn's own TTS -- must use the newly adopted voice.

        var processor = NewProcessor(handler, new StubToolExecutor(["search"]), loader);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (SetVoiceFrame("echo"), WebSocketMessageType.Text),
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-set-voice-ok", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.SpeakRequestBodies.Count);
        var guestTurnTtsRequest = JsonNode.Parse(handler.SpeakRequestBodies[1])!.AsObject();
        Assert.Equal("echo", guestTurnTtsRequest["voice"]!.GetValue<string>());
    }

    /// <summary>#236 Rick re-review item 2: an unknown/invalid voice must be DROPPED (logged as a
    /// warning, `state.Voice` left unchanged) rather than adopted, mirroring
    /// RealtimeProcessor's own drop-and-warn behaviour exactly. This is the C# counterpart to
    /// #253's Python-only conformance row
    /// "Cascade_extension_set_voice_with_an_unknown_voice_is_dropped_not_adopted" (tag
    /// `Dotnet=ready` once #253 merges).</summary>
    [Fact]
    public async Task RunSessionAsync_SetVoiceWithAnUnknownVoice_IsDroppedNotAdopted()
    {
        var (persona, loader) = LoadDeltaFixture();
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Welcome!")
            .EnqueueSpeech([1, 2])
            .EnqueueTranscript("hi")
            .EnqueueChatMessage("assistant", "We have a great burger today!")
            .EnqueueSpeech([3, 4]);

        var logger = new RecordingLogger();
        var processor = NewProcessor(handler, new StubToolExecutor(["search"]), loader, logger: logger);
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);

        var loudChunk = Pcm16(20000, count: 10);
        var silentChunk = Pcm16(0, count: 4800);
        var socket = new DelayedFakeWebSocket(
            [
                (SetVoiceFrame("not-a-real-voice"), WebSocketMessageType.Text),
                (AppendFrame(loudChunk), WebSocketMessageType.Text),
                (AppendFrame(silentChunk), WebSocketMessageType.Text),
                ([], WebSocketMessageType.Close),
            ],
            ReceiveDelay);

        await processor.RunSessionAsync(socket, persona, resolvedModel, sessionId: "sess-set-voice-bad", TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Dropped extension.set_voice"));

        Assert.Equal(2, handler.SpeakRequestBodies.Count);
        var greetingTtsRequest = JsonNode.Parse(handler.SpeakRequestBodies[0])!.AsObject();
        var guestTurnTtsRequest = JsonNode.Parse(handler.SpeakRequestBodies[1])!.AsObject();
        // Both turns must still use the persona's own default voice, not the rejected candidate --
        // and, critically, the SAME voice the greeting used (proving state.Voice was never mutated).
        Assert.NotEqual("not-a-real-voice", guestTurnTtsRequest["voice"]!.GetValue<string>());
        Assert.Equal(greetingTtsRequest["voice"]!.GetValue<string>(), guestTurnTtsRequest["voice"]!.GetValue<string>());
    }
}
