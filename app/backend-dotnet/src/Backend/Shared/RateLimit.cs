using System.Globalization;
using System.Text.RegularExpressions;
using Backend.Configuration;

namespace Backend.Shared;

/// <summary>
/// Issue #13 Wave 5 (#236 Rick re-review item 6, agreed with #235/Unity): the single shared C#
/// port of app/backend/rate_limit.py's module-level pieces -- <see cref="RateLimitedEventType"/>,
/// <see cref="FirstRetryBounds"/>/<see cref="SecondRetryBounds"/>, <see cref="ParseRetryHint"/>,
/// and <see cref="RetryDelay"/> -- used by BOTH pipelines' own retry ladders:
/// <see cref="Backend.Realtime.RateLimitRecovery"/> (event-driven, WS
/// `response.create`/`response.done` lifecycle) and <see cref="Backend.Cascade.CascadeRateLimit"/>
/// (wraps one REST chat/STT/TTS call directly and retries it in place). #235 independently added
/// its own copy of these for the realtime pipeline before #236's own cascade-side port had
/// merged; once #235 merged first, the agreed plan (recorded in both PRs' own doc comments) was
/// for whichever PR merged SECOND to extract them here, exactly as Python's own
/// <c>cascade_processor.py</c> and <c>rate_limit.py</c> stay separate modules that both import
/// from this one shared source of truth. The two ORCHESTRATIONS deliberately stay separate C#
/// types (see each type's own doc comment for why) -- only these settings/parsing/bounds/event-
/// shape pieces are shared.
/// </summary>
internal static class RateLimit
{
    public const string RateLimitedEventType = "extension.rate_limited";

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
}

/// <summary>Port of rate_limit.py's `RateLimitSettings`/`RateLimitSettings.from_config` --
/// config.yaml's `resilience.rate_limit` section, with `RATE_LIMIT_RECOVERY_ENABLED` overriding
/// <see cref="Enabled"/> and the two retry-delay defaults further overridable (test hooks only,
/// via <see cref="ConformanceHooks.Seconds"/>). This is the single shared settings type both
/// pipelines now construct -- see <see cref="RateLimit"/>'s own doc comment for the #236/#235
/// extraction history. Each pipeline's own namespace keeps referring to it under its pre-existing
/// local name via a `global using` alias (`Backend.Realtime.RateLimitSettings` /
/// `Backend.Cascade.CascadeRateLimitSettings`), so neither orchestration type nor any existing
/// call site needed renaming.</summary>
public sealed record RateLimitSettings(
    bool Enabled = true, double RetryDelaySeconds = 1.5, double SecondRetryDelaySeconds = 4.0, int MaxRetries = 2)
{
    private const string EnabledEnvVar = "RATE_LIMIT_RECOVERY_ENABLED";

    /// <summary><paramref name="environment"/> is injectable for tests (defaults to the real
    /// process environment when omitted).</summary>
    internal static RateLimitSettings FromAppConfig(AppConfig config, IReadOnlyDictionary<string, string>? environment = null)
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
            : BackendEnvironment.Get(EnabledEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            enabled = Truthy(envValue);
        }

        var retryDelaySeconds = ConformanceHooks.Seconds(
            "CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS", GetDouble(rateLimitSection, "retry_delay_seconds", 1.5));
        var secondRetryDelaySeconds = ConformanceHooks.Seconds(
            "CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS", GetDouble(rateLimitSection, "second_retry_delay_seconds", 4.0));
        var maxRetries = Math.Max(0, GetInt(rateLimitSection, "max_retries", 2));

        return new RateLimitSettings(enabled, retryDelaySeconds, secondRetryDelaySeconds, maxRetries);
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
