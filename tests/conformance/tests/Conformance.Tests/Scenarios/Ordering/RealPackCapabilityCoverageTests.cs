using System.Linq;
using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #76 remaining scope: independent capability-coverage guard for the real-pack rows added
/// across extras, bundle auto-fill, and dayparts/menu-mode scenarios. Each assertion re-discovers
/// the qualifying personas straight from disk, then cross-checks that set against the live
/// <c>[MemberData]</c> source the corresponding Theory will actually run.
/// </summary>
public sealed class RealPackCapabilityCoverageTests
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_an_extra_item_has_an_extras_theory_row()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var discovered = ConformancePersonas.DiscoverFromDisk()
            .Where(id => PersonaExtrasMenuData.HasAnyExtraItem(personasDir, id))
            .ToArray();
        var covered = GetTheoryDataValues<string>(
            typeof(RealPackExtrasConformanceTests), nameof(RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Real pack(s) with at least one isExtra menu item have no RealPackExtrasConformanceTests row: " +
            $"{string.Join(", ", missing)}.");
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_bundle_autofill_has_a_bundle_autofill_theory_row()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var discovered = ConformancePersonas.DiscoverFromDisk()
            .Where(id => PersonaBundleAutoFillMenuData.HasAnyBundleAutoFill(personasDir, id))
            .ToArray();
        var covered = GetTheoryDataValues<PersonaBundleAutoFillMenuData.BundleAutoFillCase>(
                typeof(RealPackBundleAutoFillConformanceTests), nameof(RealPackBundleAutoFillConformanceTests.DiscoveredBundleAutoFillCases))
            .Select(row => row.PersonaId)
            .ToArray();

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Real pack(s) with at least one bundle.autoFill block have no RealPackBundleAutoFillConformanceTests row: " +
            $"{string.Join(", ", missing)}. Either the discovered MemberData silently shrank, or a newly-added " +
            "pack needs a real bundle with bundle.defaultSize plus standalone side/drink slot items.");
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_dayparts_has_a_menu_mode_theory_row()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var discovered = ConformancePersonas.DiscoverFromDisk()
            .Where(id => PersonaDaypartsFeature.Read(personasDir, id))
            .ToArray();
        var covered = GetTheoryDataValues<string>(
            typeof(RealPackMenuModeConformanceTests), nameof(RealPackMenuModeConformanceTests.DiscoveredPersonaIds));

        var missing = discovered.Except(covered).ToArray();
        Assert.True(missing.Length == 0,
            $"Real pack(s) with features.dayparts:true have no RealPackMenuModeConformanceTests row: " +
            $"{string.Join(", ", missing)}.");
    }

    private static List<T> GetTheoryDataValues<T>(Type declaringType, string methodName)
    {
        var method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{declaringType.Name}.{methodName} not found.");
        var data = (TheoryData<T>)method.Invoke(null, null)!;
        return data.Select(row => row.Data).ToList();
    }
}
