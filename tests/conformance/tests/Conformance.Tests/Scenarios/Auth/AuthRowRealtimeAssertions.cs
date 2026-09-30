using System.Net;
using System.Net.WebSockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Shared <c>/realtime</c> connect-and-assert logic for every 18.11 row that has a realtime
/// variant (rows 1 to 8/6b via <see cref="AuthRowTokenCase"/>, plus rows 10, 11 and 13's own
/// bespoke cases). Deliberately duplicates <see cref="RealtimeBrowserClient.ConnectAsync"/>'s
/// query-string-building for the rejection path rather than reusing it: a rejected handshake
/// never produces a <see cref="RealtimeBrowserClient"/> instance to inspect (it's only
/// constructed after a successful upgrade), so asserting "rejected before the upgrade" needs a
/// raw <see cref="ClientWebSocket"/> the caller still holds after the throw -- the same technique
/// <c>Scenarios/Security/OriginValidationTests.cs</c>'s own bad-Origin rejection test already
/// uses.
/// </summary>
internal static class AuthRowRealtimeAssertions
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    public static async Task AssertOutcomeAsync(
        Uri backendBaseUri,
        AuthRowExpectedOutcome expected,
        string rowLabel,
        CancellationToken cancellationToken,
        bool attachAccessToken = true,
        string? accessToken = null,
        bool attachSessionToken = true,
        string? sessionToken = null,
        string? origin = null,
        string? persona = null)
    {
        if (expected == AuthRowExpectedOutcome.Accept)
        {
            await using var browser = await RealtimeBrowserClient.ConnectAsync(
                backendBaseUri,
                origin: origin,
                persona: persona,
                attachAccessToken: attachAccessToken,
                accessToken: accessToken,
                attachSessionToken: attachSessionToken,
                sessionToken: sessionToken,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var created = await browser.ReceivedFrames
                .WaitForAsync(f => f.Type == "session.created", FrameTimeout, cancellationToken)
                .ConfigureAwait(false);
            Assert.True(created is not null, $"{rowLabel}: expected the socket to open (session.created), but it never arrived.");
            return;
        }

        var expectedStatus = expected == AuthRowExpectedOutcome.Reject403 ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;

        var resolvedAccessToken = attachAccessToken
            ? accessToken ?? EntraDefaultCredentials.TryGetAccessToken(backendBaseUri)
            : null;

        string? resolvedSessionToken = null;
        if (attachSessionToken)
        {
            // R3 (Rick's PR #158 round 1 review): fetch the session token with the fixture's own
            // known-valid default access token, NEVER with resolvedAccessToken. A rejection row's
            // own access token is deliberately bad, so fetching with it throws
            // HttpRequestException from GetFromJsonAsyncSafe's EnsureSuccessStatusCode before the
            // /realtime probe below is ever attempted -- every rejection row then failed for the
            // wrong reason once a backend actually enforces the Entra check. Only the Entra layer
            // (resolvedAccessToken, attached to the connect attempt below) is meant to vary row to
            // row; the session-token layer must stay fixed and valid, so a rejection is provably
            // caused by the Entra check, not a session-token fetch failure.
            var sessionTokenFetchToken = ResolveSessionTokenFetchToken(
                resolvedAccessToken, EntraDefaultCredentials.TryGetAccessToken(backendBaseUri));
            resolvedSessionToken = sessionToken
                ?? await RealtimeBrowserClient.FetchSessionTokenAsync(backendBaseUri, sessionTokenFetchToken, cancellationToken)
                    .ConfigureAwait(false);
        }

        var query = BuildRejectionQuery(resolvedAccessToken, resolvedSessionToken, attachSessionToken, persona);
        var wsUri = new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime{query}");

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", origin ?? $"{backendBaseUri.Scheme}://{backendBaseUri.Authority}");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, cancellationToken))
            .ConfigureAwait(false);
        Assert.NotNull(ex);

        // Issue #143 item 5: "On /realtime it asserts rejection before the upgrade."
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.Equal(expectedStatus, socket.HttpStatusCode);

        if (expectedStatus == HttpStatusCode.Unauthorized)
        {
            var hasBearerChallenge =
                socket.HttpResponseHeaders?.TryGetValue("WWW-Authenticate", out var values) == true
                && values.Any(v => v.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasBearerChallenge, $"{rowLabel}: expected a WWW-Authenticate: Bearer challenge on the /realtime 401 rejection.");
        }
    }

    /// <summary>R3 pin (Rick's PR #158 round 1 review): which access token
    /// <see cref="RealtimeBrowserClient.FetchSessionTokenAsync"/> should be called with on the
    /// rejection path -- always <paramref name="defaultAccessToken"/> (the fixture's own known-
    /// valid default), never <paramref name="rowAccessToken"/> (the row's own, possibly-bad, token
    /// under test). Pure and I/O-free so a unit test can pin the decision without a real
    /// <see cref="Conformance.Fakes.FakeEntraIssuer"/> or backend.</summary>
    internal static string? ResolveSessionTokenFetchToken(string? rowAccessToken, string? defaultAccessToken) => defaultAccessToken;

    /// <summary>R3 pin (Rick's PR #158 round 1 review): pure query-string builder for the
    /// rejection path, extracted from the inline logic that used to build this alongside the
    /// (buggy) session-token fetch -- <paramref name="rowAccessToken"/> is always what gets
    /// attached as <c>access_token</c> (the Entra-layer token actually under test for this row),
    /// while <paramref name="sessionToken"/> is whatever the caller already resolved (see
    /// <see cref="ResolveSessionTokenFetchToken"/> for how that's fetched on the rejection
    /// path).</summary>
    internal static string BuildRejectionQuery(
        string? rowAccessToken, string? sessionToken, bool attachSessionToken, string? persona)
    {
        var queryParams = new List<string>();
        if (rowAccessToken is not null)
        {
            queryParams.Add($"access_token={Uri.EscapeDataString(rowAccessToken)}");
        }
        if (attachSessionToken)
        {
            queryParams.Add($"token={Uri.EscapeDataString(sessionToken ?? "")}");
        }
        if (persona is not null)
        {
            queryParams.Add($"persona={Uri.EscapeDataString(persona)}");
        }
        return queryParams.Count > 0 ? $"?{string.Join('&', queryParams)}" : "";
    }
}