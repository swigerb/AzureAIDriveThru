using System.Net;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 5: `/personas/{id}/assets/*` and `/personas/{id}/menu.json`
/// conformance rows against the real, production Sonic pack (personas/sonic). Deliberately
/// UNTAGGED for the same reason as <see cref="PersonaDiscoveryConformanceTests"/> -- the dotnet
/// backend skeleton doesn't serve persona assets yet. The disabled-pack row is covered separately
/// in <c>PersonaDisabledPackConformanceTests</c> (needs a genuinely disabled-but-on-disk pack,
/// which doesn't exist under the real personas/ directory -- see
/// <see cref="DisabledPersonaConformanceFixture"/>'s own doc comment).
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class PersonaAssetRouteConformanceTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Persona_asset_route_serves_a_real_file_with_200_and_immutable_caching() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/sonic/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? "";
        Assert.Contains("immutable", cacheControl, StringComparison.Ordinal);
        Assert.Contains("max-age", cacheControl, StringComparison.Ordinal);
    });

    [Fact]
    public Task Persona_menu_route_serves_json_with_200() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/personas/sonic/menu.json"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
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
