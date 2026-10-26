using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

[Collection(ConformanceCollection.Name)]
public sealed class RewardsDemoConformanceTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    internal static async Task<JsonElement> Act(RealtimeBrowserClient browser, string action, string? code, CancellationToken ct)
    {
        int watermark = browser.ReceivedFrames.Count;
        await browser.SendAsync(new JsonObject
        {
            ["type"] = "extension.demo_rewards",
            ["action"] = action,
            ["code"] = code,
        }, ct);
        RecordedFrame? frame = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "extension.middle_tier_tool_response", s_timeout, ct);
        Assert.NotNull(frame);
        Assert.Equal(JsonValueKind.Null, frame.Json.GetProperty("previous_item_id").ValueKind);
        Assert.Equal("get_order", frame.Json.GetProperty("tool_name").GetString());
        return JsonDocument.Parse(frame.Json.GetProperty("tool_result").GetString()!).RootElement.Clone();
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Checkin_redemption_completion_reset_and_resume_preserve_exact_ledger() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealtimeBrowserClient? browser, FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: "tacobell");
        await using RealtimeBrowserClient first = browser;
        Assert.Contains("Say exactly: Welcome to Taco Bell!", OrderScenarioHelpers.GreetingInstructions(connection));
        Assert.Single(connection.ReceivedFrames.Snapshot(), frame => frame.Type == "response.create");
        JsonElement invalid = await Act(browser, "check_in", "123456", ct);
        Assert.False(invalid.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        JsonElement checkedIn = await Act(browser, "check_in", "DEMO-PLUS", ct);
        Assert.Equal(500, checkedIn.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        ToolCallResult added = await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser,
            [("add", "Crunchy Taco", "standard", 2, 999m), ("add", "Crunchwrap Supreme", "standard", 1, 999m)],
            roundTripIndex, ct);
        JsonElement reserved = await Act(browser, "redeem", null, ct);
        Assert.Equal("$8.62", reserved.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal(79, reserved.GetProperty("rewardsDemo").GetProperty("pointsToEarn").GetInt32());
        Assert.Equal(1.99m, reserved.GetProperty("rewardsDemo").GetProperty("discount").GetDecimal());
        JsonElement completed = await Act(browser, "complete", null, ct);
        JsonElement duplicate = await Act(browser, "complete", null, ct);
        Assert.Equal(329, duplicate.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.Equal(79, completed.GetProperty("rewardsDemo").GetProperty("pointsAwarded").GetInt32());

        RecordedFrame metadata = browser.ReceivedFrames.Snapshot().First(f => f.Type == "extension.session_metadata");
        string resumeId = metadata.Json.GetProperty("resumeId").GetString()!;
        Task<FakeRealtimeConnection?> nextConnection = fixture.Realtime.WaitForNextConnectionAsync(s_timeout, ct);
        await using RealtimeBrowserClient resumed = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: "tacobell", cancellationToken: ct);
        await resumed.SendExtensionResumeAsync(resumeId, ct);
        FakeRealtimeConnection? resumedConnection = await nextConnection;
        Assert.NotNull(resumedConnection);
        RecordedFrame? restored = await resumed.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", s_timeout, ct);
        Assert.NotNull(restored);
        Assert.Equal(329, restored.Json.GetProperty("order_summary").GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.True(restored.Json.GetProperty("order_summary").GetProperty("rewardsDemo").GetProperty("completed").GetBoolean());
        JsonElement reset = await Act(resumed, "reset", null, ct);
        Assert.Empty(reset.GetProperty("items").EnumerateArray());
        Assert.Equal(329, reset.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.False(reset.GetProperty("rewardsDemo").GetProperty("completed").GetBoolean());
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        Assert.DoesNotContain(resumedConnection.ReceivedFrames.Snapshot(), frame => frame.Type == "response.create");
        RecordedFrame rehydration = resumedConnection.ReceivedFrames.Snapshot().Single(frame => frame.Type == "conversation.item.create");
        Assert.Equal("system", rehydration.Json.GetProperty("item").GetProperty("role").GetString());
        Assert.DoesNotContain("Say exactly:", rehydration.Json.GetRawText());
        Assert.DoesNotContain("Greet exactly:", rehydration.Json.GetRawText());
    });

    [Theory]
    [Trait("Dotnet", "ready")]
    [InlineData("DEMO-NEW", false, 0)]
    [InlineData("DEMO-PLUS", true, 500)]
    public Task Eligibility_uses_only_existing_points_and_a_new_session_has_no_checkin(string code, bool eligible, int balance) =>
        fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealtimeBrowserClient? browser, FakeRealtimeConnection? connection, int index) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: "tacobell");
        await using RealtimeBrowserClient _ = browser;
        JsonElement fresh = await Act(browser, "status", null, ct);
        Assert.False(fresh.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        Assert.Equal(0, fresh.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        await OrderScenarioHelpers.RunOrderStepsAsync(connection, browser, [("add", "Crunchy Taco", "standard", 2, 999m)], index, ct);
        JsonElement linked = await Act(browser, "check_in", code, ct);
        Assert.Equal(eligible, linked.GetProperty("rewardsDemo").GetProperty("canRedeem").GetBoolean());
        JsonElement reward = await Act(browser, "redeem", null, ct);
        Assert.Equal(eligible, reward.GetProperty("rewardsDemo").GetProperty("redeemed").GetBoolean());
        Assert.Equal(balance, reward.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        JsonElement cancelled = await Act(browser, "unredeem", null, ct);
        Assert.Equal("$3.98", cancelled.GetProperty("totalDisplay").GetString());
        Assert.False(cancelled.GetProperty("rewardsDemo").GetProperty("redeemed").GetBoolean());
    });
}
