using System.Linq;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #179 (live bug): a guest's combo drink was resized by the model calling
/// `remove &lt;item&gt; &lt;old size&gt;` then `add &lt;item&gt; &lt;new size&gt;`. The `remove` didn't vacate the
/// combo's drink slot, so the following `add` created a standalone duplicate drink line instead of
/// refilling (resizing) the slot. This file is the end-to-end, brand-agnostic regression proof: a
/// per-pack Theory discovering, from THIS SAME pack's own real menu/menuItems.json (never a
/// hardcoded name), one real bundle whose own drink slot is genuinely open after the bundle is
/// added (its own `bundle.autoFill`, if any, does not cover "drinks" -- see
/// <see cref="ComboBundleDiscovery.Discover"/>'s own doc comment for why that's required), then
/// reproduces the EXACT live sequence against it: add the bundle, add a side (only if this pack's
/// own bundle needs an explicit one -- a pack whose own autoFill already covers "sides", e.g.
/// another real pack's own autoFill'd side, needs none), add the drink at its smaller real size (absorbed into the
/// open slot for free -- ordinary bundle-pivot absorption, not part of #179 itself), then resize
/// that SAME drink to its larger real size two different ways: via `remove` then `add` (
/// <see cref="Discovered_pack_resizes_the_combo_drink_via_remove_then_add"/>, the exact live
/// sequence) and via the explicit `modify` action (
/// <see cref="Discovered_pack_resizes_the_combo_drink_via_explicit_modify"/>, #179's other
/// required path). Both must land on exactly the SAME observable state: one combo line showing the
/// LARGER drink, no standalone drink line at all, and a total of this pack's own bundle price plus
/// only the real per-pack upsize delta (the larger size's own menu price minus the smaller size's
/// own menu price) -- never a silent duplicate, and never an invented price.
///
/// A pack with no bundle on its own menu at all, or whose only bundle(s)
/// auto-fill their own drink slot the instant they're added (leaving no open-slot window at all to
/// reproduce #179 against), is naturally absent from <see cref="DiscoveredBundleResizeCases"/>'s
/// Theory rows rather than failing -- see <see cref="ComboBundleDiscovery.Discover"/>.
///
/// Runs under a FixedClock pinned just before the happy-hour window opens (same fixture shape as
/// <see cref="ComboAbsorptionTests"/>), one dedicated backend per discovered pack (mirroring <see
/// cref="RealPackMealNumberConformanceTests"/>'s own per-row fixture, since each pack must bind a
/// DIFFERENT persona) -- several discovered drinks are happy-hour-discounted on their own pack's
/// menu, so a real wall-clock run during that window would make the expected upsize delta
/// non-deterministic.
/// </summary>
public static class ComboBundleDiscovery
{
    public sealed record SizedItem(string Name, string Size, decimal Price);

    public sealed record BundleResizeCase(
        string PersonaId,
        string BundleName,
        string BundleSize,
        decimal BundlePrice,
        SizedItem? Side,
        SizedItem DrinkFromSize,
        SizedItem DrinkToSize)
    {
        /// <summary>The real per-pack upsize delta this pack's own menu charges for resizing the
        /// discovered drink from <see cref="DrinkFromSize"/> to <see cref="DrinkToSize"/>.</summary>
        public decimal UpsizeDelta => DrinkToSize.Price - DrinkFromSize.Price;

        public override string ToString() => PersonaId;
    }

    private sealed record CandidateItem(
        string Name, string? ComboSlot, IReadOnlyList<(string Size, decimal Price)> Sizes,
        IReadOnlyList<string>? BundleSlots, IReadOnlySet<string> AutoFillKeys, string? RequiresMachine);

    /// <summary>This pack's own persona.json `machines` block entries currently reported `"down"`
    /// -- mirrors <see cref="GoldenMenuCategoryData.LoadCurrentlyDownMachines"/>, generalized to any
    /// pack id instead of a hardcoded single pack name.</summary>
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

    private static IReadOnlyList<CandidateItem> ReadMenuItems(string personasDir, string personaId)
    {
        var menuItemsPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(menuItemsPath));

        var items = new List<CandidateItem>();
        foreach (var category in doc.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var comboSlot = item.TryGetProperty("comboSlot", out var slotEl) ? slotEl.GetString() : null;
                var sizes = item.GetProperty("sizes").EnumerateArray()
                    .Select(s => (s.GetProperty("size").GetString()!, s.GetProperty("price").GetDecimal()))
                    .ToList();
                var requiresMachine = item.TryGetProperty("requiresMachine", out var rm) ? rm.GetString() : null;

                IReadOnlyList<string>? bundleSlots = null;
                var autoFillKeys = new HashSet<string>(StringComparer.Ordinal);
                if (item.TryGetProperty("bundle", out var bundleEl))
                {
                    if (bundleEl.TryGetProperty("slots", out var slotsEl))
                    {
                        bundleSlots = slotsEl.EnumerateArray().Select(s => s.GetString()!).ToList();
                    }

                    if (bundleEl.TryGetProperty("autoFill", out var autoFillEl))
                    {
                        foreach (var prop in autoFillEl.EnumerateObject())
                        {
                            autoFillKeys.Add(prop.Name);
                        }
                    }
                }

                items.Add(new CandidateItem(name, comboSlot, sizes, bundleSlots, autoFillKeys, requiresMachine));
            }
        }

        return items;
    }

    /// <summary>
    /// Finds, in THIS pack's own menu/menuItems.json, the first real bundle whose own
    /// `bundle.slots` includes "drinks" AND whose own `bundle.autoFill` (if present at all) does
    /// NOT cover "drinks" -- a bundle that genuinely leaves its drink slot open after being added,
    /// exactly like real combo bundles with no autoFill at all, and real meal bundles whose autoFill
    /// covers only "sides", via their own default side -- so there is a real open-slot window for a
    /// standalone drink add to land in and later resize, reproducing #179's own sequence. A bundle
    /// whose own autoFill already covers "drinks" (none on any real pack today) would never leave
    /// that window open at all and is skipped. Also requires a real drinks-category item on this
    /// SAME pack's own menu with at least two DIFFERENT real prices across its sizes (so the
    /// resize has an observable, non-zero, pack-real upsize delta) and, only if this bundle's own
    /// "sides" slot is NOT already auto-filled, a real sides-category item too. Machine-gated items
    /// (this SAME pack's own persona.json `machines` block) are never chosen as the drink or side,
    /// so a currently-down machine can never make a discovered row flaky. Returns null -- never
    /// throws -- for a pack with no bundle on its own menu at all, or none that qualifies, so that
    /// pack is simply absent from the discovered Theory rows.
    /// </summary>
    public static BundleResizeCase? Discover(string personasDir, string personaId)
    {
        var items = ReadMenuItems(personasDir, personaId);

        bool IsMachineGated(CandidateItem item, IReadOnlySet<string> downMachines) =>
            item.RequiresMachine is { } machine && downMachines.Contains(machine);

        var downMachines = CurrentlyDownMachines(personasDir, personaId);

        foreach (var bundleItem in items.Where(i => i.BundleSlots is not null))
        {
            if (!bundleItem.BundleSlots!.Contains("drinks") || bundleItem.AutoFillKeys.Contains("drinks"))
            {
                continue;
            }

            if (bundleItem.Sizes.Count == 0)
            {
                continue;
            }

            var drinkItem = items.FirstOrDefault(i =>
                i.ComboSlot == "drinks" &&
                !IsMachineGated(i, downMachines) &&
                i.Sizes.Select(s => s.Price).Distinct().Count() >= 2);
            if (drinkItem is null)
            {
                continue;
            }

            var sortedDrinkSizes = drinkItem.Sizes
                .GroupBy(s => s.Price).Select(g => g.First())
                .OrderBy(s => s.Price)
                .ToList();
            var fromSize = sortedDrinkSizes[0];
            var toSize = sortedDrinkSizes[1];

            SizedItem? side = null;
            if (bundleItem.BundleSlots!.Contains("sides") && !bundleItem.AutoFillKeys.Contains("sides"))
            {
                var sideItem = items.FirstOrDefault(i =>
                    i.ComboSlot == "sides" && !IsMachineGated(i, downMachines) && i.Sizes.Count > 0);
                if (sideItem is null)
                {
                    // This bundle genuinely needs an explicit standalone side and this pack's
                    // menu has none usable -- try the next bundle candidate rather than silently
                    // skipping the side the live sequence depends on.
                    continue;
                }

                var sideSize = sideItem.Sizes[0];
                side = new SizedItem(sideItem.Name, sideSize.Size, sideSize.Price);
            }

            var bundleSize = bundleItem.Sizes[0];
            return new BundleResizeCase(
                personaId,
                bundleItem.Name,
                bundleSize.Size,
                bundleSize.Price,
                side,
                new SizedItem(drinkItem.Name, fromSize.Size, fromSize.Price),
                new SizedItem(drinkItem.Name, toSize.Size, toSize.Price));
        }

        return null;
    }
}

/// <summary>
/// One dedicated backend PER discovered pack -- each row binds a DIFFERENT persona (mirroring
/// <see cref="RealPackMealNumberConformanceTests"/>'s own per-row `MealNumberFixture`), and runs
/// under a FixedClock pinned just before the happy-hour window opens (mirroring <see
/// cref="ComboAbsorptionTests"/>'s own `HappyHourJustBeforeOpenFixture`) since several discovered
/// packs' own drinks are happy-hour-discounted and a real wall-clock run during that window would
/// make the expected upsize delta non-deterministic. <c>PersonasDir</c> is deliberately left null
/// (the base class's default, real <c>personas/</c> root): every row here is a REAL pack.
/// </summary>
file sealed class ComboBundleResizeFixture(string personaId) : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(HappyHourJustBeforeOpenFixture.Instant);
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class ComboComponentResizeConformanceTests
{
    public static TheoryData<ComboBundleDiscovery.BundleResizeCase> DiscoveredBundleResizeCases()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<ComboBundleDiscovery.BundleResizeCase>();
        foreach (var personaId in ConformancePersonas.DiscoverFromDisk())
        {
            var discovered = ComboBundleDiscovery.Discover(personasDir, personaId);
            if (discovered is not null)
            {
                data.Add(discovered);
            }
        }

        return data;
    }

    /// <summary>Scripts the live sequence's own setup half -- add the bundle, add the side (only
    /// if this pack's own bundle needs an explicit one), add the drink at its smaller real size --
    /// on the CALLER's already-connected browser/connection, so a resize script can keep scripting
    /// further steps on that SAME connection afterward (xUnit requires exactly one open connection
    /// at a time -- see ConnectAndGreetAsync's own leaked-connection assertion -- so this never
    /// opens a second one of its own).</summary>
    private static async Task<ToolCallResult> SeedComboThenFillDrinkAsync(
        FakeRealtimeConnection connection, RealtimeBrowserClient browser,
        ComboBundleDiscovery.BundleResizeCase bundleCase, int roundTripIndex, CancellationToken ct)
    {
        var steps = new List<(string Action, string Item, string Size, int Quantity, decimal Price)>
        {
            ("add", bundleCase.BundleName, bundleCase.BundleSize, 1, bundleCase.BundlePrice),
        };
        if (bundleCase.Side is { } side)
        {
            steps.Add(("add", side.Name, side.Size, 1, side.Price));
        }

        steps.Add(("add", bundleCase.DrinkFromSize.Name, bundleCase.DrinkFromSize.Size, 1, bundleCase.DrinkFromSize.Price));

        return await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser, steps, roundTripIndex, ct, callIdPrefix: "call_seed");
    }

    private static void AssertResizedComboOnlyNoStandaloneDrink(
        ComboBundleDiscovery.BundleResizeCase bundleCase, string orderSummaryJson)
    {
        var order = JsonDocument.Parse(orderSummaryJson).RootElement;
        var items = order.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items); // exactly one combo line, never a standalone drink duplicate

        var comboItem = items[0];
        Assert.Equal(bundleCase.BundleName, comboItem.GetProperty("item").GetString());
        var display = comboItem.GetProperty("display").GetString()!;
        Assert.Contains(bundleCase.DrinkToSize.Size, display);
        Assert.DoesNotContain(bundleCase.DrinkFromSize.Size, display);

        OrderScenarioHelpers.AssertMoneyEqual(
            bundleCase.BundlePrice + bundleCase.UpsizeDelta,
            OrderScenarioHelpers.GetOrderTotal(orderSummaryJson),
            $"persona '{bundleCase.PersonaId}': resizing '{bundleCase.DrinkFromSize.Name}' from " +
            $"{bundleCase.DrinkFromSize.Size} to {bundleCase.DrinkToSize.Size} must charge only " +
            $"this pack's own real upsize delta ({bundleCase.UpsizeDelta}) on top of the bundle's " +
            $"own price ({bundleCase.BundlePrice}), never duplicate the drink's full standalone price.");
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleResizeCases))]
    public async Task Discovered_pack_resizes_the_combo_drink_via_remove_then_add(
        ComboBundleDiscovery.BundleResizeCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ComboBundleResizeFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: bundleCase.PersonaId);
            await using var _ = browser;

            var seeded = await SeedComboThenFillDrinkAsync(connection, browser, bundleCase, roundTripIndex, ct);

            // The exact #179 live sequence: `remove` the slot-filling drink at its CURRENT size,
            // then `add` the SAME item at a DIFFERENT size, on the SAME connection/order.
            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [
                    ("remove", bundleCase.DrinkFromSize.Name, bundleCase.DrinkFromSize.Size, 1, 0m),
                    ("add", bundleCase.DrinkToSize.Name, bundleCase.DrinkToSize.Size, 1, bundleCase.DrinkToSize.Price),
                ],
                seeded.RoundTripIndex, ct, callIdPrefix: "call_resize");

            AssertResizedComboOnlyNoStandaloneDrink(bundleCase, result.ToolResultJson!);
        });
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleResizeCases))]
    public async Task Discovered_pack_resizes_the_combo_drink_via_explicit_modify(
        ComboBundleDiscovery.BundleResizeCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ComboBundleResizeFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: bundleCase.PersonaId);
            await using var _ = browser;

            var seedResult = await SeedComboThenFillDrinkAsync(connection, browser, bundleCase, roundTripIndex, ct);

            // #179's other required path: the explicit `modify` action resizes the slot item in
            // place directly (no `remove` call at all), priced identically to the remove-then-add
            // path above.
            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("modify", bundleCase.DrinkToSize.Name, bundleCase.DrinkToSize.Size, 1, bundleCase.DrinkToSize.Price)],
                seedResult.RoundTripIndex, ct, callIdPrefix: "call_modify");

            AssertResizedComboOnlyNoStandaloneDrink(bundleCase, result.ToolResultJson!);
        });
    }
}
