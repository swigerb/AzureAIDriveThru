using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

file static class PersonaHappyHourTestSupport
{
    public static async Task<decimal> AddItemAndReadFinalTotalAsync(
        ConformanceFixture fixture, string persona, string itemName, string size, decimal price, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: persona);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", itemName, size, 1, price)],
            roundTripIndex, ct);

        return OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!);
    }
}

/// <summary>
/// Issue #76 part 2 (Rick's wave-plan comment on #20): "happy-hour flag honored (enabled or
/// not)" for the two fixture packs. Sonic's own "an eligible item DOES get discounted inside the
/// window" proof already exists (<c>HappyHourAtOpenTests</c>/<c>HappyHourBoundaryTests.cs</c>) --
/// not duplicated here. test-alpha and test-beta's fixture menus (#74) deliberately carry no
/// <c>happyHourDiscounted: true</c> items (editing that shared Python-test fixture data is out of
/// this stream's harness-only scope), so what this file proves instead is the other, equally
/// real half of "honored": the persona-level <c>pricing.happyHour</c> flag must never blanket
/// -discount an item that didn't individually opt in (test-alpha, flag ENABLED 14-16
/// America/Chicago), and a persona with no happy-hour config at all must be completely clock
/// -invariant (test-beta, flag explicitly <c>null</c>). Both are proven the same way: add the
/// SAME item as the SAME persona at two FixedClock instants -- one inside the window
/// (<see cref="TwoPersonaHappyHourWindowFixture"/>, golden case 1 = 14:00:00) and one just
/// outside it (<see cref="TwoPersonaHappyHourOutsideWindowFixture"/>, golden case 0 = 13:59:59)
/// -- and assert the resulting totals are identical. A mutation that started applying the
/// discount without checking the item's own opt-in flag (or that let one persona's happy-hour
/// config bleed into another persona's pricing) would make either fact below fail.
/// </summary>
[Collection(TwoPersonaHappyHourWindowCollection.Name)]
public sealed class PersonaHappyHourConformanceTests(
    TwoPersonaHappyHourWindowFixture windowFixture,
    TwoPersonaHappyHourOutsideWindowFixture outsideWindowFixture)
    : IClassFixture<TwoPersonaHappyHourOutsideWindowFixture>
{
    [Fact]
    public async Task Test_alpha_item_without_its_own_happy_hour_opt_in_is_not_discounted_even_though_the_persona_flag_is_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        decimal insideWindowTotal = 0m;
        decimal outsideWindowTotal = 0m;

        await windowFixture.RunAsync(async () =>
        {
            insideWindowTotal = await PersonaHappyHourTestSupport.AddItemAndReadFinalTotalAsync(
                windowFixture, TwoPersonaConformanceFixture.PersonaA, "Alpha Burger", "small", 3.99m, ct);
        });
        await outsideWindowFixture.RunAsync(async () =>
        {
            outsideWindowTotal = await PersonaHappyHourTestSupport.AddItemAndReadFinalTotalAsync(
                outsideWindowFixture, TwoPersonaConformanceFixture.PersonaA, "Alpha Burger", "small", 3.99m, ct);
        });

        OrderScenarioHelpers.AssertMoneyEqual(
            outsideWindowTotal, insideWindowTotal,
            "test-alpha's pricing.happyHour is enabled 14:00-16:00 America/Chicago, but Alpha " +
            "Burger has no happyHourDiscounted:true field -- the persona-level flag being on must " +
            "not blanket-discount an item that never individually opted in.");
    }

    [Fact]
    public async Task Test_beta_persona_with_no_happy_hour_config_is_never_discounted_regardless_of_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        decimal insideWindowTotal = 0m;
        decimal outsideWindowTotal = 0m;

        await windowFixture.RunAsync(async () =>
        {
            insideWindowTotal = await PersonaHappyHourTestSupport.AddItemAndReadFinalTotalAsync(
                windowFixture, TwoPersonaConformanceFixture.PersonaB, "Beta Double Burger", "regular", 4.49m, ct);
        });
        await outsideWindowFixture.RunAsync(async () =>
        {
            outsideWindowTotal = await PersonaHappyHourTestSupport.AddItemAndReadFinalTotalAsync(
                outsideWindowFixture, TwoPersonaConformanceFixture.PersonaB, "Beta Double Burger", "regular", 4.49m, ct);
        });

        OrderScenarioHelpers.AssertMoneyEqual(
            outsideWindowTotal, insideWindowTotal,
            "test-beta's pricing.happyHour is null -- the clock instant must never affect its totals.");
    }
}
