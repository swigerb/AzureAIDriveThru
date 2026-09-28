namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/audio_pipeline.py's <c>EchoSuppressor</c> (issue #13's echo-suppression /
/// barge-in acceptance target). Per-connection echo suppression state machine: tracks whether the
/// assistant is currently speaking, manages the post-speech cooldown, and handles greeting-specific
/// echo blocking. Not thread-safe by design -- callers must serialize access, exactly like Python's
/// single-threaded-asyncio assumption (rtmt.py's <c>_forward_messages</c> owns one instance per
/// connection and calls into it from its own sequential loops only).
///
/// Uses a monotonic "loop time" in seconds (caller-supplied, e.g. <c>Environment.TickCount64 /
/// 1000.0</c> or a test-controlled clock) instead of wall-clock time, mirroring Python's
/// <c>asyncio.AbstractEventLoop.time()</c> -- immune to system clock adjustments.
/// </summary>
public sealed class EchoSuppressor : IDisposable
{
    private readonly double _cooldownSeconds;
    private readonly Func<CancellationToken, Task> _flushSendAsync;
    private CancellationTokenSource? _flushCts;
    private bool _closed;

    public EchoSuppressor(double cooldownSeconds, Func<CancellationToken, Task> flushSendAsync)
    {
        _cooldownSeconds = cooldownSeconds;
        _flushSendAsync = flushSendAsync;
    }

    public bool AiSpeaking { get; private set; }
    public double CooldownEnd { get; private set; }
    public bool GreetingInProgress { get; private set; }

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

    /// <summary>Returns true if user audio should be dropped (AI speaking or cooldown still active
    /// at <paramref name="loopTimeSeconds"/>).</summary>
    public bool ShouldSuppressAudio(double loopTimeSeconds) => AiSpeaking || loopTimeSeconds < CooldownEnd;

    /// <summary>AI started sending audio -- begin suppression.</summary>
    public void OnAudioDelta()
    {
        if (_greetingAwaitingRetry)
        {
            _greetingAwaitingRetry = false;
            GreetingInProgress = true;
        }
        if (GreetingInProgress)
        {
            _greetingAudioSeen = true;
        }
        AiSpeaking = true;
    }

    /// <summary>AI finished sending audio -- start cooldown and flush any echoed audio that leaked
    /// into the upstream buffer, both immediately and again once the cooldown expires.</summary>
    public void OnAudioDone(double loopTimeSeconds)
    {
        if (_closed)
        {
            // PR #58 re-review "F1": close() is terminal -- a later on_audio_done() call (e.g. a
            // leftover response.done racing the connection's own teardown) must not re-arm the
            // flush.
            return;
        }
        AiSpeaking = false;
        double cooldown;
        if (GreetingInProgress)
        {
            cooldown = _cooldownSeconds * 2;
            GreetingInProgress = false;
            _greetingAwaitingRetry = false;
            _greetingAudioSeen = false;
        }
        else
        {
            cooldown = _cooldownSeconds;
        }
        CooldownEnd = loopTimeSeconds + cooldown;

        // Flush any echoed audio that leaked into OpenAI's buffer, best-effort (fire-and-forget).
        _ = BestEffortSend();

        // Schedule a second flush after cooldown expires, cancelling any flush still pending from
        // an earlier OnAudioDone() call first.
        _flushCts?.Cancel();
        _flushCts?.Dispose();
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

    /// <summary>Cancels any delayed echo flush still pending, and becomes terminal -- called from
    /// the connection's teardown so a timer can never fire (and attempt a send) after the
    /// connection has already gone away. Idempotent.</summary>
    public void Close()
    {
        _closed = true;
        _flushCts?.Cancel();
        _flushCts?.Dispose();
        _flushCts = null;
    }

    public void Dispose() => Close();

    /// <summary>Server VAD detected speech. Returns true if it should be ignored (greeting echo).</summary>
    public bool OnSpeechStarted()
    {
        if (GreetingInProgress)
        {
            return true;
        }
        AiSpeaking = false;
        CooldownEnd = 0.0;
        _greetingAwaitingRetry = false;
        return false;
    }

    /// <summary>Client sent response.cancel -- the guest wants to speak. This is the mechanism
    /// that actually lifts mic suppression for a barge-in, not a side effect of some later
    /// completion event.</summary>
    public void OnBargeIn()
    {
        AiSpeaking = false;
        CooldownEnd = 0.0;
        _greetingAwaitingRetry = false;
    }

    /// <summary>Someone other than the rate-limit ladder itself asked for a fresh response (e.g.
    /// the browser's own response.create). A pending greeting-retry re-arm speculates that the
    /// ladder's own retry is what produces the next audio delta; if a genuinely new, unrelated
    /// response is created first, its audio must not be mistaken for the greeting's continuation.</summary>
    public void OnExternalResponseCreate() => _greetingAwaitingRetry = false;

    /// <summary>Pre-set suppression before the greeting fires.</summary>
    public void StartGreetingSuppression()
    {
        AiSpeaking = true;
        GreetingInProgress = true;
        _greetingAudioSeen = false;
    }

    /// <summary>A response finished (any status) -- the safety net for a greeting that never
    /// produced audio at all (text-only fallback, cancelled/failed before any audio, a
    /// rate-limited retry with no output). Without this, <see cref="ShouldSuppressAudio"/> would
    /// drop the guest's mic forever until they physically interrupt.</summary>
    public void OnResponseDone(double loopTimeSeconds)
    {
        if (!GreetingInProgress)
        {
            return;
        }
        GreetingInProgress = false;
        if (!AiSpeaking)
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
            AiSpeaking = false;
            _greetingAudioSeen = false;
            CooldownEnd = loopTimeSeconds + (_cooldownSeconds * 2);
            return;
        }
        // Nothing was ever actually rendered to the guest -- unmute immediately, same as an
        // explicit browser barge-in, instead of imposing an artificial multi-second mute after a
        // greeting the guest never heard. This failed/empty attempt may still be retried by the
        // rate-limit ladder with a bare response.create -- if it is, the retry's own first audio
        // delta must re-enter greeting suppression (OnAudioDelta()).
        AiSpeaking = false;
        CooldownEnd = 0.0;
        _greetingAwaitingRetry = true;
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
