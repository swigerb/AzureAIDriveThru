namespace ExtractProductionItems;

/// <summary>
/// Discovers the one persona this tool operates on by globbing
/// <c>personas/*/menu/source/*-menu-items.json</c> under the repo root, and deriving that
/// persona's sibling <c>menuItems.json</c> from the match -- issue #16 batch 1, same data-driven
/// discovery rationale as <c>UpdateMenuSizes/PersonaMenuLocator.cs</c> (PR #224 review R1): CLI
/// defaults and PythonParityTests.cs's real-fixture lookup must come from data (which persona
/// packs actually exist on disk), not a hardcoded persona-id literal.
///
/// Unlike <c>UpdateMenuSizes/PersonaMenuLocator</c>, this tool needs no
/// <c>product_search_map.json</c> opt-in file -- scripts/extract_production_items.py only ever
/// reads a production export and a sibling menuItems.json, so EVERY persona with a production
/// export is a candidate here, not just the ones that opted into the sizing tool. Exactly one
/// candidate is supported per run (matching the Python twin, which hardcodes one persona's paths);
/// zero or multiple matches is a configuration error the caller must resolve by passing
/// --production/--menu explicitly.
/// </summary>
public static class ProductionExportLocator
{
    /// <summary>A discovered persona's production-export-relevant files.</summary>
    public sealed record Discovery(string PersonaId, string ProductionFilePath, string MenuFilePath);

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
        if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one persona has a production export " +
                "('menu/source/*-menu-items.json'): " + string.Join(", ", candidates) +
                ". This tool operates on exactly one persona at a time -- pass --production and " +
                "--menu explicitly to pick one.");
        }

        var productionFilePath = candidates[0];
        var sourceDir = Path.GetDirectoryName(productionFilePath)!; // personas/<id>/menu/source
        var menuDir = Directory.GetParent(sourceDir)!.FullName;     // personas/<id>/menu
        var personaDir = Directory.GetParent(menuDir)!.FullName;    // personas/<id>
        var personaId = Path.GetFileName(personaDir);

        return new Discovery(personaId, productionFilePath, Path.Combine(menuDir, "menuItems.json"));
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
