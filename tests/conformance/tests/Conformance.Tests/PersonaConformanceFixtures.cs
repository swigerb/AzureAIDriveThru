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

/// <summary>
/// Issue 165: the Breakfast/Lunch menu-mode conformance rows need one persona that declares
/// <c>features.dayparts</c> (real breakfast/lunch items, meal-number-sharing menuPeriod pair) and
/// one that doesn't, side by side, exactly like <see cref="TwoPersonaConformanceFixture"/>'s own
/// pairing -- reuses the SAME shared fixture pack directory (test-delta/test-alpha) rather than a
/// third copy under tests/conformance/testdata/. test-delta's menu/menuItems.json ships a
/// breakfast-only "Delta Breakfast Meal" and a lunch-only "Delta Lunch Meal" sharing meal number
/// 2 (app/backend/tests/test_menu_utils.py's own golden-table precedent for meal-number-sharing
/// items), purpose-built for the mode-switch/out-of-mode-rejection/search-filter rows below.
/// test-alpha declares no dayparts feature at all -- the "packs without modes unaffected" row.
/// </summary>
public sealed class MenuModeConformanceFixture : ConformanceFixture
{
    public const string DaypartsPersona = "test-delta";
    public const string NoDaypartsPersona = TwoPersonaConformanceFixture.PersonaA;

    protected override IReadOnlyList<string>? Personas => [DaypartsPersona, NoDaypartsPersona];
    protected override string? Persona => DaypartsPersona;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class MenuModeConformanceCollection : ICollectionFixture<MenuModeConformanceFixture>
{
    public const string Name = "ConformanceMenuMode";
}

/// <summary>
/// #313 (Rick's REQUEST CHANGES re-review, items 2/3): the count-size word-spelled-quantity
/// read-back and case-sensitive spokenAs/spokenName pronunciation rewrite were previously
/// exercised only against a real persona pack's own trademarked count-sized item -- a
/// real brand name in test code/data, which forced a new `rebrand_baseline.yaml` "increase"
/// entry just to keep the ratchet green. <see cref="RepoPaths.FixturePersonasDirectory"/>'s own
/// <c>test-zeta</c> pack was extended with a synthetic, non-branded count-sized item
/// ("ZORBS® Bite Treats", sizes "10 count"/"25 count", a <c>spokenName</c> override, and a
/// trademark-cased <c>sizes.spokenAs</c> key) so these scenarios can run against a TEST-ONLY
/// fixture pack instead, with no real brand word anywhere in the test file and no baseline
/// entry required.
/// </summary>
public sealed class ZetaConformanceFixture : ConformanceFixture
{
    public const string PersonaId = "test-zeta";

    protected override IReadOnlyList<string>? Personas => [PersonaId];
    protected override string? Persona => PersonaId;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class ZetaConformanceCollection : ICollectionFixture<ZetaConformanceFixture>
{
    public const string Name = "ConformanceZeta";
}
