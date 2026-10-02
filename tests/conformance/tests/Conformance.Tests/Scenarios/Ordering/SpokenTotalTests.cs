using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #38 second re-review should-fix 2: the spoken `$X.XX` text embedded in the
/// `function_call_output` (tools.py's `delta_text`, e.g. "Added 1 Tots — your total is now
/// $9.26") is a *display* concern, separate from the exact-decimal wire contract asserted
/// elsewhere in this suite. This still needs its own assertion because Rick's N2 mutation
/// (building that spoken text from the pre-tax subtotal instead of finalTotal) doesn't touch any
/// JSON money field and so is invisible to every other scenario in this file.
///
/// The display-rounding rule (README "Rendering money for display": round half away from zero on
/// the exact decimal) is distinct from the wire contract's "no rounding, ever" rule. Golden cases
/// whose finalTotal lands exactly on a half cent are mathematically impossible to assert against
/// the live Python backend today -- its `float:.2f` formatting reproduces no single consistent
/// rounding convention for such totals (Rick's 200k-order simulation found hundreds of
/// disagreements) -- so that specific case stays `Skip`'d referencing #46. The non-half-cent case
/// stays active and alone is sufficient to catch N2 today.
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class SpokenTotalTests(HappyHourJustBeforeOpenFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Spoken_total_text_matches_the_exact_final_total_non_half_cent() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        var spokenCase = golden.SpokenTotalCases.Single(c => c.Tag == "activeNonHalfCent");
        Assert.False(spokenCase.HappyHour, "This fixture pins the clock outside the happy-hour window.");

        var mode = OrderScenarioHelpers.MenuModeForItems(repoRoot, spokenCase.Steps.Select(s => s.Item));
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, mode: mode);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        // Confirms the wire finalTotal is what the golden case expects before checking the spoken
        // text derives from it -- otherwise a failure here could just as easily mean the golden
        // case itself drifted from menu prices, not that N2 regressed.
        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        // PR #50 review (should-fix 1, Rick's X1): assert the *Display strings directly too, on an
        // active (non-Skip'd) case, not only the decimal. #104: Cherry Limeade re-based to its
        // real medium price 2.89 (was an artificial 2.99), so subtotal is 2*2.89 + 2.79 = 8.57,
        // tax 8.57*0.08=0.6856.
        Assert.Equal("$8.57", OrderScenarioHelpers.GetOrderTotalDisplay(result.ToolResultJson!));
        Assert.Equal("$0.69", OrderScenarioHelpers.GetOrderTaxDisplay(result.ToolResultJson!));
        Assert.Equal("$9.26", OrderScenarioHelpers.GetOrderFinalTotalDisplay(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });

    /// <summary>Rick's N21 (#46): a whole-cent total whose cents happen to be a multiple of ten
    /// (e.g. $10.80) must still render both trailing decimal places, not truncate to "$10.8".</summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Spoken_total_with_trailing_zero_shows_two_decimal_places() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        var spokenCase = golden.SpokenTotalCases.Single(c => c.Tag == "trailingZero");
        Assert.False(spokenCase.HappyHour, "This fixture pins the clock outside the happy-hour window.");

        var mode = OrderScenarioHelpers.MenuModeForItems(repoRoot, spokenCase.Steps.Select(s => s.Item));
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, mode: mode);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });

    /// <summary>Rick's N21 (#46): a non-midpoint value whose thousandths digit forces a round-up
    /// (9.0396 -> $9.04), distinct from the exact-half-cent case above -- proves the fix isn't
    /// merely a ceiling that rounds every fractional cent up.</summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Spoken_total_rounds_up_a_non_midpoint_value() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        var spokenCase = golden.SpokenTotalCases.Single(c => c.Tag == "roundUpNonMidpoint");
        Assert.False(spokenCase.HappyHour, "This fixture pins the clock outside the happy-hour window.");

        var mode = OrderScenarioHelpers.MenuModeForItems(repoRoot, spokenCase.Steps.Select(s => s.Item));
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, mode: mode);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });

}

// Separate class/collection because this case needs happy hour ACTIVE (the half-cent finalTotal
// only arises from a happy-hour-halved odd-cent drink price -- see the golden entry's
// description), unlike the active non-half-cent case above.
[Collection(HappyHourAtOpenCollection.Name)]
public sealed class SpokenTotalHalfCentTests(HappyHourAtOpenFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Spoken_total_text_matches_the_exact_final_total_half_cent() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        var spokenCase = golden.SpokenTotalCases.Single(c => c.Tag == "halfCent");
        Assert.True(spokenCase.HappyHour, "This fixture pins the clock inside the happy-hour window.");

        var mode = OrderScenarioHelpers.MenuModeForItems(repoRoot, spokenCase.Steps.Select(s => s.Item));
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, mode: mode);
        await using var _ = browser;

        var steps = spokenCase.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price));
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, steps, roundTripIndex, ct);

        OrderScenarioHelpers.AssertMoneyEqual(
            spokenCase.ExpectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!));

        // PR #50 review (should-fix 1, Rick's X1): assert the three *Display strings on the
        // half-cent case explicitly. #104: re-based to real menu prices (Cherry Limeade Route 44
        // 3.79 halved to 1.895 for happy hour + Salted Caramel Toffee Croissant Bites 1.99 x2 =
        // 3.98) -- subtotal 5.875 -> $5.88, tax 0.47 (exact, no rounding needed), finalTotal 6.345
        // -> $6.35. This kills a ROUND_HALF_EVEN mutation: banker's rounding leaves totalDisplay
        // unchanged (587.5 cents rounds to the even 588, same as round-half-up here) but flips
        // finalTotalDisplay to "$6.34" (634.5 cents rounds to the even 634, not up to 635) --
        // invisible if only the raw JSON decimal (never rounded) is asserted.
        Assert.Equal("$5.88", OrderScenarioHelpers.GetOrderTotalDisplay(result.ToolResultJson!));
        Assert.Equal("$0.47", OrderScenarioHelpers.GetOrderTaxDisplay(result.ToolResultJson!));
        Assert.Equal("$6.35", OrderScenarioHelpers.GetOrderFinalTotalDisplay(result.ToolResultJson!));

        Assert.Contains(spokenCase.ExpectedSpokenTotalText, result.FunctionCallOutputText, StringComparison.Ordinal);
    });
}
