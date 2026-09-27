using System.Linq;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76 part 2 (Rick's wave-plan comment on #20): "per-persona smoke scaffold: a scenario
/// set that runs once per discovered pack ... asserting: greeting from the pack, search returns
/// only that pack's items, a basic add-to-order of an on-menu item works, not_on_menu rejection
/// works, happy-hour flag honored (enabled or not)".
///
/// Deliberately data-driven off the SAME two sources the rest of this stream already discovers
/// packs from -- <see cref="ConformancePersonas.DiscoverFromDisk()"/> (real packs under
/// personas/, today just "sonic") and the fixture pack under
/// <see cref="RepoPaths.FixturePersonasDirectory"/> (test-alpha/test-beta) -- rather than one
/// hand-written test per persona, so a future real pack (#78/#79) or a new fixture persona
/// automatically gets a smoke row once <see cref="PersonaSmokeExpectations.For"/> below is taught
/// its expectations. <see cref="PersonaSmokeCoverageTests"/> is the companion scaffold that fails
/// loudly if a discovered pack is missing from either Theory's own data source.
///
/// "Happy-hour flag honored" is covered by a DIFFERENT, dedicated file
/// (<c>PersonaHappyHourConformanceTests.cs</c>) rather than inline here: proving it needs a
/// second FixedClock connection at a different instant, which would double the per-row backend
/// connections/cost for every smoke row without adding coverage sonic's own existing
/// <c>HappyHourAtOpenTests</c> and the two new happy-hour rows don't already provide together.
///
/// Untagged (Python only), same reasoning as <see cref="PersonaBusinessRuleConformanceTests"/>:
/// the dotnet backend skeleton doesn't implement the persona catalog yet.
/// </summary>
internal static class PersonaSmokeExpectations
{
    public sealed record Expectation(
        string GreetingSubstring,
        string SearchableOwnItem,
        string OrderableItemName,
        string OrderableItemSize,
        decimal OrderableItemPrice);

    /// <summary>
    /// One entry per persona id this smoke Theory knows how to exercise. Deliberately NOT a
    /// generic/fallback lookup: a discovered pack with no entry here fails loudly (see
    /// <see cref="For"/>) rather than silently skipping its own assertions, so adding a new real
    /// or fixture pack (#78/#79) forces its own PR to also teach this map its expectations.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Expectation> ById = new Dictionary<string, Expectation>
    {
        ["sonic"] = new(
            GreetingSubstring: "Welcome to Sonic Drive-In! What can I get started for you today?",
            SearchableOwnItem: "Tots",
            OrderableItemName: "Tots",
            OrderableItemSize: "medium",
            OrderableItemPrice: 2.79m),
        ["test-alpha"] = new(
            GreetingSubstring: "Welcome to Test Alpha Drive-In! What can I get started for you?",
            SearchableOwnItem: "Alpha Burger",
            OrderableItemName: "Alpha Burger",
            OrderableItemSize: "small",
            OrderableItemPrice: 3.99m),
        ["test-beta"] = new(
            GreetingSubstring: "Welcome to Test Beta Burger Co.! What can I get started for you?",
            SearchableOwnItem: "Beta Double Burger",
            OrderableItemName: "Beta Double Burger",
            OrderableItemSize: "regular",
            OrderableItemPrice: 4.49m),
    };

    public static Expectation For(string personaId)
    {
        Assert.True(ById.TryGetValue(personaId, out var expectation),
            $"PersonaSmokeTests.cs's PersonaSmokeExpectations.ById has no entry for discovered " +
            $"persona '{personaId}' -- teach this map its expectations as part of the same PR " +
            "that adds the pack, filling in all five Expectation fields: GreetingSubstring (a " +
            "substring of the pack's OWN prompts/greeting.yaml greeting text), SearchableOwnItem " +
            "(an item name from the pack's OWN menu/menuItems.json that its search index must " +
            "return), and OrderableItemName/OrderableItemSize/OrderableItemPrice (a name, one of " +
            "its sizes, and that size's price, all taken from that SAME menu/menuItems.json entry " +
            "-- the size and price must match exactly or the add-to-order smoke step will fail).");
        return expectation!;
    }
}

/// <summary>Shared smoke body -- run once per persona id by both Theory classes below.</summary>
file static class PersonaSmokeScenario
{
    public static async Task RunAsync(ConformanceFixture fixture, string personaId, CancellationToken ct)
    {
        var expected = PersonaSmokeExpectations.For(personaId);

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _ = browser;

        // 1. Greeting from the pack: the literal conversation.item.create the persona's own
        //    prompts/greeting.yaml declares (session_manager.py's build_greeting_msg), sent
        //    upstream before the greeting's response.create (rtmt.py's send_greeting_once).
        var greetingFrame = connection.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Type == "conversation.item.create" &&
            f.Json.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var itemType) &&
            itemType.GetString() == "message");
        Assert.True(greetingFrame is not null,
            $"No greeting conversation.item.create (item.type=message) frame found for persona '{personaId}'.");
        var greetingText = greetingFrame!.Json.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains(expected.GreetingSubstring, greetingText);

        // 2. Search returns only that pack's items (positive half; the dedicated
        //    PersonaSearchIsolationConformanceTests file is the exhaustive negative-half proof).
        //    Searches by the item's own name rather than a "*" wildcard: the fake's `top` result
        //    cap (3, matching tools.py's _search_cfg default) comfortably covers each fixture
        //    pack's exactly-3-item catalog under a wildcard, but a real pack's much larger catalog
        //    (sonic) would only get whichever 3 documents happen to load first -- not necessarily
        //    this persona's own known item -- so a real search-by-name is used for every pack.
        var searchResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            JsonSerializer.Serialize(new { query = expected.SearchableOwnItem }),
            "call_smoke_search", roundTripIndex, ct, toClient: false);
        roundTripIndex = searchResult.RoundTripIndex;
        Assert.Contains(expected.SearchableOwnItem, searchResult.FunctionCallOutputText);

        // 3. A basic add-to-order of an on-menu item works.
        var addResult = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", expected.OrderableItemName, expected.OrderableItemSize, 1, expected.OrderableItemPrice)],
            roundTripIndex, ct, callIdPrefix: "call_smoke_add");
        roundTripIndex = addResult.RoundTripIndex;
        Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(addResult.ToolResultJson!));

        // 4. not_on_menu rejection works.
        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = "Nonexistent Menu Item XYZ",
                size = "medium",
                quantity = 1,
                price = 1.00m,
            }),
            "call_smoke_reject", roundTripIndex, ct, toClient: false);
        Assert.Null(rejected.ToolResultJson);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText, expectedReason: "not_on_menu", expectedItemName: "Nonexistent Menu Item XYZ");
    }
}

/// <summary>Real packs under personas/ -- today just "sonic".</summary>
[Collection(ConformanceCollection.Name)]
public sealed class RealPackPersonaSmokeTests(ConformanceFixture fixture)
{
    public static TheoryData<string> DiscoveredPersonaIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public Task Discovered_pack_passes_the_smoke_scenario(string personaId) => fixture.RunAsync(
        () => PersonaSmokeScenario.RunAsync(fixture, personaId, TestContext.Current.CancellationToken));
}

/// <summary>Fixture packs under app/backend/tests/fixtures/personas/ -- test-alpha/test-beta.</summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class FixturePackPersonaSmokeTests(TwoPersonaConformanceFixture fixture)
{
    public static TheoryData<string> FixturePersonaIds()
    {
        var data = new TheoryData<string>
        {
            TwoPersonaConformanceFixture.PersonaA,
            TwoPersonaConformanceFixture.PersonaB,
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(FixturePersonaIds))]
    public Task Discovered_pack_passes_the_smoke_scenario(string personaId) => fixture.RunAsync(
        () => PersonaSmokeScenario.RunAsync(fixture, personaId, TestContext.Current.CancellationToken));
}
