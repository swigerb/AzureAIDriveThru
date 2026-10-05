using System.Diagnostics;

namespace Conformance.Harness;

/// <summary>
/// Refs #259 (Rick's #267 review, required item (a)): the shared "drain, then dump, then
/// classify" step both <see cref="DotnetBackendLauncher"/> and <see cref="PythonBackendLauncher"/>
/// run the instant their process's <see cref="Process.HasExited"/> flips true while waiting for
/// `/health`. Factored out of both launchers (which used to each inline an identical copy) so that
/// <see cref="Conformance.Tests.OutputDrainAfterExitTests"/> can assert against this one method
/// instead of a hand-rolled re-implementation of the pattern: removing the drain from here now
/// fails that test directly, instead of only proving the general <see cref="Process.WaitForExit()"/>
/// contract in isolation.
/// </summary>
internal static class ExitClassification
{
    /// <summary>
    /// Drains the process's redirected stdout/stderr, then classifies the (now guaranteed
    /// complete) captured output as either a retryable port-bind race (<see
    /// cref="PortBindRaceException"/>) or a genuine early exit (<see
    /// cref="InvalidOperationException"/>), returning the exception to throw rather than throwing
    /// it directly -- callers decide whether/how to surface it (both launchers throw it
    /// immediately, but <see cref="Conformance.Tests.OutputDrainAfterExitTests"/> only inspects its
    /// message).
    ///
    /// <para>
    /// <b>Drain, bounded by <paramref name="deadline"/>:</b> <see cref="Process.HasExited"/> can
    /// flip the instant the OS reports the process has exited, while the redirected stdout/stderr
    /// lines (delivered via <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c>) are still being
    /// delivered asynchronously on a ThreadPool callback -- reading <paramref name="output"/>'s
    /// dump at that exact instant can miss the final lines (e.g. the "address already in use" text
    /// a crash-on-bind prints right before exiting), making the classification below see an
    /// incomplete dump and misclassify a port race as an unrecognised crash (or vice versa). The
    /// parameterless <see cref="Process.WaitForExit()"/> overload's documented guarantee is that it
    /// blocks until BOTH the process has exited AND every redirected stream reader has reached EOF
    /// -- but it is unbounded: if a crashed backend ever leaves a grandchild process holding the
    /// write end of the pipe open, that call blocks forever, hanging the whole suite past its own
    /// `/health` timeout until CI's blame-hang watchdog finally kills the job. Run on a background
    /// thread and raced against <paramref name="deadline"/> instead, so an orphaned grandchild
    /// can't hang this call past the caller's own remaining budget -- worst case, the dump read
    /// immediately afterwards is the same kind of possibly-incomplete snapshot the pre-#259 code
    /// always took, not a new failure mode.
    /// </para>
    /// </summary>
    public static async Task<Exception> DrainAndClassifyAsync(
        Process process, CapturedProcessOutput output, DateTimeOffset startedAt, DateTimeOffset deadline,
        string backendName)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            var drain = Task.Run(process.WaitForExit);
            await Task.WhenAny(drain, Task.Delay(remaining)).ConfigureAwait(false);
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var dump = output.Dump();

        if (PortRaceDetection.ShouldRetry(elapsed, dump))
        {
            return new PortBindRaceException(
                $"{backendName} backend exited immediately (code {process.ExitCode}), " +
                $"{elapsed.TotalSeconds:F1}s after starting, with output matching a TCP " +
                $"port-bind failure signature -- treating as a port race between " +
                $"something else and the backend's own bind.\n" +
                $"--- backend stdout/stderr ---\n{dump}");
        }

        return new InvalidOperationException(
            $"{backendName} backend exited early (code {process.ExitCode}) before becoming healthy.\n" +
            $"--- backend stdout/stderr ---\n{dump}");
    }
}
