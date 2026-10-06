using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Security;

/// <summary>
/// swigerb/SonicAIDriveThru#25: `_websocket_handler`'s Origin check used to be
/// `origin.endswith(host)`, which accepts any Origin whose netloc merely ends with the request's
/// `Host` header as a *string suffix* -- e.g. `https://evil-&lt;host&gt;` passes
/// `"evil-&lt;host&gt;".endswith("&lt;host&gt;")` even though it's a domain the attacker actually
/// controls, not the real host. Fixed by `_origin_matches_host`, which parses the Origin with
/// `urllib.parse.urlsplit` and requires its `netloc` (host, and port when non-default) to be an
/// *exact*, case-insensitive match for `Host` -- see app/backend/rtmt.py.
///
/// `app/backend/config.yaml`'s default `security.require_session_token: false` means these
/// scenarios don't need a valid session token: the Origin check runs (and can reject) before the
/// token check, so the token/`allowed_origins` config used by the conformance backend never
/// enters into it.
///
/// swigerb/SonicAIDriveThru#21: <see cref="Exact_origin_is_accepted"/> verified passing against
/// the C# backend (3 clean runs, no flakes) -- its two siblings here were already tagged; this
/// was the one genuinely-untagged row left in the class.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class OriginValidationTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Exact_origin_is_accepted() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        var exactOrigin = $"{backend.Scheme}://{backend.Authority}";

        await using var browser = await RealtimeBrowserClient.ConnectAsync(backend, origin: exactOrigin, cancellationToken: ct);

        var created = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "session.created", FrameTimeout, ct);
        Assert.True(created is not null,
            "An Origin that exactly matches the request Host must be accepted, but no session.created arrived.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Lookalike_suffix_origin_is_rejected_with_403() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        // "evil-<host>" ends with the real host as a string suffix -- exactly the bypass #25
        // closed. A real browser can never send this as same-origin (only the attacker's own
        // "evil-<host>" page, which is a different origin the guest never visited), but a
        // malicious page can set any Origin it likes if the check doesn't validate it properly.
        var lookalikeOrigin = $"{backend.Scheme}://evil-{backend.Authority}";

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", lookalikeOrigin);
        var wsUri = await RealtimeUris.WithDefaultCredentialsAsync(backend, cancellationToken: ct);

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, ct));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.NotNull(ex);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Missing_origin_is_accepted_unchanged() => fixture.RunAsync(async () =>
    {
        // Documents existing, #25-unaffected behaviour: a request with no Origin header at all
        // (a non-browser client -- curl, a server-to-server caller) is still accepted. #25 only
        // hardens the case where an Origin *is* present but doesn't match.
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        // Deliberately no Origin header set.
        var wsUri = await RealtimeUris.WithDefaultCredentialsAsync(backend, cancellationToken: ct);

        await socket.ConnectAsync(wsUri, ct);
        Assert.Equal(WebSocketState.Open, socket.State);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    });

    // ── Rick's #230 review item 3: Uri.Authority silently drops BOTH userinfo and an explicit
    // default port, which urlsplit(...).netloc (what rtmt.py's _origin_matches_host actually
    // compares) never does. These two rows pin exactly the gap the old C# port had relative to
    // Python, asserting the Python behaviour on both legs via [Trait("Dotnet", "ready")]. ──

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Origin_with_userinfo_is_rejected_with_403() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        // urlsplit("http://user:pass@<authority>").netloc == "user:pass@<authority>", which is
        // NOT "<authority>" -- Uri.Authority would have dropped the "user:pass@" prefix entirely
        // and (wrongly) accepted this as an exact match against the real Host header.
        var userinfoOrigin = $"{backend.Scheme}://user:pass@{backend.Authority}";

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", userinfoOrigin);
        var wsUri = await RealtimeUris.WithDefaultCredentialsAsync(backend, cancellationToken: ct);

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, ct));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.NotNull(ex);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Origin_with_explicit_default_port_is_rejected_against_a_portless_host() => fixture.RunAsync(async () =>
    {
        // urlsplit("http://<host>:80").netloc == "<host>:80" -- Python never strips an explicit
        // port just because it equals the scheme's own default. Uri.Authority, by contrast,
        // normalises "http://<host>:80" down to "<host>", so it would have wrongly matched a bare
        // "<host>" Host header. A real browser's Origin header never carries an explicit default
        // port the matching Host header doesn't also carry, so this forges the raw HTTP request
        // over a bare TcpClient (the fixture's real listening port is always a dynamic non-80
        // value, so a ClientWebSocket pointed at it can never produce a portless Host header to
        // compare against a ":80" Origin by itself).
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;
        var query = await RealtimeUris.BuildQueryAsync(backend, cancellationToken: ct);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(backend.Host, backend.Port, ct);
        await using var stream = tcp.GetStream();

        var keyBytes = new byte[16];
        Random.Shared.NextBytes(keyBytes);
        var secWebSocketKey = Convert.ToBase64String(keyBytes);
        var request =
            $"GET /realtime?{query} HTTP/1.1\r\n" +
            $"Host: {backend.Host}\r\n" + // deliberately no port, unlike the real Kestrel listening address
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            $"Origin: {backend.Scheme}://{backend.Host}:80\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var statusLine = (await ReadHttpHeadersAsync(stream, ct)).Split("\r\n", 2)[0];
        Assert.Contains("403", statusLine, StringComparison.Ordinal);
    });

    /// <summary>Reads raw HTTP response header bytes up to (and including) the blank-line terminator.</summary>
    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var single = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before the HTTP response completed.");
            }
            sb.Append((char)single[0]);
        }
        return sb.ToString();
    }
}
