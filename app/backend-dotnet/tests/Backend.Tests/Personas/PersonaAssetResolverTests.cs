using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Personas;

/// <summary>
/// Direct unit coverage for PersonaAssetResolver.Resolve's traversal defense. The pinned HTTP-level
/// conformance rows (PersonaAssetRouteConformanceTests.Persona_asset_route_rejects_path_traversal_
/// attempts) exercise the SAME production code path, but every one of its three InlineData payloads
/// happens to be neutralised before Resolve ever runs: HttpClient normalises a literal "../" client
/// side, and ASP.NET Core's routing never decodes "%2f" into a real path separator (so an
/// encoded-slash payload arrives as a single opaque segment, not a literal ".." segment) -- both
/// confirmed empirically (a mutation that deletes the segment-level ".." check AND makes
/// IsUnderRoot unconditionally true still leaves all nine HTTP conformance assertions green,
/// because none of the three payloads can ever reach a real file through Kestrel/HttpClient
/// regardless of what Resolve does). These tests call Resolve directly with a literal ".." string,
/// bypassing both of those upstream layers, so a regression in the resolver's own containment logic
/// is actually caught here even though the HTTP-level conformance suite cannot observe it for these
/// specific payloads.
/// </summary>
public sealed class PersonaAssetResolverTests : IDisposable
{
    private readonly PersonaPackFixture _fixture = new();

    [Fact]
    public void Literal_parent_traversal_segment_is_rejected_even_when_the_target_file_exists()
    {
        // A real, readable file one level above the assets root -- if traversal succeeded, Resolve
        // would return this path.
        var secretPath = Path.Combine(_fixture.PersonasDir, "sonic", "persona.json");
        Assert.True(File.Exists(secretPath), "fixture setup: persona.json should exist one level above assets/");

        var catalog = PersonaCatalog.Load(personasDir: _fixture.PersonasDir);
        var persona = catalog.Get("sonic");

        var resolved = PersonaAssetResolver.Resolve(persona, "../persona.json");

        Assert.Null(resolved);
    }

    [Fact]
    public void Deeper_traversal_with_backslashes_is_also_rejected()
    {
        var catalog = PersonaCatalog.Load(personasDir: _fixture.PersonasDir);
        var persona = catalog.Get("sonic");

        var resolved = PersonaAssetResolver.Resolve(persona, "..\\..\\sonic\\persona.json");

        Assert.Null(resolved);
    }

    [Fact]
    public void A_real_asset_under_the_assets_root_still_resolves()
    {
        var catalog = PersonaCatalog.Load(personasDir: _fixture.PersonasDir);
        var persona = catalog.Get("sonic");

        var resolved = PersonaAssetResolver.Resolve(persona, "logo.svg");

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    public void Dispose() => _fixture.Dispose();
}
