using System.Net;
using System.Text.Json;
using Conformance.Harness;
using Xunit;
using System.Linq;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 5: `/personas/{id}/assets/*` and `/personas/{id}/menu.json`
/// conformance rows against the real, production Sonic pack (personas/sonic). Deliberately
/// UNTAGGED for the same reason as <see cref="PersonaDiscoveryConformanceTests"/> -- the dotnet
/// backend skeleton doesn't serve persona assets yet. The disabled-pack row is covered separately
/// in <c>PersonaDisabledPackConformanceTests</c> (needs a genuinely disabled-but-on-disk pack,
/// which doesn't exist under the real personas/ directory -- see
/// <see cref="DisabledPersonaConformanceFixture"/>'s own doc comment).
///
/// Rick's PR #102 review item 2: the immutable-caching assertions below fetch the REAL
/// `logoUrl`/`menuUrl` from `/api/personas/sonic` (which now carries a `?v=&lt;content-hash&gt;`)
/// rather than hardcoding the bare route path -- a bare, unversioned request to the same route
/// must NOT get the immutable policy (see the two "without a v" rows), so asserting against the
/// versioned URL the API itself hands out is the only way this test proves what the real
/// end-to-end contract actually is.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class PersonaAssetRouteConformanceTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Persona_asset_route_serves_a_real_file_with_200_and_immutable_caching_when_v_matches() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();

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
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/sonic/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.DoesNotContain("immutable", cacheControl, StringComparison.Ordinal);
        Assert.Contains("max-age", cacheControl, StringComparison.Ordinal);
    });

    [Fact]
    public Task Persona_menu_route_serves_json_with_200_and_immutable_caching_when_v_matches() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();

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
        using var http = new HttpClient();
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
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/nope/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    public Task Persona_menu_route_returns_404_for_an_unknown_persona() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/nope/menu.json"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Theory]
    [InlineData("/personas/sonic/assets/../../app.py")]
    [InlineData("/personas/sonic/assets/..%2f..%2fapp.py")]
    [InlineData("/personas/sonic/assets/..%2F..%2Fapp.py")]
    public Task Persona_asset_route_rejects_path_traversal_attempts(string requestPath) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        // HttpClient/aiohttp both normalise a literal "../" before it reaches the server for the
        // first InlineData case -- included anyway as a defence-in-depth row documenting intent,
        // matching _resolve_persona_asset_path's own defence (segment check AND a resolve()-based
        // relative_to(assets_root) guard, app/backend/app.py). The percent-encoded variants are
        // the ones that actually exercise the resolver's own decode-then-check path.
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, requestPath), ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"Expected a traversal attempt against {requestPath} to be rejected with 404/400, got {response.StatusCode}.");
    });
}
