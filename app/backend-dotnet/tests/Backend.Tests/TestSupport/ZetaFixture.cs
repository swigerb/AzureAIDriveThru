using Backend.Personas;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Loads app/backend/tests/fixtures/personas' "test-zeta" pack -- the SAME non-brand-coupled
/// fixture Python's own tests use (app/backend/tests/test_tool_calling.py's
/// <c>test_modify_25_count_to_10_count_reproduces_brians_exact_bug_report</c>) -- directly from
/// its single, shared location on disk, mirroring <see cref="DeltaFixture"/>'s own precedent for
/// "test-delta". test-zeta ships a synthetic, non-branded count-sized item ("ZORBS® Bite
/// Treats", sizes "10 count"/"25 count", a <c>spokenName</c> override, and a trademark-cased
/// <c>sizes.spokenAs</c> key -- see <c>PersonaConformanceFixtures.ZetaConformanceFixture</c>'s own
/// doc comment) so count-size word-spelled-quantity read-back and case-sensitive
/// spokenAs/spokenName pronunciation-rewrite tests never need a real persona pack id in their own
/// source (#313, Rick's re-review item 1).
/// </summary>
public static class ZetaFixture
{
    public const string PersonaId = "test-zeta";

    public static Persona Load()
    {
        var fixturesDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        var catalog = PersonaCatalog.Load(personasDir: fixturesDir, personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        return catalog.Get(PersonaId);
    }
}
