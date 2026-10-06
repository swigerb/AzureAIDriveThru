using Backend.Personas;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Loads app/backend/tests/fixtures/personas' "test-eta" pack directly from its single, shared
/// location on disk, mirroring <see cref="ZetaFixture"/>'s own precedent. test-eta is the ONLY
/// fixture pack under app/backend/tests/fixtures/personas that declares
/// <c>bundles.resizeRule: "componentUpcharge"</c> -- a single combo ("Eta Combo") with a side
/// ("Eta Fries", Medium-only, so absorbing it never upcharges) and a drink ("Eta Cola", Medium at
/// the pack's own <c>bundles.includedSize</c> and Large fifty cents more) -- so combo-upcharge
/// spoken-delta coverage never needs a real persona pack id in its own source (#313, Rick's
/// re-review item 2).
/// </summary>
internal static class EtaFixture
{
    public const string PersonaId = "test-eta";
    public const string Bundle = "Eta Combo";
    public const string Side = "Eta Fries";
    public const string Drink = "Eta Cola";

    internal static Persona Load()
    {
        var fixturesDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        var catalog = PersonaCatalog.Load(personasDir: fixturesDir, personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        return catalog.Get(PersonaId);
    }
}
