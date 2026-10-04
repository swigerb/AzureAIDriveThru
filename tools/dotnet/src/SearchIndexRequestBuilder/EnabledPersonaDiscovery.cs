using System.Text.Json;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Discovers every enabled persona pack under <c>personas/</c> the exact same way
/// <c>persona_loader.PersonaCatalog.load()</c> resolves its default <c>enabled_ids</c> (no
/// <c>PERSONAS</c> env var override): every folder directly under <c>personas/</c> that has a
/// <c>persona.json</c>, sorted alphabetically by persona id (issue #16: setup_search_index.py's
/// own <c>run()</c> targets every one of these by default -- see
/// <c>resolve_target_personas</c> / <c>PersonaCatalog.load</c>'s docstring).
///
/// Deliberately lighter than <c>persona_loader.py</c>: it does not load/validate
/// <c>persona.schema.json</c>/<c>menu.schema.json</c> (jsonschema/pydantic), it only reads the one
/// field this tool needs (<c>search.indexName</c>) plus the conventional sibling
/// <c>menu/menuItems.json</c> path -- the same "no heavy validation pipeline" rationale already
/// used by ExtractProductionItems/ProductionExportLocator.cs and
/// UpdateMenuSizes/PersonaMenuLocator.cs. Unlike those two tools, this one has no "pick exactly one
/// persona" ambiguity to resolve: setup_search_index.py's default behaviour -- and so this
/// discovery's -- is "every enabled persona", so a second (or third) persona pack appearing is
/// never a configuration error here, only more personas to build a plan for (the real repo already
/// has three, each discovered purely from its own personas/*/persona.json folder).
/// </summary>
public static class EnabledPersonaDiscovery
{
    /// <summary>One enabled persona's id, search index name, and menu data path.</summary>
    public sealed record DiscoveredPersona(string PersonaId, string IndexName, string MenuPath);

    public static IReadOnlyList<DiscoveredPersona> DiscoverAll(string repoRoot)
    {
        var personasDir = Path.Combine(repoRoot, "personas");
        if (!Directory.Exists(personasDir))
        {
            throw new InvalidOperationException(
                $"No 'personas' directory found under repo root '{repoRoot}'.");
        }

        var personaIds = Directory.GetDirectories(personasDir)
            .Select(Path.GetFileName)
            .Where(id => id is not null && File.Exists(Path.Combine(personasDir, id, "persona.json")))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (personaIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"No personas enabled. Checked '{personasDir}' -- expected at least one persona " +
                "folder with a persona.json.");
        }

        return personaIds.Select(id => Discover(personasDir, id)).ToList();
    }

    private static DiscoveredPersona Discover(string personasDir, string personaId)
    {
        var personaDir = Path.Combine(personasDir, personaId);
        var manifestPath = Path.Combine(personaDir, "persona.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        if (!document.RootElement.TryGetProperty("search", out var search) ||
            !search.TryGetProperty("indexName", out var indexNameElement) ||
            indexNameElement.GetString() is not { Length: > 0 } indexName)
        {
            throw new InvalidOperationException(
                $"Persona '{personaId}': '{manifestPath}' has no string search.indexName.");
        }

        var menuPath = Path.Combine(personaDir, "menu", "menuItems.json");
        return new DiscoveredPersona(personaId, indexName, menuPath);
    }
}
