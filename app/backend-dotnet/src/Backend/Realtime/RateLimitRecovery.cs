// #236 Rick re-review item 6 (shared rate-limit helper extraction, agreed with #235/Unity):
// `RateLimitSettings` is a project-wide alias for the single shared
// `Backend.Shared.RateLimitSettings` record -- see Backend/Shared/RateLimit.cs's own doc comment.
// Keeping the ALIAS (rather than renaming every call site) means RealtimeProcessor.cs, Program.cs,
// and every existing realtime test keep compiling unchanged.
global using RateLimitSettings = Backend.Shared.RateLimitSettings;

using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rate_limit.py's RateLimitRecovery. All three drive-thru demos share one
/// Azure OpenAI quota. When a response is rate-limited the service fails it with no output, and
/// without this the guest hears silence -- a frozen carhop. The ladder, per failed response
/// (never per session):
///
/// 1. silent retry: wait `RetryDelaySeconds` (or the service's hint, clamped to [0.5s, 5s]) and
///    send `response.create` again. The failed response produced nothing, so the guest's input
///    (or a tool's function_call_output) is still the last thing in the conversation and the
///    model simply regenerates.
/// 2. retry 1 also rate-limited: tell the browser (`extension.rate_limited`, attempt 1) so it
///    plays a pre-recorded apology clip, then retry once more after `SecondRetryDelaySeconds` (or
///    the hint, clamped to [2s, 8s]).
/// 3. retry 2 fails too: `extension.rate_limited` with `final: true` and stop. The session stays
///    up and the guest's next turn proceeds normally.
///
/// A pending retry is dropped as soon as anything else takes the turn: guest speech, any response
/// that starts (`response.created` that is not our own retry, e.g. VAD or a tool follow-up), a
/// `response.create` from the browser, or the socket detaching. A retry never fires while another
/// response is in flight, and it is not guest activity (it never touches the idle clock).
///
/// Unlike Python's single-threaded asyncio event loop (no locks needed at all), the C# relay runs
/// the upstream and browser directions as two genuinely concurrent loops, so every public hook
/// below does its state transition under <see cref="_sync"/> and only performs unlocked async IO
/// (notify the browser, schedule a retry's delayed send) after that lock is released -- the same
/// split already established by <see cref="EchoSuppressor"/>'s own locking. Scheduling a retry
/// happens fully inside the lock (see <see cref="SchedulePendingLocked"/>) so a concurrent
/// cancellation signal can never race a *new* schedule into existing after the signal that
/// should have prevented it already ran.
///
/// That alone is NOT enough to stop a *stale* schedule from firing, though (Rick's #235 review):
/// <c>Task.Delay(...).ContinueWith(t => { if (!t.IsCanceled) RunRetryAsync(...); })</c> checks
/// <c>t.IsCanceled</c> OUTSIDE the lock, and <c>Task.Delay</c> cannot retroactively become
/// Canceled once it has already completed -- so a cancellation signal (guest speech, the
/// browser's own response.create, teardown) that arrives after the timer has already elapsed
/// but before the continuation acquires <see cref="_sync"/> loses the race: the continuation
/// still runs, even though the cancellation already fully committed (cleared
/// <see cref="_pendingCts"/>/<see cref="_pendingScheduled"/>, possibly scheduled a brand-new
/// retry in the same call). <see cref="RunRetryAsync"/> therefore takes the exact
/// <see cref="CancellationTokenSource"/> it was scheduled with as its own identity token and,
/// as the very first thing under the lock, compares it by reference against the current
/// <see cref="_pendingCts"/>: a mismatch means this schedule has already been superseded (by a
/// cancellation OR by a newer retry scheduled in the same race window) and the stale call
/// returns immediately, touching no state and sending nothing. Only a schedule that is still
/// the live one proceeds to clear <see cref="_pendingScheduled"/>/<see cref="_pendingCts"/> and
/// act. <see cref="EchoSuppressor.OnAudioDone"/>'s delayed-flush continuation has the identical
/// shape and received the identical fix.
/// </summary>
public sealed class RateLimitRecovery
{
    public const string RateLimitedEventType = Shared.RateLimit.RateLimitedEventType;

    private static readonly (double Low, double High) FirstRetryBounds = Shared.RateLimit.FirstRetryBounds;
    private static readonly (double Low, double High) SecondRetryBounds = Shared.RateLimit.SecondRetryBounds;
    private static readonly string ResponseCreateMessage = new JsonObject { ["type"] = "response.create" }.ToJsonString();

    private readonly RateLimitSettings _settings;
    private readonly Func<string, CancellationToken, Task> _sendUpstream;
    private readonly Func<JsonObject, CancellationToken, Task> _sendClient;
    private readonly TimeProvider _timeProvider;
    private readonly string? _sessionId;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    // Retries already sent for the response currently being recovered.
    private int _attempt;
    // A retry of ours was sent and its response has not finished yet.
    private bool _awaitingRetry;
    private bool _responseInFlight;
    // The ladder ran out; ignore duplicate failure reports until the guest's next turn.
    private bool _exhausted;
    private bool _pendingScheduled;
    private CancellationTokenSource? _pendingCts;

    public RateLimitRecovery(
        RateLimitSettings settings,
        Func<string, CancellationToken, Task> sendUpstream,
        Func<JsonObject, CancellationToken, Task> sendClient,
        TimeProvider? timeProvider = null,
        string? sessionId = null,
        ILogger? logger = null)
    {
        _settings = settings;
        _sendUpstream = sendUpstream;
        _sendClient = sendClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionId = sessionId;
        _logger = logger;
    }

    public bool Enabled => _settings.Enabled;

    /// <summary>Retries actually sent so far (diagnostics/tests only, mirrors Python's
    /// `retries_sent`).</summary>
    public int RetriesSent { get; private set; }

    /// <summary>A retry is scheduled or its response is still running -- mirrors Python's `busy`
    /// property (the resume nudge, once it exists in C#, must stay quiet while this is true).</summary>
    public bool Busy
    {
        get { lock (_sync) { return _pendingScheduled || _awaitingRetry; } }
    }

    // ── signals from the upstream socket ──

    public void OnResponseCreated()
    {
        lock (_sync)
        {
            _responseInFlight = true;
            if (_awaitingRetry)
            {
                return; // our own retry starting
            }
            if (_pendingScheduled)
            {
                CancelPendingLocked("a new response started");
            }
            ResetLocked();
        }
    }

    /// <summary>Returns true if this `response.done` was a rate-limit failure the ladder handled
    /// (the caller then drops it); false leaves existing behaviour untouched.</summary>
    public async Task<bool> OnResponseDoneAsync(JsonObject message, CancellationToken ct)
    {
        JsonObject? error;
        lock (_sync)
        {
            _responseInFlight = false;
            error = Enabled ? RateLimitDetection.RateLimitErrorOfResponseDone(message) : null;
            if (error is null)
            {
                if (_awaitingRetry)
                {
                    ResetLocked(); // our retry finished (or failed for another reason)
                }
                return false;
            }
        }

        var responseId = (message["response"] as JsonObject)?["id"]?.GetValue<string>();
        await OnFailureAsync(error, $"response.done {responseId ?? "None"}", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>An `error` event already known NOT to reject one of our session.updates.</summary>
    public async Task<bool> OnErrorAsync(JsonObject message, CancellationToken ct)
    {
        JsonObject? error;
        bool inFlight;
        lock (_sync)
        {
            error = Enabled ? RateLimitDetection.RateLimitErrorOfErrorEvent(message) : null;
            if (error is null)
            {
                return false;
            }
            inFlight = _responseInFlight;
        }
        if (inFlight)
        {
            // The running response's own response.done will report the failure.
            _logger?.LogWarning(
                "Rate-limit error while a response is in flight (code={Code}); waiting for its response.done (session={SessionId})",
                error["code"]?.GetValue<string>(), _sessionId);
            return true;
        }
        await OnFailureAsync(error, "error event", ct).ConfigureAwait(false);
        return true;
    }

    public void OnGuestSpeech()
    {
        lock (_sync)
        {
            if (_pendingScheduled)
            {
                CancelPendingLocked("guest started speaking");
            }
            ResetLocked();
        }
    }

    /// <summary>Someone else (greeting, nudge, tool follow-up, browser) asked for a response.</summary>
    public void OnExternalResponseCreate(string source)
    {
        lock (_sync)
        {
            if (_pendingScheduled)
            {
                CancelPendingLocked($"{source} requested a response");
            }
            ResetLocked();
        }
    }

    public void Cancel(string reason)
    {
        lock (_sync)
        {
            if (_pendingScheduled)
            {
                CancelPendingLocked(reason);
            }
            ResetLocked();
        }
    }

    // ── ladder ──

    private void ResetLocked()
    {
        _attempt = 0;
        _awaitingRetry = false;
        _exhausted = false;
    }

    /// <summary>Must be called with <see cref="_sync"/> held and <see cref="_pendingScheduled"/>
    /// true.</summary>
    private void CancelPendingLocked(string reason)
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = null;
        _pendingScheduled = false;
        _logger?.LogInformation("Rate-limit retry cancelled: {Reason} (session={SessionId})", reason, _sessionId);
    }

    /// <summary>Must be called with <see cref="_sync"/> held. Commits the schedule (CTS created,
    /// <see cref="_pendingScheduled"/> set) fully inside the lock, before the delayed send is ever
    /// kicked off -- see this class's own doc comment for why that ordering matters here, and why
    /// the CTS itself (not just the delay/attempt numbers) is passed through to
    /// <see cref="RunRetryAsync"/> as this schedule's identity.</summary>
    private void SchedulePendingLocked(double delaySeconds, int attempt)
    {
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        _pendingScheduled = true;
        _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds), _timeProvider, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    _ = RunRetryAsync(delaySeconds, attempt, cts);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task OnFailureAsync(JsonObject error, string source, CancellationToken ct)
    {
        var hint = ParseRetryHint(error["message"]?.GetValue<string>());
        _logger?.LogWarning(
            "Model response rate-limited ({Source}): code={Code} type={Type} retry_hint={Hint} (session={SessionId})",
            source,
            error["code"]?.GetValue<string>(),
            error["type"]?.GetValue<string>(),
            hint is { } hintValue ? $"{hintValue:F3}s" : "none",
            _sessionId);

        var notifyFinal = false;
        var notifyNonFinal = false;
        var attempt = 0;

        lock (_sync)
        {
            if (_pendingScheduled)
            {
                _logger?.LogInformation(
                    "Rate-limit failure while a retry is already pending; same attempt (session={SessionId})", _sessionId);
                return;
            }
            if (_exhausted)
            {
                _logger?.LogInformation(
                    "Rate-limit failure after the final retry; waiting for the guest's next turn (session={SessionId})",
                    _sessionId);
                return;
            }
            if (!_awaitingRetry)
            {
                _attempt = 0; // a fresh failure, not one of our retries
            }
            _awaitingRetry = false;
            attempt = _attempt;

            if (attempt >= _settings.MaxRetries)
            {
                ResetLocked();
                _exhausted = true;
                notifyFinal = true;
            }
            else if (attempt == 0)
            {
                var delay = RetryDelay(hint, _settings.RetryDelaySeconds, FirstRetryBounds);
                SchedulePendingLocked(delay, attempt + 1);
            }
            else
            {
                var delay = RetryDelay(hint, _settings.SecondRetryDelaySeconds, SecondRetryBounds);
                SchedulePendingLocked(delay, attempt + 1);
                notifyNonFinal = true;
            }
        }

        if (notifyFinal)
        {
            _logger?.LogWarning(
                "Rate-limit retries exhausted after {Attempt} attempt(s); asking the guest to repeat (session={SessionId})",
                attempt, _sessionId);
            await NotifyAsync(
                new JsonObject { ["type"] = RateLimitedEventType, ["attempt"] = attempt, ["final"] = true }, ct)
                .ConfigureAwait(false);
        }
        else if (notifyNonFinal)
        {
            await NotifyAsync(new JsonObject { ["type"] = RateLimitedEventType, ["attempt"] = attempt }, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task RunRetryAsync(double delaySeconds, int attempt, CancellationTokenSource scheduledCts)
    {
        bool skip;
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingCts, scheduledCts))
            {
                // This exact schedule has already been superseded: a cancellation source
                // (guest speech, the browser's own response.create, teardown) won the race and
                // already cleared/replaced _pendingCts before this continuation could acquire the
                // lock -- possibly scheduling a brand-new retry in the very same call. Either way,
                // that call already did everything needed (its own CancelPendingLocked/ResetLocked,
                // or its own fresh SchedulePendingLocked). Touching _pendingScheduled/_pendingCts or
                // the ladder state (_attempt/_awaitingRetry) here would either wipe out a newer
                // pending retry's bookkeeping or resurrect state for an attempt nobody asked for any
                // more, and sending would be a stale/duplicate response.create (possibly onto an
                // already-closing socket). Stay out of the way entirely.
                _logger?.LogInformation(
                    "Rate-limit retry {Attempt} skipped: superseded before it could run (session={SessionId})",
                    attempt, _sessionId);
                return;
            }

            _pendingScheduled = false;
            _pendingCts = null;
            if (_responseInFlight)
            {
                skip = true;
                ResetLocked();
            }
            else
            {
                skip = false;
                _attempt = attempt;
                _awaitingRetry = true;
                RetriesSent++;
            }
        }

        if (skip)
        {
            _logger?.LogInformation(
                "Rate-limit retry {Attempt} skipped: another response is already running (session={SessionId})",
                attempt, _sessionId);
            return;
        }

        _logger?.LogInformation(
            "Rate-limit retry {Attempt}: response.create after {Delay:F2}s (session={SessionId})",
            attempt, delaySeconds, _sessionId);
        try
        {
            // CancellationToken.None: a closing socket must not crash this fire-and-forget
            // forwarder, same as EchoSuppressor's own delayed flush send.
            await _sendUpstream(ResponseCreateMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            _logger?.LogInformation(
                "Rate-limit retry {Attempt} not sent: {Message} (session={SessionId})", attempt, exc.Message, _sessionId);
            lock (_sync)
            {
                ResetLocked();
            }
        }
    }

    private async Task NotifyAsync(JsonObject payload, CancellationToken ct)
    {
        try
        {
            await _sendClient(payload, ct).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            _logger?.LogInformation(
                "Could not send {Type} to the browser: {Message} (session={SessionId})",
                payload["type"]?.GetValue<string>(), exc.Message, _sessionId);
        }
    }

    /// <summary>Seconds the service asked us to wait, from an error message, else null. Forwards
    /// to the shared <see cref="Shared.RateLimit.ParseRetryHint"/> (#236 Rick re-review item 6).</summary>
    internal static double? ParseRetryHint(string? text) => Shared.RateLimit.ParseRetryHint(text);

    /// <summary>The service's hint clamped to `bounds`; `defaultSeconds` when there is no hint.
    /// Note: `bounds` are fixed production constants, never overridable via
    /// CONFORMANCE_TEST_HOOKS (see <see cref="ConformanceHooks.Seconds"/> and this class's own
    /// doc comment for the caveat this implies for scripted rate-limit hints under test hooks).
    /// Forwards to the shared <see cref="Shared.RateLimit.RetryDelay"/> (#236 Rick re-review
    /// item 6).</summary>
    internal static double RetryDelay(double? hint, double defaultSeconds, (double Low, double High) bounds) =>
        Shared.RateLimit.RetryDelay(hint, defaultSeconds, bounds);
}
