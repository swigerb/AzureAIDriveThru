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
            resolvedSessionToken = sessionToken
                ?? await RealtimeBrowserClient.FetchSessionTokenAsync(backendBaseUri, resolvedAccessToken, cancellationToken)
                    .ConfigureAwait(false);
        }

        var queryParams = new List<string>();
        if (resolvedAccessToken is not null)
        {
            queryParams.Add($"access_token={Uri.EscapeDataString(resolvedAccessToken)}");
        }
        if (attachSessionToken)
        {
            queryParams.Add($"token={Uri.EscapeDataString(resolvedSessionToken ?? "")}");
        }
        if (persona is not null)
        {
            queryParams.Add($"persona={Uri.EscapeDataString(persona)}");
        }
        var query = queryParams.Count > 0 ? $"?{string.Join('&', queryParams)}" : "";
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
}
