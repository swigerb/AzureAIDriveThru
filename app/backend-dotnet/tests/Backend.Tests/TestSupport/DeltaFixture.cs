using Backend.Personas;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Loads app/backend/tests/fixtures/personas' "test-delta" pack -- the SAME non-brand-coupled
/// fixture Python's own #77 tests (test_bundle_and_extras_engine.py) use -- directly from its
/// single, shared location on disk, rather than duplicating a second copy of it under
/// backend-dotnet. Both backends' test suites therefore prove their engines against byte-for-byte
/// identical persona/menu JSON; a behavior difference can never be explained away by "the fixture
/// data itself quietly drifted between the two copies".
/// </summary>
internal static class DeltaFixture
{
    internal static Persona Load(string personaId = "test-delta")
    {
        var fixturesDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        var catalog = PersonaCatalog.Load(personasDir: fixturesDir, personasEnv: personaId, defaultPersonaEnv: personaId);
        return catalog.Get(personaId);
    }
}
