using Backend.Configuration;
using Backend.Models;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Search;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

public sealed class RealtimeProcessorExtensionMessageTests
{
    private sealed class RecordingLogger : ILogger<RealtimeProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static RealtimeProcessor CreateProcessor(
        SessionManager manager,
        ILogger<RealtimeProcessor>? logger = null) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>(),
            toolExecutor: new StubToolExecutor([]),
            logger: logger ?? NullLogger<RealtimeProcessor>.Instance,
            rateLimitLogger: NullLogger<RateLimitRecovery>.Instance,
            nudgeLogger: NullLogger<NudgeScheduler>.Instance,
            sessionManager: manager);

    /// <summary>A never-invoked HTTP stand-in for the real Azure AI Search endpoint -- the
    /// ticket-push tests below never call the "search" tool, only extension.set_happy_hour_mode,
    /// so this only needs to exist to satisfy <see cref="SearchTool"/>'s constructor and must
    /// never actually be dispatched to.</summary>
    private sealed class NeverInvokedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("search was not expected to be called in this test.");
    }

    /// <summary>#309 (R2): wraps the session's tools in a <see cref="SessionToolExecutor"/> --
    /// exactly like <c>Program.cs</c>'s real <c>BuildSessionToolExecutor</c> -- so the session's
    /// <c>ToolExecutor</c> implements <see cref="Backend.Tools.IOrderTicketSource"/> and
    /// <see cref="SessionManager.GetOrderSummaryJson"/> can actually read it (a bare
    /// <see cref="OrderToolExecutor"/>, as used by the override-only tests above, does not
    /// implement that interface).</summary>
    private static SessionManager CreateSessionManager(Persona persona, string sessionId, out FakeWebSocket socket)
    {
        var manager = new SessionManager(
            new SessionsConfig(),
            TimeProvider.System,
            NullLogger<SessionManager>.Instance,
            NullLogger<ContextMonitor>.Instance);
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        var orderTools = new OrderToolExecutor(order, menu, promptLoader: null, maxItemQuantity: 10, maxOrderItems: 25);
        var search = new SearchTool(
            new HttpClient(new NeverInvokedHttpHandler()),
            new SearchEndpointConfig(
                endpoint: "https://fake-search.example.com", apiKey: "test-key", semanticConfiguration: "menuSemanticConfig",
                identifierField: "id", contentField: "description", embeddingField: "embedding", useVectorQuery: true, useSemanticRanker: false),
            SearchConfig.FromAppConfig(AppConfig.Load()),
            menu, promptLoader: null, indexName: "test-menu-items", personaId: persona.Id,
            logger: NullLogger<SearchTool>.Instance,
            effectiveMachineStatus: order.EffectiveMachineStatus);
        var toolExecutor = new SessionToolExecutor(orderTools, search);
        socket = new FakeWebSocket([]);
        manager.CreateSession(sessionId, socket, persona.Id, persona.Models.Realtime.Default, null, toolExecutor, persona.Voice.Default);
        return manager;
    }

    private static SessionManager CreateSessionManager(Persona persona, string sessionId) =>
        CreateSessionManager(persona, sessionId, out _);

    [Fact]
    public async Task TryHandleSessionOverrideExtensionMessage_applies_valid_machine_and_happy_hour_updates()
    {
        const string sessionId = "sess-override-ok";
        var manager = CreateSessionManager(DeltaFixture.Load("test-alpha"), sessionId);
        var logger = new RecordingLogger();
        var processor = CreateProcessor(manager, logger);

        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "soda_machine", ["status"] = "up" },
            sessionId, ct: TestContext.Current.CancellationToken));
        Assert.Equal("up", manager.GetMachineOverrides(sessionId)["soda_machine"]);

        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" },
            sessionId, ct: TestContext.Current.CancellationToken));
        Assert.Equal("on", manager.GetHappyHourMode(sessionId));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("extension.set_machine_status", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("extension.set_happy_hour_mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryHandleSessionOverrideExtensionMessage_drops_invalid_or_unsupported_updates_without_mutation()
    {
        const string sessionId = "sess-override-bad";
        var manager = CreateSessionManager(DeltaFixture.Load("test-delta"), sessionId);
        var logger = new RecordingLogger();
        var processor = CreateProcessor(manager, logger);

        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "unknown_machine", ["status"] = "up" },
            sessionId, ct: TestContext.Current.CancellationToken));
        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "delta_machine", ["status"] = "operational" },
            sessionId, ct: TestContext.Current.CancellationToken));
        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" },
            sessionId, ct: TestContext.Current.CancellationToken));
        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "sometimes" },
            sessionId, ct: TestContext.Current.CancellationToken));

        Assert.Empty(manager.GetMachineOverrides(sessionId));
        Assert.Equal("auto", manager.GetHappyHourMode(sessionId));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("extension.set_machine_status", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("extension.set_happy_hour_mode", StringComparison.Ordinal));
    }

    /// <summary>#309 (R2): a successful extension.set_happy_hour_mode must push the refreshed
    /// ticket to the browser right away -- the same extension.middle_tier_tool_response shape a
    /// successful update_order/get_order tool call already pushes -- with previous_item_id null
    /// (this is a synthetic push, not an answer to any pending model tool call; see Unity's #309
    /// review note mirrored in RealtimeProcessor's own doc comment on this push).</summary>
    [Fact]
    public async Task TryHandleSessionOverrideExtensionMessage_happyHourMode_PushesARefreshedTicketToTheBrowser()
    {
        const string sessionId = "sess-happy-hour-ticket-push";
        var manager = CreateSessionManager(DeltaFixture.Load("test-alpha"), sessionId, out var socket);
        var processor = CreateProcessor(manager);

        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" },
            sessionId, socket, TestContext.Current.CancellationToken));

        var pushed = Assert.Single(socket.SentMessages);
        var frame = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(pushed.Data)).RootElement;
        Assert.Equal("extension.middle_tier_tool_response", frame.GetProperty("type").GetString());
        Assert.Equal("get_order", frame.GetProperty("tool_name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, frame.GetProperty("previous_item_id").ValueKind);
        Assert.Equal(manager.GetOrderSummaryJson(sessionId), frame.GetProperty("tool_result").GetString());
    }

    /// <summary>#309 (R2) negative case: a DROPPED/invalid extension.set_happy_hour_mode must
    /// never push anything to the browser -- there is no fresh ticket to push since nothing
    /// changed.</summary>
    [Fact]
    public async Task TryHandleSessionOverrideExtensionMessage_invalidHappyHourMode_PushesNoTicket()
    {
        const string sessionId = "sess-happy-hour-ticket-no-push";
        var manager = CreateSessionManager(DeltaFixture.Load("test-delta"), sessionId, out var socket);
        var processor = CreateProcessor(manager);

        Assert.True(await processor.TryHandleSessionOverrideExtensionMessageAsync(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" }, // test-delta has no happy hour configured
            sessionId, socket, TestContext.Current.CancellationToken));

        Assert.Empty(socket.SentMessages);
    }
}
