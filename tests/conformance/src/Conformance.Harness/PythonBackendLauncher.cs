using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Conformance.Harness;

/// <summary>Thrown by <see cref="BackendLauncherFactory"/> for CONFORMANCE_BACKEND=dotnet when
/// <see cref="DotnetPlaceholderPolicy.ShouldSkip"/> allows skipping (PR #22 review item 15:
/// CONFORMANCE_ALLOW_SKIP=1 and not CI) — a placeholder until the S2 .NET backend exists.
/// <see cref="ConformanceFixture"/> catches only this specific type and turns it into a skip; any
/// other exception type (see <see cref="ConformanceBackendUnavailableException"/>) fails the
/// suite normally.</summary>
public sealed class ConformanceBackendNotImplementedException(string message) : Exception(message);

/// <summary>Thrown by <see cref="BackendLauncherFactory"/> for CONFORMANCE_BACKEND=dotnet when
/// <see cref="DotnetPlaceholderPolicy.ShouldSkip"/> does not allow skipping (the default, and
/// always in CI) -- deliberately a *different* type than
/// <see cref="ConformanceBackendNotImplementedException"/> so <see cref="ConformanceFixture"/>'s
/// narrow catch clause never accidentally swallows it: it propagates out of
/// <c>InitializeAsync</c> and fails every test in the collection (PR #22 review item 15 --
/// CI must never silently skip real backend coverage just because the S2 .NET backend doesn't
/// exist yet).</summary>
public sealed class ConformanceBackendUnavailableException(string message) : Exception(message);

/// <summary>Thrown internally by <see cref="PythonBackendLauncher"/> when an early process exit
/// looks like a TCP port-bind race rather than a real backend crash (see
/// <see cref="PortRaceDetection"/>) — caught only by <see cref="PythonBackendLauncher.StartAsync"/>'s
/// own bounded retry loop and never allowed to escape to a caller.</summary>
internal sealed class PortBindRaceException(string message) : Exception(message);

/// <summary>Starts the Python backend (app/backend, via .venv) on a free port, pointed at the fakes.</summary>
public static class PythonBackendLauncher
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Issue #259: matches aiohttp's own `web.run_app` startup banner (a plain `print()` to
    /// stdout -- not through the logging module at all -- of the form
    /// `======== Running on http://host:port ========`, emitted once per bound site only AFTER
    /// `TCPSite.start()` has actually bound it) so the harness can read back the REAL bound port
    /// when the backend is launched with `PORT=0` (see <see cref="BackendEnvironment"/>) instead
    /// of assuming whatever port it originally requested. aiohttp's `TCPSite.port` property
    /// resolves to the real OS-assigned port once bound, even when 0 was requested -- so this
    /// banner always names the actual listening port, never the literal "0" that was asked for.
    /// </summary>
    private static readonly Regex RunningOnPortPattern = new(
        @"Running on https?://[^\s:]+:(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Bounded so a genuinely unbindable environment (e.g. loopback sockets exhausted) fails
    /// loudly instead of retrying forever (PR #22 review item 17). One real port race is already
    /// an unlikely coincidence on a CI runner or dev box; three in a row means something else is
    /// wrong and the real error should surface.
    /// </summary>
    private const int MaxStartAttempts = 3;

    /// <summary>
    /// Refs #259 (Rick's #267 review, required item (b)): injectable test-only seam -- a hook
    /// into the exact <see cref="ProcessStartInfo"/> (including its <c>Environment["PORT"]</c>)
    /// this launcher is about to hand to <see cref="Process.Start"/> for a given attempt,
    /// invoked once per attempt (first attempt and every retry). Lets a test assert directly on
    /// what was actually launched -- e.g. that a forced-collision first attempt used the
    /// caller-requested port while every subsequent retry always requested port 0, never a fresh
    /// <see cref="NetworkUtils.GetFreeTcpPort"/> reservation (reintroducing the exact TOCTOU race
    /// port 0 exists to remove) -- instead of only proving the retry loop recovers *some* way
    /// (already covered end-to-end by <see cref="DotnetBackendLauncherPortRaceTests"/>'s
    /// counterpart). Backed by <see cref="AsyncLocal{T}"/> (not a plain static field) so a test
    /// that sets this is only ever observed by launches reachable from that same test's own async
    /// call chain -- many other fixtures across many xunit collections launch the Python backend
    /// concurrently, and a plain static field would let one test's observer silently capture (or
    /// be captured by) another, unrelated fixture's launch running on a different thread at the
    /// same time. Never set outside tests; a no-op in production (defaults to null).
    /// </summary>
    internal static Action<ProcessStartInfo>? TestOnlyProcessStartInfoObserver
    {
        get => TestOnlyProcessStartInfoObserverLocal.Value;
        set => TestOnlyProcessStartInfoObserverLocal.Value = value;
    }

    private static readonly AsyncLocal<Action<ProcessStartInfo>?> TestOnlyProcessStartInfoObserverLocal = new();

    public static async Task<IBackendUnderTest> StartAsync(
        BackendContract contract, PythonBackendOptions options, CancellationToken cancellationToken = default)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var backendDir = RepoPaths.BackendDirectory(repoRoot);
        var pythonExe = RepoPaths.PythonExecutable(repoRoot);
        if (!File.Exists(pythonExe))
        {
            throw new FileNotFoundException(
                $"Python venv interpreter not found at '{pythonExe}'. Expected a venv at " +
                $"{Path.Combine(repoRoot, ".venv")} (see the repo README for setup).", pythonExe);
        }

        var staticIndexHtml = RepoPaths.FrontendStaticIndexHtmlPath(repoRoot);
        if (!File.Exists(staticIndexHtml))
        {
            throw new InvalidOperationException(
                $"'{staticIndexHtml}' does not exist. app/backend/static is gitignored and only " +
                "populated by building the frontend (vite's outDir points there) -- run " +
                "`npm ci && VITE_AUTH_MODE=Development npm run build` in app/frontend before " +
                "running this suite (the auth-mode build guard fails an unset mode with no Entra " +
                "ids configured, which this suite's frontend build always is). Without it, " +
                "the Python backend's aiohttp app.router.add_static(...) raises at startup and the " +
                "process exits immediately, which otherwise surfaces here only as an opaque " +
                "\"backend exited early\" failure.");
        }

        var attemptContract = contract;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartAttemptAsync(attemptContract, options, backendDir, pythonExe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PortBindRaceException) when (attempt < MaxStartAttempts)
            {
                // Issue #259: used to reassign via NetworkUtils.GetFreeTcpPort(), which has the
                // exact same inherent TOCTOU race (probe-then-release) that this bind failure is
                // itself evidence of. Port 0 asks the OS to atomically assign a genuinely free
                // ephemeral port at bind time -- no probe-then-release window at all -- and the
                // real bound port is read back afterwards from aiohttp's own "Running on" startup
                // banner (see RunningOnPortPattern/WaitForListeningAndHealthyAsync). A forced
                // collision on a specific, already-occupied port still exercises this exact retry
                // path: only the *first* attempt uses whatever port the caller explicitly
                // requested, every retry always falls back to 0.
                attemptContract = attemptContract with { Port = 0 };
            }
        }
    }

    private static async Task<IBackendUnderTest> StartAttemptAsync(
        BackendContract contract, PythonBackendOptions options, string backendDir, string pythonExe,
        CancellationToken cancellationToken)
    {
        var env = BackendEnvironment.Build(contract, options);
        var startInfo = new ProcessStartInfo(pythonExe, "app.py")
        {
            WorkingDirectory = backendDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // startInfo.Environment starts out as a *copy of this test process's own environment*
        // (not a blank slate) -- so anything ambient in the dev machine's or CI runner's shell
        // (leftover CONFORMANCE_* from a prior manual run, AZURE_* from an unrelated az-cli
        // session, corporate *_PROXY vars used by the NuGet/npm/pip proxy) would otherwise leak
        // straight into the Python child process unmodified, on top of whatever we explicitly
        // set below. Strip those categories first so every var the backend sees in these
        // categories either comes from `env` (explicit, known-good) or wasn't set at all.
        InheritedEnvironmentFilter.Apply(startInfo);

        foreach (var (key, value) in env)
        {
            startInfo.Environment[key] = value;
        }

        // Refs #259 (Rick's #267 review, required item (b)): injectable test-only seam so
        // PythonBackendLauncherPortRaceTests can assert that every attempt's actual
        // ProcessStartInfo.Environment["PORT"] -- the first attempt (whatever the caller
        // requested) and, critically, every retry (which must always be "0", never a fresh
        // NetworkUtils.GetFreeTcpPort() reservation that would reintroduce the TOCTOU gap port 0
        // exists to remove) -- is what actually gets launched, not merely what StartAsync's own
        // retry loop computed in isolation. Never set outside tests; a no-op in production.
        TestOnlyProcessStartInfoObserver?.Invoke(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Python backend process '{pythonExe} app.py'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Defence-in-depth against orphaned python.exe processes (PR #22 review item 17): if this
        // .NET test process itself is killed forcibly (Stop-Process, a crash, a CI runner reaping
        // an orphaned job) with no chance to run IAsyncDisposable/ProcessExit cleanup, Windows
        // closes every handle the killed process owned -- including this job handle -- which
        // (because of KILL_ON_JOB_CLOSE) makes the OS itself kill the Python process tree. No-op
        // on non-Windows (see WindowsJobObject's own docs for why).
        var jobObject = WindowsJobObject.TryCreateAndAssign(process.Id);

        // Belt-and-suspenders for the *graceful* exit paths that skip normal disposal (e.g. an
        // unhandled exception unwinding past IAsyncDisposable, or a hard Environment.Exit call
        // elsewhere in the process) -- runs on every platform, unlike the job object.
        EventHandler? processExitHandler = null;
        processExitHandler = (_, _) => TryKill(process);
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        Uri baseUri;

        try
        {
            baseUri = await WaitForListeningAndHealthyAsync(process, output, startedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            TryKill(process);
            jobObject?.Dispose();
            throw;
        }

        return new ProcessBackend(process, baseUri, output, jobObject, processExitHandler);
    }

    /// <summary>
    /// Issue #259: previously the caller computed <c>baseUri</c> upfront from
    /// <see cref="BackendContract.Port"/> before the process even started -- only possible because
    /// that port had already been reserved (racily) via <see cref="NetworkUtils.GetFreeTcpPort"/>.
    /// Now that the backend is launched with <c>PORT=0</c> (the OS assigns a genuinely free
    /// ephemeral port atomically at bind time), the real listening address is only known once
    /// aiohttp has actually bound it and printed its "Running on" banner (see
    /// <see cref="RunningOnPortPattern"/>) -- so this method discovers that address AND waits for
    /// `/health` to come up, under one shared <see cref="HealthTimeout"/> deadline, returning the
    /// discovered <see cref="Uri"/> once both are satisfied.
    /// </summary>
    private static async Task<Uri> WaitForListeningAndHealthyAsync(
        Process process, CapturedProcessOutput output, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        Uri? baseUri = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                // Refs #259 (Rick's #267 review, required item (a)): drain, then dump, then
                // classify, via the one shared helper both launchers call -- see
                // ExitClassification's own doc comment for why the drain must happen before the
                // dump is read, and why it's now bounded by this method's own `deadline` instead
                // of an unbounded WaitForExit().
                throw await ExitClassification.DrainAndClassifyAsync(
                    process, output, startedAt, deadline, "Python").ConfigureAwait(false);
            }

            if (baseUri is null)
            {
                var match = RunningOnPortPattern.Match(output.Dump());
                if (match.Success)
                {
                    var boundPort = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    baseUri = new Uri($"http://{BackendContract.Host}:{boundPort}/");
                }
            }

            if (baseUri is not null)
            {
                try
                {
                    using var response = await http.GetAsync(new Uri(baseUri, "/health"), cancellationToken)
                        .ConfigureAwait(false);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        return baseUri;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet — keep polling until the deadline.
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Per-request timeout, not overall cancellation — keep polling.
                }
            }

            await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Python backend did not report listening/healthy within {HealthTimeout.TotalSeconds:F0}s " +
            $"(bound port {(baseUri is null ? "never discovered" : baseUri.Port.ToString(CultureInfo.InvariantCulture))}).\n" +
            $"--- backend stdout/stderr ---\n{output.Dump()}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine.
        }
    }
}

internal sealed class ProcessBackend(
    Process process, Uri baseUri, CapturedProcessOutput output, WindowsJobObject? jobObject,
    EventHandler? processExitHandler) : IBackendUnderTest
{
    public Uri BaseUri { get; } = baseUri;

    public string DumpDiagnostics() => output.Dump();

    public int UnhandledErrorCount() => output.CountUnhandledErrors();

    public int UnhandledErrorCount(Func<IReadOnlyList<string>, bool> isBenignIncident) =>
        output.CountUnhandledErrors(isBenignIncident);

    public Task<bool> WaitForDiagnosticsAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int sinceWatermark = 0) =>
        output.WaitForDiagnosticsAsync(predicate, timeout, cancellationToken, sinceWatermark);

    public int DiagnosticsWatermark => output.Watermark;

    public Task<bool> WaitForOutputQuiescenceAsync(
        TimeSpan idleWindow,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default) =>
        output.WaitForOutputQuiescenceAsync(idleWindow, maxWait, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (processExitHandler is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited — fine.
        }
        finally
        {
            process.Dispose();
            jobObject?.Dispose();
        }
    }
}
