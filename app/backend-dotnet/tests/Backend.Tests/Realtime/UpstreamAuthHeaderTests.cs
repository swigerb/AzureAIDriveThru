using System.Net.WebSockets;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

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

/// <summary>PR #140 round-2 review (N1): simulates <c>DefaultAzureCredential</c> throwing when the
/// managed identity is not ready (MI not attached yet, RBAC not propagated, IMDS timeout).</summary>
internal sealed class ThrowingBearerTokenProvider : IUpstreamBearerTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("fake: credential unavailable");
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
            logger: NullLogger<RealtimeProcessor>.Instance,
            rateLimitLogger: NullLogger<RateLimitRecovery>.Instance,
            nudgeLogger: NullLogger<NudgeScheduler>.Instance,
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

    /// <summary>PR #140 round-2 review (N1): a credential failure must be caught by the existing
    /// <c>try</c> around <c>upstream.ConnectAsync</c> so the guest gets an established 1011
    /// "Upstream connection failed", not an unhandled exception (1006). Mutation check: moving
    /// the <c>ResolveUpstreamAuthHeaderAsync</c>/<c>SetRequestHeader</c> lines back above the
    /// <c>try</c> makes this fail.</summary>
    [Fact]
    public async Task TokenProviderThrows_ClosesWith1011InsteadOfEscaping()
    {
        var processor = CreateProcessor(string.Empty, new ThrowingBearerTokenProvider());
        var socket = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());

        await processor.RunSessionAsync(
            socket,
            PersonaCatalog.Load().Default,
            new ResolvedModel("gpt-realtime-2.1", "realtime", "gpt-realtime-2.1", false),
            "s1",
            CancellationToken.None);

        Assert.True(socket.CloseCalled);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.ClosedWithStatus);
        Assert.Equal("Upstream connection failed", socket.ClosedWithDescription);
    }
}
