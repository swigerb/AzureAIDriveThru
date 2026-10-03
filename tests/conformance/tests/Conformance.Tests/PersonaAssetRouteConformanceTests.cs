using System.Net;
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
}
