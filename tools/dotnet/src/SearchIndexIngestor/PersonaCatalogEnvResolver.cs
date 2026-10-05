namespace SearchIndexIngestor;

/// <summary>
/// Resolves <c>persona_loader.PersonaCatalog.load()</c>'s two environment-variable inputs --
/// <c>PERSONAS_DIR</c> (the personas pack directory) and <c>PERSONAS</c> (a comma allow-list of
/// persona ids) -- for <see cref="EnabledPersonaDiscovery.DiscoverAll"/>, with the SAME precedence
/// every other azd-managed setting this port reads already establishes (<see cref="OpenAiSettingsResolver"/>,
/// <see cref="SearchEndpointResolver"/>): an azd default-environment value beats a pre-existing
/// process environment variable of the same name, matching <c>setup_search_index.py</c>'s own
/// <c>load_azd_env()</c> -&gt; <c>load_dotenv(path, override=True)</c> sequencing (an azd-managed
/// value is loaded into <c>os.environ</c> AFTER the process starts, so it always wins over
/// whatever the process environment already had -- see <c>persona_loader.py</c> lines 116-136,
/// 573-579).
///
/// Issue #16 round 2 (Rick's review, 2a): before this class existed, <c>CliRunner</c> passed only
/// <c>--personas-dir</c> into <see cref="EnabledPersonaDiscovery.DiscoverAll"/> and never looked at
/// <c>PERSONAS</c>/<c>PERSONAS_DIR</c> at all, so a real run could create/populate indexes for
/// EVERY persona pack on disk even when an azd environment's own <c>PERSONAS</c> setting (a real
/// azd setting -- see <c>infra/main.bicep</c>'s own "Override at provision time ... only to
/// restrict this environment to a subset of packs") restricted it to a subset. Both are resolved
/// here, matching Python's own precedence exactly, so a real run only ever touches the indexes
/// Python's own <c>PersonaCatalog.load()</c> would.
/// </summary>
internal static class PersonaCatalogEnvResolver
{
    private const string PersonasDirVariableName = "PERSONAS_DIR";
    private const string PersonasVariableName = "PERSONAS";

    /// <param name="personasDirFlag">This port's own <c>--personas-dir</c> CLI flag, matching
    /// Python's own <c>--personas-dir</c> -&gt; <c>PersonaCatalog.load(personas_dir=...)</c>: an
    /// explicit value here ALWAYS wins over both <c>PERSONAS_DIR</c> sources below, the same way
    /// Python's explicit <c>personas_dir</c> constructor argument bypasses its own
    /// <c>PERSONAS_DIR</c> environment-variable fallback entirely (<c>persona_loader.py</c> lines
    /// 555-565).</param>
    /// <returns>The value to pass as <see cref="EnabledPersonaDiscovery.DiscoverAll"/>'s own
    /// <c>personasDirOverride</c> parameter, or <c>null</c> to fall back to its
    /// <c>&lt;repoRoot&gt;/personas</c> default -- mirroring
    /// <c>PersonaCatalog.load(personas_dir=None)</c>'s own fall-through to
    /// <c>resolve_personas_dir()</c> when neither an explicit argument nor <c>PERSONAS_DIR</c> is
    /// set.</returns>
    public static string? ResolvePersonasDir(
        string? personasDirFlag,
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyDictionary<string, string> azdEnvValues)
    {
        if (!string.IsNullOrEmpty(personasDirFlag))
        {
            return personasDirFlag;
        }

        return GetNonEmptyOrNull(azdEnvValues, PersonasDirVariableName)
            ?? getEnvironmentVariable(PersonasDirVariableName);
    }

    /// <summary>
    /// Resolves <c>PERSONAS</c> into an explicit allow-list of persona ids to pass as
    /// <see cref="EnabledPersonaDiscovery.DiscoverAll"/>'s own <c>enabledIdsOverride</c> parameter.
    /// Matches <c>persona_loader.py</c>'s own
    /// <c>[p.strip() for p in env_value.split(",") if p.strip()]</c> (lines 573-575) exactly,
    /// including its "falsy/empty <c>env_value</c> means no allow-list at all" fallback (an
    /// explicitly-set-but-empty <c>PERSONAS=""</c> is treated identically to an unset one, matching
    /// Python's own <c>if env_value:</c> truthiness check).
    /// </summary>
    /// <returns><c>null</c> (meaning "every enabled persona", <see cref="EnabledPersonaDiscovery.DiscoverAll"/>'s
    /// own default) when <c>PERSONAS</c> is unset/empty everywhere (no CLI-flag equivalent exists,
    /// in either Python or this port); otherwise the comma-split, trimmed, non-empty id list, in
    /// the SAME order <c>PERSONAS</c> itself lists them (order is not otherwise significant here --
    /// <see cref="EnabledPersonaDiscovery.DiscoverAll"/> always returns its result sorted by id,
    /// matching <c>PersonaCatalog.ids</c>'s own sorted order).</returns>
    public static IReadOnlyList<string>? ResolveEnabledIds(
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyDictionary<string, string> azdEnvValues)
    {
        var value = GetNonEmptyOrNull(azdEnvValues, PersonasVariableName)
            ?? getEnvironmentVariable(PersonasVariableName);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var ids = value
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    private static string? GetNonEmptyOrNull(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}
