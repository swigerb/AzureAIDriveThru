using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// #325: `update_order` must store the MENU's own canonical spelling for an add/modify/remove --
/// never whatever casing, trademark marks, or paren groups the model's own tool-call `item_name`
/// happened to use for that turn -- so two different spellings of the same item merge into one
/// order line instead of silently duplicating it. Runs against <see cref="ZetaConformanceFixture"/>'s
/// "test-zeta" pack (the exact fixture/item #325's own write-up names: "ZORBS&#174; Bite Treats",
/// plus "Zeta Snack Mix (Family Size)" added for the B2 paren-duplication regression, Rick's PR
/// #326 review), the same end-to-end real-backend harness as
/// <see cref="UpdateOrderAddRemoveModifyTests"/>, so this proof is independent of any one real
/// pack's own marked item names.
/// </summary>
[Collection(ZetaConformanceCollection.Name)]
public sealed class CanonicalItemNameConformanceTests(ZetaConformanceFixture fixture)
{
    private const string MarkedName = "ZORBS\u00ae Bite Treats";
    private const string UnmarkedName = "ZORBS Bite Treats";
    private const decimal Price = 3.99m;

    private const string ParenName = "Zeta Snack Mix (Family Size)";
    private const string ParenLessName = "Zeta Snack Mix";
    private const decimal ParenItemPrice = 3.49m;

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Add_with_the_unmarked_spelling_stores_the_menus_canonical_marked_name() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", UnmarkedName, "10 count", 1, Price)],
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
                ("add", MarkedName, "10 count", 1, Price),
                ("add", UnmarkedName, "10 count", 1, Price),
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
                ("add", MarkedName, "10 count", 1, Price),
                ("remove", UnmarkedName, "10 count", 1, Price),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });

    // ── B2 (Rick's PR #326 review): a canonical name that ITSELF contains a paren group ──

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Add_with_the_exact_canonical_paren_name_stores_it_unchanged_not_duplicated() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", ParenName, "regular", 1, ParenItemPrice)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(ParenName, items[0].GetProperty("item").GetString());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Add_with_the_paren_less_spelling_merges_into_the_same_line() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", ParenName, "regular", 1, ParenItemPrice),
                ("add", ParenLessName, "regular", 1, ParenItemPrice),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(ParenName, items[0].GetProperty("item").GetString());
    });
}

/// <summary>
/// #325 (B3, Rick's PR #326 review): a `modify` row -- resizing "Coke" (a real alias on the
/// default "sonic" persona's own menu data) must match the "Coca-Cola&#174;" line it was added as
/// and keep storing the canonical name, not revert the ticket back to the alias, even when the
/// size also changes in the same call. Runs against the deployment-default persona (no
/// <c>Persona</c> override), same precedent/collection as <see cref="UpdateOrderAddRemoveModifyTests"/>.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class CanonicalItemNameModifyConformanceTests(ConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Modify_with_an_alias_and_a_size_change_resolves_and_stores_the_canonical_name() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Coke", "medium", 1, 2.49m),
                ("modify", "Coke", "large", 1, 2.99m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Coca-Cola\u00ae", items[0].GetProperty("item").GetString());
    });
}
