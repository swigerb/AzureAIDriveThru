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

    /// <param name="args">The raw command-line arguments. An unrecognized argument, or a flag
    /// missing its value, is a usage error (exit code 2) rather than being silently ignored.</param>
    /// <param name="stdout">Receives the report (or <see cref="HelpText"/> for <c>--help</c>).</param>
    /// <param name="stderr">Receives the single-line error message for a usage or discovery error.</param>
    /// <param name="repoRootOverride">Normally omitted -- defaults to
    /// <c>RepoRoot.Find(AppContext.BaseDirectory)</c>. Lets tests exercise the
    /// ProductionExportLocator-discovery-fails/clean-error/exit-1 path against a synthetic
    /// directory tree instead of the real checked-in repo, which always has exactly one persona
    /// production export today and so would never actually fail discovery.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? repoRootOverride = null)
    {
        string? production = null;
        string? menu = null;

        for (int i = 0; i < args.Length; i++)
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
                case "--production" or "--menu":
                    return UsageError(stderr, $"argument {args[i]}: expected one argument");
                default:
                    // A typo'd flag must not silently fall back to persona discovery and run
                    // against the real checked-in files.
                    return UsageError(stderr, $"unrecognized arguments: {args[i]}");
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
                string sourceDir = Path.GetDirectoryName(Path.GetFullPath(production))!; // .../menu/source
                string menuDir = Directory.GetParent(sourceDir)!.FullName;               // .../menu
                menuPath = Path.Combine(menuDir, "menuItems.json");
            }
        }
        else
        {
            ProductionExportLocator.Discovery discovery;
            try
            {
                string repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
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

        IReadOnlyList<ProductionItem> productionItems = ProductionItemsExtractor.ExtractProductionItems(productionPath);
        IReadOnlyList<UiItem> uiItems = ProductionItemsExtractor.LoadUiItems(menuPath);
        IReadOnlyList<string> report = ProductionItemsExtractor.BuildReport(productionItems, uiItems);
        foreach (string line in report)
        {
            stdout.WriteLine(line);
        }
        return 0;
    }

    private static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine($"extract-production-items: {message}");
        stderr.WriteLine(HelpText);
        return 2;
    }
}
