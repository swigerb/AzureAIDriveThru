namespace Conformance.Harness;

/// <summary>
/// Pure resolution logic for the persona dimension the conformance harness launches the backend
/// under test with -- the harness half of issue #76's groundwork (docs/persona-architecture.md
/// section 8: the harness sets `PERSONAS`/`DEFAULT_PERSONA` on the backend process exactly like a
/// real deployment would). Mirrors app/backend/persona_loader.py's `PersonaCatalog.load()`
/// resolution order exactly -- explicit env var, else disk discovery, else "sonic" -- so a
/// harness-launched backend and a hand-run one resolve the same enabled/default personas given the
/// same inputs. Every method here is a pure function of its inputs (same rationale as
/// <see cref="DotnetPlaceholderPolicy"/>'s own doc comment: testable without touching the real
/// filesystem or environment).
///
/// All three real packs under personas/ (#78/#79 landed the second and third) exist on disk
/// today, so <see cref="DiscoverFromDisk()"/> resolves all three as enabled with no harness code
/// change required -- it was written to make a second/third pack "visible for free" and that is
/// exactly what happened. <see cref="DefaultPersonaId"/> ("sonic") stays the harness's own
/// DEFAULT_PERSONA unless a fixture explicitly overrides <see cref="ConformanceFixture.Persona"/>,
/// matching PersonaCatalog.load()'s own "sonic wins if enabled" tie-break in
/// <see cref="ResolveDefault"/> below.
/// </summary>
public static class ConformancePersonas
{
    /// <summary>The harness's own default/tie-break persona id (design doc section 8) when no
    /// explicit override names one and disk discovery finds nothing -- NOT a statement that it's
    /// the only pack that exists; #78/#79 landed the other two real packs under personas/
    /// alongside it.</summary>
    public const string DefaultPersonaId = "sonic";

    /// <summary>
    /// Resolves the enabled persona list exactly like `PersonaCatalog.load()`: an explicit
    /// CONFORMANCE_PERSONAS value (comma-separated, trimmed, empty entries dropped) wins;
    /// otherwise fall back to whatever <paramref name="discoverFromDisk"/> finds; otherwise
    /// (nothing enabled and nothing discoverable either) a single-element list containing only
    /// <see cref="DefaultPersonaId"/>, so the suite never ends up with zero enabled personas even
    /// on a checkout that has somehow lost personas/.
    /// </summary>
    public static IReadOnlyList<string> ResolveEnabled(string? envValue, Func<IReadOnlyList<string>> discoverFromDisk)
    {
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var explicitIds = envValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => id.Length > 0)
                .ToArray();
            if (explicitIds.Length > 0)
            {
                return explicitIds;
            }
        }

        var discovered = discoverFromDisk();
        return discovered.Count > 0 ? discovered : [DefaultPersonaId];
    }

    /// <summary>
    /// Resolves DEFAULT_PERSONA exactly like `PersonaCatalog.load()`: an explicit override wins if
    /// it names one of <paramref name="enabled"/>; otherwise <see cref="DefaultPersonaId"/> if
    /// it's enabled, else the first enabled id (assumes <paramref name="enabled"/> is already
    /// sorted, matching the ordinal sort both this class and `PersonaCatalog.load()`'s disk
    /// discovery use).
    /// </summary>
    public static string ResolveDefault(string? overridePersonaId, IReadOnlyList<string> enabled)
    {
        if (!string.IsNullOrWhiteSpace(overridePersonaId) &&
            enabled.Contains(overridePersonaId, StringComparer.Ordinal))
        {
            return overridePersonaId;
        }

        if (enabled.Contains(DefaultPersonaId, StringComparer.Ordinal))
        {
            return DefaultPersonaId;
        }

        return enabled.Count > 0 ? enabled[0] : DefaultPersonaId;
    }

    /// <summary>
    /// A fixture that overrides the default persona (<c>ConformanceFixture.Persona</c>) is asking
    /// the harness to target a persona that may not already be in the enabled set resolved from
    /// CONFORMANCE_PERSONAS/disk discovery -- e.g. a future fixture targeting "mcdonalds" before
    /// anyone has set CONFORMANCE_PERSONAS=sonic,mcdonalds explicitly. Ensures the override is
    /// always included in the enabled list sent to the backend (sorted back in, not appended raw,
    /// so PERSONAS stays in the same ordinal order disk discovery would produce), so a fixture's
    /// override always actually takes effect as DEFAULT_PERSONA rather than silently falling back
    /// to "sonic" because the requested id wasn't enabled. A no-op when the override is null or
    /// already present.
    /// </summary>
    public static IReadOnlyList<string> EnsureIncluded(IReadOnlyList<string> enabled, string? overridePersonaId)
    {
        if (string.IsNullOrWhiteSpace(overridePersonaId) ||
            enabled.Contains(overridePersonaId, StringComparer.Ordinal))
        {
            return enabled;
        }

        return enabled.Append(overridePersonaId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Discovers persona ids from disk exactly like `PersonaCatalog.load()`'s own fallback: every
    /// immediate subfolder of <paramref name="personasDir"/> that contains a `persona.json`,
    /// sorted ordinally. Testable overload -- takes the directory explicitly instead of resolving
    /// <see cref="RepoPaths.FindRepoRoot()"/> itself, so tests can point it at a scratch directory
    /// instead of the real repo's personas/ folder.
    /// </summary>
    public static IReadOnlyList<string> DiscoverFromDisk(string personasDir)
    {
        if (!Directory.Exists(personasDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(personasDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(id => File.Exists(Path.Combine(personasDir, id, "persona.json")))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Convenience overload: discovers from the real repo's personas/ folder, resolved via <see cref="RepoPaths.FindRepoRoot()"/>.</summary>
    public static IReadOnlyList<string> DiscoverFromDisk() =>
        DiscoverFromDisk(RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot()));
}
