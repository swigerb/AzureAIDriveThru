using System.Net.WebSockets;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Rick's #244 round-2 review, issue 1 (BLOCKING): the supersede-close used to run
/// <c>await CloseOutputIfOpenAsync(staleWs, ...); outcome.StaleCts?.Cancel();</c> inline in the
/// NEW (winning) connection's own call stack (<c>HandleResumeFirstFrameAsync</c>), reasoning that
/// <see cref="WebSocket.CloseOutputAsync"/> "can never hang" because it never waits for the
/// peer's own close handshake reply. Rick disproved that with a probe over a genuinely
/// non-draining transport (a half-open network-switch where the stale socket's outbound isn't
/// draining, e.g. assistant audio was streaming when the network died): <c>CloseOutputAsync</c>
/// is still a SEND, and a send can block on a full receive window with no bound at all. Awaited
/// inline, that blocked not just the stale connection's own teardown but the NEW connection's own
/// first-frame handling -- the guest would see <c>session_resumed</c> then silence until an
/// eventual 4000 idle close deleted the order.
///
/// <see cref="FramePump.CloseSupersededStaleConnectionAsync"/> was extracted specifically
/// so this can be proven WITHOUT any real sockets or real TCP backpressure (see
/// <c>ResumeHandshakeTests.Resuming_from_a_still_attached_socket_supersedes_it_with_4002</c> in the
/// conformance suite for the end-to-end counterpart, which drives this same bug through an actual
/// non-draining browser peer): a <see cref="FakeWebSocket"/> rigged to hang on
/// <c>CloseOutputAsync</c> until its own <see cref="CancellationToken"/> fires is a deterministic,
/// environment-independent stand-in for "the peer never drains" -- no timing, no load-sensitivity,
/// no flakiness.
/// </summary>
public sealed class CloseSupersededStaleConnectionAsyncTests
{
    // Generous relative to FramePump.SupersededCloseTimeout itself (2s) so this never
    // flakes under CI load while still being far short of "effectively unbounded."
    private static readonly TimeSpan AssertionDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Completes_within_its_own_timeout_even_when_the_stale_sockets_close_output_hangs_forever()
    {
        var staleWs = new FakeWebSocket([], hangCloseOutputUntilCancelled: true);
        using var staleCts = new CancellationTokenSource();

        var task = FramePump.CloseSupersededStaleConnectionAsync(
            staleWs, staleCts, TimeSpan.FromMilliseconds(200), NullLogger<RealtimeProcessor>.Instance);

        var completed = await Task.WhenAny(task, Task.Delay(AssertionDeadline, TestContext.Current.CancellationToken));
        Assert.True(
            ReferenceEquals(completed, task),
            "CloseSupersededStaleConnectionAsync did not complete within its own timeout plus " +
            $"{AssertionDeadline} of slack -- it is blocking on the stale peer's non-draining " +
            "close output again, exactly the hazard Rick's round-2 review of #244 found.");
        await task; // Propagate any unexpected exception (there should be none).

        Assert.True(staleWs.CloseOutputCalled, "Expected the close-output send to have been attempted.");
        Assert.True(
            staleCts.IsCancellationRequested,
            "Expected the stale connection's own CTS to be cancelled in the finally, regardless " +
            "of whether the close-output send itself ever completed.");
    }

    [Fact]
    public async Task Still_cancels_the_stale_cts_when_the_close_output_completes_normally()
    {
        var staleWs = new FakeWebSocket([]);
        using var staleCts = new CancellationTokenSource();

        // CI run 37210749254's re-review: a real healthy peer answers the close frame almost
        // immediately (its own ReceiveAsync auto-completes the handshake), so simulate that here
        // rather than leaving the fake parked in CloseSent forever -- otherwise this test would
        // wait out the FULL closeTimeout budget below for no reason (see
        // Does_not_cancel_the_stale_cts_until_the_socket_settles_or_the_timeout_elapses for the
        // test that actually pins down the settle-before-cancel ordering).
        var settleSimulation = Task.Run(async () =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            staleWs.SimulatePeerAnsweredClose();
        }, TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await FramePump.CloseSupersededStaleConnectionAsync(
            staleWs, staleCts, TimeSpan.FromSeconds(5), NullLogger<RealtimeProcessor>.Instance);
        sw.Stop();
        await settleSimulation;

        Assert.True(staleWs.CloseOutputCalled);
        Assert.Equal((WebSocketCloseStatus)SessionManager.SupersededCloseCode, staleWs.ClosedWithStatus);
        Assert.Equal(SessionManager.SupersededCloseReason, staleWs.ClosedWithDescription);
        Assert.True(staleCts.IsCancellationRequested);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected the settle-wait to finish promptly once the peer answered the close, not " +
            $"block for anywhere near the full 5s closeTimeout budget; took {sw.Elapsed}.");
    }

    /// <summary>
    /// Mutation-check pinning down the actual round-2 re-review fix: <c>staleCts</c> must NOT be
    /// cancelled the instant the courtesy close frame is sent -- only once the stale socket has
    /// settled (the peer's own answering close observed) or the close timeout has genuinely
    /// elapsed. A regression back to "cancel immediately after CloseOutputAsync returns" (the
    /// shape that raced <c>Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002</c>
    /// under CI load) would make <c>staleCts.IsCancellationRequested</c> already true well before
    /// <see cref="FakeWebSocket.SimulatePeerAnsweredClose"/> is ever called below.
    /// </summary>
    [Fact]
    public async Task Does_not_cancel_the_stale_cts_until_the_socket_settles_or_the_timeout_elapses()
    {
        var staleWs = new FakeWebSocket([]);
        using var staleCts = new CancellationTokenSource();

        var closeTask = FramePump.CloseSupersededStaleConnectionAsync(
            staleWs, staleCts, TimeSpan.FromSeconds(5), NullLogger<RealtimeProcessor>.Instance);

        // FakeWebSocket's non-hanging CloseOutputAsync completes synchronously, so by the time we
        // get here the close frame is long sent and the socket is parked in CloseSent -- but
        // nothing has simulated the peer answering yet, so the settle-wait must still be pending
        // and staleCts must still be un-cancelled.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketState.CloseSent, staleWs.State);
        Assert.False(
            staleCts.IsCancellationRequested,
            "staleCts was cancelled before the stale socket settled or its own timeout elapsed -- " +
            "this re-introduces the abort race a healthy peer's close-frame delivery can lose " +
            "under load (CI run 37210749254).");

        staleWs.SimulatePeerAnsweredClose();
        await closeTask;
        Assert.True(staleCts.IsCancellationRequested);
    }

    [Fact]
    public async Task Tolerates_a_null_staleCts()
    {
        // outcome.StaleCts is nullable on ResumeOutcome; the caller must not be able to throw
        // (and therefore never reach the finally / never return) just because a resume somehow
        // produced a stale socket with no CTS attached. Simulate the peer answering promptly so
        // this doesn't wait out the full closeTimeout budget in the settle-wait for no reason.
        var staleWs = new FakeWebSocket([]);
        var settleSimulation = Task.Run(async () =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            staleWs.SimulatePeerAnsweredClose();
        }, TestContext.Current.CancellationToken);

        await FramePump.CloseSupersededStaleConnectionAsync(
            staleWs, staleCts: null, TimeSpan.FromSeconds(2), NullLogger<RealtimeProcessor>.Instance);
        await settleSimulation;

        Assert.True(staleWs.CloseOutputCalled);
    }

    /// <summary>
    /// Mutation-check (explicit instruction in Rick's round-2 review): reverting to the OLD
    /// inline-await shape -- awaiting the close-output send directly, with no bounding timeout --
    /// must make the equivalent of this test fail/hang. This test doesn't call production code
    /// via a reverted git diff (that would require literally breaking the build to prove a
    /// negative); instead it proves the SAME property the real mutation-check proved manually
    /// during development: an unbounded awaited close on this exact hanging fake never completes,
    /// which is exactly what the pre-fix code's call shape reduces to. Kept as a permanent
    /// regression guard for the specific failure shape (unbounded await, no CancellationToken)
    /// rather than only a one-time manual check.
    /// </summary>
    [Fact]
    public async Task Mutation_check_an_unbounded_await_on_the_same_hanging_fake_never_completes()
    {
        var staleWs = new FakeWebSocket([], hangCloseOutputUntilCancelled: true);

        var unboundedClose = staleWs.CloseOutputAsync(
            (WebSocketCloseStatus)SessionManager.SupersededCloseCode, SessionManager.SupersededCloseReason,
            CancellationToken.None);

        var completed = await Task.WhenAny(
            unboundedClose, Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
        Assert.False(
            ReferenceEquals(completed, unboundedClose),
            "An unbounded CloseOutputAsync against a non-draining peer was expected to still be " +
            "pending after 500ms -- if this now completes, FakeWebSocket's hang simulation (and " +
            "therefore every other assertion in this file) no longer proves what it claims to.");
    }
}
