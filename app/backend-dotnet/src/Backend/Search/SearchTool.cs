using System.Net.Http.Json;
using System.Text.Json;
using Backend.Configuration;
using Backend.Personas;
using Backend.Prompts;
using Backend.Tools;
using Microsoft.Extensions.Logging;

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
    private readonly ISearchBearerTokenProvider? _bearerTokenProvider;
    private readonly ILogger? _logger;
    // Issue 165: this session's own bound daypart ("breakfast"/"lunch"), resolved once at
    // connect time -- null for a persona with no features.dayparts, or an unbound caller. The
    // single reader is the OData filter built in ExecuteAsync below.
    private readonly string? _menuMode;
    // #309 (R1): this session's own OrderState.EffectiveMachineStatus (pack default + the
    // session's own operator override, if any, applied on top) -- mirrors tools.py's
    // `order_state_singleton.effective_machine_status(session_id, machine)`. Null for an
    // unbound/direct caller (e.g. most of this class's own tests), in which case FormatRecord
    // falls back to `_menu.MachineStatus(machine)` exactly as before this fix.
    private readonly Func<string, string?>? _effectiveMachineStatus;

    public SearchTool(
        HttpClient http,
        SearchEndpointConfig config,
        SearchConfig searchConfig,
        MenuCatalog menu,
        PromptLoader? promptLoader,
        string indexName,
        string? personaId,
        ISearchBearerTokenProvider? bearerTokenProvider = null,
        ILogger? logger = null,
        string? menuMode = null,
        Func<string, string?>? effectiveMachineStatus = null)
    {
        _http = http;
        _config = config;
        _searchConfig = searchConfig;
        _menu = menu;
        _promptLoader = promptLoader;
        _indexName = indexName;
        _personaId = personaId;
        _bearerTokenProvider = bearerTokenProvider;
        _logger = logger;
        _menuMode = menuMode;
        _effectiveMachineStatus = effectiveMachineStatus;
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

        // #309 (R1): the cache stores the RAW records Azure AI Search returned, not the
        // formatted/OOS-tagged ToolResult -- see FormatRecord below and SearchResultCache's own
        // doc comment for why. Machine status (and therefore which items get an "[OOS: ...]"
        // tag) is a per-session, override-sensitive property, so the formatted text is built
        // fresh on EVERY call, cache hit or not, off THIS call's own effective status.
        var cacheKey = $"{_personaId ?? ""}::{_menuMode ?? ""}::{query.Trim().ToLowerInvariant()}";
        List<JsonElement>? records = Cache.TryGet(cacheKey, out var cachedRecords) ? cachedRecords : null;

        // Issue 165: restrict results server-side to items whose own menuPeriod is _menuMode,
        // "allDay", or unset ("" -- setup_search_index.py's own sentinel for a period-less item)
        // -- the same OData filter setup_search_index.py documents. Null for a persona with no
        // features.dayparts (every existing caller keeps passing nothing, a pure no-op).
        // Rick's PR 166 round-1 review, required item 6: the third clause keeps
        // MenuCatalog.ItemAvailableNow's own always-available treatment of a period-less item and
        // this search filter in sync -- without it, a period-less item could be added to an order
        // in either mode yet never surface in a mode-filtered search.
        var modeFilter = _menuMode is { Length: > 0 }
            ? $"menuPeriod eq '{_menuMode}' or menuPeriod eq 'allDay' or menuPeriod eq ''"
            : null;

        var selectFields = new[] { _config.IdentifierField, "name", "category", "description", "sizes" };
        var semanticEnabled = _config.UseSemanticRanker && !string.IsNullOrEmpty(_config.SemanticConfiguration);

        if (records is null)
        {
            try
            {
                records = await FetchRecordsAsync(query, selectFields, includeVector: true, semantic: semanticEnabled, modeFilter, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exc)
            {
                _logger?.LogError(exc,
                    "Search timed out for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}",
                    _personaId, _indexName, exc.GetType().Name, exc.Message);
                return ServerError("search_service_unavailable",
                    "I'm having trouble reaching our menu right now — could you try that again?");
            }
            catch (SearchApiException exc) when (exc.Message.Contains("Could not find a property named"))
            {
                // #37/PR #50 review: gracefully retry with a minimal projection on a field-name
                // mismatch (e.g. an out-of-date `select` list against the real index's schema, or --
                // issue 165 -- a `menuPeriod` filter against an index that hasn't been rebuilt with
                // that field yet). Dropping the mode filter here too means a stale index degrades to
                // unfiltered search rather than failing the lookup outright.
                _logger?.LogWarning(exc,
                    "Search field-name mismatch for persona {PersonaId} index {IndexName}; retrying with a minimal projection: {ExceptionType}: {ExceptionMessage}",
                    _personaId, _indexName, exc.GetType().Name, exc.Message);
                try
                {
                    string?[] fallbackCandidates = [_config.IdentifierField, _config.ContentField];
                    var fallbackSelect = fallbackCandidates.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToArray();
                    records = await FetchRecordsAsync(query, fallbackSelect, includeVector: true, semantic: semanticEnabled, filter: null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception retryExc)
                {
                    _logger?.LogError(retryExc,
                        "Search field-name-mismatch retry also failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}",
                        _personaId, _indexName, retryExc.GetType().Name, retryExc.Message);
                    return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
                }
            }
            catch (SearchApiException exc) when (semanticEnabled && exc.Message.Contains("semantic", StringComparison.OrdinalIgnoreCase))
            {
                // Belt and braces: the service rejected the semantic query even though configuration
                // said it was available (e.g. the SKU changed after deployment). Retry without the
                // ranker rather than failing the lookup outright.
                _logger?.LogWarning(exc,
                    "Semantic ranker rejected by the service for persona {PersonaId} index {IndexName}; retrying without it: {ExceptionType}: {ExceptionMessage}",
                    _personaId, _indexName, exc.GetType().Name, exc.Message);
                try
                {
                    records = await FetchRecordsAsync(query, selectFields, includeVector: true, semantic: false, modeFilter, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception retryExc)
                {
                    _logger?.LogError(retryExc,
                        "Semantic-ranker retry also failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}",
                        _personaId, _indexName, retryExc.GetType().Name, retryExc.Message);
                    return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
                }
            }
            catch (SearchApiException exc)
            {
                _logger?.LogError(exc,
                    "Search failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}",
                    _personaId, _indexName, exc.GetType().Name, exc.Message);
                return ServerError("search_service_unavailable", "I'm sorry, I can't reach our menu data right now.");
            }
            catch (OperationCanceledException)
            {
                // #247: a genuine caller-side cancellation (e.g. a barge-in cancelling this still
                // in-flight tool call) must propagate as a real cancellation, not be swallowed into a
                // "graceful" ServerError tool result here. RunChatToolLoopAsync's own tool-execution
                // loop relies on exactly this exception reaching it so it can truncate the now-
                // orphaned tool_calls round out of history; converting it to a normal-looking
                // completed ToolResult would let the round "succeed" and leave this call's id (which
                // this cancelled response never actually answered) orphaned in history for the next
                // request. FetchRecordsAsync already distinguishes this from an internal timeout via
                // its own `when (!cancellationToken.IsCancellationRequested)` guard, so this only
                // fires for real caller cancellation.
                throw;
            }
            catch (Exception exc)
            {
                _logger?.LogError(exc,
                    "Search failed unexpectedly for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}",
                    _personaId, _indexName, exc.GetType().Name, exc.Message);
                return ServerError("search_service_unavailable", "I had a little glitch looking that up — could you say that again?");
            }

            // #309 (R1): cache the RAW records (pre-formatting/pre-OOS-tagging) -- see the
            // comment above the cache lookup for why the formatted ToolResult itself must never
            // be cached.
            Cache.Put(cacheKey, records, _searchConfig.CacheTtlSeconds, _searchConfig.CacheMaxSize);
        }

        var results = records.Select(FormatRecord).ToList();
        var joined = string.Join("\n-----\n", results);
        var noResults = _promptLoader is { } pl && pl.ErrorMessages.TryGetValue("search_no_results", out var raw) && raw is string rawText
            ? rawText
            : "No matching menu entries found.";

        return new ToolResult(joined.Length > 0 ? joined : noResults, ToolResultDirection.ToServer);
    }

    private ToolResult ServerError(string errorKey, string fallback) =>
        new(_promptLoader?.RenderError(errorKey) ?? fallback, ToolResultDirection.ToServer);

    private string FormatRecord(JsonElement record)
    {
        var identifier = GetString(record, _config.IdentifierField) ?? GetString(record, "id") ?? "unknown";
        var itemName = GetString(record, "name") ?? "N/A";
        var category = GetString(record, "category") ?? "N/A";
        var sizeStr = FormatSizes(record);

        // #313 (Rick's review, 1.1): every persona prompt requires calling `search` BEFORE
        // `update_order`, so this is the model's first (and often only) exposure to the item's
        // name -- it previously saw only the raw catalog string (e.g. a trademarked "WIDGET®")
        // and had to improvise a pronunciation. The canonical name stays first (it
        // is what the model must still pass back to `update_order`); the spoken form is appended
        // so the model has a correct pronunciation to actually say out loud.
        var spokenName = _menu.Spoken(itemName);
        var nameForSpeech = spokenName != itemName ? $"{itemName} (say: {spokenName})" : itemName;
        var summary = $"[{identifier}]: Item: {nameForSpeech}, Category: {category}, Available Sizes: {sizeStr}";

        // Flag items affected by machine outages so the model knows not to recommend them --
        // data-driven off the item's own `requiresMachine` field (#73), never a keyword list.
        // #309 (R1): routed through this session's *effective* status (pack default with the
        // session's own operator override, if any, applied on top -- see
        // OrderState.EffectiveMachineStatus, the same one OrderToolExecutor.UpdateOrder already
        // checks) so a guest-visible OOS tag always agrees with what update_order will actually
        // do with the same item, instead of the raw, override-blind pack default. An unbound/
        // direct caller (no effectiveMachineStatus delegate, e.g. this class's own tests) falls
        // back to the pack default exactly as before.
        var machine = _menu.RequiresMachine(itemName);
        var machineStatus = !string.IsNullOrEmpty(machine)
            ? (_effectiveMachineStatus is not null ? _effectiveMachineStatus(machine) : _menu.MachineStatus(machine))
            : null;
        if (!string.IsNullOrEmpty(machine) && machineStatus == "down")
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
        string query, IReadOnlyList<string> selectFields, bool includeVector, bool semantic, string? filter, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_searchConfig.TimeoutSeconds));

        try
        {
            var body = BuildSearchBody(query, selectFields, includeVector, semantic, filter);
            var url = $"{_config.Endpoint.TrimEnd('/')}/indexes('{Uri.EscapeDataString(_indexName)}')" +
                      $"/docs/search.post.search?api-version={SearchEndpointConfig.ApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            var (headerName, headerValue) = await ResolveAuthHeaderAsync(timeoutCts.Token).ConfigureAwait(false);
            request.Headers.Add(headerName, headerValue);

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

    private object BuildSearchBody(string query, IReadOnlyList<string> selectFields, bool includeVector, bool semantic, string? filter)
    {
        var body = new Dictionary<string, object?>
        {
            ["search"] = query,
            ["top"] = _searchConfig.TopResults,
            ["select"] = string.Join(",", selectFields),
        };
        if (!string.IsNullOrEmpty(filter))
        {
            // Issue 165: data-plane search.post.search body key is "filter" (no "$"-prefix --
            // this isn't the OData query-string convention), an OData expression string exactly
            // like the one setup_search_index.py's own filterable "menuPeriod" field supports.
            body["filter"] = filter;
        }
        if (semantic)
        {
            body["queryType"] = "semantic";
            body["semanticConfiguration"] = _config.SemanticConfiguration;
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

    /// <summary>Chooses the outbound auth header for this Azure AI Search REST call --
    /// <c>api-key</c> when configured (the only mode before this), else a managed-identity bearer
    /// token via <see cref="_bearerTokenProvider"/> (falling back to the lazily-constructed real
    /// <see cref="DefaultAzureCredentialSearchTokenProvider"/> if none was injected), matching
    /// tools.py's own <c>DefaultAzureCredential</c> fallback and the
    /// <c>https://search.azure.com/.default</c> scope -- mirrors PR #140 R5's
    /// <c>RealtimeProcessor.ResolveUpstreamAuthHeaderAsync</c> one for one. A credential failure
    /// here throws out of this method and is caught by <see cref="ExecuteAsync"/>'s own catch-all,
    /// which reports a server-error <see cref="ToolResult"/> instead of tearing down the session --
    /// no token is ever logged. Internal (not private) purely so a unit test can exercise the
    /// selection without a real HTTP call or Azure credential.</summary>
    internal async Task<(string HeaderName, string HeaderValue)> ResolveAuthHeaderAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            return ("api-key", _config.ApiKey);
        }

        var provider = _bearerTokenProvider ?? DefaultAzureCredentialSearchTokenProvider.Instance.Value;
        var token = await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return ("Authorization", $"Bearer {token}");
    }
}
