using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// personas/, today just the default pack) and the fixture pack under
/// <see cref="RepoPaths.FixturePersonasDirectory"/> (test-alpha/test-beta) -- rather than one
/// hand-written test per persona, so a future real pack (#78/#79) or a new fixture persona
/// automatically gets a smoke row once it has its own
/// tests/conformance/testdata/personas/&lt;id&gt;/smoke.json (<see cref="PersonaSmokeExpectations.For"/>).
/// <see cref="PersonaSmokeCoverageTests"/> is the companion scaffold that fails loudly if a
/// discovered pack is missing from either Theory's own data source.
///
/// Rick's PR #108 second review: this file (and <see cref="PersonaSmokeExpectations"/>,
/// <see cref="PersonaSmokeScenario"/>, <see cref="PersonaSmokeCoverageTests"/>) is shared C# and
/// so carries NO persona-specific literal -- no pack id, no greeting text, no menu item name --
/// of its own; every persona-specific value used below comes from that persona's own
/// smoke.json, read fresh per call. This lets a data-only pack PR (#111/#112) add its own
/// persona's smoke coverage without touching this repo's shared test code at all.
///
/// "Happy-hour flag honored" is covered by a DIFFERENT, dedicated file
/// (<c>PersonaHappyHourConformanceTests.cs</c>) rather than inline here: proving it needs a
/// second FixedClock connection at a different instant, which would double the per-row backend
/// connections/cost for every smoke row without adding coverage the default pack's own existing
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
    /// MONEY CONTRACT: <see cref="Expectation.OrderableItemPrice"/> is stored as a quoted, exact
    /// decimal string in each persona's own smoke.json (same convention as
    /// GoldenOrderPricingData.cs), so <see cref="JsonNumberHandling.AllowReadingFromString"/> lets
    /// it deserialize straight into a `decimal` with no intermediate `double`.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Rick's PR #108 second review: reads one persona's smoke expectations from its OWN data
    /// file -- tests/conformance/testdata/personas/&lt;id&gt;/smoke.json (<see
    /// cref="RepoPaths.PersonaSmokeDataPath"/>) -- instead of a hand-copied literal dictionary in
    /// this shared C# file. This is deliberate: a data-only pack PR (#111/#112) must be able to
    /// add a new persona's smoke coverage by adding ONE file under testdata/personas/, without
    /// touching PersonaSmokeTests.cs (which therefore carries no persona-specific literal --
    /// no pack name, no greeting text, no menu item -- at all). A discovered pack with no file
    /// here fails loudly (see the message below) rather than silently skipping its own
    /// assertions, so adding a new real or fixture pack forces its own PR to also add its own
    /// smoke.json.
    /// </summary>
    public static Expectation For(string personaId)
    {
        var path = RepoPaths.PersonaSmokeDataPath(RepoPaths.FindRepoRoot(), personaId);
        Assert.True(File.Exists(path),
            $"PersonaSmokeTests.cs's PersonaSmokeExpectations.For('{personaId}') found no data " +
            $"file at '{path}'. Create it (as part of the same PR that adds the pack) with " +
            "exactly these five fields, each sourced from THIS SAME persona's own pack files: " +
            "greetingSubstring (a substring of the pack's OWN prompts/greeting.yaml greeting " +
            "text), searchableOwnItem (an item name from the pack's OWN menu/menuItems.json that " +
            "its search index must return), and orderableItemName/orderableItemSize/" +
            "orderableItemPrice (a name, one of its sizes, and that size's price, all taken from " +
            "that SAME menu/menuItems.json entry -- the size and price must match exactly or the " +
            "add-to-order smoke step will fail; prefer an item that is NOT happyHourDiscounted:" +
            "true, since the smoke fixture runs on the real wall clock and this price is asserted " +
            "as charged). Template:\n" +
            "{\n" +
            "  \"greetingSubstring\": \"<substring of this pack's own greeting text>\",\n" +
            "  \"searchableOwnItem\": \"<an item name from this pack's own menu>\",\n" +
            "  \"orderableItemName\": \"<a menu item name from this SAME pack>\",\n" +
            "  \"orderableItemSize\": \"<one of that item's own sizes>\",\n" +
            "  \"orderableItemPrice\": \"<that size's price, quoted, e.g. \\\"2.79\\\">\"\n" +
            "}");

        var json = File.ReadAllText(path);
        var expectation = JsonSerializer.Deserialize<Expectation>(json, Options);
        Assert.True(expectation is not null, $"'{path}' deserialized to null.");
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
        //    (the default pack) would only get whichever 3 documents happen to load first -- not necessarily
        //    this persona's own known item -- so a real search-by-name is used for every pack.
        var searchResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            JsonSerializer.Serialize(new { query = expected.SearchableOwnItem }),
            "call_smoke_search", roundTripIndex, ct, toClient: false);
        roundTripIndex = searchResult.RoundTripIndex;
        Assert.Contains(expected.SearchableOwnItem, searchResult.FunctionCallOutputText);

        // 3. A basic add-to-order of an on-menu item works -- and (Rick's PR #108 round 4 review
        //    item 1) the amount actually CHARGED is the pack's own listed menu price, not just
        //    that the item landed in the order at all. The server charges the pack's own menu
        //    price and ignores whatever price `update_order` is called with (#107), so the add
        //    step below deliberately sends a wrong tool price (0.01m) instead of
        //    expected.OrderableItemPrice. PersonaMenuPrice.Read re-reads that SAME persona's own
        //    menu/menuItems.json fresh, independent of smoke.json, so the two assertions together
        //    (smoke.json's price literal matches the real menu below, and the charged total
        //    matches smoke.json's price further down despite the wrong tool price sent) prove
        //    this session is priced from its own pack's menu, not from the echoed tool argument.
        var realMenuPrice = PersonaMenuPrice.Read(
            fixture.PersonasDirectory, personaId, expected.OrderableItemName, expected.OrderableItemSize);
        Assert.Equal(realMenuPrice, expected.OrderableItemPrice);

        //    Pre-tax ("total", not "finalTotal"): the quantity is always 1 here, so this is
        //    exactly OrderableItemPrice for any pack whose smoke item isn't happy-hour-eligible
        //    (see the "prefer a non-happyHourDiscounted item" guidance in
        //    PersonaSmokeExpectations.For's template -- this fixture runs on the real wall clock,
        //    not a FixedClock, so a happy-hour-eligible item's charged price would be
        //    time-of-day-dependent and this exact-match assertion would flake).
        var addResult = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", expected.OrderableItemName, expected.OrderableItemSize, 1, 0.01m)],
            roundTripIndex, ct, callIdPrefix: "call_smoke_add");
        roundTripIndex = addResult.RoundTripIndex;
        Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(addResult.ToolResultJson!));
        OrderScenarioHelpers.AssertMoneyEqual(
            expected.OrderableItemPrice,
            OrderScenarioHelpers.GetOrderTotal(addResult.ToolResultJson!),
            $"persona '{personaId}': the order's pre-tax total after adding one " +
            $"'{expected.OrderableItemName}' ({expected.OrderableItemSize}) must equal its own " +
            $"pack's listed menu price ({expected.OrderableItemPrice}), not just contain 1 item.");

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

/// <summary>Real packs under personas/ -- today just the default pack.</summary>
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
    [Trait("Dotnet", "ready")]
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

    /// <summary>
    /// Fixture packs discovered on disk under app/backend/tests/fixtures/personas/ that
    /// deliberately have NO row here, with the reason why -- <see
    /// cref="PersonaSmokeCoverageTests"/> reads this so a pack that is genuinely out of scope for
    /// this generic smoke scaffold by design doesn't fail loudly there either, while a pack that
    /// is simply forgotten still does.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FixturePersonaExclusions { get; } =
        new Dictionary<string, string>
        {
            ["test-gamma"] = "PR #106: a narrow-`models.realtime.allowed` fixture pack scoped to " +
                "ModelSelectionConformanceFixture's negative (404) model-selection rows only (see " +
                "ModelSelectionConformanceFixtures.cs) -- it is not part of " +
                "TwoPersonaConformanceFixture (test-alpha/test-beta) and doesn't need its own " +
                "generic greeting/search/order/happy-hour smoke coverage.",
            ["test-delta"] = "Refs #77, #283: a narrow bundle/extras-engine fixture pack exercised " +
                "by app/backend/tests/test_bundle_and_extras_engine.py (Python unit tests) and, " +
                "since #283, also discovered automatically by " +
                "Scenarios/Ordering/ComboComponentResizeConformanceTests.cs's own " +
                "DiscoveredBundleResizeCases() (its 'Delta Classic Meal' bundle genuinely leaves " +
                "a drink slot open) -- it is still not part of TwoPersonaConformanceFixture " +
                "(test-alpha/test-beta) and doesn't need its own generic greeting/search/order/" +
                "happy-hour smoke coverage; the combo-resize Theory in " +
                "Scenarios/Ordering/ComboComponentResizeConformanceTests.cs already covers it.",
            ["test-epsilon"] = "Refs #179/#184: a narrow wholeBundleSize plus happy-hour fixture " +
                "pack exercised by backend unit tests only -- it is not part of " +
                "TwoPersonaConformanceFixture (test-alpha/test-beta) and doesn't need generic " +
                "greeting/search/order smoke coverage.",
            ["test-zeta"] = "Refs #283: a narrow, TEST-ONLY `includedAnySize` combo fixture pack " +
                "(two distinctly-named bundles, each with a genuinely open drinks slot) added so " +
                "Scenarios/Ordering/ComboComponentResizeConformanceTests.cs's Discovered_* theories " +
                "-- which need a real `includedAnySize` pack with an open drink slot, something no " +
                "shipped persona/ pack has today -- produce real rows on both conformance legs " +
                "instead of permanently SKIPPING via [Theory(SkipTestWithoutData = true)]. Not " +
                "part of TwoPersonaConformanceFixture (test-alpha/test-beta) and doesn't need its " +
                "own generic greeting/search/order/happy-hour smoke coverage; the combo-resize " +
                "Theory above already covers it end to end.",
        };

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(FixturePersonaIds))]
    public Task Discovered_pack_passes_the_smoke_scenario(string personaId) => fixture.RunAsync(
        () => PersonaSmokeScenario.RunAsync(fixture, personaId, TestContext.Current.CancellationToken));
}
