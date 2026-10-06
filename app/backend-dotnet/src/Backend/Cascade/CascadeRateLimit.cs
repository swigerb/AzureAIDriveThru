// #236 Rick re-review item 6 (shared rate-limit helper extraction, agreed with #235/Unity):
// `CascadeRateLimitSettings` is a project-wide alias for the single shared
// `Backend.Shared.RateLimitSettings` record -- see Backend/Shared/RateLimit.cs's own doc comment.
// Keeping the ALIAS (rather than renaming every call site to `RateLimitSettings`) means
// CascadeProcessor.cs and every existing Cascade test keep compiling unchanged.
global using CascadeRateLimitSettings = Backend.Shared.RateLimitSettings;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Backend.Cascade;

/// <summary>
/// Issue #82: the cascade pipeline's OWN 429 retry ladder -- a C# port of
/// cascade_processor.py's module-level `_http_status_of`/`_retry_hint_of`/
/// `_with_rate_limit_retry`, built on the shared settings/hint-parsing/delay/bounds/event-shape
/// pieces in <see cref="Backend.Shared.RateLimit"/> (the equivalent of rate_limit.py itself).
/// Deliberately a SEPARATE orchestration type from the realtime pipeline's own
/// <see cref="Realtime.RateLimitRecovery"/> -- that one answers "is this upstream WS
/// `response.done`/`error` event a rate limit, and should a NEW `response.create` be sent for it"
/// for the realtime pipeline's own event-driven WS relay. Cascade's calls are plain REST round
/// trips (chat/STT/TTS), so its own ladder wraps ONE such call directly and retries it in place --
/// a materially different shape from realtime's event-driven retry.
///
/// #236 Rick re-review item 6 (agreed with #235/Unity): #235 independently added its own retry to
/// <see cref="Realtime.RateLimitRecovery"/>, duplicating this type's own settings/hint-parsing/
/// delay/bounds and the same `extension.rate_limited` event shape (both are ports of the SAME
/// Python rate_limit.py). Since #235 merged first, #236 (this PR) is the one that extracts the
/// shared pieces into <see cref="Backend.Shared.RateLimit"/>/<see cref="Backend.Shared.RateLimitSettings"/>
/// -- this type now forwards to them rather than keeping its own copies, while the two
/// orchestrations (cascade's wrap-one-REST-call loop vs. realtime's WS-event-driven retry) stay
/// separate, exactly as cascade_processor.py and rate_limit.py stay separate in Python today.
/// </summary>
public static class CascadeRateLimit
{
    public const string RateLimitedEvent = Shared.RateLimit.RateLimitedEventType;

    public static readonly (double Low, double High) FirstRetryBounds = Shared.RateLimit.FirstRetryBounds;
    public static readonly (double Low, double High) SecondRetryBounds = Shared.RateLimit.SecondRetryBounds;

    /// <summary>Seconds the service asked us to wait, from an error message, else null. Port of
    /// rate_limit.py's `parse_retry_hint`; forwards to the shared
    /// <see cref="Shared.RateLimit.ParseRetryHint"/> (#236 Rick re-review item 6).</summary>
    public static double? ParseRetryHint(string? text) => Shared.RateLimit.ParseRetryHint(text);

    /// <summary>The service's hint clamped to <paramref name="bounds"/>; <paramref name="defaultValue"/>
    /// when there is no hint. Port of rate_limit.py's `retry_delay`; forwards to the shared
    /// <see cref="Shared.RateLimit.RetryDelay"/> (#236 Rick re-review item 6).</summary>
    public static double RetryDelay(double? hint, double defaultValue, (double Low, double High) bounds) =>
        Shared.RateLimit.RetryDelay(hint, defaultValue, bounds);

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
