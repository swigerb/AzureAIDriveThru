using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Independently reconstructs the exact Azure AI Search REST request body that
/// <c>create_or_update_index</c> (setup_search_index.py lines 116-245) sends via
/// <c>SearchIndexClient.create_or_update_index(index)</c> -- a <c>PUT /indexes('&lt;name&gt;')</c>.
/// Field names/shapes below were captured empirically from the REAL azure-search-documents==12.0.0
/// Python SDK (pinned in app/backend/requirements.txt) via a one-off probe using the same
/// HttpTransport-capture technique
/// tests/SearchIndexRequestBuilder.Tests/Fixtures/capture_search_index_requests.py uses for the
/// parity test, not guessed from the SDK's Python-side model attribute names (which use
/// snake_case/different nesting from the REST JSON's camelCase -- e.g. the SDK's
/// <c>vector_search_dimensions</c> model field serializes as this body's "dimensions").
/// </summary>
internal static class SearchIndexDefinitionBuilder
{
    // setup_search_index.py line ~64.
    private const string EmbeddingModel = "text-embedding-3-large";
    private const int EmbeddingDimensions = 3072;

    private const string HnswAlgorithmName = "menuHnsw";
    private const string VectorProfileName = "menuHnswProfile";
    private const string VectorizerName = "menuVectorizer";
    private const string SemanticConfigName = "menuSemanticConfig";

    public static JsonObject Build(string indexName, string openAiEndpoint, string embeddingDeployment)
    {
        return new JsonObject
        {
            ["name"] = indexName,
            ["fields"] = BuildFields(),
            ["vectorSearch"] = BuildVectorSearch(openAiEndpoint, embeddingDeployment),
            ["semantic"] = BuildSemanticSearch(),
        };
    }

    private static JsonArray BuildFields() =>
    [
        new JsonObject
        {
            ["name"] = "id",
            ["type"] = "Edm.String",
            ["key"] = true,
            ["searchable"] = false,
            ["filterable"] = true,
            ["facetable"] = false,
            ["sortable"] = true,
            ["retrievable"] = true,
        },
        new JsonObject
        {
            ["name"] = "category",
            ["type"] = "Edm.String",
            ["sortable"] = true,
            ["filterable"] = true,
            ["facetable"] = true,
        },
        new JsonObject
        {
            ["name"] = "name",
            ["type"] = "Edm.String",
            ["sortable"] = true,
            ["filterable"] = true,
            ["facetable"] = true,
        },
        new JsonObject { ["name"] = "description", ["type"] = "Edm.String" },
        new JsonObject { ["name"] = "longDescription", ["type"] = "Edm.String" },
        new JsonObject { ["name"] = "origin", ["type"] = "Edm.String", ["filterable"] = true },
        new JsonObject { ["name"] = "caffeineContent", ["type"] = "Edm.String", ["filterable"] = true },
        new JsonObject { ["name"] = "brewingMethod", ["type"] = "Edm.String", ["filterable"] = true },
        new JsonObject
        {
            ["name"] = "popularity",
            ["type"] = "Edm.String",
            ["filterable"] = true,
            ["facetable"] = true,
        },
        // #165: filterable only (no facetable) -- an empty-string sentinel for packs with no
        // dayparts must never become its own facet bucket.
        new JsonObject { ["name"] = "menuPeriod", ["type"] = "Edm.String", ["filterable"] = true },
        new JsonObject
        {
            ["name"] = "sizes",
            ["type"] = "Edm.String",
            ["filterable"] = false,
            ["facetable"] = false,
        },
        new JsonObject
        {
            ["name"] = "embedding",
            ["type"] = "Collection(Edm.Single)",
            ["dimensions"] = EmbeddingDimensions,
            ["vectorSearchProfile"] = VectorProfileName,
        },
    ];

    private static JsonObject BuildVectorSearch(string openAiEndpoint, string embeddingDeployment) =>
        new()
        {
            ["algorithms"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = HnswAlgorithmName,
                    ["hnswParameters"] = new JsonObject
                    {
                        ["metric"] = "cosine",
                        ["m"] = 10,
                        ["efConstruction"] = 200,
                    },
                    ["kind"] = "hnsw",
                },
            },
            ["profiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = VectorProfileName,
                    ["algorithm"] = HnswAlgorithmName,
                    ["vectorizer"] = VectorizerName,
                },
            },
            ["vectorizers"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = VectorizerName,
                    ["azureOpenAIParameters"] = new JsonObject
                    {
                        ["resourceUri"] = openAiEndpoint,
                        ["deploymentId"] = embeddingDeployment,
                        ["modelName"] = EmbeddingModel,
                    },
                    ["kind"] = "azureOpenAI",
                },
            },
        };

    private static JsonObject BuildSemanticSearch() =>
        new()
        {
            ["configurations"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = SemanticConfigName,
                    ["prioritizedFields"] = new JsonObject
                    {
                        ["titleField"] = new JsonObject { ["fieldName"] = "name" },
                        ["prioritizedContentFields"] = new JsonArray
                        {
                            new JsonObject { ["fieldName"] = "description" },
                            new JsonObject { ["fieldName"] = "longDescription" },
                            new JsonObject { ["fieldName"] = "category" },
                        },
                    },
                },
            },
        };
}
