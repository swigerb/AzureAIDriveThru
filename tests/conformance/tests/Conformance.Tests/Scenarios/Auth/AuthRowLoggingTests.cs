using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 row 14: "scans the captured stdout and
/// stderr for both token strings" -- the minted Entra access token and the HMAC session token
/// must never be written to the backend's own logs. Runs after exercising both token layers
/// (row 8's valid access token and row 10c's matching session token) so there's something in the
/// captured output window to have leaked, then asserts <see
/// cref="IBackendUnderTest.DumpDiagnostics"/> (this fixture's default profile already runs
/// <c>python app.py</c>, not gunicorn -- issue #143 explicitly defers the gunicorn path to
/// #144's own unit/Dockerfile tests) contains neither token string anywhere.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class AuthRowLoggingTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Row_14_minted_tokens_never_appear_in_captured_stdout_or_stderr() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!;
        var issuer = fixture.EntraIssuer!;

        var accessToken = issuer.Mint();
        var sessionToken = await RealtimeBrowserClient.FetchSessionTokenAsync(backend.BaseUri, accessToken, ct)
            .ConfigureAwait(false);

        using var http = ConformanceHttpClient.Create();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using (await http.GetAsync(new Uri(backend.BaseUri, "/api/personas"), ct).ConfigureAwait(false)) { }

        await using (var browser = await RealtimeBrowserClient.ConnectAsync(
            backend.BaseUri, attachAccessToken: true, accessToken: accessToken,
            attachSessionToken: true, sessionToken: sessionToken, cancellationToken: ct).ConfigureAwait(false))
        {
            await browser.ReceivedFrames
                .WaitForAsync(f => f.Type == "session.created", AuthRowRealtimeAssertions.FrameTimeout, ct)
                .ConfigureAwait(false);
        }

        var dump = backend.DumpDiagnostics();
        Assert.DoesNotContain(accessToken, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionToken!, dump, StringComparison.Ordinal);
    });
}
