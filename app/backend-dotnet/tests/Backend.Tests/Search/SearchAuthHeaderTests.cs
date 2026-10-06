using System.Net;
using Backend.Configuration;
using Backend.Ordering;
using Backend.Search;
using Backend.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

/// <summary>Captures every log entry a <see cref="SearchTool"/> emits so R5's logging tests can
/// assert both that a failure was actually logged AND that no entry ever contains the bearer
/// token or api-key -- Rick's PR #149 R5 review.</summary>
internal sealed class RecordingLogger : ILogger<SearchTool>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>Issue #14 (Search tool auth follow-up, mirroring PR #140 R5's realtime upstream
/// bearer-token fallback): the search call must keep using the REST <c>api-key</c> header when
/// <c>AZURE_SEARCH_API_KEY</c> is configured, and must fall back to a real managed-identity bearer
/// token when it is empty -- same shape as #140's own
/// <see cref="Backend.Sessions.RealtimeProcessor.ResolveUpstreamAuthHeaderAsync"/>:
/// <c>Authorization: Bearer &lt;token&gt;</c>. These tests exercise
/// <see cref="SearchTool.ResolveAuthHeaderAsync"/> directly (internal, exposed via
/// InternalsVisibleTo) so the selection is provable without a real HTTP call or a real
/// <c>DefaultAzureCredential</c>.</summary>
public sealed class SearchAuthHeaderTests
{
    private static SearchTool NewTool(
        string apiKey, ISearchBearerTokenProvider? bearerTokenProvider,
        HttpMessageHandler? handler = null, ILogger<SearchTool>? logger = null)
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
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        return new SearchTool(
            httpClient, endpointConfig, searchConfig, menu, promptLoader: null,
            "test-delta-menu-items", "search-auth-header-tests", logger ?? NullLogger<SearchTool>.Instance,
            bearerTokenProvider);
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
        // The header value is the real bearer token (same as #140's upstream fallback,
        // RealtimeProcessor.ResolveUpstreamAuthHeaderAsync) -- it is what is actually sent on
        // the wire, not just a log line.
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
    /// token is ever logged either way (Rick's PR #149 R5 review: pin that a failure IS logged,
    /// and that the log never carries a secret).</summary>
    [Fact]
    public async Task TokenProviderThrows_IsSurfacedAsTheGenericApologyFallback_NotAnUnhandledException()
    {
        var logger = new RecordingLogger();
        var tool = NewTool(apiKey: string.Empty, new ThrowingSearchBearerTokenProvider(), logger: logger);
        var args = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["query"] = "anything" });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        // Falls into ExecuteAsync's generic catch-all (not the SearchApiException-specific
        // branches, since a credential failure never reaches the HTTP call at all) -- same
        // "I had a little glitch" apology tools.py's own generic exception handler falls back to.
        Assert.Equal("I had a little glitch looking that up \u2014 could you say that again?", result.ToText());

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(nameof(InvalidOperationException)));
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("Bearer", e.Message);
            Assert.DoesNotContain("api-key", e.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Rick's PR #149 R2/R5 review: the token itself must never reach a log line, even
    /// when a real token WAS successfully minted and the failure happens later (the HTTP call
    /// itself 500s) -- a strictly stronger pin than the credential-failure test above, where no
    /// token was ever minted at all.</summary>
    [Fact]
    public async Task WorkingTokenProvider_HttpCallFails_LogsTheFailure_ButNeverTheToken()
    {
        var logger = new RecordingLogger();
        const string secretToken = "super-secret-managed-identity-token";
        var handler = new QueuedHttpHandler().Enqueue(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""");
        var tool = NewTool(apiKey: string.Empty, new FakeSearchBearerTokenProvider(secretToken), handler, logger);
        var args = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["query"] = "anything" });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal("I'm sorry, I can't reach our menu data right now.", result.ToText());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain(secretToken, e.Message);
            Assert.DoesNotContain("Bearer " + secretToken, e.Message);
        });
    }
}
