using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace SearchIndexIngestor;

/// <summary>
/// The one production <see cref="IEmbeddingClient"/> implementation: a direct Azure OpenAI REST
/// call, authenticated the same way setup_search_index.py's own
/// <c>AzureOpenAI(azure_ad_token_provider=get_bearer_token_provider(DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"), ...)</c>
/// is (<c>run()</c>, lines 480-484) -- same credential type, same scope string, no API key ever
/// read or sent. Request/response shape (<c>POST .../embeddings?api-version=...</c>, body
/// <c>{"input": [...]}, response {"data": [{"embedding": [...], "index": N}, ...]}</c>) is the
/// standard, versionless Azure OpenAI embeddings wire contract (NOT independently captured against
/// a live trace the way the Search REST calls were, since this port's only Azure OpenAI dependency
/// is embeddings generation, which setup_search_index.py's own tests/this task's "deterministic
/// fakes only in the test project" framing never requires byte-matching -- see
/// docs/dotnet_tooling.md's caveat on this).
/// </summary>
public sealed class AzureOpenAiEmbeddingClient : IEmbeddingClient, IDisposable
{
    // The OpenAI SDK's own stable embeddings api-version as of this port -- a standalone constant
    // (not shared with SearchIndexHttpClient's Azure-AI-Search-specific one, which is unrelated).
    private const string ApiVersion = "2024-06-01";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly bool _ownsHttpClient;

    public AzureOpenAiEmbeddingClient(HttpClient httpClient, string endpoint, bool ownsHttpClient = false)
    {
        _http = httpClient;
        _endpoint = endpoint.TrimEnd('/');
        _ownsHttpClient = ownsHttpClient;
    }

    public static AzureOpenAiEmbeddingClient CreateForProduction(string endpoint, TokenCredential credential)
    {
        var handler = new BearerTokenHandler(credential, "https://cognitiveservices.azure.com/.default", new SocketsHttpHandler());
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        return new AzureOpenAiEmbeddingClient(http, endpoint, ownsHttpClient: true);
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts, string deployment, CancellationToken cancellationToken)
    {
        var url = $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(deployment)}/embeddings?api-version={ApiVersion}";
        var inputArray = new JsonArray();
        foreach (var text in texts)
        {
            inputArray.Add(JsonValue.Create(text));
        }
        var requestBody = new JsonObject { ["input"] = inputArray };

        using var response = await _http.PostAsJsonAsync(url, requestBody, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Azure OpenAI embeddings request failed with status {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {errorBody}");
        }

        var responseBody = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        var data = responseBody?["data"]?.AsArray() ??
            throw new InvalidOperationException("Azure OpenAI's embeddings response had no \"data\" array.");

        // Sorted defensively by "index" rather than trusting response array order -- slightly more
        // robust than Python's own code (which trusts the OpenAI SDK's already-sorted return order
        // outright), not a behavioural difference that's ever observable given a well-behaved
        // service.
        return data
            .OrderBy(item => item?["index"]?.GetValue<int>() ?? 0)
            .Select(item => (IReadOnlyList<double>)(item?["embedding"]?.AsArray()
                .Select(v => v!.GetValue<double>()).ToList() ??
                throw new InvalidOperationException("Azure OpenAI's embeddings response had an entry with no \"embedding\" array.")))
            .ToList();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
