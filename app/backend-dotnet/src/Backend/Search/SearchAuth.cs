using Azure.Core;
using Azure.Identity;

namespace Backend.Search;

/// <summary>
/// Real credential fallback for the Azure AI Search data-plane REST calls in <see
/// cref="SearchTool"/> -- mirrors PR #140 R5's upstream bearer-token fallback for the realtime
/// connect (<see cref="Backend.Realtime.IUpstreamBearerTokenProvider"/>/
/// <see cref="Backend.Realtime.DefaultAzureCredentialTokenProvider"/>), replacing the placeholder
/// documented in <see cref="SearchEndpointConfig"/>'s "Auth scope note": <c>AZURE_SEARCH_API_KEY</c>
/// when configured (the only mode before this), else a managed-identity bearer token via
/// <c>azure.identity.DefaultAzureCredential</c> and the Azure AI Search data-plane scope
/// (<c>https://search.azure.com/.default</c>). Kept behind an interface purely so <see
/// cref="SearchTool"/>'s header-selection logic can be unit tested without a real Azure credential
/// (a fake substitutes it in tests).
/// </summary>
public interface ISearchBearerTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Production implementation: wraps a <see cref="TokenCredential"/> (a real
/// <see cref="DefaultAzureCredential"/> by default) and requests the Azure AI Search data-plane
/// scope.</summary>
public sealed class DefaultAzureCredentialSearchTokenProvider : ISearchBearerTokenProvider
{
    // Azure AI Search's own data-plane scope -- the search equivalent of PR #140 R5's
    // "https://cognitiveservices.azure.com/.default" for the realtime upstream connect.
    private static readonly string[] Scopes = ["https://search.azure.com/.default"];

    private readonly TokenCredential _credential;

    public DefaultAzureCredentialSearchTokenProvider(TokenCredential? credential = null)
    {
        _credential = credential ?? new DefaultAzureCredential();
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken)
            .ConfigureAwait(false);
        return token.Token;
    }

    /// <summary>Lazily-constructed process-wide default, so a <see cref="DefaultAzureCredential"/>
    /// (which probes several credential sources on first token request) is only ever constructed
    /// if a search call actually needs it -- every test and every deployment with an api-key
    /// configured never touches this.</summary>
    public static readonly Lazy<DefaultAzureCredentialSearchTokenProvider> Instance =
        new(() => new DefaultAzureCredentialSearchTokenProvider());
}
