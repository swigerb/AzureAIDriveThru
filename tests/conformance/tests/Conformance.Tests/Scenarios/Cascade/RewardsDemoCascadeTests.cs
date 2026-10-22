using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class RewardsDemoCascadeTests(CascadeConformanceFixture fixture)
{
    [Fact]
    public Task Cascade_checkin_tool_order_redemption_and_completion_use_the_same_ledger() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: "tacobell");
        await using var browser = connection.Browser;
        var invalid = await RewardsDemoConformanceTests.Act(browser, "check_in", "123456", ct);
        Assert.False(invalid.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        await RewardsDemoConformanceTests.Act(browser, "check_in", "DEMO-PLUS", ct);

        fixture.Chat.EnqueueMessage(new JsonObject
        {
            ["role"] = "assistant", ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "cascade_demo_tacos", ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_order",
                    ["arguments"] = """{"action":"add","item_name":"Crunchy Taco","size":"standard","quantity":2}""",
                },
            }),
        });
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "Added two demo tacos." });
        fixture.Realtime.NextTranscript = "Two plain crunchy tacos.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);
        var added = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "extension.middle_tier_tool_response"
                && f.Json.GetProperty("tool_name").GetString() == "update_order",
            CascadeScenarioHelpers.FrameTimeout, ct);
        Assert.NotNull(added);
        Assert.Equal("$3.98", JsonDocument.Parse(added.Json.GetProperty("tool_result").GetString()!)
            .RootElement.GetProperty("totalDisplay").GetString());
        Assert.NotNull(await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > added.Sequence && f.Type == "response.done", CascadeScenarioHelpers.FrameTimeout, ct));

        var redeemed = await RewardsDemoConformanceTests.Act(browser, "redeem", null, ct);
        Assert.Equal("$2.15", redeemed.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal(19, redeemed.GetProperty("rewardsDemo").GetProperty("pointsToEarn").GetInt32());
        await RewardsDemoConformanceTests.Act(browser, "complete", null, ct);
        var twice = await RewardsDemoConformanceTests.Act(browser, "complete", null, ct);
        Assert.Equal(269, twice.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        var reset = await RewardsDemoConformanceTests.Act(browser, "reset", null, ct);
        Assert.Empty(reset.GetProperty("items").EnumerateArray());
        Assert.Equal(269, reset.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.True(reset.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
    });
}
