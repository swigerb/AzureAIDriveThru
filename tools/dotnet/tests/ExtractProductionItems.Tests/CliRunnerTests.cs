namespace ExtractProductionItems.Tests;

/// <summary>
/// Unit tests for CliRunner, driven directly against in-memory <see cref="TextWriter"/>s so
/// they're fast and deterministic -- no subprocess spawning needed. Mirrors
/// UpdateMenuSizes/CliRunnerTests.cs's coverage: (1) --production skips ProductionExportLocator
/// discovery entirely; (2) --menu derived from --production's sibling menu directory when not
/// given explicitly; (3) ProductionExportLocator's InvalidOperationException is caught and turned
/// into a clean, single-line stderr message plus exit code 1, never an unhandled stack trace.
/// </summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string WriteTempJson(string prefix, string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squanchy-extract-cli-runner-{prefix}-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    private const string SampleProductionJson = """
        { "menus": { "menu-1": { "products": {}, "categories": {}, "productGroups": {} } } }
        """;
    private const string SampleMenuJson = """{ "menuItems": [] }""";

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
    public void Run_SkipsPersonaDiscoveryEntirely_WhenProductionAndMenuAreGivenExplicitly()
    {
        var production = WriteTempJson("production", SampleProductionJson);
        var menu = WriteTempJson("menu", SampleMenuJson);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--production", production, "--menu", menu], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
        Assert.Contains("Total production items: 0", stdout.ToString());
    }

    [Fact]
    public void Run_DerivesMenuPath_FromProductionsSiblingMenuDirectory_WhenOnlyProductionGiven()
    {
        // personas/<id>/menu/source/<id>-menu-items.json -> personas/<id>/menu/menuItems.json,
        // the same convention ProductionExportLocator uses -- exercised here via --production
        // alone, with no --menu and no real persona-discovery glob involved.
        var personaDir = Path.Combine(Path.GetTempPath(), $"squanchy-extract-cli-runner-persona-{Guid.NewGuid():n}");
        var menuDir = Path.Combine(personaDir, "menu");
        var sourceDir = Path.Combine(menuDir, "source");
        Directory.CreateDirectory(sourceDir);
        _tempDirs.Add(personaDir);

        var production = Path.Combine(sourceDir, "acme-menu-items.json");
        File.WriteAllText(production, SampleProductionJson);
        File.WriteAllText(Path.Combine(menuDir, "menuItems.json"), SampleMenuJson);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--production", production], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Run_PrintsCleanErrorAndReturnsNonZeroExitCode_WhenPersonaDiscoveryFails()
    {
        // No --production given, so CliRunner must call ProductionExportLocator.Locate --
        // pointed, via repoRootOverride, at a synthetic repo root with no "personas" directory at
        // all, so discovery deterministically fails here regardless of whatever real persona
        // packs the checked-in repo happens to have today.
        var emptyRepoRoot = Path.Combine(Path.GetTempPath(), $"squanchy-extract-cli-runner-empty-repo-{Guid.NewGuid():n}");
        Directory.CreateDirectory(emptyRepoRoot);
        _tempDirs.Add(emptyRepoRoot);

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

    [Fact]
    public void Run_PropagatesFileNotFoundException_ForABadExplicitProductionPath()
    {
        // --production bypasses discovery entirely, so a bad path here is CliRunner's caller's
        // own mistake, not a ProductionExportLocator ambiguity/absence -- confirms the catch
        // block is scoped to ProductionExportLocator.Locate's InvalidOperationException
        // specifically, not a blanket catch-all that would also mask real bugs in
        // ProductionItemsExtractor itself.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var ex = Record.Exception(() => CliRunner.Run(
            ["--production", Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():n}.json")],
            stdout,
            stderr));

        Assert.IsType<FileNotFoundException>(ex);
    }
}
