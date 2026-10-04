using System.Net;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 rows 9 to 13 -- each has its own bespoke,
/// single-route shape (unlike rows 1 to 8/6b, which run identically on REST and realtime and are
/// covered by <c>AuthRowRestTests.cs</c>/<c>AuthRowRealtimeTokenTests.cs</c>).
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class AuthRowSpecialCaseTests(ConformanceFixture fixture)
{
    /// <summary>Row 9, persona-architecture.md 18.11: "a query token on a REST path." A valid
    /// Entra bearer sent as <c>?access_token=</c> instead of the <c>Authorization</c> header must
    /// still be rejected on REST -- unlike <c>/realtime</c> (a WebSocket handshake can't carry a
    /// custom header from a browser, hence the query-param convention there), REST always has the
    /// header available and must not honour the query-string fallback.</summary>
    [Fact]
    public Task Row_9_query_token_on_REST_is_rejected() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var token = fixture.EntraIssuer!.Mint();

        using var http = new HttpClient();
        var uri = new Uri(fixture.Backend!.BaseUri, $"/api/personas?access_token={Uri.EscapeDataString(token)}");
        using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(
            response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"),
            "Row 9: expected WWW-Authenticate: Bearer on the 401 for a query-string access_token on REST.");
    });

    /// <summary>Row 10, persona-architecture.md 18.11: "the /realtime session token missing,
    /// bound to another oid, or matching" -- three sub-cases, all with a genuinely valid Entra
    /// access token (so only the session-token layer is under test).</summary>
    [Fact]
    public Task Row_10a_valid_access_token_missing_session_token_is_rejected() => fixture.RunAuthRowAsync(() =>
        AuthRowRealtimeAssertions.AssertOutcomeAsync(
            fixture.Backend!.BaseUri,
            AuthRowExpectedOutcome.Reject401,
            "Row 10a (session token missing)",
            TestContext.Current.CancellationToken,
            attachAccessToken: true,
            accessToken: fixture.EntraIssuer!.Mint(),
            attachSessionToken: false));

    [Fact]
    public Task Row_10b_session_token_bound_to_another_oid_is_rejected() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var issuer = fixture.EntraIssuer!;
        var backend = fixture.Backend!.BaseUri;

        var connectingAccessToken = issuer.Mint();
        // A real /api/auth/session fetch, but authorized with a *different* oid's access token --
        // once #144/#147 bind the returned session token to the caller's own oid, this genuinely
        // produces a session token that doesn't match the oid the socket connects with.
        var otherOidAccessToken = issuer.Mint(new FakeEntraTokenOverrides { Oid = FakeEntraIssuer.OtherOid });
        var mismatchedSessionToken = await RealtimeBrowserClient
            .FetchSessionTokenAsync(backend, otherOidAccessToken, ct)
            .ConfigureAwait(false);

        await AuthRowRealtimeAssertions.AssertOutcomeAsync(
            backend,
            AuthRowExpectedOutcome.Reject401,
            "Row 10b (session token bound to another oid)",
            ct,
            attachAccessToken: true,
            accessToken: connectingAccessToken,
            attachSessionToken: true,
            sessionToken: mismatchedSessionToken).ConfigureAwait(false);
    });

    [Fact]
    public Task Row_10c_matching_access_and_session_tokens_are_accepted() => fixture.RunAuthRowAsync(() =>
        AuthRowRealtimeAssertions.AssertOutcomeAsync(
            fixture.Backend!.BaseUri,
            AuthRowExpectedOutcome.Accept,
            "Row 10c (matching access and session tokens)",
            TestContext.Current.CancellationToken,
            attachAccessToken: true,
            accessToken: fixture.EntraIssuer!.Mint(),
            attachSessionToken: true));

    /// <summary>Row 11, persona-architecture.md 18.11 / 18.3's check order: "bad Origin with no
    /// token gives 401" -- the Entra check runs before the Origin check, so an unrelated-Origin
    /// mismatch never gets the chance to surface its own 403.</summary>
    [Fact]
    public Task Row_11_bad_origin_with_no_token_gives_401_not_403() => fixture.RunAuthRowAsync(() =>
        AuthRowRealtimeAssertions.AssertOutcomeAsync(
            fixture.Backend!.BaseUri,
            AuthRowExpectedOutcome.Reject401,
            "Row 11 (bad Origin, no token)",
            TestContext.Current.CancellationToken,
            attachAccessToken: false,
            attachSessionToken: false,
            origin: "http://evil.example.com"));

    /// <summary>Row 12, persona-architecture.md 18.11: "the anonymous allow-list." A fixed set of
    /// REST routes must stay reachable with zero token at all, and a route just outside the list
    /// (a protected persona asset) must still 401.</summary>
    public static TheoryData<string> AnonymousAllowListPaths() =>
    [
        "/",
        "/health",
        $"/personas/{ConformancePersonas.DefaultPersonaId}/assets/logo.svg",
        $"/personas/{ConformancePersonas.DefaultPersonaId}/assets/audio/apology-en.wav",
    ];

    [Theory]
    [MemberData(nameof(AnonymousAllowListPaths))]
    public Task Row_12_anonymous_allow_list_path_is_reachable_with_no_token(string path) => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct).ConfigureAwait(false);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Row 12: expected 200 with no token on allow-listed {path}, got {(int)response.StatusCode}.");
    });

    /// <summary>#163/#222 F4 (decided, case-insensitive asset extension), pinned here per PR
    /// #222's own follow-up note that conformance coverage of this rule "belongs in
    /// tests/conformance... flagging for whoever owns the conformance suite / #147": a request for
    /// the SAME row-12 allow-listed asset, but with an upper-cased extension, must never be
    /// blocked by auth (no 401) -- byte-for-byte matching Python's <c>_is_anonymous</c>, which
    /// lower-cases the suffix before comparing against the identical literal extension set.
    /// Asserting "not 401" rather than "200" is deliberate: the on-disk asset is literally named
    /// <c>logo.svg</c> (lower-case), so on a case-sensitive filesystem (Linux CI runners) a request
    /// for <c>LOGO.SVG</c> legitimately 404s at the file-serving layer once auth has already let it
    /// through -- that's a correct, expected outcome of case-sensitive file lookup, not an auth
    /// bug, and is exactly what Python's own aiohttp static-file serving would do on the same OS.
    /// A 401 here, and only a 401, would indicate the extension-match itself is case-sensitive.</summary>
    [Fact]
    public Task Row_12_anonymous_allow_list_path_is_case_insensitive_on_extension() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"/personas/{ConformancePersonas.DefaultPersonaId}/assets/LOGO.SVG";
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct).ConfigureAwait(false);
        Assert.True(
            response.StatusCode != HttpStatusCode.Unauthorized,
            $"Row 12 (case-insensitive extension, #163/#222 F4): expected {path} to NOT be " +
            $"blocked by auth (any status other than 401 is fine -- a 404 just means this " +
            $"filesystem is case-sensitive and the literal file is named logo.svg), got 401.");
    });

    [Fact]
    public Task Row_12_protected_asset_outside_the_allow_list_still_401s() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"/personas/{ConformancePersonas.DefaultPersonaId}/assets/demo/dummyOrder.json";
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct).ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"Row 12: expected the protected asset {path} to 401 with no token, got {(int)response.StatusCode}.");
        Assert.True(
            response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"),
            "Row 12: expected WWW-Authenticate: Bearer on the protected asset's 401.");
    });

    /// <summary>#223 (Rick's review of PR #225, mirrored here for #147 parity): a persona asset
    /// name that IS a dotfile (e.g. <c>.png</c>) has NO extension under Python's
    /// <c>PurePosixPath.suffix</c> rule -- a leading dot is a "hidden file" marker, not an
    /// extension delimiter -- so it is NOT anonymous and must still 401 with no token, exactly
    /// like any other unrecognized-extension asset (the <c>demo/dummyOrder.json</c> case above).
    /// Deliberately does not require the file to exist on disk: the auth decision happens before
    /// file resolution, so this 401 would fire the same way whether or not <c>.png</c> is actually
    /// present in the persona pack -- avoiding the case-sensitive-filesystem pitfall that the Row
    /// 12 case-insensitive-extension test above already had to work around.</summary>
    [Fact]
    public Task Row_12_dotfile_asset_has_no_extension_still_401s() => fixture.RunAuthRowAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"/personas/{ConformancePersonas.DefaultPersonaId}/assets/.png";
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct).ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"Row 12 (#223 dotfile edge case): expected the dotfile asset {path} to 401 with no " +
            $"token (a leading dot is not an extension delimiter, so it's not anonymous), got " +
            $"{(int)response.StatusCode}.");
        Assert.True(
            response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"),
            "Row 12 (#223 dotfile edge case): expected WWW-Authenticate: Bearer on the dotfile asset's 401.");
    });

    /// <summary>Row 13, persona-architecture.md 18.11: "an unknown persona with no token gives
    /// 401" -- the Entra check must run, and win, before the router ever gets far enough to
    /// discover the persona id doesn't exist (which would otherwise be a 404).</summary>
    [Fact]
    public Task Row_13_unknown_persona_with_no_token_gives_401_not_404() => fixture.RunAuthRowAsync(() =>
        AuthRowRealtimeAssertions.AssertOutcomeAsync(
            fixture.Backend!.BaseUri,
            AuthRowExpectedOutcome.Reject401,
            "Row 13 (unknown persona, no token)",
            TestContext.Current.CancellationToken,
            attachAccessToken: false,
            attachSessionToken: false,
            persona: "no-such-persona"));
}
