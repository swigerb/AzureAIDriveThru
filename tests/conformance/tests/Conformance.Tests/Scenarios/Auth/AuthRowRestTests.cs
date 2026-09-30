using System.Net;
using System.Net.Http.Headers;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 rows 1 to 8 (including 6b) on REST: "Each
/// runs on REST (`/api/personas`, `/api/auth/session`, `menu.json`) and on `/realtime` unless
/// noted." Runs every <see cref="AuthRowTokenCase"/> against all three REST paths in one theory
/// row per case (the paths aren't an independent axis worth reporting separately -- a row either
/// behaves the same way on all three "deny by default" REST routes or it's a bug in this test, not
/// a legitimately-different per-route outcome). Gated behind <see cref="AuthRowCapability"/>
/// (issue #143's own critical acceptance: both backends start with auth enforcement OFF, so every
/// one of these skips cleanly today -- see <see cref="ConformanceFixture.RunAuthRowAsync"/>).
/// </summary>
[Collection(ConformanceCollection.Name)]
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
}
