namespace UpdateMenuSizes;

/// <summary>
/// Discovers the one persona this tool operates on by globbing
/// <c>personas/*/menu/source/*-menu-items.json</c> under the repo root, then -- among personas
/// that have one -- selecting the one that has also OPTED IN by placing a sibling
/// <c>menu/product_search_map.json</c> next to its export, and deriving that persona's
/// conventional sibling files (<c>menuItems.json</c>, <c>product_search_map.json</c>) from the
/// match -- issue #16, PR #224 review R1: Program.cs's CLI defaults and PythonParityTests.cs's
/// real-fixture lookup must come from data (which persona packs actually exist on disk), not a
/// hardcoded persona-id literal, so this tool and its tests keep working unchanged as persona
/// packs are added/renamed/removed.
///
/// The opt-in filter (PR #224 review R3) exists because docs/persona-architecture.md's planned
/// multi-persona layout means a SECOND persona can add its own
/// <c>menu/source/*-menu-items.json</c> export (porting its own POS data) without wanting this
/// specific Mini/RT-44 size-variant reconciliation tool at all, and without that addition breaking
/// the FIRST persona's existing, already-opted-in automated run: only personas with a
/// <c>product_search_map.json</c> are candidates, so an un-opted-in persona's export is invisible
/// to this tool.
///
/// Exactly one OPTED-IN persona is supported per run (matching the Python twin, which hardcodes
/// one persona's paths); zero or multiple opted-in matches is a configuration error the caller
/// must resolve by passing --production/--menu explicitly.
/// </summary>
public static class PersonaMenuLocator
{
    /// <summary>A discovered persona's menu-port-relevant files.</summary>
    public sealed record Discovery(
        string PersonaId,
        string ProductionFilePath,
        string MenuFilePath,
        string ProductSearchMapFilePath);

    public static Discovery Locate(string repoRoot)
    {
        var personasDir = Path.Combine(repoRoot, "personas");
        var candidates = FindProductionExports(personasDir);

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No persona production export matched 'personas/*/menu/source/*-menu-items.json' " +
                $"under '{repoRoot}'. Pass --production and --menu explicitly, or add a persona pack " +
                "with that layout.");
        }

        var optedIn = candidates.Where(HasOptedIn).ToList();

        if (optedIn.Count == 0)
        {
            throw new InvalidOperationException(
                "No persona has opted into this tool: none of the persona production export(s) " +
                "found (" + string.Join(", ", candidates) + ") has a sibling " +
                "'menu/product_search_map.json'. Pass --production and --menu explicitly, or add " +
                "that persona's own product_search_map.json next to its export to opt in.");
        }
        if (optedIn.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one persona has opted into this tool (each has both a " +
                "'menu/source/*-menu-items.json' export and a sibling " +
                "'menu/product_search_map.json'): " + string.Join(", ", optedIn) +
                ". This tool operates on exactly one persona at a time -- pass --production and " +
                "--menu explicitly to pick one.");
        }

        var productionFilePath = optedIn[0];
        var menuDir = MenuDirectoryFor(productionFilePath);
        var personaDir = Directory.GetParent(menuDir)!.FullName;   // personas/<id>
        var personaId = Path.GetFileName(personaDir);

        return new Discovery(
            personaId,
            productionFilePath,
            Path.Combine(menuDir, "menuItems.json"),
            ProductSearchMapPathFor(productionFilePath));
    }

    /// <summary><c>personas/&lt;id&gt;/menu/source/&lt;name&gt;-menu-items.json</c> -&gt;
    /// <c>personas/&lt;id&gt;/menu</c>.</summary>
    private static string MenuDirectoryFor(string productionFilePath)
    {
        var sourceDir = Path.GetDirectoryName(productionFilePath)!; // personas/<id>/menu/source
        return Directory.GetParent(sourceDir)!.FullName;            // personas/<id>/menu
    }

    private static string ProductSearchMapPathFor(string productionFilePath) =>
        Path.Combine(MenuDirectoryFor(productionFilePath), "product_search_map.json");

    private static bool HasOptedIn(string productionFilePath) =>
        File.Exists(ProductSearchMapPathFor(productionFilePath));

    private static List<string> FindProductionExports(string personasDir)
    {
        if (!Directory.Exists(personasDir))
        {
            return [];
        }

        var matches = new List<string>();
        foreach (var personaDir in Directory.GetDirectories(personasDir))
        {
            var sourceDir = Path.Combine(personaDir, "menu", "source");
            if (!Directory.Exists(sourceDir))
            {
                continue;
            }
            matches.AddRange(Directory.GetFiles(sourceDir, "*-menu-items.json"));
        }
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }
}
