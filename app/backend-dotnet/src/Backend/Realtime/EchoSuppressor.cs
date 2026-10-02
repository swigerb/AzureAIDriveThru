namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/audio_pipeline.py's <c>EchoSuppressor</c> (issue #13's echo-suppression /
/// barge-in acceptance target). Per-connection echo suppression state machine: tracks whether the
/// assistant is currently speaking, manages the post-speech cooldown, and handles greeting-specific
/// echo blocking. Internally synchronized: unlike Python's single-threaded-asyncio assumption
/// (rtmt.py's <c>_forward_messages</c> owns one instance per connection and calls into it from its
/// own sequential loop only), the C# browser and upstream relay loops run truly in parallel on the
/// thread pool, and the flush timer's continuation fires on its own thread as well. Every public
/// member takes an internal lock around its state mutation/read, so callers do not need to
/// serialize access themselves.
///
/// Uses a monotonic "loop time" in seconds (caller-supplied, e.g. <c>Environment.TickCount64 /
/// 1000.0</c> or a test-controlled clock) instead of wall-clock time, mirroring Python's
/// <c>asyncio.AbstractEventLoop.time()</c> -- immune to system clock adjustments.
/// </summary>
public sealed class EchoSuppressor : IDisposable
{
    private readonly object _sync = new();
    private readonly double _cooldownSeconds;
    private readonly Func<CancellationToken, Task> _flushSendAsync;
    private CancellationTokenSource? _flushCts;
    private bool _closed;
    private bool _aiSpeaking;
    private double _cooldownEnd;
    private bool _greetingInProgress;

    public EchoSuppressor(double cooldownSeconds, Func<CancellationToken, Task> flushSendAsync)
    {
        _cooldownSeconds = cooldownSeconds;
        _flushSendAsync = flushSendAsync;
    }

    public bool AiSpeaking { get { lock (_sync) { return _aiSpeaking; } } }
    public double CooldownEnd { get { lock (_sync) { return _cooldownEnd; } } }
    public bool GreetingInProgress { get { lock (_sync) { return _greetingInProgress; } } }

    /// <summary>PR #58 re-review "M1": set when a greeting's response.done arrives with no audio
    /// ever rendered -- the rate-limit recovery ladder may retry that same greeting with a bare
    /// response.create, whose own first audio delta must re-enter greeting suppression instead of
    /// being treated as an ordinary response.</summary>
    private bool _greetingAwaitingRetry;

    /// <summary>PR #58 re-review "S1": set the moment a real audio delta arrives while a greeting
    /// is in progress, so <see cref="OnResponseDone"/> can tell "nothing rendered" (instant unmute)
    /// apart from "some audio rendered but never completed" (extended cooldown, same as a normal
    /// completion).</summary>
    private bool _greetingAudioSeen;

    /// <summary>Returns true if user audio should be dropped while AI audio is still active. Once
    /// assistant audio has completed, the cooldown only protects a delayed upstream-buffer clear.
    /// The first guest mic frame after completion cancels that delayed clear and is forwarded, so
    /// short acknowledgements right after the assistant finishes are not swallowed.</summary>
    public bool ShouldSuppressAudio(double loopTimeSeconds)
    {
        CancellationTokenSource? ctsToDispose = null;
        lock (_sync)
        {
            if (_aiSpeaking)
            {
                return true;
            }

            if (loopTimeSeconds < _cooldownEnd)
            {
                _cooldownEnd = 0.0;
                ctsToDispose = _flushCts;
                _flushCts = null;
            }
        }

        if (ctsToDispose is not null)
        {
            ctsToDispose.Cancel();
            ctsToDispose.Dispose();
        }

        return false;
    }

    /// <summary>AI started sending audio -- begin suppression.</summary>
    public void OnAudioDelta()
    {
        lock (_sync)
        {
            if (_greetingAwaitingRetry)
            {
                _greetingAwaitingRetry = false;
                _greetingInProgress = true;
            }
            if (_greetingInProgress)
            {
                _greetingAudioSeen = true;
            }
            _aiSpeaking = true;
        }
    }

    /// <summary>AI finished sending audio -- start cooldown and flush any echoed audio that leaked
    /// into the upstream buffer, both immediately and again once the cooldown expires.</summary>
    public void OnAudioDone(double loopTimeSeconds)
    {
        CancellationTokenSource? previousCts;
        lock (_sync)
        {
            if (_closed)
            {
                // PR #58 re-review "F1": close() is terminal -- a later on_audio_done() call (e.g. a
                // leftover response.done racing the connection's own teardown) must not re-arm the
                // flush.
                return;
            }
            _aiSpeaking = false;
            double cooldown;
            if (_greetingInProgress)
            {
                cooldown = _cooldownSeconds * 2;
                _greetingInProgress = false;
                _greetingAwaitingRetry = false;
                _greetingAudioSeen = false;
            }
            else
            {
                cooldown = _cooldownSeconds;
            }
            _cooldownEnd = loopTimeSeconds + cooldown;

            // Cancel/dispose any flush still pending from an earlier OnAudioDone() call, then arm a
            // fresh one -- both the cancellation and the replacement happen under the lock so a
            // concurrent Close() cannot observe (or race to dispose) a half-replaced _flushCts.
            previousCts = _flushCts;
            var cts = new CancellationTokenSource();
            _flushCts = cts;
            _ = Task.Delay(TimeSpan.FromSeconds(cooldown), cts.Token).ContinueWith(
                t =>
                {
                    if (!t.IsCanceled)
                    {
                        _ = BestEffortSend();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        previousCts?.Cancel();
        previousCts?.Dispose();

        // Flush any echoed audio that leaked into OpenAI's buffer, best-effort (fire-and-forget).
        // Invoked outside the lock: the send may await, and nothing here needs to hold _sync while
        // that happens.
        _ = BestEffortSend();
    }

    /// <summary>Cancels any delayed echo flush still pending, and becomes terminal -- called from
    /// the connection's teardown so a timer can never fire (and attempt a send) after the
    /// connection has already gone away. Idempotent.</summary>
    public void Close()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _closed = true;
            cts = _flushCts;
            _flushCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    public void Dispose() => Close();

    /// <summary>Server VAD detected speech. Returns true if it should be ignored (greeting echo).</summary>
    public bool OnSpeechStarted()
    {
        lock (_sync)
        {
            if (_greetingInProgress)
            {
                return true;
            }
            _aiSpeaking = false;
            _cooldownEnd = 0.0;
            _greetingAwaitingRetry = false;
            return false;
        }
    }

    /// <summary>Client sent response.cancel -- the guest wants to speak. This is the mechanism
    /// that actually lifts mic suppression for a barge-in, not a side effect of some later
    /// completion event.</summary>
    public void OnBargeIn()
    {
        lock (_sync)
        {
            _aiSpeaking = false;
            _cooldownEnd = 0.0;
            _greetingAwaitingRetry = false;
        }
    }

    /// <summary>Someone other than the rate-limit ladder itself asked for a fresh response (e.g.
    /// the browser's own response.create). A pending greeting-retry re-arm speculates that the
    /// ladder's own retry is what produces the next audio delta; if a genuinely new, unrelated
    /// response is created first, its audio must not be mistaken for the greeting's continuation.</summary>
    public void OnExternalResponseCreate()
    {
        lock (_sync)
        {
            _greetingAwaitingRetry = false;
        }
    }

    /// <summary>Pre-set suppression before the greeting fires.</summary>
    public void StartGreetingSuppression()
    {
        lock (_sync)
        {
            _aiSpeaking = true;
            _greetingInProgress = true;
            _greetingAudioSeen = false;
        }
    }

    /// <summary>A response finished (any status) -- the safety net for a greeting that never
    /// produced audio at all (text-only fallback, cancelled/failed before any audio, a
    /// rate-limited retry with no output). Without this, <see cref="ShouldSuppressAudio"/> would
    /// drop the guest's mic forever until they physically interrupt.</summary>
    public void OnResponseDone(double loopTimeSeconds)
    {
        lock (_sync)
        {
            if (!_greetingInProgress)
            {
                return;
            }
            _greetingInProgress = false;
            if (!_aiSpeaking)
            {
                // Something else (OnBargeIn(), a genuine mid-greeting interrupt) already cleared
                // AiSpeaking before this response.done arrived -- nothing left to re-arm.
                return;
            }
            if (_greetingAudioSeen)
            {
                // Audio started but never completed with audio.done -- treat this exactly like a
                // normal OnAudioDone() completion: extended cooldown, no instant unmute, no retry
                // re-arm.
                _aiSpeaking = false;
                _greetingAudioSeen = false;
                _cooldownEnd = loopTimeSeconds + (_cooldownSeconds * 2);
                return;
            }
            // Nothing was ever actually rendered to the guest -- unmute immediately, same as an
            // explicit browser barge-in, instead of imposing an artificial multi-second mute after a
            // greeting the guest never heard. This failed/empty attempt may still be retried by the
            // rate-limit ladder with a bare response.create -- if it is, the retry's own first audio
            // delta must re-enter greeting suppression (OnAudioDelta()).
            _aiSpeaking = false;
            _cooldownEnd = 0.0;
            _greetingAwaitingRetry = true;
        }
    }

    private async Task BestEffortSend()
    {
        try
        {
            await _flushSendAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: nothing downstream depends on the flush succeeding -- the connection
            // may already be closing/closed. Mirrors rtmt.py's `_best_effort_send`.
        }
    }
}
