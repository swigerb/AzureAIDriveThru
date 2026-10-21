using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Cascade;
using Backend.Models;
using Backend.Personas;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

public sealed class CascadeResumeCoordinatorTests
{
    private static readonly Lazy<Persona> TestPersona = new(() => DeltaFixture.Load("test-alpha"));

    [Fact]
    public async Task NegotiateAsync_resumeAdoptsSessionIdentityAndExecutorBeforeAnnouncing()
    {
        var persona = TestPersona.Value;
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);
        var sessionManager = new SessionManager(
            new SessionsConfig(),
            TimeProvider.System,
            NullLogger<SessionManager>.Instance,
            NullLogger<ContextMonitor>.Instance);
        var priorSocket = new FakeWebSocket([]);
        var resumedExecutor = new StubToolExecutor([]);
        var resumedIdentifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, "prior-token", pipeline: "cascade");
        sessionManager.CreateSession(
            "prior-session", priorSocket, persona.Id, resolvedModel.Id, null,
            resumedExecutor, "echo", identifiers: resumedIdentifiers);
        sessionManager.MarkConversationStarted("prior-session");
        var resumeId = sessionManager.IssueResumeId("prior-session")!;
        sessionManager.Detach(priorSocket, "prior-session", "test setup");

        var resumeFrame = System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "extension.resume",
            ["resume_id"] = resumeId,
        }.ToJsonString());
        var browser = new FakeWebSocket([(resumeFrame, true, WebSocketMessageType.Text)]);
        var state = new CascadeProcessor.CascadeSessionState
        {
            SessionId = "new-session",
            Deployment = "chat-dep",
            RoleName = persona.RoleName,
        };
        var provisionalExecutor = new StubToolExecutor([]);
        var provisionalIdentifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, "new-token", pipeline: "cascade");
        using var linkedCts = new CancellationTokenSource();
        sessionManager.CreateSession(
            "new-session", browser, persona.Id, resolvedModel.Id, null,
            provisionalExecutor, "marin", linkedCts, provisionalIdentifiers);
        var context = new CascadeSessionContext
        {
            BrowserSocket = browser,
            Persona = persona,
            ResolvedModel = resolvedModel,
            MenuMode = null,
            LinkedCts = linkedCts,
            CancellationToken = linkedCts.Token,
            SessionManager = sessionManager,
            State = state,
            Logger = NullLogger.Instance,
            TimeProvider = TimeProvider.System,
            PromptLoader = null,
            Detector = new TurnDetector(0.5, 500, 24000),
            AllowedVoices = new HashSet<string> { "marin", "echo" },
            TurnRegistryLock = new object(),
            SessionId = "new-session",
            ToolExecutor = provisionalExecutor,
            Identifiers = provisionalIdentifiers,
        };
        context.TurnCoordinator = new CascadeTurnCoordinator(context);

        var leftover = await new CascadeResumeCoordinator(context).NegotiateAsync();

        Assert.Null(leftover);
        Assert.Equal("prior-session", context.SessionId);
        Assert.Same(resumedExecutor, context.ToolExecutor);
        Assert.Same(resumedIdentifiers, context.Identifiers);
        Assert.Equal("echo", state.Voice);
        var frame = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(
            Assert.Single(browser.SentMessages).Data))!.AsObject();
        Assert.Equal("extension.session_resumed", (string?)frame["type"]);
    }

    [Fact]
    public async Task NegotiateAsync_withoutFirstFrameTimeout_announcesFreshSessionAndOwnsGreetingTask()
    {
        var browser = new FakeWebSocket([]);
        var persona = TestPersona.Value;
        var resolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false);
        var sessionManager = new SessionManager(
            new SessionsConfig(firstFrameTimeoutSeconds: 0),
            TimeProvider.System,
            NullLogger<SessionManager>.Instance,
            NullLogger<ContextMonitor>.Instance);
        var state = new CascadeProcessor.CascadeSessionState
        {
            SessionId = "session-1",
            Deployment = "chat-dep",
            RoleName = persona.RoleName,
        };
        using var linkedCts = new CancellationTokenSource();
        var context = new CascadeSessionContext
        {
            BrowserSocket = browser,
            Persona = persona,
            ResolvedModel = resolvedModel,
            MenuMode = null,
            LinkedCts = linkedCts,
            CancellationToken = linkedCts.Token,
            SessionManager = sessionManager,
            State = state,
            Logger = NullLogger.Instance,
            TimeProvider = TimeProvider.System,
            PromptLoader = null,
            Detector = new TurnDetector(0.5, 500, 24000),
            AllowedVoices = new HashSet<string> { "marin", "echo" },
            TurnRegistryLock = new object(),
            SessionId = "session-1",
            ToolExecutor = new StubToolExecutor([]),
            Identifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, "session-1", pipeline: "cascade"),
        };
        context.TurnCoordinator = new CascadeTurnCoordinator(context);

        var leftover = await new CascadeResumeCoordinator(context).NegotiateAsync();

        Assert.Null(leftover);
        Assert.NotNull(state.CurrentTurnTask);
        var frame = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(
            Assert.Single(browser.SentMessages).Data))!.AsObject();
        Assert.Equal("extension.session_metadata", (string?)frame["type"]);
    }
}
