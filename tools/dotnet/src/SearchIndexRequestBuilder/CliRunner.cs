using System.Text.Json;

namespace SearchIndexRequestBuilder;

/// <summary>
/// The CLI's argument parsing and top-level error handling, factored out of Program.cs's entry
/// point so it can be unit-tested directly against <see cref="TextWriter"/>s instead of spawning a
/// real process -- same shape as ExtractProductionItems/CliRunner.cs and
/// UpdateMenuSizes/CliRunner.cs.
///
/// Unlike those two tools, this one takes no persona-selection arguments: it always targets every
/// enabled persona (<see cref="EnabledPersonaDiscovery.DiscoverAll"/>), matching
/// setup_search_index.py's own default (no <c>--persona</c>) behaviour, and it never makes a
/// network call under any flag -- this is a request-building report, not an ingestion tool. It DOES
/// take <c>--openai-endpoint</c>/<c>--embedding-deployment</c> (or the equivalent
/// AZURE_OPENAI_EASTUS2_ENDPOINT/AZURE_OPENAI_EMBEDDING_DEPLOYMENT environment variables) so the
/// printed index definition's vectorizer fields reflect a real environment, not a fixed placeholder
/// (PR #250 review R4) -- see <see cref="OpenAiSettingsResolver"/>.
/// </summary>
public static class CliRunner
{
    public const string HelpText =
        "Usage: dotnet run --project tools/dotnet/src/SearchIndexRequestBuilder -- " +
        "[--openai-endpoint <url>] [--embedding-deployment <name>]";

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string? repoRootOverride = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        string? openAiEndpointFlag = null;
        string? embeddingDeploymentFlag = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

        OpenAiSettingsResolver.Settings settings;
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> personas;
        try
        {
            settings = OpenAiSettingsResolver.Resolve(openAiEndpointFlag, embeddingDeploymentFlag, getEnvironmentVariable);
            var repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
            personas = EnabledPersonaDiscovery.DiscoverAll(repoRoot);
        }
        catch (InvalidOperationException ex)
        {
            // A clean, single-line, actionable message -- not an unhandled-exception stack trace --
            // since both "no Azure OpenAI endpoint configured" and "no personas enabled" are
            // expected, resolvable configuration states.
            stderr.WriteLine($"search-index-request-builder: {ex.Message}");
            return 1;
        }

        // This tool never calls Azure OpenAI's real, non-deterministic embedding endpoint under
        // any flag -- so unlike setup_search_index.py, no document body it could print would ever
        // have a real "embedding" value. Said once, up front, rather than repeated per persona.
        stdout.WriteLine(
            "search-index-request-builder: request bodies only -- no Azure OpenAI/Search call is " +
            "ever made, so no document's \"embedding\" field is computed (see docs/dotnet_tooling.md).");

        var writerOptions = new JsonWriterOptions { Indented = true };
        foreach (var persona in personas)
        {
            var plan = SearchIndexRequestPlanner.BuildPlan(persona, settings.OpenAiEndpoint, settings.EmbeddingDeployment);
            stdout.WriteLine(
                $"[plan] persona '{plan.PersonaId}': index '{plan.IndexName}', " +
                $"{plan.DocumentCount} document(s) across {plan.DocumentBatches.Count} upload batch(es) -- " +
                "request bodies only, no Azure call made.");

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, writerOptions))
            {
                plan.IndexDefinition.WriteTo(writer);
            }
            stdout.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return 0;
    }
}
