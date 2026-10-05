using Backend.Personas;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Loads app/backend/tests/fixtures/personas' "test-zeta" pack -- the SAME fixture both
/// conformance legs discover combo-component-resize rows from (#283), and the exact pack #325's
/// own write-up names as carrying a trademark-marked item ("ZORBS&#174; Bite Treats") -- directly
/// from its single, shared location on disk. Mirrors <see cref="DeltaFixture"/>'s precedent: both
/// backends' test suites prove their engines against byte-for-byte identical persona/menu JSON.
/// </summary>
public static class ZetaFixture
{
    public static Persona Load(string personaId = "test-zeta")
    {
        var fixturesDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        var catalog = PersonaCatalog.Load(personasDir: fixturesDir, personasEnv: personaId, defaultPersonaEnv: personaId);
        return catalog.Get(personaId);
    }
}
