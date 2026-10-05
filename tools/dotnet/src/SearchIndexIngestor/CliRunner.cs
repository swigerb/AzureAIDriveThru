using SearchIndexRequestBuilder;

namespace SearchIndexIngestor;

/// <summary>
/// The CLI's argument parsing, orchestration ordering, and top-level error handling -- faithful
/// port of setup_search_index.py's <c>_build_arg_parser</c> (lines 500-532) and
/// <c>main</c>/<c>run</c> (lines 447-554), factored out of Program.cs the same way
/// SearchIndexRequestBuilder/CliRunner.cs is, so it can be unit-tested directly against
/// <see cref="TextWriter"/>s instead of spawning a real process.
///
/// Flags <c>--persona</c>/<c>--dry-run</c>/<c>--personas-dir</c> match Python's three exactly
/// (name, repeatable-and-comma-separated semantics for <c>--persona</c>, same help text content).
/// <c>--search-endpoint</c>/<c>--openai-endpoint</c>/<c>--embedding-deployment</c> are this port's
/// own additions (Python has none for any of the three -- every one of those always comes from the
/// azd-managed environment or a process environment variable there), reusing
/// <see cref="OpenAiSettingsResolver"/>/<see cref="SearchEndpointResolver"/>'s own
/// CLI-flag-beats-azd-beats-environment-variable precedence, the same convention
/// SearchIndexRequestBuilder/CliRunner.cs already established for the two OpenAI ones.
/// </summary>
public static class CliRunner
{
    public const string HelpText =
        "Usage: dotnet run --project tools/dotnet/src/SearchIndexIngestor -- " +
        "[--persona ID[,ID...]] [--dry-run] [--personas-dir PATH] [--search-endpoint <url>] " +
        "[--openai-endpoint <url>] [--embedding-deployment <name>]\n" +
        "  --persona ID[,ID...]   Restrict ingestion to these persona id(s) -- repeatable and/or\n" +
        "                         comma-separated. Default: every enabled persona.\n" +
        "  --dry-run              Print the planned index name and document count for each\n" +
        "                         targeted persona and exit -- no Azure Search/OpenAI calls, no\n" +
        "                         credential required, no azd env loaded.\n" +
        "  --personas-dir PATH    Override the personas directory (mainly for local testing\n" +
        "                         against a fixture pack directory).\n" +
        "  --search-endpoint URL  Azure AI Search endpoint, if not set via azd/environment.\n" +
        "  --openai-endpoint URL  Azure OpenAI endpoint, if not set via azd/environment.\n" +
        "  --embedding-deployment NAME\n" +
        "                         Azure OpenAI embedding deployment name, if not set via\n" +
        "                         azd/environment.";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string? repoRootOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, IReadOnlyDictionary<string, string>>? loadAzdEnvValues = null,
        Func<string, Azure.Core.TokenCredential, SearchIndexHttpClient>? createSearchClient = null,
        Func<string, Azure.Core.TokenCredential, IEmbeddingClient>? createEmbeddingClient = null,
        Func<Azure.Core.TokenCredential>? createCredential = null)
    {
        List<string> personaFlags = [];
        var dryRun = false;
        string? personasDir = null;
        string? searchEndpointFlag = null;
        string? openAiEndpointFlag = null;
        string? embeddingDeploymentFlag = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--persona" when i + 1 < args.Length:
                    personaFlags.Add(args[++i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--personas-dir" when i + 1 < args.Length:
                    personasDir = args[++i];
                    break;
                case "--search-endpoint" when i + 1 < args.Length:
                    searchEndpointFlag = args[++i];
                    break;
                case "--openai-endpoint" when i + 1 < args.Length:
                    openAiEndpointFlag = args[++i];
                    break;
                case "--embedding-deployment" when i + 1 < args.Length:
                    embeddingDeploymentFlag = args[++i];
                    break;
                case "--help" or "-h":
                    stdout.WriteLine(HelpText);
                    return 0;
            }
        }

        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        try
        {
            var repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
            var loadAzd = loadAzdEnvValues ?? (root => AzdEnvLoader.LoadDefaultEnvValues(root));

            // setup_search_index.py's main() loads azd env / checks the skip flag ONLY outside
            // --dry-run (lines 538-547) -- dry-run's own help text promises "no azd env loaded".
            IReadOnlyDictionary<string, string> azdEnvValues = new Dictionary<string, string>();
            if (!dryRun)
            {
                azdEnvValues = loadAzd(repoRoot);
                if (SearchEndpointResolver.IsSkipIndexSetupEnabled(getEnv, azdEnvValues))
                {
                    stdout.WriteLine(
                        $"{SearchEndpointResolver.SkipIndexSetupVariableName} is set " +
                        "-- leaving every persona's index untouched.");
                    return 0;
                }
            }

            // Issue #16 round 2 (Rick's review, 2a): PERSONAS/PERSONAS_DIR must be honoured exactly
            // like persona_loader.PersonaCatalog.load() does -- reading the process env AND the azd
            // env (azdEnvValues is already {} above for --dry-run, matching Python's own
            // load_azd_env()-only-outside-dry-run behaviour) with the same override=True precedence
            // every other azd-aware setting here already uses. Otherwise a real run can create and
            // populate indexes for personas an environment's own PERSONAS setting never enabled.
            var resolvedPersonasDir = PersonaCatalogEnvResolver.ResolvePersonasDir(personasDir, getEnv, azdEnvValues);
            var enabledIds = PersonaCatalogEnvResolver.ResolveEnabledIds(getEnv, azdEnvValues);
            var catalog = EnabledPersonaDiscovery.DiscoverAll(repoRoot, resolvedPersonasDir, enabledIds);
            var targeted = PersonaTargeting.ResolveTargetPersonas(catalog, personaFlags);
            var plans = targeted.Select(PersonaIngestPlanBuilder.Build).ToList();

            if (dryRun)
            {
                foreach (var plan in plans)
                {
                    stdout.WriteLine(
                        $"[dry-run] persona '{plan.PersonaId}': index '{plan.IndexName}', " +
                        $"{plan.DocumentCount} document(s) planned");
                }
                return 0;
            }

            var searchEndpoint = SearchEndpointResolver.Resolve(searchEndpointFlag, getEnv, azdEnvValues);
            var settings = OpenAiSettingsResolver.Resolve(openAiEndpointFlag, embeddingDeploymentFlag, getEnv, azdEnvValues);

            // DefaultAzureCredential only -- no Azure OpenAI or Search key is ever read, issued, or
            // stored, matching setup_search_index.py's own run() comment (lines 475-477) verbatim.
            var credential = (createCredential ?? (() => new Azure.Identity.DefaultAzureCredential()))();
            using var searchClient = (createSearchClient ?? ((endpoint, cred) => SearchIndexHttpClient.CreateForProduction(endpoint, cred)))(searchEndpoint, credential);
            var embeddingClient = (createEmbeddingClient ?? ((endpoint, cred) => AzureOpenAiEmbeddingClient.CreateForProduction(endpoint, cred)))(settings.OpenAiEndpoint, credential);
            try
            {
                var orchestrator = new SearchIndexOrchestrator(searchClient, embeddingClient, settings.OpenAiEndpoint, settings.EmbeddingDeployment, stdout);
                foreach (var plan in plans)
                {
                    await orchestrator.IngestAsync(plan, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                (embeddingClient as IDisposable)?.Dispose();
            }

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            // A clean, single-line, actionable message -- not an unhandled-exception stack trace --
            // for every expected, resolvable configuration/validation failure (unknown --persona
            // id, missing menu file, no endpoint configured, ...). Mirrors Python's own SystemExit
            // paths (lines 386-388, 400-401) -- NOT verify_document_count's RuntimeError (lines
            // 346-348), which this port also raises as a plain, UN-caught Exception (see
            // SearchIndexOrchestrator's remarks) so that failure surfaces distinctly, matching
            // Python's own distinct unhandled-exception behaviour there.
            stderr.WriteLine($"search-index-ingestor: {ex.Message}");
            return 1;
        }
    }
}
