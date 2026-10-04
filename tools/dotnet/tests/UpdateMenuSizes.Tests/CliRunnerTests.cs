namespace UpdateMenuSizes.Tests;

/// <summary>
/// Unit tests for CliRunner (PR #224 review R3), driven directly against in-memory
/// <see cref="TextWriter"/>s so they're fast and deterministic -- no subprocess spawning needed.
/// Covers: (1) --production skips PersonaMenuLocator discovery entirely, even when other persona
/// packs on disk would otherwise make discovery ambiguous or impossible; (2) PersonaMenuLocator's
/// InvalidOperationException is caught and turned into a clean, single-line stderr message plus
/// exit code 1, never an unhandled stack trace.
/// </summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string WriteTempJson(string prefix, string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squanchy-cli-runner-{prefix}-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Run_PrintsHelpAndExitsZero_WhenHelpFlagGiven()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--help"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Run_SkipsPersonaDiscoveryEntirely_WhenProductionIsGivenExplicitly()
    {
        // No "personas" directory exists anywhere near AppContext.BaseDirectory's ancestors in
        // this scenario -- or if one does (e.g. the real repo this test runs in), it may well be
        // ambiguous/empty/whatever. None of that may matter or throw: --production must bypass
        // PersonaMenuLocator.Locate entirely.
        var menuDir = Path.Combine(Path.GetTempPath(), $"squanchy-cli-runner-menu-{Guid.NewGuid():n}");
        Directory.CreateDirectory(menuDir);
        try
        {
            var production = WriteTempJson("production", """
                { "menus": { "menu-1": { "products": {
                    "p-mini": { "displayName": "Mini Cherry Limeade", "price": 1.50 }
                } } } }
                """);
            var menu = Path.Combine(menuDir, "menuItems.json");
            File.WriteAllText(menu, """
                {
                  "menuItems": [
                    { "items": [ { "name": "Cherry Limeade", "sizes": [ { "size": "mini", "price": 1.50 } ] } ] }
                  ]
                }
                """);
            var productSearchMap = Path.Combine(menuDir, "product_search_map.json");
            File.WriteAllText(productSearchMap, """{ "Cherry Limeade": "Cherry Limeade" }""");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--production", production, "--menu", menu, "--product-search-map", productSearchMap],
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal("", stderr.ToString());
        }
        finally
        {
            Directory.Delete(menuDir, recursive: true);
        }
    }

    [Fact]
    public void Run_DerivesMenuAndProductSearchMapPaths_FromProductionsSiblingMenuDirectory_WhenOnlyProductionGiven()
    {
        // personas/<id>/menu/source/<id>-menu-items.json -> personas/<id>/menu/menuItems.json and
        // personas/<id>/menu/product_search_map.json, the same convention PersonaMenuLocator uses
        // -- exercised here via --production alone, with no --menu/--product-search-map and no
        // real persona-discovery glob involved.
        var personaDir = Path.Combine(Path.GetTempPath(), $"squanchy-cli-runner-persona-{Guid.NewGuid():n}");
        var menuDir = Path.Combine(personaDir, "menu");
        var sourceDir = Path.Combine(menuDir, "source");
        Directory.CreateDirectory(sourceDir);
        try
        {
            var production = Path.Combine(sourceDir, "acme-menu-items.json");
            File.WriteAllText(production, """
                { "menus": { "menu-1": { "products": {
                    "p-mini": { "displayName": "Mini Cherry Limeade", "price": 1.50 }
                } } } }
                """);
            File.WriteAllText(Path.Combine(menuDir, "menuItems.json"), """
                {
                  "menuItems": [
                    { "items": [ { "name": "Cherry Limeade", "sizes": [ { "size": "mini", "price": 1.50 } ] } ] }
                  ]
                }
                """);
            File.WriteAllText(
                Path.Combine(menuDir, "product_search_map.json"),
                """{ "Cherry Limeade": "Cherry Limeade" }""");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliRunner.Run(["--production", production], stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal("", stderr.ToString());
        }
        finally
        {
            Directory.Delete(personaDir, recursive: true);
        }
    }

    [Fact]
    public void Run_PrintsCleanErrorAndReturnsNonZeroExitCode_WhenPersonaDiscoveryFails()
    {
        // No --production given, so CliRunner must call PersonaMenuLocator.Locate -- pointed, via
        // repoRootOverride, at a synthetic repo root with no "personas" directory at all, so
        // discovery deterministically fails here regardless of whatever real persona packs the
        // checked-in repo happens to have today.
        var emptyRepoRoot = Path.Combine(Path.GetTempPath(), $"squanchy-cli-runner-empty-repo-{Guid.NewGuid():n}");
        Directory.CreateDirectory(emptyRepoRoot);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliRunner.Run([], stdout, stderr, repoRootOverride: emptyRepoRoot);

            Assert.Equal(1, exitCode);
            Assert.Equal("", stdout.ToString());
            var stderrText = stderr.ToString();
            Assert.Contains("No persona production export matched", stderrText);
            // A clean, single-line, actionable message -- not a stack trace.
            Assert.DoesNotContain("   at ", stderrText);
            Assert.DoesNotContain("System.InvalidOperationException", stderrText);
        }
        finally
        {
            Directory.Delete(emptyRepoRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_PropagatesFileNotFoundException_ForABadExplicitProductionPath()
    {
        // --production bypasses discovery entirely, so a bad path here is CliRunner's caller's
        // own mistake, not a PersonaMenuLocator ambiguity/absence -- confirms the catch block is
        // scoped to PersonaMenuLocator.Locate's InvalidOperationException specifically, not a
        // blanket catch-all that would also mask real bugs in MenuSizeUpdater itself.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var ex = Record.Exception(() => CliRunner.Run(
            ["--production", Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():n}.json")],
            stdout,
            stderr));

        Assert.IsType<FileNotFoundException>(ex);
    }
}
