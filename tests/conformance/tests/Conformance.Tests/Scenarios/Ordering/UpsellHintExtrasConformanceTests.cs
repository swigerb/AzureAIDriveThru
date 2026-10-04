using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// #168 follow-up (Rick's PR #217 review): the default persona's own "Extras &amp; Sides" category
/// holds BOTH genuine stand-alone sides (Cheese Tots) AND modifier-only extras (Flavor Add-In, Add
/// Bacon, Whipped Topping, Sweet Cream, Jalapeños) that are added as their own order line to
/// accompany/flavor an existing item, not chosen as a side themselves. #168's hints.yaml fix
/// correctly added "extras & sides" to the side bucket's own trigger_categories so a genuine side
/// gets the dedicated "...a refreshing Drink or Slush to complete their meal!" hint, but that
/// change also made an extra wrongly inherit the same hint -- e.g. right after the drink hint had
/// already suggested the very same thing for the drink the extra is flavoring. Pinned to a
/// FixedClock just before the happy-hour window opens (same precedent as ComboAbsorptionTests)
/// since one step here adds a drink and happy-hour banner text is otherwise appended to the same
/// model-facing string these tests assert on.
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class UpsellHintExtrasConformanceTests(HappyHourJustBeforeOpenFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_an_extra_right_after_a_drink_does_not_trigger_the_side_completion_hint() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Cherry Limeade", "medium", 1, 2.89m),
                ("add", "Flavor Add-In", "standard", 1, 0.3m),
            ],
            roundTripIndex, ct);

        Assert.DoesNotContain("complete their meal", result.FunctionCallOutputText);
        Assert.Contains("add anything else", result.FunctionCallOutputText);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_real_side_sharing_the_extras_category_still_triggers_the_side_completion_hint() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Cheese Tots", "medium", 1, 3.39m)],
            roundTripIndex, ct);

        Assert.Contains("complete their meal", result.FunctionCallOutputText);
    });
}
