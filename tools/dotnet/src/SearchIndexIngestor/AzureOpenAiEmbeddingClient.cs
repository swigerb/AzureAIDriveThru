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

    /// <summary>openai 1.109.1's own default connect timeout (Rick's review, item 2b).</summary>
    public static readonly TimeSpan ProductionConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>openai 1.109.1's own default read/write/pool timeout (Rick's review, item 2b).</summary>
    public static readonly TimeSpan ProductionOverallTimeout = TimeSpan.FromSeconds(600);

    public static AzureOpenAiEmbeddingClient CreateForProduction(string endpoint, TokenCredential credential)
    {
        var http = CreateProductionHttpClient(credential);
        return new AzureOpenAiEmbeddingClient(http, endpoint, ownsHttpClient: true);
    }

    /// <summary>Factored out of <see cref="CreateForProduction"/> so tests can assert on the
    /// configured timeout/retry-handler shape directly, without constructing a real
    /// <c>DefaultAzureCredential</c>.
    ///
    /// Wraps <see cref="OpenAiRetryHandler"/> (Rick's review, item 2b: openai 1.109.1's own
    /// <c>max_retries=2</c>, retrying 408/409/429/5xx and connection errors) around a
    /// <see cref="SocketsHttpHandler"/> whose own <see cref="SocketsHttpHandler.ConnectTimeout"/> is
    /// <see cref="ProductionConnectTimeout"/> (5 s) and whose owning <see cref="HttpClient.Timeout"/>
    /// is <see cref="ProductionOverallTimeout"/> (600 s), matching the pinned openai SDK's own
    /// <c>Timeout(connect=5, read=600, write=600, pool=600)</c> (<c>HttpClient.Timeout</c> is the
    /// closest .NET equivalent to the SDK's combined read/write/pool timeout; .NET has no separate
    /// read-vs-write-vs-pool split the way <c>httpx</c>'s own transport does, so all three are
    /// represented by the one 600 s knob here deliberately).</summary>
    internal static HttpClient CreateProductionHttpClient(TokenCredential credential)
    {
        var socketsHandler = new SocketsHttpHandler { ConnectTimeout = ProductionConnectTimeout };
        var bearerHandler = new BearerTokenHandler(credential, "https://cognitiveservices.azure.com/.default", socketsHandler);
        var retryHandler = new OpenAiRetryHandler(bearerHandler);
        return new HttpClient(retryHandler) { Timeout = ProductionOverallTimeout };
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
        // Matches the real request the pinned openai==1.109.1 SDK sends (captured by Rick's
        // review, item 3): {"input": [...], "model": "<deployment>", "encoding_format": "base64"}.
        // "model" is included here (Python's embeddings.create(input=texts, model=deployment)); the
        // SDK's own "encoding_format": "base64" is deliberately NOT sent -- a documented, accepted
        // divergence (docs/dotnet_tooling.md): omitting it makes the raw REST API respond with
        // plain float JSON instead of a base64-encoded float32 buffer, which is numerically
        // equivalent at Edm.Single precision and avoids this port needing a base64-decode step for
        // no parity benefit (Azure routes purely on the deployment in the URL, never on this body's
        // "model" field, for an Azure OpenAI resource).
        var requestBody = new JsonObject { ["input"] = inputArray, ["model"] = deployment };

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
