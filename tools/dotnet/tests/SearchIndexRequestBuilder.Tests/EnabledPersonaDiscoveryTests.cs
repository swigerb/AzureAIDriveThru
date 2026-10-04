namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for EnabledPersonaDiscovery's 0/1/N-persona layouts, built under a fresh synthetic
/// temp directory (same convention as ExtractProductionItems.Tests/ProductionExportLocatorTests.cs
/// and UpdateMenuSizes.Tests/PersonaMenuLocatorTests.cs). Unlike those two tools' locators, this one
/// has no "pick exactly one" ambiguity -- a second (or third) persona pack is never an error here,
/// only more personas to discover -- so these tests assert ALL enabled personas come back, in
/// sorted order, rather than throwing on more than one.
/// </summary>
public sealed class EnabledPersonaDiscoveryTests : IDisposable
{
    private readonly string _repoRoot =
        Path.Combine(Path.GetTempPath(), $"squanchy-enabled-persona-discovery-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }

    private void CreatePersona(string personaId, string indexName)
    {
        var personaDir = Path.Combine(_repoRoot, "personas", personaId);
        Directory.CreateDirectory(Path.Combine(personaDir, "menu"));
        File.WriteAllText(
            Path.Combine(personaDir, "persona.json"),
            "{\"search\": {\"indexName\": \"" + indexName + "\"}}");
        File.WriteAllText(Path.Combine(personaDir, "menu", "menuItems.json"), """{"menuItems": []}""");
    }

    [Fact]
    public void DiscoverAll_Throws_WhenNoPersonasDirectoryExists()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => EnabledPersonaDiscovery.DiscoverAll(_repoRoot));
        Assert.Contains("No 'personas' directory found", ex.Message);
    }

    [Fact]
    public void DiscoverAll_Throws_WhenPersonasDirectoryIsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "personas"));
        var ex = Assert.Throws<InvalidOperationException>(() => EnabledPersonaDiscovery.DiscoverAll(_repoRoot));
        Assert.Contains("No personas enabled", ex.Message);
    }

    [Fact]
    public void DiscoverAll_FindsExactlyOnePersona_WhenOnlyOneExists()
    {
        CreatePersona("alpha", "alpha-menu-items");
        var result = EnabledPersonaDiscovery.DiscoverAll(_repoRoot);

        var persona = Assert.Single(result);
        Assert.Equal("alpha", persona.PersonaId);
        Assert.Equal("alpha-menu-items", persona.IndexName);
        Assert.Equal(Path.Combine(_repoRoot, "personas", "alpha", "menu", "menuItems.json"), persona.MenuPath);
    }

    [Fact]
    public void DiscoverAll_FindsEveryPersona_WhenMultipleExist_SortedOrdinally()
    {
        CreatePersona("zulu", "zulu-menu-items");
        CreatePersona("alpha", "alpha-menu-items");
        CreatePersona("mike", "mike-menu-items");

        var result = EnabledPersonaDiscovery.DiscoverAll(_repoRoot);

        Assert.Equal(["alpha", "mike", "zulu"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void DiscoverAll_IgnoresFoldersWithoutAPersonaManifest()
    {
        CreatePersona("alpha", "alpha-menu-items");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "personas", "not-a-persona"));

        var result = EnabledPersonaDiscovery.DiscoverAll(_repoRoot);

        Assert.Equal(["alpha"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void DiscoverAll_Throws_WhenAPersonaManifestHasNoIndexName()
    {
        var personaDir = Path.Combine(_repoRoot, "personas", "broken");
        Directory.CreateDirectory(Path.Combine(personaDir, "menu"));
        File.WriteAllText(Path.Combine(personaDir, "persona.json"), "{}");

        var ex = Assert.Throws<InvalidOperationException>(() => EnabledPersonaDiscovery.DiscoverAll(_repoRoot));
        Assert.Contains("search.indexName", ex.Message);
    }

    [Fact]
    public void DiscoverAll_UsesPersonasDirOverride_InsteadOfRepoRootPersonas_WhenGiven()
    {
        // Issue #16 (SearchIndexIngestor's --personas-dir flag): an explicit override bypasses
        // <repoRoot>/personas entirely -- even one that exists and has its own (different)
        // personas -- matching persona_loader.PersonaCatalog.load(personas_dir=...)'s own
        // "explicit argument wins outright" precedence.
        CreatePersona("repo-root-persona", "repo-root-menu-items");

        var overrideDir = Path.Combine(Path.GetTempPath(), $"squanchy-personas-dir-override-{Guid.NewGuid():n}");
        try
        {
            Directory.CreateDirectory(Path.Combine(overrideDir, "fixture-persona", "menu"));
            File.WriteAllText(
                Path.Combine(overrideDir, "fixture-persona", "persona.json"),
                "{\"search\": {\"indexName\": \"fixture-menu-items\"}}");
            File.WriteAllText(
                Path.Combine(overrideDir, "fixture-persona", "menu", "menuItems.json"),
                """{"menuItems": []}""");

            var result = EnabledPersonaDiscovery.DiscoverAll(_repoRoot, overrideDir);

            var persona = Assert.Single(result);
            Assert.Equal("fixture-persona", persona.PersonaId);
            Assert.Equal("fixture-menu-items", persona.IndexName);
        }
        finally
        {
            Directory.Delete(overrideDir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAll_IgnoresPersonasDirOverride_WhenNullOrEmpty()
    {
        CreatePersona("alpha", "alpha-menu-items");

        Assert.Equal(["alpha"], EnabledPersonaDiscovery.DiscoverAll(_repoRoot, null).Select(p => p.PersonaId));
        Assert.Equal(["alpha"], EnabledPersonaDiscovery.DiscoverAll(_repoRoot, "").Select(p => p.PersonaId));
    }
}
