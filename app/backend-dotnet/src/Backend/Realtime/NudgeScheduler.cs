namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's per-connection <c>nudge_after_silence</c>/<c>cancel_nudge</c>
/// pair (issue #15, #181): after a resume rehydrates an in-progress order, if the guest says
/// nothing for <c>nudge_after_seconds</c>, the assistant checks in once. <see cref="Arm"/> is only
/// ever called once per connection, and only once THIS socket's own client has proven the
/// conversation is live by sending its own <c>session.update</c> (#181 -- a resume succeeding, or
/// even the upstream bootstrap completing, is deliberately NOT enough; see
/// <see cref="Backend.Sessions.RealtimeProcessor"/>'s own wiring for exactly where that gate
/// lives). Once armed, firing additionally waits -- with no timeout, matching the greeting's own
/// `session_configured` gate -- for the resumed upstream's own <c>session.updated</c>, and is
/// skipped outright (silently, no retry) if a rate-limit retry is already in flight.
///
/// Cancellation sources (all <see cref="Cancel"/>): guest speech, a completed guest transcript, a
/// guest-initiated <c>response.create</c>, and socket teardown.
///
/// Uses the exact CTS-identity pattern <see cref="RateLimitRecovery"/>'s delayed retry and
/// <see cref="EchoSuppressor"/>'s delayed flush both already use (Rick's #235 review): a bare
/// `Task.Delay(...).ContinueWith(t => { if (!t.IsCanceled) ... })` checks `t.IsCanceled` OUTSIDE
/// the lock, and `Task.Delay` cannot retroactively become Canceled once it has already completed,
/// so a cancellation that arrives after the timer elapsed but before the continuation acquires the
/// lock would otherwise still run a now-stale nudge. The continuation instead takes the exact
/// <see cref="CancellationTokenSource"/> it was scheduled with as its own identity and, as the
/// first thing under the lock, compares it by reference against the still-pending one; a mismatch
/// means this schedule was already superseded (by <see cref="Cancel"/>) and the stale call returns
/// immediately, sending nothing.
/// </summary>
internal sealed class NudgeScheduler
{
    private readonly double _nudgeAfterSeconds;
    private readonly Func<CancellationToken, Task> _sendNudgeAsync;
    private readonly Func<bool> _isRateLimitBusy;
    private readonly Task _sessionConfigured;
    private readonly TimeProvider _timeProvider;
    private readonly string? _sessionId;
    private readonly ILogger? _logger;
    private readonly Lock _sync = new();

    private bool _armed;
    private CancellationTokenSource? _pendingCts;

    public NudgeScheduler(
        double nudgeAfterSeconds,
        Func<CancellationToken, Task> sendNudgeAsync,
        Func<bool> isRateLimitBusy,
        Task sessionConfigured,
        TimeProvider? timeProvider = null,
        string? sessionId = null,
        ILogger? logger = null)
    {
        _nudgeAfterSeconds = nudgeAfterSeconds;
        _sendNudgeAsync = sendNudgeAsync;
        _isRateLimitBusy = isRateLimitBusy;
        _sessionConfigured = sessionConfigured;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionId = sessionId;
        _logger = logger;
    }

    /// <summary>Diagnostics/tests only: a delay is currently scheduled and has not yet fired or
    /// been cancelled.</summary>
    public bool Pending
    {
        get { lock (_sync) { return _pendingCts is not null; } }
    }

    /// <summary>Test-only: exposes the internal lock so a test can hold it (via
    /// <see cref="Lock.EnterScope"/>) across a FakeTimeProvider.Advance() to deterministically
    /// reproduce a timer-vs-cancellation race -- same pattern as
    /// <see cref="Backend.Sessions.SessionManager.SyncRootForTests"/>.</summary>
    internal Lock SyncRootForTests => _sync;

    /// <summary>Schedules the silence timer. A no-op if already armed (or if the timer is
    /// disabled, `nudge_after_seconds &lt;= 0` -- mirrors rtmt.py's own
    /// `if self._sessions.nudge_after_seconds > 0` guard around setting
    /// `nudge_awaiting_client_live` in the first place, kept here too as defence in depth). Must
    /// only ever be called once per connection, after THIS socket's own client has sent its first
    /// post-resume `session.update` (#181).</summary>
    public void Arm()
    {
        lock (_sync)
        {
            if (_armed || _nudgeAfterSeconds <= 0)
            {
                return;
            }
            _armed = true;
            ScheduleLocked();
        }
    }

    /// <summary>Must be called with <see cref="_sync"/> held.</summary>
    private void ScheduleLocked()
    {
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(_nudgeAfterSeconds), _timeProvider, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    _ = RunNudgeAsync(cts);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunNudgeAsync(CancellationTokenSource scheduledCts)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingCts, scheduledCts))
            {
                // Superseded by Cancel() in the race window between the timer elapsing and this
                // continuation acquiring the lock -- see this class's own doc comment. Cancel()
                // already did everything needed; touching _pendingCts here would wipe out
                // whatever it already cleared.
                _logger?.NudgeTimerFiredAfterCancel(_sessionId);
                return;
            }
            _pendingCts = null;
        }

        // Unlocked from here: matches session.updated, the same gate the greeting uses, with no
        // timeout fallback (rtmt.py's own `await session_configured.wait()`).
        await _sessionConfigured.ConfigureAwait(false);

        if (_isRateLimitBusy())
        {
            // The assistant is already retrying a rate-limited response; a nudge now would stack
            // a second response on top of it.
            _logger?.NudgeSkippedRateLimitBusy(_sessionId);
            return;
        }

        _logger?.NudgeFiring(_nudgeAfterSeconds, _sessionId);
        try
        {
            // CancellationToken.None: a closing socket must not crash this fire-and-forget
            // send, same as RateLimitRecovery's own delayed retry.
            await _sendNudgeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.NudgeSendFailed(ex, ex.Message, _sessionId);
        }
    }

    /// <summary>Cancels a pending nudge, if any. Safe to call even if never armed, or already
    /// fired/cancelled.</summary>
    public void Cancel(string reason)
    {
        lock (_sync)
        {
            if (_pendingCts is null)
            {
                return;
            }
            _pendingCts.Cancel();
            _pendingCts.Dispose();
            _pendingCts = null;
            _logger?.NudgeCancelled(reason, _sessionId);
        }
    }
}
