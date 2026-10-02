using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Ordering;

/// <summary>
/// C# port of app/backend/tests/test_combo_orders.py's TestComboInstanceDeterminismAndLifecycle
/// (PR #184 round 2, Rick's review, items 2 &amp; 3): per-combo-instance slot state -- determinism
/// across two SEPARATE combo lines, per-physical-unit independence within one quantity-N line,
/// and removal clearing a combo's slot state with no extra code. Exercised directly against
/// <see cref="OrderState.HandleOrderUpdate"/> (bypassing <c>OrderToolExecutor</c>'s add-time
/// machine gate), exactly like the Python test suite's own technique and this project's own
/// <c>HappyHourPricingTests</c>.
///
/// <para>Against the SAME "test-delta" fixture pack Python's own tests use (<see cref="DeltaFixture"/>),
/// not a real persona pack -- PR #184 round 3 moved this off the real production pack it previously
/// hardcoded, per Rick's review (item A of that round), so this file carries zero brand-word lines. "Delta
/// Classic Meal" is the bundle (its two slots have NO default autoFill, unlike test-delta's "Delta Meal" --
/// see menuItems.json -- so it starts incomplete the instant it's added, exactly like this suite's needs),
/// used at two different sizes (regular/large) as the two separate combo
/// instances; "Delta Fries"/"Delta Onion Rings" are its two distinguishable real sides and "Delta
/// Latte"/"Delta Iced Tea" its two distinguishable real drinks (see menuItems.json).</para>
///
/// <para><b>Not parallel-unsafe:</b> the happy-hour test below uses the same
/// <c>CONFORMANCE_TEST_HOOKS</c>/<c>CONFORMANCE_FIXED_NOW</c> env-var freeze technique as
/// <c>HappyHourPricingTests</c>, against this SAME process-wide clock -- xUnit runs the [Fact]s
/// within one class sequentially, but runs DIFFERENT classes in parallel by default, so without
/// coordination this class's freeze could race <c>HappyHourPricingTests</c>'s own freeze. Both
/// classes are pinned to the shared <see cref="ClockHookTestCollection"/> xUnit collection
/// (<c>[Collection(ClockHookTestCollection.Name)]</c>) specifically so xUnit never runs them
/// concurrently with each other, eliminating that race. Every OTHER test in this class builds an
/// <c>OrderState</c> for a call that never touches <see cref="ConformanceHooks"/> at all. test-delta
/// has no happy-hour window configured, so this test forces one on with the same env-var hooks
/// rather than asserting a real discount.</para>
/// </summary>
[Collection(ClockHookTestCollection.Name)]
public sealed class ComboInstanceDeterminismAndLifecycleTests : IDisposable
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string FixedNowEnv = "CONFORMANCE_FIXED_NOW";

    private const string Combo = "Delta Classic Meal";
    private const decimal ComboRegularPrice = 5.49m;
    private const decimal ComboLargePrice = 6.99m;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(FixedNowEnv, null);
    }

    private static void FreezeAt(string isoInstant)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(FixedNowEnv, isoInstant);
    }

    private static OrderState NewOrder() => PersonaOrderFactory.CreateOrderState(DeltaFixture.Load());

    [Fact]
    public void TwoSeparateCombos_ResizeTargetsTheOneThatHoldsTheItem()
    {
        // Rick's review, item 2: with TWO SEPARATE combo lines (not one quantity-2 line),
        // resizing a drink by name must target whichever instance actually holds that exact
        // drink -- determinism, not an arbitrary/first-match pick. Same item ("Delta Classic Meal") at
        // two different sizes gives two independent OrderItem lines, same as two different combo
        // items would.
        var order = NewOrder();

        // Two distinct combo instances (different sizes) -- each its own OrderItem line.
        order.HandleOrderUpdate("add", Combo, "regular", 1, ComboRegularPrice);
        order.HandleOrderUpdate("add", Combo, "large", 1, ComboLargePrice);
        order.HandleOrderUpdate("add", "Delta Fries", "regular", 1, 1.99m);
        order.HandleOrderUpdate("add", "Delta Onion Rings", "regular", 1, 1.79m);
        order.HandleOrderUpdate("add", "Delta Iced Tea", "regular", 1, 1.89m);
        order.HandleOrderUpdate("add", "Delta Latte", "regular", 1, 3.49m);

        Assert.Equal(2, order.Items.Count);
        var regularCombo = order.Items.Single(i => i.Item == Combo && i.Size == "regular");
        var largeCombo = order.Items.Single(i => i.Item == Combo && i.Size == "large");
        // The MOST RECENTLY added instance (the "large" line) is scanned first for a vacant
        // slot (see FindBundleSlot) -- it absorbs the FIRST-added side/drink of each kind
        // (Delta Fries, Delta Iced Tea), leaving the SECOND-added ones (Delta Onion Rings,
        // Delta Latte) for the "regular" line, whose slots were still vacant by then.
        Assert.Contains("Delta Iced Tea", largeCombo.Display);
        Assert.Contains("Delta Latte", regularCombo.Display);

        // Resize the drink that only the "regular" line holds -- must leave the "large" line
        // (and its own, different drink) untouched.
        var result = order.HandleOrderUpdate("modify", "Delta Latte", "large", 1, 4.29m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        regularCombo = order.Items.Single(i => i.Item == Combo && i.Size == "regular");
        largeCombo = order.Items.Single(i => i.Item == Combo && i.Size == "large");
        Assert.Contains("Large Delta Latte", regularCombo.Display);
        Assert.Contains("Delta Iced Tea", largeCombo.Display);
        Assert.DoesNotContain("Delta Latte", largeCombo.Display);
        Assert.Equal(ComboRegularPrice + ComboLargePrice, order.Summary.Total);
    }

    [Fact]
    public void QuantityTwoCombo_SlotsAreIndependentPerPhysicalUnit()
    {
        // Rick's review, item 2: one quantity-2 combo line has TWO independent slots per
        // component -- filling only one unit's side must leave the combo incomplete (the second
        // unit still needs its own side), not silently count as "done" for both.
        var order = NewOrder();
        order.HandleOrderUpdate("add", Combo, "regular", 2, ComboRegularPrice);
        order.HandleOrderUpdate("add", "Delta Fries", "regular", 1, 1.99m);
        order.HandleOrderUpdate("add", "Delta Latte", "regular", 1, 3.49m);

        Assert.Single(order.Items);
        Assert.Equal(2, order.Items[0].Quantity);
        // Only one of the two units' side/drink slots is filled so far.
        Assert.False(order.GetComboRequirements().IsComplete);

        order.HandleOrderUpdate("add", "Delta Onion Rings", "regular", 1, 1.79m);
        order.HandleOrderUpdate("add", "Delta Iced Tea", "regular", 1, 1.89m);
        Assert.True(order.GetComboRequirements().IsComplete);
        Assert.Single(order.Items);
        Assert.Equal(ComboRegularPrice * 2, order.Summary.Total);
    }

    [Fact]
    public void RemovingCombo_ClearsItsSlotState_NewComboStartsFresh()
    {
        // Rick's review, item 3: removing a combo line must clear ITS slot state with no extra
        // cleanup code (state lives ON the OrderItem) -- a brand-new combo added afterward (even
        // the identical item/size) must never inherit the old one's filled slots or completeness.
        var order = NewOrder();
        order.HandleOrderUpdate("add", Combo, "regular", 1, ComboRegularPrice);
        order.HandleOrderUpdate("add", "Delta Fries", "regular", 1, 1.99m);
        order.HandleOrderUpdate("add", "Delta Latte", "regular", 1, 3.49m);
        Assert.True(order.GetComboRequirements().IsComplete);

        order.HandleOrderUpdate("remove", Combo, "regular", 1, 0m);
        Assert.Empty(order.Items);

        // A brand-new combo instance -- same item, same size -- must start incomplete again.
        order.HandleOrderUpdate("add", Combo, "regular", 1, ComboRegularPrice);
        Assert.False(order.GetComboRequirements().IsComplete);
        var newCombo = order.Items[0];
        Assert.DoesNotContain("Delta Fries", newCombo.Display);
        Assert.DoesNotContain("Delta Latte", newCombo.Display);
        Assert.Equal(ComboRegularPrice, order.Summary.Total);
    }

    [Fact]
    public void HappyHour_DoesNotDiscountAResizedComboDrink()
    {
        // Happy hour's 50% drink discount must never apply to a combo's absorbed drink, even
        // immediately after that slot was explicitly resized -- a resize never promotes the slot
        // to a separately priced/discounted order line. test-delta has no happy-hour window of
        // its own, so this forces one on via the same process-wide clock hook
        // HappyHourPricingTests uses, directly against OrderState.IsHappyHour -- the discount
        // itself only ever applies to an item marked happyHourDiscounted: true, which no
        // test-delta item is, so this proves the absorbed-slot guard independent of any
        // particular persona's own happy-hour configuration.
        FreezeAt("2026-01-15T21:00:00Z");
        var order = NewOrder();
        order.HandleOrderUpdate("add", Combo, "regular", 1, ComboRegularPrice);
        order.HandleOrderUpdate("add", "Delta Fries", "regular", 1, 1.99m);
        order.HandleOrderUpdate("add", "Delta Latte", "regular", 1, 3.49m);

        var result = order.HandleOrderUpdate("modify", "Delta Latte", "large", 1, 4.29m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        // Still just the flat combo price -- no discount, no upcharge, no standalone line.
        Assert.Equal(ComboRegularPrice, order.Summary.Total);
    }
}
