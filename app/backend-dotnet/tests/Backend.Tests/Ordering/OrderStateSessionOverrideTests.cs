using Backend.Ordering;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Ordering;

[Collection(ClockHookTestCollection.Name)]
public sealed class OrderStateSessionOverrideTests : IDisposable
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

    [Fact]
    public void SetMachineOverride_accepts_a_known_machine_and_applies_it_immediately()
    {
        var order = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));

        Assert.Equal("down", order.EffectiveMachineStatus("soda_machine"));

        Assert.True(order.SetMachineOverride("soda_machine", "up"));
        Assert.Equal("up", order.EffectiveMachineStatus("soda_machine"));
        Assert.Equal("up", order.GetMachineOverrides()["soda_machine"]);
    }

    [Fact]
    public void SetMachineOverride_rejects_unknown_machine_and_invalid_status_without_mutation()
    {
        var order = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));

        Assert.False(order.SetMachineOverride("unknown_machine", "up"));
        Assert.False(order.SetMachineOverride("soda_machine", "operational"));
        Assert.Empty(order.GetMachineOverrides());
        Assert.Equal("down", order.EffectiveMachineStatus("soda_machine"));
        Assert.Null(order.EffectiveMachineStatus("unknown_machine"));
    }

    [Fact]
    public void HappyHourMode_on_and_off_override_the_window_and_auto_restores_it()
    {
        FreezeAt("2026-01-15T19:00:00Z"); // 13:00 America/Chicago, outside 14:00-16:00.
        var forcedOn = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));
        Assert.False(forcedOn.IsHappyHour());
        Assert.True(forcedOn.SetHappyHourMode("on"));
        forcedOn.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);
        Assert.Equal("on", forcedOn.GetHappyHourMode());
        Assert.True(forcedOn.IsHappyHour());
        Assert.Equal(1.99m, forcedOn.Summary.Total);

        var auto = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));
        Assert.True(auto.SetHappyHourMode("on"));
        Assert.True(auto.SetHappyHourMode("auto"));
        auto.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);
        Assert.Equal("auto", auto.GetHappyHourMode());
        Assert.False(auto.IsHappyHour());
        Assert.Equal(3.98m, auto.Summary.Total);

        FreezeAt("2026-01-15T21:00:00Z"); // 15:00 America/Chicago, inside the window.
        var forcedOff = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));
        Assert.True(forcedOff.SetHappyHourMode("off"));
        forcedOff.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);
        Assert.Equal("off", forcedOff.GetHappyHourMode());
        Assert.False(forcedOff.IsHappyHour());
        Assert.Equal(3.98m, forcedOff.Summary.Total);
    }

    [Fact]
    public void SetHappyHourMode_is_rejected_for_a_persona_without_happy_hour()
    {
        var order = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-delta"));

        Assert.False(order.SetHappyHourMode("on"));
        Assert.Equal("auto", order.GetHappyHourMode());
        Assert.False(order.IsHappyHour());
    }

    /// <summary>#309 (R2): C# parity for order_state.py's own `set_happy_hour_mode` ->
    /// `_update_summary()` fix. The item is added FIRST, OUTSIDE the happy-hour window (so
    /// `Summary.Total` is cached at full price with no further mutation after this point) --
    /// flipping the mode to "on" must recompute `Summary` IMMEDIATELY, not only the next time
    /// some unrelated order mutation happens to refresh the cache. Without the
    /// <c>SetHappyHourMode</c> -> <c>UpdateSummary</c> call, `Summary.Total` would still read the
    /// stale full price here even though <see cref="OrderState.HappyHourBanner"/> (computed live)
    /// already announces happy hour -- exactly the guest-visible total/banner disagreement this
    /// fix prevents.</summary>
    [Fact]
    public void SetHappyHourMode_RecomputesTheCachedSummaryImmediately_SoTotalAndBannerAgree()
    {
        FreezeAt("2026-01-15T19:00:00Z"); // 13:00 America/Chicago, outside 14:00-16:00.
        var order = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load("test-alpha"));
        order.HandleOrderUpdate("add", "Alpha Cola", "small", 2, 1.99m);
        Assert.Equal(3.98m, order.Summary.Total); // full price -- outside the window

        Assert.True(order.SetHappyHourMode("on"));

        // No further order mutation happened -- the cached summary must already reflect the
        // discount the live banner is about to announce.
        Assert.Equal(1.99m, order.Summary.Total); // 2 * 1.99 * 0.5
        Assert.Equal(" [ALPHA HAPPY HOUR ACTIVE] [HAPPY HOUR DISCOUNT APPLIED TO: 2 x Small Alpha Cola]", order.HappyHourBanner);

        Assert.True(order.SetHappyHourMode("off"));

        // Flipping back off, again with no other mutation, must restore the full price just as
        // immediately.
        Assert.Equal(3.98m, order.Summary.Total);
        Assert.Equal("", order.HappyHourBanner);
    }
}
