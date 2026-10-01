using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Ordering;

/// <summary>
/// Port of a representative subset of app/backend/tests/test_extras_rules.py's happy-hour pricing
/// cases (#113) against the real "test-alpha" fixture pack (window 14:00-16:00 America/Chicago,
/// 0.5 multiplier, "Alpha Cola" is the only <c>happyHourDiscounted: true</c> item) -- exercised
/// directly against <see cref="OrderState.HandleOrderUpdate"/> (bypassing
/// <c>OrderToolExecutor</c>'s add-time machine gate, exactly like the Python test suite's own
/// "seeded directly via handle_order_update" technique) since Alpha Cola's own
/// <c>requiresMachine: soda_machine</c> is (deliberately) "down" in this pack, for an unrelated
/// scenario elsewhere.
///
/// <para><b>Not parallel-unsafe:</b> <see cref="ConformanceHooks"/>'s two env vars are read fresh
/// on every call and are process-wide, but only a persona with a configured
/// <c>pricing.happyHour</c> window ever calls <see cref="ConformanceHooks.Now"/> at all -- every
/// other test in this project builds an <c>OrderState</c> for a persona with <c>happyHour: null</c>,
/// so it can never observe this class's env var mutations. xUnit itself runs the [Fact]s within
/// one class sequentially, but a SECOND class elsewhere in this project now also freezes this
/// SAME process-wide clock against a REAL happy-hour-configured persona (<c>sonic</c>, in
/// <c>ComboInstanceDeterminismAndLifecycleTests.HappyHour_DoesNotDiscountAResizedComboDrink</c>,
/// PR #184 round 2) -- and xUnit runs DIFFERENT classes in parallel by default, so without
/// coordination those two classes' freezes could race each other's env var mutations. Both classes
/// are pinned to the shared <see cref="ClockHookTestCollection"/> xUnit collection (<c>[Collection(
/// ClockHookTestCollection.Name)]</c>) specifically so xUnit never runs them concurrently with each
/// other, eliminating that race.</para>
/// </summary>
[Collection(ClockHookTestCollection.Name)]
public sealed class HappyHourPricingTests : IDisposable
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string FixedNowEnv = "CONFORMANCE_FIXED_NOW";

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

    private static Persona Alpha() => DeltaFixture.Load("test-alpha");

    [Fact]
    public void OutsideHappyHourWindow_DrinkIsFullPrice()
    {
        // 13:00 America/Chicago (CST, -06:00) -- one hour before the 14:00 window opens.
        FreezeAt("2026-01-15T19:00:00Z");
        var order = PersonaOrderFactory.CreateOrderState(Alpha());

        order.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);

        Assert.False(order.IsHappyHour());
        Assert.Equal(3.98m, order.Summary.Total);
        Assert.Equal("", order.HappyHourBanner);
    }

    [Fact]
    public void InsideHappyHourWindow_DiscountedDrinkIsHalvedAndBannerAnnounces()
    {
        // 15:00 America/Chicago (CST, -06:00) -- inside the 14:00-16:00 window.
        FreezeAt("2026-01-15T21:00:00Z");
        var order = PersonaOrderFactory.CreateOrderState(Alpha());

        order.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);

        Assert.True(order.IsHappyHour());
        Assert.Equal(1.99m, order.Summary.Total); // 2 * 1.99 * 0.5
        Assert.Equal(" [ALPHA HAPPY HOUR ACTIVE]", order.HappyHourBanner);
    }

    [Fact]
    public void InsideHappyHourWindow_NonDiscountedItemIsUnaffected()
    {
        FreezeAt("2026-01-15T21:00:00Z");
        var order = PersonaOrderFactory.CreateOrderState(Alpha());

        order.HandleOrderUpdate("add", "Alpha Burger", "small", 1, 3.99m);

        Assert.True(order.IsHappyHour());
        Assert.Equal(3.99m, order.Summary.Total);
    }

    [Fact]
    public void TaxIsAlwaysComputedFromThisPersonasOwnRateAfterAnyHappyHourDiscount()
    {
        FreezeAt("2026-01-15T21:00:00Z"); // inside window
        var order = PersonaOrderFactory.CreateOrderState(Alpha());

        order.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m); // 1.99 after discount

        // test-alpha's own taxRate is "0.05".
        Assert.Equal(0.0995m, order.Summary.Tax);
        Assert.Equal(2.0895m, order.Summary.FinalTotal);
    }
}

/// <summary>PR #184 round 2: defines the shared xUnit collection that forces every test class
/// freezing the process-wide <c>CONFORMANCE_TEST_HOOKS</c>/<c>CONFORMANCE_FIXED_NOW</c> clock hook
/// against a REAL happy-hour-configured persona to run sequentially with each other (xUnit never
/// runs two classes in the SAME collection concurrently, even though it runs different collections
/// in parallel by default) -- see <see cref="HappyHourPricingTests"/> and
/// <c>ComboInstanceDeterminismAndLifecycleTests</c>.</summary>
[CollectionDefinition(Name)]
public sealed class ClockHookTestCollection
{
    public const string Name = "Process-wide clock hook tests";
}
