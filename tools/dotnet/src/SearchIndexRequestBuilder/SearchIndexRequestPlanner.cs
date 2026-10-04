using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Orchestrates one persona's plan the same way setup_search_index.py's <c>build_plan</c> +
/// <c>ingest_plan</c> do (lines 392-410, 413-445), but stops at request-building: it never
/// constructs a <c>SearchIndexClient</c>/<c>SearchClient</c> against a real endpoint, so there is no
/// Azure call to make here at all, not even behind a flag.
/// </summary>
public static class SearchIndexRequestPlanner
{
    /// <summary>Fixed, test-visible stand-ins for <c>AZURE_OPENAI_EASTUS2_ENDPOINT</c> and
    /// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> (setup_search_index.py's <c>run()</c>, lines
    /// 472-473) -- these flow directly into the index definition's vectorizer fields, so the
    /// capture harness (Fixtures/capture_search_index_requests.py) must use these EXACT same
    /// literal strings for the two sides' index-definition bodies to match.</summary>
    public const string FakeOpenAiEndpoint = "https://fake.openai.azure.com";

    public const string FakeEmbeddingDeployment = "fake-embedding-deployment";

    public sealed record PersonaRequestPlan(
        string PersonaId,
        string IndexName,
        JsonObject IndexDefinition,
        IReadOnlyList<JsonObject> DocumentBatches,
        int DocumentCount);

    public static PersonaRequestPlan BuildPlan(EnabledPersonaDiscovery.DiscoveredPersona persona)
    {
        var menuData = JsonNode.Parse(File.ReadAllText(persona.MenuPath)) ??
            throw new InvalidOperationException($"'{persona.MenuPath}' did not parse to a JSON value.");
        var prepared = MenuDocumentBuilder.Build(menuData);

        var documents = new List<JsonObject>(prepared.Count);
        foreach (var document in prepared)
        {
            var embeddingArray = new JsonArray();
            foreach (var component in FixtureEmbedding.For(document.CombinedTextForEmbedding))
            {
                embeddingArray.Add(component);
            }
            document.Fields["embedding"] = embeddingArray;
            documents.Add(document.Fields);
        }

        var indexDefinition = SearchIndexDefinitionBuilder.Build(
            persona.IndexName, FakeOpenAiEndpoint, FakeEmbeddingDeployment);
        var documentBatches = DocumentBatchBuilder.BuildBatches(documents);

        return new PersonaRequestPlan(persona.PersonaId, persona.IndexName, indexDefinition, documentBatches, documents.Count);
    }
}
