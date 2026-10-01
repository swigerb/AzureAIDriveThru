using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// C# port of app/backend/tests/test_combo_orders.py's TestComboComponentResize (#179, PR #184
/// round 2): proves the same live-bug sequence is fixed in this backend too -- `remove` of a
/// combo's drink no longer no-ops, a follow-up `add` of that same item at a new size resolves to
/// an in-place resize (never a standalone duplicate line), the explicit `modify` action prices
/// identically, and genuinely unrelated items/adds are left exactly as before.
///
/// <para>PR #184 round 2 (Rick's review, item 1): pricing is now a PURE function of the bundle's
/// own final state, not a stateful delta/"free reference" computation -- test-delta's own
/// <c>bundles.resizeRule</c> is the implicit "includedAnySize" default, so a side or drink is
/// included in the combo AT ANY SIZE: the combo's own price never changes no matter what size
/// fills its slots, in either direction (upsize or downsize), and ordering a size up front costs
/// exactly the same as resizing into it afterward -- see
/// <see cref="SameTotal_RegardlessOfOrderPath"/> for the general property this guarantees.</para>
///
/// Against the SAME "test-delta" fixture pack Python's own tests use (<see cref="DeltaFixture"/>):
/// "Delta Meal" is the bundle (sides+drinks slots, bundle.autoFill), "Delta Fries" the side
/// (regular only, $1.99), "Delta Latte" the multi-size comboSlot drink (regular $3.49 / large
/// $4.29 -- the fixture's only real, non-autoFill-only comboSlot drink, added for this test
/// class, see menuItems.json). test-delta's own pricing is 7% tax and NO happy hour
/// (persona.json), so assertions compare against <c>total</c> (pre-tax), never
/// <c>finalTotal</c>, to stay independent of that rate.
///
/// <para>Delta Fries and Delta Latte are always added BEFORE Delta Meal in these scenarios
/// (mirroring <c>Add_BundleWithPreexistingStandaloneSide_AbsorbsRatherThanAutofillingThatSlot</c>
/// above) so bundle-pivot absorption -- not <c>bundle.autoFill</c>'s synthetic filler text --
/// fills both slots with a REAL, priced order line each test can then resize. Adding the bundle
/// first would let autoFill claim any slot that's still open the instant the bundle lands,
/// leaving no open slot for a later standalone add to land in at all.</para>
///
/// <para>Assertions check the combo line's <c>display</c> string, not its <c>components</c>
/// array -- mirroring order_state.py's own test assertions (which check <c>combo_item.display</c>
/// too): <c>components</c> is only ever populated once, at the bundle's first-time pivot/autoFill
/// absorption (pre-existing, unchanged-by-#179 behavior in both backends), while <c>display</c> is
/// the field <c>_rebuild_bundle_display</c>/<c>RebuildBundleDisplay</c> actively keeps current on
/// every subsequent fill, resize, or vacate.</para>
/// </summary>
public sealed class ComboComponentResizeTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;
    private const decimal ComboPrice = 5.99m;
    private const decimal SidePrice = 1.99m;
    private const decimal LatteRegularPrice = 3.49m;
    private const decimal LatteLargePrice = 4.29m;

    private static OrderToolExecutor NewExecutor(out OrderState order)
    {
        var persona = DeltaFixture.Load();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        order = PersonaOrderFactory.CreateOrderState(persona);
        return new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems, menuMode: null);
    }

    private static JsonElement Args(string action, string itemName, string size, int quantity, decimal price) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["item_name"] = itemName,
            ["size"] = size,
            ["quantity"] = quantity,
            ["price"] = price,
        });

    private static async Task SeedComboWithSideAndRegularDrinkAsync(OrderToolExecutor executor, CancellationToken ct)
    {
        await executor.ExecuteAsync("update_order", Args("add", "Delta Fries", "regular", 1, SidePrice), ct);
        await executor.ExecuteAsync("update_order", Args("add", "Delta Latte", "regular", 1, LatteRegularPrice), ct);
        await executor.ExecuteAsync("update_order", Args("add", "Delta Meal", "regular", 1, ComboPrice), ct);
    }

    private static List<JsonElement> GetItems(JsonDocument doc) =>
        doc.RootElement.GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task RemoveThenAdd_ResizesDrinkInPlace_NoStandaloneDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out var order);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        // The exact #179 sequence: `remove Delta Latte Regular` must vacate the combo's drink
        // slot (not no-op), and the following `add Delta Latte Large` must refill that slot as a
        // resize, never a standalone duplicate, charging only the real upsize delta.
        Assert.True(order.IsAbsorbedComponent("Delta Latte")); // precondition: slot filled before remove
        var removeResult = await executor.ExecuteAsync(
            "update_order", Args("remove", "Delta Latte", "regular", 1, 0m), ct);
        Assert.Equal(ToolResultDirection.ToBoth, removeResult.Destination);
        // Asserts the slot's own bookkeeping was actually cleared -- not merely that the JSON
        // below happens to look right via some other, independent add-time resize path -- the
        // precise signal a mutation that disables HandleRemove's #179 vacate branch must flip.
        Assert.False(order.IsAbsorbedComponent("Delta Latte"));
        using (var removeClient = JsonDocument.Parse(removeResult.ToClientText()))
        {
            Assert.Single(GetItems(removeClient)); // still just the combo line; nothing orphaned
        }

        var addResult = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Latte", "large", 1, LatteLargePrice), ct);
        Assert.Equal(ToolResultDirection.ToBoth, addResult.Destination);

        using var client = JsonDocument.Parse(addResult.ToClientText());
        var items = GetItems(client);
        Assert.Single(items); // exactly one combo line, no standalone drink duplicate
        var comboItem = items[0];
        Assert.Equal("Delta Meal", comboItem.GetProperty("item").GetString());
        var display = comboItem.GetProperty("display").GetString();
        Assert.Contains("Large Delta Latte", display);
        Assert.DoesNotContain("Regular Delta Latte", display);

        // PR #184 round 2: pricing is a pure function of the combo's own final state -- a side
        // or drink is included at ANY size, so resizing the drink to Large never changes the
        // combo's own (always-base) price.
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task ExplicitModify_ResizesDrinkInPlace_IdenticalPricingToRemoveThenAdd()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Delta Latte", "large", 1, LatteLargePrice), ct);
        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);

        using var client = JsonDocument.Parse(result.ToClientText());
        var items = GetItems(client);
        Assert.Single(items);
        var display = items[0].GetProperty("display").GetString();
        Assert.Contains("Large Delta Latte", display);
        Assert.DoesNotContain("Regular Delta Latte", display);
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task AddSameItemDifferentSizeWhileSlotFull_ResizesNotDuplicates()
    {
        // An `add` of the SAME item at a DIFFERENT size while the slot is already full (no
        // `remove` call at all) must also resolve to an in-place resize -- the model doesn't
        // always call `remove` first.
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Latte", "large", 1, LatteLargePrice), ct);
        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);

        using var client = JsonDocument.Parse(result.ToClientText());
        var items = GetItems(client);
        Assert.Single(items);
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Downsize_IsAlsoFree_NoCreditBelowBasePrice()
    {
        // PR #184 round 2 (Rick's item 1): resizing DOWN must be just as free as resizing up --
        // NO credit below the combo's own base price in either direction. Establish the slot
        // filled at Large (absorbed via bundle-pivot, pre-existing before the combo lands), then
        // downsize to Regular: the total must stay exactly the combo's base price throughout.
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await executor.ExecuteAsync("update_order", Args("add", "Delta Latte", "large", 1, LatteLargePrice), ct);
        await executor.ExecuteAsync("update_order", Args("add", "Delta Meal", "regular", 1, ComboPrice), ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Delta Latte", "regular", 1, LatteRegularPrice), ct);
        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);

        using var client = JsonDocument.Parse(result.ToClientText());
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task SameTotal_RegardlessOfOrderPath()
    {
        // PR #184 round 2 (Rick's item 1, the headline property): ordering a size up front and
        // resizing into it afterward must total IDENTICALLY -- the live #179 bug was that these
        // two paths diverged (resize-in-place double-charged/under-charged relative to ordering
        // it that size from the start). Path A: order Large up front. Path B: order Regular, then
        // resize to Large via remove+add. Both must land on the exact same grand total.
        var ct = TestContext.Current.CancellationToken;

        var executorA = NewExecutor(out _);
        await executorA.ExecuteAsync("update_order", Args("add", "Delta Fries", "regular", 1, SidePrice), ct);
        await executorA.ExecuteAsync("update_order", Args("add", "Delta Latte", "large", 1, LatteLargePrice), ct);
        var resultA = await executorA.ExecuteAsync(
            "update_order", Args("add", "Delta Meal", "regular", 1, ComboPrice), ct);
        using var clientA = JsonDocument.Parse(resultA.ToClientText());
        var totalA = clientA.RootElement.GetProperty("total").GetDecimal();

        var executorB = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executorB, ct); // Delta Latte starts Regular
        await executorB.ExecuteAsync("update_order", Args("remove", "Delta Latte", "regular", 1, 0m), ct);
        var resultB = await executorB.ExecuteAsync(
            "update_order", Args("add", "Delta Latte", "large", 1, LatteLargePrice), ct);
        using var clientB = JsonDocument.Parse(resultB.ToClientText());
        var totalB = clientB.RootElement.GetProperty("total").GetDecimal();

        Assert.Equal(ComboPrice, totalA);
        Assert.Equal(totalA, totalB);
    }

    [Fact]
    public async Task DifferentItemWhileSlotFull_RemainsAStandaloneAdd()
    {
        // Swapping to a GENUINELY DIFFERENT item while the drink slot is already full is neither
        // a resize nor a fresh absorption -- it must fall through to a normal, full-price
        // standalone add (unchanged, pre-existing behavior), not silently overwrite the combo's
        // existing drink. "Delta Burger" (mains, no comboSlot/bundle/machine fields at all) is
        // the fixture's plain baseline item for exactly this kind of check.
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Burger", "regular", 1, 3.99m), ct);
        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);

        using var client = JsonDocument.Parse(result.ToClientText());
        var items = GetItems(client);
        Assert.Equal(2, items.Count);
        var comboItem = items.Single(i => i.GetProperty("item").GetString() == "Delta Meal");
        Assert.Contains("Regular Delta Latte", comboItem.GetProperty("display").GetString());
        var burger = items.Single(i => i.GetProperty("item").GetString() == "Delta Burger");
        Assert.Equal(3.99m, burger.GetProperty("price").GetDecimal());
        Assert.Equal(ComboPrice + 3.99m, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task ModifyRejectsAnItemThatIsNeitherARawLineNorAnAbsorbedComponent()
    {
        // `modify` on an item that's genuinely not in the order at all (not a raw line, not
        // absorbed into any combo slot) must stay a documented no-op rejection, same as before
        // #179 -- guards against the new absorbed-component branch becoming a false-positive
        // match for anything.
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Delta Burger", "regular", 1, 3.99m), ct);
        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("not_in_order", payload.RootElement.GetProperty("reason").GetString());

        var getOrder = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), ct);
        using var client = JsonDocument.Parse(getOrder.ToClientText());
        Assert.Single(GetItems(client));
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task RemoveOfAnItemThatIsNeitherARawLineNorAnAbsorbedComponent_IsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor(out _);
        await SeedComboWithSideAndRegularDrinkAsync(executor, ct);

        var result = await executor.ExecuteAsync(
            "update_order", Args("remove", "Delta Burger", "regular", 1, 0m), ct);

        using var client = JsonDocument.Parse(result.ToClientText());
        Assert.Single(GetItems(client));
        Assert.Equal(ComboPrice, client.RootElement.GetProperty("total").GetDecimal());
    }
}
