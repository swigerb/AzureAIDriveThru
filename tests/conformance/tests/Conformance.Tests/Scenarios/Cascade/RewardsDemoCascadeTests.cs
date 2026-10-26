using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class RewardsDemoCascadeTests(CascadeConformanceFixture fixture)
{
    [Fact]
    public Task Cascade_greeting_instruction_is_request_scoped_and_resume_preserves_only_real_speech() =>
        fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int before = fixture.Chat.RequestCount;
        CascadeConnection connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: "tacobell");
        await using RealtimeBrowserClient browser = connection.Browser;
        JsonElement greeting = fixture.Chat.Requests[before].RawBody;
        Assert.Empty(greeting.GetProperty("tools").EnumerateArray());
        Assert.Equal("none", greeting.GetProperty("tool_choice").GetString());
        Assert.All(greeting.GetProperty("messages").EnumerateArray(), message =>
            Assert.Equal("system", message.GetProperty("role").GetString()));
        Assert.Contains("Say exactly: Welcome to Taco Bell!",
            greeting.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
        Assert.Equal(before + 1, fixture.Chat.RequestCount);
        RecordedFrame metadata = browser.ReceivedFrames.Snapshot().Single(frame => frame.Type == "extension.session_metadata");
        string resumeId = metadata.Json.GetProperty("resumeId").GetString()!;
        fixture.Chat.EnqueueMessage(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "scoped-order",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
            }),
        });
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "What can I get started for you?" });
        fixture.Realtime.NextTranscript = "Ready to order.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);
        Assert.NotNull(await browser.ReceivedFrames.WaitForAsync(
            frame => frame.Sequence > connection.GreetingWatermark && frame.Type == "extension.round_trip_token",
            CascadeScenarioHelpers.FrameTimeout, ct));
        JsonElement next = fixture.Chat.Requests[before + 1].RawBody;
        Assert.NotEmpty(next.GetProperty("tools").EnumerateArray());
        Assert.Equal(["system", "assistant", "user"],
            next.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Contains("Welcome to the drive-thru!", next.GetRawText());
        Assert.DoesNotContain("Say exactly:", next.GetRawText());
        Assert.DoesNotContain("Greet exactly:", next.GetRawText());
        JsonElement result = fixture.Chat.Requests[before + 2].RawBody.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("scoped-order", result.GetProperty("tool_call_id").GetString());
        Assert.Equal(before + 3, fixture.Chat.RequestCount);

        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(CascadeScenarioHelpers.FrameTimeout, ct);
        await using RealtimeBrowserClient resumed = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct, persona: "tacobell");
        await resumed.SendExtensionResumeAsync(resumeId, ct);
        Assert.NotNull(await resumed.ReceivedFrames.WaitForAsync(
            frame => frame.Type == "extension.session_resumed", CascadeScenarioHelpers.FrameTimeout, ct));
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        Assert.Equal(before + 3, fixture.Chat.RequestCount);
        Assert.DoesNotContain(resumed.ReceivedFrames.Snapshot(), frame => frame.Type == "response.created");
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "Take your time." });
        fixture.Realtime.NextTranscript = "One moment, please.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(resumed, ct);
        Assert.NotNull(await resumed.ReceivedFrames.WaitForAsync(
            frame => frame.Type == "extension.round_trip_token", CascadeScenarioHelpers.FrameTimeout, ct));
        string rehydrated = fixture.Chat.Requests[before + 3].RawBody.GetRawText();
        Assert.Contains("Welcome to the drive-thru!", rehydrated);
        Assert.DoesNotContain("Say exactly:", rehydrated);
        Assert.DoesNotContain("Greet exactly:", rehydrated);
    });

    [Fact]
    public Task Cascade_checkin_tool_order_redemption_and_completion_use_the_same_ledger() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CascadeConnection connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: "tacobell");
        await using RealtimeBrowserClient browser = connection.Browser;
        JsonElement invalid = await RewardsDemoConformanceTests.Act(browser, "check_in", "123456", ct);
        Assert.False(invalid.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
        await RewardsDemoConformanceTests.Act(browser, "check_in", "DEMO-PLUS", ct);

        fixture.Chat.EnqueueMessage(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "cascade_demo_tacos",
                ["type"] = "function",
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
        RecordedFrame? added = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "extension.middle_tier_tool_response"
                && f.Json.GetProperty("tool_name").GetString() == "update_order",
            CascadeScenarioHelpers.FrameTimeout, ct);
        Assert.NotNull(added);
        Assert.Equal("$3.98", JsonDocument.Parse(added.Json.GetProperty("tool_result").GetString()!)
            .RootElement.GetProperty("totalDisplay").GetString());
        Assert.NotNull(await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > added.Sequence && f.Type == "response.done", CascadeScenarioHelpers.FrameTimeout, ct));

        JsonElement redeemed = await RewardsDemoConformanceTests.Act(browser, "redeem", null, ct);
        Assert.Equal("$2.15", redeemed.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal(19, redeemed.GetProperty("rewardsDemo").GetProperty("pointsToEarn").GetInt32());
        await RewardsDemoConformanceTests.Act(browser, "complete", null, ct);
        JsonElement twice = await RewardsDemoConformanceTests.Act(browser, "complete", null, ct);
        Assert.Equal(269, twice.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        JsonElement reset = await RewardsDemoConformanceTests.Act(browser, "reset", null, ct);
        Assert.Empty(reset.GetProperty("items").EnumerateArray());
        Assert.Equal(269, reset.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        Assert.True(reset.GetProperty("rewardsDemo").GetProperty("checkedIn").GetBoolean());
    });
}
