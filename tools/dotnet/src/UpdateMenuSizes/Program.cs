using UpdateMenuSizes;

// Faithful C# port of scripts/update_menu_sizes.py (issue #16). With no arguments, discovers the
// one persona pack checked into the repo (personas/*/menu/source/*-menu-items.json, via
// PersonaMenuLocator) and resolves its conventional sibling files, so
// `dotnet run --project tools/dotnet/src/UpdateMenuSizes` behaves like
// `python scripts/update_menu_sizes.py` for whichever persona exists today -- no persona id is
// hardcoded here. --production/--menu/--product-search-map let tests (and anyone who wants a dry
// run against a copy) point at different files without touching the real ones.
var (productionPath, menuPath, productSearchMapPath) = ParseArgs(args);
var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, productSearchMapPath);
foreach (var line in result.Log)
{
    Console.WriteLine(line);
}
return 0;

static (string ProductionPath, string MenuPath, string ProductSearchMapPath) ParseArgs(string[] args)
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
                Console.WriteLine(
                    "Usage: dotnet run --project tools/dotnet/src/UpdateMenuSizes -- " +
                    "[--production <*-menu-items.json>] [--menu <menuItems.json>] " +
                    "[--product-search-map <product_search_map.json>]");
                Environment.Exit(0);
                break;
        }
    }

    if (production is not null && menu is not null)
    {
        // Same convention PersonaMenuLocator uses: the product search map lives alongside
        // menuItems.json unless the caller overrides it explicitly.
        productSearchMap ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(menu))!, "product_search_map.json");
        return (production, menu, productSearchMap);
    }

    var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
    var discovery = PersonaMenuLocator.Locate(repoRoot);
    production ??= discovery.ProductionFilePath;
    menu ??= discovery.MenuFilePath;
    productSearchMap ??= discovery.ProductSearchMapFilePath;
    return (production, menu, productSearchMap);
}
