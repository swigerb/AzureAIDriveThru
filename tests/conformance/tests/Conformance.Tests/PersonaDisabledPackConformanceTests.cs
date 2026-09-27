using System.Net;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 5: "serve only the ENABLED pack's own files" -- a persona pack that
/// genuinely exists on disk (test-beta, under <see cref="RepoPaths.FixturePersonasDirectory"/>)
/// but is excluded from PERSONAS must 404 on every persona-scoped route, exactly like an unknown
/// id -- never served just because its files happen to be resolvable on disk. See
/// <see cref="DisabledPersonaConformanceFixture"/>'s own doc comment for why this needs a
/// dedicated fixture/collection rather than reusing <see cref="ConformanceFixture"/> (only
/// personas/sonic exists under the real personas/ directory, so there is no genuinely-disabled
/// pack to test against there).
///
/// S2 part 2 (#12) ports the persona routes' enabled-packs-only filtering and fixes the dotnet
/// launcher to forward PERSONAS_DIR (<see cref="Conformance.Harness.DotnetBackendLauncher"/>) so
/// this fixture's test-alpha/test-beta packs actually reach the C# backend process -- all four
/// rows below now pass and are tagged <c>[Trait("Dotnet", "ready")]</c> at the class level.
/// </summary>
[Collection(DisabledPersonaConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class PersonaDisabledPackConformanceTests(DisabledPersonaConformanceFixture fixture)
{
    private const string DisabledPersonaId = TwoPersonaConformanceFixture.PersonaB;

    [Fact]
    public Task Enabled_persona_asset_route_still_serves_200() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri(fixture.Backend!.BaseUri, $"/personas/{TwoPersonaConformanceFixture.PersonaA}/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    });

    [Fact]
    public Task Disabled_persona_asset_route_returns_404_even_though_the_pack_exists_on_disk() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri(fixture.Backend!.BaseUri, $"/personas/{DisabledPersonaId}/assets/logo.svg"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    public Task Disabled_persona_menu_route_returns_404_even_though_the_pack_exists_on_disk() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri(fixture.Backend!.BaseUri, $"/personas/{DisabledPersonaId}/menu.json"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    public Task Disabled_persona_detail_route_returns_404() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(
            new Uri(fixture.Backend!.BaseUri, $"/api/personas/{DisabledPersonaId}"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });
}
