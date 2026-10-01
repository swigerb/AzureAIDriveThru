using Backend.Ordering;
using Backend.Personas;

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
/// <para>Uses the REAL, production "sonic" persona pack (not a test fixture) -- mirroring
/// Python's own choice to exercise this against real menu data, not a synthetic fixture -- loaded
/// explicitly (<c>personasEnv</c>/<c>defaultPersonaEnv</c> pinned to "sonic" so this test can
/// never be affected by this process's own <c>PERSONAS</c>/<c>DEFAULT_PERSONA</c> env vars).</para>
///
/// <para><b>Not parallel-unsafe:</b> the happy-hour test below uses the same
/// <c>CONFORMANCE_TEST_HOOKS</c>/<c>CONFORMANCE_FIXED_NOW</c> env-var freeze technique as
/// <c>HappyHourPricingTests</c>, against this SAME process-wide clock -- xUnit runs the [Fact]s
/// within one class sequentially, but runs DIFFERENT classes in parallel by default, so without
/// coordination this class's freeze could race <c>HappyHourPricingTests</c>'s own freeze. Both
/// classes are pinned to the shared <see cref="ClockHookTestCollection"/> xUnit collection
/// (<c>[Collection(ClockHookTestCollection.Name)]</c>) specifically so xUnit never runs them
/// concurrently with each other, eliminating that race. Every OTHER test in this class builds an
/// <c>OrderState</c> for a call that never touches <see cref="ConformanceHooks"/> at all.</para>
/// </summary>
[Collection(ClockHookTestCollection.Name)]
public sealed class ComboInstanceDeterminismAndLifecycleTests : IDisposable
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string FixedNowEnv = "CONFORMANCE_FIXED_NOW";

    private const string Combo = "SONIC® Cheeseburger Combo";
    private const decimal ComboPrice = 9.19m;

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

    private static OrderState NewSonicOrder()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir, personasEnv: "sonic", defaultPersonaEnv: "sonic");
        return PersonaOrderFactory.CreateOrderState(catalog.Get("sonic"));
    }

    [Fact]
    public void TwoSeparateCombos_ResizeTargetsTheOneThatHoldsTheItem()
    {
        // Rick's review, item 2: with TWO SEPARATE combo lines (not one quantity-2 line),
        // resizing a drink by name must target whichever instance actually holds that exact
        // drink -- determinism, not an arbitrary/first-match pick.
        const string otherCombo = "Fish Sandwich Combo";
        const decimal otherComboPrice = 8.39m;
        var order = NewSonicOrder();

        // Two distinct combo instances (different items) -- each its own OrderItem line.
        order.HandleOrderUpdate("add", Combo, "standard", 1, ComboPrice);
        order.HandleOrderUpdate("add", otherCombo, "standard", 1, otherComboPrice);
        order.HandleOrderUpdate("add", "Tots", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Groovy Fries", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Cherry Limeade", "medium", 1, 2.89m);
        order.HandleOrderUpdate("add", "Ocean Water®", "medium", 1, 2.89m);

        Assert.Equal(2, order.Items.Count);
        var cheeseburgerCombo = order.Items.Single(i => i.Item == Combo);
        var fishCombo = order.Items.Single(i => i.Item == otherCombo);
        // The MOST RECENTLY added instance (Fish Sandwich Combo) is scanned first for a vacant
        // slot (see FindBundleSlot) -- it absorbs Tots/Cherry Limeade, leaving Groovy
        // Fries/Ocean Water for the cheeseburger combo.
        Assert.Contains("Cherry Limeade", fishCombo.Display);
        Assert.Contains("Ocean Water®", cheeseburgerCombo.Display);

        // Resize the drink that only the fish combo holds -- must leave the cheeseburger combo
        // (and its own, different drink) untouched.
        var result = order.HandleOrderUpdate("modify", "Cherry Limeade", "large", 1, 3.39m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        cheeseburgerCombo = order.Items.Single(i => i.Item == Combo);
        fishCombo = order.Items.Single(i => i.Item == otherCombo);
        Assert.Contains("Large Cherry Limeade", fishCombo.Display);
        Assert.Contains("Ocean Water®", cheeseburgerCombo.Display);
        Assert.DoesNotContain("Cherry Limeade", cheeseburgerCombo.Display);
        Assert.Equal(ComboPrice + otherComboPrice, order.Summary.Total);
    }

    [Fact]
    public void QuantityTwoCombo_SlotsAreIndependentPerPhysicalUnit()
    {
        // Rick's review, item 2: one quantity-2 combo line has TWO independent slots per
        // component -- filling only one unit's side must leave the combo incomplete (the second
        // unit still needs its own side), not silently count as "done" for both.
        var order = NewSonicOrder();
        order.HandleOrderUpdate("add", Combo, "standard", 2, ComboPrice);
        order.HandleOrderUpdate("add", "Tots", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Cherry Limeade", "medium", 1, 2.89m);

        Assert.Single(order.Items);
        Assert.Equal(2, order.Items[0].Quantity);
        // Only one of the two units' side/drink slots is filled so far.
        Assert.False(order.GetComboRequirements().IsComplete);

        order.HandleOrderUpdate("add", "Groovy Fries", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Ocean Water®", "medium", 1, 2.89m);
        Assert.True(order.GetComboRequirements().IsComplete);
        Assert.Single(order.Items);
        Assert.Equal(ComboPrice * 2, order.Summary.Total);
    }

    [Fact]
    public void RemovingCombo_ClearsItsSlotState_NewComboStartsFresh()
    {
        // Rick's review, item 3: removing a combo line must clear ITS slot state with no extra
        // cleanup code (state lives ON the OrderItem) -- a brand-new combo added afterward (even
        // the identical item/size) must never inherit the old one's filled slots or completeness.
        var order = NewSonicOrder();
        order.HandleOrderUpdate("add", Combo, "standard", 1, ComboPrice);
        order.HandleOrderUpdate("add", "Tots", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Cherry Limeade", "medium", 1, 2.89m);
        Assert.True(order.GetComboRequirements().IsComplete);

        order.HandleOrderUpdate("remove", Combo, "standard", 1, 0m);
        Assert.Empty(order.Items);

        // A brand-new combo instance -- same item, same size -- must start incomplete again.
        order.HandleOrderUpdate("add", Combo, "standard", 1, ComboPrice);
        Assert.False(order.GetComboRequirements().IsComplete);
        var newCombo = order.Items[0];
        Assert.DoesNotContain("Tots", newCombo.Display);
        Assert.DoesNotContain("Cherry Limeade", newCombo.Display);
        Assert.Equal(ComboPrice, order.Summary.Total);
    }

    [Fact]
    public void HappyHour_DoesNotDiscountAResizedComboDrink()
    {
        // Happy hour's 50% drink discount must never apply to a combo's absorbed drink, even
        // immediately after that slot was explicitly resized -- a resize never promotes the slot
        // to a separately priced/discounted order line. Sonic's own happy hour window is
        // 14:00-16:00 America/Chicago (persona.json); 15:00 America/Chicago = 21:00 UTC, and
        // "Cherry Limeade" is one of Sonic's own happyHourDiscounted: true items (menuItems.json).
        FreezeAt("2026-01-15T21:00:00Z");
        var order = NewSonicOrder();
        order.HandleOrderUpdate("add", Combo, "standard", 1, ComboPrice);
        order.HandleOrderUpdate("add", "Tots", "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", "Cherry Limeade", "medium", 1, 2.89m);

        Assert.True(order.IsHappyHour());
        var result = order.HandleOrderUpdate("modify", "Cherry Limeade", "large", 1, 3.39m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        // Still just the flat combo price -- no discount, no upcharge, no standalone line.
        Assert.Equal(ComboPrice, order.Summary.Total);
    }
}
