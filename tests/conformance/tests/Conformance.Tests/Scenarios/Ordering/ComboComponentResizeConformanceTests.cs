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
/// LARGER drink, no standalone drink line at all, and a total of EXACTLY this pack's own bundle
/// price (#179 round 2, issue #184, Rick's required item 1: every pack's own bundle price already
/// includes a slot-filling side/drink at ANY size it's filled at -- `includedAnySize`, the default
/// `bundles.resizeRule` -- so resizing the drink in place never adds or credits anything; "the
/// bundle price plus the real upsize delta" was round 1's bug, not round 2's fix) -- never a silent
/// duplicate, and never an invented price.
///
/// A pack with no bundle on its own menu at all, whose only bundle(s) auto-fill their own drink
/// slot the instant they're added (leaving no open-slot window at all to reproduce #179 against),
/// or whose own `bundles.resizeRule` is `wholeBundleSize` (that pack's component-resize behavior is
/// fundamentally different -- resizing ANY absorbed slot cascades into resizing the WHOLE bundle,
/// see <see cref="ComboBundleDiscovery.BundleResizeRule"/> and
/// <see cref="WholeBundleSizeResizeConformanceTests"/>, the dedicated scenario for that rule) is
/// naturally absent from <see cref="DiscoveredBundleResizeCases"/>'s Theory rows rather than
/// failing -- see <see cref="ComboBundleDiscovery.Discover"/>.
///
/// Runs under a FixedClock pinned just before the happy-hour window opens (same fixture shape as
/// <see cref="ComboAbsorptionTests"/>), one dedicated backend per discovered pack (mirroring <see
/// cref="RealPackMealNumberConformanceTests"/>'s own per-row fixture, since each pack must bind a
/// DIFFERENT persona) -- several discovered drinks are happy-hour-discounted on their own pack's
/// menu, so a real wall-clock run during that window would make the expected total
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
        public override string ToString() => PersonaId;
    }

    /// <summary>#184 round 3 (Rick's review, item H(b)): two SEPARATE, DISTINCTLY-NAMED bundle
    /// items on the SAME pack, each with its own genuinely open drink slot, so a conformance
    /// Theory can force two instances onto the order at once and verify a resize lands on
    /// exactly the instance that holds the targeted drink -- never the other one.</summary>
    public sealed record TwoInstanceCase(
        string PersonaId,
        string FirstBundleName,
        string FirstBundleSize,
        decimal FirstBundlePrice,
        string SecondBundleName,
        string SecondBundleSize,
        decimal SecondBundlePrice,
        SizedItem DrinkFromSize,
        SizedItem DrinkToSize,
        SizedItem SecondDrink)
    {
        public override string ToString() => PersonaId;
    }

    public sealed record WholeBundleResizeCase(
        string PersonaId,
        string BundleName,
        string FromSize,
        decimal FromPrice,
        string ToSize,
        decimal ToPrice,
        SizedItem? Drink)
    {
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

    /// <summary>This pack's own persona.json `bundles.resizeRule` (#184 round 2, Rick's required
    /// item 1/2): `"includedAnySize"` (the default, when absent) means a bundle's own price always
    /// includes a filled slot at ANY size for free, so resizing a slot component never changes the
    /// bundle's own total. `"wholeBundleSize"` means resizing ANY slot component instead cascades
    /// into resizing the WHOLE bundle's own line (see
    /// <see cref="DiscoverWholeBundleResize"/>) -- a fundamentally different mechanism this
    /// generic per-component <see cref="Discover"/> scenario does not apply to.</summary>
    public static string BundleResizeRule(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(personaJsonPath));
        if (doc.RootElement.TryGetProperty("bundles", out var bundles) &&
            bundles.TryGetProperty("resizeRule", out var rule) &&
            rule.GetString() is { Length: > 0 } ruleValue)
        {
            return ruleValue;
        }

        return "includedAnySize";
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
    /// so a currently-down machine can never make a discovered row flaky. Skips this pack entirely
    /// (returns null) when its own `bundles.resizeRule` is `wholeBundleSize` -- resizing a slot
    /// component on that rule cascades into resizing the WHOLE bundle (see `ApplyWholeBundleResize`
    /// in OrderState.cs / `_apply_whole_bundle_resize` in order_state.py), a fundamentally
    /// different mechanism covered by the dedicated <see cref="DiscoverWholeBundleResize"/>
    /// scenario instead. Returns null -- never throws -- for a pack with no bundle on its own menu
    /// at all, or none that qualifies, so that pack is simply absent from the discovered Theory
    /// rows.
    /// </summary>
    public static BundleResizeCase? Discover(string personasDir, string personaId)
    {
        if (BundleResizeRule(personasDir, personaId) != "includedAnySize")
        {
            return null;
        }

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

                // #184 round 3 (Rick's review, item H(c)): the real live sequence always orders
                // the side at its LARGEST real size (e.g. "Large Tots"), not whatever happens to
                // sort first in this pack's own menuItems.json -- sort by price like the drink
                // sizes above and take the largest, so a pack whose own JSON lists Small before
                // Large (or any other order) still reproduces the live sequence's own size.
                var sideSize = sideItem.Sizes
                    .GroupBy(s => s.Price).Select(g => g.First())
                    .OrderByDescending(s => s.Price)
                    .First();
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

    /// <summary>
    /// #184 round 3 (Rick's review, item H(b)): finds, on THIS SAME pack's own menu, TWO
    /// separate, distinctly-named `includedAnySize` bundles that each genuinely leave their own
    /// drink slot open (same open-slot test as <see cref="Discover"/>), plus a real drinks-category
    /// item with at least two different real prices (for the resize) and a SECOND, distinctly-
    /// named real drinks-category item (to seed the other instance so the test can tell them
    /// apart). Deliberately ignores any "sides" slot on either bundle -- only drink-slot
    /// absorption-order determinism is under test here. Returns null for any pack that doesn't
    /// have two qualifying bundles or two distinct drinks (most packs, which have only one
    /// qualifying combo line), so that pack is simply absent from the discovered Theory rows.
    /// </summary>
    public static TwoInstanceCase? DiscoverTwoInstance(string personasDir, string personaId)
    {
        if (BundleResizeRule(personasDir, personaId) != "includedAnySize")
        {
            return null;
        }

        var items = ReadMenuItems(personasDir, personaId);

        bool IsMachineGated(CandidateItem item, IReadOnlySet<string> downMachines) =>
            item.RequiresMachine is { } machine && downMachines.Contains(machine);

        var downMachines = CurrentlyDownMachines(personasDir, personaId);

        var openDrinkBundles = items
            .Where(i => i.BundleSlots is not null
                && i.BundleSlots!.Contains("drinks") && !i.AutoFillKeys.Contains("drinks")
                && i.Sizes.Count > 0)
            .ToList();
        if (openDrinkBundles.Count < 2)
        {
            return null;
        }

        var first = openDrinkBundles[0];
        var second = openDrinkBundles[1];

        var drinkItem = items.FirstOrDefault(i =>
            i.ComboSlot == "drinks" &&
            !IsMachineGated(i, downMachines) &&
            i.Sizes.Select(s => s.Price).Distinct().Count() >= 2);
        if (drinkItem is null)
        {
            return null;
        }

        var sortedDrinkSizes = drinkItem.Sizes
            .GroupBy(s => s.Price).Select(g => g.First())
            .OrderBy(s => s.Price)
            .ToList();
        var fromSize = sortedDrinkSizes[0];
        var toSize = sortedDrinkSizes[1];

        var secondDrinkItem = items.FirstOrDefault(i =>
            i.ComboSlot == "drinks" && i.Name != drinkItem.Name &&
            !IsMachineGated(i, downMachines) && i.Sizes.Count > 0);
        if (secondDrinkItem is null)
        {
            return null;
        }

        var firstSize = first.Sizes[0];
        var secondSize = second.Sizes[0];
        return new TwoInstanceCase(
            personaId,
            first.Name,
            firstSize.Size,
            firstSize.Price,
            second.Name,
            secondSize.Size,
            secondSize.Price,
            new SizedItem(drinkItem.Name, fromSize.Size, fromSize.Price),
            new SizedItem(drinkItem.Name, toSize.Size, toSize.Price),
            new SizedItem(secondDrinkItem.Name, secondDrinkItem.Sizes[0].Size, secondDrinkItem.Sizes[0].Price));
    }

    /// <summary>
    /// Finds, in THIS pack's own menu/menuItems.json, the first real bundle with at least two
    /// DIFFERENT real prices across its OWN sizes (the whole-bundle "make it a large meal" resize's
    /// own target/source, not any slot component's size) on a pack whose own `bundles.resizeRule`
    /// is `wholeBundleSize` (see <see cref="BundleResizeRule"/>). Resolves <paramref
    /// name="personaId"/>'s own smallest- and largest-priced bundle sizes, and, if the bundle's own
    /// "drinks" slot is genuinely open (same open-slot test as <see cref="Discover"/>), a real
    /// drinks-category item to explicitly fill it with (proving the explicit slot gets relabeled to
    /// the bundle's new size too, not just its autofilled sides) -- machine-gated items are never
    /// chosen. Returns null -- never throws -- for a pack whose own resizeRule isn't
    /// `wholeBundleSize`, or that has no bundle with two distinct sizes on its own menu, so that
    /// pack is simply absent from the discovered Theory rows.
    /// </summary>
    public static WholeBundleResizeCase? DiscoverWholeBundleResize(string personasDir, string personaId)
    {
        if (BundleResizeRule(personasDir, personaId) != "wholeBundleSize")
        {
            return null;
        }

        var items = ReadMenuItems(personasDir, personaId);
        var downMachines = CurrentlyDownMachines(personasDir, personaId);

        foreach (var bundleItem in items.Where(i => i.BundleSlots is not null))
        {
            var distinctSizes = bundleItem.Sizes
                .GroupBy(s => s.Price).Select(g => g.First())
                .OrderBy(s => s.Price)
                .ToList();
            if (distinctSizes.Count < 2)
            {
                continue;
            }

            var fromSize = distinctSizes[0];
            var toSize = distinctSizes[^1];

            SizedItem? drink = null;
            if (bundleItem.BundleSlots!.Contains("drinks") && !bundleItem.AutoFillKeys.Contains("drinks"))
            {
                var drinkItem = items.FirstOrDefault(i =>
                    i.ComboSlot == "drinks" &&
                    !(i.RequiresMachine is { } machine && downMachines.Contains(machine)) &&
                    i.Sizes.Count > 0);
                if (drinkItem is not null)
                {
                    var drinkSize = drinkItem.Sizes[0];
                    drink = new SizedItem(drinkItem.Name, drinkSize.Size, drinkSize.Price);
                }
            }

            return new WholeBundleResizeCase(
                personaId, bundleItem.Name, fromSize.Size, fromSize.Price, toSize.Size, toSize.Price, drink);
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
            bundleCase.BundlePrice,
            OrderScenarioHelpers.GetOrderTotal(orderSummaryJson),
            $"persona '{bundleCase.PersonaId}': resizing '{bundleCase.DrinkFromSize.Name}' from " +
            $"{bundleCase.DrinkFromSize.Size} to {bundleCase.DrinkToSize.Size} must charge EXACTLY " +
            $"this pack's own bundle price ({bundleCase.BundlePrice}) -- `includedAnySize` means a " +
            "slot-filling drink at ANY size is already included for free, so resizing it in place " +
            "never adds a per-pack upsize delta on top (that was round 1's bug) and never " +
            "duplicates the drink's full standalone price.");
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

    /// <summary>#184 round 3 (Rick's review, item H(a)): path independence -- this SAME pack's
    /// own drink added directly at its LARGER real size up front must charge the EXACT SAME
    /// total as seeding it at the smaller size and resizing to that same larger size later. Both
    /// paths run on their own fresh connection (xUnit requires exactly one open connection at a
    /// time), one after the other on the SAME fixture/backend instance.</summary>
    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredBundleResizeCases))]
    public async Task Discovered_pack_charges_the_same_total_large_up_front_or_resized_later(
        ComboBundleDiscovery.BundleResizeCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ComboBundleResizeFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            async Task<decimal> UpFrontTotalAsync()
            {
                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                    fixture, ct, persona: bundleCase.PersonaId);
                await using var _ = browser;

                var steps = new List<(string Action, string Item, string Size, int Quantity, decimal Price)>
                {
                    ("add", bundleCase.BundleName, bundleCase.BundleSize, 1, bundleCase.BundlePrice),
                };
                if (bundleCase.Side is { } side)
                {
                    steps.Add(("add", side.Name, side.Size, 1, side.Price));
                }

                steps.Add(("add", bundleCase.DrinkToSize.Name, bundleCase.DrinkToSize.Size, 1, bundleCase.DrinkToSize.Price));

                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser, steps, roundTripIndex, ct, callIdPrefix: "call_upfront");
                return OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!);
            }

            async Task<decimal> ResizedLaterTotalAsync()
            {
                var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                    fixture, ct, persona: bundleCase.PersonaId);
                await using var _ = browser;

                var seeded = await SeedComboThenFillDrinkAsync(connection, browser, bundleCase, roundTripIndex, ct);
                var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser,
                    [("modify", bundleCase.DrinkToSize.Name, bundleCase.DrinkToSize.Size, 1, bundleCase.DrinkToSize.Price)],
                    seeded.RoundTripIndex, ct, callIdPrefix: "call_resize_later");
                return OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!);
            }

            var upFrontTotal = await UpFrontTotalAsync();
            var resizedLaterTotal = await ResizedLaterTotalAsync();

            OrderScenarioHelpers.AssertMoneyEqual(
                upFrontTotal, resizedLaterTotal,
                $"persona '{bundleCase.PersonaId}': adding '{bundleCase.DrinkToSize.Name}' at " +
                $"{bundleCase.DrinkToSize.Size} up front must charge the SAME total as adding it " +
                $"at {bundleCase.DrinkFromSize.Size} and resizing to {bundleCase.DrinkToSize.Size} " +
                "later -- path independence, #184 round 3 item H(a).");
        });
    }

    public static TheoryData<ComboBundleDiscovery.TwoInstanceCase> DiscoveredTwoInstanceResizeCases()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<ComboBundleDiscovery.TwoInstanceCase>();
        foreach (var personaId in ConformancePersonas.DiscoverFromDisk())
        {
            if (ComboBundleDiscovery.DiscoverTwoInstance(personasDir, personaId) is { } discovered)
            {
                data.Add(discovered);
            }
        }

        return data;
    }

    /// <summary>#184 round 3 (Rick's review, item H(b)): end-to-end mirror of Backend.Tests'
    /// `TwoSeparateCombos_ResizeTargetsTheOneThatHoldsTheItem` -- two SEPARATE bundle instances
    /// (distinct names on the same pack), each absorbing its own distinguishable drink. The MOST
    /// RECENTLY added instance is scanned first for a vacant slot, so the first drink added after
    /// both instances lands on it; the second, distinct drink then lands on the first-added
    /// instance instead. Resizing the drink that only the most-recently-added instance holds must
    /// leave the other instance (and its own, different drink) completely untouched --
    /// determinism by identity, never an arbitrary/first-match pick.</summary>
    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredTwoInstanceResizeCases))]
    public async Task Discovered_pack_with_two_bundle_instances_resizes_only_the_holder(
        ComboBundleDiscovery.TwoInstanceCase twoCase)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var fixture = new ComboBundleResizeFixture(twoCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: twoCase.PersonaId);
            await using var _ = browser;

            var seedSteps = new List<(string Action, string Item, string Size, int Quantity, decimal Price)>
            {
                ("add", twoCase.FirstBundleName, twoCase.FirstBundleSize, 1, twoCase.FirstBundlePrice),
                ("add", twoCase.SecondBundleName, twoCase.SecondBundleSize, 1, twoCase.SecondBundlePrice),
                // The MOST RECENTLY added instance (SecondBundleName) is scanned first for a
                // vacant drink slot, so this drink absorbs into THAT instance first.
                ("add", twoCase.DrinkFromSize.Name, twoCase.DrinkFromSize.Size, 1, twoCase.DrinkFromSize.Price),
                // Its drink slot now filled -- this second, distinct drink absorbs into the
                // FIRST-added instance instead.
                ("add", twoCase.SecondDrink.Name, twoCase.SecondDrink.Size, 1, twoCase.SecondDrink.Price),
            };
            var seeded = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser, seedSteps, roundTripIndex, ct, callIdPrefix: "call_seed_two");

            var seededItems = JsonDocument.Parse(seeded.ToolResultJson!).RootElement
                .GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, seededItems.Count);

            var seededFirstInstance = seededItems.Single(i => i.GetProperty("item").GetString() == twoCase.FirstBundleName);
            var seededSecondInstance = seededItems.Single(i => i.GetProperty("item").GetString() == twoCase.SecondBundleName);
            Assert.Contains(twoCase.SecondDrink.Name, seededFirstInstance.GetProperty("display").GetString());
            Assert.Contains(twoCase.DrinkFromSize.Name, seededSecondInstance.GetProperty("display").GetString());

            // Resize the drink that only the most-recently-added (second) instance holds.
            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("modify", twoCase.DrinkToSize.Name, twoCase.DrinkToSize.Size, 1, twoCase.DrinkToSize.Price)],
                seeded.RoundTripIndex, ct, callIdPrefix: "call_resize_holder");

            var items = JsonDocument.Parse(result.ToolResultJson!).RootElement
                .GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);

            var resizedSecondInstance = items.Single(i => i.GetProperty("item").GetString() == twoCase.SecondBundleName);
            var untouchedFirstInstance = items.Single(i => i.GetProperty("item").GetString() == twoCase.FirstBundleName);

            Assert.Contains(twoCase.DrinkToSize.Size, resizedSecondInstance.GetProperty("display").GetString());
            Assert.DoesNotContain(twoCase.DrinkFromSize.Size, resizedSecondInstance.GetProperty("display").GetString());
            Assert.Contains(twoCase.SecondDrink.Name, untouchedFirstInstance.GetProperty("display").GetString());

            OrderScenarioHelpers.AssertMoneyEqual(
                twoCase.FirstBundlePrice + twoCase.SecondBundlePrice,
                OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                $"persona '{twoCase.PersonaId}': two separate bundle instances " +
                $"('{twoCase.FirstBundleName}' and '{twoCase.SecondBundleName}') must each keep " +
                "their own bundle price after only one is resized -- #184 round 3 item H(b).");
        });
    }
}

/// <summary>
/// Issue #184 round 2 (Rick's required item 1/2, discovered while fixing
/// <see cref="ComboComponentResizeConformanceTests"/> above): a pack whose own `bundles.resizeRule`
/// is `wholeBundleSize` (today, exactly the real pack whose own app resizes "the WHOLE meal", never
/// just a slot) has a fundamentally different resize mechanism than <see
/// cref="ComboComponentResizeConformanceTests"/> covers -- filling or resizing ANY slot component
/// on that rule cascades into resizing the BUNDLE'S OWN line (`ApplyWholeBundleResize` in
/// OrderState.cs / `_apply_whole_bundle_resize` in order_state.py): the bundle's own size and price
/// change together, and every filled slot (autofilled or explicit) is relabeled to the new size,
/// never separately priced. This end-to-end, brand-agnostic Theory discovers, from THIS SAME
/// pack's own real menu/menuItems.json (never a hardcoded name), one real bundle with at least two
/// distinct real sizes/prices, seeds it at its smallest size (plus an explicit drink, only if this
/// bundle's own drink slot is genuinely open), then resizes the BUNDLE ITSELF (never a slot
/// component) to its largest size via `modify` -- mirroring the real "make it a large meal" UX --
/// and asserts exactly one order line, charged EXACTLY that largest size's own real menu price (no
/// separate slot pricing at all), with every filled slot's display relabeled to the new size and
/// the old size's label gone. A pack whose own `bundles.resizeRule` isn't `wholeBundleSize`, or
/// that has no bundle with two distinct sizes on its own menu, is naturally absent from
/// <see cref="DiscoveredWholeBundleResizeCases"/>'s Theory rows rather than failing -- see
/// <see cref="ComboBundleDiscovery.DiscoverWholeBundleResize"/>.
/// </summary>
public sealed class WholeBundleSizeResizeConformanceTests
{
    public static TheoryData<ComboBundleDiscovery.WholeBundleResizeCase> DiscoveredWholeBundleResizeCases()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<ComboBundleDiscovery.WholeBundleResizeCase>();
        foreach (var personaId in ConformancePersonas.DiscoverFromDisk())
        {
            var discovered = ComboBundleDiscovery.DiscoverWholeBundleResize(personasDir, personaId);
            if (discovered is not null)
            {
                data.Add(discovered);
            }
        }

        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredWholeBundleResizeCases))]
    public async Task Discovered_whole_bundle_size_pack_resizes_the_meal_and_relabels_its_slots(
        ComboBundleDiscovery.WholeBundleResizeCase bundleCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ComboBundleResizeFixture(bundleCase.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                fixture, ct, persona: bundleCase.PersonaId);
            await using var _ = browser;

            var steps = new List<(string Action, string Item, string Size, int Quantity, decimal Price)>
            {
                ("add", bundleCase.BundleName, bundleCase.FromSize, 1, bundleCase.FromPrice),
            };
            if (bundleCase.Drink is { } drink)
            {
                steps.Add(("add", drink.Name, drink.Size, 1, drink.Price));
            }

            var seeded = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser, steps, roundTripIndex, ct, callIdPrefix: "call_seed_meal");

            // "Make it a large meal" -- modifies the WHOLE bundle's own line, never a slot
            // component, matching the real app's own whole-meal resize UX.
            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("modify", bundleCase.BundleName, bundleCase.ToSize, 1, bundleCase.ToPrice)],
                seeded.RoundTripIndex, ct, callIdPrefix: "call_resize_meal");

            var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
            var items = order.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items); // the meal resize is one line, never split into a second one

            var meal = items[0];
            Assert.Equal(bundleCase.BundleName, meal.GetProperty("item").GetString());
            var display = meal.GetProperty("display").GetString()!;
            Assert.Contains(bundleCase.ToSize, display);
            Assert.DoesNotContain(bundleCase.FromSize, display); // old size's label is gone, not just the bundle's own

            OrderScenarioHelpers.AssertMoneyEqual(
                bundleCase.ToPrice,
                OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                $"persona '{bundleCase.PersonaId}': resizing the WHOLE bundle '{bundleCase.BundleName}' " +
                $"to {bundleCase.ToSize} must charge EXACTLY that size's own real menu price " +
                $"({bundleCase.ToPrice}) -- `wholeBundleSize` reprices the bundle's own line, never a " +
                "slot component separately.");
        });
    }
}
