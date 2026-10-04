using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Console;

namespace Backend;

/// <summary>
/// Port of the ``now(tz)`` half of app/backend/conformance_hooks.py (docs/dotnet_mapping.md,
/// issue #14): lets the black-box conformance harness freeze the *business-logic* wall-clock read
/// <see cref="Backend.Ordering.OrderState"/>'s happy-hour lookup uses, deterministically and fast,
/// without sleeping through real wall-clock minutes or hard-coding guesses about production
/// values. A no-op (returns the real current time) unless <c>CONFORMANCE_TEST_HOOKS=1</c> is set
/// in the process environment.
///
/// Also ports Python's ``seconds(env_var, default)`` timer-duration override (see
/// <see cref="Seconds"/>) -- first used by issue #13 Wave 4's rate-limit retry ladder, and now
/// also by Cascade/CascadeRateLimit.cs's retry delays (issue #13's cascade pipeline). Also carries
/// <see cref="CascadeFakeToken"/> (the `cascade_credential()`/`CONFORMANCE_CASCADE_FAKE_TOKEN`
/// fake-bearer-token substitution) added here for the same reason, rather than a second,
/// competing hooks module -- exactly mirroring Python's "one shared, centralised hooks module"
/// design. `cascade_chat_kwargs()` has no C# equivalent: it exists only to relax azure-core's
/// BearerTokenCredentialPolicy's https-only enforcement for the azure-ai-inference SDK, which the
/// C# port doesn't use (a plain HttpClient call has no such policy to relax in the first place).
///
/// NEVER set CONFORMANCE_TEST_HOOKS in infra/ (bicep), the Dockerfile, or azure.yaml -- see
/// conformance_hooks.py's own module docstring for why (a dedicated guard test scans those files
/// for the literal string in the Python tree; a C# mirror of that guard is out of this file's
/// scope but the same rule applies).
/// </summary>
public static class ConformanceHooks
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string FixedNowEnv = "CONFORMANCE_FIXED_NOW";
    private const string CascadeFakeTokenEnv = "CONFORMANCE_CASCADE_FAKE_TOKEN";

    // Accepts a trailing numeric offset ("+05:00"/"-0500") or a literal "Z" (UTC) -- matches
    // Python's `datetime.fromisoformat` contract of "RFC 3339 with an explicit numeric UTC
    // offset", never an IANA zone *name* suffix.
    private static readonly Regex ExplicitOffsetSuffix = new(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    /// <summary>Live re-check of <c>CONFORMANCE_TEST_HOOKS</c> -- read fresh every call (never
    /// cached), matching Python's <c>hooks_enabled_now()</c> re-read contract so a test process
    /// that mutates the env var mid-run is never stuck on a stale snapshot.</summary>
    public static bool HooksEnabled =>
        (Environment.GetEnvironmentVariable(EnabledEnv) ?? "").Trim() == "1";

    /// <summary>Returns the current time in <paramref name="tz"/>. Identical to
    /// <c>DateTimeOffset.Now</c> converted into <paramref name="tz"/> unless test hooks are
    /// enabled AND <c>CONFORMANCE_FIXED_NOW</c> is set, in which case that fixed instant is
    /// returned instead (converted into <paramref name="tz"/> so callers always get a consistent
    /// offset for the zone they asked for).</summary>
    public static DateTimeOffset Now(TimeZoneInfo tz)
    {
        if (HooksEnabled)
        {
            var raw = Environment.GetEnvironmentVariable(FixedNowEnv);
            if (!string.IsNullOrEmpty(raw))
            {
                return TimeZoneInfo.ConvertTime(ParseFixedNow(raw), tz);
            }
        }
        return TimeZoneInfo.ConvertTime(DateTimeOffset.Now, tz);
    }

    /// <summary>Port of conformance_hooks.py's ``seconds(env_var, default)``: overrides a timer
    /// duration fed into a delayed send (idle timeout, resume grace, resume nudge, the greeting
    /// timeout, issue #13 Wave 4's two realtime rate-limit retry delays, and the cascade
    /// pipeline's own rate-limit retry delays -- see Cascade/CascadeRateLimit.cs). Returns
    /// <paramref name="defaultValue"/> unchanged unless test hooks are enabled AND
    /// <paramref name="envVar"/> is set to a non-empty value, in which case the value must parse as
    /// a finite, strictly-positive number or this throws immediately -- it does NOT silently fall
    /// back to <paramref name="defaultValue"/>, matching Python's own fail-fast-at-startup
    /// contract: every call site assigns the result once, so a bad override should fail the
    /// backend's startup with a clear error instead of quietly running an entire test session with
    /// a wrong timer value. Has no effect on <see cref="Now"/> (see this class's own doc comment:
    /// the two mechanisms are independent).</summary>
    /// <exception cref="InvalidOperationException"><paramref name="envVar"/> is set but not a
    /// positive, finite number of seconds, while test hooks are enabled.</exception>
    public static double Seconds(string envVar, double defaultValue)
    {
        if (!HooksEnabled)
        {
            return defaultValue;
        }
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"{envVar} must be a positive, finite number of seconds (test hooks are enabled), " +
                $"got unparseable value: '{raw}'");
        }
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidOperationException(
                $"{envVar} must be a positive, finite number of seconds (test hooks are enabled), got: '{raw}'");
        }
        return value;
    }

    /// <summary>Port of conformance_hooks.py's `cascade_credential()`: the fixed bearer token the
    /// cascade pipeline's Foundry chat/STT/TTS calls should present instead of a real
    /// <c>DefaultAzureCredential</c>, when test hooks are enabled AND
    /// <c>CONFORMANCE_CASCADE_FAKE_TOKEN</c> is set to a non-empty value; <c>null</c> otherwise, in
    /// which case the caller (Program.cs) constructs the real credential exactly as it already
    /// does for the realtime pipeline's own upstream connect (see
    /// <see cref="Realtime.DefaultAzureCredentialTokenProvider"/>). Cascade has no api-key
    /// fallback to reuse (unlike realtime's own conformance-harness credential story), so this
    /// fake-token substitution is the only way its three REST calls can be exercised against the
    /// conformance harness's fakes without a real Azure AD identity.</summary>
    public static string? CascadeFakeToken =>
        HooksEnabled && Environment.GetEnvironmentVariable(CascadeFakeTokenEnv) is { Length: > 0 } token
            ? token
            : null;

    /// <summary>
    /// #233 (N32, split from #63): Program.cs's <c>builder.Services.Configure&lt;
    /// SimpleConsoleFormatterOptions&gt;(ConformanceHooks.ApplyConsoleTimestampFormat)</c> call
    /// (guarded there by <see cref="HooksEnabled"/>, since this method is extracted purely so it
    /// has a directly unit-testable name/signature -- Program.cs's top-level statements aren't
    /// otherwise unit-testable). Self-timestamps every console log line with a UTC,
    /// microsecond-resolution instant THIS process actually logged it at, mirroring
    /// app/backend/app.py's conformance-hooks-gated logging.Formatter change: see that file's
    /// <c>_ConformanceTimestampFormatter</c> doc comment for why (the conformance harness's
    /// <c>CapturedProcessOutput</c> otherwise stamps a line at the moment it *observes* it over
    /// the redirected stdout/stderr pipe, which can lag the backend's actual write under CI/CPU
    /// contention enough to make sequential events look simultaneous or reordered).
    ///
    /// The literal <c>'Z'</c> (not .NET's <c>K</c>/round-trip-offset specifier) matches the
    /// Python side's literal <c>Z</c> suffix exactly, and the trailing space is load-bearing: both
    /// together produce the exact <c>yyyy-MM-ddTHH:mm:ss.ffffffZ </c> shape
    /// <c>CapturedProcessOutput.BackendTimestampPrefix</c>'s regex expects, so the harness only
    /// needs one timestamp parser for both backends' lines.
    /// </summary>
    public static void ApplyConsoleTimestampFormat(SimpleConsoleFormatterOptions options)
    {
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffff'Z' ";
    }

    private static DateTimeOffset ParseFixedNow(string raw)
    {
        if (!ExplicitOffsetSuffix.IsMatch(raw.Trim()))
        {
            throw new InvalidOperationException(
                $"{FixedNowEnv} must be an RFC 3339 timestamp with an explicit numeric UTC " +
                $"offset (e.g. '2026-07-04T15:30:00-05:00' or '...Z'), got: '{raw}'");
        }
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new InvalidOperationException($"{FixedNowEnv} is not a parseable RFC 3339 timestamp: '{raw}'");
        }
        return parsed;
    }
}
