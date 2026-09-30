using Conformance.Fakes;
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
    /// <summary>R6 (Rick's PR #158 round 1 review): before this fix, row 14 read
    /// <see cref="IBackendUnderTest.DumpDiagnostics"/> synchronously right after the socket
    /// closed. aiohttp's own access logger only writes the <c>/realtime</c> request line after the
    /// WebSocket handler returns (#55), so that instantaneous read raced -- and usually won -- the
    /// still-in-flight capture, leaving the row vacuously green no matter what actually got
    /// logged. Rick proved it: a probe copy that instead waited for the line found it **did**
    /// contain the JWT, and the session token's own <c>==</c> padding meant the raw-form check
    /// could never have matched its escaped form (<c>%3D%3D</c>) either way.</summary>
    [Fact]
    public Task Row_14_minted_tokens_never_appear_in_captured_stdout_or_stderr() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!;
        var issuer = fixture.EntraIssuer!;

        // Taken before any of this row's own activity, so the positive control and the leak
        // checks below only ever see output this row itself produced -- not an earlier row's
        // (or an earlier test collection run's) identically-shaped /realtime line.
        var watermark = backend.DiagnosticsWatermark;

        var accessToken = issuer.Mint();
        var sessionToken = await RealtimeBrowserClient.FetchSessionTokenAsync(backend.BaseUri, accessToken, ct)
            .ConfigureAwait(false);

        using var http = ConformanceHttpClient.Create();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using (await http.GetAsync(new Uri(backend.BaseUri, "/api/personas"), ct).ConfigureAwait(false)) { }

        // Row 9's shape (a query-string ?access_token= on a REST path) with a genuinely rejected
        // token -- Rick's review item 4: "leaks on rejected requests count too." A query-string
        // token is far more likely to end up verbatim in an access-log request line than a
        // header-only one, so this deliberately-bad token needs its own leak check below just as
        // much as the two genuinely-valid tokens above.
        var rejectedToken = issuer.Mint(new FakeEntraTokenOverrides { Kid = "unknown-kid-row-14-probe" });
        using var rejectedHttp = ConformanceHttpClient.Create();
        using (await rejectedHttp.GetAsync(
            new Uri(backend.BaseUri, $"/api/personas?access_token={Uri.EscapeDataString(rejectedToken!)}"), ct)
            .ConfigureAwait(false))
        { }

        await using (var browser = await RealtimeBrowserClient.ConnectAsync(
            backend.BaseUri, attachAccessToken: true, accessToken: accessToken,
            attachSessionToken: true, sessionToken: sessionToken, cancellationToken: ct).ConfigureAwait(false))
        {
            await browser.ReceivedFrames
                .WaitForAsync(f => f.Type == "session.created", AuthRowRealtimeAssertions.FrameTimeout, ct)
                .ConfigureAwait(false);
        }

        // Positive control (R6): proves the access line this row's own leak checks depend on has
        // actually landed in the capture before those checks run at all -- without this, a row
        // that reads too early passes not because nothing leaked, but because the evidence simply
        // wasn't there yet to catch it.
        var accessLineLogged = await backend.WaitForDiagnosticsAsync(
            d => d.Contains("/realtime", StringComparison.Ordinal), TimeSpan.FromSeconds(10), ct, sinceWatermark: watermark)
            .ConfigureAwait(false);
        Assert.True(accessLineLogged,
            $"Row 14: expected the backend's captured stdout/stderr to eventually contain a \"/realtime\" " +
            $"access line -- without it, the leak checks below would pass vacuously, not because " +
            $"nothing leaked.\n\n--- backend stdout/stderr ---\n{backend.DumpDiagnostics()}");

        var dump = backend.DumpDiagnostics();
        foreach (var token in new[] { accessToken, sessionToken!, rejectedToken! })
        {
            Assert.DoesNotContain(token, dump, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(token), dump, StringComparison.Ordinal);

            // The signature segment: a JWT's third (last) dot-separated part, or -- for the HMAC
            // session token's own two-part `{payload_b64}.{sig}` shape (rtmt.py's
            // create_hmac_token) -- its hex HMAC after the single dot. `Split('.')[^1]` gets
            // either correctly: it is by far the most distinctive part of either token (unlike a
            // JWT's header/payload segments, which repeat verbatim across every token this fixture
            // ever mints), so it is what an incomplete raw-or-escaped-only check like the one
            // above would miss if a log line embedded the token some other way (e.g. re-encoded,
            // wrapped, or truncated with the signature intact).
            var signatureSegment = token.Split('.')[^1];
            Assert.DoesNotContain(signatureSegment, dump, StringComparison.Ordinal);
        }
    });
}
