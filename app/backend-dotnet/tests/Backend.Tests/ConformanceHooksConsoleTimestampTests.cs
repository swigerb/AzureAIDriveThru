using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Backend.Tests;

// `Backend.Tests` does not implicitly see the root `Backend` namespace that
// <see cref="ConformanceHooks"/> lives in.
using Backend;

/// <summary>
/// #233 (N32, split from #63): <see cref="ConformanceHooks.ApplyConsoleTimestampFormat"/> is the
/// extracted, directly-testable body of Program.cs's <c>ConformanceHooks.HooksEnabled</c>-gated
/// <c>builder.Logging.AddSimpleConsole(...)</c> call (Program.cs's top-level statements aren't
/// otherwise unit-testable). No env-var/<see cref="ConformanceHooks.HooksEnabled"/> interaction
/// here -- that gating lives in Program.cs, not in this method, so no
/// <c>ClockHookTestCollection</c> pinning is needed.
///
/// Mutation-check: change the format string (e.g. drop the trailing space, or swap the literal
/// <c>'Z'</c> for .NET's round-trip <c>K</c> specifier) and
/// <see cref="ApplyConsoleTimestampFormat_produces_the_exact_shape_the_harness_regex_expects"/>
/// must fail; set <c>UseUtcTimestamp</c> to <c>false</c> (or remove the assignment) and
/// <see cref="ApplyConsoleTimestampFormat_sets_utc_and_the_shared_literal_format_string"/> must
/// fail; revert Program.cs's call site from <c>AddSimpleConsole</c> back to the original (broken)
/// <c>Services.Configure&lt;SimpleConsoleFormatterOptions&gt;(...)</c> and
/// <see cref="AddSimpleConsole_is_what_actually_makes_real_console_output_self_timestamped"/>
/// must fail even though the two option-level tests above still pass (PR #264 review: that was
/// exactly the gap those two tests couldn't catch, since neither one goes through a real
/// <c>ConsoleLoggerProvider</c>).
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
        // #264: SingleLine so the self-timestamp prefixes the same physical line as the actual
        // message, not the bare "level: category[eventId]" line above it -- see this method's doc
        // comment in ConformanceHooks.cs.
        Assert.True(options.SingleLine);
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

    /// <summary>
    /// PR #264 review: Rick found that the original cut of this feature configured
    /// <see cref="SimpleConsoleFormatterOptions"/> via <c>builder.Services.Configure&lt;
    /// SimpleConsoleFormatterOptions&gt;(ConformanceHooks.ApplyConsoleTimestampFormat)</c>
    /// directly -- which silently has NO effect on real output, because
    /// <c>WebApplication.CreateBuilder</c>'s default console registration leaves
    /// <c>ConsoleLoggerOptions.FormatterName</c> <see langword="null"/>, and
    /// <c>ConsoleLoggerProvider</c> falls back to its legacy built-in formatter (which ignores
    /// <see cref="SimpleConsoleFormatterOptions"/> entirely) whenever that name is unset. The two
    /// tests above only ever inspect the options object directly, so neither one could ever catch
    /// this -- this test instead builds a real <see cref="ILoggerFactory"/> the same way
    /// Program.cs does (<c>ILoggingBuilder.AddSimpleConsole(ConformanceHooks.ApplyConsoleTimestampFormat)</c>),
    /// captures actual <see cref="Console.Out"/>, logs one message, and asserts the captured line
    /// is genuinely self-timestamped in the shared shape -- the real contract this feature exists
    /// to deliver.
    /// </summary>
    [Fact]
    public void AddSimpleConsole_is_what_actually_makes_real_console_output_self_timestamped()
    {
        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);

            using (var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddSimpleConsole(ConformanceHooks.ApplyConsoleTimestampFormat);
            }))
            {
                var logger = loggerFactory.CreateLogger("Backend.ConformanceHooksConsoleTimestampTests");
                logger.LogInformation("conformance timestamp end-to-end probe");
            } // Disposing the factory disposes/flushes ConsoleLoggerProvider's background writer.
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capture.ToString();

        // Same literal regex as CapturedProcessOutput.BackendTimestampPrefix (duplicated
        // deliberately: Backend.Tests has no project reference to tests/conformance).
        Assert.Matches(
            new Regex(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z info: Backend\.ConformanceHooksConsoleTimestampTests\[0\] conformance timestamp end-to-end probe$",
                RegexOptions.Multiline),
            output.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// PR #264 review: "make sure production C# console output is unchanged when hooks are off".
    /// Program.cs structurally guarantees this by never calling <c>AddSimpleConsole</c> at all
    /// when <see cref="ConformanceHooks.HooksEnabled"/> is <see langword="false"/> -- there is no
    /// conditional formatting logic to disable, only a call site that is skipped entirely. This
    /// test proves what that skip leaves behind: the same default <see cref="ILoggerFactory"/>
    /// console registration <c>WebApplication.CreateBuilder</c> uses in production, with no
    /// <see cref="ApplyConsoleTimestampFormat"/> involvement whatsoever, produces plain
    /// "level: category[eventId]" / indented-message output with no embedded timestamp line --
    /// i.e. byte-for-byte what every backend run produced before #233, and what every hooks-off
    /// run still produces today.
    /// </summary>
    [Fact]
    public void Default_console_registration_without_AddSimpleConsole_has_no_backend_timestamp_prefix()
    {
        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);

            using (var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddConsole(); // No ApplyConsoleTimestampFormat call -- hooks-disabled baseline.
            }))
            {
                var logger = loggerFactory.CreateLogger("Backend.ConformanceHooksConsoleTimestampTests");
                logger.LogInformation("conformance timestamp end-to-end probe");
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capture.ToString();

        Assert.DoesNotMatch(
            new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z ", RegexOptions.Multiline),
            output);
        Assert.Contains("info: Backend.ConformanceHooksConsoleTimestampTests[0]", output);
        Assert.Contains("conformance timestamp end-to-end probe", output);
    }
}
