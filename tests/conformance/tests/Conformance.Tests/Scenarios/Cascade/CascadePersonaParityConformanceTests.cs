using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Coordinator follow-up on PR #287 (issue #21 acceptance gap 1): <see cref="CascadeConformanceTests"/>
/// proves the cascade pipeline's dispatch/tool-calling/pricing contract once, against the fixture's
/// own default persona pack only (<see cref="ConformancePersonas.DefaultPersonaId"/>). That leaves
/// every OTHER real, shipped pack (<see cref="ConformancePersonas.DiscoverFromDisk()"/>) completely
/// unexercised on the cascade pipeline -- a persona-binding regression that happened to only affect
/// a non-default pack's own menu/tax lookup (e.g. a hardcoded fallback to the default pack somewhere
/// in <c>CascadeProcessor</c>'s persona-scoped menu/tax resolution) would be invisible to the
/// existing cascade suite.
///
/// <see cref="CascadeConformanceFixture"/> already boots its one backend process with ALL
/// disk-discovered real packs enabled (see that fixture's own `Startup validation passed` log line
/// -- it never overrides <c>Personas</c>/<c>Persona</c>, so <c>ConformanceFixture</c>'s own
/// disk-discovery fallback picks up every real pack automatically, same as the realtime pipeline's
/// <c>RealPackMenuModeConformanceTests</c>/<c>RealPackBundleAutoFillConformanceTests</c>/etc.
/// already do). This class therefore reuses that SAME fixture/collection (no second backend
/// process needed) and just asks for a different persona per connection --
/// <see cref="CascadeScenarioHelpers.ConnectPastGreetingAsync"/> already accepts a <c>persona</c>
/// override for exactly this purpose.
///
/// Like the realtime pipeline's own per-pack theories (e.g.
/// <c>RealPackBundleAutoFillConformanceTests</c>), every fact this Theory asserts -- which pack,
/// which item, its size, its price, its tax -- is read from that SAME pack's own
/// <c>menu/menuItems.json</c>/<c>persona.json</c>, never hardcoded in shared C#, so new packs are
/// covered automatically via <see cref="ConformancePersonas.DiscoverFromDisk()"/> with no code
/// change here. The fixture's own default pack is excluded from this Theory's rows (expressed
/// generically via <see cref="ConformancePersonas.DefaultPersonaId"/>, not a literal pack id)
/// because <see cref="CascadeConformanceTests"/>' existing pricing row already covers it.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class CascadePersonaParityConformanceTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

    private sealed record SingleSizeItem(string Name, string Size, decimal Price);

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
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<string, string, string, string, string, string, string>();

        foreach (var personaId in ConformancePersonas.DiscoverFromDisk())
        {
            // The fixture's own default pack is already covered end to end by
            // CascadeConformanceTests -- this Theory's whole point is every OTHER pack, expressed
            // generically so it keeps working if the default pack ever changes.
            if (string.Equals(personaId, ConformancePersonas.DefaultPersonaId, StringComparison.Ordinal))
            {
                continue;
            }

            var item = DiscoverSingleSizeItem(personasDir, personaId);
            Assert.True(item is not null,
                $"persona '{personaId}' has no single-size, non-combo-slot menu item in its own " +
                "menu/menuItems.json for this Theory to exercise.");

            var taxRate = ReadTaxRate(personasDir, personaId);
            var tax = Math.Round(item!.Price * taxRate, 2, MidpointRounding.AwayFromZero);
            var finalTotal = Math.Round(item.Price + tax, 2, MidpointRounding.AwayFromZero);

            data.Add(
                personaId,
                item.Name,
                item.Size,
                FormatUsd(item.Price),
                FormatUsd(tax),
                FormatUsd(finalTotal),
                $"Added a {item.Size} {item.Name} -- anything else?");
        }

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

    private static SingleSizeItem? DiscoverSingleSizeItem(string personasDir, string personaId)
    {
        var menuItemsPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(menuItemsPath));

        foreach (var category in doc.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("comboSlot", out var comboSlot) &&
                    comboSlot.ValueKind == JsonValueKind.String &&
                    !string.Equals(comboSlot.GetString(), "none", StringComparison.Ordinal))
                {
                    continue;
                }

                var sizes = item.GetProperty("sizes");
                if (sizes.GetArrayLength() != 1)
                {
                    continue;
                }

                var onlySize = sizes[0];
                return new SingleSizeItem(
                    item.GetProperty("name").GetString()!,
                    onlySize.GetProperty("size").GetString()!,
                    onlySize.GetProperty("price").GetDecimal());
            }
        }

        return null;
    }

    private static decimal ReadTaxRate(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(personaJsonPath));
        var taxRateText = doc.RootElement.GetProperty("pricing").GetProperty("taxRate").GetString();
        return decimal.Parse(taxRateText!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatUsd(decimal value) => $"${value:0.00}";
}
