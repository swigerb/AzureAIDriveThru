namespace ExtractProductionItems.Tests;

/// <summary>
/// Unit tests for ProductionExportLocator's persona discovery (issue #16 batch 1, mirroring
/// UpdateMenuSizes/PersonaMenuLocatorTests.cs's rigor): 0-persona, 1-persona, and
/// 2-persona-with-a-production-export layouts, built under a fresh synthetic temp directory so
/// they're fast, deterministic, and independent of whatever real persona packs exist in the repo
/// today. Unlike PersonaMenuLocator, there is no opt-in file here -- every persona with a
/// production export is a candidate.
/// </summary>
public sealed class ProductionExportLocatorTests : IDisposable
{
    private readonly string _repoRoot =
        Path.Combine(Path.GetTempPath(), $"squanchy-production-export-locator-{Guid.NewGuid():n}");

    public ProductionExportLocatorTests()
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

    /// <summary>Creates personas/{personaId}/menu/source/{personaId}-menu-items.json, matching the
    /// real persona-pack layout ProductionExportLocator globs for.</summary>
    private void CreatePersona(string personaId)
    {
        var sourceDir = Path.Combine(_repoRoot, "personas", personaId, "menu", "source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, $"{personaId}-menu-items.json"), "{}");
    }

    [Fact]
    public void Locate_ThrowsCleanError_WhenNoPersonaHasAProductionExport()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "personas"));

        var ex = Assert.Throws<InvalidOperationException>(() => ProductionExportLocator.Locate(_repoRoot));

        Assert.Contains("No persona production export matched", ex.Message);
    }

    [Fact]
    public void Locate_ThrowsCleanError_WhenPersonasDirectoryDoesNotExistAtAll()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProductionExportLocator.Locate(_repoRoot));

        Assert.Contains("No persona production export matched", ex.Message);
    }

    [Fact]
    public void Locate_SelectsTheSinglePersona_WhenExactlyOneHasAProductionExport()
    {
        // A synthetic persona id, so this test stays independent of whichever real persona
        // pack(s) happen to be checked into the repo.
        CreatePersona("acme");

        var discovery = ProductionExportLocator.Locate(_repoRoot);

        Assert.Equal("acme", discovery.PersonaId);
        Assert.Equal(
            Path.Combine(_repoRoot, "personas", "acme", "menu", "source", "acme-menu-items.json"),
            discovery.ProductionFilePath);
        Assert.Equal(
            Path.Combine(_repoRoot, "personas", "acme", "menu", "menuItems.json"),
            discovery.MenuFilePath);
    }

    [Fact]
    public void Locate_ThrowsAmbiguityError_WhenTwoPersonasBothHaveAProductionExport()
    {
        // Unlike PersonaMenuLocator (UpdateMenuSizes), there is no opt-in file here -- ANY second
        // persona with its own production export is immediately ambiguous, since this tool (like
        // its Python twin) operates on exactly one persona's production data per run.
        CreatePersona("acme");
        CreatePersona("globex");

        var ex = Assert.Throws<InvalidOperationException>(() => ProductionExportLocator.Locate(_repoRoot));

        Assert.Contains("More than one persona has a production export", ex.Message);
    }

    [Fact]
    public void Locate_IgnoresAPersonaWithNoMenuSourceDirectory()
    {
        // A persona pack that exists but has no menu/source/*-menu-items.json at all (e.g. a UI-
        // only persona pack with just a menuItems.json) must not count as a candidate and must not
        // make an otherwise-unambiguous discovery fail.
        var uiOnlyMenuDir = Path.Combine(_repoRoot, "personas", "ui-only", "menu");
        Directory.CreateDirectory(uiOnlyMenuDir);
        File.WriteAllText(Path.Combine(uiOnlyMenuDir, "menuItems.json"), "{}");
        CreatePersona("acme");

        var discovery = ProductionExportLocator.Locate(_repoRoot);

        Assert.Equal("acme", discovery.PersonaId);
    }
}
