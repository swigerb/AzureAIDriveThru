using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backend.Configuration;
using Microsoft.Extensions.Logging;

namespace Backend.Cascade;

/// <summary>
/// Issue #82: the cascade pipeline's OWN 429 retry ladder -- a C# port of
/// cascade_processor.py's module-level `_http_status_of`/`_retry_hint_of`/
/// `_with_rate_limit_retry` plus the pieces of rate_limit.py it reuses (`FIRST_RETRY_BOUNDS`/
/// `SECOND_RETRY_BOUNDS`/`RATE_LIMITED_EVENT`/`RateLimitSettings`/`parse_retry_hint`/
/// `retry_delay`). Deliberately a SEPARATE type from the realtime pipeline's own
/// <see cref="Realtime.RateLimitDetection"/> (RealtimeProcessor.cs) -- that one answers "is this
/// upstream WS `response.done`/`error` event a rate limit, and should a NEW `response.create` be
/// sent for it" for the realtime pipeline's own event-driven WS relay. Cascade's calls are plain
/// REST round trips (chat/STT/TTS), so its own ladder wraps ONE such call directly and retries it
/// in place -- a materially different shape from realtime's event-driven retry.
///
/// #236 Rick re-review item 2: #235 (Unity, "C# rate-limit retry ladder", realtime) independently
/// added its own retry to <see cref="Realtime.RateLimitDetection"/>, duplicating this type's own
/// `FIRST_RETRY_BOUNDS`/`SECOND_RETRY_BOUNDS`/`RateLimitSettings`/`ParseRetryHint`/`RetryDelay`
/// and the same `extension.rate_limited` event shape (both are ports of the SAME Python
/// rate_limit.py). This class's doc previously claimed realtime had "no retry at all" -- that was
/// true when this was written but #235 changed it. Agreed de-duplication plan (not done in this
/// PR, to keep #236's and #235's diffs independently reviewable): whichever of #235/#236 merges
/// SECOND extracts the shared settings/hint-parsing/delay/bounds/event-shape pieces into one
/// common C# helper (the equivalent of Python's rate_limit.py itself), while keeping the two
/// orchestrations (cascade's wrap-one-REST-call loop vs. realtime's WS-event-driven retry)
/// separate, exactly as cascade_processor.py and rate_limit.py stay separate in Python today.
/// </summary>
public static class CascadeRateLimit
{
    public const string RateLimitedEvent = "extension.rate_limited";

    public static readonly (double Low, double High) FirstRetryBounds = (0.5, 5.0);
    public static readonly (double Low, double High) SecondRetryBounds = (2.0, 8.0);

    // "Please try again in 1.5s", "try again in 250ms", "retry after 7 seconds" -- byte-for-byte
    // port of rate_limit.py's own _HINT_RE.
    private static readonly Regex HintRegex = new(
        @"(?:try\s+again|retry)\s+(?:in|after)\s+(\d+(?:\.\d+)?)\s*(ms|msec|millisecond|milliseconds|s|sec|secs|second|seconds)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Seconds the service asked us to wait, from an error message, else null. Port of
    /// rate_limit.py's `parse_retry_hint`.</summary>
    public static double? ParseRetryHint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var match = HintRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? value / 1000.0 : value;
    }

    /// <summary>The service's hint clamped to <paramref name="bounds"/>; <paramref name="defaultValue"/>
    /// when there is no hint. Port of rate_limit.py's `retry_delay` -- note <paramref name="bounds"/>
    /// are fixed production constants, never overridable via CONFORMANCE_TEST_HOOKS (same caveat
    /// as rate_limit.py's own module docstring).</summary>
    public static double RetryDelay(double? hint, double defaultValue, (double Low, double High) bounds) =>
        hint is null ? defaultValue : Math.Min(Math.Max(hint.Value, bounds.Low), bounds.High);

    /// <summary>Runs <paramref name="op"/> (a zero-arg async callable performing ONE chat/STT/TTS
    /// call) applying the SAME retry-ladder semantics as rate_limit.py's `RateLimitRecovery`
    /// (silent retry, then `extension.rate_limited` at attempt 1, then `extension.rate_limited`
    /// with `final: true`) -- adapted for cascade's REST-call-based 429s
    /// (<see cref="FoundryHttpException"/>) instead of the realtime pipeline's own WS
    /// `response.create`/`response.done` lifecycle. A non-429 error, or a 429 while
    /// <see cref="CascadeRateLimitSettings.Enabled"/> is false, propagates unchanged so existing
    /// callers' own try/catch keep handling it exactly as before. Throws
    /// <see cref="CascadeRateLimitExhausted"/> once the ladder is spent; by then the client has
    /// already gotten the final notice.</summary>
    public static async Task<T> WithRetryAsync<T>(
        CascadeRateLimitSettings settings,
        Func<Task<T>> op,
        string opName,
        Func<JsonObject, CancellationToken, Task> notifyClient,
        string sessionId,
        ILogger? logger,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        // Issue #13 Wave 2 convention (RealtimeProcessor's own _timeProvider): sourced from an
        // injectable clock so a test can swap in a FakeTimeProvider instead of waiting on the
        // real 0.5-8s wall-clock delays below. Defaults to TimeProvider.System in production.
        var clock = timeProvider ?? TimeProvider.System;
        var attempt = 0;
        while (true)
        {
            try
            {
                return await op().ConfigureAwait(false);
            }
            catch (FoundryHttpException exc) when (exc.StatusCode == 429 && settings.Enabled)
            {
                var hint = exc.RetryAfterSeconds ?? ParseRetryHint(exc.Message);
                if (attempt >= settings.MaxRetries)
                {
                    logger?.LogWarning(
                        "Cascade {OpName} rate-limited; retries exhausted after {Attempt} attempt(s) (session={SessionId})",
                        opName, attempt, sessionId);
                    await notifyClient(
                        new JsonObject { ["type"] = RateLimitedEvent, ["attempt"] = attempt, ["final"] = true }, ct)
                        .ConfigureAwait(false);
                    throw new CascadeRateLimitExhausted(opName, exc);
                }

                double delay;
                if (attempt == 0)
                {
                    delay = RetryDelay(hint, settings.RetryDelaySeconds, FirstRetryBounds);
                }
                else
                {
                    delay = RetryDelay(hint, settings.SecondRetryDelaySeconds, SecondRetryBounds);
                    await notifyClient(new JsonObject { ["type"] = RateLimitedEvent, ["attempt"] = attempt }, ct)
                        .ConfigureAwait(false);
                }
                logger?.LogInformation(
                    "Cascade {OpName} rate-limited; retry {Attempt} after {Delay:F2}s (session={SessionId})",
                    opName, attempt + 1, delay, sessionId);
                await Task.Delay(TimeSpan.FromSeconds(delay), clock, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>Void-returning overload of <see cref="WithRetryAsync{T}"/> (used by <c>_speak</c>,
    /// which streams chunks directly rather than returning a value).</summary>
    public static async Task WithRetryAsync(
        CascadeRateLimitSettings settings,
        Func<Task> op,
        string opName,
        Func<JsonObject, CancellationToken, Task> notifyClient,
        string sessionId,
        ILogger? logger,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        await WithRetryAsync<object?>(
            settings,
            async () => { await op().ConfigureAwait(false); return null; },
            opName, notifyClient, sessionId, logger, ct, timeProvider).ConfigureAwait(false);
    }
}

/// <summary>Raised internally once a chat/STT/TTS call has been retried through the full ladder
/// (<see cref="CascadeRateLimit.WithRetryAsync{T}"/>) and still failed with a 429 -- by the time
/// this is raised the client has already received the final `extension.rate_limited` notice, so
/// callers just need to end the turn cleanly (same as the realtime pipeline's own
/// guest-repeats-themselves outcome). Port of cascade_processor.py's `CascadeRateLimitExhausted`.</summary>
public sealed class CascadeRateLimitExhausted(string opName, Exception innerException)
    : Exception($"Cascade {opName} rate-limited; retries exhausted.", innerException);

/// <summary>Raised by <see cref="FoundryChatClient"/>/<see cref="FoundryAudioClient"/> for a
/// non-2xx Foundry REST response -- carries exactly the two facts
/// <see cref="CascadeRateLimit.WithRetryAsync{T}"/> needs (the HTTP status, and a retry-after hint
/// preferring the response's own `Retry-After` header, falling back to
/// <see cref="CascadeRateLimit.ParseRetryHint"/>'s free-text parse of the response body) -- the C#
/// equivalent of cascade_processor.py's `_http_status_of`/`_retry_hint_of` reading an azure-core
/// `HttpResponseError`/aiohttp `ClientResponseError`.</summary>
public sealed class FoundryHttpException(int statusCode, double? retryAfterSeconds, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public double? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Port of rate_limit.py's `RateLimitSettings`/`RateLimitSettings.from_config` --
/// `resilience.rate_limit` from config.yaml, with `RATE_LIMIT_RECOVERY_ENABLED` overriding
/// <see cref="Enabled"/>, same as the realtime pipeline's own settings would if it ported the
/// full ladder (it doesn't -- see this file's own class doc). Shared verbatim by both pipelines'
/// settings in Python; duplicated (not shared) here since the realtime side never needed its own
/// typed settings object for its single-notice scope cut.</summary>
public sealed record CascadeRateLimitSettings(bool Enabled, double RetryDelaySeconds, double SecondRetryDelaySeconds, int MaxRetries)
{
    private const string EnabledEnvVar = "RATE_LIMIT_RECOVERY_ENABLED";

    public static CascadeRateLimitSettings FromAppConfig(AppConfig config, IReadOnlyDictionary<string, string>? environment = null)
    {
        var resilienceSection = config.TryGetSection("resilience");
        IDictionary<object, object>? rateLimitSection = null;
        if (resilienceSection is not null && resilienceSection.TryGetValue("rate_limit", out var raw))
        {
            rateLimitSection = raw as IDictionary<object, object>;
        }

        var enabled = GetBool(rateLimitSection, "enabled", true);
        var envValue = environment is not null
            ? environment.GetValueOrDefault(EnabledEnvVar)
            : Environment.GetEnvironmentVariable(EnabledEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            enabled = Truthy(envValue);
        }

        var retryDelaySeconds = ConformanceHooks.Seconds(
            "CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS", GetDouble(rateLimitSection, "retry_delay_seconds", 1.5));
        var secondRetryDelaySeconds = ConformanceHooks.Seconds(
            "CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS", GetDouble(rateLimitSection, "second_retry_delay_seconds", 4.0));
        var maxRetries = Math.Max(0, GetInt(rateLimitSection, "max_retries", 2));

        return new CascadeRateLimitSettings(enabled, retryDelaySeconds, secondRetryDelaySeconds, maxRetries);
    }

    private static bool Truthy(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static bool GetBool(IDictionary<object, object>? section, string key, bool fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }
}
