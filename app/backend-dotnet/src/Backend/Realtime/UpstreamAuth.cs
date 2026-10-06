using Azure.Core;
using Azure.Identity;

namespace Backend.Realtime;

/// <summary>
/// PR #140 R5 (Rick's round-2 review): the upstream Azure OpenAI realtime WebSocket connect only
/// ever sent <c>api-key</c>, and a deployment with no key configured at all had no way to
/// authenticate -- rtmt.py falls back to a managed-identity bearer token via
/// <c>azure.identity.DefaultAzureCredential</c> in exactly that situation
/// (<c>get_bearer_token_provider(credentials, "https://cognitiveservices.azure.com/.default")</c>).
/// This is that fallback's C# equivalent, kept behind an interface purely so
/// <see cref="Sessions.RealtimeProcessor"/>'s header-selection logic can be unit tested without a
/// real Azure credential (<c>UpstreamAuthHeaderTests</c> substitutes a fake).
/// </summary>
internal interface IUpstreamBearerTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Production implementation: wraps a <see cref="TokenCredential"/> (a real
/// <see cref="DefaultAzureCredential"/> by default) and requests the Cognitive Services scope
/// rtmt.py uses for the same upstream endpoint.</summary>
internal sealed class DefaultAzureCredentialTokenProvider : IUpstreamBearerTokenProvider
{
    // Matches rtmt.py's get_bearer_token_provider(credentials, "https://cognitiveservices.azure.com/.default").
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly TokenCredential _credential;

    public DefaultAzureCredentialTokenProvider(TokenCredential? credential = null)
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
    /// if a connection actually needs it -- every test and every deployment with an api-key
    /// configured never touches this.</summary>
    public static readonly Lazy<DefaultAzureCredentialTokenProvider> Instance = new(() => new DefaultAzureCredentialTokenProvider());
}

/// <summary>Issue #82 (cascade pipeline): C# equivalent of conformance_hooks.py's
/// <c>_FakeCascadeCredential</c> -- an <see cref="IUpstreamBearerTokenProvider"/> that always
/// returns the one fixed token string it was built with, regardless of scope. Cascade has no
/// api-key fallback to reuse (unlike the realtime pipeline's own conformance story), so this is
/// the ONLY way its chat/STT/TTS REST calls can be exercised against the conformance harness's
/// fakes -- substituted in by Program.cs when <see cref="ConformanceHooks.CascadeFakeToken"/> is
/// non-null, exactly like the real <see cref="DefaultAzureCredentialTokenProvider"/> is used
/// otherwise.</summary>
internal sealed class StaticBearerTokenProvider(string token) : IUpstreamBearerTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
}
