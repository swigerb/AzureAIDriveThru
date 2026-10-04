using UpdateMenuSizes;

// Faithful C# port of scripts/update_menu_sizes.py (issue #16). With no arguments, resolves the
// same repo-relative default paths the Python twin hardcodes
// (personas/sonic/menu/source/sonic-menu-items.json, personas/sonic/menu/menuItems.json), so
// `dotnet run --project tools/dotnet/src/UpdateMenuSizes` behaves like
// `python scripts/update_menu_sizes.py`. --production/--menu let tests (and anyone who wants a
// dry run against a copy) point at different files without touching the real ones.
var (productionPath, menuPath) = ParseArgs(args);
var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);
foreach (var line in result.Log)
{
    Console.WriteLine(line);
}
return 0;

static (string ProductionPath, string MenuPath) ParseArgs(string[] args)
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
                Console.WriteLine(
                    "Usage: dotnet run --project tools/dotnet/src/UpdateMenuSizes -- " +
                    "[--production <sonic-menu-items.json>] [--menu <menuItems.json>]");
                Environment.Exit(0);
                break;
        }
    }

    if (production is not null && menu is not null)
    {
        return (production, menu);
    }

    var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
    production ??= Path.Combine(repoRoot, "personas", "sonic", "menu", "source", "sonic-menu-items.json");
    menu ??= Path.Combine(repoRoot, "personas", "sonic", "menu", "menuItems.json");
    return (production, menu);
}
