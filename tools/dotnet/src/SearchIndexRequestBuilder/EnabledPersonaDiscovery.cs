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

    /// <param name="repoRoot">The repo root to look for a <c>personas</c> directory under, when
    /// <paramref name="personasDirOverride"/> is not given.</param>
    /// <param name="personasDirOverride">Issue #16 (SearchIndexIngestor's <c>--personas-dir</c>
    /// flag): an explicit directory to use INSTEAD of <c>&lt;repoRoot&gt;/personas</c>, matching
    /// setup_search_index.py's own <c>--personas-dir</c> -&gt;
    /// <c>PersonaCatalog.load(personas_dir=...)</c> precedence -- an explicit value here always
    /// wins, the same way Python's explicit constructor argument beats its own <c>PERSONAS_DIR</c>
    /// environment-variable fallback. <c>null</c>/empty (the default, and
    /// SearchIndexRequestBuilder's only caller) means "use repoRoot/personas", matching this
    /// method's original, pre-#16-orchestration behaviour exactly.</param>
    /// <param name="enabledIdsOverride">Issue #16 round 2 (Rick's review, 2a): an explicit allow-list
    /// of persona ids to use INSTEAD OF "every folder with a persona.json", matching
    /// <c>persona_loader.PersonaCatalog.load()</c>'s own <c>PERSONAS</c> env-var allow-list
    /// precedence (SearchIndexIngestor's <see cref="SearchIndexIngestor.PersonaCatalogEnvResolver"/>
    /// resolves <c>PERSONAS</c> into this list before calling here). <c>null</c>/empty (the
    /// default, and SearchIndexRequestBuilder's only caller, which has no <c>PERSONAS</c>-allow-list
    /// concept of its own) means "every enabled persona", matching this method's original
    /// behaviour exactly. Each requested id MUST have a <c>persona.json</c> under
    /// <paramref name="personasDirOverride"/>/<c>repoRoot/personas</c> -- an id that doesn't is a
    /// configuration error (mirrors <c>persona_loader._load_one_persona</c>'s own
    /// "enabled but does not exist" failure), not silently ignored. The returned list is still
    /// sorted by id regardless of the requested ids' own order, matching
    /// <c>PersonaCatalog.ids</c>'s own <c>sorted(self._personas.keys())</c> (the requested ids'
    /// order only matters for <c>--persona</c>, handled entirely by
    /// <see cref="SearchIndexIngestor.PersonaTargeting"/>, a separate, later step).</param>
    public static IReadOnlyList<DiscoveredPersona> DiscoverAll(
        string repoRoot, string? personasDirOverride = null, IReadOnlyList<string>? enabledIdsOverride = null)
    {
        var personasDir = string.IsNullOrEmpty(personasDirOverride)
            ? Path.Combine(repoRoot, "personas")
            : personasDirOverride;
        if (!Directory.Exists(personasDir))
        {
            throw new InvalidOperationException(
                $"No 'personas' directory found under repo root '{repoRoot}'.");
        }

        List<string> personaIds;
        if (enabledIdsOverride is { Count: > 0 })
        {
            var missing = enabledIdsOverride
                .Where(id => !File.Exists(Path.Combine(personasDir, id, "persona.json")))
                .ToList();
            if (missing.Count > 0)
            {
                // Mirrors persona_loader._load_one_persona's own
                // f"Persona '{persona_id}' is enabled but {manifest_path} does not exist." message
                // content -- raised as this port's established clean, catchable
                // InvalidOperationException (see PersonaTargeting's remarks) rather than Python's
                // unhandled PersonaValidationError/stack trace, since PERSONAS naming a
                // non-existent pack is just as much an expected, resolvable configuration error as
                // an unknown --persona id.
                throw new InvalidOperationException(
                    $"Persona '{missing[0]}' is enabled (PERSONAS) but " +
                    $"{Path.Combine(personasDir, missing[0], "persona.json")} does not exist.");
            }

            personaIds = enabledIdsOverride.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        else
        {
            personaIds = Directory.GetDirectories(personasDir)
                .Select(Path.GetFileName)
                .Where(id => id is not null && File.Exists(Path.Combine(personasDir, id, "persona.json")))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

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
