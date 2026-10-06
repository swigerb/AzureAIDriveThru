using Backend.Configuration;

namespace Backend.Search;

/// <summary>
/// Port of app/backend/app.py's `persona_search_contexts`/`attach_tools_rtmt` Azure AI Search
/// connection settings (docs/dotnet_mapping.md, issue #14/#23): deployment-wide env-var-driven
/// dials every persona's own search calls share (only the index name -- <see
/// cref="Backend.Personas.PersonaSearch.IndexName"/> -- varies per persona, via persona.json, not
/// these). Same env var names, same fallback defaults as Python's own `os.environ.get(name) or
/// default` reads, so an unset var behaves identically in both backends.
///
/// <para><b>#23 decision (recorded in docs/dotnet_mapping.md "Spikes #44 and #23"):</b> rather than
/// fighting `Azure.Search.Documents`'s HTTPS-only transport assumption against the conformance
/// harness's HTTP-only `FakeSearchServer`, this C# port calls the Azure AI Search data-plane REST
/// API directly via a plain <see cref="System.Net.Http.HttpClient"/> (<c>Search/SearchTool.cs</c>)
/// -- see that file for the request/response shape, which matches exactly what `FakeSearchServer`
/// (and the real service) expects.</para>
///
/// <para><b>Auth scope note:</b> <c>AZURE_SEARCH_API_KEY</c> (sent as the REST <c>api-key</c>
/// header -- mirrors Python's <c>AzureKeyCredential</c> path) when configured; else <see
/// cref="SearchTool"/> falls back to a managed-identity bearer token via
/// <c>Azure.Identity.DefaultAzureCredential</c> (scope <c>https://search.azure.com/.default</c>,
/// <see cref="DefaultAzureCredentialSearchTokenProvider"/>), mirroring Python's own
/// <c>DefaultAzureCredential</c> fallback and PR #140 R5's identical pattern for the realtime
/// upstream connect.</para>
/// </summary>
internal sealed class SearchEndpointConfig
{
    /// <summary>Azure AI Search REST data-plane API version this client sends -- pinned to the
    /// same version issue #23 verified `azure-search-documents` 12.0.0 itself sends.</summary>
    public const string ApiVersion = "2026-04-01";

    public string Endpoint { get; }
    public string? ApiKey { get; }
    public string SemanticConfiguration { get; }
    public string IdentifierField { get; }
    public string ContentField { get; }
    public string EmbeddingField { get; }
    public bool UseVectorQuery { get; }
    public bool UseSemanticRanker { get; }

    public SearchEndpointConfig(
        string endpoint,
        string? apiKey,
        string semanticConfiguration,
        string identifierField,
        string contentField,
        string embeddingField,
        bool useVectorQuery,
        bool useSemanticRanker)
    {
        Endpoint = endpoint;
        ApiKey = apiKey;
        SemanticConfiguration = semanticConfiguration;
        IdentifierField = identifierField;
        ContentField = contentField;
        EmbeddingField = embeddingField;
        UseVectorQuery = useVectorQuery;
        UseSemanticRanker = useSemanticRanker;
    }

    /// <summary>Mirrors app.py's env-var reads for both the deployment-wide default search
    /// context and every per-persona `persona_search_contexts[persona_id]` entry -- the same six
    /// env vars back both in Python (only the index name/client differ per persona), so there is
    /// exactly one env-reading path here too.</summary>
    public static SearchEndpointConfig FromEnvironment()
    {
        var endpoint = BackendEnvironment.Get(BackendEnvironment.AzureSearchEndpoint) ?? "";
        var apiKey = BackendEnvironment.Get(BackendEnvironment.AzureSearchApiKey);
        var semanticConfiguration = NonEmpty(BackendEnvironment.Get(BackendEnvironment.AzureSearchSemanticConfiguration), "menuSemanticConfig");
        var identifierField = NonEmpty(BackendEnvironment.Get(BackendEnvironment.AzureSearchIdentifierField), "id");
        var contentField = NonEmpty(BackendEnvironment.Get(BackendEnvironment.AzureSearchContentField), "description");
        var embeddingField = NonEmpty(BackendEnvironment.Get(BackendEnvironment.AzureSearchEmbeddingField), "embedding");
        var useVectorQuery = GetBoolEnv(BackendEnvironment.AzureSearchUseVectorQuery, true);
        var useSemanticRanker = !NonEmpty(BackendEnvironment.Get(BackendEnvironment.AzureSearchSemanticRanker), "standard")
            .Equals("disabled", StringComparison.OrdinalIgnoreCase);

        return new SearchEndpointConfig(
            endpoint, apiKey, semanticConfiguration, identifierField, contentField, embeddingField,
            useVectorQuery, useSemanticRanker);
    }

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    /// <summary>Mirrors app.py's `_get_bool_env`: unset -> <paramref name="fallback"/>; otherwise
    /// one of "1"/"true"/"yes"/"on" (case-insensitive, trimmed) -> true, anything else -> false.</summary>
    private static bool GetBoolEnv(string name, bool fallback)
    {
        var value = BackendEnvironment.Get(name);
        if (value is null)
        {
            return fallback;
        }
        return value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }
}
