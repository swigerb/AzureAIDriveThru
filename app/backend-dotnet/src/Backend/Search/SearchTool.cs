using System.Net.Http.Json;
using System.Text.Json;
using Backend.Configuration;
using Backend.Personas;
using Backend.Prompts;
using Backend.Tools;

namespace Backend.Search;

/// <summary>
/// Port of app/backend/tools.py's <c>search</c> (docs/dotnet_mapping.md, issues #14/#23/#37/#74).
/// One instance is owned by exactly one session's actor (same confinement contract as <see
/// cref="Ordering.OrderState"/>/<see cref="OrderToolExecutor"/>), closed over that session's own
/// bound persona's <see cref="MenuCatalog"/>, <see cref="PromptLoader"/>, and Azure AI Search
/// index name -- see <see cref="Tools.SessionToolExecutor"/> for how this composes with
/// <see cref="OrderToolExecutor"/> into one session's full <see cref="IToolExecutor"/>.
///
/// <para><b>#23 (REST, not the SDK):</b> calls the Azure AI Search data-plane REST API directly
/// via <see cref="HttpClient"/> (<c>POST {endpoint}/indexes('{indexName}')/docs/search.post.search
/// ?api-version=...</c>, <c>api-key</c> header) instead of <c>Azure.Search.Documents</c>, which
/// refuses to talk to an HTTP (non-HTTPS) endpoint -- exactly the shape the conformance harness's
/// <c>FakeSearchServer</c> (and the real service) expects.</para>
///
/// <para><b>Caching (#37, PR #50 review should-fix 4):</b> a shared, process-wide, TTL + max-size
/// <see cref="SearchResultCache"/> (mirrors Python's module-level <c>_search_cache</c>) namespaced
/// by persona id so two personas asking the same question never share a cached result from each
/// other's (potentially different) search index.</para>
/// </summary>
public sealed class SearchTool
{
    private static readonly SearchResultCache Cache = new();

    private readonly HttpClient _http;
    private readonly SearchEndpointConfig _config;
    private readonly SearchConfig _searchConfig;
    private readonly MenuCatalog _menu;
    private readonly PromptLoader? _promptLoader;
    private readonly string _indexName;
    private readonly string? _personaId;

    public SearchTool(
        HttpClient http,
        SearchEndpointConfig config,
        SearchConfig searchConfig,
        MenuCatalog menu,
        PromptLoader? promptLoader,
        string indexName,
        string? personaId)
    {
        _http = http;
        _config = config;
        _searchConfig = searchConfig;
        _menu = menu;
        _promptLoader = promptLoader;
        _indexName = indexName;
        _personaId = personaId;
    }

    /// <summary>Executes one <c>search</c> tool call. Always <see
    /// cref="ToolResultDirection.ToServer"/> -- mirrors tools.py's <c>search</c> always
    /// constructing a <c>TO_SERVER</c> <c>ToolResult</c>: search results are model-facing
    /// knowledge-base lookups, never a client-visible order-state change.</summary>
    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        var rawQuery = args.TryGetProperty("query", out var queryEl) ? queryEl.GetString() ?? "" : "";

        // #77 (strategies.searchQueryRewrite): this persona's own named extension point, applied
        // to `query` itself so the cache key and every search request see the same rewritten text.
        var query = _menu.RewriteSearchQuery(rawQuery);

        var cacheKey = $"{_personaId ?? ""}::{query.Trim().ToLowerInvariant()}";
        if (Cache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var selectFields = new[] { _config.IdentifierField, "name", "category", "description", "sizes" };
        var semanticEnabled = _config.UseSemanticRanker && !string.IsNullOrEmpty(_config.SemanticConfiguration);

        List<JsonElement> records;
        try
        {
            records = await FetchRecordsAsync(query, selectFields, includeVector: true, semantic: semanticEnabled, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ServerError("search_service_unavailable",
                "I'm having trouble reaching our menu right now — could you try that again?");
        }
        catch (SearchApiException exc) when (exc.Message.Contains("Could not find a property named"))
        {
            // #37/PR #50 review: gracefully retry with a minimal projection on a field-name
            // mismatch (e.g. an out-of-date `select` list against the real index's schema).
            try
            {
                string?[] fallbackCandidates = [_config.IdentifierField, _config.ContentField];
                var fallbackSelect = fallbackCandidates.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToArray();
                records = await FetchRecordsAsync(query, fallbackSelect, includeVector: true, semantic: semanticEnabled, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
            }
        }
        catch (SearchApiException exc) when (semanticEnabled && exc.Message.Contains("semantic", StringComparison.OrdinalIgnoreCase))
        {
            // Belt and braces: the service rejected the semantic query even though configuration
            // said it was available (e.g. the SKU changed after deployment). Retry without the
            // ranker rather than failing the lookup outright.
            try
            {
                records = await FetchRecordsAsync(query, selectFields, includeVector: true, semantic: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
            }
        }
        catch (SearchApiException)
        {
            return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
        }
        catch (Exception)
        {
            return ServerError("search_service_unavailable", "I had a little glitch looking that up — could you say that again?");
        }

        var results = records.Select(FormatRecord).ToList();
        var joined = string.Join("\n-----\n", results);
        var noResults = _promptLoader is { } pl && pl.ErrorMessages.TryGetValue("search_no_results", out var raw) && raw is string rawText
            ? rawText
            : "No matching menu entries found.";

        var result = new ToolResult(joined.Length > 0 ? joined : noResults, ToolResultDirection.ToServer);
        Cache.Put(cacheKey, result, _searchConfig.CacheTtlSeconds, _searchConfig.CacheMaxSize);
        return result;
    }

    private ToolResult ServerError(string errorKey, string fallback) =>
        new(_promptLoader?.RenderError(errorKey) ?? fallback, ToolResultDirection.ToServer);

    private string FormatRecord(JsonElement record)
    {
        var identifier = GetString(record, _config.IdentifierField) ?? GetString(record, "id") ?? "unknown";
        var itemName = GetString(record, "name") ?? "N/A";
        var category = GetString(record, "category") ?? "N/A";
        var sizeStr = FormatSizes(record);

        var summary = $"[{identifier}]: Item: {itemName}, Category: {category}, Available Sizes: {sizeStr}";

        // Flag items affected by machine outages so the model knows not to recommend them --
        // data-driven off the item's own `requiresMachine` field (#73), never a keyword list.
        var machine = _menu.RequiresMachine(itemName);
        if (!string.IsNullOrEmpty(machine) && _menu.MachineStatus(machine) == "down")
        {
            summary += $" [OOS: {_menu.MachineLabel(machine)}]";
        }

        return summary;
    }

    private string FormatSizes(JsonElement record)
    {
        if (!record.TryGetProperty("sizes", out var sizesEl) || sizesEl.ValueKind != JsonValueKind.String)
        {
            return "N/A";
        }
        var raw = sizesEl.GetString() ?? "N/A";
        try
        {
            using var sizesDoc = JsonDocument.Parse(raw);
            var parts = sizesDoc.RootElement.EnumerateArray().Select(size =>
            {
                var sizeCode = size.TryGetProperty("size", out var s) ? s.GetString() ?? "" : "";
                var priceText = size.TryGetProperty("price", out var p) ? p.GetRawText() : "0";
                return $"{FormatSizeHumanReadable(sizeCode)} (${priceText})";
            });
            return string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>Mirrors tools.py's <c>_format_size_human_readable</c>: this persona's own size
    /// map's display label, or the raw code capitalized when it isn't a recognized size.</summary>
    private string FormatSizeHumanReadable(string size)
    {
        var normalized = _menu.NormalizeSize(size);
        if (normalized.Length > 0)
        {
            return normalized;
        }
        return size.Length == 0 ? size : char.ToUpperInvariant(size[0]) + size[1..];
    }

    private static string? GetString(JsonElement record, string field) =>
        !string.IsNullOrEmpty(field) && record.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Issues one Azure AI Search REST request and returns its <c>value</c> array,
    /// bounded by <see cref="SearchConfig.TimeoutSeconds"/> (PR #50 review should-fix 4: the
    /// timeout wraps the ENTIRE request/response round trip, not just constructing it). Throws
    /// <see cref="TimeoutException"/> on expiry and <see cref="SearchApiException"/> on any
    /// non-2xx response.</summary>
    private async Task<List<JsonElement>> FetchRecordsAsync(
        string query, IReadOnlyList<string> selectFields, bool includeVector, bool semantic, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_searchConfig.TimeoutSeconds));

        try
        {
            var body = BuildSearchBody(query, selectFields, includeVector, semantic);
            var url = $"{_config.Endpoint.TrimEnd('/')}/indexes('{Uri.EscapeDataString(_indexName)}')" +
                      $"/docs/search.post.search?api-version={SearchEndpointConfig.ApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                request.Headers.Add("api-key", _config.ApiKey);
            }

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new SearchApiException((int)response.StatusCode, ExtractErrorMessage(text, response.StatusCode));
            }

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("value", out var values)
                ? values.EnumerateArray().Select(e => e.Clone()).ToList()
                : [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Azure AI Search request timed out after {_searchConfig.TimeoutSeconds}s.");
        }
    }

    private object BuildSearchBody(string query, IReadOnlyList<string> selectFields, bool includeVector, bool semantic)
    {
        var body = new Dictionary<string, object?>
        {
            ["search"] = query,
            ["top"] = _searchConfig.TopResults,
            ["select"] = string.Join(",", selectFields),
        };
        if (semantic)
        {
            body["queryType"] = "semantic";
            body["semanticConfigurationName"] = _config.SemanticConfiguration;
        }
        if (includeVector && _config.UseVectorQuery && !string.IsNullOrEmpty(_config.EmbeddingField))
        {
            body["vectorQueries"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["kind"] = "text",
                    ["text"] = query,
                    ["k"] = _searchConfig.KNearestNeighbors,
                    ["fields"] = _config.EmbeddingField,
                },
            };
        }
        return body;
    }

    private static string ExtractErrorMessage(string body, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? $"Azure AI Search request failed with status {(int)statusCode}.";
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic message below -- an error body that isn't the expected
            // Azure-AI-Search JSON shape (e.g. a plain-text 5xx from a proxy) is still a real
            // failure, just without a specific message to surface.
        }
        return $"Azure AI Search request failed with status {(int)statusCode}.";
    }
}
