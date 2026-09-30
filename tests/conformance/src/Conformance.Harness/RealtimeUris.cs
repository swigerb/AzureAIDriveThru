namespace Conformance.Harness;

/// <summary>
/// Rick's PR #158 round 1 review, R8: several existing conformance scenarios build `/realtime`
/// URIs by hand and send neither an `access_token` (the Entra bearer) nor a session `token` (the
/// HMAC-signed `/api/auth/session` token). That's fine while nothing enforces 18.11's auth rows,
/// but under 18.3's check order (Entra, then Origin, then session token, then the persona/model
/// 404s), once #144 enforces, each of them gets a 401 before ever reaching the behaviour actually
/// under test.
///
/// Mirrors <see cref="RealtimeBrowserClient.ConnectAsync"/>'s own default credential resolution --
/// <see cref="EntraDefaultCredentials.TryGetAccessToken"/> for `access_token`, a real
/// <see cref="RealtimeBrowserClient.FetchSessionTokenAsync"/> call using that access token for
/// `token` -- for the handful of call sites that build a raw <see cref="System.Net.WebSockets.ClientWebSocket"/>
/// or <see cref="HttpClient"/> request against `/realtime` directly instead of going through that
/// class. Returns the URI/query unchanged (no `access_token`/`token` appended) when
/// <see cref="EntraDefaultCredentials"/> has nothing registered for the given backend base URI --
/// the Development pass-through case, which never had credentials to attach in the first place.
/// </summary>
public static class RealtimeUris
{
    /// <summary>
    /// Builds a full `ws://{host}:{port}/realtime[?query]` URI with the default `access_token`
    /// and `token` attached (when registered), plus any caller-supplied query on top (for
    /// example `persona=nope` or `model=unknown`).
    /// </summary>
    public static async Task<Uri> WithDefaultCredentialsAsync(
        Uri backendBaseUri, string? extraQuery = null, CancellationToken cancellationToken = default)
    {
        var query = await BuildQueryAsync(backendBaseUri, extraQuery, cancellationToken).ConfigureAwait(false);
        return new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime{(query.Length > 0 ? $"?{query}" : "")}");
    }

    /// <summary>
    /// Same credential resolution as <see cref="WithDefaultCredentialsAsync"/>, but returns just
    /// the query string (no leading `?`, no scheme/host) for the one call site that builds an
    /// `HttpClient`-based request against `/realtime` instead of a WebSocket.
    /// </summary>
    public static async Task<string> BuildQueryAsync(
        Uri backendBaseUri, string? extraQuery = null, CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        var accessToken = EntraDefaultCredentials.TryGetAccessToken(backendBaseUri);
        if (accessToken is not null)
        {
            var sessionToken = await RealtimeBrowserClient.FetchSessionTokenAsync(backendBaseUri, accessToken, cancellationToken)
                .ConfigureAwait(false);
            queryParams.Add($"access_token={Uri.EscapeDataString(accessToken)}");
            queryParams.Add($"token={Uri.EscapeDataString(sessionToken ?? "")}");
        }
        if (!string.IsNullOrEmpty(extraQuery))
        {
            queryParams.Add(extraQuery);
        }
        return string.Join('&', queryParams);
    }
}
