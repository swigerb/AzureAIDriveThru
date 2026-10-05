using System.Net.WebSockets;
using System.Text.Json;
using Backend.Configuration;
using Backend.Ordering;
using Backend.Personas;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #15: <see cref="SessionManager"/> unit tests -- resume validation order (disabled →
/// malformed → unknown → expired → persona/model/mode mismatch → accept), single-use resume
/// credential rotation, idle/grace eviction, detached-LRU capping, and the idle-sweep close path.
/// Every test drives a <see cref="FakeTimeProvider"/> deterministically (same convention as
/// <see cref="Realtime.RateLimitRecoveryUnitTests"/>/<see cref="Realtime.NudgeSchedulerTests"/>),
/// never sleeping real wall-clock time.
/// </summary>
public sealed class SessionManagerTests
{
    private sealed class FakeToolExecutor : IToolExecutor
    {
        public IReadOnlyList<string> ToolNames => [];
        public Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default) =>
            throw new NotSupportedException("not exercised by SessionManager tests");
    }

    private static SessionManager NewManager(
        FakeTimeProvider time,
        double idleTimeoutSeconds = 300,
        bool resumeEnabled = true,
        double graceSeconds = 120,
        int maxDetached = 20,
        int historyTurns = 6,
        int historyChars = 2000) =>
        new(
            new SessionsConfig(
                idleTimeoutSeconds: idleTimeoutSeconds,
                resumeEnabled: resumeEnabled,
                graceSeconds: graceSeconds,
                maxDetached: maxDetached,
                historyTurns: historyTurns,
                historyChars: historyChars),
            time);

    private static FakeWebSocket NewSocket() => new([]);

    // ── Session lifecycle basics ──────────────────────────────────────────────────────────────

    [Fact]
    public void CreateSession_ThenGetVoice_ReturnsTheBoundVoice()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        mgr.CreateSession("s1", NewSocket(), "persona-a", "model-a", null, new FakeToolExecutor(), "alloy");
        Assert.Equal("alloy", mgr.GetVoice("s1"));
    }

    [Fact]
    public void GetVoice_UnknownSession_ReturnsEmptyString()
    {
        var mgr = NewManager(new FakeTimeProvider());
        Assert.Equal("", mgr.GetVoice("nope"));
    }

    [Fact]
    public void SetVoice_UpdatesTheBoundVoice()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        mgr.CreateSession("s1", NewSocket(), "persona-a", "model-a", null, new FakeToolExecutor(), "alloy");
        mgr.SetVoice("s1", "verse");
        Assert.Equal("verse", mgr.GetVoice("s1"));
    }

    [Fact]
    public void SetMachineStatus_and_SetHappyHourMode_delegate_to_the_sessions_order_state()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        var persona = DeltaFixture.Load("test-alpha");
        var toolExecutor = new OrderToolExecutor(
            PersonaOrderFactory.CreateOrderState(persona),
            PersonaOrderFactory.GetMenuCatalog(persona),
            promptLoader: null,
            maxItemQuantity: 10,
            maxOrderItems: 25);

        mgr.CreateSession("s1", NewSocket(), persona.Id, persona.Models.Realtime.Default, null, toolExecutor, persona.Voice.Default);

        Assert.True(mgr.SetMachineStatus("s1", "soda_machine", "up"));
        Assert.Equal("up", mgr.GetMachineOverrides("s1")["soda_machine"]);
        Assert.True(mgr.SetHappyHourMode("s1", "on"));
        Assert.Equal("on", mgr.GetHappyHourMode("s1"));
    }

    // ── Context monitor lifecycle (issue #13 tail) ────────────────────────────────────────────

    [Fact]
    public void CreateSession_AlsoCreatesAContextMonitor()
    {
        var mgr = NewManager(new FakeTimeProvider());
        mgr.CreateSession("s1", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");
        Assert.NotNull(mgr.GetContextMonitor("s1"));
    }

    [Fact]
    public void GetContextMonitor_UnknownOrNullSessionId_ReturnsNull()
    {
        var mgr = NewManager(new FakeTimeProvider());
        Assert.Null(mgr.GetContextMonitor("nope"));
        Assert.Null(mgr.GetContextMonitor(null));
    }

    [Fact]
    public void EndSession_RemovesTheContextMonitor()
    {
        var mgr = NewManager(new FakeTimeProvider());
        mgr.CreateSession("s1", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");
        Assert.NotNull(mgr.GetContextMonitor("s1"));
        mgr.EndSession("s1", "test teardown");
        Assert.Null(mgr.GetContextMonitor("s1"));
    }

    // ── Resume credential: issuance, single-use, disabled ─────────────────────────────────────

    [Fact]
    public void IssueResumeId_WhenResumeDisabled_ReturnsNull()
    {
        var mgr = NewManager(new FakeTimeProvider(), resumeEnabled: false);
        mgr.CreateSession("s1", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");
        Assert.Null(mgr.IssueResumeId("s1"));
    }

    [Fact]
    public void IssueResumeId_IsAtLeast32CharsAndUniquePerCall()
    {
        var mgr = NewManager(new FakeTimeProvider());
        mgr.CreateSession("s1", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");
        var first = mgr.IssueResumeId("s1");
        var second = mgr.IssueResumeId("s1");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(first!.Length, 32, 128);
        Assert.NotEqual(first, second); // rotation invalidates the previous credential
    }

    [Fact]
    public void IssueResumeId_Reissue_InvalidatesThePreviousOne()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        var ws1 = NewSocket();
        mgr.CreateSession("s1", ws1, "p", "m", null, new FakeToolExecutor(), "alloy");
        var stale = mgr.IssueResumeId("s1")!;
        mgr.IssueResumeId("s1"); // rotate -- `stale` must no longer resolve

        var outcome = mgr.TryResume(NewSocket(), stale, "p", "m", null, "provisional");
        Assert.False(outcome.Accepted);
        Assert.Equal("unknown", outcome.Reason);
    }

    // ── TryResume validation order ────────────────────────────────────────────────────────────

    [Fact]
    public void TryResume_WhenResumeDisabled_RejectsAsDisabled()
    {
        var mgr = NewManager(new FakeTimeProvider(), resumeEnabled: false);
        var outcome = mgr.TryResume(NewSocket(), "x", "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("disabled", outcome.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void TryResume_WithMalformedPresentedId_Rejects(string? presentedId)
    {
        var mgr = NewManager(new FakeTimeProvider());
        var outcome = mgr.TryResume(NewSocket(), presentedId, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("malformed", outcome.Reason);
    }

    [Fact]
    public void TryResume_WithUnknownId_Rejects()
    {
        var mgr = NewManager(new FakeTimeProvider());
        var unknownButWellFormed = new string('a', 64);
        var outcome = mgr.TryResume(NewSocket(), unknownButWellFormed, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("unknown", outcome.Reason);
    }

    [Fact]
    public void TryResume_AfterIdleExpiry_RejectsAsExpired_AndEndsTheSession()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 60, graceSeconds: 600); // grace outlives idle on paper
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws, "s1", "socket closed");

        time.Advance(TimeSpan.FromSeconds(61)); // past the idle deadline even though grace would allow more

        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("expired", outcome.Reason);

        // The expired session was ended as a side effect -- a second attempt reports "unknown",
        // not "expired" again (there is nothing left to be expired).
        var again = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov2");
        Assert.Equal("unknown", again.Reason);
    }

    [Theory]
    [InlineData("other-persona", "m", null, "persona_mismatch")]
    [InlineData("p", "other-model", null, "model_mismatch")]
    [InlineData("p", "m", "other-mode", "mode_mismatch")]
    public void TryResume_WithMismatchedBinding_Rejects(
        string requestedPersona, string requestedModel, string? requestedMode, string expectedReason)
    {
        var mgr = NewManager(new FakeTimeProvider());
        mgr.CreateSession("s1", NewSocket(), "p", "m", "mode", new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;

        var outcome = mgr.TryResume(NewSocket(), resumeId, requestedPersona, requestedModel, requestedMode, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal(expectedReason, outcome.Reason);

        // A rejected resume must NOT consume the credential -- a correctly-bound retry still works.
        var retry = mgr.TryResume(NewSocket(), resumeId, "p", "m", "mode", "prov");
        Assert.True(retry.Accepted);
    }

    [Fact]
    public void TryResume_HappyPath_AcceptsAndReturnsBoundState_AndRotatesTheCredential()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        var toolExecutor = new FakeToolExecutor();
        mgr.CreateSession("s1", NewSocket(), "p", "m", "mode", toolExecutor, "verse");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.SetVoice("s1", "verse2");

        var newSocket = NewSocket();
        var outcome = mgr.TryResume(newSocket, resumeId, "p", "m", "mode", "prov");

        Assert.True(outcome.Accepted);
        Assert.Equal("s1", outcome.SessionId);
        Assert.Equal("verse2", outcome.Voice);
        Assert.Same(toolExecutor, outcome.ToolExecutor);
        Assert.False(outcome.ConversationStarted); // never marked started in this test
        Assert.NotNull(outcome.ResumeId);
        Assert.NotEqual(resumeId, outcome.ResumeId); // single-use: a fresh credential was minted

        // The consumed id can never be replayed.
        var replay = mgr.TryResume(NewSocket(), resumeId, "p", "m", "mode", "prov3");
        Assert.False(replay.Accepted);
        Assert.Equal("unknown", replay.Reason);
    }

    [Fact]
    public void TryResume_EndsTheCallersProvisionalSession_WhenDifferentFromTheResumedOne()
    {
        var mgr = NewManager(new FakeTimeProvider());
        mgr.CreateSession("s1", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.CreateSession("provisional", NewSocket(), "p", "m", null, new FakeToolExecutor(), "alloy");

        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "provisional");
        Assert.True(outcome.Accepted);

        // The provisional session must be gone -- its own (now-orphaned) voice lookup resolves to
        // "" exactly like any other unknown session id.
        Assert.Equal("", mgr.GetVoice("provisional"));
    }

    [Fact]
    public void TryResume_WhenPriorSocketStillAttached_ReturnsItAsStaleWs_ForTheCallerToSupersede()
    {
        var mgr = NewManager(new FakeTimeProvider());
        var oldWs = NewSocket();
        mgr.CreateSession("s1", oldWs, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        // Deliberately NOT detached -- the old socket is still "attached" when the resume arrives
        // (e.g. a duplicate tab), so the manager must hand it back for a non-blocking 4002 close.

        var newWs = NewSocket();
        var outcome = mgr.TryResume(newWs, resumeId, "p", "m", null, "prov");

        Assert.True(outcome.Accepted);
        Assert.Same(oldWs, outcome.StaleWs);
    }

    [Fact]
    public void TryResume_WhenNoPriorSocketWasAttached_StaleWsIsNull()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws, "s1", "socket closed"); // properly detached first, as a real reconnect would be

        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.True(outcome.Accepted);
        Assert.Null(outcome.StaleWs);
    }

    // ── Detach / grace / idle expiry ──────────────────────────────────────────────────────────

    [Fact]
    public void Detach_WithResumeDisabled_EndsTheSessionImmediately()
    {
        var mgr = NewManager(new FakeTimeProvider(), resumeEnabled: false);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        mgr.Detach(ws, "s1", "socket closed");
        Assert.Equal("", mgr.GetVoice("s1")); // gone
    }

    [Fact]
    public void Detach_FromAWrongOrStaleSocket_IsANoOp()
    {
        var mgr = NewManager(new FakeTimeProvider());
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        mgr.Detach(NewSocket(), "s1", "stale close handler"); // NOT the attached socket
        Assert.Equal("alloy", mgr.GetVoice("s1")); // untouched -- still live and attached
    }

    [Fact]
    public void Detach_ThenResumeWithinGrace_Succeeds()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 300, graceSeconds: 60);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws, "s1", "socket closed");

        time.Advance(TimeSpan.FromSeconds(59));
        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.True(outcome.Accepted);
    }

    [Fact]
    public void Detach_ThenGraceExpires_ResumeIsRejectedAsExpired()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 300, graceSeconds: 60);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws, "s1", "socket closed");

        time.Advance(TimeSpan.FromSeconds(61));
        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("expired", outcome.Reason);
    }

    [Fact]
    public void Detach_WhenGraceWouldExceedRemainingIdleBudget_EndsImmediately()
    {
        // idle_timeout=10s, grace=600s, and the session has already been idle 9s before detach --
        // the grace hold must be capped at the 1s of idle budget actually left, not the full 600s.
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 10, graceSeconds: 600);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;

        time.Advance(TimeSpan.FromSeconds(9));
        mgr.Detach(ws, "s1", "socket closed");

        time.Advance(TimeSpan.FromSeconds(2)); // past the 1s of remaining idle budget
        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("expired", outcome.Reason);
    }

    [Fact]
    public void Detach_EvictsOldestWhenMaxDetachedExceeded()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, maxDetached: 1, graceSeconds: 600, idleTimeoutSeconds: 6000);

        var ws1 = NewSocket();
        mgr.CreateSession("s1", ws1, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resume1 = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws1, "s1", "socket closed");

        var ws2 = NewSocket();
        mgr.CreateSession("s2", ws2, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resume2 = mgr.IssueResumeId("s2")!;
        mgr.Detach(ws2, "s2", "socket closed"); // pushes the LRU over maxDetached=1, evicting s1

        var outcome1 = mgr.TryResume(NewSocket(), resume1, "p", "m", null, "prov1");
        Assert.False(outcome1.Accepted);
        Assert.Equal("unknown", outcome1.Reason); // evicted, not merely expired

        var outcome2 = mgr.TryResume(NewSocket(), resume2, "p", "m", null, "prov2");
        Assert.True(outcome2.Accepted); // the newer detached session survives
    }

    [Fact]
    public void SweepDetached_EndsSessionsPastGraceOrIdleBudget_AndReturnsTheCount()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, graceSeconds: 10, idleTimeoutSeconds: 600);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");
        var resumeId = mgr.IssueResumeId("s1")!;
        mgr.Detach(ws, "s1", "socket closed");

        Assert.Equal(0, mgr.SweepDetached()); // not expired yet
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, mgr.SweepDetached());
        Assert.Equal(0, mgr.SweepDetached()); // idempotent -- already gone

        var outcome = mgr.TryResume(NewSocket(), resumeId, "p", "m", null, "prov");
        Assert.False(outcome.Accepted);
        Assert.Equal("unknown", outcome.Reason);
    }

    [Fact]
    public async Task CloseIdleSessionsAsync_ClosesAttachedIdleSocketsWith4000_AndEndsTheSession()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 30);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");

        time.Advance(TimeSpan.FromSeconds(31));
        await mgr.CloseIdleSessionsAsync(TestContext.Current.CancellationToken);

        Assert.True(ws.CloseOutputCalled);
        Assert.Equal((WebSocketCloseStatus)SessionManager.IdleCloseCode, ws.ClosedWithStatus);
        Assert.Equal(SessionManager.IdleCloseReason, ws.ClosedWithDescription);
        Assert.Equal("", mgr.GetVoice("s1")); // session itself is gone, not merely detached
    }

    [Fact]
    public async Task CloseIdleSessionsAsync_LeavesActiveSessionsUntouched()
    {
        var time = new FakeTimeProvider();
        var mgr = NewManager(time, idleTimeoutSeconds: 30);
        var ws = NewSocket();
        mgr.CreateSession("s1", ws, "p", "m", null, new FakeToolExecutor(), "alloy");

        time.Advance(TimeSpan.FromSeconds(10));
        mgr.TouchActivity("s1");
        time.Advance(TimeSpan.FromSeconds(25)); // 25s since touch, still under the 30s timeout
        await mgr.CloseIdleSessionsAsync(TestContext.Current.CancellationToken);

        Assert.False(ws.CloseOutputCalled);
        Assert.Equal("alloy", mgr.GetVoice("s1"));
    }

    // ── Rehydration / nudge text builders (pure static helpers) ──────────────────────────────

    [Fact]
    public void BuildRehydrationText_IncludesOrderJsonAndHistory_OldestFirst()
    {
        var text = SessionManager.BuildRehydrationText(
            "{\"items\":[\"Tots\"]}",
            [("guest", "I'll have tots"), ("assistant", "Anything else?")],
            "the assistant");

        Assert.Contains("{\"items\":[\"Tots\"]}", text);
        Assert.Contains("Guest: I'll have tots", text);
        Assert.Contains("The assistant: Anything else?", text);
        Assert.True(text.IndexOf("I'll have tots", StringComparison.Ordinal) <
                    text.IndexOf("Anything else?", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRehydrationText_WithNoHistory_SaysNoneRecorded()
    {
        var text = SessionManager.BuildRehydrationText("{}", [], "the assistant");
        Assert.Contains("(none recorded)", text);
    }

    [Fact]
    public void BuildNudgeText_FormatsTheRoleName()
    {
        var text = SessionManager.BuildNudgeText("the assistant");
        Assert.Contains("the assistant", text);
    }
}
