using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Conformance.Fakes;

/// <summary>
/// A Kestrel-hosted fake of the Azure AI Search REST surface the backend's
/// `azure-search-documents` client calls (POST /indexes('{name}')/docs/search.post.search).
/// Answers from each persona pack's own `menu/menuItems.json` (via <see cref="MenuIndex"/>)
/// rather than a real index, and accepts the exact body shape the SDK sends: `search`,
/// `queryType`, `semanticConfiguration`, `select` (comma-joined string), `top`, and
/// `vectorQueries`.
///
/// Issue #76 part 2: this is now MULTI-index -- one Kestrel host answering every persona's own
/// index name (see <see cref="MenuIndex.ResolveIndexPaths"/>) with only that SAME persona's own
/// menu documents, routed by the route's own `indexName` (previously ignored entirely: every
/// index name, real or not, answered from one Sonic-only document set). This is what the real
/// backend's per-persona `SearchClient` (app/backend/app.py's `persona_search_contexts`, one
/// client per enabled persona pointed at its own `persona.manifest.search.indexName`) actually
/// depends on to prove cross-persona search isolation end to end -- see
/// PersonaSearchIsolationConformanceTests.cs.
/// </summary>
public sealed class FakeSearchServer : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, string> _indexNameToMenuItemsJsonPath;
    private WebApplication? _app;
    private IReadOnlyDictionary<string, IReadOnlyList<MenuDocument>> _documentsByIndex =
        new Dictionary<string, IReadOnlyList<MenuDocument>>(StringComparer.Ordinal);

    public FrameLog ReceivedRequests { get; } = new();

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    public string? LastApiKeyHeader { get; private set; }

    /// <summary>
    /// Issue #9 / harness follow-up #23: opt-in, one-shot field-name-mismatch simulation. When
    /// set to a field name (e.g. "sizes"), the *next* request whose `select` list contains that
    /// field is answered with HTTP 400 and an Azure-AI-Search-shaped error body whose message
    /// contains "Could not find a property named '&lt;field&gt;'" — the exact substring
    /// app/backend/tools.py's `search()` matches on to trigger its fallback retry with a minimal
    /// `select`. The flag clears itself immediately after firing once, so the retry (which asks
    /// for a different, always-present field set) and every other unrelated request/scenario
    /// succeed normally. Defaults to null (inert) — no existing scenario's behavior changes.
    ///
    /// Backed by <see cref="_rejectSelectFieldOnce"/> and consumed via
    /// <see cref="Interlocked.CompareExchange{T}"/> in <see cref="HandleSearchAsync"/> rather than
    /// a plain read-then-clear (PR #38 review item 8): Kestrel can process two requests
    /// concurrently, and a read-then-clear would let both see the flag armed and both claim to
    /// have triggered the one-shot 400, or (worse) let the second request silently swallow a flag
    /// meant for a different, later scenario's request. The compare-exchange guarantees only the
    /// one request that actually observes the still-armed value can clear it.
    /// </summary>
    public string? RejectSelectFieldOnce
    {
        get => Volatile.Read(ref _rejectSelectFieldOnce);
        set => Volatile.Write(ref _rejectSelectFieldOnce, value);
    }
    private string? _rejectSelectFieldOnce;

    /// <summary>
    /// Asserts <see cref="RejectSelectFieldOnce"/> is not still armed (PR #38 review item 8,
    /// mirrors <see cref="FakeRealtimeUpstreamServer.AssertNoPendingOneShotSwitches"/>). A
    /// scenario that sets this flag and then never actually sends a matching search request
    /// (an assertion failing early, a copy-paste mistake) would otherwise leave it armed to
    /// silently reject an unrelated later scenario's search request instead — called from
    /// <see cref="Conformance.Tests.ConformanceFixture.RunAsync"/> before every scenario body
    /// runs, exactly like the realtime server's equivalent check.
    /// </summary>
    /// <exception cref="InvalidOperationException">A previous scenario armed
    /// <see cref="RejectSelectFieldOnce"/> but it was never consumed by a matching request.</exception>
    public void AssertNoPendingOneShotSwitches()
    {
        var pending = Volatile.Read(ref _rejectSelectFieldOnce);
        if (pending is not null)
        {
            throw new InvalidOperationException(
                $"RejectSelectFieldOnce(\"{pending}\") was armed but never consumed by a matching " +
                "search request in the scenario that set it -- a previous scenario likely set this " +
                "but never actually sent a request whose select list contained that field " +
                "afterwards, leaving it armed to silently reject an unrelated later scenario's " +
                "search request instead.");
        }
    }

    /// <param name="indexNameToMenuItemsJsonPath">Every Azure AI Search index name this fake
    /// should answer, mapped to that SAME index's own `menu/menuItems.json` path -- typically
    /// built via <see cref="MenuIndex.ResolveIndexPaths"/> from whichever personas the fixture's
    /// backend is actually launched with, so the fake never loads (and can never accidentally
    /// leak) a persona's documents under an index name the backend never queries.</param>
    public FakeSearchServer(IReadOnlyDictionary<string, string> indexNameToMenuItemsJsonPath)
    {
        _indexNameToMenuItemsJsonPath = indexNameToMenuItemsJsonPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default, int? fixedPort = null)
    {
        _documentsByIndex = _indexNameToMenuItemsJsonPath.ToDictionary(
            kv => kv.Key, kv => MenuIndex.Load(kv.Value), StringComparer.Ordinal);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{fixedPort?.ToString() ?? "0"}");
        var app = builder.Build();
        app.MapGet("/", () => Results.Ok());
        app.MapPost("/indexes('{indexName}')/docs/search.post.search", HandleSearchAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseUri = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleSearchAsync(HttpContext context, string indexName)
    {
        LastApiKeyHeader = context.Request.Headers["api-key"];
        if (string.IsNullOrEmpty(LastApiKeyHeader))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Issue #76 part 2: the whole point of the multi-index fake -- look the requested index
        // up in whatever set this fake was actually started with, rather than always answering
        // from one shared document set regardless of which index the caller's SearchClient
        // targeted. An index name this fake was never given (a disabled persona, a typo, or a
        // genuinely unknown index) 404s with an Azure-AI-Search-shaped error body, exactly like
        // the real service would for a nonexistent index -- never silently falls back to someone
        // else's documents.
        if (!_documentsByIndex.TryGetValue(indexName, out var documents))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json;odata.metadata=none";
            var notFoundBody = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "ResourceNotFound",
                    ["message"] = $"The index '{indexName}' for service was not found.",
                },
            };
            await context.Response.WriteAsync(notFoundBody.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using var requestDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
        var recorded = ReceivedRequests.Add(requestDoc.RootElement.Clone());

        var root = recorded.Json;
        var searchText = root.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var top = root.TryGetProperty("top", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 50;
        var selectFields = root.TryGetProperty("select", out var sel) && sel.ValueKind == JsonValueKind.String
            ? sel.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

        var rejectField = Volatile.Read(ref _rejectSelectFieldOnce);
        if (rejectField is not null && selectFields is not null &&
            selectFields.Contains(rejectField, StringComparer.OrdinalIgnoreCase) &&
            Interlocked.CompareExchange(ref _rejectSelectFieldOnce, null, rejectField) == rejectField)
        {
            // Won the race to consume the one-shot flag: only this request is rejected. A
            // concurrent request that lost the compare-exchange falls through to a normal
            // response instead of double-rejecting.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json;odata.metadata=none";
            var errorBody = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "InvalidRequestParameter",
                    ["message"] = $"Could not find a property named '{rejectField}' on type 'search.document'.",
                },
            };
            await context.Response.WriteAsync(errorBody.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var matches = Filter(documents, searchText).Take(top);

        var values = new JsonArray();
        foreach (var doc in matches)
        {
            values.Add(ProjectDocument(doc, selectFields));
        }

        var response = new JsonObject { ["value"] = values };
        context.Response.ContentType = "application/json;odata.metadata=none";
        await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
    }

    private static IEnumerable<MenuDocument> Filter(IReadOnlyList<MenuDocument> documents, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText) || searchText == "*")
        {
            return documents;
        }

        var terms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = documents.Where(d => terms.Any(term =>
            d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            d.Category.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

        // A real semantic/vector search never returns zero rows for a plausible menu question;
        // fall back to the full catalog so tools.py's happy path always has something to reason
        // over, matching how the real ranked/semantic index behaves for near-miss queries.
        return matched.Count > 0 ? matched : documents;
    }

    private static JsonObject ProjectDocument(MenuDocument doc, string[]? selectFields)
    {
        var all = new JsonObject
        {
            ["@search.score"] = 1.0,
            ["id"] = doc.Id,
            ["name"] = doc.Name,
            ["category"] = doc.Category,
            ["description"] = doc.Description,
            ["sizes"] = doc.SizesJson,
        };

        if (selectFields is null)
        {
            return all;
        }

        var projected = new JsonObject { ["@search.score"] = 1.0 };
        foreach (var field in selectFields)
        {
            if (all.TryGetPropertyValue(field, out var value))
            {
                projected[field] = value?.DeepClone();
            }
        }
        return projected;
    }
}
