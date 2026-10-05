using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// #325: `update_order` must store the MENU's own canonical spelling for an add/modify/remove --
/// never whatever casing or trademark marks the model's own tool-call `item_name` happened to use
/// for that turn -- so two different spellings of the same item merge into one order line instead
/// of silently duplicating it. Runs against <see cref="ZetaConformanceFixture"/>'s "test-zeta" pack
/// (the exact fixture/item #325's own write-up names: "ZORBS&#174; Bite Treats"), the same
/// end-to-end real-backend harness as <see cref="UpdateOrderAddRemoveModifyTests"/>, so this proof
/// is independent of any one real pack's own marked item names.
/// </summary>
[Collection(ZetaConformanceCollection.Name)]
public sealed class CanonicalItemNameConformanceTests(ZetaConformanceFixture fixture)
{
    private const string MarkedName = "ZORBS\u00ae Bite Treats";
    private const string UnmarkedName = "ZORBS Bite Treats";
    private const decimal Price = 2.99m;

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Add_with_the_unmarked_spelling_stores_the_menus_canonical_marked_name() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", UnmarkedName, "regular", 1, Price)],
            roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(MarkedName, items[0].GetProperty("item").GetString());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_the_same_item_with_different_spellings_merges_into_one_line() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", MarkedName, "regular", 1, Price),
                ("add", UnmarkedName, "regular", 1, Price),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(MarkedName, items[0].GetProperty("item").GetString());
        OrderScenarioHelpers.AssertMoneyEqual(2 * Price, order.GetProperty("total").GetDecimal());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Remove_with_the_unmarked_spelling_matches_a_line_added_with_the_marked_spelling() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", MarkedName, "regular", 1, Price),
                ("remove", UnmarkedName, "regular", 1, Price),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });
}
