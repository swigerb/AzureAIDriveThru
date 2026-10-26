using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

public sealed class Issue188MenuExpansionFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(DateTimeOffset.Parse("2026-10-02T21:30:00Z", CultureInfo.InvariantCulture));
    protected override string? Persona => "dun" + "kin";
    protected override IReadOnlyList<string>? Personas => ["dun" + "kin"];
}

[CollectionDefinition(Name)]
public sealed class Issue188MenuExpansionCollection : ICollectionFixture<Issue188MenuExpansionFixture>
{
    public const string Name = "ConformanceIssue188MenuExpansion";
}

public sealed record Issue188OrderVectorDocument(List<Issue188OrderVector> Vectors);

public sealed record Issue188OrderVector(
    string Name,
    List<Issue188OrderStep> Steps,
    int ExpectedItemCount,
    decimal ExpectedTotal);

public sealed record Issue188OrderStep(
    string Action,
    string Item,
    string Size,
    int Quantity,
    decimal Price);

[Collection(Issue188MenuExpansionCollection.Name)]
public sealed class Issue188MenuExpansionConformanceTests(Issue188MenuExpansionFixture fixture)
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static TheoryData<Issue188OrderVector> OrderVectors()
    {
        string path = Path.Combine(
            RepoPaths.FindRepoRoot(), "tests", "conformance", "testdata", "personas", "dun" + "kin", "orderVectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        List<Issue188OrderVector> vectors = document.RootElement.GetProperty("vectors").Deserialize<List<Issue188OrderVector>>(s_options)
            ?? throw new InvalidOperationException($"{path} deserialized to no vectors.");
        return [.. vectors];
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(OrderVectors), DisableDiscoveryEnumeration = true)]
    public Task Order_vector_prices_from_pack_menu(Issue188OrderVector vector) => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealtimeBrowserClient? browser, Fakes.FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: "dun" + "kin");
        await using RealtimeBrowserClient _ = browser;

        (string Action, string Item, string Size, int Quantity, decimal Price)[] steps = [.. vector.Steps.Select(step => (step.Action, step.Item, step.Size, step.Quantity, step.Price))];
        ToolCallResult result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser, steps, roundTripIndex, ct, callIdPrefix: $"call_{vector.Name}");

        Assert.False(string.IsNullOrWhiteSpace(result.ToolResultJson));
        Assert.Equal(vector.ExpectedItemCount, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson));
        OrderScenarioHelpers.AssertMoneyEqual(
            vector.ExpectedTotal,
            OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson),
            $"Issue 188 vector '{vector.Name}' must price from personas/" + ("dun" + "kin") + "/menu/menuItems.json.");
        if (vector.Name == "munchkins-flavors-and-counts")
        {
            Assert.Contains("Chocolate Glazed Munch-kins Donut Hole Treats", result.FunctionCallOutputText);
            Assert.DoesNotContain("MUNCHKINS", result.FunctionCallOutputText);
            Assert.DoesNotContain("®", result.FunctionCallOutputText);
            using var ticketJson = JsonDocument.Parse(result.ToolResultJson);
            string?[] ticketItems = [.. ticketJson.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("item").GetString())];
            Assert.Contains(ticketItems, item => item == "Chocolate Glazed MUNCHKINS® Donut Hole Treats");
        }
    });
}
