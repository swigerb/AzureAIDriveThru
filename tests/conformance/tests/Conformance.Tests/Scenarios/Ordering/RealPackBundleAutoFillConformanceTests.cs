using System.Linq;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #76 remaining scope: a per-pack, brand-neutral conformance proof of real-pack
/// <c>bundle.autoFill</c> over every REAL pack whose own menu defines at least one bundle item with
/// an <c>autoFill</c> block. Every fact it asserts -- which bundle qualifies, which size the pack
/// treats as its default, the slot-filler text, and which real side/drink can be pre-ordered and
/// absorbed -- is read from that SAME pack's own <c>menu/menuItems.json</c> and <c>persona.json</c>,
/// never hardcoded in shared C#.
/// </summary>
public static class PersonaBundleAutoFillMenuData
{
    public sealed record MenuItemRef(string Name, string Size, decimal Price)
    {
        public string Display => $"{Size} {Name}".Trim();
    }

    public sealed record BundleAutoFillCase(
        string PersonaId,
        string BundleName,
        string BundleMenuPeriod,
        string SizedBundleSize,
        decimal SizedBundlePrice,
        string DefaultBundleSize,
        decimal DefaultBundlePrice,
        IReadOnlyList<string> BundleSlots,
        IReadOnlyDictionary<string, string> AutoFillTemplates,
        MenuItemRef? PreorderedSide,
        MenuItemRef? PreorderedDrink,
        bool HasDayparts)
    {
        public override string ToString() => PersonaId;

        public string? MenuMode => !HasDayparts
            ? null
            : string.Equals(BundleMenuPeriod, "breakfast", StringComparison.Ordinal)
                ? "breakfast"
                : "lunch";
    }

    private sealed record CandidateItem(
        string Name,
        string MenuPeriod,
        string? ComboSlot,
        IReadOnlyList<(string Size, decimal Price)> Sizes,
        IReadOnlyList<string> BundleSlots,
        IReadOnlyDictionary<string, string> AutoFillTemplates,
        string BundleDefaultSize,
        string? RequiresMachine);

    public static bool HasAnyBundleAutoFill(string personasDir, string personaId) =>
        ReadMenuItems(personasDir, personaId).Any(i => i.AutoFillTemplates.Count > 0);

    public static BundleAutoFillCase? Discover(string personasDir, string personaId)
    {
        var items = ReadMenuItems(personasDir, personaId);
        var downMachines = CurrentlyDownMachines(personasDir, personaId);
        var hasDayparts = PersonaDaypartsFeature.Read(personasDir, personaId);

        foreach (var bundle in items.Where(i => i.AutoFillTemplates.Count > 0 && i.BundleSlots.Count > 0))
        {
            if (string.IsNullOrWhiteSpace(bundle.BundleDefaultSize))
            {
                continue;
            }

            var defaultSize = bundle.Sizes.FirstOrDefault(s =>
                string.Equals(s.Size, bundle.BundleDefaultSize, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(defaultSize.Size))
            {
                continue;
            }

            var explicitSize = bundle.Sizes.FirstOrDefault(s =>
                !string.Equals(s.Size, bundle.BundleDefaultSize, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(explicitSize.Size))
            {
                explicitSize = defaultSize;
            }

            var side = bundle.BundleSlots.Contains("sides")
                ? FindStandaloneComponent(items, "sides", bundle.MenuPeriod, explicitSize.Size, downMachines)
                : null;
            var drink = bundle.BundleSlots.Contains("drinks")
                ? FindStandaloneComponent(items, "drinks", bundle.MenuPeriod, explicitSize.Size, downMachines)
                : null;

            return new BundleAutoFillCase(
                personaId,
                bundle.Name,
                bundle.MenuPeriod,
                explicitSize.Size,
                explicitSize.Price,
                defaultSize.Size,
                defaultSize.Price,
                bundle.BundleSlots,
                bundle.AutoFillTemplates,
                side,
                drink,
                hasDayparts);
        }

        return null;
    }

    public static string ExpandAutoFill(string template, string resolvedSizeLabel) =>
        template.Replace("{size}", resolvedSizeLabel ?? string.Empty).Trim();

    private static MenuItemRef? FindStandaloneComponent(
        IReadOnlyList<CandidateItem> items,
        string comboSlot,
        string bundleMenuPeriod,
        string preferredDifferentSize,
        IReadOnlySet<string> downMachines)
    {
        foreach (var item in items)
        {
            if (!string.Equals(item.ComboSlot, comboSlot, StringComparison.Ordinal) ||
                !IsMenuPeriodCompatible(item.MenuPeriod, bundleMenuPeriod) ||
                IsMachineGated(item, downMachines) ||
                item.Sizes.Count == 0)
            {
                continue;
            }

            var size = item.Sizes.FirstOrDefault(s => !string.Equals(s.Size, preferredDifferentSize, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(size.Size))
            {
                size = item.Sizes[0];
            }

            return new MenuItemRef(item.Name, size.Size, size.Price);
        }

        return null;

        static bool IsMachineGated(CandidateItem item, IReadOnlySet<string> downMachines) =>
            item.RequiresMachine is { Length: > 0 } machine && downMachines.Contains(machine);

        static bool IsMenuPeriodCompatible(string componentMenuPeriod, string bundleMenuPeriod) =>
            string.IsNullOrEmpty(componentMenuPeriod) ||
            string.Equals(componentMenuPeriod, "allDay", StringComparison.Ordinal) ||
            string.Equals(componentMenuPeriod, bundleMenuPeriod, StringComparison.Ordinal);
    }

    private static IReadOnlyList<CandidateItem> ReadMenuItems(string personasDir, string personaId)
    {
        var menuItemsPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(menuItemsPath));

        var items = new List<CandidateItem>();
        foreach (var category in doc.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                var sizes = item.GetProperty("sizes").EnumerateArray()
                    .Select(s => (s.GetProperty("size").GetString()!, s.GetProperty("price").GetDecimal()))
                    .ToList();

                var bundleSlots = new List<string>();
                var autoFill = new Dictionary<string, string>(StringComparer.Ordinal);
                var bundleDefaultSize = string.Empty;
                if (item.TryGetProperty("bundle", out var bundleElement))
                {
                    if (bundleElement.TryGetProperty("slots", out var slotsElement))
                    {
                        bundleSlots = slotsElement.EnumerateArray().Select(s => s.GetString()!).ToList();
                    }

                    if (bundleElement.TryGetProperty("autoFill", out var autoFillElement))
                    {
                        foreach (var prop in autoFillElement.EnumerateObject())
                        {
                            autoFill[prop.Name] = prop.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (bundleElement.TryGetProperty("defaultSize", out var defaultSizeElement))
                    {
                        bundleDefaultSize = defaultSizeElement.GetString() ?? string.Empty;
                    }
                }

                items.Add(new CandidateItem(
                    item.GetProperty("name").GetString()!,
                    item.TryGetProperty("menuPeriod", out var menuPeriodElement) ? menuPeriodElement.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("comboSlot", out var comboSlotElement) ? comboSlotElement.GetString() : null,
                    sizes,
                    bundleSlots,
                    autoFill,
                    bundleDefaultSize,
                    item.TryGetProperty("requiresMachine", out var requiresMachineElement) ? requiresMachineElement.GetString() : null));
            }
        }

        return items;
    }

    private static IReadOnlySet<string> CurrentlyDownMachines(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(personaJsonPath));
        var down = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("machines", out var machines))
        {
            foreach (var machine in machines.EnumerateObject())
            {
                if (machine.Value.TryGetProperty("status", out var status) && status.GetString() == "down")
                {
                    down.Add(machine.Name);
                }
            }
        }

        return down;
    }
}

file sealed class BundleAutoFillFixture(string personaId) : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(HappyHourJustBeforeOpenFixture.Instant);
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class RealPackBundleAutoFillConformanceTests
{
    public static TheoryData<PersonaBundleAutoFillMenuData.BundleAutoFillCase> DiscoveredBundleAutoFillCases()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<PersonaBundleAutoFillMenuData.BundleAutoFillCase>();
        foreach (var personaId in ConformancePersonas.DiscoverFromDisk())
        {
            if (!PersonaBundleAutoFillMenuData.HasAnyBundleAutoFill(personasDir, personaId))
            {
                continue;
            }

            var discovered = PersonaBundleAutoFillMenuData.Discover(personasDir, personaId);
            Assert.True(discovered is not null,
                $"persona '{personaId}' has at least one real bundle.autoFill block in its own menu/menuItems.json, " +
                "but no qualifying row could be discovered. Ensure at least one such bundle also has " +
                "a real bundle.defaultSize and real standalone side/drink slot items for absorption coverage.");
            data.Add(discovered!);
        }

        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleAutoFillCases))]
    public async Task Discovered_pack_autofills_its_own_bundle_slots_when_meal_size_is_explicit(
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new BundleAutoFillFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: bundleCase.PersonaId, mode: bundleCase.MenuMode);
            await using var _ = browser;

            var added = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("add", bundleCase.BundleName, bundleCase.SizedBundleSize, 1, bundleCase.SizedBundlePrice)],
                roundTripIndex, ct, callIdPrefix: "call_real_pack_bundle_autofill_sized");

            AssertBundleState(added.ToolResultJson!, bundleCase, bundleCase.SizedBundleSize);
        });
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleAutoFillCases))]
    public async Task Discovered_pack_uses_its_own_bundle_default_size_for_unsized_bundle_autofill(
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new BundleAutoFillFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: bundleCase.PersonaId, mode: bundleCase.MenuMode);
            await using var _ = browser;

            var added = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("add", bundleCase.BundleName, "standard", 1, bundleCase.DefaultBundlePrice)],
                roundTripIndex, ct, callIdPrefix: "call_real_pack_bundle_autofill_unsized");

            AssertBundleState(added.ToolResultJson!, bundleCase, bundleCase.DefaultBundleSize);
        });
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleAutoFillCases))]
    public async Task Discovered_pack_absorbs_preordered_side_or_drink_instead_of_autofilling_that_slot(
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new BundleAutoFillFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            Assert.True(bundleCase.PreorderedSide is not null,
                $"persona '{bundleCase.PersonaId}' bundle '{bundleCase.BundleName}' needs a real comboSlot:sides item " +
                "to prove side absorption beats autoFill.");
            Assert.True(bundleCase.PreorderedDrink is not null,
                $"persona '{bundleCase.PersonaId}' bundle '{bundleCase.BundleName}' needs a real comboSlot:drinks item " +
                "to prove drink absorption beats autoFill.");

            var sideFirst = await AddStandaloneThenBundleAsync(
                fixture, bundleCase.PersonaId, bundleCase.MenuMode, bundleCase.PreorderedSide!, bundleCase, ct,
                callIdPrefix: "call_real_pack_bundle_absorb_side");
            AssertAbsorptionState(
                sideFirst.ToolResultJson!, bundleCase, bundleCase.SizedBundleSize,
                absorbedComponent: bundleCase.PreorderedSide!.Display, absorbedSlot: "sides");

            var drinkFirst = await AddStandaloneThenBundleAsync(
                fixture, bundleCase.PersonaId, bundleCase.MenuMode, bundleCase.PreorderedDrink!, bundleCase, ct,
                callIdPrefix: "call_real_pack_bundle_absorb_drink");
            AssertAbsorptionState(
                drinkFirst.ToolResultJson!, bundleCase, bundleCase.SizedBundleSize,
                absorbedComponent: bundleCase.PreorderedDrink!.Display, absorbedSlot: "drinks");
        });
    }

    private static async Task<ToolCallResult> AddStandaloneThenBundleAsync(
        ConformanceFixture fixture,
        string personaId,
        string? mode,
        PersonaBundleAutoFillMenuData.MenuItemRef preordered,
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase,
        CancellationToken ct,
        string callIdPrefix)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId, mode: mode);
        await using var _ = browser;

        return await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", preordered.Name, preordered.Size, 1, preordered.Price),
                ("add", bundleCase.BundleName, bundleCase.SizedBundleSize, 1, bundleCase.SizedBundlePrice),
            ],
            roundTripIndex, ct, callIdPrefix: callIdPrefix);
    }

    private static void AssertBundleState(
        string orderSummaryJson,
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase,
        string resolvedSizeLabel)
    {
        using var order = JsonDocument.Parse(orderSummaryJson);
        var items = order.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var bundle = items[0];
        var components = bundle.GetProperty("components").EnumerateArray()
            .Select(component => component.GetString() ?? string.Empty)
            .ToArray();

        foreach (var template in bundleCase.AutoFillTemplates.Values)
        {
            var expected = PersonaBundleAutoFillMenuData.ExpandAutoFill(template, resolvedSizeLabel);
            Assert.Contains(expected, components);
        }
    }

    private static void AssertAbsorptionState(
        string orderSummaryJson,
        PersonaBundleAutoFillMenuData.BundleAutoFillCase bundleCase,
        string resolvedSizeLabel,
        string absorbedComponent,
        string absorbedSlot)
    {
        using var order = JsonDocument.Parse(orderSummaryJson);
        var items = order.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var bundle = items[0];
        var components = bundle.GetProperty("components").EnumerateArray()
            .Select(component => component.GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(absorbedComponent, components);
        if (bundleCase.AutoFillTemplates.TryGetValue(absorbedSlot, out var absorbedTemplate))
        {
            var absorbedAutoFill = PersonaBundleAutoFillMenuData.ExpandAutoFill(absorbedTemplate, resolvedSizeLabel);
            if (!string.Equals(absorbedAutoFill, absorbedComponent, StringComparison.Ordinal))
            {
                Assert.DoesNotContain(absorbedAutoFill, components);
            }
        }

        foreach (var (slot, template) in bundleCase.AutoFillTemplates)
        {
            if (string.Equals(slot, absorbedSlot, StringComparison.Ordinal))
            {
                continue;
            }

            var expected = PersonaBundleAutoFillMenuData.ExpandAutoFill(template, resolvedSizeLabel);
            Assert.Contains(expected, components);
        }
    }
}
