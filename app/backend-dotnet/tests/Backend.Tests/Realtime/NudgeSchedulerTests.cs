using Backend.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Realtime;

/// <summary>
/// Issue #15/#181: <see cref="NudgeScheduler"/> unit tests. Every test drives a
/// <see cref="FakeTimeProvider"/> deterministically instead of sleeping real wall-clock time (same
/// convention as <see cref="RateLimitRecoveryUnitTests"/>), and the stale-continuation race test
/// mirrors that class's own <c>RunStaleRetryRaceAsync</c> pattern exactly, proving the CTS-identity
/// fix applies equally here.
/// </summary>
public sealed class NudgeSchedulerTests
{
    private sealed class Harness
    {
        public FakeTimeProvider Time { get; } = new();
        public List<string> Sent { get; } = [];
        public bool RateLimitBusy { get; set; }
        public TaskCompletionSource<bool> SessionConfigured { get; } = new();
        public NudgeScheduler Scheduler { get; }

        public Harness(double nudgeAfterSeconds = 2.0)
        {
            Scheduler = new NudgeScheduler(
                nudgeAfterSeconds,
                sendNudgeAsync: ct => { Sent.Add("nudge"); return Task.CompletedTask; },
                isRateLimitBusy: () => RateLimitBusy,
                sessionConfigured: SessionConfigured.Task,
                timeProvider: Time,
                sessionId: "s1",
                logger: NullLogger<NudgeScheduler>.Instance);
        }
    }

    [Fact]
    public void Arm_WhenDisabled_NeverSchedules()
    {
        var h = new Harness(nudgeAfterSeconds: 0);
        h.Scheduler.Arm();
        Assert.False(h.Scheduler.Pending);
    }

    [Fact]
    public void Arm_IsIdempotent_SecondCallIsANoOp()
    {
        var h = new Harness();
        h.Scheduler.Arm();
        Assert.True(h.Scheduler.Pending);
        var pendingBefore = h.Scheduler.Pending;
        h.Scheduler.Arm(); // must not re-schedule / reset the clock
        Assert.Equal(pendingBefore, h.Scheduler.Pending);
    }

    [Fact]
    public async Task Fires_AfterDelay_OnceSessionConfiguredAndNotBusy()
    {
        var h = new Harness(nudgeAfterSeconds: 2.0);
        h.Scheduler.Arm();
        h.SessionConfigured.SetResult(true);

        h.Time.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(50, TestContext.Current.CancellationToken); // let any (incorrectly) fired continuation run
        Assert.Empty(h.Sent);

        h.Time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitUntilAsync(() => h.Sent.Count > 0);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task Fires_OnlyAfterSessionConfiguredResolves_NotBeforeEvenIfTimerElapsed()
    {
        var h = new Harness(nudgeAfterSeconds: 1.0);
        h.Scheduler.Arm();
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(h.Sent); // session_configured never resolved -- must still be waiting

        h.SessionConfigured.SetResult(true);
        await WaitUntilAsync(() => h.Sent.Count > 0);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task Fires_Skipped_WhenRateLimitBusy()
    {
        var h = new Harness(nudgeAfterSeconds: 1.0);
        h.RateLimitBusy = true;
        h.Scheduler.Arm();
        h.SessionConfigured.SetResult(true);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Cancel_BeforeFiring_PreventsTheSend()
    {
        var h = new Harness(nudgeAfterSeconds: 1.0);
        h.Scheduler.Arm();
        h.SessionConfigured.SetResult(true);
        h.Scheduler.Cancel("guest speech_started");
        Assert.False(h.Scheduler.Pending);
    }

    [Fact]
    public void Cancel_WhenNeverArmed_IsSafeNoOp()
    {
        var h = new Harness();
        h.Scheduler.Cancel("socket closed"); // must not throw
        Assert.False(h.Scheduler.Pending);
    }

    // ── Rick's #235-review CTS-identity pattern, applied here too ──────────────────────────────
    //
    // Same race as RateLimitRecoveryUnitTests' StaleTimerContinuation_* theory: hold the
    // scheduler's own lock (SyncRootForTests) while the fake clock is advanced past the nudge
    // delay on a background thread, call Cancel() while still holding the lock (so the stale
    // continuation is provably blocked on the SAME lock, not just racing in wall-clock time), then
    // release. The stale continuation must see its own CTS no longer matches _pendingCts and must
    // send nothing.
    [Fact]
    public async Task StaleTimerContinuation_LosesRaceToCancel_SendsNothing()
    {
        var h = new Harness(nudgeAfterSeconds: 1.0);
        h.Scheduler.Arm();
        h.SessionConfigured.SetResult(true);

        var syncRoot = h.Scheduler.SyncRootForTests;
        Task advanceTask;
        using (syncRoot.EnterScope())
        {
            advanceTask = Task.Run(() => h.Time.Advance(TimeSpan.FromSeconds(1.5)), TestContext.Current.CancellationToken);
            Thread.Sleep(100);
            h.Scheduler.Cancel("guest speech_started");
        }

        var finished = await Task.WhenAny(advanceTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == advanceTask;
        Assert.True(finished, "the stale continuation deadlocked instead of returning once superseded");
        await advanceTask;

        await Task.Delay(150, TestContext.Current.CancellationToken); // give a (buggy) stale continuation a chance to misbehave
        Assert.Empty(h.Sent);
        Assert.False(h.Scheduler.Pending);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
