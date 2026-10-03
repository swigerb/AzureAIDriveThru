using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

file static class PersonaHappyHourTestSupport
{
    public static async Task<ToolCallResult> AddItemAndReadResultAsync(
        ConformanceFixture fixture, string persona, string itemName, string size, decimal price, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: persona);
        await using var _ = browser;

        return await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", itemName, size, 1, price)],
            roundTripIndex, ct);
    }

    public static async Task<decimal> AddItemAndReadFinalTotalAsync(
        ConformanceFixture fixture, string persona, string itemName, string size, decimal price, CancellationToken ct)
    {
        var result = await AddItemAndReadResultAsync(fixture, persona, itemName, size, price, ct);
        return OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!);
    }
}

/// <summary>
/// Issue #76 part 2 (Rick's wave-plan comment on #20, and Rick's PR #108 review required items 2
/// and 3): "happy-hour flag honored (enabled or not)" for the two fixture packs, now covering both
/// the positive and negative half of "honored". Sonic's own "an eligible item DOES get discounted
/// AND announces its own banner inside the window" proof lives in
/// <c>HappyHourAtOpenTests</c>/<c>HappyHourBoundaryTests.cs</c> -- not duplicated here.
/// test-alpha's menu (#74) now carries exactly ONE <c>happyHourDiscounted: true</c> item ("Alpha
/// Cola", per Rick's PR #108 review required item 2) so this file can prove test-alpha's own
/// positive case the same way Sonic's is proven: inside test-alpha's window the item is
/// discounted by test-alpha's OWN priceMultiplier (0.5) and the `update_order` result carries
/// test-alpha's OWN banner (read from its persona.json, never a literal); outside the window,
/// neither. This row was briefly <c>Skip("until #113")</c> in an earlier revision of this PR
/// (tools.py hardcoded the default pack's banner literal for every session); #113 (Beth, PR
/// #115, branch squad/113-pack-happy-hour) has since merged into dev -- <c>order_state.py</c>
/// now resolves each session's own bound-pack banner via
/// <c>get_happy_hour_banner_for_session</c> -- so this PR merges origin/dev and un-skips the
/// row here, exactly as #115's own coordination note with this PR said it would once it
/// landed. "Alpha Burger" (never
/// opted in) and test-beta (persona-level flag null) remain the
/// negative proofs: the persona-level <c>pricing.happyHour</c> flag must never blanket-discount
/// an item that didn't individually opt in, a persona with no happy-hour config at all must be
/// completely clock-invariant, and (Rick's PR #108 review required item 3) neither of those
/// negative cases may ever surface a happy-hour banner either. All four facts add the SAME item
/// as the SAME persona at two FixedClock instants -- one inside the window
/// (<see cref="TwoPersonaHappyHourWindowFixture"/>, golden case 1 = 14:00:00) and one just
/// outside it (<see cref="TwoPersonaHappyHourOutsideWindowFixture"/>, golden case 0 = 13:59:59).
/// A mutation that started applying the discount/banner without checking the item's own opt-in
/// flag, or that let one persona's happy-hour config (or banner) bleed into another persona's
/// session, would make one of the facts below fail.
/// </summary>
[Collection(TwoPersonaHappyHourWindowCollection.Name)]
public sealed class PersonaHappyHourConformanceTests(
    TwoPersonaHappyHourWindowFixture windowFixture,
    TwoPersonaHappyHourOutsideWindowFixture outsideWindowFixture)
    : IClassFixture<TwoPersonaHappyHourOutsideWindowFixture>
{
    [Fact]
    [Trait("Dotnet", "ready")]
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

    // Rick's PR #108 review, required item 2: "Alpha Cola" is the ONE item in the test-alpha
    // fixture pack marked happyHourDiscounted:true. Inside test-alpha's own window it must be
    // discounted by test-alpha's OWN priceMultiplier (0.5, not Sonic's, even though the two
    // happen to share the same numeric value -- see the decision note on why the mutation
    // evidence for this fact perturbs Sonic's own multiplier rather than relying on that
    // coincidence) and the update_order result must carry test-alpha's OWN banner, read from its
    // persona.json rather than typed here; outside the window, neither.
    //
    // Previously SKIPPED until #113: tools.py's update_order/get_order hardcoded the default
    // pack's literal banner text for every session regardless of its bound persona. #113 (Beth,
    // PR #115, branch squad/113-pack-happy-hour) has since merged into dev -- order_state.py now
    // resolves each session's own bound-pack banner via get_happy_hour_banner_for_session -- so
    // this revision merges origin/dev and un-skips this row, per #113's own acceptance criteria
    // ("un-skip the test-alpha banner row that PR #108 adds as skipped") and #115's own
    // coordination note on this PR.
    [Fact]
    [Trait("Dotnet", "ready")]
    public async Task Test_alpha_item_with_its_own_happy_hour_opt_in_gets_test_alphas_own_multiplier_and_banner()
    {
        var ct = TestContext.Current.CancellationToken;
        var expectedBanner = PersonaHappyHourBanner.Read(
            RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot()), TwoPersonaConformanceFixture.PersonaA);
        const decimal unitPrice = 1.99m; // Small, app/backend/tests/fixtures/personas/test-alpha/menu/menuItems.json
        ToolCallResult insideResult = null!;
        ToolCallResult outsideResult = null!;

        await windowFixture.RunAsync(async () =>
        {
            insideResult = await PersonaHappyHourTestSupport.AddItemAndReadResultAsync(
                windowFixture, TwoPersonaConformanceFixture.PersonaA, "Alpha Cola", "small", unitPrice, ct);
        });
        await outsideWindowFixture.RunAsync(async () =>
        {
            outsideResult = await PersonaHappyHourTestSupport.AddItemAndReadResultAsync(
                outsideWindowFixture, TwoPersonaConformanceFixture.PersonaA, "Alpha Cola", "small", unitPrice, ct);
        });

        Assert.Contains(expectedBanner, insideResult.FunctionCallOutputText);
        Assert.Contains(
            "[HAPPY HOUR DISCOUNT APPLIED TO: Small Alpha Cola]",
            insideResult.FunctionCallOutputText);
        Assert.DoesNotContain(expectedBanner, outsideResult.FunctionCallOutputText);
        Assert.DoesNotContain("HAPPY HOUR", outsideResult.FunctionCallOutputText);

        // test-alpha's taxRate is 0.05 (app/backend/tests/fixtures/personas/test-alpha/persona.json).
        OrderScenarioHelpers.AssertMoneyEqual(
            unitPrice * 0.5m * 1.05m,
            OrderScenarioHelpers.GetOrderFinalTotal(insideResult.ToolResultJson!),
            "Alpha Cola opted in via happyHourDiscounted:true -- inside test-alpha's own window it " +
            "must be discounted by test-alpha's own priceMultiplier (0.5).");
        OrderScenarioHelpers.AssertMoneyEqual(
            unitPrice * 1.05m,
            OrderScenarioHelpers.GetOrderFinalTotal(outsideResult.ToolResultJson!));
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public async Task Test_beta_persona_with_no_happy_hour_config_is_never_discounted_regardless_of_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        ToolCallResult insideResult = null!;
        ToolCallResult outsideResult = null!;

        await windowFixture.RunAsync(async () =>
        {
            insideResult = await PersonaHappyHourTestSupport.AddItemAndReadResultAsync(
                windowFixture, TwoPersonaConformanceFixture.PersonaB, "Beta Double Burger", "regular", 4.49m, ct);
        });
        await outsideWindowFixture.RunAsync(async () =>
        {
            outsideResult = await PersonaHappyHourTestSupport.AddItemAndReadResultAsync(
                outsideWindowFixture, TwoPersonaConformanceFixture.PersonaB, "Beta Double Burger", "regular", 4.49m, ct);
        });

        OrderScenarioHelpers.AssertMoneyEqual(
            OrderScenarioHelpers.GetOrderFinalTotal(outsideResult.ToolResultJson!),
            OrderScenarioHelpers.GetOrderFinalTotal(insideResult.ToolResultJson!),
            "test-beta's pricing.happyHour is null -- the clock instant must never affect its totals.");

        // Rick's PR #108 review, required item 3: test-beta's happyHour is null, so no banner may
        // ever appear -- clock-invariant, same as the price.
        Assert.DoesNotContain("HAPPY HOUR", insideResult.FunctionCallOutputText);
        Assert.DoesNotContain("HAPPY HOUR", outsideResult.FunctionCallOutputText);
    }
}
