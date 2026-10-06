using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.Ordering;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

[Collection(ClockHookTestCollection.Name)]
public sealed class OrderToolExecutorHappyHourBannerTests : IDisposable
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string FixedNowEnv = "CONFORMANCE_FIXED_NOW";
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(FixedNowEnv, null);
    }

    private static void FreezeAt(string isoInstant)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(FixedNowEnv, isoInstant);
    }

    private static OrderToolExecutor NewExecutor(Persona persona)
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        return new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems);
    }

    private static JsonElement Args(string action, string itemName, string size, int quantity, decimal? price = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["item_name"] = itemName,
            ["size"] = size,
            ["quantity"] = quantity,
        };
        if (price is { } p)
        {
            dict["price"] = p;
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    [Fact]
    public async Task StandaloneDiscountedLine_GetsHappyHourNoteNamingThatLine()
    {
        FreezeAt("2026-01-15T21:00:00Z");
        var executor = NewExecutor(DeltaFixture.Load("test-epsilon"));

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Cola", "medium", 1, 1.50m), TestContext.Current.CancellationToken);

        Assert.Contains("[EPSILON HAPPY HOUR ACTIVE]", result.ToText());
        Assert.Contains("[HAPPY HOUR DISCOUNT APPLIED TO: Medium Epsilon Cola]", result.ToText());
    }

    [Fact]
    public async Task ComboComponentOnly_GetsNoHappyHourClaimEvenWhenTheComponentItemIsDiscountable()
    {
        FreezeAt("2026-01-15T21:00:00Z");
        var executor = NewExecutor(DeltaFixture.Load("test-epsilon"));
        var ct = TestContext.Current.CancellationToken;

        var meal = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Standard Meal", "standard", 1, 3.49m), ct);
        Assert.DoesNotContain("HAPPY HOUR", meal.ToText());

        var component = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Cola", "medium", 1, 1.50m), ct);

        Assert.DoesNotContain("HAPPY HOUR", component.ToText());
        Assert.DoesNotContain("DISCOUNT APPLIED", component.ToText());
        using var client = JsonDocument.Parse(component.ToClientText());
        Assert.Equal(3.49m, client.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task OutsideWindow_GetsNoHappyHourNote()
    {
        FreezeAt("2026-01-15T19:00:00Z");
        var executor = NewExecutor(DeltaFixture.Load("test-epsilon"));

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Cola", "medium", 1, 1.50m), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("HAPPY HOUR", result.ToText());
    }

    [Fact]
    public async Task AnnounceFalse_StillDiscountsButGetsNoHappyHourNote()
    {
        FreezeAt("2026-01-15T21:00:00Z");
        var epsilon = DeltaFixture.Load("test-epsilon");
        var quiet = epsilon with
        {
            Pricing = epsilon.Pricing with
            {
                HappyHour = epsilon.Pricing.HappyHour! with { Announce = false },
            },
        };
        var executor = NewExecutor(quiet);

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Epsilon Cola", "medium", 1, 1.50m), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("HAPPY HOUR", result.ToText());
        using var client = JsonDocument.Parse(result.ToClientText());
        Assert.Equal(0.75m, client.RootElement.GetProperty("total").GetDecimal());
    }
}
