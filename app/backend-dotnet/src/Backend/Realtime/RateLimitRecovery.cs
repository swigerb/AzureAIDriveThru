using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backend.Configuration;
using Microsoft.Extensions.Logging;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rate_limit.py's RateLimitSettings -- config.yaml's `resilience.rate_limit`
/// section, with RATE_LIMIT_RECOVERY_ENABLED overriding `enabled` and the two retry-delay
/// defaults further overridable (test hooks only, via <see cref="ConformanceHooks.Seconds"/>) --
/// same shipped defaults as the Python dataclass so an empty/missing section behaves identically
/// in both backends.
/// </summary>
public sealed class RateLimitSettings
{
    private const string EnabledEnvVar = "RATE_LIMIT_RECOVERY_ENABLED";

    public bool Enabled { get; }
    public double RetryDelaySeconds { get; }
    public double SecondRetryDelaySeconds { get; }
    public int MaxRetries { get; }

    public RateLimitSettings(
        bool enabled = true,
        double retryDelaySeconds = 1.5,
        double secondRetryDelaySeconds = 4.0,
        int maxRetries = 2)
    {
        Enabled = enabled;
        RetryDelaySeconds = retryDelaySeconds;
        SecondRetryDelaySeconds = secondRetryDelaySeconds;
        MaxRetries = maxRetries;
    }

    /// <summary>`resilience.rate_limit` from config.yaml; RATE_LIMIT_RECOVERY_ENABLED overrides
    /// `enabled`. <paramref name="getEnv"/> is injectable for tests, defaulting to the real
    /// process environment.</summary>
    public static RateLimitSettings FromAppConfig(AppConfig config, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var resilience = config.TryGetSection("resilience");
        var rateLimit = resilience is not null && resilience.TryGetValue("rate_limit", out var nested)
            ? nested as IDictionary<object, object>
            : null;

        var enabled = GetBool(rateLimit, "enabled", true);
        var envValue = getEnv(EnabledEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            enabled = IsTruthy(envValue);
        }

        return new RateLimitSettings(
            enabled: enabled,
            retryDelaySeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS", GetDouble(rateLimit, "retry_delay_seconds", 1.5)),
            secondRetryDelaySeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS", GetDouble(rateLimit, "second_retry_delay_seconds", 4.0)),
            maxRetries: Math.Max(0, GetInt(rateLimit, "max_retries", 2)));
    }

    private static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback) =>
        section is not null && section.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out var value)
            ? value
            : fallback;

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback) =>
        section is not null && section.TryGetValue(key, out var raw) &&
        double.TryParse(raw?.ToString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(IDictionary<object, object>? section, string key, bool fallback)
    {
        if (section is null || !section.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }
        if (raw is bool direct)
        {
            return direct;
        }
        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }
}

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
/// happens fully inside the lock (see <see cref="SchedulePendingLocked"/>) specifically so a
/// concurrent cancellation signal (guest speech from the upstream loop, the browser's own
/// response.create from the other loop) can never race a retry into existing after the signal
/// that should have prevented it already ran.
/// </summary>
public sealed class RateLimitRecovery
{
    public const string RateLimitedEventType = "extension.rate_limited";

    private static readonly (double Low, double High) FirstRetryBounds = (0.5, 5.0);
    private static readonly (double Low, double High) SecondRetryBounds = (2.0, 8.0);
    private static readonly string ResponseCreateMessage = new JsonObject { ["type"] = "response.create" }.ToJsonString();

    // "Please try again in 1.5s", "try again in 250ms", "retry after 7 seconds".
    private static readonly Regex HintPattern = new(
        @"(?:try\s+again|retry)\s+(?:in|after)\s+(\d+(?:\.\d+)?)\s*(ms|msec|millisecond|milliseconds|s|sec|secs|second|seconds)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// kicked off -- see this class's own doc comment for why that ordering matters here.</summary>
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
                    _ = RunRetryAsync(delaySeconds, attempt);
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

    private async Task RunRetryAsync(double delaySeconds, int attempt)
    {
        bool skip;
        lock (_sync)
        {
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

    /// <summary>Seconds the service asked us to wait, from an error message, else null.</summary>
    internal static double? ParseRetryHint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var match = HintPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? value / 1000.0 : value;
    }

    /// <summary>The service's hint clamped to `bounds`; `defaultSeconds` when there is no hint.
    /// Note: `bounds` are fixed production constants, never overridable via
    /// CONFORMANCE_TEST_HOOKS (see <see cref="ConformanceHooks.Seconds"/> and this class's own
    /// doc comment for the caveat this implies for scripted rate-limit hints under test hooks).</summary>
    internal static double RetryDelay(double? hint, double defaultSeconds, (double Low, double High) bounds) =>
        hint is null ? defaultSeconds : Math.Min(Math.Max(hint.Value, bounds.Low), bounds.High);
}
