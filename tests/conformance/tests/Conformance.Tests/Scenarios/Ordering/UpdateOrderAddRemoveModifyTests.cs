using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #9: `update_order` add/remove/modify scenarios — quantities, sizes (including Route 44),
/// tool-call price is ignored in favor of the resolved menu price (#104), and both the per-item
/// and whole-order quantity limits. Scripts real scripted-function-call turns against the real
/// Python backend (see OrderScenarioHelpers.cs) and asserts on the `tool_result` JSON order
/// summary (extension.middle_tier_tool_response) that reaches the browser, exactly as
/// app/backend/tests/test_order_state.py and test_tool_calling.py assert against the in-process
/// order state directly.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class UpdateOrderAddRemoveModifyTests(ConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Add_single_item_creates_one_line_with_correct_total() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", 1, 2.79m)],
            roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(2.79m, order.GetProperty("total").GetDecimal());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_the_same_item_and_size_twice_merges_into_one_line_with_summed_quantity() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Non-drink item deliberately: this class runs under the ambient ConformanceCollection
        // (real wall-clock time, see BackendProfiles.Default), so a drink item's price would be
        // non-deterministic depending on whether the suite happens to run during the real
        // 14:00-16:00 happy-hour window. Matches the same precedent as UpdateOrderToolCallTests.cs.
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 1, 2.79m),
                ("add", "Tots", "medium", 2, 2.79m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        OrderScenarioHelpers.AssertMoneyEqual(3 * 2.79m, order.GetProperty("total").GetDecimal());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Removing_part_of_a_quantity_decrements_the_line_instead_of_deleting_it() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 3, 2.79m),
                ("remove", "Tots", "medium", 1, 2.79m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Removing_the_full_quantity_clears_the_line_entirely() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", 1, 2.79m),
                ("remove", "Tots", "medium", 1, 2.79m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(0m, order.GetProperty("total").GetDecimal());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Removing_an_item_that_was_never_added_is_a_safe_no_op() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("remove", "Onion Rings", "medium", 1, 3.89m)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });

    /// <summary>
    /// #77 (rick-2's PR #116 review): a `modify` for an on-menu item that isn't in the order
    /// must come back as the structured rejection (status "rejected", item_added false, reason
    /// "not_in_order", the menu's item name, a non-empty message from the pack's
    /// `item_not_in_order` error message), sent to the model only, and must leave the order
    /// untouched. Before this, the backend logged a no-op but still told the model the item was
    /// "Changed". A mutation that removes the check in tools.py fails this row.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Modifying_an_item_that_is_not_in_the_order_is_rejected_and_changes_nothing() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "modify",
                item_name = "Tots",
                size = "medium",
                quantity = 1,
                price = 2.79m,
            }),
            "call_modify_missing", roundTripIndex, ct, toClient: false);
        Assert.Null(rejected.ToolResultJson);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText, expectedReason: "not_in_order", expectedItemName: "Tots");
        using (var doc = JsonDocument.Parse(rejected.FunctionCallOutputText))
        {
            var message = doc.RootElement.GetProperty("message").GetString()!;
            Assert.Contains("Tots", message);
            Assert.DoesNotContain("An error occurred", message);
        }

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_modify_missing", rejected.RoundTripIndex, ct);
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
        OrderScenarioHelpers.AssertMoneyEqual(
            0m,
            OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
            "A rejected modify must leave the order exactly as it was.");
    });

    /// <summary>
    /// #104 acceptance criterion (Rick's issue #104 constraints): the unit price charged always
    /// comes from the resolved menu record for (item, size), never the tool call's own `price`
    /// argument -- the price parameter is accepted (kept in the tool schema so a caller can still
    /// send one) but ignored, only ever logged if it disagrees with the menu. Covers a wrong price
    /// that undercharges (0.0, previously rejected outright pre-#104), a negative price, a price
    /// that overcharges, and a JSON `null` price -- all four must still add the item and charge
    /// exactly the real menu price (Tots, medium = 2.79). `price` is passed as a raw JSON fragment
    /// string (not a `double` InlineData interpolated with `{{}}`) so `"null"` can be expressed
    /// directly and no culture-sensitive `double`-to-string formatting is in play. Rick's PR #107
    /// review, required item 1: a `null` tool price must never crash the add (guarded in
    /// order_state.py -- see `Adding_an_item_with_a_null_or_omitted_tool_call_price_is_charged_the_menu_price`
    /// below for the omitted-argument case, which can't be a `[Theory]` row since "omitted" means
    /// no JSON key at all, not a value). A mutation that reverts to trusting the tool-call price
    /// (e.g. re-introducing tools.py's old price&lt;=0 rejection, or reading `price` straight
    /// through in order_state.py's add branch) fails this by either rejecting the add outright,
    /// crashing on the `null` case, or charging the wrong amount.
    /// </summary>
    [Theory]
    [Trait("Dotnet", "ready")]
    [InlineData("0.0")]
    [InlineData("-5.00")]
    [InlineData("999.99")]
    [InlineData("null")]
    [InlineData("\"cheap\"")]
    [InlineData("true")]
    public Task Adding_an_item_with_a_wrong_tool_call_price_is_charged_the_menu_price(string toolCallPriceJson) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            $$"""{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":{{toolCallPriceJson}}}""",
            "call_wrong_tool_price", roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(2.79m, order.GetProperty("total").GetDecimal(),
            $"A wrong tool-call price ({toolCallPriceJson}) must be ignored and 'Tots' (medium) charged " +
            "its real menu price, 2.79 -- not the tool-call price, not rejected outright, and not a crash.");
    });

    /// <summary>
    /// Rick's PR #107 review, required item 1 (the omitted-argument twin of the `[Theory]` above):
    /// `update_order` with the `price` key left out of the JSON entirely. `tools.py` defaults a
    /// missing `price` to `0.0` via `args.get("price", 0.0)`, which is already numeric and never
    /// crashed even before the null/non-numeric guard, but it must still be ignored the same as
    /// every other tool-call price and the real menu price charged.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_an_item_with_an_omitted_tool_call_price_is_charged_the_menu_price() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1}""",
            "call_omitted_tool_price", roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(2.79m, order.GetProperty("total").GetDecimal(),
            "An omitted tool-call price must default harmlessly and 'Tots' (medium) still be " +
            "charged its real menu price, 2.79.");
    });

    /// <summary>
    /// Rick's PR #107 review, required item 2 (wrong-size carry-over acceptance row): adding a
    /// Tots medium (menu price 2.79), removing it, then re-adding it as a large but with the
    /// stale medium price (2.79) still on the tool call -- the resolved size is `large`, so the
    /// charge must be the large menu price (3.49), never the carried-over medium price. Mirrors
    /// app/backend/tests/test_tool_calling.py's
    /// test_resize_wrong_size_price_carryover_charges_new_size_menu_price. Non-drink item
    /// deliberately (originally used Cherry Limeade, which is `happyHourDiscounted:true` and made
    /// this test's total non-deterministic depending on the real 14:00-16:00 happy-hour window --
    /// same precedent as the "non-drink item deliberately" comment on
    /// Adding_the_same_item_and_size_twice_merges_into_one_line_with_summed_quantity above).
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_the_wrong_size_with_a_stale_price_carried_over_is_charged_the_new_size_menu_price() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":2.79}""",
            "call_add_medium", roundTripIndex, ct);
        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"remove","item_name":"Tots","size":"medium","quantity":1,"price":2.79}""",
            "call_remove_medium", roundTripIndex, ct);
        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"large","quantity":1,"price":2.79}""",
            "call_add_large_stale_price", roundTripIndex, ct);

        Assert.NotNull(result.ToolResultJson);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(3.49m, order.GetProperty("total").GetDecimal(),
            "A large re-add with the medium's stale tool-call price (2.79) carried over must be " +
            "charged the large's real menu price, 3.49 -- never the carried-over medium price.");
    });

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(Route44AliasCases))]
    public Task Route_44_size_aliases_all_display_as_Route_44(string sizeAlias) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var repoRoot = RepoPaths.FindRepoRoot();
        var golden = GoldenOrderPricingData.Load(repoRoot);
        Assert.Contains(sizeAlias, golden.Route44.Aliases);

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Cherry Limeade", sizeAlias, 1, 3.79m)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal($"{golden.Route44.ExpectedDisplayPrefix} Cherry Limeade", items[0].GetProperty("display").GetString());
    });

    /// <summary>Data-driven over the golden file's `route44.aliases` list (PR #38 review item 4,
    /// Rick's M5) rather than a hardcoded `[InlineData]` set, so the mixed/upper-case aliases
    /// (`RT44`, `Route 44`) added there specifically to prove case-insensitive matching are
    /// exercised without this test file needing to know about them explicitly.</summary>
    public static TheoryData<string> Route44AliasCases()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<string>();
        foreach (var alias in golden.Route44.Aliases)
        {
            data.Add(alias);
        }
        return data;
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_the_same_drink_with_two_different_Route_44_aliases_merges_into_one_line() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // "rt44" and "route 44" are both in golden.Route44.Aliases and both normalise to the same
        // canonical Route 44 size -- see #40's acceptance criteria.
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Cherry Limeade", "rt44", 1, 3.79m),
                ("add", "Cherry Limeade", "route 44", 1, 3.79m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal($"{golden.Route44.ExpectedDisplayPrefix} Cherry Limeade", items[0].GetProperty("display").GetString());
    });

    /// <summary>PR #50 review (second round, should-fix, kills Rick's Y2): "large" and "l"
    /// must canonicalize to the same size key, exactly like the Route 44 aliases above, so two
    /// adds spelled differently merge into one order line rather than silently creating two.
    /// #73 (ADR-001 decision 4 "No off-menu"): this Fact originally used the synthetic
    /// placeholder item "Latte" with the "Extra Large"/"xl" alias pair. Under #73 every item and
    /// size must resolve against the real menu, and no item on the Sonic menu offers an "xl" size
    /// at all (verified against personas/sonic/menu/menuItems.json), so that alias pair can no
    /// longer be exercised end-to-end against any real item. This now uses the real item "Tots"
    /// with the still-real "l"/"large" abbreviation alias pair to preserve the same
    /// merge-differently-spelled-aliases regression coverage. The extralarge&lt;-&gt;xl key
    /// equivalence itself remains covered directly against the pure function via
    /// canonical_size_key's own doctest in app/backend/menu_utils.py.</summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_the_same_drink_with_L_and_large_merges_into_one_line() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "l", 1, 3.49m),
                ("add", "Tots", "large", 1, 3.49m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("Large Tots", items[0].GetProperty("display").GetString());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Removing_a_Route_44_drink_with_a_different_alias_than_it_was_added_with_removes_it() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Cherry Limeade", "rt44", 1, 3.79m),
                ("remove", "Cherry Limeade", "44oz", 1, 3.79m),
            ],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());
    });

    /// <summary>PR #38 review item 6: wires up the previously-unenforced `sizeDisplayCases` golden
    /// table (ported from test_order_state.py/menu_utils.py size-display coverage — small/medium/
    /// large, mini, the three Route 44 aliases again, and the "no display" sizes: standard/n/a/na/
    /// none/empty/n.a.) against the live backend's `display` field, rather than leaving it dead
    /// data nothing in this suite reads.</summary>
    public static TheoryData<int> SizeDisplayCaseIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.SizeDisplayCases.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(SizeDisplayCaseIndexes))]
    public Task Size_aliases_and_hidden_sizes_display_correctly(int caseIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var sizeCase = golden.SizeDisplayCases[caseIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // update_order's price is caller-supplied and never menu-validated (see
        // order_state.py::_handle_order_update), so an arbitrary placeholder price is fine here —
        // this Theory is only proving the `display` field, not pricing.
        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", sizeCase.Item, sizeCase.Size, 1, 1.00m)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(sizeCase.ExpectedDisplay, items[0].GetProperty("display").GetString());
    });

    /// <summary>Rick's PR #100 review, required item 2: wires up the `sizeNotAvailableCases`
    /// golden table -- a real menu item ordered in a size it does not offer (Cherry Limeade in
    /// an XL variant it has never had; Tots in an unrecognised size word) -- against the live
    /// backend's structured `size_not_available` rejection (#73's on-menu size gate), never a
    /// silent absorb/round-to-nearest-real-size.</summary>
    public static TheoryData<int> SizeNotAvailableCaseIndexes()
    {
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.SizeNotAvailableCases.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(SizeNotAvailableCaseIndexes))]
    public Task Unavailable_size_is_rejected_with_the_items_real_available_sizes(int caseIndex) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var sizeCase = golden.SizeNotAvailableCases[caseIndex];

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = sizeCase.Item,
                size = sizeCase.Size,
                quantity = 1,
                price = 3.79m, // placeholder -- size_not_available is checked before price validation
            }),
            "call_reject_size", roundTripIndex, ct, toClient: false);
        Assert.Null(rejected.ToolResultJson);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText,
            expectedReason: "size_not_available",
            expectedItemName: sizeCase.ExpectedItemName,
            expectedAvailableSizes: sizeCase.ExpectedAvailableSizes);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_reject_size", rejected.RoundTripIndex, ct);
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Adding_up_to_the_per_item_quantity_limit_succeeds_but_one_more_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var max = golden.QuantityLimits.MaxItemQuantity;

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        var atLimit = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Tots", "medium", max, 2.79m)],
            roundTripIndex, ct);
        var orderAtLimit = JsonDocument.Parse(atLimit.ToolResultJson!).RootElement;
        Assert.Equal(max, orderAtLimit.GetProperty("items")[0].GetProperty("quantity").GetInt32());

        // One more of the same item+size pushes the per-item total over MAX_QUANTITY_PER_ITEM —
        // tools.py rejects the whole call (TO_SERVER-only apology), so quantity must stay unchanged.
        var overLimit = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1,"price":2.79}""",
            "call_over_item_limit", atLimit.RoundTripIndex, ct, toClient: false);
        Assert.Null(overLimit.ToolResultJson);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_item_limit", overLimit.RoundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        Assert.Equal(max, order.GetProperty("items")[0].GetProperty("quantity").GetInt32());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Exceeding_the_whole_order_item_limit_is_rejected_while_staying_at_the_cap() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var golden = GoldenOrderPricingData.Load(RepoPaths.FindRepoRoot());
        var maxTotal = golden.QuantityLimits.MaxOrderItems;
        var maxPerItem = golden.QuantityLimits.MaxItemQuantity;
        Assert.True(maxTotal > maxPerItem, "This scenario assumes the whole-order cap exceeds the per-item cap.");

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // Fill the order to exactly MAX_TOTAL_ITEMS using distinct item+size lines (so no single
        // line ever exceeds MAX_QUANTITY_PER_ITEM): maxPerItem + maxPerItem + remainder.
        var remainder = maxTotal - (2 * maxPerItem);
        Assert.True(remainder > 0 && remainder < maxPerItem, "Golden quantity limits changed shape; adjust this fill plan.");

        var filled = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [
                ("add", "Tots", "medium", maxPerItem, 2.79m),
                ("add", "Onion Rings", "medium", maxPerItem, 3.89m),
                ("add", "Groovy Fries", "medium", remainder, 2.79m),
            ],
            roundTripIndex, ct);
        var orderFilled = JsonDocument.Parse(filled.ToolResultJson!).RootElement;
        var totalQty = 0;
        foreach (var item in orderFilled.GetProperty("items").EnumerateArray())
        {
            totalQty += item.GetProperty("quantity").GetInt32();
        }
        Assert.Equal(maxTotal, totalQty);

        var overLimit = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Mozzarella Sticks","size":"medium","quantity":1,"price":4.29}""",
            "call_over_total_limit", filled.RoundTripIndex, ct, toClient: false);
        Assert.Null(overLimit.ToolResultJson);

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_total_limit", overLimit.RoundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        var finalQty = 0;
        foreach (var item in order.GetProperty("items").EnumerateArray())
        {
            finalQty += item.GetProperty("quantity").GetInt32();
        }
        Assert.Equal(maxTotal, finalQty);
        Assert.Equal(3, order.GetProperty("items").GetArrayLength());
    });
}
