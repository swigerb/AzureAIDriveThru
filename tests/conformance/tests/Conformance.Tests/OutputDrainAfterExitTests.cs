using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Refs #259 (Rick's #267 review, required item (a)): end-to-end proof that draining a process's
/// captured output fully before classifying its exit (the fix in the one shared
/// <see cref="ExitClassification.DrainAndClassifyAsync"/> helper both
/// <see cref="DotnetBackendLauncher"/> and <see cref="PythonBackendLauncher"/> call from their own
/// <c>WaitForListeningAndHealthyAsync</c> -- block on the parameterless <see
/// cref="Process.WaitForExit()"/> overload once <see cref="Process.HasExited"/> is observed true,
/// BEFORE reading <see cref="CapturedProcessOutput.Dump"/>) actually matters, not just a
/// theoretical concern. Exercises the shared helper directly (not a hand-rolled
/// re-implementation of the pattern) so that deleting the drain from inside <see
/// cref="ExitClassification"/> itself -- not just from one launcher -- is what this test is
/// sensitive to; see Rick's #267 review finding 4, which flagged the pre-this-fix version of this
/// test for only proving the general <see cref="Process.WaitForExit()"/> contract, never the
/// launchers' own code path.
///
/// Same manufacturing technique as
/// <see cref="DotnetBackendLauncherPortRaceTests"/>'s forced bind collision: rather than relying
/// on a rare, unreproducible timing window (a crashing backend that happens to flush its final
/// "address already in use" line asynchronously right as the process object's own
/// <c>HasExited</c> flips), this test manufactures a GUARANTEED drain race by having a real child
/// process emit a large burst of stdout lines (far more than a single OS pipe-buffer flush can
/// deliver instantaneously) ending in a known sentinel line, then exit immediately -- and polls
/// <see cref="Process.HasExited"/> in a tight, delay-free spin loop from the parent side, which
/// reliably observes the OS-level exit well before <c>BeginOutputReadLine</c>'s asynchronous
/// callback queue has delivered every line to <see cref="CapturedProcessOutput"/>.
/// </summary>
[Trait("Category", "Harness")]
public sealed class OutputDrainAfterExitTests
{
    private const int BurstLineCount = 20_000;
    private const string SentinelLine = "SENTINEL-LAST-LINE-#259";

    /// <summary>
    /// Mutation check: delete (or no-op) the <see cref="Process.WaitForExit()"/> call inside <see
    /// cref="ExitClassification.DrainAndClassifyAsync"/> -- restoring the pre-#259 behaviour of
    /// reading <see cref="CapturedProcessOutput.Dump"/> immediately once <see
    /// cref="Process.HasExited"/> is observed true, with no drain wait -- and this assertion
    /// fails: the tight spin loop reliably observes the OS-level exit before the async output
    /// pump has delivered all <see cref="BurstLineCount"/> lines, so the classifying exception's
    /// message is missing <see cref="SentinelLine"/> (and/or earlier lines) without the fix. With
    /// the fix, <see cref="Process.WaitForExit()"/>'s documented guarantee (it blocks until BOTH
    /// the process has exited AND every redirected stream reader started via
    /// <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c> has reached EOF) makes the dump
    /// complete every time, deterministically -- not probabilistically. Measured rate when this
    /// drain is removed: see the README's "Full output drain before classifying a crash" entry.
    /// The race reproduces probabilistically (the rate varies by machine and CPU contention,
    /// observed anywhere from roughly 25% to 60% locally across different boxes); the fix itself
    /// is deterministic.
    /// </summary>
    [Fact]
    public async Task DrainAndClassifyAsync_message_always_contains_the_final_line_of_a_large_burst_from_a_fast_exiting_process()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);

        var script =
            $"for i in range({BurstLineCount}):\n" +
            "    print(f'line {i}')\n" +
            $"print('{SentinelLine}')\n";

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

        var startedAt = DateTimeOffset.UtcNow;
        Assert.True(process.Start(), "Failed to start the Python venv interpreter for this test.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var ct = TestContext.Current.CancellationToken;

        // Deliberately NO Task.Delay/sleep here -- a tight spin loop observes HasExited flipping
        // to true as early as physically possible on this machine, maximising the chance the
        // async BeginOutputReadLine callback queue (which delivers lineCount lines across many
        // separate ThreadPool callbacks) is still catching up at that exact instant.
        while (!process.HasExited)
        {
            ct.ThrowIfCancellationRequested();
        }

        // The fix under test: the shared helper both launchers call from
        // WaitForListeningAndHealthyAsync once HasExited is observed true -- drains the redirected
        // streams BEFORE reading/classifying the dump. A generous 30s deadline keeps this a no-op
        // bound (the process has already exited; the drain itself is near-instant) rather than
        // something this test could itself race.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var ex = await ExitClassification.DrainAndClassifyAsync(process, output, startedAt, deadline, "Test");

        Assert.Contains(SentinelLine, ex.Message);
    }
}
