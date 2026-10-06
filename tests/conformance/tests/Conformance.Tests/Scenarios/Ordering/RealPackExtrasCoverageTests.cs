using System.Linq;
using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #76 follow-up: companion coverage scaffold for
/// <see cref="RealPackExtrasConformanceTests"/>, mirroring <see
/// cref="RealPackHappyHourCoverageTests"/>'s own pattern exactly -- independently re-discovers
/// which real packs on disk have at least one <c>isExtra</c> menu item (via a fresh, direct
/// <see cref="PersonaExtrasMenuData.HasAnyExtraItem"/> scan over every pack <see
/// cref="ConformancePersonas.DiscoverFromDisk()"/> returns, not by trusting
/// <c>RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras()</c>'s own filtering logic),
/// and cross-checks that set against whatever
/// <c>RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras()</c>'s own <c>[MemberData]</c>
/// source method ACTUALLY returns right now, read via reflection rather than a hand-copied literal
/// list. A mutation that broke that filter (e.g. hardcoded/shrank the result, or inverted the
/// <c>HasAnyExtraItem</c> check) would leave the main Theory itself green (fewer rows, but every
/// row that still ran still passes) while this test fails, because its own expected set comes from
/// an independent re-scan of disk using the harness-level menu reader directly, not the Theory's
/// own data source.
/// </summary>
public sealed class RealPackExtrasCoverageTests
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_an_extra_item_has_an_extras_theory_row()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var discovered = ConformancePersonas.DiscoverFromDisk()
            .Where(id => PersonaExtrasMenuData.HasAnyExtraItem(personasDir, id))
            .ToArray();

        // Issue #274 follow-up D (#266 re-review): an empty `discovered` set produces an empty
        // `missing` set below, which trivially passes `Assert.True(missing.Length == 0, ...)` --
        // exactly the same gap Rick's review caught in RealPackCapabilityCoverageTests (see that
        // class's own doc comment). A predicate or disk-discovery regression that silently empties
        // the qualifying set must turn this test red, not green.
        Assert.NotEmpty(discovered);

        var covered = GetTheoryDataValues<string>(
            typeof(RealPackExtrasConformanceTests), nameof(RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Real pack(s) with at least one isExtra menu item have no " +
            $"RealPackExtrasConformanceTests row: {string.Join(", ", missing)}. Either " +
            "RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras() silently shrank or " +
            "broke its own HasAnyExtraItem filter, or a newly-added real pack needs its extras " +
            "gate (accepted/extras_blocked_category/extras_no_base_item) exercised -- see " +
            "RealPackExtrasConformanceTests.cs's own doc comment for what's required (a schema-" +
            "required persona.json extras.allowedBaseCategories/blockedBaseCategories block that " +
            "each match at least one real menu item's own category).");
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
