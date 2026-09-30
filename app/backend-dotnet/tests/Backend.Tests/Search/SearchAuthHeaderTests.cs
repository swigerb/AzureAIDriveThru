using Backend.Configuration;
using Backend.Ordering;
using Backend.Search;
using Backend.Tests.TestSupport;
using Xunit;

namespace Backend.Tests.Search;

/// <summary>A fake bearer token provider so <see cref="SearchAuthHeaderTests"/> never touches a
/// real Azure credential -- same shape as Realtime's own <c>FakeBearerTokenProvider</c>.</summary>
internal sealed class FakeSearchBearerTokenProvider(string token) : ISearchBearerTokenProvider
{
    public int CallCount { get; private set; }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(token);
    }
}

/// <summary>Simulates <c>DefaultAzureCredential</c> throwing when the managed identity is not
/// ready yet (MI not attached, RBAC not propagated, IMDS timeout) -- same shape as Realtime's own
/// <c>ThrowingBearerTokenProvider</c> (PR #140 round-2 review, N1).</summary>
internal sealed class ThrowingSearchBearerTokenProvider : ISearchBearerTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("fake: credential unavailable");
}

/// <summary>Issue #14 (Search tool auth follow-up, mirroring PR #140 R5's realtime upstream
/// bearer-token fallback): the search call must keep using the REST <c>api-key</c> header when
/// <c>AZURE_SEARCH_API_KEY</c> is configured, and must fall back to a real managed-identity bearer
/// token (unlike #140's own upstream fallback, this one is never masked -- an
/// <c>Authorization: Bearer &lt;token&gt;</c> header only works with the real value) only when it
/// is empty. These tests exercise <see cref="SearchTool.ResolveAuthHeaderAsync"/> directly
/// (internal, exposed via InternalsVisibleTo) so the selection is provable without a real HTTP call
/// or a real <c>DefaultAzureCredential</c>.</summary>
public sealed class SearchAuthHeaderTests
{
    private static SearchTool NewTool(string apiKey, ISearchBearerTokenProvider? bearerTokenProvider)
    {
        var persona = DeltaFixture.Load();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var searchConfig = SearchConfig.FromAppConfig(AppConfig.Load());
        var endpointConfig = new SearchEndpointConfig(
            endpoint: "https://fake-search.example.com",
            apiKey: apiKey,
            semanticConfiguration: "menuSemanticConfig",
            identifierField: "id",
            contentField: "description",
            embeddingField: "embedding",
            useVectorQuery: true,
            useSemanticRanker: false);
        var httpClient = new HttpClient();
        return new SearchTool(
            httpClient, endpointConfig, searchConfig, menu, promptLoader: null,
            "test-delta-menu-items", "search-auth-header-tests", bearerTokenProvider);
    }

    [Fact]
    public async Task ApiKeyConfigured_UsesApiKeyHeader_AndNeverCallsTheTokenProvider()
    {
        var provider = new FakeSearchBearerTokenProvider("should-not-be-used");
        var tool = NewTool(apiKey: "sk-real-search-key", provider);

        var (name, value) = await tool.ResolveAuthHeaderAsync(CancellationToken.None);

        Assert.Equal("api-key", name);
        Assert.Equal("sk-real-search-key", value);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task NoApiKeyConfigured_FallsBackToTheRealManagedIdentityBearerToken()
    {
        var provider = new FakeSearchBearerTokenProvider("fake-aad-token");
        var tool = NewTool(apiKey: string.Empty, provider);

        var (name, value) = await tool.ResolveAuthHeaderAsync(CancellationToken.None);

        Assert.Equal("Authorization", name);
        // Unlike PR #140 R5's realtime upstream fallback (which returns a literal "******"), the
        // search bearer header must carry the real token -- it is the value sent on the wire, not
        // just a log line.
        Assert.Equal("Bearer fake-aad-token", value);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task NoApiKeyConfigured_RequestsAFreshTokenPerCall()
    {
        var provider = new FakeSearchBearerTokenProvider("fake-aad-token");
        var tool = NewTool(apiKey: string.Empty, provider);

        await tool.ResolveAuthHeaderAsync(CancellationToken.None);
        await tool.ResolveAuthHeaderAsync(CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    /// <summary>A credential failure must be caught by <see cref="SearchTool.ExecuteAsync"/>'s
    /// existing catch-all so a guest gets a graceful apology instead of the session dying -- no
    /// token is ever logged either way.</summary>
    [Fact]
    public async Task TokenProviderThrows_IsSurfacedAsTheGenericApologyFallback_NotAnUnhandledException()
    {
        var tool = NewTool(apiKey: string.Empty, new ThrowingSearchBearerTokenProvider());
        var args = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["query"] = "anything" });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        // Falls into ExecuteAsync's generic catch-all (not the SearchApiException-specific
        // branches, since a credential failure never reaches the HTTP call at all) -- same
        // "I had a little glitch" apology tools.py's own generic exception handler falls back to.
        Assert.Equal("I had a little glitch looking that up \u2014 could you say that again?", result.ToText());
    }
}
