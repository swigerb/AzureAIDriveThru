using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 1: the two-pack persona_mismatch conformance row needs a genuine
/// second persona pack, which doesn't exist for real yet (a second/third real pack belongs to
/// future issues #78/#79, explicitly out of scope here) -- so this fixture points PERSONAS_DIR at
/// the same TEST-ONLY fixture pack app/backend/tests/test_persona_binding.py already exercises
/// (test-alpha/test-beta), rather than duplicating a second fixture pack under
/// tests/conformance/testdata/ and its own persona.schema.json/menu.schema.json compliance work.
///
/// Both packs are ENABLED here (unlike <see cref="DisabledPersonaConformanceFixture"/>) so a
/// resume attempt that switches from one to the other exercises the persona_mismatch rejection
/// path (session_manager.py's <c>resume()</c>), not the unrelated "unknown persona" 404 a
/// disabled/nonexistent id would hit instead.
///
/// PERSONAS/DEFAULT_PERSONA/PERSONAS_DIR are all read once at Python module-import time, so this
/// needs its own dedicated collection/process -- same reasoning as every other profile/persona
/// override fixture in this project (see <c>BackendProfileFixtures.cs</c>).
/// </summary>
public sealed class TwoPersonaConformanceFixture : ConformanceFixture
{
    public const string PersonaA = "test-alpha";
    public const string PersonaB = "test-beta";

    protected override IReadOnlyList<string>? Personas => [PersonaA, PersonaB];
    protected override string? Persona => PersonaA;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class TwoPersonaConformanceCollection : ICollectionFixture<TwoPersonaConformanceFixture>
{
    public const string Name = "ConformanceTwoPersona";
}

/// <summary>
/// Rick's PR #102 review item 5: the "serve only the ENABLED pack's own files" conformance row
/// needs a persona that genuinely exists on disk but is excluded from PERSONAS, exactly modelling
/// a real disabled-persona scenario (see app/backend/tests/test_persona_binding.py's
/// PersonaAssetAndMenuRouteTests for the Python-side equivalent). Reuses the same fixture pack as
/// <see cref="TwoPersonaConformanceFixture"/> (test-alpha/test-beta both exist under
/// <see cref="RepoPaths.FixturePersonasDirectory"/>) but only enables test-alpha here -- test-beta
/// is on disk and therefore a real, resolvable pack, yet must 404 on every persona-scoped route.
/// </summary>
public sealed class DisabledPersonaConformanceFixture : ConformanceFixture
{
    protected override IReadOnlyList<string>? Personas => [TwoPersonaConformanceFixture.PersonaA];
    protected override string? Persona => TwoPersonaConformanceFixture.PersonaA;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class DisabledPersonaConformanceCollection : ICollectionFixture<DisabledPersonaConformanceFixture>
{
    public const string Name = "ConformanceDisabledPersona";
}
