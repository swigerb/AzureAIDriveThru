using Backend.Configuration;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging;

namespace Backend.Tests.Sessions;

public sealed class RealtimeProcessorExtensionMessageTests
{
    private sealed class RecordingLogger : ILogger
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

    private static RealtimeProcessor CreateProcessor(SessionManager manager, ILogger? logger = null) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>(),
            toolExecutor: new StubToolExecutor([]),
            logger: logger,
            sessionManager: manager);

    private static SessionManager CreateSessionManager(Persona persona, string sessionId)
    {
        var manager = new SessionManager();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        var toolExecutor = new OrderToolExecutor(order, menu, promptLoader: null, maxItemQuantity: 10, maxOrderItems: 25);
        manager.CreateSession(sessionId, new FakeWebSocket([]), persona.Id, persona.Models.Realtime.Default, null, toolExecutor, persona.Voice.Default);
        return manager;
    }

    [Fact]
    public void TryHandleSessionOverrideExtensionMessage_applies_valid_machine_and_happy_hour_updates()
    {
        const string sessionId = "sess-override-ok";
        var manager = CreateSessionManager(DeltaFixture.Load("test-alpha"), sessionId);
        var logger = new RecordingLogger();
        var processor = CreateProcessor(manager, logger);

        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "soda_machine", ["status"] = "up" },
            sessionId));
        Assert.Equal("up", manager.GetMachineOverrides(sessionId)["soda_machine"]);

        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" },
            sessionId));
        Assert.Equal("on", manager.GetHappyHourMode(sessionId));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("extension.set_machine_status", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("extension.set_happy_hour_mode", StringComparison.Ordinal));
    }

    [Fact]
    public void TryHandleSessionOverrideExtensionMessage_drops_invalid_or_unsupported_updates_without_mutation()
    {
        const string sessionId = "sess-override-bad";
        var manager = CreateSessionManager(DeltaFixture.Load("test-delta"), sessionId);
        var logger = new RecordingLogger();
        var processor = CreateProcessor(manager, logger);

        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "unknown_machine", ["status"] = "up" },
            sessionId));
        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_machine_status",
            new System.Text.Json.Nodes.JsonObject { ["machine"] = "delta_machine", ["status"] = "operational" },
            sessionId));
        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "on" },
            sessionId));
        Assert.True(processor.TryHandleSessionOverrideExtensionMessage(
            "extension.set_happy_hour_mode",
            new System.Text.Json.Nodes.JsonObject { ["mode"] = "sometimes" },
            sessionId));

        Assert.Empty(manager.GetMachineOverrides(sessionId));
        Assert.Equal("auto", manager.GetHappyHourMode(sessionId));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("extension.set_machine_status", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("extension.set_happy_hour_mode", StringComparison.Ordinal));
    }
}
