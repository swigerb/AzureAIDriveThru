using System.Net;
using System.Net.Http.Headers;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 rows 1 to 8/17 to 19 (including 6b) on REST: "Each
/// runs on REST (`/api/personas`, `/api/auth/session`, `menu.json`) and on `/realtime` unless
/// noted." Runs every <see cref="AuthRowTokenCase"/> against all three REST paths in one theory
/// row per case (the paths aren't an independent axis worth reporting separately -- a row either
/// behaves the same way on all three "deny by default" REST routes or it's a bug in this test, not
/// a legitimately-different per-route outcome). Gated behind <see cref="AuthRowCapability"/>
/// (issue #143's own critical acceptance: both backends start with auth enforcement OFF, so every
/// one of these skips cleanly today -- see <see cref="ConformanceFixture.RunAuthRowAsync"/>).
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
[AuthRowCapabilityGated]
public sealed class AuthRowRestTokenTests(ConformanceFixture fixture)
{
    /// <summary>18.11's own three named REST routes -- deliberately not the anonymous allow-list
    /// (row 12 covers that separately) or an unknown route (row 13 is realtime-only).</summary>
    private static readonly string[] RestPaths =
    [
        "/api/personas",
        "/api/auth/session",
        $"/personas/{ConformancePersonas.DefaultPersonaId}/menu.json",
    ];

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < AuthRowTokenCase.All.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public Task Row_asserts_on_every_REST_path(int caseIndex) => fixture.RunAuthRowAsync(async () =>
    {
        var row = AuthRowTokenCase.All[caseIndex];
        var token = row.MintToken(fixture.EntraIssuer!);
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in RestPaths)
        {
            // A raw HttpClient, not ConformanceHttpClient.Create(): this row controls the
            // Authorization header itself (including "no header at all" for row 1), which the
            // ambient auto-attach factory would otherwise clobber.
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Backend!.BaseUri, path));
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            AssertExpectedOutcome(row, path, response);
        }
    });

    private static void AssertExpectedOutcome(AuthRowTokenCase row, string path, HttpResponseMessage response)
    {
        switch (row.Expected)
        {
            case AuthRowExpectedOutcome.Reject401:
                Assert.True(
                    response.StatusCode == HttpStatusCode.Unauthorized,
                    $"{row} on {path}: expected 401, got {(int)response.StatusCode}.");
                AssertWwwAuthenticateBearer(row, path, response);
                break;

            case AuthRowExpectedOutcome.Reject403:
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{row} on {path}: expected 403, got {(int)response.StatusCode}.");
                break;

            case AuthRowExpectedOutcome.Accept:
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"{row} on {path}: expected 200, got {(int)response.StatusCode}.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    /// <summary>18.4's failure contract, and issue #143 item 5: every 401 (not just row 1's) must
    /// carry <c>WWW-Authenticate: Bearer</c>.</summary>
    private static void AssertWwwAuthenticateBearer(AuthRowTokenCase row, string path, HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"),
            $"{row} on {path}: expected a WWW-Authenticate: Bearer challenge on 401, got: " +
            $"[{string.Join(", ", response.Headers.WwwAuthenticate)}].");
    }

    /// <summary>#163/#222 N3 (decided, case-insensitive scheme), pinned here per PR #222's own
    /// follow-up note that conformance coverage of this rule "belongs in tests/conformance...
    /// flagging for whoever owns the conformance suite / #147": a valid token sent with a
    /// lower- or mixed-case Authorization scheme token (e.g. "bearer"/"BEARER"/"BeArEr") must be
    /// accepted exactly like the canonical-cased scheme. entra_middleware._extract_token now
    /// lower-cases the scheme before comparing; ASP.NET Core's JwtBearerHandler already does this
    /// natively (it matches the scheme with StringComparison.OrdinalIgnoreCase internally), so
    /// this pins that already-correct behavior against regression rather than fixing anything.
    /// </summary>
    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public Task Scheme_case_insensitive_Bearer_scheme_is_accepted(string scheme) => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var token = fixture.EntraIssuer!.Mint();

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Backend!.BaseUri, "/api/personas"));
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"#163/#222 N3 ({scheme}): expected 200 on /api/personas with a valid token under a " +
            $"differently-cased scheme, got {(int)response.StatusCode}.");
    });
}
