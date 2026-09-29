namespace Conformance.Harness;

/// <summary>
/// Instance-owned "build Backend.csproj exactly once" gate (issue #135; R1/R2 of #151's round-2
/// review). <see cref="DotnetBackendLauncher"/> owns exactly one shared instance for real fixtures
/// (its private <c>Builder</c> field); unit tests construct their own private instances instead of
/// touching anything static.
///
/// R1: this used to be a pair of static fields on <see cref="DotnetBackendLauncher"/> itself
/// (<c>BuildGate</c>/<c>_buildTask</c>) plus a static <c>RunBuild</c> seam and a
/// <c>ResetForTests()</c> escape hatch. That let <c>DotnetBackendLauncherBuildGateTests</c> --
/// which has no xunit <c>[Collection]</c>, so it runs in parallel with
/// <c>ConformanceCollection</c> -- swap the seam to a fake and null the cache while a real fixture
/// in another collection could be mid-<see cref="DotnetBackendLauncher.StartAsync"/> call. A real
/// fixture caught in that window could observe the test's fake build (including its injected
/// failure), block on the test's deliberately-held-open fake, or see the cache nulled and kick off
/// a SECOND real build while backends from the first build were already running -- exactly the
/// MSBuild file-lock race issue #135 exists to remove. Making the gate an instance removes the
/// shared mutable surface entirely: a test's <see cref="DotnetBackendBuildGate"/> and the
/// launcher's shared one are different objects, so nothing a test does to its own instance can
/// ever be observed by a real fixture using the launcher's instance.
///
/// R2: the shared build itself always runs with <see cref="CancellationToken.None"/> (never any
/// individual caller's token) -- only each caller's own <c>WaitAsync(cancellationToken)</c> on the
/// shared task is subject to that caller's token. A cancelled caller therefore only stops ITS OWN
/// wait; it can never cancel the build out from under every other caller sharing the same in-flight
/// build. The retry condition after taking <see cref="_gate"/> covers both a faulted AND a
/// cancelled shared task, so a build that failed -- or was ever left cancelled -- is retried by the
/// next caller instead of wedging the rest of the run.
/// </summary>
internal sealed class DotnetBackendBuildGate(Func<string, CancellationToken, Task> runBuild)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<string>? _buildTask;

    /// <summary>
    /// Builds Backend.csproj EXACTLY ONCE for the lifetime of this gate instance -- never
    /// conditioned on whether a dll already happens to exist on disk (a pre-existing dll proves
    /// nothing about whether it matches the source about to run against it). The single cached
    /// <see cref="_buildTask"/> is the source of truth: once it completes successfully, every
    /// later caller takes the lock-free fast path and just awaits that same completed task; while
    /// it's in flight (or hasn't started, or ended faulted/cancelled), callers briefly take
    /// <see cref="_gate"/> only long enough to either observe the in-flight task or kick off the
    /// one build, then release the gate and wait on the task outside the lock -- so the build
    /// itself never serializes backend *starts*, only the decision of who runs it.
    /// </summary>
    public async Task<string> EnsureBuiltAsync(string csprojPath, CancellationToken cancellationToken)
    {
        var current = _buildTask;
        if (current is { IsCompletedSuccessfully: true })
        {
            return await current.ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _buildTask;

            // Retry on a cancelled shared task exactly like a faulted one: neither means the
            // build succeeded, so the next caller through here must not just replay the same
            // outcome forever.
            if (current is null || (current.IsCompleted && !current.IsCompletedSuccessfully))
            {
                current = BuildAndResolveAsync(csprojPath);
                _buildTask = current;
            }
        }
        finally
        {
            _gate.Release();
        }

        // The shared task above never observes any caller's token (see BuildAndResolveAsync
        // below) -- WaitAsync(cancellationToken) here only cancels THIS caller's wait. If this
        // caller's token fires, everyone else still awaiting `current` (and the build process
        // itself) is entirely unaffected.
        return await current.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildAndResolveAsync(string csprojPath)
    {
        // CancellationToken.None, deliberately: this task is shared by every caller that joins
        // the in-flight build, so it must not be cancellable by whichever caller happened to
        // trigger it (or any single caller waiting on it) -- see this type's own doc comment (R2).
        await runBuild(csprojPath, CancellationToken.None).ConfigureAwait(false);

        var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"'dotnet build \"{csprojPath}\"' completed but the expected output " +
                $"'{dllPath}' is still missing. Check the Configuration/TargetFramework assumptions " +
                "in DotnetBackendLauncher.ResolveDllPath against Backend.csproj/Directory.Build.props.");
        }

        return dllPath;
    }
}
