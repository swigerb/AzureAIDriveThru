using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for Rick's #151 review of issue #135: <see cref="DotnetBackendLauncher.EnsureBuiltAsync"/>
/// must build Backend.csproj EXACTLY ONCE per test process, never conditioned on whether a dll
/// already happens to exist on disk (R1) -- a pre-existing dll proves nothing about whether it
/// reflects the source about to run against it. These tests drive <see
/// cref="DotnetBackendLauncher.EnsureBuiltAsync"/> directly against the <see
/// cref="DotnetBackendLauncher.RunBuild"/> seam (a fake build, never real MSBuild), so they're
/// fast, deterministic, and require no SDK. Each test calls <see
/// cref="DotnetBackendLauncher.ResetForTests"/> first so the shared static build cache never
/// leaks between tests.
/// </summary>
public sealed class DotnetBackendLauncherBuildGateTests
{
    [Fact]
    public async Task EnsureBuiltAsync_builds_exactly_once_even_when_the_dll_already_exists_on_disk()
    {
        var ct = TestContext.Current.CancellationToken;
        DotnetBackendLauncher.ResetForTests();
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_builds_exactly_once_even_when_the_dll_already_exists_on_disk));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            // This is the exact shape of the R1 bug: a dll already on disk before the harness ever
            // runs. The pre-fix EnsureBuiltAsync treated `File.Exists(dllPath)` as "already built"
            // and skipped the build outright -- so a fixture would silently launch this stale dll
            // instead of whatever the mutation below represents (a rebuild reflecting new source).
            File.WriteAllText(dllPath, "stale dll content from a previous run");

            var buildInvocations = 0;
            DotnetBackendLauncher.RunBuild = (_, _) =>
            {
                Interlocked.Increment(ref buildInvocations);
                File.WriteAllText(dllPath, "freshly built dll content");
                return Task.CompletedTask;
            };

            var resolvedDllPath = await DotnetBackendLauncher.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(1, buildInvocations);
            Assert.Equal(dllPath, resolvedDllPath);
            Assert.Equal("freshly built dll content", File.ReadAllText(dllPath));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            DotnetBackendLauncher.ResetForTests();
        }
    }

    /// <summary>
    /// Mutation check for the concurrency half of R1/the original #135 fix: eight concurrent
    /// <see cref="DotnetBackendLauncher.EnsureBuiltAsync"/> callers (the shape that used to race
    /// concurrent `dotnet run` invocations against each other, MSB4018) must still trigger the
    /// fake build exactly once. The fake build is held open by a <see cref="TaskCompletionSource"/>
    /// -- never a timer/delay -- until all eight callers have entered <c>EnsureBuiltAsync</c>, so
    /// the test actually exercises the race (all eight genuinely concurrent) instead of letting a
    /// winner finish before the rest even start.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_with_eight_concurrent_callers_builds_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        DotnetBackendLauncher.ResetForTests();
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_with_eight_concurrent_callers_builds_exactly_once));
        try
        {
            const int callerCount = 8;
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var buildInvocations = 0;
            var arrivals = new SemaphoreSlim(0, callerCount);
            var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DotnetBackendLauncher.RunBuild = async (_, token) =>
            {
                Interlocked.Increment(ref buildInvocations);
                await releaseBuild.Task.WaitAsync(token);
                File.WriteAllText(dllPath, "freshly built dll content");
            };

            var callers = Enumerable.Range(0, callerCount)
                .Select(_ => Task.Run(async () =>
                {
                    arrivals.Release();
                    return await DotnetBackendLauncher.EnsureBuiltAsync(csprojPath, ct);
                }, ct))
                .ToArray();

            // Event-driven, not a timer: block here until every one of the eight callers has
            // actually entered EnsureBuiltAsync, then (only then) let the held-open fake build
            // complete.
            for (var i = 0; i < callerCount; i++)
            {
                await arrivals.WaitAsync(ct);
            }

            releaseBuild.SetResult();

            var results = await Task.WhenAll(callers);

            Assert.Equal(1, buildInvocations);
            Assert.All(results, result => Assert.Equal(dllPath, result));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            DotnetBackendLauncher.ResetForTests();
        }
    }

    /// <summary>
    /// A failing build must surface (not swallow) its failure to its caller, and the NEXT caller
    /// must retry rather than replaying a cached fault forever -- otherwise one transient MSBuild
    /// failure would wedge every subsequent fixture in the same test process.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_surfaces_a_failed_build_and_the_next_caller_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        DotnetBackendLauncher.ResetForTests();
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_surfaces_a_failed_build_and_the_next_caller_retries));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var buildInvocations = 0;
            DotnetBackendLauncher.RunBuild = (_, _) =>
            {
                var invocation = Interlocked.Increment(ref buildInvocations);
                if (invocation == 1)
                {
                    throw new InvalidOperationException("fake MSBuild failure: CS9999 something didn't compile");
                }

                File.WriteAllText(dllPath, "freshly built dll content");
                return Task.CompletedTask;
            };

            var firstCallEx = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DotnetBackendLauncher.EnsureBuiltAsync(csprojPath, ct));
            Assert.Contains("fake MSBuild failure: CS9999 something didn't compile", firstCallEx.Message, StringComparison.Ordinal);

            var resolvedDllPath = await DotnetBackendLauncher.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(2, buildInvocations);
            Assert.Equal(dllPath, resolvedDllPath);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            DotnetBackendLauncher.ResetForTests();
        }
    }

    private static string CreateScratchRoot(string testName)
    {
        // Kept under this project's own build output (gitignored via obj/) rather than the OS
        // temp directory, per repo convention (see RepoPathsTests.CreateScratchRoot) of not
        // scattering test artifacts outside the tree.
        var scratch = Path.Combine(AppContext.BaseDirectory, "dotnet-backend-launcher-build-gate-test-scratch", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }
}
