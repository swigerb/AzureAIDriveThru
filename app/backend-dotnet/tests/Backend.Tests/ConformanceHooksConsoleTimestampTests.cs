using System.Globalization;
using Microsoft.Extensions.Logging.Console;

namespace Backend.Tests;

// `Backend.Tests` does not implicitly see the root `Backend` namespace that
// <see cref="ConformanceHooks"/> lives in.
using Backend;

/// <summary>
/// #233 (N32, split from #63): <see cref="ConformanceHooks.ApplyConsoleTimestampFormat"/> is the
/// extracted, directly-testable body of Program.cs's <c>ConformanceHooks.HooksEnabled</c>-gated
/// <c>builder.Services.Configure&lt;SimpleConsoleFormatterOptions&gt;(...)</c> call (Program.cs's
/// top-level statements aren't otherwise unit-testable). No env-var/<see
/// cref="ConformanceHooks.HooksEnabled"/> interaction here -- that gating lives in Program.cs, not
/// in this method, so no <c>ClockHookTestCollection</c> pinning is needed.
///
/// Mutation-check: change the format string (e.g. drop the trailing space, or swap the literal
/// <c>'Z'</c> for .NET's round-trip <c>K</c> specifier) and
/// <see cref="ApplyConsoleTimestampFormat_produces_the_exact_shape_the_harness_regex_expects"/>
/// must fail; set <c>UseUtcTimestamp</c> to <c>false</c> (or remove the assignment) and
/// <see cref="ApplyConsoleTimestampFormat_sets_utc_and_the_shared_literal_format_string"/> must
/// fail.
/// </summary>
public sealed class ConformanceHooksConsoleTimestampTests
{
    [Fact]
    public void ApplyConsoleTimestampFormat_sets_utc_and_the_shared_literal_format_string()
    {
        var options = new SimpleConsoleFormatterOptions();

        ConformanceHooks.ApplyConsoleTimestampFormat(options);

        Assert.True(options.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-ddTHH:mm:ss.ffffff'Z' ", options.TimestampFormat);
    }

    /// <summary>
    /// The real cross-component contract at stake: this formatted output must be exactly what
    /// tests/conformance's <c>CapturedProcessOutput.BackendTimestampPrefix</c> regex
    /// (<c>^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z) </c>) matches, and exactly what
    /// app/backend/app.py's <c>_ConformanceTimestampFormatter</c> emits -- a mismatch in either
    /// direction would silently stop the harness from recognising/stripping this backend's
    /// embedded timestamps while the other backend's kept working.
    /// </summary>
    [Fact]
    public void ApplyConsoleTimestampFormat_produces_the_exact_shape_the_harness_regex_expects()
    {
        var options = new SimpleConsoleFormatterOptions();
        ConformanceHooks.ApplyConsoleTimestampFormat(options);

        // 123456 microseconds == 1,234,560 ticks (1 tick = 100ns = 0.1us).
        var formatted = new DateTimeOffset(2026, 7, 4, 15, 30, 0, TimeSpan.Zero)
            .AddTicks(1_234_560)
            .ToString(options.TimestampFormat, CultureInfo.InvariantCulture);

        Assert.Equal("2026-07-04T15:30:00.123456Z ", formatted);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z $", formatted);
    }
}
