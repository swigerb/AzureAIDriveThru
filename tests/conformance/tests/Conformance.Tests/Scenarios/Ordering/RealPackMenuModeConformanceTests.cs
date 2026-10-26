using System.Globalization;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

internal static class PersonaDaypartsFeature
{
    public static bool Read(string personasDir, string personaId)
    {
        string personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using FileStream stream = File.OpenRead(personaJsonPath);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("features").GetProperty("dayparts").GetBoolean();
    }
}

internal static class PersonaMenuModeVectors
{
    public sealed record ItemVector(string Name, string Size, string Price)
    {
        public decimal MenuPrice => decimal.Parse(Price, CultureInfo.InvariantCulture);
    }

    public sealed record Vectors(
        ItemVector BreakfastItem,
        ItemVector LunchItem,
        ItemVector AllDayItem,
        IReadOnlyList<ItemVector>? AllDayItems = null)
    {
        public IReadOnlyList<ItemVector> AllDayItemVectors => AllDayItems is { Count: > 0 } ? AllDayItems : [AllDayItem];
    }

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Vectors? For(string personaId, bool isDayparts)
    {
        string repoRoot = RepoPaths.FindRepoRoot();
        string path = Path.Combine(RepoPaths.PersonaSmokeDataDirectory(repoRoot), personaId, "menuMode.json");
        bool exists = File.Exists(path);

        if (!isDayparts)
        {
            return null;
        }

        Assert.True(exists,
            $"persona '{personaId}' declares features.dayparts:true in its own persona.json, but " +
            $"'{path}' does not exist. Add menuMode.json with breakfastItem, lunchItem, and " +
            "allDayItem values sourced from this same pack's own menu/menuItems.json.");

        Vectors? vectors = JsonSerializer.Deserialize<Vectors>(File.ReadAllText(path), s_options);
        Assert.True(vectors is not null, $"'{path}' deserialized to null.");
        return vectors;
    }
}

file sealed class RealPackMenuModeFixture(string personaId) : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(DateTimeOffset.Parse("2026-10-02T21:30:00Z"));
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class RealPackMenuModeConformanceTests
{
    public static TheoryData<string> DiscoveredPersonaIds()
    {
        var data = new TheoryData<string>();
        foreach (string id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public async Task Discovered_dayparts_pack_honors_its_own_menu_mode_vectors(string personaId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        bool isDayparts = PersonaDaypartsFeature.Read(personasDir, personaId);
        PersonaMenuModeVectors.Vectors? vectors = PersonaMenuModeVectors.For(personaId, isDayparts);
        if (vectors is null)
        {
            return;
        }

        await using var fixture = new RealPackMenuModeFixture(personaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            await AssertItemAcceptedAsync(fixture, personaId, "breakfast", vectors.BreakfastItem, ct);
            await AssertItemAcceptedAsync(fixture, personaId, "lunch", vectors.LunchItem, ct);
            foreach (PersonaMenuModeVectors.ItemVector item in vectors.AllDayItemVectors)
            {
                await AssertItemAcceptedAsync(fixture, personaId, "breakfast", item, ct);
                await AssertItemAcceptedAsync(fixture, personaId, "lunch", item, ct);
            }
            await AssertItemRejectedAsync(fixture, personaId, "lunch", vectors.BreakfastItem, ct);
            await AssertItemRejectedAsync(fixture, personaId, "breakfast", vectors.LunchItem, ct);
            await AssertSearchFilterAsync(fixture, personaId, "breakfast", ct);
        });
    }

    private static async Task AssertItemAcceptedAsync(
        ConformanceFixture fixture,
        string personaId,
        string mode,
        PersonaMenuModeVectors.ItemVector item,
        CancellationToken ct)
    {
        (RealtimeBrowserClient? browser, FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId, mode: mode);
        await using RealtimeBrowserClient _ = browser;

        ToolCallResult result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", item.Name, item.Size, 1, item.MenuPrice)],
            roundTripIndex, ct);

        JsonElement order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        OrderScenarioHelpers.AssertMoneyEqual(item.MenuPrice, OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
            $"A session bound to {mode} mode must accept '{item.Name}' at its own menu price.");
    }

    private static async Task AssertItemRejectedAsync(
        ConformanceFixture fixture,
        string personaId,
        string mode,
        PersonaMenuModeVectors.ItemVector item,
        CancellationToken ct)
    {
        (RealtimeBrowserClient? browser, FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId, mode: mode);
        await using RealtimeBrowserClient _ = browser;

        ToolCallResult rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = item.Name,
                size = item.Size,
                quantity = 1,
                price = item.MenuPrice,
            }),
            "call_real_pack_add_out_of_mode", roundTripIndex, ct, toClient: false);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText, expectedReason: "item_out_of_mode", expectedItemName: item.Name);

        ToolCallResult result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_real_pack_get_after_out_of_mode", rejected.RoundTripIndex, ct);
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
    }

    private static async Task AssertSearchFilterAsync(
        ConformanceFixture fixture,
        string personaId,
        string mode,
        CancellationToken ct)
    {
        (RealtimeBrowserClient? browser, FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId, mode: mode);
        await using RealtimeBrowserClient _ = browser;

        string query = $"real pack menu mode filter {personaId} {mode}";
        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            JsonSerializer.Serialize(new { query }),
            "call_real_pack_menu_mode_search", roundTripIndex, ct, toClient: false);

        RecordedFrame request = fixture.Search.ReceivedRequests.Snapshot().Single(f =>
            f.Json.TryGetProperty("search", out JsonElement s) && s.GetString() == query);
        string? filter = request.Json.GetProperty("filter").GetString();
        Assert.Equal($"menuPeriod eq '{mode}' or menuPeriod eq 'allDay' or menuPeriod eq ''", filter);
    }
}
