using System.Reflection;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #76 remaining scope: independent capability-coverage guard for the real-pack rows added
/// across bundle auto-fill and dayparts/menu-mode scenarios. Each assertion re-discovers the
/// qualifying personas straight from disk, then cross-checks that set against the live
/// <c>[MemberData]</c> source the corresponding Theory will actually run.
///
/// Rick's PR #266 review item 2 (required before approval): a prior version of this class also
/// duplicated <c>RealPackExtrasCoverageTests.Every_real_pack_with_an_extra_item_has_an_extras_theory_row</c>
/// verbatim (same name, same logic) -- removed here; that class already owns extras coverage.
/// Removing it also drops the <c>[Trait("Dotnet", "ready")]</c> floor by one (see
/// <see cref="DotnetTraitCoverageTests"/>'s doc comment and its 261 floor).
///
/// Rick's review item 2 also found that none of the three Facts here could fail with zero
/// qualifying cases: an empty `discovered` set produces an empty `missing` set, which trivially
/// passes `Assert.True(missing.Length == 0, ...)`. Each Fact below now asserts
/// <c>Assert.NotEmpty(discovered)</c> first, so a predicate or disk-discovery regression that
/// silently empties the qualifying set turns this test red instead of green.
///
/// Rick's review item 2 also found that the bundle-auto-fill Fact and its own MemberData source
/// (<see cref="RealPackBundleAutoFillConformanceTests.DiscoveredBundleAutoFillCases"/>) both called
/// the same <see cref="PersonaBundleAutoFillMenuData.HasAnyBundleAutoFill"/> predicate, so a broken
/// predicate would empty both sides together and this Fact could never catch it. The bundle
/// auto-fill check below instead re-scans each persona's own <c>menu/menuItems.json</c> directly
/// via <see cref="JsonDocument"/> for a <c>bundle.autoFill</c> block, independent of
/// <c>PersonaBundleAutoFillMenuData</c> entirely.
/// </summary>
public sealed class RealPackCapabilityCoverageTests
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_bundle_autofill_has_a_bundle_autofill_theory_row()
    {
        string personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        string[] discovered = [.. ConformancePersonas.DiscoverFromDisk().Where(id => HasBundleAutoFillBlock(personasDir, id))];
        Assert.NotEmpty(discovered);

        string[] covered = [.. GetTheoryDataValues<PersonaBundleAutoFillMenuData.BundleAutoFillCase>(
                typeof(RealPackBundleAutoFillConformanceTests), nameof(RealPackBundleAutoFillConformanceTests.DiscoveredBundleAutoFillCases))
            .Select(row => row.PersonaId)];

        string[] missing = [.. discovered.Except(covered)];
        Assert.True(missing.Length == 0,
            $"Real pack(s) with at least one bundle.autoFill block have no RealPackBundleAutoFillConformanceTests row: " +
            $"{string.Join(", ", missing)}. Either the discovered MemberData silently shrank, or a newly-added " +
            "pack needs a real bundle with bundle.defaultSize plus standalone side/drink slot items.");
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public void Every_real_pack_with_dayparts_has_a_menu_mode_theory_row()
    {
        string personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        string[] discovered = [.. ConformancePersonas.DiscoverFromDisk().Where(id => PersonaDaypartsFeature.Read(personasDir, id))];
        Assert.NotEmpty(discovered);

        List<string> covered = GetTheoryDataValues<string>(
            typeof(RealPackMenuModeConformanceTests), nameof(RealPackMenuModeConformanceTests.DiscoveredPersonaIds));

        string[] missing = [.. discovered.Except(covered)];
        Assert.True(missing.Length == 0,
            $"Real pack(s) with features.dayparts:true have no RealPackMenuModeConformanceTests row: " +
            $"{string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Direct, dependency-free <see cref="JsonDocument"/> scan of a persona's own
    /// <c>menu/menuItems.json</c> for a non-empty <c>bundle.autoFill</c> object on any item. Does
    /// NOT call <see cref="PersonaBundleAutoFillMenuData.HasAnyBundleAutoFill"/> or any other
    /// helper from <c>RealPackBundleAutoFillConformanceTests.cs</c>, so a bug in that predicate
    /// cannot silently empty both this Fact's `discovered` set and the Theory's MemberData at the
    /// same time.
    /// </summary>
    private static bool HasBundleAutoFillBlock(string personasDir, string personaId)
    {
        string menuItemsPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(menuItemsPath));

        foreach (JsonElement category in doc.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (JsonElement item in category.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("bundle", out JsonElement bundleElement) &&
                    bundleElement.TryGetProperty("autoFill", out JsonElement autoFillElement) &&
                    autoFillElement.ValueKind == JsonValueKind.Object &&
                    autoFillElement.EnumerateObject().Any())
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<T> GetTheoryDataValues<T>(Type declaringType, string methodName)
    {
        MethodInfo method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{declaringType.Name}.{methodName} not found.");
        var data = (TheoryData<T>)method.Invoke(null, null)!;
        return [.. data.Select(row => row.Data)];
    }
}
