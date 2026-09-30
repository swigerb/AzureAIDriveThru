using System.Globalization;
using System.Text.RegularExpressions;

namespace Backend;

/// <summary>
/// Port of the ``now(tz)`` half of app/backend/conformance_hooks.py (docs/dotnet_mapping.md,
/// issue #14): lets the black-box conformance harness freeze the *business-logic* wall-clock read
/// <see cref="Backend.Ordering.OrderState"/>'s happy-hour lookup uses, deterministically and fast,
/// without sleeping through real wall-clock minutes or hard-coding guesses about production
/// values. A no-op (returns the real current time) unless <c>CONFORMANCE_TEST_HOOKS=1</c> is set
/// in the process environment.
///
/// Deliberately scoped to only the piece #14's order engine needs. Python's module also has a
/// ``seconds(env_var, default)`` timer-duration override and a ``cascade_credential()``/
/// ``cascade_chat_kwargs()`` pair -- those affect asyncio timer durations and the cascade Foundry
/// chat client respectively, neither of which any code in this file touches. They belong to
/// whichever wave first needs testable timer durations for the C# realtime pipeline (#13) or the
/// cascade pipeline, and should be added to this same file (not a second, competing one) when that
/// wave lands, exactly mirroring Python's "one shared, centralised hooks module" design.
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
