using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="ToolCallDispatcher"/>, extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/>'s own <c>HandleToolCallDoneAsync</c> local
/// closure. These lock down the same wire behaviour the extraction promises not to change:
/// unknown call ids / tool names are dropped silently, a superseded connection never dispatches,
/// a successful call always replies upstream (function_call_output) and -- depending on
/// <see cref="ToolResultDirection"/> -- to the browser, and a failing call still sends a
/// function_call_output (the fallback apology text) and counts the failure.
/// </summary>
public sealed class ToolCallDispatcherTests
{
    private static RealtimeProcessor.RealtimeSessionState NewState(IToolExecutor executor) => new()
    {
        SessionId = "session-1",
        Voice = "marin",
        Echo = new EchoSuppressor(0, _ => Task.CompletedTask),
        RateLimit = new RateLimitRecovery(
            new RateLimitSettings(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            NullLogger<RateLimitRecovery>.Instance),
        ToolFailures = new ToolFailureTracker(),
        Guard = new SessionUpdateGuard(),
        Identifiers = new SessionIdentifiers("sonic", "gpt-realtime"),
        ToolExecutor = executor,
    };

    private sealed class ThrowingOrderTicketExecutor : IToolExecutor, IOrderTicketSource
    {
        public IReadOnlyList<string> ToolNames { get; } = ["update_order"];
        public string CurrentOrderSummaryJson { get; set; } = """{"items":[]}""";

        public Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private static List<string> TextFrames(FakeWebSocket socket) =>
        socket.SentMessages
            .Where(m => m.MessageType == WebSocketMessageType.Text)
            .Select(m => System.Text.Encoding.UTF8.GetString(m.Data))
            .ToList();

    [Fact]
    public async Task HandleToolCallDoneAsync_ignores_a_call_id_not_in_ToolsPending()
    {
        var state = NewState(new StubToolExecutor(["get_order"]));
        var upstream = new FakeWebSocket([]);
        var browser = new FakeWebSocket([]);
        var dispatcher = new ToolCallDispatcher(upstream, browser, state, sessionManager: null, "session-1", NullLogger.Instance, CancellationToken.None);

        await dispatcher.HandleToolCallDoneAsync(new JsonObject { ["call_id"] = "unknown-call", ["name"] = "get_order" });

        Assert.Empty(TextFrames(upstream));
        Assert.Empty(TextFrames(browser));
    }

    [Fact]
    public async Task HandleToolCallDoneAsync_ignores_an_unregistered_tool_name()
    {
        var state = NewState(new StubToolExecutor(["get_order"]));
        state.ToolsPending["call-1"] = "prev-item";
        var upstream = new FakeWebSocket([]);
        var browser = new FakeWebSocket([]);
        var dispatcher = new ToolCallDispatcher(upstream, browser, state, sessionManager: null, "session-1", NullLogger.Instance, CancellationToken.None);

        await dispatcher.HandleToolCallDoneAsync(new JsonObject { ["call_id"] = "call-1", ["name"] = "delete_everything" });

        Assert.Empty(TextFrames(upstream));
        Assert.Empty(TextFrames(browser));
    }

    [Fact]
    public async Task HandleToolCallDoneAsync_drops_the_call_when_this_connection_is_superseded()
    {
        var state = NewState(new StubToolExecutor(["get_order"]));
        state.ToolsPending["call-1"] = "prev-item";
        state.Superseded.MarkSuperseded();
        var upstream = new FakeWebSocket([]);
        var browser = new FakeWebSocket([]);
        var dispatcher = new ToolCallDispatcher(upstream, browser, state, sessionManager: null, "session-1", NullLogger.Instance, CancellationToken.None);

        await dispatcher.HandleToolCallDoneAsync(new JsonObject { ["call_id"] = "call-1", ["name"] = "get_order" });

        Assert.Empty(TextFrames(upstream));
        Assert.Empty(TextFrames(browser));
    }

    [Fact]
    public async Task HandleToolCallDoneAsync_sends_function_call_output_and_client_response_on_success()
    {
        var state = NewState(new StubToolExecutor(["get_order"]));
        state.ToolsPending["call-1"] = "prev-item";
        var upstream = new FakeWebSocket([]);
        var browser = new FakeWebSocket([]);
        var dispatcher = new ToolCallDispatcher(upstream, browser, state, sessionManager: null, "session-1", NullLogger.Instance, CancellationToken.None);

        await dispatcher.HandleToolCallDoneAsync(new JsonObject
        {
            ["call_id"] = "call-1",
            ["name"] = "get_order",
            ["arguments"] = "{}",
        });

        var upstreamFrame = JsonNode.Parse(Assert.Single(TextFrames(upstream)))!.AsObject();
        Assert.Equal("conversation.item.create", (string?)upstreamFrame["type"]);
        Assert.Equal("function_call_output", (string?)upstreamFrame["item"]!["type"]);
        Assert.Equal("call-1", (string?)upstreamFrame["item"]!["call_id"]);

        var browserFrame = JsonNode.Parse(Assert.Single(TextFrames(browser)))!.AsObject();
        Assert.Equal("extension.middle_tier_tool_response", (string?)browserFrame["type"]);
        Assert.Equal("prev-item", (string?)browserFrame["previous_item_id"]);
        Assert.Equal("get_order", (string?)browserFrame["tool_name"]);
    }

    [Fact]
    public async Task HandleToolCallDoneAsync_on_exception_sends_fallback_output_refreshes_ticket_and_counts_the_failure()
    {
        var executor = new ThrowingOrderTicketExecutor();
        var state = NewState(executor);
        state.ToolsPending["call-1"] = "prev-item";
        var upstream = new FakeWebSocket([]);
        var browser = new FakeWebSocket([]);
        var dispatcher = new ToolCallDispatcher(upstream, browser, state, sessionManager: null, "session-1", NullLogger.Instance, CancellationToken.None);

        await dispatcher.HandleToolCallDoneAsync(new JsonObject
        {
            ["call_id"] = "call-1",
            ["name"] = "update_order",
            ["arguments"] = "{}",
        });

        // The ticket refresh (extension.middle_tier_tool_response for get_order) goes to the
        // browser BEFORE the function_call_output goes upstream -- same order as before extraction.
        var browserFrame = JsonNode.Parse(Assert.Single(TextFrames(browser)))!.AsObject();
        Assert.Equal("get_order", (string?)browserFrame["tool_name"]);
        Assert.Equal(executor.CurrentOrderSummaryJson, (string?)browserFrame["tool_result"]);

        var upstreamFrame = JsonNode.Parse(Assert.Single(TextFrames(upstream)))!.AsObject();
        Assert.Equal("conversation.item.create", (string?)upstreamFrame["type"]);
        Assert.Equal("function_call_output", (string?)upstreamFrame["item"]!["type"]);
        Assert.Contains("did not complete", (string?)upstreamFrame["item"]!["output"]);

        // RecordCallFailure only flags the round; HandleResponseDoneAsync's EndRound() tallies it
        // once per round (not per call) -- call that here to prove the flag was actually set.
        state.ToolFailures.EndRound();
        Assert.Equal(1, state.ToolFailures.Count);
    }
}
