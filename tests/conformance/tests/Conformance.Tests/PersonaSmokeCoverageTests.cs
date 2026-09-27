using System.Linq;
using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76 part 2 (Rick's wave-plan comment on #20): "coverage test scaffold: a test that
/// fails if a discovered pack lacks the minimum scenario coverage (e.g. per-pack smoke present)".
///
/// Independently re-discovers packs from disk -- the exact same
/// <see cref="ConformancePersonas.DiscoverFromDisk()"/> mechanism <c>RealPackPersonaSmokeTests</c>
/// and <c>PersonaCatalog.load()</c>'s own real-persona fallback both use, plus its directory
/// -taking overload pointed at the fixture pack folder for <c>FixturePackPersonaSmokeTests</c> --
/// and cross-checks the result against whatever <c>PersonaSmokeTests.cs</c>'s own
/// <c>[MemberData]</c> source method ACTUALLY returns right now, read via reflection rather than
/// a hand-copied literal list. A mutation that hardcoded/shrank
/// <c>RealPackPersonaSmokeTests.DiscoveredPersonaIds()</c> or
/// <c>FixturePackPersonaSmokeTests.FixturePersonaIds()</c> to silently drop a persona -- "remove
/// smoke for a pack" -- would leave the smoke Theory itself green (fewer rows, but every row that
/// still ran still passes) while this test fails, because its own expected set comes from
/// independently re-scanning disk, not from calling into the Theory's data source and trusting it.
/// </summary>
public sealed class PersonaSmokeCoverageTests
{
    [Fact]
    public void Every_real_pack_discovered_on_disk_has_a_persona_smoke_theory_row()
    {
        var discovered = ConformancePersonas.DiscoverFromDisk();
        var covered = GetTheoryDataValues<string>(typeof(RealPackPersonaSmokeTests), nameof(RealPackPersonaSmokeTests.DiscoveredPersonaIds));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Persona pack(s) discovered on disk under personas/ have no RealPackPersonaSmokeTests " +
            $"row: {string.Join(", ", missing)}. Either RealPackPersonaSmokeTests.DiscoveredPersonaIds() " +
            "silently shrank, or a newly-added real pack (#78/#79) needs its own " +
            "tests/conformance/testdata/personas/<id>/smoke.json -- see " +
            "PersonaSmokeExpectations.For's failure message in PersonaSmokeTests.cs for the exact " +
            "five fields and a template.");
    }

    [Fact]
    public void Every_fixture_pack_discovered_on_disk_has_a_persona_smoke_theory_row()
    {
        var discovered = ConformancePersonas.DiscoverFromDisk(RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot()));
        var covered = GetTheoryDataValues<string>(typeof(FixturePackPersonaSmokeTests), nameof(FixturePackPersonaSmokeTests.FixturePersonaIds));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Persona pack(s) discovered on disk under app/backend/tests/fixtures/personas/ have " +
            $"no FixturePackPersonaSmokeTests row: {string.Join(", ", missing)}. Either " +
            "FixturePackPersonaSmokeTests.FixturePersonaIds() silently shrank, or a newly-added " +
            "fixture pack needs its own tests/conformance/testdata/personas/<id>/smoke.json -- " +
            "see PersonaSmokeExpectations.For's failure message in PersonaSmokeTests.cs for the " +
            "exact five fields and a template.");
    }

    /// <summary>Invokes a `public static TheoryData&lt;T&gt;`-returning `[MemberData]` source
    /// method by reflection and returns its actual current data values -- the live set the
    /// Theory itself will run against, not a copy that could drift from it.</summary>
    private static List<T> GetTheoryDataValues<T>(Type declaringType, string methodName)
    {
        var method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{declaringType.Name}.{methodName} not found.");
        var data = (TheoryData<T>)method.Invoke(null, null)!;
        return data.Select(row => row.Data).ToList();
    }
}
