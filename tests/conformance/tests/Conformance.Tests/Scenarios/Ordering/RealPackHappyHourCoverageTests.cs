using System.Linq;
using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #127: companion coverage scaffold for
/// <see cref="RealPackHappyHourConformanceTests"/>, mirroring
/// <see cref="PersonaSmokeCoverageTests"/>'s own pattern exactly (same rationale: independently
/// re-discovers packs from disk and cross-checks the result against whatever
/// <c>RealPackHappyHourConformanceTests.DiscoveredPersonaIds()</c>'s own <c>[MemberData]</c>
/// source method ACTUALLY returns right now, read via reflection rather than a hand-copied
/// literal list). A mutation that hardcoded/shrank
/// <c>RealPackHappyHourConformanceTests.DiscoveredPersonaIds()</c> to silently drop a persona
/// would leave that Theory itself green (fewer rows, but every row that still ran still passes)
/// while this test fails, because its own expected set comes from independently re-scanning disk.
/// </summary>
public sealed class RealPackHappyHourCoverageTests
{
    [Fact]
    public void Every_real_pack_discovered_on_disk_has_a_happy_hour_theory_row()
    {
        var discovered = ConformancePersonas.DiscoverFromDisk();
        var covered = GetTheoryDataValues<string>(
            typeof(RealPackHappyHourConformanceTests), nameof(RealPackHappyHourConformanceTests.DiscoveredPersonaIds));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Persona pack(s) discovered on disk under personas/ have no " +
            $"RealPackHappyHourConformanceTests row: {string.Join(", ", missing)}. Either " +
            "RealPackHappyHourConformanceTests.DiscoveredPersonaIds() silently shrank, or a newly-" +
            "added real pack (#112 or later) needs happy-hour smoke coverage -- see " +
            "PersonaHappyHourSmokeExpectations.For's failure message in " +
            "RealPackHappyHourConformanceTests.cs for the exact fields and a template (only " +
            "required at all if that pack's own persona.json enables pricing.happyHour).");
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
