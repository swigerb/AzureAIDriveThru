using SearchIndexRequestBuilder;

namespace SearchIndexIngestor;

/// <summary>
/// Faithful port of setup_search_index.py's <c>resolve_target_personas</c> (lines 365-389):
/// resolves which of the catalog's enabled personas a run should target, from the repeatable
/// and/or comma-separated <c>--persona</c> flag.
/// </summary>
public static class PersonaTargeting
{
    /// <param name="catalog">Every enabled persona (see <see cref="EnabledPersonaDiscovery"/>),
    /// already sorted by persona id -- returned as-is when <paramref name="requested"/> is
    /// empty/null, matching <c>[catalog.get(pid) for pid in catalog.ids]</c>.</param>
    /// <param name="requested">The raw <c>--persona</c> flag values (one entry per occurrence of
    /// the flag; each entry may itself be comma-separated) -- <c>null</c>/empty means "every
    /// enabled persona".</param>
    /// <exception cref="InvalidOperationException">A requested id isn't in <paramref name="catalog"/>
    /// -- Python's equivalent raises <c>SystemExit(message)</c> (exit code 1, message to stderr);
    /// this port's established convention (see <see cref="PersonaIngestPlanBuilder"/>,
    /// <c>OpenAiSettingsResolver</c>) is the same clean, catchable exception type for every
    /// expected, resolvable CLI/configuration error, all caught once by
    /// <see cref="CliRunner"/>.</exception>
    public static IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> ResolveTargetPersonas(
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> catalog,
        IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return catalog;
        }

        var ids = requested
            .SelectMany(item => item.Split(','))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var byId = catalog.ToDictionary(p => p.PersonaId, StringComparer.Ordinal);
        var unknown = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            var knownIds = string.Join(", ", catalog.Select(p => p.PersonaId));
            // Mirrors Python's f"--persona named {unknown} -- not in the enabled persona catalog
            // ({', '.join(catalog.ids) or '(none)'})" message content (a Python list's repr --
            // ['a', 'b'] -- rather than a byte-identical string, which isn't the point here: see
            // docs/dotnet_tooling.md's "known, accepted divergences" for this port's error-message
            // convention).
            var unknownRepr = string.Join(", ", unknown.Select(id => $"'{id}'"));
            throw new InvalidOperationException(
                $"--persona named [{unknownRepr}] -- not in the enabled persona catalog " +
                $"({(knownIds.Length > 0 ? knownIds : "(none)")})");
        }

        return ids.Select(id => byId[id]).ToList();
    }
}
