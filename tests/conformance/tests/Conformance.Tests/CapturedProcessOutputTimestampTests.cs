using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #233 (N32, split from #63): proves <see cref="CapturedProcessOutput"/> prefers a backend's own
/// embedded self-timestamp (emitted by both backends under <c>CONFORMANCE_TEST_HOOKS=1</c>) over
/// its own read-time <c>DateTimeOffset.UtcNow</c> for the displayed/ordering timestamp, while
/// still stripping that prefix before the line is stored or scanned -- so
/// <see cref="CapturedProcessOutput.CountUnhandledErrors()"/>'s exact-prefix matching
/// (<c>"ERROR:"</c>, <c>"Traceback (most recent call last):"</c>) is completely unaffected either
/// way. Uses the internal <see cref="CapturedProcessOutput.Append"/>/<see
/// cref="CapturedProcessOutput.LastAppendUtc"/> seam (see their doc comments; same
/// <c>InternalsVisibleTo</c> visibility already used by <c>CapturedProcessOutputWaitTests</c>) for
/// deterministic, no-process coverage of the stripping/preferring logic itself, plus one real
/// end-to-end test driving an actual Python process through <see cref="CapturedProcessOutput.Attach"/>
/// to prove the wiring holds for a genuinely captured line, not just a hand-built one.
///
/// Mutation-checked: remove <c>StripBackendTimestamp</c>'s call inside <c>Append</c> (so the raw,
/// un-stripped line is stored/scanned/dumped) and
/// <see cref="Dump_uses_the_embedded_timestamp_not_read_time_when_the_line_has_one"/> and
/// <see cref="CountUnhandledErrors_still_counts_a_backend_timestamped_error_header_and_traceback"/>
/// both fail.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class CapturedProcessOutputTimestampTests
{
    [Fact]
    public void Dump_uses_the_embedded_timestamp_not_read_time_when_the_line_has_one()
    {
        var output = new CapturedProcessOutput();

        // A deliberately far-past instant -- if Append ever fell back to DateTimeOffset.UtcNow
        // for a line that actually has an embedded timestamp, the dumped bracket would show
        // "now" (within a few ms of this test running) instead of this fixed past instant.
        output.Append("ERR", "2020-01-02T03:04:05.123456Z INFO:sonic-drive-in:server started");

        Assert.Equal("[03:04:05.123 ERR] INFO:sonic-drive-in:server started", output.Dump());
    }

    [Fact]
    public void Dump_falls_back_to_a_recent_timestamp_when_the_line_has_no_embedded_one()
    {
        var output = new CapturedProcessOutput();
        var before = DateTimeOffset.UtcNow.TimeOfDay;

        output.Append("ERR", "INFO:sonic-drive-in:server started");

        var dumped = output.Dump();
        Assert.StartsWith("[", dumped);
        Assert.EndsWith(" ERR] INFO:sonic-drive-in:server started", dumped);

        // The bracketed HH:mm:ss.fff must be within a couple of seconds of "now" -- i.e. real
        // read-time, not derived from (absent) backend content. Compared as a time-of-day
        // TimeSpan (rather than reconstructing a full date) to avoid any UTC-midnight-rollover
        // edge case making this flaky.
        var bracket = dumped[1..dumped.IndexOf(' ')];
        var bracketTimeOfDay = TimeSpan.ParseExact(bracket, @"hh\:mm\:ss\.fff", null);
        var delta = (bracketTimeOfDay - before).Duration();
        Assert.True(
            delta < TimeSpan.FromSeconds(5) || delta > TimeSpan.FromHours(23),
            $"expected the dumped bracket ({bracket}) to be within a few seconds of now ({before}), " +
            $"got a delta of {delta}");
    }

    [Fact]
    public void Append_strips_the_backend_timestamp_prefix_from_the_stored_content()
    {
        var output = new CapturedProcessOutput();

        output.Append("ERR", "2026-07-04T15:30:00.000001Z ERROR:sonic-drive-in:boom");

        // The stored/dumped content must no longer carry the timestamp prefix -- only the
        // original log line survives after the bracket.
        Assert.Equal("[15:30:00.000 ERR] ERROR:sonic-drive-in:boom", output.Dump());
    }

    [Fact]
    public void CountUnhandledErrors_still_counts_a_backend_timestamped_error_header_and_traceback()
    {
        var output = new CapturedProcessOutput();

        output.Append("ERR", "2026-07-04T15:30:00.000001Z ERROR:sonic-drive-in:boom");
        output.Append("ERR", "Traceback (most recent call last):");
        output.Append("ERR", "  File \"rtmt.py\", line 10, in handle");
        output.Append("ERR", "KeyError: 'output'");

        // One incident: the timestamp prefix on the ERROR: header line must not stop
        // ScanLine's content.StartsWith("ERROR:", ...) check from recognising it.
        Assert.Equal(1, output.CountUnhandledErrors());
    }

    [Fact]
    public void CountUnhandledErrors_still_counts_a_backend_timestamped_traceback_without_an_error_header()
    {
        var output = new CapturedProcessOutput();

        // A bare traceback (no preceding logger ERROR: line) is itself one incident -- the
        // Traceback header line can carry an embedded timestamp too (e.g. an uncaught exception
        // printed by Python's default excepthook still goes through app.py's root logger when
        // logger.exception(...) formats a record, but a genuinely bare traceback from an
        // unhandled exception would not -- included here anyway since ScanLine's
        // isTracebackHeader check is exact-match and must also survive the strip).
        output.Append("ERR", "2026-07-04T15:30:00.000001Z Traceback (most recent call last):");
        output.Append("ERR", "  File \"rtmt.py\", line 10, in handle");
        output.Append("ERR", "KeyError: 'output'");

        Assert.Equal(1, output.CountUnhandledErrors());
    }

    [Fact]
    public void CountUnhandledErrors_is_unaffected_by_timestamped_ordinary_non_error_lines()
    {
        var output = new CapturedProcessOutput();

        output.Append("ERR", "2026-07-04T15:30:00.000001Z INFO:sonic-drive-in:server started");
        output.Append("ERR", "2026-07-04T15:30:00.100001Z INFO:sonic-drive-in:a plain diagnostic line");

        Assert.Equal(0, output.CountUnhandledErrors());
    }

    [Fact]
    public void A_line_that_only_superficially_resembles_the_timestamp_prefix_is_left_untouched()
    {
        var output = new CapturedProcessOutput();

        // Not a match: missing the trailing "Z " (just "Z" with no space) -- must be stored and
        // scanned completely unchanged, proving the regex anchors on the real shape rather than
        // loosely matching any leading digits.
        const string line = "2026-07-04T15:30:00.000001Z-ERROR:sonic-drive-in:boom";
        output.Append("ERR", line);

        Assert.Contains(line, output.Dump());
        // "ERROR:" is not at content[0] here (it's preceded by the un-stripped stamp), so this
        // must NOT be recognised as an error header.
        Assert.Equal(0, output.CountUnhandledErrors());
    }

    [Fact]
    public void LastAppendUtc_stays_on_real_read_time_even_when_the_line_carries_a_far_past_embedded_timestamp()
    {
        // #233: WaitForOutputQuiescenceAsync's silence detection is about when *this harness*
        // last observed output, not when the backend logged it -- so _lastAppendUtc (exposed via
        // the internal LastAppendUtc seam) must never be derived from the embedded timestamp.
        var output = new CapturedProcessOutput();
        var before = DateTimeOffset.UtcNow;

        output.Append("ERR", "2000-01-01T00:00:00.000000Z INFO:sonic-drive-in:ancient embedded stamp");

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(output.LastAppendUtc, before, after);
    }

    [Fact]
    public async Task CountUnhandledErrors_counts_a_real_backend_timestamped_error_captured_through_a_live_process()
    {
        // End-to-end: a real short-lived Python process prints the exact literal prefix shape
        // _ConformanceTimestampFormatter produces, through the real Attach/BeginErrorReadLine
        // wiring (not a hand-built Append call), proving the regex/strip logic holds for
        // genuinely captured output too.
        const string script =
            "import sys\n" +
            "print('2026-07-04T15:30:00.000001Z ERROR:sonic-drive-in:boom', file=sys.stderr)\n" +
            "print('2026-07-04T15:30:00.000002Z Traceback (most recent call last):', file=sys.stderr)\n" +
            "print('  File \"rtmt.py\", line 10, in handle', file=sys.stderr)\n" +
            "print(\"KeyError: 'output'\", file=sys.stderr)\n" +
            "print('2026-07-04T15:30:00.000003Z INFO:sonic-drive-in:a plain diagnostic line', file=sys.stderr)\n";

        var repoRoot = RepoPaths.FindRepoRoot();
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        Assert.True(process.Start(), "Failed to start the Python venv interpreter for this test.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, output.CountUnhandledErrors());
        Assert.DoesNotContain("2026-07-04T15:30:00", output.Dump());
        Assert.Contains("[15:30:00.000 ERR] ERROR:sonic-drive-in:boom", output.Dump());
    }
}
