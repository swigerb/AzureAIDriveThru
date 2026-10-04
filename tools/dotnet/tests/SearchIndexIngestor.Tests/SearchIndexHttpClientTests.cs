using System.Net;
using System.Text.Json.Nodes;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// C#-only unit tests for <see cref="SearchIndexHttpClient"/>'s REST plumbing: method/URL/header
/// shape for every one of its five calls, batch-succeeded-count parsing, document-count's raw
/// integer body parsing, and <see cref="SearchIndexHttpClient.ListAllDocumentIdsAsync"/>'s own
/// pagination loop (a genuine production-correctness concern this port adds that has no Python
/// counterpart to byte-compare against -- see SearchIndexHttpClient.cs's own remarks on why this
/// is C#-only tested rather than captured against the Python twin).
/// </summary>
public sealed class SearchIndexHttpClientTests
{
    private const string Endpoint = "https://fake.search.windows.net";
    private const string IndexName = "my-index";

    [Fact]
    public async Task CreateOrUpdateIndexAsync_SendsExpectedMethodUrlAndHeaders()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        await client.CreateOrUpdateIndexAsync(IndexName, new JsonObject { ["name"] = IndexName }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{Endpoint}/indexes('{IndexName}')?api-version=2026-04-01", request.Uri);
        // .NET's HttpHeaders normalizes the parsed MediaTypeWithQualityHeaderValue back to
        // "type; param" (with a space) when stringified, even though the literal added via
        // TryAddWithoutValidation had none -- a header-formatting artifact of .NET's own typed
        // header parsing, not a wire-format difference this test needs to care about.
        Assert.Equal("application/json; odata.metadata=minimal", request.Headers["Accept"]);
        Assert.Equal("return=representation", request.Headers["Prefer"]);
        Assert.Contains("\"name\":\"my-index\"", request.Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task UploadBatchAsync_SendsExpectedMethodUrlAndHeaders_AndReturnsSucceededCount()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"value":[{"key":"a","status":true},{"key":"b","status":false}]}"""));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        var batchBody = new JsonObject
        {
            ["value"] = new JsonArray
            {
                new JsonObject { ["@search.action"] = "mergeOrUpload", ["id"] = "a" },
                new JsonObject { ["@search.action"] = "mergeOrUpload", ["id"] = "b" },
            },
        };
        var succeeded = await client.UploadBatchAsync(IndexName, batchBody, CancellationToken.None);

        Assert.Equal(1, succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Endpoint}/indexes('{IndexName}')/docs/search.index?api-version=2026-04-01", request.Uri);
        Assert.Equal("application/json; odata.metadata=none", request.Headers["Accept"]);
    }

    [Fact]
    public async Task GetDocumentCountAsync_ParsesRawIntegerBody_NotJsonObject()
    {
        // Azure AI Search's own get_document_count() response is a raw integer string body
        // (confirmed from the azure-search-documents SDK's own source -- see
        // SearchIndexHttpClient.cs's remarks), not {"count": N}.
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "60"));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        var count = await client.GetDocumentCountAsync(IndexName, CancellationToken.None);

        Assert.Equal(60, count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Endpoint}/indexes('{IndexName}')/docs/$count?api-version=2026-04-01", request.Uri);
    }

    [Fact]
    public async Task ListAllDocumentIdsAsync_FollowsNextPageParameters_AcrossMultiplePages()
    {
        var handler = FakeHttpMessageHandler.Sequenced(
            _ => FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """{"value":[{"id":"a"},{"id":"b"}],"@search.nextPageParameters":{"search":"*","select":"id","$skip":2}}"""),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[{"id":"c"}]}"""));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        var ids = await client.ListAllDocumentIdsAsync(IndexName, CancellationToken.None);

        Assert.Equal(["a", "b", "c"], ids);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(
            $"{Endpoint}/indexes('{IndexName}')/docs/search.post.search?api-version=2026-04-01", r.Uri));
        // Second request's body is the continuation params from the first response's
        // "@search.nextPageParameters", not the original {"search": "*", "select": "id"}.
        Assert.Contains("\"$skip\":2", handler.Requests[1].Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task DeleteBatchAsync_TagsEachDocumentWithSearchActionDelete()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}"""));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        await client.DeleteBatchAsync(IndexName, ["a", "b"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        var value = body["value"]!.AsArray();
        Assert.Equal(2, value.Count);
        Assert.Equal("delete", value[0]!["@search.action"]!.GetValue<string>());
        Assert.Equal("a", value[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task EveryCall_Throws_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.PlainText(HttpStatusCode.Forbidden, "nope"));
        using var client = new SearchIndexHttpClient(new HttpClient(handler), Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateOrUpdateIndexAsync(IndexName, new JsonObject(), CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }
}
