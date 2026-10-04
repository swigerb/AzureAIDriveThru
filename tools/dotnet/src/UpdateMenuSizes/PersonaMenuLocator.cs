namespace UpdateMenuSizes;

/// <summary>
/// Discovers the one persona this tool operates on by globbing
/// <c>personas/*/menu/source/*-menu-items.json</c> under the repo root, and derives that
/// persona's conventional sibling files (<c>menuItems.json</c>, <c>product_search_map.json</c>)
/// alongside it -- issue #16, PR #224 review R1: Program.cs's CLI defaults and
/// PythonParityTests.cs's real-fixture lookup must come from data (which persona packs actually
/// exist on disk), not a hardcoded persona-id literal, so this tool and its tests keep working
/// unchanged as persona packs are added/renamed/removed.
///
/// Exactly one persona is supported per run (matching the Python twin, which hardcodes one
/// persona's paths); zero or multiple matches is a configuration error the caller must resolve by
/// passing --production/--menu explicitly.
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
        var matches = FindProductionExports(personasDir);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No persona production export matched 'personas/*/menu/source/*-menu-items.json' " +
                $"under '{repoRoot}'. Pass --production and --menu explicitly, or add a persona pack " +
                "with that layout.");
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one persona production export matched " +
                "'personas/*/menu/source/*-menu-items.json': " + string.Join(", ", matches) +
                ". This tool operates on exactly one persona at a time -- pass --production and " +
                "--menu explicitly to pick one.");
        }

        var productionFilePath = matches[0];
        var sourceDir = Path.GetDirectoryName(productionFilePath)!; // personas/<id>/menu/source
        var menuDir = Directory.GetParent(sourceDir)!.FullName;     // personas/<id>/menu
        var personaDir = Directory.GetParent(menuDir)!.FullName;    // personas/<id>
        var personaId = Path.GetFileName(personaDir);

        return new Discovery(
            personaId,
            productionFilePath,
            Path.Combine(menuDir, "menuItems.json"),
            Path.Combine(menuDir, "product_search_map.json"));
    }

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
