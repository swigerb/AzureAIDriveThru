using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="ResumeCoordinator"/>, extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/>'s own <c>RejectLateResumeAsync</c>,
/// <c>HandleResumeFirstFrameAsync</c>, <c>AnnounceAfterFirstFrameDecisionAsync</c> and
/// <c>SendFreshSessionMetadataAsync</c> local closures. These exercise the resume/fresh
/// announce paths directly against a hand-built <see cref="ResumeOutcome"/> (rather than a real
/// <see cref="SessionManager.TryResume"/> handshake) so they stay focused on this collaborator's
/// own wire behaviour, not the registry's.
/// </summary>
public sealed class ResumeCoordinatorTests
{
    private static RealtimeProcessor.RealtimeSessionState NewState() => new()
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
        Identifiers = new SessionIdentifiers("test-persona", "gpt-realtime"),
        ToolExecutor = new StubToolExecutor([]),
    };

    private static SessionManager NewSessionManager() => new(
        new SessionsConfig(),
        TimeProvider.System,
        NullLogger<SessionManager>.Instance,
        NullLogger<ContextMonitor>.Instance);

    private static ResumeCoordinator NewCoordinator(
        FakeWebSocket browser, FakeWebSocket upstream, RealtimeProcessor.RealtimeSessionState state, SessionManager? sessionManager) =>
        new(
            browser, upstream, DeltaFixture.Load("test-alpha"), new ResolvedModel("gpt-realtime", "realtime", "gpt-realtime-2.1", false),
            menuMode: null, "session-1", new CancellationTokenSource(), state, sessionManager,
            buildVoiceUpdateFrame: voice => new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["voice"] = voice } },
            NullLogger.Instance, CancellationToken.None);

    private static List<string> TextFrames(FakeWebSocket socket) =>
        socket.SentMessages
            .Where(m => m.MessageType == WebSocketMessageType.Text)
            .Select(m => System.Text.Encoding.UTF8.GetString(m.Data))
            .ToList();

    [Fact]
    public async Task RejectLateResumeAsync_sends_resume_rejected_and_no_fresh_metadata_when_none_was_ever_announced()
    {
        var state = NewState();
        var browser = new FakeWebSocket([]);
        var upstream = new FakeWebSocket([]);
        var coordinator = NewCoordinator(browser, upstream, state, sessionManager: null);

        await coordinator.RejectLateResumeAsync("not this connection's first frame");

        var frame = JsonNode.Parse(Assert.Single(TextFrames(browser)))!.AsObject();
        Assert.Equal("extension.resume_rejected", (string?)frame["type"]);
        Assert.Equal("not_first_frame", (string?)frame["reason"]);
    }

    [Fact]
    public async Task RejectLateResumeAsync_re_announces_fresh_metadata_when_a_baton_was_already_held()
    {
        var state = NewState();
        state.MetadataAnnounced = true;
        var browser = new FakeWebSocket([]);
        var upstream = new FakeWebSocket([]);
        var sessionManager = NewSessionManager();
        var coordinator = NewCoordinator(browser, upstream, state, sessionManager);

        await coordinator.RejectLateResumeAsync("first-frame timeout already decided");

        var frames = TextFrames(browser);
        Assert.Equal(2, frames.Count);
        Assert.Equal("extension.resume_rejected", (string?)JsonNode.Parse(frames[0])!["type"]);
        Assert.Equal("extension.session_metadata", (string?)JsonNode.Parse(frames[1])!["type"]);
    }

    [Fact]
    public async Task AnnounceAfterFirstFrameDecisionAsync_sends_fresh_session_metadata_when_not_resumed()
    {
        var state = NewState();
        state.FirstFrameDecision.TrySetResult(false);
        var browser = new FakeWebSocket([]);
        var upstream = new FakeWebSocket([]);
        var sessionManager = NewSessionManager();
        var coordinator = NewCoordinator(browser, upstream, state, sessionManager);

        await coordinator.AnnounceAfterFirstFrameDecisionAsync();

        var frame = JsonNode.Parse(Assert.Single(TextFrames(browser)))!.AsObject();
        Assert.Equal("extension.session_metadata", (string?)frame["type"]);
        Assert.True(state.MetadataAnnounced);
    }

    [Fact]
    public async Task AnnounceAfterFirstFrameDecisionAsync_announces_resume_without_rehydration_when_conversation_had_not_started()
    {
        var state = NewState();
        state.ResumeAnnounce = new ResumeOutcome(Accepted: true, ResumeId: "resume-123", ConversationStarted: false);
        state.FirstFrameDecision.TrySetResult(true);
        var browser = new FakeWebSocket([]);
        var upstream = new FakeWebSocket([]);
        var coordinator = NewCoordinator(browser, upstream, state, sessionManager: null);

        await coordinator.AnnounceAfterFirstFrameDecisionAsync();

        var browserFrame = JsonNode.Parse(Assert.Single(TextFrames(browser)))!.AsObject();
        Assert.Equal("extension.session_resumed", (string?)browserFrame["type"]);
        Assert.Equal("resume-123", (string?)browserFrame["resume_id"]);
        Assert.True(state.MetadataAnnounced);
        // No conversation yet -- no rehydration item, no voice restore sent upstream.
        Assert.Empty(TextFrames(upstream));
    }

    [Fact]
    public async Task AnnounceAfterFirstFrameDecisionAsync_rehydrates_and_restores_voice_when_conversation_had_started()
    {
        var state = NewState();
        state.Voice = "amuch";
        state.ResumeAnnounce = new ResumeOutcome(
            Accepted: true,
            ResumeId: "resume-456",
            ConversationStarted: true,
            ToolExecutor: new StubToolExecutor([]),
            RecentTurns: [("guest", "one Route 44 please")]);
        state.FirstFrameDecision.TrySetResult(true);
        var browser = new FakeWebSocket([]);
        var upstream = new FakeWebSocket([]);
        var coordinator = NewCoordinator(browser, upstream, state, sessionManager: null);

        await coordinator.AnnounceAfterFirstFrameDecisionAsync();

        var upstreamFrames = TextFrames(upstream);
        Assert.Equal(2, upstreamFrames.Count);
        Assert.Equal("conversation.item.create", (string?)JsonNode.Parse(upstreamFrames[0])!["type"]);
        var voiceFrame = JsonNode.Parse(upstreamFrames[1])!.AsObject();
        Assert.Equal("session.update", (string?)voiceFrame["type"]);
        Assert.Equal("amuch", (string?)voiceFrame["session"]!["voice"]);
    }
}
