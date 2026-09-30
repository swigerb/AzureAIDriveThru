using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Unit tests for the two pure helpers <see cref="AuthRowRealtimeAssertions"/> extracted for R3
/// (Rick's PR #158 round 1 review) -- no <see cref="Conformance.Fakes.FakeEntraIssuer"/>, no
/// backend, no socket. These pin the fix itself: the session-token fetch on a rejection row must
/// always use a known-valid default access token, never the row's own (possibly bad) one.
/// </summary>
[Trait("Dotnet", "ready")]
public sealed class AuthRowRealtimeAssertionsTests
{
    /// <summary>R3 pin (Rick's PR #158 round 1 review): <see cref="AuthRowRealtimeAssertions.ResolveSessionTokenFetchToken"/>
    /// must always return the fixture's own default access token, regardless of what the row's
    /// own (possibly bad) access token is -- including when the row's token is null (row 1's "no
    /// token" case) or looks nothing like a real token at all. Before the fix, the rejection path
    /// fetched the session token with the row's own bad token, which throws
    /// <c>HttpRequestException</c> once a backend actually enforces the Entra check, crashing
    /// setup instead of exercising the rejection. A mutation that returns
    /// <paramref name="rowAccessToken"/> instead of <paramref name="defaultAccessToken"/> (or vice
    /// versa reversed) would fail every case below except the one where they happen to be
    /// unequal.</summary>
    [Theory]
    [InlineData("this-is-a-deliberately-bad-token", "a-known-valid-default-token")]
    [InlineData(null, "a-known-valid-default-token")]
    [InlineData("", "a-known-valid-default-token")]
    public void Always_resolves_to_the_default_access_token_never_the_row_s_own(string? rowAccessToken, string? defaultAccessToken)
    {
        var resolved = AuthRowRealtimeAssertions.ResolveSessionTokenFetchToken(rowAccessToken, defaultAccessToken);

        Assert.Equal(defaultAccessToken, resolved);
        Assert.NotEqual(rowAccessToken, resolved);
    }

    /// <summary>R3 pin (Rick's PR #158 round 1 review): <see cref="AuthRowRealtimeAssertions.BuildRejectionQuery"/>
    /// must attach the row's own access token as <c>access_token</c> (the Entra layer actually
    /// under test for this row) while the session <c>token</c> param carries whatever was already
    /// resolved separately -- the two must never be swapped or conflated, which is exactly the bug
    /// R3 fixes upstream of this helper.</summary>
    [Fact]
    public void Attaches_the_row_s_own_access_token_and_the_separately_resolved_session_token()
    {
        var query = AuthRowRealtimeAssertions.BuildRejectionQuery(
            rowAccessToken: "row-access-token",
            sessionToken: "resolved-session-token",
            attachSessionToken: true,
            persona: null);

        Assert.Contains("access_token=row-access-token", query);
        Assert.Contains("token=resolved-session-token", query);
    }

    /// <summary>R3 pin: when <paramref name="attachSessionToken"/> is false (18.11 row 10's "no
    /// session token" case), the <c>token</c> query param must be omitted entirely, not sent
    /// empty -- matching <see cref="Conformance.Harness.RealtimeBrowserClient.ConnectAsync"/>'s
    /// own behaviour exactly.</summary>
    [Fact]
    public void Omits_the_session_token_param_entirely_when_attachSessionToken_is_false()
    {
        var query = AuthRowRealtimeAssertions.BuildRejectionQuery(
            rowAccessToken: "row-access-token",
            sessionToken: null,
            attachSessionToken: false,
            persona: null);

        Assert.Equal("?access_token=row-access-token", query);
    }

    /// <summary>R3 pin: no access token at all (18.11 row 1's "no token" case) must omit
    /// <c>access_token</c> entirely rather than sending it empty.</summary>
    [Fact]
    public void Omits_the_access_token_param_entirely_when_the_row_access_token_is_null()
    {
        var query = AuthRowRealtimeAssertions.BuildRejectionQuery(
            rowAccessToken: null,
            sessionToken: "resolved-session-token",
            attachSessionToken: true,
            persona: null);

        Assert.DoesNotContain("access_token=", query);
        Assert.Contains("token=resolved-session-token", query);
    }
}
