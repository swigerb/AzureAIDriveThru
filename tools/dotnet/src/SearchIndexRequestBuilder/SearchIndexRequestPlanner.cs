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
    public sealed record PersonaRequestPlan(
        string PersonaId,
        string IndexName,
        JsonObject IndexDefinition,
        IReadOnlyList<JsonObject> DocumentBatches,
        int DocumentCount);

    /// <param name="persona">The persona to plan for (see <see cref="EnabledPersonaDiscovery"/>).</param>
    /// <param name="openAiEndpoint">Flows straight into the index definition's vectorizer
    /// "resourceUri" field -- <see cref="CliRunner"/> resolves this from the real
    /// AZURE_OPENAI_EASTUS2_ENDPOINT environment variable/--openai-endpoint flag
    /// (<see cref="OpenAiSettingsResolver"/>); only the parity test
    /// (tools/dotnet/tests/SearchIndexRequestBuilder.Tests) passes a fixed fake value, to match the
    /// capture harness's own fake endpoint.</param>
    /// <param name="embeddingDeployment">Flows straight into the index definition's vectorizer
    /// "deploymentId" field -- resolved the same way as <paramref name="openAiEndpoint"/>.</param>
    /// <param name="embeddingProvider">Computes each document's "embedding" field from its
    /// combined embedding-input text. Defaults to <c>null</c>, meaning no "embedding" field is
    /// attached at all: this tool never calls Azure OpenAI's real, non-deterministic
    /// <c>generate_embeddings</c> under any flag, so a real CLI run has no embedding values to
    /// report (see docs/dotnet_tooling.md's "Batch 2 port" section). Only the parity test injects
    /// a deterministic fixture formula (tools/dotnet/tests/SearchIndexRequestBuilder.Tests'
    /// FixtureEmbedding.cs) here, so its captured documents' "embedding" arrays can be compared
    /// against the capture harness's own matching fixture.</param>
    public static PersonaRequestPlan BuildPlan(
        EnabledPersonaDiscovery.DiscoveredPersona persona,
        string openAiEndpoint,
        string embeddingDeployment,
        Func<string, IReadOnlyList<double>>? embeddingProvider = null)
    {
        var menuData = JsonNode.Parse(File.ReadAllText(persona.MenuPath)) ??
            throw new InvalidOperationException($"'{persona.MenuPath}' did not parse to a JSON value.");
        var prepared = MenuDocumentBuilder.Build(menuData);

        var documents = new List<JsonObject>(prepared.Count);
        foreach (var document in prepared)
        {
            if (embeddingProvider is not null)
            {
                var embeddingArray = new JsonArray();
                foreach (var component in embeddingProvider(document.CombinedTextForEmbedding))
                {
                    embeddingArray.Add(component);
                }
                document.Fields["embedding"] = embeddingArray;
            }
            documents.Add(document.Fields);
        }

        var indexDefinition = SearchIndexDefinitionBuilder.Build(persona.IndexName, openAiEndpoint, embeddingDeployment);
        var documentBatches = DocumentBatchBuilder.BuildBatches(documents);

        return new PersonaRequestPlan(persona.PersonaId, persona.IndexName, indexDefinition, documentBatches, documents.Count);
    }
}
