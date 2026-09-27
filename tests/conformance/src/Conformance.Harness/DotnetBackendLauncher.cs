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

        foreach (var (key, value) in options.ExtraEnvironment)
        {
            env[key] = value;
        }

        return env;
    }
}

/// <summary>
/// Starts the C# backend (app/backend-dotnet/src/Backend) via `dotnet run`, on a free port,
/// pointed at the fakes -- the S2 (issue #12) counterpart to <see cref="PythonBackendLauncher"/>.
///
/// Scope note: this wave's C# backend is a skeleton (host, config, persona-pack loading, health,
/// auth token, static files, one event loop per session) -- it does not yet implement the
/// realtime relay or order pipeline. This launcher itself is backend-agnostic and starts the
/// process/waits for /health exactly like the Python launcher; it is the *scenarios* run against
/// it that are necessarily a subset (health + auth token scenarios only) until later waves add
/// the rest. See docs/dotnet_mapping.md for exactly what is/isn't covered yet.
///
/// Deliberately simpler than <see cref="PythonBackendLauncher"/> in one respect: no port-bind-race
/// retry loop (<see cref="PortRaceDetection"/>) -- that hardening can be ported here if this
/// launcher is ever promoted into the CI matrix and racy port reuse shows up in practice.
/// </summary>
public static class DotnetBackendLauncher
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<IBackendUnderTest> StartAsync(
        BackendContract contract, DotnetBackendOptions options, CancellationToken cancellationToken = default)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "app", "backend-dotnet", "src", "Backend", "Backend.csproj");
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException(
                $"C# backend project not found at '{csprojPath}'. Expected app/backend-dotnet/src/Backend/Backend.csproj.",
                csprojPath);
        }

        var env = DotnetBackendEnvironment.Build(contract, options);
        var startInfo = new ProcessStartInfo("dotnet", $"run --no-launch-profile --project \"{csprojPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(csprojPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start C# backend process 'dotnet run --project {csprojPath}'.");
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
            await WaitForHealthAsync(baseUri, process, output, cancellationToken).ConfigureAwait(false);
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
        Uri baseUri, Process process, CapturedProcessOutput output, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthUri = new Uri(baseUri, "/health");
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"C# backend exited early (code {process.ExitCode}) before becoming healthy.\n" +
                    $"--- backend stdout/stderr ---\n{output.Dump()}");
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
                // Not listening yet (or still building via `dotnet run`) -- keep polling.
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
}
