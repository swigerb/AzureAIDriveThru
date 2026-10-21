using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Shared;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

public sealed class RealtimeServerRelayOrderCompletionTests
{
    private const string ClosingText =
        "I have one Sonic Double Cheeseburger. That brings your new total to $7.12. " +
        "Thank you! Your carhop will have that right out!";

    [Fact]
    public async Task Tool_order_result_requests_and_relays_spoken_closing_response()
    {
        var upstream = new FakeWebSocket(
        [
            Text("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call-1","name":"get_order"}}"""),
            Text("""{"type":"conversation.item.created","previous_item_id":"item-1","item":{"type":"function_call","call_id":"call-1","name":"get_order","arguments":"{}"}}"""),
            Text("""{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call-1","name":"get_order","arguments":"{}"}}"""),
            Text("""{"type":"response.done","response":{"id":"response-tool","status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"get_order","arguments":"{}"}]}}"""),
            Text("""{"type":"response.created","response":{"id":"response-close"}}"""),
            Text("""{"type":"response.output_audio_transcript.delta","response_id":"response-close","item_id":"item-close","content_index":0,"delta":"I have one Sonic Double Cheeseburger."}"""),
            Text("""{"type":"response.output_audio.delta","response_id":"response-close","item_id":"item-close","content_index":0,"delta":"AA=="}"""),
            Text("""{"type":"response.done","response":{"id":"response-close","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"audio","transcript":"I have one Sonic Double Cheeseburger. That brings your new total to $7.12. Thank you! Your carhop will have that right out!"}]}]}}"""),
            Close(),
        ]);
        var browser = new FakeWebSocket([]);
        var state = NewState();
        using var linkedCts = new CancellationTokenSource();
        var context = NewContext(browser, upstream, state, linkedCts);

        await new RealtimeServerRelay(context).RunAsync();

        var upstreamFrames = TextFrames(upstream).Select(Parse).ToArray();
        Assert.Equal("conversation.item.create", (string?)upstreamFrames[0]["type"]);
        Assert.Equal("function_call_output", (string?)upstreamFrames[0]["item"]!["type"]);
        Assert.Equal("response.create", (string?)upstreamFrames[1]["type"]);

        var browserFrames = TextFrames(browser).Select(Parse).ToArray();
        Assert.Contains(browserFrames, frame =>
            (string?)frame["type"] == "response.audio_transcript.delta"
            && ((string?)frame["delta"])!.Contains("Sonic Double Cheeseburger", StringComparison.Ordinal));
        Assert.Contains(browserFrames, frame =>
            (string?)frame["type"] == "response.audio.delta" && (string?)frame["delta"] == "AA==");
        var finalResponse = browserFrames.Last(frame => (string?)frame["type"] == "response.done");
        var finalTranscript = (string?)finalResponse["response"]!["output"]![0]!["content"]![0]!["transcript"];
        Assert.Contains("Your carhop will have that right out!", finalTranscript, StringComparison.Ordinal);
    }

    private static (byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType) Text(string json) =>
        (Encoding.UTF8.GetBytes(json), true, WebSocketMessageType.Text);

    private static (byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType) Close() =>
        ([], true, WebSocketMessageType.Close);

    private static JsonObject Parse(string frame) => JsonNode.Parse(frame)!.AsObject();

    private static List<string> TextFrames(FakeWebSocket socket) =>
        socket.SentMessages
            .Where(message => message.MessageType == WebSocketMessageType.Text)
            .Select(message => Encoding.UTF8.GetString(message.Data))
            .ToList();

    private static RealtimeProcessor.RealtimeSessionState NewState()
    {
        var state = new RealtimeProcessor.RealtimeSessionState
        {
            SessionId = "session-order-completion",
            Voice = "marin",
            Echo = new EchoSuppressor(0, _ => Task.CompletedTask),
            RateLimit = new RateLimitRecovery(
                new RateLimitSettings(),
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                NullLogger<RateLimitRecovery>.Instance),
            ToolFailures = new ToolFailureTracker(),
            Guard = new SessionUpdateGuard(),
            Identifiers = new SessionIdentifiers("sonic", "gpt-realtime-2.1"),
            ToolExecutor = new StubToolExecutor(["get_order"]),
        };
        state.EffectiveSessionId = state.SessionId;
        return state;
    }

    private static RealtimeSessionContext NewContext(
        FakeWebSocket browser,
        FakeWebSocket upstream,
        RealtimeProcessor.RealtimeSessionState state,
        CancellationTokenSource linkedCts)
    {
        var ct = CancellationToken.None;
        var persona = PersonaCatalog.Load().Default;
        var resolvedModel = new ResolvedModel("gpt-realtime-2.1", "realtime", "gpt-realtime-2.1", false);
        var sessionConfig = new RealtimeSessionConfig();
        var processor = new RealtimeProcessor(
            ModelCatalog.FromConfig(AppConfig.Load()),
            new RealtimeProcessorOptions(
                "gpt-realtime-2.1",
                "https://example.openai.azure.com",
                "not-used",
                sessionConfig,
                ClientServerFilter.DefaultAllowedVoices,
                1.5,
                5.0,
                new RateLimitSettings(),
                new ConnectionConfig()),
            new RealtimeProcessorDependencies(
                new Dictionary<string, PromptLoader>(),
                new StubToolExecutor([]),
                NullLogger<RealtimeProcessor>.Instance,
                NullLogger<RateLimitRecovery>.Instance,
                NullLogger<NudgeScheduler>.Instance));
        var greetingGate = new GreetingGate(
            upstream, null, state, null, TimeProvider.System, 5.0, state.SessionId,
            NullLogger.Instance, ct);
        var resumeCoordinator = new ResumeCoordinator(
            browser, upstream, persona, resolvedModel, null, state.SessionId, linkedCts, state, null,
            _ => new JsonObject(), NullLogger.Instance, ct);
        var toolCallDispatcher = new ToolCallDispatcher(
            upstream, browser, state, null, state.SessionId, NullLogger.Instance, ct);

        return new RealtimeSessionContext
        {
            Processor = processor,
            BrowserSocket = browser,
            Upstream = upstream,
            Persona = persona,
            ResolvedModel = resolvedModel,
            MenuMode = null,
            SessionId = state.SessionId,
            SystemMessage = null,
            PromptLoader = null,
            ToolSchemas = [],
            ReasoningOverride = Overridable.Of<bool?>(null),
            State = state,
            SessionConfig = sessionConfig,
            SessionManager = null,
            GreetingGate = greetingGate,
            ToolCallDispatcher = toolCallDispatcher,
            ResumeCoordinator = resumeCoordinator,
            AllowedVoices = ClientServerFilter.DefaultAllowedVoices,
            Logger = NullLogger.Instance,
            TimeProvider = TimeProvider.System,
            CancellationToken = ct,
        };
    }
}
