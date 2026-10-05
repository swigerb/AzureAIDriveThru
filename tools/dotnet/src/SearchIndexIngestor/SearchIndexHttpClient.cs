using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace SearchIndexIngestor;

/// <summary>
/// Issues the exact Azure AI Search REST calls setup_search_index.py's
/// <c>create_or_update_index</c>/<c>upload_documents</c>/<c>delete_stale_documents</c>/
/// <c>verify_document_count</c> send via the real <c>azure-search-documents==12.0.0</c> SDK --
/// method/URL path/api-version/selected headers captured empirically the same way
/// SearchIndexDefinitionBuilder.cs/DocumentBatchBuilder.cs's request BODIES were (see those
/// classes' own remarks), via a one-off HttpTransport-capture probe against the real pinned SDK.
///
/// Deliberately raw <see cref="HttpClient"/>, not the <c>Azure.Search.Documents</c> package: this
/// solution (tools/dotnet) has never referenced an Azure SDK package before this PR (every prior
/// tool hand-builds REST JSON bodies and never opens a socket at all), and
/// <c>Azure.Search.Documents</c> would be a second, heavier new dependency on top of the one
/// (<c>Azure.Identity</c>, for <c>DefaultAzureCredential</c> bearer tokens -- see
/// <see cref="BearerTokenHandler"/>) this port already needs. Reusing
/// SearchIndexDefinitionBuilder/DocumentBatchBuilder's already-reviewed body builders for the
/// request CONTENT, directly over raw HTTP, avoids that second dependency entirely while still
/// genuinely hitting a live Search service in production.
/// </summary>
public sealed class SearchIndexHttpClient : IDisposable
{
    // Empirically captured (azure-search-documents==12.0.0's own build_search_*_request helpers
    // default to this exact literal) -- NOT a documented/guessed value. A future SDK version
    // bumping this would need this constant updated to match, same as
    // SearchIndexDefinitionBuilder.cs's own captured constants.
    private const string ApiVersion = "2026-04-01";

    private readonly HttpClient _http;
    private readonly string _searchEndpoint;
    private readonly bool _ownsHttpClient;

    /// <param name="httpClient">Already configured for auth (production: wraps
    /// <see cref="BearerTokenHandler"/> -- see <see cref="CreateForProduction"/>). Tests pass a
    /// plain <see cref="HttpClient"/> over a fake <see cref="HttpMessageHandler"/> instead, with no
    /// auth concerns at all (the strict parity test never asserts on the Authorization header's
    /// VALUE -- see docs/dotnet_tooling.md's "headers that matter" note -- only that method/URL
    /// path/api-version/Content-Type/Accept/Prefer/body match).</param>
    /// <param name="searchEndpoint">e.g. <c>https://my-search.search.windows.net</c> (no trailing
    /// slash required -- normalized below).</param>
    /// <param name="ownsHttpClient">Whether <see cref="Dispose"/> should dispose
    /// <paramref name="httpClient"/> too -- true for <see cref="CreateForProduction"/>'s own
    /// client, false when a test (or future caller) owns the client's lifetime itself.</param>
    public SearchIndexHttpClient(HttpClient httpClient, string searchEndpoint, bool ownsHttpClient = false)
    {
        _http = httpClient;
        _searchEndpoint = searchEndpoint.TrimEnd('/');
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>azure-core's own default connection AND read timeout for every
    /// <c>azure-search-documents</c> request (Rick's review, item 2b) -- see
    /// <see cref="CreateForProduction"/>'s remarks for how this is applied.</summary>
    public static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Builds a production client: <c>DefaultAzureCredential</c> bearer tokens scoped to
    /// <c>https://search.azure.com/.default</c> -- the exact same scope
    /// <c>azure-search-documents</c>' own <c>_configuration.py</c> defaults
    /// <c>credential_scopes</c> to (confirmed by reading the pinned SDK's source directly, not
    /// guessed) -- matching setup_search_index.py's own "DefaultAzureCredential only: no Azure
    /// OpenAI or Search key is ever read, issued, or stored" guarantee (module docstring, line 24).
    ///
    /// Wraps <see cref="SearchRetryHandler"/> (Rick's review, item 2b: azure-core's own
    /// <c>retry_total=10</c>/<c>backoff_factor=0.8</c>/<c>backoff_max=120</c>, retrying
    /// 408/429/500/502-504 and honouring <c>Retry-After</c>) around a <see cref="SocketsHttpHandler"/>
    /// whose own <see cref="SocketsHttpHandler.ConnectTimeout"/> AND whose owning
    /// <see cref="HttpClient.Timeout"/> are both set to <see cref="ProductionTimeout"/> (300 s),
    /// matching azure-core's pinned 300 s connection AND read timeouts (<c>HttpClient.Timeout</c>
    /// is the closest .NET equivalent to a per-request "read" timeout; .NET has no separate
    /// connect-vs-read split the way azure-core's own transport does, so both knobs are set to the
    /// same value here deliberately).
    /// </summary>
    public static SearchIndexHttpClient CreateForProduction(string searchEndpoint, TokenCredential credential)
    {
        var http = CreateProductionHttpClient(credential);
        return new SearchIndexHttpClient(http, searchEndpoint, ownsHttpClient: true);
    }

    /// <summary>Factored out of <see cref="CreateForProduction"/> so tests can assert on the
    /// configured timeout/retry-handler shape directly, without constructing a real
    /// <c>DefaultAzureCredential</c> (see <see cref="CreateForProduction"/>'s own remarks).</summary>
    internal static HttpClient CreateProductionHttpClient(TokenCredential credential)
    {
        var socketsHandler = new SocketsHttpHandler { ConnectTimeout = ProductionTimeout };
        var bearerHandler = new BearerTokenHandler(credential, "https://search.azure.com/.default", socketsHandler);
        var retryHandler = new SearchRetryHandler(bearerHandler);
        return new HttpClient(retryHandler) { Timeout = ProductionTimeout };
    }

    /// <summary>
    /// <c>create_or_update_index</c> (setup_search_index.py lines 116-245) ->
    /// <c>PUT /indexes('&lt;name&gt;')?api-version=...</c>, <c>Prefer: return=representation</c>,
    /// <c>Accept: application/json;odata.metadata=minimal</c> (captured empirically -- the ONE
    /// request type among these four whose Accept differs from every other call's
    /// <c>odata.metadata=none</c>).
    /// </summary>
    public async Task CreateOrUpdateIndexAsync(string indexName, JsonObject indexDefinition, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, IndexUrl(indexName))
        {
            Content = JsonNodeContent(indexDefinition),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=minimal");
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create/update index", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>upload_documents</c>'s per-batch <c>merge_or_upload_documents(batch)</c> (lines 294-309)
    /// -&gt; <c>POST /indexes('&lt;name&gt;')/docs/search.index?api-version=...</c>. Returns how
    /// many of <paramref name="batchBody"/>'s documents succeeded, parsed from the real
    /// <c>IndexDocumentsResult</c> response shape (<c>{"value": [{"key", "status", ...}, ...]}</c>)
    /// -- the same count setup_search_index.py's own <c>sum(1 for r in result if r.succeeded)</c>
    /// computes.
    /// </summary>
    public async Task<int> UploadBatchAsync(string indexName, JsonObject batchBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DocsSearchIndexUrl(indexName))
        {
            Content = JsonNodeContent(batchBody),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=none");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "upload document batch", cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        var results = body?["value"]?.AsArray() ??
            throw new InvalidOperationException("Azure AI Search's upload response had no \"value\" array.");
        return results.Count(r => r?["status"]?.GetValue<bool>() == true);
    }

    /// <summary>
    /// <c>delete_stale_documents</c>'s existing-id listing (line 318) -&gt;
    /// <c>POST /indexes('&lt;name&gt;')/docs/search.post.search?api-version=...</c>, body
    /// <c>{"search": "*", "select": "id"}</c>. Unlike the capture-only parity test's fixture
    /// (which never has more documents than fit in one page), a REAL index can hold more ids than
    /// Azure AI Search returns in a single response -- so this follows the REST API's own
    /// <c>@search.nextPageParameters</c> continuation token across as many requests as it takes to
    /// enumerate every id, the same thing azure-search-documents' <c>SearchItemPaged</c> iterator
    /// does transparently for the real Python script (<c>[doc["id"] for doc in
    /// search_client.search(...)]</c> above it).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAllDocumentIdsAsync(string indexName, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        JsonObject? nextPageParameters = new() { ["search"] = "*", ["select"] = "id" };

        while (nextPageParameters is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DocsSearchPostSearchUrl(indexName))
            {
                Content = JsonNodeContent(nextPageParameters),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=none");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "list existing document ids", cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            foreach (var doc in body?["value"]?.AsArray() ?? [])
            {
                var id = doc?["id"]?.GetValue<string>();
                if (id is not null)
                {
                    ids.Add(id);
                }
            }

            nextPageParameters = body?["@search.nextPageParameters"]?.AsObject()?.DeepClone()?.AsObject();
        }

        return ids;
    }

    /// <summary>
    /// <c>delete_stale_documents</c>'s delete batch (line 322) -- the SAME URL as
    /// <see cref="UploadBatchAsync"/> (<c>docs/search.index</c>), differentiated only by each
    /// document's <c>"@search.action": "delete"</c> instead of <c>"mergeOrUpload"</c>.
    /// </summary>
    public async Task DeleteBatchAsync(string indexName, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var value = new JsonArray();
        foreach (var id in ids)
        {
            value.Add(new JsonObject { ["@search.action"] = "delete", ["id"] = id });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, DocsSearchIndexUrl(indexName))
        {
            Content = JsonNodeContent(new JsonObject { ["value"] = value }),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=none");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "delete stale document batch", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>verify_document_count</c>'s <c>search_client.get_document_count()</c> (line 341) -&gt;
    /// <c>GET /indexes('&lt;name&gt;')/docs/$count?api-version=...</c>. Unlike every other call
    /// here, Azure AI Search returns this as a RAW integer response body (e.g. <c>60</c>), not a
    /// JSON object -- confirmed by reading azure-search-documents' own
    /// <c>get_document_count</c>, which deserializes <c>response.text()</c> directly as an
    /// <c>int</c>, never as JSON.
    /// </summary>
    public async Task<int> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DocsCountUrl(indexName));
        request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=none");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "get document count", cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string IndexUrl(string indexName) =>
        $"{_searchEndpoint}/indexes('{Uri.EscapeDataString(indexName)}')?api-version={ApiVersion}";

    private string DocsSearchIndexUrl(string indexName) =>
        $"{_searchEndpoint}/indexes('{Uri.EscapeDataString(indexName)}')/docs/search.index?api-version={ApiVersion}";

    private string DocsSearchPostSearchUrl(string indexName) =>
        $"{_searchEndpoint}/indexes('{Uri.EscapeDataString(indexName)}')/docs/search.post.search?api-version={ApiVersion}";

    private string DocsCountUrl(string indexName) =>
        $"{_searchEndpoint}/indexes('{Uri.EscapeDataString(indexName)}')/docs/$count?api-version={ApiVersion}";

    private static JsonContent JsonNodeContent(JsonNode body)
    {
        var content = JsonContent.Create(body, options: new JsonSerializerOptions());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Azure AI Search request to {action} failed with status {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {body}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
