using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="DotnetBackendBuildGate"/> (Rick's #151 review of issue #135, rounds 1
/// and 2). Each test constructs its OWN gate instance -- nothing here is process-wide static state,
/// so this class needs no xunit <c>[Collection]</c> and can run in parallel with every real fixture
/// in <c>ConformanceCollection</c> without any risk of a test's fake build or held-open task ever
/// being observed by a real <see cref="DotnetBackendLauncher.StartAsync"/> call (that risk is
/// exactly what R1 of the round-2 review flagged against the previous static-seam design). These
/// tests drive <see cref="DotnetBackendBuildGate.EnsureBuiltAsync"/> directly against a fake
/// <c>runBuild</c> delegate passed into each gate's constructor (never real MSBuild), so they're
/// fast, deterministic, and require no SDK.
/// </summary>
public sealed class DotnetBackendBuildGateTests
{
    [Fact]
    public async Task EnsureBuiltAsync_builds_exactly_once_even_when_the_dll_already_exists_on_disk()
    {
        var ct = TestContext.Current.CancellationToken;
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_builds_exactly_once_even_when_the_dll_already_exists_on_disk));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            // This is the exact shape of the original R1 bug: a dll already on disk before the
            // harness ever runs. The pre-fix EnsureBuiltAsync treated `File.Exists(dllPath)` as
            // "already built" and skipped the build outright -- so a fixture would silently
            // launch this stale dll instead of whatever the mutation below represents (a rebuild
            // reflecting new source).
            File.WriteAllText(dllPath, "stale dll content from a previous run");

            var buildInvocations = 0;
            var gate = new DotnetBackendBuildGate((_, _) =>
            {
                Interlocked.Increment(ref buildInvocations);
                File.WriteAllText(dllPath, "freshly built dll content");
                return Task.CompletedTask;
            });

            var resolvedDllPath = await gate.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(1, buildInvocations);
            Assert.Equal(dllPath, resolvedDllPath);
            Assert.Equal("freshly built dll content", File.ReadAllText(dllPath));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Mutation check for the concurrency half of R1/the original #135 fix: eight concurrent
    /// <see cref="DotnetBackendBuildGate.EnsureBuiltAsync"/> callers sharing ONE gate instance (the
    /// shape that used to race concurrent `dotnet run` invocations against each other, MSB4018)
    /// must still trigger the fake build exactly once. The fake build is held open by a
    /// <see cref="TaskCompletionSource"/> -- never a timer/delay -- until all eight callers have
    /// entered <c>EnsureBuiltAsync</c>, so the test actually exercises the race (all eight
    /// genuinely concurrent) instead of letting a winner finish before the rest even start.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_with_eight_concurrent_callers_builds_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
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
            var gate = new DotnetBackendBuildGate(async (_, token) =>
            {
                Interlocked.Increment(ref buildInvocations);
                await releaseBuild.Task.WaitAsync(token);
                File.WriteAllText(dllPath, "freshly built dll content");
            });

            var callers = Enumerable.Range(0, callerCount)
                .Select(_ => Task.Run(async () =>
                {
                    arrivals.Release();
                    return await gate.EnsureBuiltAsync(csprojPath, ct);
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
        }
    }

    /// <summary>
    /// A failing build must surface (not swallow) its failure to its caller, and the NEXT caller
    /// must retry rather than replaying a cached fault forever -- otherwise one transient MSBuild
    /// failure would wedge every subsequent fixture sharing this gate instance.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_surfaces_a_failed_build_and_the_next_caller_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_surfaces_a_failed_build_and_the_next_caller_retries));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var buildInvocations = 0;
            var gate = new DotnetBackendBuildGate((_, _) =>
            {
                var invocation = Interlocked.Increment(ref buildInvocations);
                if (invocation == 1)
                {
                    throw new InvalidOperationException("fake MSBuild failure: CS9999 something didn't compile");
                }

                File.WriteAllText(dllPath, "freshly built dll content");
                return Task.CompletedTask;
            });

            var firstCallEx = await Assert.ThrowsAsync<InvalidOperationException>(
                () => gate.EnsureBuiltAsync(csprojPath, ct));
            Assert.Contains("fake MSBuild failure: CS9999 something didn't compile", firstCallEx.Message, StringComparison.Ordinal);

            var resolvedDllPath = await gate.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(2, buildInvocations);
            Assert.Equal(dllPath, resolvedDllPath);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// R2 of the round-2 review: the first caller to reach a shared in-flight build must not be
    /// able to cancel that build (or the cache built from it) out from under every other caller
    /// also waiting on it. Caller A's own CTS is cancelled while the fake build is still held open
    /// -- A must observe its own cancellation, but the shared build keeps running for caller B,
    /// which still gets the resolved dll path from the SAME (single) build.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_a_cancelled_caller_does_not_poison_the_build_for_later_callers()
    {
        var ct = TestContext.Current.CancellationToken;
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_a_cancelled_caller_does_not_poison_the_build_for_later_callers));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var buildInvocations = 0;
            var buildEntered = new SemaphoreSlim(0, 1);
            var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new DotnetBackendBuildGate(async (_, token) =>
            {
                Interlocked.Increment(ref buildInvocations);
                buildEntered.Release();
                // The gate always calls this delegate with CancellationToken.None (R2), so
                // `token` here is never cancelled by caller A's CTS below -- that is the whole
                // point being asserted by this test.
                await releaseBuild.Task.WaitAsync(token);
                File.WriteAllText(dllPath, "freshly built dll content");
            });

            using var callerACts = new CancellationTokenSource();
            var callerATask = Task.Run(() => gate.EnsureBuiltAsync(csprojPath, callerACts.Token), ct);

            // Wait until the fake build has actually started before cancelling caller A, so this
            // exercises "cancelled while a build is genuinely in flight", not "cancelled before
            // anything started".
            await buildEntered.WaitAsync(ct);
            await callerACts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerATask);

            // Now let the held-open fake build finish. Caller B uses a fresh, non-cancelled token
            // and must still get the dll path from the SAME build caller A was (unsuccessfully,
            // for itself) waiting on.
            releaseBuild.SetResult();
            var callerBResult = await gate.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(1, buildInvocations);
            Assert.Equal(dllPath, callerBResult);
            Assert.Equal("freshly built dll content", File.ReadAllText(dllPath));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// R2 of the round-2 review: a build that ends up Cancelled (as opposed to Faulted) must be
    /// retried by the next caller exactly like a Faulted one -- the retry condition can't only
    /// check <c>IsFaulted</c>, or a caller unlucky enough to see the shared task end up Cancelled
    /// would replay that same cancellation forever.
    /// </summary>
    [Fact]
    public async Task EnsureBuiltAsync_retries_after_a_cancelled_build_same_as_a_failed_build()
    {
        var ct = TestContext.Current.CancellationToken;
        var scratch = CreateScratchRoot(nameof(EnsureBuiltAsync_retries_after_a_cancelled_build_same_as_a_failed_build));
        try
        {
            var csprojPath = Path.Combine(scratch, "Backend.csproj");
            var dllPath = DotnetBackendLauncher.ResolveDllPath(csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var buildInvocations = 0;
            var gate = new DotnetBackendBuildGate((_, _) =>
            {
                var invocation = Interlocked.Increment(ref buildInvocations);
                if (invocation == 1)
                {
                    // Simulates the shared build itself ending up Cancelled (for example a build
                    // process killed out-of-band) rather than Faulted -- the async state machine
                    // marks the returned Task Canceled, not Faulted, when an
                    // OperationCanceledException propagates out of an async method/lambda body.
                    throw new OperationCanceledException("fake: build process observed a cancellation");
                }

                File.WriteAllText(dllPath, "freshly built dll content");
                return Task.CompletedTask;
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => gate.EnsureBuiltAsync(csprojPath, ct));

            var resolvedDllPath = await gate.EnsureBuiltAsync(csprojPath, ct);

            Assert.Equal(2, buildInvocations);
            Assert.Equal(dllPath, resolvedDllPath);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string CreateScratchRoot(string testName)
    {
        // Kept under this project's own build output (gitignored via obj/) rather than the OS
        // temp directory, per repo convention (see RepoPathsTests.CreateScratchRoot) of not
        // scattering test artifacts outside the tree.
        var scratch = Path.Combine(AppContext.BaseDirectory, "dotnet-backend-build-gate-test-scratch", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }
}
