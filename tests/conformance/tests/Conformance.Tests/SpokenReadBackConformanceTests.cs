using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #304: proves the server-composed, mandatory <c>spokenReadBack</c> field actually reaches
/// the wire on a <c>get_order</c> tool result -- every line's quantity/size/spoken name, then
/// "Your total is {finalTotalDisplay}." -- the same contract app/backend/order_state.py's
/// <c>_compose_spoken_readback</c> and Backend.Ordering.OrderState's <c>ComposeSpokenReadBack</c>
/// both compute and cache onto <c>OrderSummary</c> once per update, so this tool response and the
/// cached field can never drift apart on either backend.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SpokenReadBackConformanceTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = OrderScenarioHelpers.FrameTimeout;

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Get_order_result_carries_the_server_composed_spoken_read_back() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // "Tots" is a real, single-size-ambiguous menu item on the default persona, already used by
        // UpdateOrderToolCallTests for the identical reason: on-menu, no combo/bundle
        // complications, so the read-back's grouping logic has exactly one line to describe.
        var addResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"small","quantity":2,"price":2.19}""",
            "call_spoken_readback_add", roundTripIndex, ct);

        var getOrderResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_spoken_readback_get", addResult.RoundTripIndex, ct);

        Assert.NotNull(getOrderResult.ToolResultJson);
        using var summary = JsonDocument.Parse(getOrderResult.ToolResultJson!);
        Assert.True(summary.RootElement.TryGetProperty("spokenReadBack", out var spokenReadBack),
            "Expected get_order's tool_result to carry a spokenReadBack field (issue #304).");

        var spokenReadBackText = spokenReadBack.GetString();
        Assert.False(string.IsNullOrWhiteSpace(spokenReadBackText),
            "spokenReadBack must never be empty once the order has at least one item.");
        Assert.Contains("2 ", spokenReadBackText);
        Assert.Contains("Tots", spokenReadBackText);
        Assert.Contains($"Your total is {OrderScenarioHelpers.GetOrderFinalTotalDisplay(getOrderResult.ToolResultJson!)}.", spokenReadBackText);
    });
}
