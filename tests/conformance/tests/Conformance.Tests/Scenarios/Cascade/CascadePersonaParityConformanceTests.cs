using System.Text.Json.Nodes;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Coordinator follow-up on PR #287 (issue #21 acceptance gap 1): <see cref="CascadeConformanceTests"/>
/// proves the cascade pipeline's dispatch/tool-calling/pricing contract once, against the real,
/// shipped `sonic` pack only. That leaves the other two real, shipped packs
/// (<c>personas/dunkin</c>, <c>personas/mcdonalds</c>) completely unexercised on the cascade
/// pipeline -- a persona-binding regression that happened to only affect those two packs' own
/// menu/tax lookup (e.g. a hardcoded "sonic" fallback somewhere in <c>CascadeProcessor</c>'s
/// persona-scoped menu/tax resolution) would be invisible to the existing cascade suite.
///
/// <see cref="CascadeConformanceFixture"/> already boots its one backend process with ALL
/// disk-discovered real packs enabled (`PERSONAS=dunkin,mcdonalds,sonic`, see that fixture's own
/// `Startup validation passed` log line -- it never overrides <c>Personas</c>/<c>Persona</c>, so
/// <c>ConformanceFixture</c>'s own disk-discovery fallback picks up every real pack automatically,
/// same as the realtime pipeline's <c>RealPackMenuModeConformanceTests</c>/
/// <c>RealPackBundleAutoFillConformanceTests</c>/etc. already do). This class therefore reuses
/// that SAME fixture/collection (no second backend process needed) and just asks for a different
/// persona per connection -- <see cref="CascadeScenarioHelpers.ConnectPastGreetingAsync"/> already
/// accepts a <c>persona</c> override for exactly this purpose.
///
/// Expected totals below are computed from each pack's own real
/// <c>menu/menuItems.json</c> price and <c>persona.json</c>'s <c>taxRate</c> (0.08 for every real
/// pack today), using the exact same "subtotal*taxRate, round-half-up to the cent at display time
/// only" money contract <c>golden-order-pricing.json</c> documents for `sonic` -- never invented,
/// so a pricing or tax-binding regression on a non-default persona fails loudly here.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class CascadePersonaParityConformanceTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

    private static JsonObject ToolCallMessage(string toolCallId, string toolName, string argumentsJson) => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["id"] = toolCallId,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = toolName, ["arguments"] = argumentsJson },
        }),
    };

    private static JsonObject FinalMessage(string content) => new() { ["role"] = "assistant", ["content"] = content };

    public static TheoryData<string, string, string, string, string, string, string> NonDefaultRealPackOrderCases()
    {
        var data = new TheoryData<string, string, string, string, string, string, string>();

        // personas/dunkin/menu/menuItems.json: Original Blend Coffee, medium = $2.59.
        // tax = 2.59 * 0.08 = 0.2072 -> "$0.21"; final = 2.7972 -> "$2.80".
        data.Add("dunkin", "Original Blend Coffee", "medium", "$2.59", "$0.21", "$2.80", "Added a medium Original Blend Coffee -- anything else?");

        // personas/mcdonalds/menu/menuItems.json: Cheeseburger, Standard = $2.09.
        // tax = 2.09 * 0.08 = 0.1672 -> "$0.17"; final = 2.2572 -> "$2.26".
        data.Add("mcdonalds", "Cheeseburger", "standard", "$2.09", "$0.17", "$2.26", "Added a Cheeseburger -- anything else?");

        return data;
    }

    [Theory]
    [MemberData(nameof(NonDefaultRealPackOrderCases))]
    public Task Cascade_binds_session_metadata_and_prices_from_the_requested_non_default_real_packs_own_menu(
        string personaId, string itemName, string size, string expectedSubtotal, string expectedTax,
        string expectedFinalTotal, string finalReply) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: personaId);
        await using var browser = connection.Browser;

        var metadata = browser.ReceivedFrames.Snapshot().FirstOrDefault(f => f.Type == "extension.session_metadata");
        Assert.True(metadata is not null, $"Expected extension.session_metadata within {FrameTimeout}.");
        Assert.Equal(personaId, metadata!.Json.GetProperty("persona").GetString());
        Assert.Equal("cascade", metadata.Json.GetProperty("pipeline").GetString());

        fixture.Chat.EnqueueMessage(ToolCallMessage(
            "call_update_order_1", "update_order",
            $$"""{"action":"add","item_name":"{{itemName}}","size":"{{size}}","quantity":1}"""));
        fixture.Chat.EnqueueMessage(FinalMessage(finalReply));
        fixture.Realtime.NextTranscript = $"I'll get a {size} {itemName}.";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(update_order) within {FrameTimeout}.");

        var orderSummaryJson = toolResponse!.Json.GetProperty("tool_result").GetString()!;
        Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(orderSummaryJson));
        Assert.Equal(expectedSubtotal, OrderScenarioHelpers.GetOrderTotalDisplay(orderSummaryJson));
        Assert.Equal(expectedTax, OrderScenarioHelpers.GetOrderTaxDisplay(orderSummaryJson));
        Assert.Equal(expectedFinalTotal, OrderScenarioHelpers.GetOrderFinalTotalDisplay(orderSummaryJson));

        // Same anti-flake convention CascadeConformanceTests' own pricing row documents (#118
        // Rick re-review item 3): drain the final round's scripted reply before returning, so a
        // leftover FakeChatCompletionsServer message never leaks into whichever cascade test runs
        // next out of this fixture's shared, process-wide Chat queue.
        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta",
            FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's final answer as response.audio_transcript.delta.");
        Assert.Equal(finalReply, finalAnswer!.Json.GetProperty("delta").GetString());
    });
}
