namespace UpdateMenuSizes.Tests;

/// <summary>
/// Unit tests for PersonaMenuLocator's opt-in persona discovery (PR #224 review R3):
/// docs/persona-architecture.md's planned multi-persona layout means a second persona can add its
/// own menu/source/*-menu-items.json export without wanting this tool, so discovery must only
/// consider personas that have ALSO placed a sibling menu/product_search_map.json next to their
/// export ("opted in") -- these tests build synthetic personas/ layouts under a fresh temp
/// directory (0, 1, and 2-persona-only-one-opted-in, plus a bonus 2-opted-in ambiguity case) so
/// they're fast, deterministic, and independent of whatever real persona packs exist in the repo
/// today.
/// </summary>
public sealed class PersonaMenuLocatorTests : IDisposable
{
    private readonly string _repoRoot =
        Path.Combine(Path.GetTempPath(), $"squanchy-persona-locator-{Guid.NewGuid():n}");

    public PersonaMenuLocatorTests()
    {
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }

    /// <summary>Creates personas/{personaId}/menu/source/{personaId}-menu-items.json, and -- when
    /// <paramref name="optIn"/> is true -- a sibling personas/{personaId}/menu/product_search_map.json,
    /// matching the real persona-pack layout PersonaMenuLocator globs for.</summary>
    private void CreatePersona(string personaId, bool optIn)
    {
        var menuDir = Path.Combine(_repoRoot, "personas", personaId, "menu");
        var sourceDir = Path.Combine(menuDir, "source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, $"{personaId}-menu-items.json"), "[]");
        if (optIn)
        {
            File.WriteAllText(Path.Combine(menuDir, "product_search_map.json"), "{}");
        }
    }

    [Fact]
    public void Locate_ThrowsCleanError_WhenNoPersonaHasAProductionExport()
    {
        // personas/ exists but is otherwise empty -- the 0-persona layout.
        Directory.CreateDirectory(Path.Combine(_repoRoot, "personas"));

        var ex = Assert.Throws<InvalidOperationException>(() => PersonaMenuLocator.Locate(_repoRoot));

        Assert.Contains("No persona production export matched", ex.Message);
    }

    [Fact]
    public void Locate_ThrowsCleanError_WhenPersonasDirectoryDoesNotExistAtAll()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PersonaMenuLocator.Locate(_repoRoot));

        Assert.Contains("No persona production export matched", ex.Message);
    }

    [Fact]
    public void Locate_SelectsTheSinglePersona_WhenExactlyOneExistsAndHasOptedIn()
    {
        // The 1-persona layout -- mirrors today's only real-world persona-pack shape (one
        // persona with a production export and a sibling product_search_map.json), using a
        // synthetic persona id here so this test stays independent of whichever real persona
        // pack(s) happen to be checked into the repo.
        CreatePersona("acme", optIn: true);

        var discovery = PersonaMenuLocator.Locate(_repoRoot);

        Assert.Equal("acme", discovery.PersonaId);
        Assert.Equal(
            Path.Combine(_repoRoot, "personas", "acme", "menu", "source", "acme-menu-items.json"),
            discovery.ProductionFilePath);
        Assert.Equal(
            Path.Combine(_repoRoot, "personas", "acme", "menu", "menuItems.json"),
            discovery.MenuFilePath);
        Assert.Equal(
            Path.Combine(_repoRoot, "personas", "acme", "menu", "product_search_map.json"),
            discovery.ProductSearchMapFilePath);
    }

    [Fact]
    public void Locate_ThrowsCleanError_WhenOnePersonaExistsButHasNotOptedIn()
    {
        CreatePersona("acme", optIn: false);

        var ex = Assert.Throws<InvalidOperationException>(() => PersonaMenuLocator.Locate(_repoRoot));

        Assert.Contains("No persona has opted into this tool", ex.Message);
    }

    [Fact]
    public void Locate_SelectsTheOptedInPersona_WhenASecondPersonaAddsAnExportWithoutOptingIn()
    {
        // The exact scenario PR #224 review R3 called out: a second persona (e.g. "globex")
        // adds its own production export per docs/persona-architecture.md's planned layout, but
        // has not opted into THIS tool -- "acme" (already opted in) must still be found cleanly,
        // with no ambiguity error.
        CreatePersona("acme", optIn: true);
        CreatePersona("globex", optIn: false);

        var discovery = PersonaMenuLocator.Locate(_repoRoot);

        Assert.Equal("acme", discovery.PersonaId);
    }

    [Fact]
    public void Locate_ThrowsAmbiguityError_WhenTwoPersonasHaveBothOptedIn()
    {
        CreatePersona("acme", optIn: true);
        CreatePersona("globex", optIn: true);

        var ex = Assert.Throws<InvalidOperationException>(() => PersonaMenuLocator.Locate(_repoRoot));

        Assert.Contains("More than one persona has opted into this tool", ex.Message);
    }
}
