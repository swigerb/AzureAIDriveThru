using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

public sealed record WholeBundleGoldenStep(string Action, string Item, string Size, int Quantity, decimal Price);

public sealed record WholeBundleGoldenVector(
    string Description,
    IReadOnlyList<WholeBundleGoldenStep> Steps,
    decimal ExpectedTotal,
    int ExpectedItemCount,
    bool? ExpectedComplete = null,
    string? ExpectedItemSize = null,
    IReadOnlyList<string>? ExpectedComponents = null,
    IReadOnlyList<string>? ForbiddenComponents = null,
    IReadOnlyList<string>? ExpectedDisplayIncludes = null);

public sealed record WholeBundleGoldenData(
    string PersonaId,
    IReadOnlyList<WholeBundleGoldenVector> MealSizeTotals,
    IReadOnlyList<WholeBundleGoldenVector> DefaultSizeCases,
    WholeBundleGoldenVector StandardOnlyMealPlusDrink,
    WholeBundleGoldenVector QuantityTwoSplit)
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static WholeBundleGoldenData Load(string repoRoot, string personaId)
    {
        string path = Path.Combine(
            repoRoot, "tests", "conformance", "testdata", "personas", personaId, "wholeBundleSize.json");
        return JsonSerializer.Deserialize<WholeBundleGoldenData>(File.ReadAllText(path), s_options)
            ?? throw new InvalidDataException($"Whole-bundle golden data at '{path}' deserialized to null.");
    }

    public IEnumerable<WholeBundleGoldenVector> AllVectors() =>
        MealSizeTotals.Concat(DefaultSizeCases).Concat([StandardOnlyMealPlusDrink, QuantityTwoSplit]);
}

file sealed class PackOwnedWholeBundleGoldenFixture(string personaId) : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(HappyHourJustBeforeOpenFixture.Instant);
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class PackOwnedWholeBundleGoldenConformanceTests
{
    public static TheoryData<string> PersonaIdsWithWholeBundleGoldenData()
    {
        var data = new TheoryData<string>();
        string root = RepoPaths.FindRepoRoot();
        foreach (string dir in Directory.EnumerateDirectories(RepoPaths.PersonaSmokeDataDirectory(root)))
        {
            if (File.Exists(Path.Combine(dir, "wholeBundleSize.json")))
            {
                data.Add(Path.GetFileName(dir));
            }
        }
        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(PersonaIdsWithWholeBundleGoldenData))]
    public async Task Pack_owned_whole_bundle_vectors_match_expected_totals(string personaId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var golden = WholeBundleGoldenData.Load(RepoPaths.FindRepoRoot(), personaId);

        await using var fixture = new PackOwnedWholeBundleGoldenFixture(golden.PersonaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(async () =>
        {
            foreach (WholeBundleGoldenVector vector in golden.AllVectors())
            {
                (RealtimeBrowserClient? browser, Fakes.FakeRealtimeConnection? connection, int roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
                    fixture, ct, persona: golden.PersonaId);
                await using RealtimeBrowserClient _ = browser;

                var steps = vector.Steps.Select(s => (s.Action, s.Item, s.Size, s.Quantity, s.Price)).ToList();
                ToolCallResult result = await OrderScenarioHelpers.RunOrderStepsAsync(
                    connection, browser, steps, roundTripIndex, ct, callIdPrefix: "call_pack_whole_bundle");

                OrderScenarioHelpers.AssertMoneyEqual(
                    vector.ExpectedTotal,
                    OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                    $"{golden.PersonaId} vector '{vector.Description}' expected total {vector.ExpectedTotal}.");
                Assert.Equal(vector.ExpectedItemCount, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));

                if (vector.ExpectedItemSize is { Length: > 0 } expectedSize)
                {
                    using var order = JsonDocument.Parse(result.ToolResultJson!);
                    Assert.Equal(expectedSize, order.RootElement.GetProperty("items")[0].GetProperty("size").GetString());
                }
                if (vector.ExpectedDisplayIncludes is { Count: > 0 } displayIncludes)
                {
                    using var order = JsonDocument.Parse(result.ToolResultJson!);
                    string? display = order.RootElement.GetProperty("items")[0].GetProperty("display").GetString();
                    foreach (string expected in displayIncludes)
                    {
                        Assert.Contains(expected, display);
                    }
                }
                if (vector.ExpectedComponents is { Count: > 0 } expectedComponents)
                {
                    using var order = JsonDocument.Parse(result.ToolResultJson!);
                    var components = order.RootElement.GetProperty("items")[0].GetProperty("components")
                        .EnumerateArray()
                        .Select(component => component.GetString() ?? "")
                        .ToList();
                    Assert.Equal(expectedComponents, components);
                }
                if (vector.ForbiddenComponents is { Count: > 0 } forbiddenComponents)
                {
                    using var order = JsonDocument.Parse(result.ToolResultJson!);
                    var components = order.RootElement.GetProperty("items")[0].GetProperty("components")
                        .EnumerateArray()
                        .Select(component => component.GetString() ?? "")
                        .ToList();
                    foreach (string forbidden in forbiddenComponents)
                    {
                        Assert.DoesNotContain(forbidden, components);
                    }
                }
            }
        });
    }
}
