using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #259: end-to-end proof that draining a process's captured output fully before
/// classifying its exit (the fix in
/// <see cref="DotnetBackendLauncher"/>/<see cref="PythonBackendLauncher"/>'s shared
/// <c>WaitForListeningAndHealthyAsync</c> pattern -- call the parameterless, blocking
/// <see cref="Process.WaitForExit()"/> overload once <see cref="Process.HasExited"/> is observed
/// true, BEFORE reading <see cref="CapturedProcessOutput.Dump"/> -- actually matters, not just a
/// theoretical concern.
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
    /// Mutation check: delete (or no-op) the <see cref="Process.WaitForExit()"/> call below --
    /// restoring the pre-#259 behaviour of reading <see cref="CapturedProcessOutput.Dump"/>
    /// immediately once <see cref="Process.HasExited"/> is observed true, with no drain wait --
    /// and this assertion fails: the tight spin loop reliably observes the OS-level exit before
    /// the async output pump has delivered all <see cref="BurstLineCount"/> lines, so the dump is
    /// missing <see cref="SentinelLine"/> (and/or earlier lines) without the fix. With the fix,
    /// <see cref="Process.WaitForExit()"/>'s documented guarantee (it blocks until BOTH the
    /// process has exited AND every redirected stream reader started via
    /// <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c> has reached EOF) makes the dump
    /// complete every time, deterministically -- not probabilistically.
    /// </summary>
    [Fact]
    public async Task Dump_after_WaitForExit_always_contains_the_final_line_of_a_large_burst_from_a_fast_exiting_process()
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

        // The fix under test: block until the redirected streams are fully drained BEFORE
        // classifying/reading the dump, exactly as WaitForListeningAndHealthyAsync does.
        process.WaitForExit();

        var dump = output.Dump();

        Assert.Contains(SentinelLine, dump);
    }
}
