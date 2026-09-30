using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue 165: the Breakfast/Lunch menu-mode feature end to end, over the real wire protocol,
/// against <see cref="MenuModeConformanceFixture"/>'s test-delta (declares
/// <c>features.dayparts: true</c>, ships a breakfast-only "Delta Breakfast Meal" and a
/// lunch-only "Delta Lunch Meal" sharing meal number 2) and test-alpha (no dayparts feature at
/// all) packs. Covers every conformance row issue 165 itself calls out: mode switch, search
/// filtered by mode, out-of-mode item rejected, and packs without modes unaffected.
///
/// The session's menu mode is bound once at WebSocket connect time via the `?mode=` query
/// parameter (see <see cref="RealtimeBrowserClient.ConnectAsync"/>'s `mode` parameter and
/// app/backend/rtmt.py's `_websocket_handler`/app/backend-dotnet/src/Backend/Program.cs's
/// `/realtime` handler) -- there is no mid-session mode switch in either backend today, so each
/// row below opens its own connection with the mode it needs rather than trying to change mode
/// on an already-connected session.
/// </summary>
[Collection(MenuModeConformanceCollection.Name)]
public sealed class MenuModeConformanceTests(MenuModeConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Breakfast_meal_can_be_added_when_connected_in_breakfast_mode() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.DaypartsPersona, mode: "breakfast");
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Delta Breakfast Meal", "regular", 1, 4.99m)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(4.99m, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
            "A session bound to breakfast mode must accept the breakfast-only meal at its own menu price.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Lunch_meal_can_be_added_when_connected_in_lunch_mode() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.DaypartsPersona, mode: "lunch");
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Delta Lunch Meal", "regular", 1, 6.49m)],
            roundTripIndex, ct);

        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(6.49m, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
            "A session bound to lunch mode must accept the lunch-only meal at its own menu price, " +
            "even though it shares meal number 2 with the breakfast-only meal.");
    });

    /// <summary>
    /// The out-of-mode gate (tools.py's `item_out_of_mode` / OrderToolExecutor.CheckAddOrModifyGates)
    /// runs before the item is ever added: a lunch-bound session trying to add the
    /// breakfast-only meal is rejected with the same structured shape (status "rejected",
    /// item_added false, reason "item_out_of_mode") every other add-time gate uses, sent to the
    /// model only (TO_SERVER), and leaves the order untouched.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Breakfast_item_is_rejected_as_out_of_mode_when_connected_in_lunch_mode() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.DaypartsPersona, mode: "lunch");
        await using var _ = browser;

        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = "Delta Breakfast Meal",
                size = "regular",
                quantity = 1,
                price = 4.99m,
            }),
            "call_add_out_of_mode", roundTripIndex, ct, toClient: false);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText, expectedReason: "item_out_of_mode", expectedItemName: "Delta Breakfast Meal");

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_get_after_out_of_mode", rejected.RoundTripIndex, ct);
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
        OrderScenarioHelpers.AssertMoneyEqual(0m, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
            "A rejected out-of-mode add must leave the order exactly as it was.");
    });

    /// <summary>
    /// tools.py's `search()` / SearchTool.ExecuteAsync build an OData `filter` string
    /// (`menuPeriod eq '{mode}' or menuPeriod eq 'allDay'`) off the session's bound menu mode and
    /// send it on every search request -- proving the wire contract without needing
    /// FakeSearchServer to actually apply the filter server-side (its document set has no
    /// mode-restricted rows to filter in the first place; the real Azure AI Search index is what
    /// applies `filter`, not this fake).
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Search_request_filter_reflects_the_bound_session_menu_mode() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.DaypartsPersona, mode: "breakfast");
        await using var _ = browser;

        const string query = "issue165 menu mode search filter breakfast";
        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            $$"""{"query":"{{query}}"}""",
            "call_search_menu_mode", roundTripIndex, ct, toClient: false);

        var matchingRequest = fixture.Search.ReceivedRequests.Snapshot()
            .Single(f => f.Json.TryGetProperty("search", out var s) && s.GetString() == query);
        var filter = matchingRequest.Json.GetProperty("filter").GetString();
        Assert.Equal("menuPeriod eq 'breakfast' or menuPeriod eq 'allDay'", filter);
    });

    /// <summary>
    /// test-alpha declares no `features.dayparts` at all, so a stray `?mode=breakfast` on its
    /// connect URL must be silently ignored (never a 400, never bound as the session's menu
    /// mode): every item adds normally and search sends no `filter` field, exactly like a
    /// connection with no `mode` query param at all. This is the "packs without modes unaffected"
    /// row issue 165 calls out explicitly.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_pack_without_dayparts_ignores_a_stray_mode_query_param() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.NoDaypartsPersona, mode: "breakfast");
        await using var _ = browser;

        var addResult = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Alpha Burger", "small", 1, 3.99m)],
            roundTripIndex, ct);
        var order = JsonDocument.Parse(addResult.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());

        const string query = "issue165 menu mode unaffected pack search";
        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            $$"""{"query":"{{query}}"}""",
            "call_search_no_dayparts", addResult.RoundTripIndex, ct, toClient: false);

        var matchingRequest = fixture.Search.ReceivedRequests.Snapshot()
            .Single(f => f.Json.TryGetProperty("search", out var s) && s.GetString() == query);
        Assert.False(matchingRequest.Json.TryGetProperty("filter", out var unexpectedFilter),
            "A persona with no dayparts feature must never bind a menu mode, so search must " +
            "send no filter field at all -- not even an empty one.");
    });
}
