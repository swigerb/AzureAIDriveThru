using System.Net.Http.Headers;
using Azure.Core;

namespace SearchIndexIngestor;

/// <summary>
/// Attaches a <c>DefaultAzureCredential</c>-issued bearer token to every outbound request, the
/// <see cref="HttpClient"/>/raw-REST equivalent of what <c>azure-core</c>'s own
/// <c>BearerTokenCredentialPolicy</c> (Search SDK) and <c>get_bearer_token_provider</c> (OpenAI SDK)
/// do for the real Python script -- see <c>run()</c> (setup_search_index.py lines 477-484): ONE
/// <c>DefaultAzureCredential()</c> instance, scoped per call site
/// (<c>https://search.azure.com/.default</c> for Azure AI Search,
/// <c>https://cognitiveservices.azure.com/.default</c> for Azure OpenAI -- the exact same second
/// scope string Python's own <c>get_bearer_token_provider(credential, "https://cognitiveservices.azure.com/.default")</c>
/// uses).
///
/// Caches the token until shortly before its expiry rather than re-authenticating on every one of
/// a run's many requests (one CLI invocation can issue index/upload/list/delete/count requests for
/// several personas) -- the same "don't refetch a still-valid token" behaviour
/// <c>get_bearer_token_provider</c>/<c>BearerTokenCredentialPolicy</c> both provide for their SDKs,
/// reimplemented here directly since raw <see cref="HttpClient"/> has no equivalent built in (see
/// SearchIndexHttpClient.cs's remarks for why this port uses raw HttpClient instead of the
/// Azure.Search.Documents/OpenAI SDK packages those policies come from).
/// </summary>
internal sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;
    private readonly object _gate = new();
    private AccessToken? _cachedToken;

    public BearerTokenHandler(TokenCredential credential, string scope, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _credential = credential;
        _scopes = [scope];
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        // A two-minute safety margin before the real expiry -- generous enough that a token never
        // expires mid-flight across this handler's own (uncontended, single CLI process) request
        // sequence, without re-authenticating on literally every request.
        lock (_gate)
        {
            if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return cached.Token;
            }
        }

        var fresh = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _cachedToken = fresh;
        }
        return fresh.Token;
    }
}
