using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Configuration;
using Backend.Ordering;
using Backend.Search;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Search;

/// <summary>A canned/queued-response <see cref="HttpMessageHandler"/> stand-in for the real Azure
/// AI Search REST endpoint (conformance's own <c>FakeSearchServer</c> plays the same role for the
/// full end-to-end harness) -- records every request it receives so tests can assert exactly how
/// many round trips a given <see cref="SearchTool"/> call made (e.g. the cache/retry-cascade
/// tests below).</summary>
internal sealed class QueuedHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    public List<string> RequestBodies { get; } = [];

    public QueuedHttpHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }
        var (status, body) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.InternalServerError, """{"error":{"message":"no queued response"}}""");
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// Direct C# port of representative app/backend/tests/test_tools_search.py scenarios (#14/#23/
/// #37): result formatting (incl. the double-encoded ``sizes`` JSON-string quirk and the
/// machine-OOS suffix), the process-wide result cache, and the field-mismatch fallback-retry
/// cascade -- exercised against a queued fake <see cref="HttpMessageHandler"/> standing in for the
/// real Azure AI Search REST endpoint (same request/response shape <c>FakeSearchServer</c> speaks
/// for the full conformance harness).
/// </summary>
public sealed class SearchToolTests
{
    private static SearchTool NewTool(
        QueuedHttpHandler handler, string personaId = "search-tool-tests", bool useSemanticRanker = false)
    {
        var persona = DeltaFixture.Load();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var searchConfig = SearchConfig.FromAppConfig(AppConfig.Load());
        var endpointConfig = new SearchEndpointConfig(
            endpoint: "https://fake-search.example.com",
            apiKey: "test-key",
            semanticConfiguration: "menuSemanticConfig",
            identifierField: "id",
            contentField: "description",
            embeddingField: "embedding",
            useVectorQuery: true,
            useSemanticRanker: useSemanticRanker); // false keeps the happy-path/cache/no-results
                                                    // tests simple; the semantic-ranker tests below
                                                    // (Rick's PR #149 R2 review) opt it back in.
        var httpClient = new HttpClient(handler);
        return new SearchTool(httpClient, endpointConfig, searchConfig, menu, promptLoader: null, "test-delta-menu-items", personaId);
    }

    private static JsonElement QueryArgs(string query) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["query"] = query });

    [Fact]
    public async Task ExecuteAsync_FormatsRecordsIncludingDoubleEncodedSizesAndMachineOosSuffix()
    {
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """
            {
              "value": [
                {
                  "id": "delta-shake",
                  "name": "Delta Shake",
                  "category": "drinks",
                  "sizes": "[{\"size\": \"regular\", \"price\": 2.99}]"
                }
              ]
            }
            """);
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        var result = await tool.ExecuteAsync(QueryArgs("shake"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        var text = result.ToText();
        Assert.Contains("[delta-shake]", text);
        Assert.Contains("Item: Delta Shake", text);
        Assert.Contains("Category: drinks", text);
        Assert.Contains("Regular ($2.99)", text);
        // Delta Shake's own requiresMachine (delta_machine) is "down" in this fixture pack.
        Assert.Contains("[OOS: Delta machine is down]", text);
    }

    [Fact]
    public async Task ExecuteAsync_NoValueRecords_ReturnsNoResultsFallback()
    {
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """{"value": []}""");
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        var result = await tool.ExecuteAsync(QueryArgs("nothing matches this"), TestContext.Current.CancellationToken);

        Assert.Equal("No matching menu entries found.", result.ToText());
    }

    [Fact]
    public async Task ExecuteAsync_SecondCallWithSameQuery_IsServedFromCacheNotASecondRequest()
    {
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"value": [{"id": "delta-latte", "name": "Delta Latte", "category": "drinks", "sizes": "[]"}]}
            """);
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        var first = await tool.ExecuteAsync(QueryArgs("latte"), TestContext.Current.CancellationToken);
        var second = await tool.ExecuteAsync(QueryArgs("LATTE  "), TestContext.Current.CancellationToken); // same key: trim+lower

        Assert.Equal(first.ToText(), second.ToText());
        Assert.Single(handler.RequestBodies); // only ONE HTTP round trip for both calls
    }

    [Fact]
    public async Task ExecuteAsync_FieldMismatch400_RetriesWithMinimalSelectAndSucceeds()
    {
        var handler = new QueuedHttpHandler()
            .Enqueue(HttpStatusCode.BadRequest, """
                {"error":{"code":"InvalidRequestParameter","message":"Could not find a property named 'sizes' on type 'search.document'."}}
                """)
            .Enqueue(HttpStatusCode.OK, """
                {"value": [{"id": "delta-burger", "description": "Test Delta's plain burger."}]}
                """);
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        var result = await tool.ExecuteAsync(QueryArgs("burger"), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.RequestBodies.Count);
        // The retry's own `select` is the minimal [identifierField, contentField] projection.
        using var retryBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("id,description", retryBody.RootElement.GetProperty("select").GetString());
        Assert.Contains("[delta-burger]", result.ToText());
    }

    [Fact]
    public async Task ExecuteAsync_EveryAttemptFails_ReturnsTheGenericApologyFallback()
    {
        var handler = new QueuedHttpHandler()
            .Enqueue(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""")
            .Enqueue(HttpStatusCode.InternalServerError, """{"error":{"message":"boom again"}}""");
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        var result = await tool.ExecuteAsync(QueryArgs("anything"), TestContext.Current.CancellationToken);

        Assert.Equal("I'm sorry, I can't reach our menu data right now.", result.ToText());
    }

    /// <summary>Rick's PR #149 R2 review: the Search Documents REST request body property is
    /// <c>semanticConfiguration</c> (docs/search.post.search), not the Python SDK's own
    /// <c>semantic_configuration_name</c> keyword argument name -- pins the fixed request body
    /// so the wrong key (which the real service either ignores or 400s on, silently absorbed by
    /// the semantic-retry branch below) can't regress unnoticed. Mutation check: reverting
    /// <see cref="SearchTool"/>'s key back to <c>semanticConfigurationName</c> fails this test.</summary>
    [Fact]
    public async Task ExecuteAsync_SemanticRankerEnabled_SendsQueryTypeSemanticAndSemanticConfiguration()
    {
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """{"value": []}""");
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"), useSemanticRanker: true);

        await tool.ExecuteAsync(QueryArgs("shake"), TestContext.Current.CancellationToken);

        Assert.Single(handler.RequestBodies);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("semantic", body.RootElement.GetProperty("queryType").GetString());
        Assert.Equal("menuSemanticConfig", body.RootElement.GetProperty("semanticConfiguration").GetString());
        Assert.False(body.RootElement.TryGetProperty("semanticConfigurationName", out _),
            "The request body must never carry the Python-SDK-only 'semanticConfigurationName' key.");
    }

    /// <summary>The retry-cascade test <see cref="SearchToolTests.NewTool"/>'s own doc comment
    /// promises (previously unimplemented -- Rick's PR #149 R2 review): the service rejects the
    /// semantic query even though configuration says it's available, so the tool retries once
    /// without the ranker rather than failing the lookup outright.</summary>
    [Fact]
    public async Task ExecuteAsync_SemanticRankerRejectedByService_RetriesWithoutSemanticAndSucceeds()
    {
        var handler = new QueuedHttpHandler()
            .Enqueue(HttpStatusCode.BadRequest, """
                {"error":{"code":"InvalidRequestParameter","message":"semantic ranker is not available for this service"}}
                """)
            .Enqueue(HttpStatusCode.OK, """
                {"value": [{"id": "delta-latte", "name": "Delta Latte", "category": "drinks", "sizes": "[]"}]}
                """);
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"), useSemanticRanker: true);

        var result = await tool.ExecuteAsync(QueryArgs("latte"), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.RequestBodies.Count);
        using var retryBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(retryBody.RootElement.TryGetProperty("queryType", out _),
            "The semantic-ranker retry must drop queryType entirely, not just the configuration key.");
        Assert.False(retryBody.RootElement.TryGetProperty("semanticConfiguration", out _));
        Assert.Contains("[delta-latte]", result.ToText());
    }
}
