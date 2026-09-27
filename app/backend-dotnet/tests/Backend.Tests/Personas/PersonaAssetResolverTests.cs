using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Personas;

/// <summary>
/// Direct unit coverage for PersonaAssetResolver.Resolve's traversal and symlink defense. The
/// pinned HTTP-level conformance rows (PersonaAssetRouteConformanceTests.Persona_asset_route_
/// rejects_path_traversal_attempts, since Rick's PR #122 review item 1 pointed every row at
/// `persona.json`, one level above `assets/`, instead of the blind two-levels-up `app.py` target
/// the original rows used) exercise the SAME production code path, but the literal `../` and
/// `..%2f`/`..%2F` payloads are still neutralised BEFORE Resolve ever runs: HttpClient normalises
/// a literal "../" client-side, and ASP.NET Core's routing never decodes "%2f" into a real path
/// separator (so an encoded-slash payload arrives as a single opaque segment, not a literal ".."
/// segment) -- confirmed empirically (a mutation that deletes the segment-level ".." check AND
/// makes IsUnderRoot unconditionally true leaves those rows green regardless of what Resolve does,
/// because none of those payloads can ever reach a real file through Kestrel/HttpClient). The
/// `..%5cpersona.json` row added by that same review item is DIFFERENT: Kestrel decodes `%5c` to a
/// literal backslash before routing, so that row genuinely reaches Resolve's own segment check --
/// the same mutation above DOES turn it red on the dotnet leg (see the PR body's mutation
/// evidence). These unit tests call Resolve directly with literal ".." and symlinked-directory
/// inputs, bypassing HttpClient/Kestrel's own encoding quirks entirely, so a regression in the
/// resolver's own containment logic is caught here even for the payloads the HTTP-level
/// conformance suite cannot observe it through.
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

    /// <summary>
    /// Rick's PR #122 review item 3: a symlinked directory PLANTED INSIDE `assets/` that points
    /// back OUTSIDE the pack must not let a request underneath it escape containment -- the
    /// leaf-only symlink check an earlier draft had would miss this (the leaf, `secret.txt`, is
    /// never itself a symlink; the DIRECTORY one segment above it is). The one symlink this test
    /// creates lives entirely under `$env:TEMP`/`$TMPDIR` -- inside <see cref="PersonaPackFixture"/>'s
    /// own throwaway pack copy (never the real, shared `personas/` tree) -- and is deleted by this
    /// test's own `finally` block, never left behind.
    ///
    /// Creating a directory symlink needs either an elevated process or Windows Developer Mode;
    /// skips (doesn't fail) when the OS/user refuses -- Linux CI runs this for real.
    /// </summary>
    [Fact]
    public void Symlinked_directory_inside_assets_cannot_escape_the_pack_root()
    {
        var catalog = PersonaCatalog.Load(personasDir: _fixture.PersonasDir);
        var persona = catalog.Get("sonic");

        var outsideDir = Path.Combine(Path.GetTempPath(), "beth-persona-symlink-outside-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(outsideDir);
        var secretPath = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(secretPath, "must never be reachable through /personas/sonic/assets/*");

        var symlinkPath = Path.Combine(persona.AssetsDir, "escape");
        try
        {
            Directory.CreateSymbolicLink(symlinkPath, outsideDir);
        }
        catch (Exception exc) when (exc is UnauthorizedAccessException or IOException)
        {
            Directory.Delete(outsideDir, recursive: true);
            Assert.Skip(
                "Creating a directory symlink was refused (Windows without Developer Mode / an " +
                "unprivileged process); Linux CI exercises this scenario for real.");
            return;
        }

        try
        {
            var resolved = PersonaAssetResolver.Resolve(persona, "escape/secret.txt");

            Assert.Null(resolved);
        }
        finally
        {
            // Deletes only the symlink entry itself -- .NET does not traverse a directory
            // reparse point on delete, so `outsideDir`'s own contents are untouched here.
            Directory.Delete(symlinkPath);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    public void Dispose() => _fixture.Dispose();
}
