using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Configuration;
using Backend.Ordering;
using Backend.Personas;
using Backend.Search;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

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

/// <summary>A fake <see cref="HttpMessageHandler"/> that throws instead of returning any
/// response -- used to simulate a genuine caller-side cancellation (e.g. a barge-in) arriving
/// mid-request, as opposed to <see cref="QueuedHttpHandler"/>'s scripted HTTP error responses.</summary>
internal sealed class ThrowingHttpHandler(Func<Exception> makeException) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw makeException();
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
        HttpMessageHandler handler, string personaId = "search-tool-tests", bool useSemanticRanker = false,
        string? menuMode = null, Persona? persona = null, Func<string, string?>? effectiveMachineStatus = null)
    {
        persona ??= DeltaFixture.Load();
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
        return new SearchTool(
            httpClient,
            menu,
            new SearchToolOptions(
                endpointConfig,
                searchConfig,
                PromptLoader: null,
                IndexName: "test-delta-menu-items",
                PersonaId: personaId,
                MenuMode: menuMode,
                EffectiveMachineStatus: effectiveMachineStatus),
            NullLogger<SearchTool>.Instance);
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

    /// <summary>#309 (R1), C# parity for tools.py's own `search()` effective-status fix: an item
    /// whose pack default machine status is "down" (test-delta's own delta_machine) must NOT be
    /// tagged OOS once THIS session's own OrderState carries a per-session override bringing that
    /// machine back "up" -- mirrors OrderToolExecutor.UpdateOrder's own
    /// OrderState.EffectiveMachineStatus check for the exact same item/machine (Rick's review:
    /// the two must always agree).</summary>
    [Fact]
    public async Task ExecuteAsync_PackDownButSessionOverridesMachineUp_ItemIsNotTaggedOos()
    {
        var persona = DeltaFixture.Load("test-delta");
        var order = PersonaOrderFactory.CreateOrderState(persona);
        Assert.True(order.SetMachineOverride("delta_machine", "up"));

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
        var tool = NewTool(
            handler, personaId: Guid.NewGuid().ToString("n"), persona: persona,
            effectiveMachineStatus: order.EffectiveMachineStatus);

        var result = await tool.ExecuteAsync(QueryArgs("shake"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("[OOS:", result.ToText(), StringComparison.Ordinal);
    }

    /// <summary>#309 (R1) counterpart: an item whose pack default machine status is "up"
    /// (test-beta's own soda_machine) MUST be tagged OOS once this session's own OrderState
    /// carries a per-session override taking that machine "down" -- the raw, override-blind pack
    /// default alone must never be trusted once a session has its own override in effect.</summary>
    [Fact]
    public async Task ExecuteAsync_PackUpButSessionOverridesMachineDown_ItemIsTaggedOos()
    {
        var persona = DeltaFixture.Load("test-beta");
        var order = PersonaOrderFactory.CreateOrderState(persona);
        Assert.True(order.SetMachineOverride("soda_machine", "down"));

        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """
            {
              "value": [
                {
                  "id": "beta-root-beer",
                  "name": "Beta Root Beer",
                  "category": "drinks",
                  "sizes": "[{\"size\": \"regular\", \"price\": 2.29}]"
                }
              ]
            }
            """);
        var tool = NewTool(
            handler, personaId: Guid.NewGuid().ToString("n"), persona: persona,
            effectiveMachineStatus: order.EffectiveMachineStatus);

        var result = await tool.ExecuteAsync(QueryArgs("root beer"), TestContext.Current.CancellationToken);

        Assert.Contains("[OOS: Soda machine is down]", result.ToText(), StringComparison.Ordinal);
    }

    /// <summary>#309 (R1) cache-safety regression: the SAME cached query, asked by two different
    /// sessions with DIFFERENT effective machine-status overrides in effect, must NOT share a
    /// stale OOS tag computed under the other session's override state -- only ONE HTTP round
    /// trip is made (the second call is a genuine cache hit on the RAW records), but each call's
    /// own formatting reflects ITS OWN caller's current effective status. Mirrors Unity's Python
    /// cache-safety test for `_search_cache` now storing raw records instead of a formatted
    /// ToolResult.</summary>
    [Fact]
    public async Task ExecuteAsync_CacheHit_IsReformattedAgainstTheCurrentCallersOwnOverrideState()
    {
        var persona = DeltaFixture.Load("test-beta"); // soda_machine pack default "operational"/up
        var personaId = Guid.NewGuid().ToString("n"); // shared cache key namespace for both calls
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """
            {
              "value": [
                {
                  "id": "beta-root-beer",
                  "name": "Beta Root Beer",
                  "category": "drinks",
                  "sizes": "[{\"size\": \"regular\", \"price\": 2.29}]"
                }
              ]
            }
            """);

        // First caller's own session has overridden soda_machine DOWN -- this call issues the
        // one real HTTP request and populates the cache with the RAW record.
        var downOrder = PersonaOrderFactory.CreateOrderState(persona);
        Assert.True(downOrder.SetMachineOverride("soda_machine", "down"));
        var downTool = NewTool(handler, personaId: personaId, persona: persona, effectiveMachineStatus: downOrder.EffectiveMachineStatus);
        var downResult = await downTool.ExecuteAsync(QueryArgs("root beer"), TestContext.Current.CancellationToken);
        Assert.Contains("[OOS: Soda machine is down]", downResult.ToText(), StringComparison.Ordinal);

        // Second caller's own session has NO override (pack default "up") and asks the exact
        // same query -- served from cache (still only one HTTP request total), but reformatted
        // fresh against THIS caller's own (un-overridden) effective status.
        var upOrder = PersonaOrderFactory.CreateOrderState(persona);
        var upTool = NewTool(handler, personaId: personaId, persona: persona, effectiveMachineStatus: upOrder.EffectiveMachineStatus);
        var upResult = await upTool.ExecuteAsync(QueryArgs("root beer"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("[OOS:", upResult.ToText(), StringComparison.Ordinal);
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

    /// <summary>#247 regression: a genuine caller-side cancellation (e.g. a barge-in cancelling
    /// this still in-flight tool call) must propagate as <see cref="OperationCanceledException"/>
    /// out of <see cref="SearchTool.ExecuteAsync"/>, NOT be swallowed into a normal-looking
    /// completed <see cref="ToolResult"/> by the generic catch-all. <see
    /// cref="Cascade.CascadeProcessorTests"/>'s own tool-execution-round truncation relies on
    /// exactly this exception reaching <c>RunChatToolLoopAsync</c>'s tool-call loop so it can
    /// remove the now-orphaned <c>tool_calls</c> round from history; swallowing it here would let
    /// the round "succeed" and leave this call's id orphaned in history for the next request --
    /// exactly the conformance-level bug this test was written to catch (and which the fake
    /// Azure AI Search endpoint used for the corresponding conformance row exposed). Mutation
    /// check: deleting <see cref="SearchTool"/>'s own <c>catch (OperationCanceledException) {
    /// throw; }</c> branch (letting the generic <c>catch (Exception)</c> absorb it again) fails
    /// this test.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancelsMidRequest_PropagatesOperationCanceledExceptionInsteadOfSwallowingIt()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ThrowingHttpHandler(() =>
        {
            cts.Cancel();
            return new TaskCanceledException("simulated barge-in cancellation mid-request");
        });
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.ExecuteAsync(QueryArgs("anything"), cts.Token));
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

    /// <summary>
    /// Rick's PR 166 round-1 review, required item 6: the OData filter <see cref="SearchTool"/>
    /// builds for a bound menu mode must include a trailing <c>menuPeriod eq ''</c> clause, so a
    /// period-less item (one with no <c>menuPeriod</c> of its own -- indexed by
    /// setup_search_index.py with the empty-string sentinel) is never excluded by a mode-filtered
    /// search, matching <c>MenuCatalog.ItemAvailableNow</c>'s own always-available treatment of
    /// the same item. C# port of app/backend/tests/test_tool_calling.py's
    /// <c>SearchModeFilterTests</c>.
    /// </summary>
    [Theory]
    [InlineData("breakfast")]
    [InlineData("lunch")]
    public async Task ExecuteAsync_BoundMenuMode_SendsFilterAdmittingAllDayAndPeriodlessItems(string mode)
    {
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """{"value": []}""");
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"), menuMode: mode);

        await tool.ExecuteAsync(QueryArgs("anything"), TestContext.Current.CancellationToken);

        Assert.Single(handler.RequestBodies);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal(
            $"menuPeriod eq '{mode}' or menuPeriod eq 'allDay' or menuPeriod eq ''",
            body.RootElement.GetProperty("filter").GetString());
    }

    /// <summary>
    /// Cross-checks <c>MenuCatalog.ItemAvailableNow</c> (the add-time gate) against the REAL
    /// filter <see cref="SearchTool"/> sends, for the exact same period-less items test-delta
    /// ships (<c>Delta Meal</c>, <c>Delta Burger</c>, <c>Delta Fries</c>, <c>Delta Latte</c>,
    /// <c>Delta Extra Shot</c>, <c>Delta Shake</c>): both must agree a period-less item is never
    /// excluded by mode, in either direction -- the actual parity R6 asks for.
    /// </summary>
    [Theory]
    [InlineData("Delta Meal", "breakfast")]
    [InlineData("Delta Meal", "lunch")]
    [InlineData("Delta Burger", "breakfast")]
    [InlineData("Delta Burger", "lunch")]
    [InlineData("Delta Fries", "breakfast")]
    [InlineData("Delta Fries", "lunch")]
    [InlineData("Delta Latte", "breakfast")]
    [InlineData("Delta Latte", "lunch")]
    [InlineData("Delta Extra Shot", "breakfast")]
    [InlineData("Delta Extra Shot", "lunch")]
    [InlineData("Delta Shake", "breakfast")]
    [InlineData("Delta Shake", "lunch")]
    public async Task ExecuteAsync_PeriodlessItemIsAdmittedByBothTheAddGateAndTheSearchFilter(string itemName, string mode)
    {
        var persona = DeltaFixture.Load();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        Assert.True(menu.ItemAvailableNow(itemName, mode));

        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.OK, """{"value": []}""");
        var tool = NewTool(handler, personaId: Guid.NewGuid().ToString("n"), menuMode: mode);
        await tool.ExecuteAsync(QueryArgs(itemName), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Contains("menuPeriod eq ''", body.RootElement.GetProperty("filter").GetString());
    }
}
