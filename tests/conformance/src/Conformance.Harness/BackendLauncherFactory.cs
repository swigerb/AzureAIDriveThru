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
/// subset (health + auth-token scenarios) against CONFORMANCE_BACKEND=dotnet -- see
/// docs/dotnet_mapping.md for exactly what is/isn't covered. #76 added this scoped subset to the
/// CI matrix's own `backend: [python, dotnet]` axis (.github/workflows/conformance.yml).
/// <see cref="DotnetPlaceholderPolicy"/> is left in place (with its own tests) for any caller that
/// still wants the old fail/skip-by-default behaviour instead of actually starting the backend.
///
/// #76 groundwork also added the persona axis here: `personas`/`persona` resolve exactly like the
/// backend's own PERSONAS/DEFAULT_PERSONA fallback (see <see cref="ConformancePersonas"/>), so a
/// caller that never touches these two parameters gets exactly today's implicit "sonic" behaviour
/// with no change required -- only personas/sonic exists on disk, so <see
/// cref="ConformancePersonas.DiscoverFromDisk()"/> always resolves to <c>["sonic"]</c> today.
/// </summary>
public static class BackendLauncherFactory
{
    public static async Task<IBackendUnderTest> StartAsync(
        Uri realtimeBaseUri, Uri searchBaseUri, int port,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? deployment = null,
        IReadOnlyList<string>? personas = null,
        string? persona = null,
        CancellationToken cancellationToken = default)
    {
        var explicitUrl = Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return new ExternalBackend(new Uri(explicitUrl));
        }

        var target = (Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND") ?? "python").Trim().ToLowerInvariant();

        // Issue #76: resolve the persona axis exactly like the backend itself would (env var,
        // else disk discovery, else "sonic") -- see ConformancePersonas' own doc comment. A
        // fixture's Persona override is folded into the enabled set so it always actually takes
        // effect as DEFAULT_PERSONA, even if it isn't already covered by CONFORMANCE_PERSONAS or
        // disk discovery (ConformancePersonas.EnsureIncluded's own doc comment).
        var resolvedPersonas = personas ?? ConformancePersonas.ResolveEnabled(
            Environment.GetEnvironmentVariable("CONFORMANCE_PERSONAS"), ConformancePersonas.DiscoverFromDisk);
        resolvedPersonas = ConformancePersonas.EnsureIncluded(resolvedPersonas, persona);
        var resolvedDefaultPersona = ConformancePersonas.ResolveDefault(persona, resolvedPersonas);

        var contract = BackendContract.ForPort(
            realtimeBaseUri, searchBaseUri, port, deployment, resolvedPersonas, resolvedDefaultPersona);
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
