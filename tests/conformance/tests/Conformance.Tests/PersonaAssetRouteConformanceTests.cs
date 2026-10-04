using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conformance.Harness;
using Xunit;
using System.Linq;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 5: `/personas/{id}/assets/*` and `/personas/{id}/menu.json`
/// conformance rows against the real, production Sonic pack (personas/sonic). S2 part 2 (#12)
/// ports Personas/PersonaRoutes.cs's asset/menu routes (traversal-safe resolution, `?v=` content
/// hashing, pinned content types) -- every row here (including all three path-traversal
/// InlineData rows) is now tagged <c>[Trait("Dotnet", "ready")]</c> at the class level. The
/// disabled-pack row is covered separately in <c>PersonaDisabledPackConformanceTests</c> (needs a
/// genuinely disabled-but-on-disk pack, which doesn't exist under the real personas/ directory --
/// see <see cref="DisabledPersonaConformanceFixture"/>'s own doc comment).
///
/// Rick's PR #102 review item 2: the immutable-caching assertions below fetch the REAL
/// `logoUrl`/`menuUrl` from `/api/personas/sonic` (which now carries a `?v=&lt;content-hash&gt;`)
/// rather than hardcoding the bare route path -- a bare, unversioned request to the same route
/// must NOT get the immutable policy (see the two "without a v" rows), so asserting against the
/// versioned URL the API itself hands out is the only way this test proves what the real
/// end-to-end contract actually is.
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class PersonaAssetRouteConformanceTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Persona_asset_route_serves_a_real_file_with_200_and_immutable_caching_when_v_matches() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();

        // logoUrl is only in the /api/personas summary list, not the /api/personas/{id} detail
        // body (see app.py's `_persona_summary_body` vs `_persona_detail_body`).
        using var indexResponse = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas"), ct);
        using var indexDocument = JsonDocument.Parse(await indexResponse.Content.ReadAsStreamAsync(ct));
        var sonic = indexDocument.RootElement.GetProperty("personas").EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == "sonic");
        var logoUrl = sonic.GetProperty("logoUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(logoUrl));
        Assert.Contains("?v=", logoUrl, StringComparison.Ordinal);

        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, logoUrl), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.Contains("immutable", cacheControl, StringComparison.Ordinal);
        Assert.Contains("max-age", cacheControl, StringComparison.Ordinal);
    });

    [Fact]
    public Task Persona_asset_route_serves_short_cache_header_without_a_v() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/sonic/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.DoesNotContain("immutable", cacheControl, StringComparison.Ordinal);
        Assert.Contains("max-age", cacheControl, StringComparison.Ordinal);
    });

    [Fact]
    public Task Persona_asset_route_serves_mp3_with_audio_mpeg_content_type() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();

        using var indexResponse = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas"), ct);
        using var indexDocument = JsonDocument.Parse(await indexResponse.Content.ReadAsStreamAsync(ct));
        var personaIds = indexDocument.RootElement.GetProperty("personas").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();

        foreach (var personaId in personaIds)
        {
            using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, $"/personas/{personaId}/assets/demo/guest/01.mp3"), ct);
            if (response.StatusCode != HttpStatusCode.OK) continue;
            Assert.Contains("audio/mpeg", response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);
            return;
        }

        Assert.Fail("Expected at least one enabled persona to serve a demo MP3 clip.");
    });

    [Fact]
    public Task Persona_menu_route_serves_json_with_200_and_immutable_caching_when_v_matches() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();

        using var detailResponse = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas/sonic"), ct);
        using var detailDocument = JsonDocument.Parse(await detailResponse.Content.ReadAsStreamAsync(ct));
        var menuUrl = detailDocument.RootElement.GetProperty("menuUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(menuUrl));
        Assert.Contains("?v=", menuUrl, StringComparison.Ordinal);

        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, menuUrl), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.Contains("immutable", cacheControl, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    });

    [Fact]
    public Task Persona_menu_route_serves_short_cache_header_without_a_v() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/sonic/menu.json"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.DoesNotContain("immutable", cacheControl, StringComparison.Ordinal);
        Assert.Contains("max-age", cacheControl, StringComparison.Ordinal);
    });

    [Fact]
    public Task Persona_asset_route_returns_404_for_an_unknown_persona() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/nope/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    public Task Persona_menu_route_returns_404_for_an_unknown_persona() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/nope/menu.json"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    /// <summary>
    /// Rick's PR #122 review item 1: every row here USED to target `../../app.py` -- a file that
    /// doesn't exist even one level further up (personas/sonic/assets -&gt; personas/sonic -&gt;
    /// personas), so all four rows 404'd for "no such file" reasons and stayed green even with a
    /// completely disabled resolver (verified below by literally disabling each backend's checks).
    /// `personas/sonic/persona.json` sits exactly ONE level above assets/ and DOES exist, so every
    /// row now targets it instead (a single `..`, not two) -- a row can only prove anything about
    /// the resolver if defeating the resolver would make that row 200 instead of 404.
    ///
    /// Per backend, these rows are NOT equally mutation-sensitive, because of a genuine, deliberate
    /// framework difference in how the traversal payload reaches the resolver at all:
    ///  - Literal `../persona.json`: both HttpClient (client-side, per RFC 3986 dot-segment
    ///    removal in the Uri constructor) and aiohttp's own request-line normalisation collapse
    ///    this to `/personas/sonic/persona.json` BEFORE it is ever sent/routed -- neither backend's
    ///    resolver ever sees a `..` segment for this row. Kept only as a defence-in-depth
    ///    documentation row; it can never go red on either leg no matter what the resolver does.
    ///  - `..%2fpersona.json` / `..%2Fpersona.json`: ASP.NET Core's routing deliberately does NOT
    ///    decode `%2f`/`%2F` into a literal `/` in a route value (a well-known security behaviour
    ///    that avoids exactly this kind of ambiguity) -- so on the DOTNET leg, `assetPath` arrives
    ///    as the single, literal, unsplit segment `"..%2fpersona.json"`, which never equals `".."`
    ///    and never matches a real file either way. These rows are therefore structurally blind on
    ///    the dotnet leg regardless of PersonaAssetResolver's own logic -- disabling its checks
    ///    cannot turn them red, and that is NOT a resolver bug. On the PYTHON leg, aiohttp's
    ///    `match_info` DOES decode `%2f` into a real `/`, so `_resolve_persona_asset_path` sees a
    ///    genuine `..` segment -- these rows ARE mutation-sensitive on the Python leg (proven
    ///    below).
    ///  - `..%5cpersona.json`: `%5c` is an ordinary percent-encoded byte (backslash is not a URI
    ///    path separator, so there is no ambiguity for either framework to specially block) -- BOTH
    ///    ASP.NET Core routing and aiohttp decode it into a literal backslash, which
    ///    PersonaAssetResolver's own `requestedPath.Replace('\\', '/').Split('/')` (and Python's
    ///    equivalent) then splits into a genuine `".."` segment. This is the one row that
    ///    genuinely exercises -- and, mutation-tested below, is sensitive to -- EACH backend's OWN
    ///    resolver logic, not just routing/framework behaviour upstream of it.
    ///
    /// Mutation evidence (performed by hand for this revision, then reverted -- see PR #122 body):
    ///  - Python: temporarily made `_resolve_persona_asset_path` skip its segment-rejection loop
    ///    and its `relative_to(assets_root)` containment check -- the `%2f`, `%2F`, and `%5c` rows
    ///    all went red (200, serving app/backend/personas/sonic/persona.json's *sibling* file
    ///    content -- actually the real, existing persona.json one level up); the literal row
    ///    stayed a 404 (never reaches the handler). Reverting restored all four rows to green.
    ///  - Dotnet: temporarily made PersonaAssetResolver.Resolve skip its segment-rejection loop and
    ///    forced IsUnderRoot to always return true -- ONLY the `%5c` row went red (200); `%2f`,
    ///    `%2F`, and the literal row stayed green (still 404), exactly as the framework-behaviour
    ///    analysis above predicts. Reverting restored 4/4 green with a clean `git diff`.
    /// </summary>
    [Theory]
    [InlineData("/personas/sonic/assets/../persona.json")]
    [InlineData("/personas/sonic/assets/..%2fpersona.json")]
    [InlineData("/personas/sonic/assets/..%2Fpersona.json")]
    [InlineData("/personas/sonic/assets/..%5cpersona.json")]
    public Task Persona_asset_route_rejects_path_traversal_attempts(string requestPath) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = ConformanceHttpClient.Create();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, requestPath), ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"Expected a traversal attempt against {requestPath} to be rejected with 404/400, got {response.StatusCode}.");
    });

    /// <summary>
    /// #63 harness follow-up (adopted from PR #122 review item "Raw-request helper for the
    /// path-traversal rows"): the first literal `../persona.json` row above is documented (see
    /// that test's doc comment) as unable to ever go red on EITHER backend, because
    /// <see cref="ConformanceHttpClient"/>'s underlying <see cref="HttpClient"/> collapses the
    /// `..` dot-segment client-side (RFC 3986) before the request line is ever written to the
    /// wire -- the request each backend actually receives already reads the collapsed, dot-free
    /// asset path, with no `..` anywhere in it. This row instead opens a bare
    /// <see cref="TcpClient"/> and writes a hand-built HTTP/1.1 request line containing the raw,
    /// uncollapsed `..` bytes, so the backend itself -- not a client-side URI normaliser -- is what
    /// decides what happens to the traversal attempt.
    ///
    /// Rick's PR #220 review: a raw request with no <c>Authorization</c> header is meaningless in
    /// this fixture's default Entra mode -- Python's per-request auth middleware 401s the
    /// unauthenticated request before `_resolve_persona_asset_path`'s own containment check ever
    /// runs, and this test previously accepted 401 as a passing outcome, so it could never actually
    /// exercise (or catch a regression in) either backend's resolver. Fixed by minting a real
    /// token from this fixture's own <see cref="FakeEntraIssuer"/> (<c>fixture.EntraIssuer!.Mint()</c>,
    /// the same source <see cref="ConformanceHttpClient"/> uses for every other row in this class)
    /// and sending it on the raw request line, so both backends now reach their own resolver code
    /// for this row, same as the `%2f`/`%5c` InlineData rows above. With a valid token attached:
    /// Kestrel's routing still removes dot segments server-side before a request is ever routed, so
    /// this row stays structurally 404 on the dotnet leg regardless of
    /// <c>PersonaAssetResolver</c> (same reasoning as the `%2f`/`%2F` rows above); on the Python
    /// leg, aiohttp passes the raw `../persona.json` through to `match_info` unmodified, and now
    /// that auth no longer short-circuits it, `_resolve_persona_asset_path`'s own segment-rejection
    /// and `relative_to(assets_root)` containment check is what rejects it. Only 404/400 are
    /// accepted now -- 401 is deliberately no longer in the accepted set, and 200 is asserted
    /// against explicitly with its own pinpoint message, since that's the one outcome that would
    /// mean the escaped file actually leaked. The response body is also asserted to never contain
    /// `persona.json`'s own `"schemaVersion"` key (present verbatim in every real persona.json
    /// under personas/), so even a 200 that somehow slipped past the status-code assertion (or a
    /// non-404/400 status this test doesn't otherwise recognise) can't silently pass by returning
    /// the escaped file's content under an unexpected status line.
    ///
    /// Persona id is discovered from disk (<see cref="ConformancePersonas.DiscoverFromDisk()"/>)
    /// rather than a hardcoded literal, so this row never needs its own rebrand-baseline entry
    /// (issue #105) for naming a specific real pack -- any pack the fixture happens to be running
    /// against works identically, since the point here is the traversal rejection, not which pack.
    ///
    /// Mutation evidence (performed by hand for this revision, then reverted -- see PR #220 body):
    ///  - Python: temporarily made `_resolve_persona_asset_path` skip its segment-rejection loop
    ///    and its `relative_to(assets_root)` containment check -- this row went red (200, serving
    ///    persona.json's content, `"schemaVersion"` present in the body). Reverting restored it to
    ///    green.
    /// </summary>
    [Fact]
    public Task Persona_asset_route_rejects_a_raw_uncollapsed_path_traversal_attempt() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backendUri = fixture.Backend!.BaseUri;
        var personaId = ConformancePersonas.DiscoverFromDisk().First();
        var token = fixture.EntraIssuer!.Mint();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(backendUri.Host, backendUri.Port, ct);
        await using var stream = tcp.GetStream();

        var request =
            $"GET /personas/{personaId}/assets/../persona.json HTTP/1.1\r\n" +
            $"Host: {backendUri.Host}:{backendUri.Port}\r\n" +
            $"Authorization: Bearer {token}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var (statusLine, body) = await ReadRawHttpResponseAsync(stream, ct);

        Assert.False(
            statusLine.Contains(" 200 ", StringComparison.Ordinal),
            $"A raw, uncollapsed '..' traversal attempt with a VALID token must never succeed -- " +
            $"got status line: {statusLine}, body: {body}");
        Assert.True(
            statusLine.Contains(" 404 ", StringComparison.Ordinal)
                || statusLine.Contains(" 400 ", StringComparison.Ordinal),
            $"Expected an authenticated, raw, uncollapsed '..' traversal attempt to be rejected with " +
            $"404/400, got status line: {statusLine}");
        Assert.DoesNotContain("\"schemaVersion\"", body, StringComparison.Ordinal);
    });

    /// <summary>Reads the HTTP response status line (first `\r\n`-terminated line) byte by byte --
    /// mirroring <c>HeartbeatPongSurvivalTests.ReadHttpHeadersAsync</c>'s same one-byte-at-a-time
    /// approach -- then drains the rest of the response (remaining headers + body) until the
    /// server closes the connection (the request line above sends <c>Connection: close</c>, so EOF
    /// reliably marks the end of the response), and returns the status line alongside just the
    /// body portion (after the blank line separating headers from body).</summary>
    private static async Task<(string StatusLine, string Body)> ReadRawHttpResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var single = new byte[1];
        while (!sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before an HTTP status line was received.");
            }

            sb.Append((char)single[0]);
        }

        var statusLine = sb.ToString();

        using var restStream = new MemoryStream();
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            restStream.Write(buffer, 0, bytesRead);
        }

        var rest = Encoding.ASCII.GetString(restStream.ToArray());
        var headerEnd = rest.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = headerEnd >= 0 ? rest[(headerEnd + 4)..] : rest;

        return (statusLine, body);
    }
}
