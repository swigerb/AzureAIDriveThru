using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Cascade;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="CascadeToolCallDispatcher"/>, extracted from
/// <see cref="CascadeProcessor.RunSessionAsync"/>'s own <c>CallChatCompletionAsync</c>,
/// <c>ExecuteToolCallAsync</c> and <c>RunChatToolLoopAsync</c> local closures. Reuses
/// <see cref="RoutingFoundryHandler"/> and <see cref="RecordingToolExecutor"/> from
/// <see cref="CascadeProcessorTests"/> (same assembly, `internal` visible) rather than
/// duplicating them.
/// </summary>
public sealed class CascadeToolCallDispatcherTests
{
    private const string Endpoint = "https://fake-foundry.example.com";

    private static CascadeProcessor.CascadeSessionState NewState() => new()
    {
        SessionId = "session-1",
        Deployment = "chat-dep",
    };

    private static CascadeToolCallDispatcher NewDispatcher(
        RoutingFoundryHandler handler,
        IToolExecutor toolExecutor,
        CascadeProcessor.CascadeSessionState state,
        FakeWebSocket? browserSocket = null) =>
        new(
            browserSocket ?? new FakeWebSocket([]),
            toolExecutor,
            promptLoader: null,
            state,
            new RateLimitSettings(),
            new FoundryChatClient(new HttpClient(handler), Endpoint, new StaticBearerTokenProvider("fake-token")),
            toolDefinitions: [],
            sessionManager: null,
            "session-1",
            TimeProvider.System,
            notifyClientAsync: (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            CancellationToken.None);

    [Fact]
    public async Task RunChatToolLoopAsync_NoToolCalls_ReturnsAssistantContentAndAppendsIt()
    {
        var handler = new RoutingFoundryHandler().EnqueueChatMessage("assistant", "Order confirmed.");
        var state = NewState();
        var dispatcher = NewDispatcher(handler, new StubToolExecutor([]), state);

        var result = await dispatcher.RunChatToolLoopAsync(CancellationToken.None);

        Assert.Equal("Order confirmed.", result);
        Assert.Contains(state.Messages, m => m["role"]?.GetValue<string>() == "assistant"
            && m["content"]?.GetValue<string>() == "Order confirmed.");
    }

    [Fact]
    public async Task RunChatToolLoopAsync_OneToolCallRound_ExecutesToolThenReturnsFinalAnswer()
    {
        var toolCall = new JsonObject
        {
            ["id"] = "call-1",
            ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = """{"a":1}""" },
        };
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", null, new JsonArray(toolCall))
            .EnqueueChatMessage("assistant", "Here's your order.");
        var toolExecutor = new RecordingToolExecutor(["get_order"]);
        var state = NewState();
        var dispatcher = NewDispatcher(handler, toolExecutor, state);

        var result = await dispatcher.RunChatToolLoopAsync(CancellationToken.None);

        Assert.Equal("Here's your order.", result);
        Assert.Single(toolExecutor.ReceivedArgumentsJson);
        Assert.Contains(state.Messages, m => m["role"]?.GetValue<string>() == "tool");
    }

    [Fact]
    public async Task RunChatToolLoopAsync_UnknownTool_AppendsEmptyToolMessageWithoutExecuting()
    {
        var toolCall = new JsonObject
        {
            ["id"] = "call-2",
            ["function"] = new JsonObject { ["name"] = "not_a_real_tool", ["arguments"] = "{}" },
        };
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", null, new JsonArray(toolCall))
            .EnqueueChatMessage("assistant", "Done.");
        var toolExecutor = new RecordingToolExecutor(["get_order"]);
        var state = NewState();
        var dispatcher = NewDispatcher(handler, toolExecutor, state);

        var result = await dispatcher.RunChatToolLoopAsync(CancellationToken.None);

        Assert.Equal("Done.", result);
        Assert.Empty(toolExecutor.ReceivedArgumentsJson);
        var toolMessage = Assert.Single(state.Messages, m => m["role"]?.GetValue<string>() == "tool");
        Assert.Equal("", toolMessage["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunChatToolLoopAsync_ToolThrows_AppendsPromptLoaderFallbackAndKeepsGoing()
    {
        var toolCall = new JsonObject
        {
            ["id"] = "call-3",
            ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
        };
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", null, new JsonArray(toolCall))
            .EnqueueChatMessage("assistant", "Recovered.");
        var toolExecutor = new RecordingToolExecutor(
            ["get_order"], (_, _, _) => throw new InvalidOperationException("boom"));
        var state = NewState();
        var dispatcher = NewDispatcher(handler, toolExecutor, state);

        var result = await dispatcher.RunChatToolLoopAsync(CancellationToken.None);

        Assert.Equal("Recovered.", result);
        var toolMessage = Assert.Single(state.Messages, m => m["role"]?.GetValue<string>() == "tool");
        Assert.Contains("Something went wrong", toolMessage["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunChatToolLoopAsync_CapsAtMaxRounds_ReturnsEmptyString()
    {
        var handler = new RoutingFoundryHandler();
        for (var i = 0; i < CascadeProcessor.MaxToolRounds; i++)
        {
            var toolCall = new JsonObject
            {
                ["id"] = $"call-{i}",
                ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
            };
            handler.EnqueueChatMessage("assistant", null, new JsonArray(toolCall));
        }
        var toolExecutor = new RecordingToolExecutor(["get_order"]);
        var state = NewState();
        var dispatcher = NewDispatcher(handler, toolExecutor, state);

        var result = await dispatcher.RunChatToolLoopAsync(CancellationToken.None);

        Assert.Equal("", result);
        Assert.Equal(CascadeProcessor.MaxToolRounds, toolExecutor.ReceivedArgumentsJson.Count);
    }
}
