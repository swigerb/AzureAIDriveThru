using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Models;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Cascade;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
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
        FakeWebSocket? browserSocket = null,
        Action<CascadeSessionContext>? configureContext = null)
    {
        var context = new CascadeSessionContext
        {
            BrowserSocket = browserSocket ?? new FakeWebSocket([]),
            Persona = DeltaFixture.Load(),
            ResolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false),
            MenuMode = null,
            LinkedCts = new CancellationTokenSource(),
            CancellationToken = CancellationToken.None,
            SessionManager = null,
            State = state,
            PromptLoader = null,
            Detector = new TurnDetector(0.5, 500, 24000),
            AllowedVoices = new HashSet<string> { "marin" },
            Logger = NullLogger.Instance,
            TimeProvider = TimeProvider.System,
            TurnRegistryLock = new object(),
            SessionId = "session-1",
            ToolExecutor = toolExecutor,
            Identifiers = new SessionIdentifiers("test-alpha", "gpt-5-mini", "session-1", pipeline: "cascade"),
        };
        configureContext?.Invoke(context);
        return new(
            context,
            new RateLimitSettings(),
            new FoundryChatClient(new HttpClient(handler), Endpoint, new StaticBearerTokenProvider("fake-token")),
            toolDefinitions: []);
    }

    [Fact]
    public async Task Diagnostic_records_actual_tool_round_without_changing_history_or_returned_speech()
    {
        var call = new JsonObject
        {
            ["id"] = "synthetic-call",
            ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
        };
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", "Adding your combo.", new JsonArray(call))
            .EnqueueChatMessage("assistant", "What drink would you like?");
        var state = NewState();
        state.Messages.Add(CascadeChatMessage.User("NEVER-COPY-GUEST"));
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        var capture = new CascadeDiagnosticCapture("session-1", DateTimeOffset.UtcNow.AddMinutes(10),
            TimeProvider.System, logger) { ResponseId = "response-1" };
        var dispatcher = NewDispatcher(handler, new RecordingToolExecutor(["get_order"]), state,
            configureContext: context => context.Diagnostic = capture);

        Assert.Equal("What drink would you like?", await dispatcher.RunChatToolLoopAsync(CancellationToken.None));

        var secondRequest = JsonNode.Parse(handler.ChatRequestBodies[1])!;
        Assert.Equal(new[] { "user", "assistant", "tool" },
            secondRequest["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("Adding your combo.", secondRequest["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(4, state.Messages.Count);
        Assert.Equal("What drink would you like?", state.Messages[^1]["content"]!.GetValue<string>());
        Assert.Equal(4, logger.Lines.Count);
        Assert.DoesNotContain("NEVER-COPY-GUEST", string.Join("\n", logger.Lines));
        Assert.Contains("Adding your combo.", string.Join("\n", logger.Lines));
    }

    [Fact]
    public async Task Diagnostic_does_not_follow_resumed_session_binding()
    {
        var handler = new RoutingFoundryHandler().EnqueueChatMessage("assistant", "Restored.");
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        CascadeSessionContext? context = null;
        var dispatcher = NewDispatcher(handler, new RecordingToolExecutor([]), NewState(),
            configureContext: created =>
            {
                context = created;
                created.Diagnostic = new CascadeDiagnosticCapture("session-1",
                    DateTimeOffset.UtcNow.AddMinutes(10), TimeProvider.System, logger);
            });
        context!.SessionId = "resumed-session";

        Assert.Equal("Restored.", await dispatcher.RunChatToolLoopAsync(CancellationToken.None));

        Assert.Null(context.Diagnostic);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task RunChatToolLoopAsync_usesTheResumedExecutorInsteadOfTheProvisionalBinding()
    {
        var handler = new RoutingFoundryHandler()
            .EnqueueChatMessage("assistant", null, new JsonArray(new JsonObject
            {
                ["id"] = "resumed-call",
                ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
            }))
            .EnqueueChatMessage("assistant", "Restored.");
        var provisional = new RecordingToolExecutor(["get_order"]);
        var resumed = new RecordingToolExecutor(["get_order"]);
        CascadeSessionContext? context = null;
        var dispatcher = NewDispatcher(handler, provisional, NewState(),
            configureContext: created => context = created);
        context!.SessionId = "resumed-session";
        context.ToolExecutor = resumed;

        Assert.Equal("Restored.", await dispatcher.RunChatToolLoopAsync(CancellationToken.None));

        Assert.Empty(provisional.ReceivedArgumentsJson);
        Assert.Single(resumed.ReceivedArgumentsJson);
    }

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
