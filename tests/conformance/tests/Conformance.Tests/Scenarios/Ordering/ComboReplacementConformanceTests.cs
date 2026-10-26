using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Cascade;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

internal static class ComboReplacementContract
{
    public static readonly string[] Calls =
    [
        """{"action":"add","item_name":"SuperSONIC Double Cheeseburger (Pickles)","size":"standard","quantity":1}""",
        """{"action":"add","item_name":"SuperSONIC Double Cheeseburger Combo","size":"standard","quantity":1}""",
        """{"action":"add","item_name":"Tots","size":"medium","quantity":1}""",
        """{"action":"add","item_name":"Rainbow Slush","size":"standard","quantity":1}""",
        """{"action":"replace","item_name":"Medium Cherry Flavor Bubble Slush","original_item_name":"Rainbow Slush","size":"standard","quantity":1}""",
    ];

    public static void AssertTicket(string json)
    {
        var ticket = JsonElement.Parse(json);
        JsonElement combo = Assert.Single(ticket.GetProperty("items").EnumerateArray());
        Assert.Contains("(Pickles)", combo.GetProperty("item").GetString());
        Assert.Equal(1, combo.GetProperty("quantity").GetInt32());
        Assert.Contains("Medium Tots", combo.GetProperty("display").GetString());
        Assert.Contains("Medium Cherry Flavor Bubble Slush", combo.GetProperty("display").GetString());
        Assert.DoesNotContain("Rainbow", combo.GetProperty("display").GetString());
        Assert.Equal("$10.19", ticket.GetProperty("totalDisplay").GetString());
        Assert.Equal("$11.01", ticket.GetProperty("finalTotalDisplay").GetString());
        Assert.DoesNotContain("Rainbow", ticket.GetProperty("spokenReadBack").GetString());
        Assert.Contains("Medium Cherry Flavor Bubble Slush", ticket.GetProperty("spokenReadBack").GetString());
    }

    public static void AssertPromptAndSchema(string prompt, JsonElement tools, bool nestedFunction)
    {
        Assert.Contains("FINISH INTENT OVERRIDES", prompt);
        Assert.Contains("Nope, that'll be it", prompt);
        Assert.Contains("do not repeat the read-back or call get_order again", prompt);
        Assert.DoesNotContain("After EVERY confirmed item", prompt);
        Assert.DoesNotContain("ALWAYS state the total in the closing phrase", prompt);
        Assert.DoesNotContain("- Call get_order and read back items with TOTAL only", prompt);
        Assert.DoesNotContain("NEVER say 'All set'", prompt);
        JsonElement update = tools.EnumerateArray()
            .Select(t => nestedFunction ? t.GetProperty("function") : t)
            .Single(t => t.GetProperty("name").GetString() == "update_order");
        JsonElement properties = update.GetProperty("parameters").GetProperty("properties");
        Assert.Contains(properties.GetProperty("action").GetProperty("enum").EnumerateArray(), a => a.GetString() == "replace");
        Assert.Equal("string", properties.GetProperty("original_item_name").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("combo_index").GetProperty("type").GetString());
    }
}

[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class ComboReplacementRealtimeTests(ConformanceFixture fixture)
{
    [Fact]
    public Task RealtimeFakeCallerReplacesIncludedSlushAndReceivesCurrentClosingReadback() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealtimeBrowserClient browser, FakeRealtimeConnection connection, int round) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, mode: "lunch");
        await using RealtimeBrowserClient _ = browser;
        JsonElement session = connection.ReceivedFrames.Snapshot().First(f => f.Type == "session.update").Json.GetProperty("session");
        ComboReplacementContract.AssertPromptAndSchema(
            session.GetProperty("instructions").GetString()!, session.GetProperty("tools"), nestedFunction: false);

        ToolCallResult? last = null;
        for (int i = 0; i < ComboReplacementContract.Calls.Length; i++)
        {
            last = await OrderScenarioHelpers.CallToolAsync(connection, browser, "update_order",
                ComboReplacementContract.Calls[i], $"combo_swap_{i}", round, ct);
            round = last.RoundTripIndex;
        }
        ComboReplacementContract.AssertTicket(last!.ToolResultJson!);
        Assert.Contains("Replaced Rainbow Slush", last.FunctionCallOutputText);

        ToolCallResult closing = await OrderScenarioHelpers.CallToolAsync(connection, browser, "get_order",
            "{}", "closing_readback", round, ct);
        ComboReplacementContract.AssertTicket(closing.ToolResultJson!);
        Assert.DoesNotContain("UPSELL HINT", closing.FunctionCallOutputText);
        Assert.DoesNotContain("Anything else?", closing.FunctionCallOutputText);
    });
}

[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class ComboReplacementCascadeTests(CascadeConformanceFixture fixture)
{
    [Fact]
    public Task CascadeFakeCallerReplacesIncludedSlushAndReceivesCurrentClosingReadback() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CascadeConnection connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, mode: "lunch");
        await using RealtimeBrowserClient browser = connection.Browser;
        int requestWatermark = fixture.Chat.RequestCount;
        int frameWatermark = browser.ReceivedFrames.Count;
        for (int i = 0; i <= ComboReplacementContract.Calls.Length; i++)
        {
            bool closing = i == ComboReplacementContract.Calls.Length;
            fixture.Chat.EnqueueMessage(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"cascade_swap_{i}",
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = closing ? "get_order" : "update_order",
                        ["arguments"] = closing ? "{}" : ComboReplacementContract.Calls[i],
                    },
                }),
            });
        }
        // Scripted speech proves transport, not model adherence to the finish instructions.
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "Thank you! Your carhop will have that right out!" });
        fixture.Realtime.NextTranscript = "Can I make that a cherry slush instead? Nope, that'll be it.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);
        RecordedFrame? finished = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= frameWatermark && f.Type == "extension.round_trip_token", CascadeScenarioHelpers.FrameTimeout, ct);
        Assert.NotNull(finished);

        RecordedFrame closingTicket = browser.ReceivedFrames.Snapshot().Last(f => f.Sequence >= frameWatermark &&
            f.Type == "extension.middle_tier_tool_response" && f.Json.GetProperty("tool_name").GetString() == "get_order");
        ComboReplacementContract.AssertTicket(closingTicket.Json.GetProperty("tool_result").GetString()!);
        JsonElement request = fixture.Chat.Requests[requestWatermark].RawBody;
        string prompt = request.GetProperty("messages")[0].GetProperty("content").GetString()!;
        ComboReplacementContract.AssertPromptAndSchema(prompt, request.GetProperty("tools"), nestedFunction: true);
        JsonElement finalRequest = fixture.Chat.Requests[^1].RawBody;
        string closingOutput = finalRequest.GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
        Assert.Contains("Medium Cherry Flavor Bubble Slush", closingOutput);
        Assert.DoesNotContain("Rainbow", closingOutput);
        Assert.DoesNotContain("UPSELL HINT", closingOutput);
    });
}
