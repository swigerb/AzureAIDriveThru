using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tools;
using Xunit;

namespace Backend.Tests.Realtime;

/// <summary>A fake bearer token provider so <see cref="UpstreamAuthHeaderTests"/> never touches a
/// real Azure credential.</summary>
internal sealed class FakeBearerTokenProvider(string token) : IUpstreamBearerTokenProvider
{
    public int CallCount { get; private set; }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(token);
    }
}

/// <summary>PR #140 R5 (Rick's round-2 review): the upstream connect must keep using `api-key`
/// when one is configured, and must fall back to a managed-identity bearer token
/// (`Authorization: Bearer &lt;token&gt;`) only when it is empty. These tests exercise
/// <see cref="RealtimeProcessor.ResolveUpstreamAuthHeaderAsync"/> directly (internal, exposed via
/// InternalsVisibleTo) so the selection is provable without a real ClientWebSocket connect or a
/// real DefaultAzureCredential.</summary>
public sealed class UpstreamAuthHeaderTests
{
    private static RealtimeProcessor CreateProcessor(string upstreamApiKey, IUpstreamBearerTokenProvider? bearerTokenProvider) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: upstreamApiKey,
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>(),
            toolExecutor: new StubToolExecutor([]),
            bearerTokenProvider: bearerTokenProvider);

    [Fact]
    public async Task ApiKeyConfigured_UsesApiKeyHeader_AndNeverCallsTheTokenProvider()
    {
        var provider = new FakeBearerTokenProvider("should-not-be-used");
        var processor = CreateProcessor(upstreamApiKey: "sk-real-key", provider);

        var (name, value) = await processor.ResolveUpstreamAuthHeaderAsync(CancellationToken.None);

        Assert.Equal("api-key", name);
        Assert.Equal("sk-real-key", value);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task NoApiKeyConfigured_FallsBackToManagedIdentityBearerToken()
    {
        var provider = new FakeBearerTokenProvider("fake-aad-token");
        var processor = CreateProcessor(upstreamApiKey: string.Empty, provider);

        var (name, value) = await processor.ResolveUpstreamAuthHeaderAsync(CancellationToken.None);

        Assert.Equal("Authorization", name);
        Assert.Equal("Bearer fake-aad-token", value);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task NoApiKeyConfigured_RequestsAFreshTokenPerConnection()
    {
        var provider = new FakeBearerTokenProvider("fake-aad-token");
        var processor = CreateProcessor(upstreamApiKey: string.Empty, provider);

        await processor.ResolveUpstreamAuthHeaderAsync(CancellationToken.None);
        await processor.ResolveUpstreamAuthHeaderAsync(CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }
}
