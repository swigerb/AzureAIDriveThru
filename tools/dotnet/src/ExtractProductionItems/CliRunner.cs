namespace ExtractProductionItems;

/// <summary>
/// The CLI's argument parsing, persona discovery, and top-level error handling, factored out of
/// Program.cs's entry point so it can be unit-tested directly against <see cref="TextWriter"/>s
/// instead of spawning a real process -- same shape as UpdateMenuSizes/CliRunner.cs (PR #224
/// review R3): ProductionExportLocator's discovery errors must print a clean, single-line message
/// plus a non-zero exit code, not an unhandled-exception stack trace.
/// </summary>
public static class CliRunner
{
    public const string HelpText =
        "Usage: dotnet run --project tools/dotnet/src/ExtractProductionItems -- " +
        "[--production <*-menu-items.json>] [--menu <menuItems.json>]";

    /// <param name="repoRootOverride">Normally omitted -- defaults to
    /// <c>RepoRoot.Find(AppContext.BaseDirectory)</c>. Lets tests exercise the
    /// ProductionExportLocator-discovery-fails/clean-error/exit-1 path against a synthetic
    /// directory tree instead of the real checked-in repo, which always has exactly one persona
    /// production export today and so would never actually fail discovery.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? repoRootOverride = null)
    {
        string? production = null;
        string? menu = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--production" when i + 1 < args.Length:
                    production = args[++i];
                    break;
                case "--menu" when i + 1 < args.Length:
                    menu = args[++i];
                    break;
                case "--help" or "-h":
                    stdout.WriteLine(HelpText);
                    return 0;
            }
        }

        string productionPath;
        string menuPath;

        if (production is not null)
        {
            // An explicit --production skips persona discovery entirely, same rationale as
            // UpdateMenuSizes/CliRunner.cs: the caller has already picked a persona, so this run
            // must not need (or be blocked by) however many OTHER persona packs happen to exist on
            // disk. --menu still defaults to the same menu/source-sibling convention
            // ProductionExportLocator uses, derived from --production.
            productionPath = production;
            if (menu is not null)
            {
                menuPath = menu;
            }
            else
            {
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(production))!; // .../menu/source
                var menuDir = Directory.GetParent(sourceDir)!.FullName;               // .../menu
                menuPath = Path.Combine(menuDir, "menuItems.json");
            }
        }
        else
        {
            ProductionExportLocator.Discovery discovery;
            try
            {
                var repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
                discovery = ProductionExportLocator.Locate(repoRoot);
            }
            catch (InvalidOperationException ex)
            {
                // A clean, single-line, actionable message -- not an unhandled-exception stack
                // trace -- since this is an expected, resolvable configuration state (no persona
                // export exists yet, or more than one does), not a bug in this tool.
                stderr.WriteLine($"extract-production-items: {ex.Message}");
                return 1;
            }
            productionPath = discovery.ProductionFilePath;
            menuPath = menu ?? discovery.MenuFilePath;
        }

        var productionItems = ProductionItemsExtractor.ExtractProductionItems(productionPath);
        var uiItems = ProductionItemsExtractor.LoadUiItems(menuPath);
        var report = ProductionItemsExtractor.BuildReport(productionItems, uiItems);
        foreach (var line in report)
        {
            stdout.WriteLine(line);
        }
        return 0;
    }
}
