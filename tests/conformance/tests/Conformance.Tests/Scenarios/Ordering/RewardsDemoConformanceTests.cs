using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

[Collection(ConformanceCollection.Name)]
public sealed class RewardsDemoConformanceTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static async Task<JsonElement> Act(RealtimeBrowserClient browser, string action, string? code, CancellationToken ct)
    {
        var watermark = browser.ReceivedFrames.Count;
        await browser.SendAsync(new JsonObject
        {
            ["type"] = "extension.demo_rewards", ["action"] = action, ["code"] = code,
        }, ct);
        var frame = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "extension.middle_tier_tool_response", Timeout, ct);
        Assert.NotNull(frame);
        Assert.Equal(JsonValueKind.Null, frame.Json.GetProperty("previous_item_id").ValueKind);
        Assert.Equal("get_order", frame.Json.GetProperty("tool_name").GetString());
        return JsonDocument.Parse(frame.Json.GetProperty("tool_result").GetString()!).RootElement.Clone();
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Checkin_redemption_completion_reset_and_resume_preserve_exact_ledger() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: "tacobell");
        await using var first = browser;
        var invalid = await Act(browser, "check_in", "123456", ct);
        Assert.False(invalid.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        var checkedIn = await Act(browser, "check_in", "DEMO-PLUS", ct);
        Assert.Equal(500, checkedIn.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        var added = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser,
            [("add", "Crunchy Taco", "standard", 2, 999m), ("add", "Crunchwrap Supreme", "standard", 1, 999m)],
            roundTripIndex, ct);
        var reserved = await Act(browser, "redeem", null, ct);
        Assert.Equal("$8.62", reserved.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal(79, reserved.GetProperty("rewardsDemo").GetProperty("pointsToEarn").GetInt32());
        Assert.Equal(1.99m, reserved.GetProperty("rewardsDemo").GetProperty("discount").GetDecimal());
        var completed = await Act(browser, "complete", null, ct);
        var duplicate = await Act(browser, "complete", null, ct);
        Assert.Equal(329, duplicate.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.Equal(79, completed.GetProperty("rewardsDemo").GetProperty("pointsAwarded").GetInt32());

        var metadata = browser.ReceivedFrames.Snapshot().First(f => f.Type == "extension.session_metadata");
        var resumeId = metadata.Json.GetProperty("resumeId").GetString()!;
        var nextConnection = fixture.Realtime.WaitForNextConnectionAsync(Timeout, ct);
        await using var resumed = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: "tacobell", cancellationToken: ct);
        await resumed.SendExtensionResumeAsync(resumeId, ct);
        Assert.NotNull(await nextConnection);
        var restored = await resumed.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", Timeout, ct);
        Assert.NotNull(restored);
        Assert.Equal(329, restored.Json.GetProperty("order_summary").GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.True(restored.Json.GetProperty("order_summary").GetProperty("rewardsDemo").GetProperty("completed").GetBoolean());
        var reset = await Act(resumed, "reset", null, ct);
        Assert.Empty(reset.GetProperty("items").EnumerateArray());
        Assert.Equal(329, reset.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.False(reset.GetProperty("rewardsDemo").GetProperty("completed").GetBoolean());
    });

    [Theory]
    [Trait("Dotnet", "ready")]
    [InlineData("DEMO-NEW", false, 0)]
    [InlineData("DEMO-PLUS", true, 500)]
    public Task Eligibility_uses_only_existing_points_and_a_new_session_has_no_checkin(string code, bool eligible, int balance) =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, index) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: "tacobell");
        await using var _ = browser;
        var fresh = await Act(browser, "status", null, ct);
        Assert.False(fresh.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        Assert.Equal(0, fresh.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, [("add", "Crunchy Taco", "standard", 2, 999m)], index, ct);
        var linked = await Act(browser, "check_in", code, ct);
        Assert.Equal(eligible, linked.GetProperty("rewardsDemo").GetProperty("canRedeem").GetBoolean());
        var reward = await Act(browser, "redeem", null, ct);
        Assert.Equal(eligible, reward.GetProperty("rewardsDemo").GetProperty("redeemed").GetBoolean());
        Assert.Equal(balance, reward.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        var cancelled = await Act(browser, "unredeem", null, ct);
        Assert.Equal("$3.98", cancelled.GetProperty("totalDisplay").GetString());
        Assert.False(cancelled.GetProperty("rewardsDemo").GetProperty("redeemed").GetBoolean());
    });
}
