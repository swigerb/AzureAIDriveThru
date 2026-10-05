using System.Text.Json.Nodes;
using SearchIndexRequestBuilder;

namespace SearchIndexIngestor;

/// <summary>
/// Faithful port of setup_search_index.py's <c>PersonaIngestPlan</c> dataclass (lines 351-362) plus
/// <c>build_plan</c> (lines 392-410): what would be (or, outside <c>--dry-run</c>, is being)
/// ingested for one persona. <see cref="Documents"/> are mutated in place to attach each one's
/// "embedding" field once <see cref="IEmbeddingClient"/> returns real values -- mirroring
/// <c>ingest_plan</c>'s own <c>doc["embedding"] = emb</c> loop (line 435-436), not a new list.
/// </summary>
public sealed record PersonaIngestPlan(
    string PersonaId,
    string IndexName,
    IReadOnlyList<JsonObject> Documents,
    IReadOnlyList<string> TextsForEmbedding)
{
    public int DocumentCount => Documents.Count;
}

/// <summary>Builds a <see cref="PersonaIngestPlan"/> for one persona the same way
/// setup_search_index.py's <c>build_plan</c> does: read-only, never touches Azure, safe to call for
/// every target persona before deciding whether this is a dry run.</summary>
public static class PersonaIngestPlanBuilder
{
    public static PersonaIngestPlan Build(EnabledPersonaDiscovery.DiscoveredPersona persona)
    {
        if (!File.Exists(persona.MenuPath))
        {
            // Python's build_plan logs CRITICAL then calls sys.exit(1) -- a true SystemExit, not an
            // exception, but this port's established convention (OpenAiSettingsResolver,
            // SearchIndexRequestBuilder/CliRunner) is a clean, catchable InvalidOperationException
            // that the top-level CliRunner turns into a one-line stderr message + exit 1, never an
            // unhandled-exception stack trace.
            throw new InvalidOperationException(
                $"Persona '{persona.PersonaId}': menu data file not found at {persona.MenuPath}");
        }

        var menuData = JsonNode.Parse(File.ReadAllText(persona.MenuPath)) ??
            throw new InvalidOperationException($"'{persona.MenuPath}' did not parse to a JSON value.");
        var prepared = MenuDocumentBuilder.Build(menuData);

        var documents = prepared.Select(p => p.Fields).ToList();
        var texts = prepared.Select(p => p.CombinedTextForEmbedding).ToList();
        return new PersonaIngestPlan(persona.PersonaId, persona.IndexName, documents, texts);
    }
}
