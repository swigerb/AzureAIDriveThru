using Azure.Core;

namespace SearchIndexIngestor.Tests;

/// <summary>A <see cref="TokenCredential"/> that is never actually asked for a token in these
/// tests (every test-seam factory below builds its client directly from a fake HTTP handler,
/// bypassing <see cref="BearerTokenHandler"/> entirely) -- it exists purely so
/// <see cref="CliRunner.RunAsync"/>'s <c>createCredential</c> parameter has something constructible
/// to hand to the two client-factory seams, without constructing a real
/// <c>DefaultAzureCredential</c> (which would attempt real credential-chain probing).</summary>
internal sealed class FakeTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
