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
/// network call under any flag -- this is a request-building report, not an ingestion tool.
/// </summary>
public static class CliRunner
{
    public const string HelpText = "Usage: dotnet run --project tools/dotnet/src/SearchIndexRequestBuilder";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? repoRootOverride = null)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            stdout.WriteLine(HelpText);
            return 0;
        }

        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> personas;
        try
        {
            var repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
            personas = EnabledPersonaDiscovery.DiscoverAll(repoRoot);
        }
        catch (InvalidOperationException ex)
        {
            // A clean, single-line, actionable message -- not an unhandled-exception stack trace --
            // since "no personas enabled" is an expected, resolvable configuration state.
            stderr.WriteLine($"search-index-request-builder: {ex.Message}");
            return 1;
        }

        var writerOptions = new JsonWriterOptions { Indented = true };
        foreach (var persona in personas)
        {
            var plan = SearchIndexRequestPlanner.BuildPlan(persona);
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
