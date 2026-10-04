namespace UpdateMenuSizes;

/// <summary>
/// The CLI's argument parsing, persona discovery, and top-level error handling, factored out of
/// Program.cs's entry point (rather than left as top-level-statement local functions) so it can be
/// unit-tested directly against <see cref="TextWriter"/>s instead of spawning a real process --
/// PR #224 review R3: Program.cs must catch <see cref="PersonaMenuLocator"/>'s discovery errors and
/// print a clean, single-line message plus a non-zero exit code, not an unhandled-exception stack
/// trace.
/// </summary>
public static class CliRunner
{
    public const string HelpText =
        "Usage: dotnet run --project tools/dotnet/src/UpdateMenuSizes -- " +
        "[--production <*-menu-items.json>] [--menu <menuItems.json>] " +
        "[--product-search-map <product_search_map.json>]";

    /// <param name="repoRootOverride">Normally omitted -- defaults to
    /// <c>RepoRoot.Find(AppContext.BaseDirectory)</c>. Lets tests exercise the
    /// PersonaMenuLocator-discovery-fails/clean-error/exit-1 path (CliRunnerTests) against a
    /// synthetic directory tree instead of the real checked-in repo, which always has exactly one
    /// opted-in persona today and so would never actually fail discovery.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? repoRootOverride = null)
    {
        string? production = null;
        string? menu = null;
        string? productSearchMap = null;

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
                case "--product-search-map" when i + 1 < args.Length:
                    productSearchMap = args[++i];
                    break;
                case "--help" or "-h":
                    stdout.WriteLine(HelpText);
                    return 0;
            }
        }

        string productionPath;
        string menuPath;
        string productSearchMapPath;

        if (production is not null)
        {
            // An explicit --production skips persona discovery entirely (PR #224 review R3): the
            // caller has already picked a persona, so this run must not also need (or be
            // confused/blocked by) however many OTHER persona packs happen to exist on disk --
            // including the "more than one persona has opted in" case PersonaMenuLocator itself
            // would otherwise refuse. --menu/--product-search-map still default to the same
            // menu/source-sibling convention PersonaMenuLocator uses, derived from whichever of
            // --production/--menu was actually given.
            string menuDir;
            if (menu is not null)
            {
                menuDir = Path.GetDirectoryName(Path.GetFullPath(menu))!;
            }
            else
            {
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(production))!; // .../menu/source
                menuDir = Directory.GetParent(sourceDir)!.FullName;                   // .../menu
                menu = Path.Combine(menuDir, "menuItems.json");
            }
            productionPath = production;
            menuPath = menu;
            productSearchMapPath = productSearchMap ?? Path.Combine(menuDir, "product_search_map.json");
        }
        else
        {
            PersonaMenuLocator.Discovery discovery;
            try
            {
                var repoRoot = repoRootOverride ?? RepoRoot.Find(AppContext.BaseDirectory);
                discovery = PersonaMenuLocator.Locate(repoRoot);
            }
            catch (InvalidOperationException ex)
            {
                // A clean, single-line, actionable message -- not an unhandled-exception stack
                // trace -- since this is an expected, resolvable configuration state (no persona
                // has opted in yet, more than one has, or no persona pack exists at all), not a
                // bug in this tool.
                stderr.WriteLine($"update-menu-sizes: {ex.Message}");
                return 1;
            }
            productionPath = discovery.ProductionFilePath;
            menuPath = menu ?? discovery.MenuFilePath;
            productSearchMapPath = productSearchMap ?? discovery.ProductSearchMapFilePath;
        }

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, productSearchMapPath);
        foreach (var line in result.Log)
        {
            stdout.WriteLine(line);
        }
        return 0;
    }
}
