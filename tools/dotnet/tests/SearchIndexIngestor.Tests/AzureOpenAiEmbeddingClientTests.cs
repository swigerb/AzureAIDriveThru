using System.Net;
using System.Text.Json.Nodes;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// C#-only unit tests for <see cref="AzureOpenAiEmbeddingClient"/> -- the one piece of this port's
/// REST surface that is NOT captured against a live Python trace (see its own remarks on why: the
/// standard, versionless Azure OpenAI embeddings wire contract, and this task's "deterministic
/// fakes only in the test project" framing). These tests instead pin down this port's own request
/// construction and response parsing against a fake handler, so a regression here (wrong URL
/// shape, wrong body shape, trusting response array order instead of each entry's own "index")
/// still fails a test even without a live trace to compare against.
/// </summary>
public sealed class AzureOpenAiEmbeddingClientTests
{
    [Fact]
    public async Task GenerateEmbeddingsAsync_SendsExpectedUrlAndBody_AndSortsResultsByResponseIndex()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            // Deliberately returned out of order -- this port must sort by each entry's own
            // "index" field, not trust response array order.
            """{"data":[{"embedding":[0.2,0.3],"index":1},{"embedding":[0.0,0.1],"index":0}]}"""));
        using var client = new AzureOpenAiEmbeddingClient(new HttpClient(handler), "https://fake.openai.azure.com");

        var result = await client.GenerateEmbeddingsAsync(["first text", "second text"], "my-deployment", CancellationToken.None);

        Assert.Equal([0.0, 0.1], result[0]);
        Assert.Equal([0.2, 0.3], result[1]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://fake.openai.azure.com/openai/deployments/my-deployment/embeddings?api-version=2024-06-01",
            request.Uri);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(["first text", "second text"], body["input"]!.AsArray().Select(v => v!.GetValue<string>()));
        // Matches openai's own embeddings.create(input=texts, model=deployment) body shape
        // (Rick's review, item 4/round 2). "encoding_format" is a documented, accepted divergence
        // (see docs/dotnet_tooling.md) -- deliberately NOT asserted here as present.
        Assert.Equal("my-deployment", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_EscapesTheDeploymentNameInTheUrl()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK, """{"data":[{"embedding":[0.0],"index":0}]}"""));
        using var client = new AzureOpenAiEmbeddingClient(new HttpClient(handler), "https://fake.openai.azure.com");

        await client.GenerateEmbeddingsAsync(["text"], "deployment with spaces", CancellationToken.None);

        Assert.Contains("deployment%20with%20spaces", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_Throws_OnNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.PlainText(HttpStatusCode.Unauthorized, "denied"));
        using var client = new AzureOpenAiEmbeddingClient(new HttpClient(handler), "https://fake.openai.azure.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GenerateEmbeddingsAsync(["text"], "my-deployment", CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public void CreateProductionHttpClient_ConfiguresOpenAiSdkDefaultTimeouts()
    {
        // Rick's review, item 2b: openai==1.109.1's own pinned Timeout(connect=5, read=600,
        // write=600, pool=600). This port represents that single 600 s "overall" budget via
        // HttpClient.Timeout (see AzureOpenAiEmbeddingClient.CreateForProduction's own remarks).
        using var http = AzureOpenAiEmbeddingClient.CreateProductionHttpClient(new FakeTokenCredential());

        Assert.Equal(TimeSpan.FromSeconds(600), http.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), AzureOpenAiEmbeddingClient.ProductionConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(600), AzureOpenAiEmbeddingClient.ProductionOverallTimeout);
    }

    [Fact]
    public async Task CreateProductionHttpClient_RetriesATransient429_ThenSucceeds()
    {
        // End-to-end proof that CreateForProduction's handler chain (OpenAiRetryHandler wrapping
        // the bearer-token handler) actually retries -- not just that OpenAiRetryHandler's own unit
        // tests pass in isolation.
        var attempts = 0;
        var innerHandler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? FakeHttpMessageHandler.PlainText(HttpStatusCode.TooManyRequests, "slow down")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.0],"index":0}]}""");
        });
        var retryHandler = new OpenAiRetryHandler(innerHandler, delay: (_, _) => Task.CompletedTask);
        using var http = new HttpClient(retryHandler);
        using var client = new AzureOpenAiEmbeddingClient(http, "https://fake.openai.azure.com");

        await client.GenerateEmbeddingsAsync(["text"], "my-deployment", CancellationToken.None);

        Assert.Equal(2, attempts);
    }
}
