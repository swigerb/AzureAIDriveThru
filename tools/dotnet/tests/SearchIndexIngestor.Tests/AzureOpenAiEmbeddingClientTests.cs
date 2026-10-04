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
}
