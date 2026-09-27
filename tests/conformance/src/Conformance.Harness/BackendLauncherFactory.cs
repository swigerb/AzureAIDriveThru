namespace Conformance.Harness;

/// <summary>A pre-existing backend at a fixed URL (CONFORMANCE_BACKEND_URL) — the harness starts nothing and cleans up nothing.</summary>
internal sealed class ExternalBackend(Uri baseUri) : IBackendUnderTest
{
    public Uri BaseUri { get; } = baseUri;
    public string DumpDiagnostics() => "(external backend — no captured output)";
    public int UnhandledErrorCount() => 0;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Chooses which backend implementation the conformance suite talks to, per `CONFORMANCE_BACKEND`
/// (python|dotnet) or an explicit `CONFORMANCE_BACKEND_URL` override.
///
/// S2 (issue #12) update: `dotnet` now starts the real C# skeleton via
/// <see cref="DotnetBackendLauncher"/> instead of always throwing. That backend is a skeleton this
/// wave (host/config/persona-pack loading/health/auth-token/static-files) -- it does not yet
/// implement the realtime relay or order pipeline, so running the *full* suite against it will
/// fail every scenario that needs those. Until a later wave fills those in, only run a scoped
/// subset (health + auth-token scenarios) against CONFORMANCE_BACKEND=dotnet locally -- see
/// docs/dotnet_mapping.md for exactly what is/isn't covered. This factory change does not add
/// "dotnet" to the CI matrix (.github/workflows/conformance.yml) itself -- that axis is owned
/// separately per the wave plan; CI continues to run CONFORMANCE_BACKEND=python only until that
/// axis's owner adds the dotnet leg.
/// <see cref="DotnetPlaceholderPolicy"/> is left in place (with its own tests) for any caller that
/// still wants the old fail/skip-by-default behaviour instead of actually starting the backend.
/// </summary>
public static class BackendLauncherFactory
{
    public static async Task<IBackendUnderTest> StartAsync(
        Uri realtimeBaseUri, Uri searchBaseUri, int port,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? deployment = null,
        CancellationToken cancellationToken = default)
    {
        var explicitUrl = Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return new ExternalBackend(new Uri(explicitUrl));
        }

        var target = (Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND") ?? "python").Trim().ToLowerInvariant();
        var contract = BackendContract.ForPort(realtimeBaseUri, searchBaseUri, port, deployment);
        var env = extraEnvironment ?? new Dictionary<string, string>();

        return target switch
        {
            "python" => await PythonBackendLauncher.StartAsync(
                contract, new PythonBackendOptions { ExtraEnvironment = env }, cancellationToken).ConfigureAwait(false),
            "dotnet" => await DotnetBackendLauncher.StartAsync(
                contract, new DotnetBackendOptions { ExtraEnvironment = env }, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unknown CONFORMANCE_BACKEND '{target}' — expected 'python' or 'dotnet'."),
        };
    }
}
