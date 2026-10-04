using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace Conformance.Harness;

/// <summary>
/// S2 (issue #12)-specific extras layered on top of the neutral <see cref="BackendContract"/> --
/// this backend's own equivalent of <see cref="PythonBackendOptions"/> (see that type's own doc
/// comment, which predicted exactly this: "a future .NET launcher (S2) would have its own
/// equivalent options type instead of reusing this one").
/// </summary>
public sealed class DotnetBackendOptions
{
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Builds the exact environment variable set app/backend-dotnet/src/Backend/Program.cs needs to
/// start against the fakes. Deliberately does NOT reuse <see cref="BackendEnvironment"/>: that
/// type's own doc comment already calls out that its Python-only entries (PYTHONUNBUFFERED,
/// PYTHONUTF8) are launcher-specific, not part of the neutral contract -- this is this backend's
/// own equivalent, covering the same neutral BackendContract group plus nothing CPython-specific.
/// </summary>
internal static class DotnetBackendEnvironment
{
    public static Dictionary<string, string> Build(BackendContract contract, DotnetBackendOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOST"] = BackendContract.Host,
            ["PORT"] = contract.Port.ToString(),

            ["AZURE_OPENAI_EASTUS2_API_KEY"] = BackendContract.OpenAiApiKey,
            ["AZURE_SEARCH_API_KEY"] = BackendContract.SearchApiKey,

            ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = contract.RealtimeBaseUri.ToString().TrimEnd('/'),
            ["AZURE_OPENAI_REALTIME_DEPLOYMENT"] = contract.Deployment,
            ["AZURE_OPENAI_REALTIME_VOICE_CHOICE"] = contract.Voice,
            ["AZURE_SEARCH_ENDPOINT"] = contract.SearchBaseUri.ToString().TrimEnd('/'),
            ["AZURE_SEARCH_INDEX"] = contract.SearchIndex,
            ["AZURE_SEARCH_SEMANTIC_CONFIGURATION"] = BackendContract.SearchSemanticConfiguration,
            ["AZURE_SEARCH_IDENTIFIER_FIELD"] = BackendContract.SearchIdentifierField,
            ["AZURE_SEARCH_CONTENT_FIELD"] = BackendContract.SearchContentField,
            ["AZURE_SEARCH_EMBEDDING_FIELD"] = BackendContract.SearchEmbeddingField,
            ["AZURE_SEARCH_TITLE_FIELD"] = BackendContract.SearchTitleField,
            ["AZURE_SEARCH_USE_VECTOR_QUERY"] = BackendContract.SearchUseVectorQuery ? "true" : "false",
            ["AZURE_SEARCH_SEMANTIC_RANKER"] = BackendContract.SearchSemanticRanker,
            ["STORE_TIMEZONE"] = contract.StoreTimeZone,
            ["RUNNING_IN_PRODUCTION"] = "true",
            ["LOG_LEVEL"] = "INFO",
            // Single-process HMAC secret; random per launch is fine since only this process ever
            // needs to validate tokens it issued itself (same rationale as BackendEnvironment).
            ["APP_SESSION_SECRET"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["RATE_LIMIT_RECOVERY_ENABLED"] = "true",

            // Issue #76: same PERSONAS/DEFAULT_PERSONA contract as BackendEnvironment (Python) --
            // app/backend-dotnet/src/Backend/Personas/PersonaCatalog.cs reads the same env var
            // names with the same fallback semantics.
            ["PERSONAS"] = string.Join(",", contract.Personas),
            ["DEFAULT_PERSONA"] = contract.DefaultPersona,

            // .NET-specific: make sure ASP.NET Core doesn't pick up a stray Development-only
            // behaviour (e.g. developer exception pages) from an inherited ASPNETCORE_ENVIRONMENT.
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
        };

        // Same rationale as BackendEnvironment.cs (Python launcher): only set when a fixture
        // explicitly overrides it (BackendContract.ForPort's personasDir param) -- omitted
        // entirely otherwise, so every existing scenario resolves personas from the real repo
        // personas/ folder exactly as before. Without this, every PersonaConformanceFixtures-
        // derived fixture (test-alpha/test-beta packs) silently falls back to the real repo's
        // sonic-only personas/ tree when run against the dotnet backend, and any row asserting
        // on the fixture packs (disabled-pack, two-persona mismatch, model-selection fixtures)
        // fails for the wrong reason.
        if (contract.PersonasDir is not null)
        {
            env["PERSONAS_DIR"] = contract.PersonasDir;
        }

        foreach (var (key, value) in options.ExtraEnvironment)
        {
            env[key] = value;
        }

        return env;
    }
}

/// <summary>
/// Starts the C# backend (app/backend-dotnet/src/Backend) on a free port, pointed at the fakes --
/// the S2 (issue #12) counterpart to <see cref="PythonBackendLauncher"/>. Builds Backend.csproj at
/// most once per test process (issue #135) and launches the resulting Backend.dll directly
/// (`dotnet "&lt;dll&gt;"`) -- never `dotnet run`, which rebuilds on every call.
///
/// Scope note: this wave's C# backend is a skeleton (host, config, persona-pack loading, health,
/// auth token, static files, one event loop per session) -- it does not yet implement the
/// realtime relay or order pipeline. This launcher itself is backend-agnostic and starts the
/// process/waits for /health exactly like the Python launcher; it is the *scenarios* run against
/// it that are necessarily a subset (health + auth token scenarios only) until later waves add
/// the rest. See docs/dotnet_mapping.md for exactly what is/isn't covered yet.
///
/// Issue #240: used to be deliberately simpler than <see cref="PythonBackendLauncher"/> in one
/// respect -- no port-bind-race retry loop (<see cref="PortRaceDetection"/>) -- on the stated
/// theory that this launcher wasn't yet promoted into the CI matrix. It was already in the CI
/// matrix (.github/workflows/conformance.yml's `backend: [python, dotnet]` axis, since #76), and
/// CI (PR #224 run 37174516348, attempt 1) hit exactly the race that theory dismissed: many
/// Happy-Hour-related fixtures (<c>RealPackHappyHourFixture</c> per persona/instant,
/// <c>HappyHourAtOpenFixture</c>/<c>HappyHourAtCloseTests</c> and siblings, each its own xunit
/// collection) call <see cref="NetworkUtils.GetFreeTcpPort"/> and start a backend concurrently;
/// <see cref="NetworkUtils.GetFreeTcpPort"/>'s own doc comment already names the inherent TOCTOU
/// race between releasing the probe socket and the real bind -- Kestrel lost that race for one
/// persona row (port 46037, <c>AddressInUseException</c>, process exit code 134) while its sibling
/// rows in the same theory (different ports) passed. Now mirrors Python's own
/// bind-and-retry loop exactly (<see cref="MaxStartAttempts"/>, <see cref="PortRaceDetection"/> --
/// already backend-agnostic: its signature list includes the literal text .NET's
/// <c>SocketException</c>/<c>AddressInUseException</c> renders, "Address already in use", so no
/// change was needed there), reusing the same <see cref="PortBindRaceException"/> type Python's
/// launcher throws internally (declared in PythonBackendLauncher.cs, scoped to the shared
/// <c>Conformance.Harness</c> namespace, not duplicated here).
/// </summary>
public static class DotnetBackendLauncher
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Same rationale and same value as <see cref="PythonBackendLauncher"/>'s own
    /// <c>MaxStartAttempts</c> (issue #240): bounded so a genuinely unbindable environment fails
    /// loudly instead of retrying forever. Kept as this class's own constant (not shared) so each
    /// launcher's retry policy can be tuned independently later, matching how
    /// <see cref="HealthTimeout"/>/<see cref="HealthPollInterval"/> are already duplicated per
    /// launcher rather than factored out.
    /// </summary>
    private const int MaxStartAttempts = 3;

    // Issue #135: `dotnet run` rebuilds every time it's invoked. Several fixtures in parallel
    // xunit collections used to each call StartAsync (and therefore `dotnet run`) at the same
    // time, so concurrent MSBuild invocations raced on the same app/backend-dotnet/**/obj outputs
    // -- MSB4018 GenerateDepsFile file-lock flake. CI's own dotnet leg already runs
    // `dotnet build Backend.slnx --no-restore` before the suite starts (.github/workflows/
    // conformance.yml), so the rebuild here was pure waste even outside the race. Fixed
    // structurally, not with retries: this gate makes sure EXACTLY ONE build runs per test
    // process, however many fixtures call StartAsync (serially or in parallel), and every launch
    // afterwards starts the already-built Backend.dll directly (`dotnet "<dll>"`, never
    // `run`/`build`/`restore` -- see BuildStartInfo/DotnetBackendLauncherStartInfoTests).
    //
    // R1 fix (Rick's #151 review, round 1): the rule is "build exactly once per test process",
    // not "build if the dll is missing". A dll on disk proves nothing about whether it reflects
    // the source that's about to run against it -- neither Conformance.Harness.csproj nor
    // Conformance.Tests.csproj has a ProjectReference to Backend.csproj, so a plain `dotnet test`
    // never rebuilds it, and a stale dll from an earlier run would otherwise be launched silently.
    //
    // R1 fix (Rick's #151 review, round 2): the gate is now an INSTANCE, <see
    // cref="DotnetBackendBuildGate"/> -- see that type's own doc comment for why. This field is
    // the one and only shared instance real fixtures use; unit tests construct their own private
    // instances instead of touching anything here, so nothing a test does can ever be observed by
    // a real fixture calling StartAsync concurrently in another xunit collection.
    private static readonly DotnetBackendBuildGate Builder = new(RunBuildAsync);

    public static async Task<IBackendUnderTest> StartAsync(
        BackendContract contract, DotnetBackendOptions options, CancellationToken cancellationToken = default)
    {
        var attemptContract = contract;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartAttemptAsync(attemptContract, options, cancellationToken).ConfigureAwait(false);
            }
            catch (PortBindRaceException) when (attempt < MaxStartAttempts)
            {
                // Same rationale as PythonBackendLauncher: NetworkUtils.GetFreeTcpPort() has an
                // inherent TOCTOU race between releasing the probe socket and this backend's own
                // Kestrel bind -- pick a fresh port and try again.
                attemptContract = attemptContract with { Port = NetworkUtils.GetFreeTcpPort() };
            }
        }
    }

    private static async Task<IBackendUnderTest> StartAttemptAsync(
        BackendContract contract, DotnetBackendOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "app", "backend-dotnet", "src", "Backend", "Backend.csproj");
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException(
                $"C# backend project not found at '{csprojPath}'. Expected app/backend-dotnet/src/Backend/Backend.csproj.",
                csprojPath);
        }

        var dllPath = await Builder.EnsureBuiltAsync(csprojPath, cancellationToken).ConfigureAwait(false);

        var env = DotnetBackendEnvironment.Build(contract, options);
        var startInfo = BuildStartInfo(dllPath);

        // Same rationale as PythonBackendLauncher: strip ambient CONFORMANCE_*/AZURE_*/*_PROXY
        // vars from this test process's own environment before layering the explicit, known-good
        // set below, so nothing leftover from a prior manual run leaks into the child process.
        InheritedEnvironmentFilter.Apply(startInfo);
        foreach (var (key, value) in env)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new CapturedProcessOutput();
        output.Attach(process);

        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start C# backend process 'dotnet \"{dllPath}\"'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Same defence-in-depth against orphaned dotnet.exe processes as PythonBackendLauncher --
        // see WindowsJobObject's own doc comment for why this is Windows-only and why the
        // ProcessExit handler below covers every platform as a second line of defence.
        var jobObject = WindowsJobObject.TryCreateAndAssign(process.Id);

        EventHandler? processExitHandler = null;
        processExitHandler = (_, _) => TryKill(process);
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        var baseUri = new Uri($"http://{BackendContract.Host}:{contract.Port}/");

        try
        {
            await WaitForHealthAsync(baseUri, process, output, startedAt, cancellationToken).ConfigureAwait(false);
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

    private static async Task WaitForHealthAsync(
        Uri baseUri, Process process, CapturedProcessOutput output, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthUri = new Uri(baseUri, "/health");
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                var dump = output.Dump();

                // Issue #240: same heuristic/rationale as PythonBackendLauncher -- an early exit
                // whose captured output matches a known TCP bind-failure signature is a port race
                // between NetworkUtils.GetFreeTcpPort() and this backend's own Kestrel bind, not a
                // real backend crash; only that case is worth retrying with a fresh port.
                if (PortRaceDetection.ShouldRetry(elapsed, dump))
                {
                    throw new PortBindRaceException(
                        $"C# backend exited immediately (code {process.ExitCode}), " +
                        $"{elapsed.TotalSeconds:F1}s after starting, with output matching a TCP " +
                        $"port-bind failure signature -- treating as a port race between " +
                        $"NetworkUtils.GetFreeTcpPort() and the backend's own Kestrel bind.\n" +
                        $"--- backend stdout/stderr ---\n{dump}");
                }

                throw new InvalidOperationException(
                    $"C# backend exited early (code {process.ExitCode}) before becoming healthy.\n" +
                    $"--- backend stdout/stderr ---\n{dump}");
            }

            try
            {
                using var response = await http.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet -- keep polling.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-request timeout, not overall cancellation -- keep polling.
            }

            await Task.Delay(HealthPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"C# backend did not report healthy at {healthUri} within {HealthTimeout.TotalSeconds:F0}s.\n" +
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
            // Already exited between the check and the kill -- fine.
        }
    }

    /// <summary>
    /// Pure, testable construction of the launch <see cref="ProcessStartInfo"/> -- `dotnet
    /// "&lt;dll&gt;"` directly against an already-built output, never `dotnet run`/`build`/
    /// `restore` (issue #135). Kept separate from StartAsync so a unit test can assert on the
    /// exact command/arguments without starting a real process or needing the SDK installed.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string dllPath) =>
        new("dotnet", $"\"{dllPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dllPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    /// <summary>
    /// Resolves Backend.csproj's build output path without invoking MSBuild -- the default SDK
    /// layout (no custom OutputPath/BaseOutputPath in Backend.csproj or its Directory.Build.props)
    /// is bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;/Backend.dll relative to the project
    /// directory. Matches the Configuration/TargetFramework CI's own prebuild step
    /// (.github/workflows/conformance.yml's "dotnet build Backend.slnx --no-restore", no `-c`) and
    /// Backend.csproj's own `net11.0` TFM (app/backend-dotnet/Directory.Build.props) both resolve
    /// to.
    /// </summary>
    internal static string ResolveDllPath(string csprojPath) =>
        Path.Combine(Path.GetDirectoryName(csprojPath)!, "bin", BuildConfiguration, TargetFramework, "Backend.dll");

    private const string BuildConfiguration = "Debug";
    private const string TargetFramework = "net11.0";

    /// <summary>
    /// Runs the real MSBuild build (issue #135). Always called by <see cref="Builder"/>'s
    /// <see cref="DotnetBackendBuildGate.EnsureBuiltAsync"/> with <see cref="CancellationToken.None"/>
    /// (R2 of Rick's #151 round-2 review) -- the <paramref name="cancellationToken"/> parameter
    /// exists only to satisfy the <c>Func&lt;string, CancellationToken, Task&gt;</c> seam shape the
    /// gate expects (and so tests substituting a fake here can still accept a token), not because
    /// this build is ever meant to be cancelled by an individual caller. A single caller's
    /// cancellation must stop THAT caller's wait, never the shared build every other caller is
    /// also waiting on -- see <see cref="DotnetBackendBuildGate"/>'s own doc comment.
    /// </summary>
    private static async Task RunBuildAsync(string csprojPath, CancellationToken cancellationToken)
    {
        // R2 fix (Rick's #151 review, round 1): no `--no-restore`. A developer whose only
        // interaction with this repo is `dotnet test tests/conformance` has never restored the
        // Backend project -- `dotnet run` used to restore implicitly, and this launcher replaced
        // `dotnet run` (issue #135) without keeping that behaviour. Restore is a no-op when the
        // assets are already current, so this doesn't cost CI anything (its prebuild step already
        // restored/built).
        var buildInfo = new ProcessStartInfo("dotnet", $"build \"{csprojPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        InheritedEnvironmentFilter.Apply(buildInfo);

        using var buildProcess = new Process { StartInfo = buildInfo };
        var buildOutput = new CapturedProcessOutput();
        buildOutput.Attach(buildProcess);

        if (!buildProcess.Start())
        {
            throw new InvalidOperationException($"Failed to start 'dotnet build \"{csprojPath}\"'.");
        }
        buildProcess.BeginOutputReadLine();
        buildProcess.BeginErrorReadLine();
        await buildProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (buildProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet build \"{csprojPath}\"' failed with exit code {buildProcess.ExitCode}.\n" +
                $"--- build stdout/stderr ---\n{buildOutput.Dump()}");
        }
    }
}
