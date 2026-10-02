using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// C# port of a representative subset of app/backend/tests/test_bundle_and_extras_engine.py's
/// scenarios (#77) against the SAME "test-delta"/"test-beta" fixture packs Python's own tests use
/// (<see cref="DeltaFixture"/>) -- modify (resize/reprice/not_in_order), add-time
/// machine_unavailable, bundle.autoFill, and extras.splitCombinedNames' suggested_calls. Each
/// OrderToolExecutor here is a brand-new instance per test, closed over its own fresh OrderState,
/// mirroring one session's actor-confined lifetime (no promptLoader -- fallback message text is
/// asserted instead, which is deterministic and needs no prompts/ pack on disk).
/// </summary>
public sealed class OrderToolExecutorBundleAndExtrasTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    private static OrderToolExecutor NewExecutor(Persona persona, string? menuMode = null)
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        return new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems, menuMode: menuMode);
    }

    private static JsonElement Args(string action, string itemName, string size, int quantity, decimal? price = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["item_name"] = itemName,
            ["size"] = size,
            ["quantity"] = quantity,
        };
        if (price is { } p)
        {
            dict["price"] = p;
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    // ── bundle.autoFill ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_BundleWithNoPreexistingAbsorption_AutofillsEverySlot()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Meal", "regular", 1, 5.99m), TestContext.Current.CancellationToken);

        var summary = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        var client = JsonDocument.Parse(summary.ToClientText());
        var items = client.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        var components = items[0].GetProperty("components").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Delta Fries (Regular)", components);
        Assert.Contains("Delta Cola (Regular)", components);
    }

    [Fact]
    public async Task Add_BundleWithPreexistingStandaloneSide_AbsorbsRatherThanAutofillingThatSlot()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Fries", "regular", 1, 1.99m), TestContext.Current.CancellationToken);
        await executor.ExecuteAsync("update_order", Args("add", "Delta Meal", "regular", 1, 5.99m), TestContext.Current.CancellationToken);

        var summary = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        var client = JsonDocument.Parse(summary.ToClientText());
        var items = client.RootElement.GetProperty("items").EnumerateArray().ToList();

        // The standalone fries line is gone -- absorbed, not double-counted -- leaving only the
        // bundle line itself.
        Assert.Single(items);
        var components = items[0].GetProperty("components").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(components, c => c!.Contains("Delta Fries"));
        Assert.Contains("Delta Cola (Regular)", components);
    }

    // ── extras.splitCombinedNames -> suggested_calls ────────────────────────────────────────

    [Fact]
    public async Task Add_CombinedOffMenuName_OffersSuggestedCallsAndAddsNothing()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Latte with Extra Shot", "regular", 1, 4.24m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("not_on_menu", payload.RootElement.GetProperty("reason").GetString());
        var suggested = payload.RootElement.GetProperty("suggested_calls").EnumerateArray().ToList();
        Assert.Equal(2, suggested.Count);
        Assert.Equal("Delta Latte", suggested[0].GetProperty("item_name").GetString());
        Assert.Equal("Delta Extra Shot", suggested[1].GetProperty("item_name").GetString());

        var getOrder = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        using var client = JsonDocument.Parse(getOrder.ToClientText());
        Assert.Equal(0, client.RootElement.GetProperty("items").GetArrayLength());
    }

    // ── modify ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Modify_ResizesInPlaceAndReprices_IgnoringTheCallersOwnBogusPrice()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Latte", "regular", 1, 3.49m), TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Delta Latte", "large", 1, 999.99m), TestContext.Current.CancellationToken); // bogus, must be ignored

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        using var client = JsonDocument.Parse(result.ToClientText());
        var items = client.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("large", items[0].GetProperty("size").GetString());
        Assert.Equal(4.29m, items[0].GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Modify_OfAnItemNotInTheOrder_IsRejectedNotInOrder_AndLeavesOrderUnchanged()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Meal", "regular", 1, 5.99m), TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Delta Latte", "large", 1, 4.29m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("rejected", payload.RootElement.GetProperty("status").GetString());
        Assert.False(payload.RootElement.GetProperty("item_added").GetBoolean());
        Assert.Equal("not_in_order", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Delta Latte", payload.RootElement.GetProperty("item_name").GetString());
        Assert.Contains("nothing was changed", payload.RootElement.GetProperty("message").GetString());

        var getOrder = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        using var client = JsonDocument.Parse(getOrder.ToClientText());
        Assert.Equal(1, client.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Modify_OnAnEmptyOrder_IsRejectedAndAddsNothing()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync("update_order", Args("modify", "Delta Latte", "large", 1, 4.29m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("not_in_order", payload.RootElement.GetProperty("reason").GetString());
    }

    // ── add-time machine_unavailable ────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_OfACurrentlyDownMachineItem_IsRejectedMachineUnavailable()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync("update_order", Args("add", "Delta Shake", "regular", 1, 2.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        var keys = payload.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "status", "item_added", "reason", "item_name", "message" }, keys);
        Assert.Equal("rejected", payload.RootElement.GetProperty("status").GetString());
        Assert.False(payload.RootElement.GetProperty("item_added").GetBoolean());
        Assert.Equal("machine_unavailable", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Delta Shake", payload.RootElement.GetProperty("item_name").GetString());
        Assert.Contains("Delta machine is down", payload.RootElement.GetProperty("message").GetString());

        var getOrder = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        using var client = JsonDocument.Parse(getOrder.ToClientText());
        Assert.Equal(0, client.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Add_SameMachineKeyOperationalOnAnotherPersona_IsUnaffected()
    {
        // test-beta keys its fountain machine "soda_machine" too, but reports it operational --
        // proves the gate reads THIS persona's own live status, never a shared/cached one.
        var executor = NewExecutor(DeltaFixture.Load("test-beta"));
        var result = await executor.ExecuteAsync("update_order", Args("add", "Beta Root Beer", "regular", 1, 1.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        using var client = JsonDocument.Parse(result.ToClientText());
        Assert.Equal(1, client.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Remove_BypassesTheMachineGate()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync("update_order", Args("remove", "Delta Shake", "regular", 1), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
    }

    // ── modify delta-text wording ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Modify_ASizeChange_SpeaksTheOriginalAppsUpgradedWording()
    {
        // PR #184 round 3 (Rick's review, item C): a genuine size resize via `modify` must
        // speak the original app's exact wording/verb and from/to form -- "Upgraded {item}
        // from {Old} to {New}, your total is now $X" -- not the generic "Changed ..."
        // fallback, and with no em dash.
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Latte", "regular", 1, 3.49m), TestContext.Current.CancellationToken);
        var result = await executor.ExecuteAsync("update_order", Args("modify", "Delta Latte", "large", 1, 4.29m), TestContext.Current.CancellationToken);

        var text = result.ToText();
        Assert.StartsWith("Upgraded Delta Latte from Regular to Large, your total is now", text);
        Assert.DoesNotContain('\u2014', text);
    }

    [Fact]
    public async Task Modify_RejectedWholeBundleComponentResize_ReturnsStructuredRejectionNotSuccessText()
    {
        var executor = NewExecutor(DeltaFixture.Load("test-epsilon"));
        await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Standard Meal", "standard", 1, 3.49m),
            TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(
            "update_order", Args("modify", "Epsilon Fries", "large", 1, 0m),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("rejected", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("combo_component_resize_rejected", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Epsilon Fries", payload.RootElement.GetProperty("item_name").GetString());
        Assert.Contains("can't be resized by itself", payload.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("Changed", result.ToText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("standard")]
    public async Task Add_MissingOrStandardBundleSize_DefaultsToBundleDefaultSize(string requestedSize)
    {
        var executor = NewExecutor(DeltaFixture.Load("test-epsilon"));

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Snack Meal", requestedSize, 1, 0m),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        using var client = JsonDocument.Parse(result.ToClientText());
        var item = client.RootElement.GetProperty("items")[0];
        Assert.Equal("medium", item.GetProperty("size").GetString());
        Assert.Equal(5.99m, item.GetProperty("price").GetDecimal());
        Assert.Contains("Medium Epsilon Fries", item.GetProperty("display").GetString());
    }

    // ── extras gate: extras_no_base_item / extras_blocked_category ─────────────────────────
    // test-delta's own extras.allowedBaseCategories: ["drinks"], blockedBaseCategories: ["mains"].

    [Fact]
    public async Task Add_ExtraToAnEmptyOrder_IsRejectedNoBaseItem()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync("update_order", Args("add", "Delta Extra Shot", "regular", 1, 0.75m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("extras_no_base_item", payload.RootElement.GetProperty("reason").GetString());

        var getOrder = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        using var client = JsonDocument.Parse(getOrder.ToClientText());
        Assert.Equal(0, client.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Add_ExtraWithOnlyABlockedCategoryBaseInOrder_IsRejectedBlockedCategory()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Burger", "regular", 1, 3.99m), TestContext.Current.CancellationToken); // mains, blocked

        var result = await executor.ExecuteAsync("update_order", Args("add", "Delta Extra Shot", "regular", 1, 0.75m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("extras_blocked_category", payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Add_ExtraWithAnAllowedCategoryBaseInOrder_Succeeds()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        await executor.ExecuteAsync("update_order", Args("add", "Delta Latte", "regular", 1, 3.49m), TestContext.Current.CancellationToken); // drinks, allowed

        var result = await executor.ExecuteAsync("update_order", Args("add", "Delta Extra Shot", "regular", 1, 0.75m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        using var client = JsonDocument.Parse(result.ToClientText());
        Assert.Equal(2, client.RootElement.GetProperty("items").GetArrayLength());
    }

    // ── size_not_available ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_AnOnMenuItemInAnUnavailableSize_IsRejectedSizeNotAvailable()
    {
        // Delta Breakfast Meal only ever declares a "regular" size.
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Breakfast Meal", "large", 1, 4.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("size_not_available", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Delta Breakfast Meal", payload.RootElement.GetProperty("item_name").GetString());
        var availableSizes = payload.RootElement.GetProperty("available_sizes").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(["Regular"], availableSizes);
    }

    // ── not_on_menu (plain, no combined-name suggestion) ────────────────────────────────────

    [Fact]
    public async Task Add_AnItemNotOnTheMenuAtAll_IsRejectedNotOnMenuWithNoSuggestedCalls()
    {
        var executor = NewExecutor(DeltaFixture.Load());
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Nonexistent Thing", "regular", 1, 1.00m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("not_on_menu", payload.RootElement.GetProperty("reason").GetString());
        Assert.False(payload.RootElement.TryGetProperty("suggested_calls", out _));
    }

    // ── Issue 165: item_out_of_mode gate (MenuCatalog.ItemAvailableNow, add-only) ───────────

    [Fact]
    public async Task Add_BreakfastOnlyItem_WhenSessionHasNoBoundMenuMode_Succeeds()
    {
        // A persona with no features.dayparts (or a dayparts persona whose session somehow has
        // no bound mode) must never gate on menuPeriod at all -- MenuCatalog.ItemAvailableNow's
        // null-active-mode branch is a permanent no-op, mirroring menu_utils.py's
        // item_available_now(active_mode=None) always returning True.
        var executor = NewExecutor(DeltaFixture.Load(), menuMode: null);
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Breakfast Meal", "regular", 1, 4.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
    }

    [Fact]
    public async Task Add_BreakfastOnlyItem_WhenSessionBoundToBreakfast_Succeeds()
    {
        var executor = NewExecutor(DeltaFixture.Load(), menuMode: "breakfast");
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Breakfast Meal", "regular", 1, 4.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
    }

    [Fact]
    public async Task Add_BreakfastOnlyItem_WhenSessionBoundToLunch_IsRejectedOutOfMode()
    {
        var executor = NewExecutor(DeltaFixture.Load(), menuMode: "lunch");
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Breakfast Meal", "regular", 1, 4.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        using var payload = JsonDocument.Parse(result.ToText());
        Assert.Equal("item_out_of_mode", payload.RootElement.GetProperty("reason").GetString());
        Assert.False(payload.RootElement.GetProperty("item_added").GetBoolean());
        Assert.Equal("Delta Breakfast Meal", payload.RootElement.GetProperty("item_name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("message").GetString()));

        var summary = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        var client = JsonDocument.Parse(summary.ToClientText());
        Assert.Equal(0, client.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Add_ItemWithNoMenuPeriod_IsAlwaysAvailableRegardlessOfBoundMenuMode()
    {
        // "Delta Burger" declares no menuPeriod -- an allDay item -- so it must add cleanly
        // whichever daypart the session happens to be bound to.
        var executor = NewExecutor(DeltaFixture.Load(), menuMode: "breakfast");
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Delta Burger", "regular", 1, 3.99m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
    }

    [Fact]
    public async Task Modify_OnAnAlreadyAddedOutOfModeItem_IsNotGatedByMenuMode()
    {
        // The item_out_of_mode gate only runs for action=="add" -- a "modify" on an item the
        // session added earlier (necessarily while it was still in-mode) must never be re-gated
        // just because two OrderToolExecutor instances over the same OrderState happen to be
        // constructed with different bound modes. Mirrors tools.py's update_order, which only
        // checks item_available_now on the add branch.
        var menu = PersonaOrderFactory.GetMenuCatalog(DeltaFixture.Load());
        var order = PersonaOrderFactory.CreateOrderState(DeltaFixture.Load());
        var breakfastExecutor = new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems, menuMode: "breakfast");
        await breakfastExecutor.ExecuteAsync(
            "update_order", Args("add", "Delta Breakfast Meal", "regular", 1, 4.99m), TestContext.Current.CancellationToken);

        var lunchExecutor = new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems, menuMode: "lunch");
        var result = await lunchExecutor.ExecuteAsync(
            "update_order", Args("modify", "Delta Breakfast Meal", "regular", 1, 4.99m), TestContext.Current.CancellationToken);

        // The point of this test is that the modify reaches OrderState at all (ToBoth, not
        // ToServer-rejected item_out_of_mode) -- the item_out_of_mode gate must never fire for a
        // "modify" action, regardless of which mode the executor instance handling it was built
        // with. The resulting resize/requantify behavior itself is covered by #77's own tests.
        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        using var client = JsonDocument.Parse(result.ToClientText());
        var items = client.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Delta Breakfast Meal", items[0].GetProperty("item").GetString());
    }
}
