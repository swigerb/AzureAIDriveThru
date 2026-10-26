using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 12: proves the FixedClock backend profile actually freezes
/// app/backend/order_state.py's notion of "now" for the Python process it launches. Scripts an
/// `update_order` "add" call for a drink-keyword item while the clock is frozen inside the
/// 14:00-16:00 happy-hour window, and asserts the resulting order summary's finalTotal reflects
/// the 50% happy-hour discount plus tax -- deterministic regardless of what day/hour the suite
/// actually runs on.
/// </summary>
[Collection(FixedClockConformanceCollection.Name)]
public sealed class HappyHourPricingTests(FixedClockConformanceFixture fixture)
{
    private static readonly TimeSpan s_frameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Happy_hour_discount_applies_at_the_frozen_clock_instant() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(s_frameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        Task<FakeRealtimeConnection?> connectionTask = fixture.Realtime.WaitForNextConnectionAsync(s_frameTimeout, ct);
        await using RealtimeBrowserClient browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        FakeRealtimeConnection? connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {s_frameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        RecordedFrame? greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", s_frameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        const string CallId = "call_update_order_happy_hour";
        // #104: update_order now prices from the resolved menu record, not the caller-supplied
        // tool price, so this must be the real menu price (personas/sonic/menu/menuItems.json,
        // "Cherry Limeade" medium) rather than an arbitrary tool-call constant.
        const double Price = 2.89;
        connection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                // #73 (ADR-001 decision 4 "No off-menu"): "Cherry Limeade" is a real drink on
                // personas/sonic/menu/menuItems.json, so update_order's on-menu gate resolves it
                // directly (no keyword-category-inference fallback needed or available anymore).
                // Size must be one of Cherry Limeade's real sizes (mini/small/medium/large/route 44)
                // or the #73 size gate rejects it as size_not_available -- "Regular" was never a
                // real size for this item and is replaced with "medium" here.
                ArgumentsJson: $$"""{"action":"add","item_name":"Cherry Limeade","size":"medium","quantity":1,"price":{{Price}}}""",
                CallId: CallId),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        RecordedFrame? functionCallOutput = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out JsonElement item) &&
                 item.TryGetProperty("type", out JsonElement itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out JsonElement respondedCallId) &&
                 respondedCallId.GetString() == CallId,
            s_frameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={CallId} upstream within {s_frameTimeout}.");

        RecordedFrame? toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out JsonElement toolName) &&
                 toolName.GetString() == "update_order",
            s_frameTimeout, ct);
        Assert.True(toolResponse is not null,
            $"Expected extension.middle_tier_tool_response for update_order on the browser within {s_frameTimeout}.");

        // tool_result is the order summary's JSON-serialised OrderSummary (see
        // app/backend/tools.py's update_order -> client_text=json_order_summary). Parsed as
        // decimal per the money contract (golden-order-pricing.json's own `description` /
        // README's "Money contract" section) -- never GetDouble(), which would reintroduce the
        // double-arithmetic rounding this suite's decimal contract exists to catch.
        string? toolResultJson = toolResponse!.Json.GetProperty("tool_result").GetString();
        Assert.NotNull(toolResultJson);
        decimal finalTotal = OrderScenarioHelpers.GetOrderFinalTotal(toolResultJson!);

        // price(2.89) * qty(1) * happy_hour_discount(0.5) = 1.445 subtotal; tax = 1.445 * tax_rate
        // (0.08) = 0.1156; finalTotal = 1.5606. See app/backend/config.yaml's business_rules and
        // order_state.py's _update_summary. Exact decimal, no rounding at any step (must-fix #1).
        const decimal ExpectedFinalTotal = 1.5606m;
        OrderScenarioHelpers.AssertMoneyEqual(ExpectedFinalTotal, finalTotal);

        // The "backend logged no unhandled error during this scenario" invariant (PR #22 review
        // item N5) is now a fixture-wide, language-neutral check applied by
        // ConformanceFixture.RunAsync after every scenario -- no per-test assertion needed here.
    });

    /// <summary>
    /// Rick's PR #107 review, required item 2 (pre-discounted happy-hour acceptance row): the
    /// tool call sends a price that's already halved (1.45, as if the model itself had applied the
    /// happy-hour discount to Cherry Limeade medium's real menu price of 2.89) -- since the price
    /// argument is ignored entirely (#104), the backend must still resolve the real menu price
    /// (2.89) and apply the happy-hour discount to IT exactly once, landing on the same 1.445
    /// subtotal / 1.5606 finalTotal as
    /// <see cref="Happy_hour_discount_applies_at_the_frozen_clock_instant"/> above -- never
    /// double-discounting the already-discounted tool price down to 0.725 subtotal / 0.783
    /// finalTotal. Mirrors app/backend/tests/test_extras_rules.py's
    /// test_pre_applied_discount_tool_price_is_not_double_discounted.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Pre_discounted_tool_call_price_is_not_double_discounted_at_the_frozen_clock_instant() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(s_frameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        Task<FakeRealtimeConnection?> connectionTask = fixture.Realtime.WaitForNextConnectionAsync(s_frameTimeout, ct);
        await using RealtimeBrowserClient browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        FakeRealtimeConnection? connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {s_frameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        RecordedFrame? greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", s_frameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        const string CallId = "call_update_order_happy_hour_pre_discounted";
        // Deliberately the already-halved price (2.89 * 0.5 = 1.445, rounded here to 1.45 as a
        // model might send it) -- the point of this test is that this value is ignored entirely.
        const double PreDiscountedPrice = 1.45;
        connection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: $$"""{"action":"add","item_name":"Cherry Limeade","size":"medium","quantity":1,"price":{{PreDiscountedPrice}}}""",
                CallId: CallId),
            new DoneEvent(),
        ]));
        await browser.SendResponseCreateAsync(ct);

        RecordedFrame? functionCallOutput = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out JsonElement item) &&
                 item.TryGetProperty("type", out JsonElement itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out JsonElement respondedCallId) &&
                 respondedCallId.GetString() == CallId,
            s_frameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={CallId} upstream within {s_frameTimeout}.");

        RecordedFrame? toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out JsonElement toolName) &&
                 toolName.GetString() == "update_order",
            s_frameTimeout, ct);
        Assert.True(toolResponse is not null,
            $"Expected extension.middle_tier_tool_response for update_order on the browser within {s_frameTimeout}.");

        string? toolResultJson = toolResponse!.Json.GetProperty("tool_result").GetString();
        Assert.NotNull(toolResultJson);
        decimal finalTotal = OrderScenarioHelpers.GetOrderFinalTotal(toolResultJson!);

        // menu_price(2.89) * qty(1) * happy_hour_discount(0.5) = 1.445 subtotal; tax = 1.445 *
        // tax_rate (0.08) = 0.1156; finalTotal = 1.5606 -- identical to the real-menu-price test
        // above, proving the ignored tool-call price (already pre-discounted or not) never changes
        // the result.
        const decimal ExpectedFinalTotal = 1.5606m;
        OrderScenarioHelpers.AssertMoneyEqual(ExpectedFinalTotal, finalTotal,
            "A pre-discounted tool-call price (1.45) must not be discounted again -- the resolved " +
            "menu price (2.89) must be discounted exactly once, landing on the same 1.5606 " +
            "finalTotal as an undiscounted tool-call price, never 0.783 (double-discounted).");
    });
}
