using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Ordering;

[Collection(ClockHookTestCollection.Name)]
public sealed class Issue188MenuExpansionTests : IDisposable
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

    private static Persona Pack() => PersonaCatalog.Load(personasEnv: "dun" + "kin", defaultPersonaEnv: "dun" + "kin").Get("dun" + "kin");

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

    private static (OrderToolExecutor Executor, OrderState Order, MenuCatalog Menu) NewExecutor()
    {
        var persona = Pack();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        return (new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems), order, menu);
    }

    private static void FreezeAt(string isoInstant)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(FixedNowEnv, isoInstant);
    }

    [Fact]
    public async Task LargeHotRegularCoffee_FreeModsAndPaidSwirl_ArePricedFromMenu()
    {
        FreezeAt("2026-10-02T21:30:00Z");
        var (executor, order, _) = NewExecutor();

        var coffee = await executor.ExecuteAsync(
            "update_order", Args("add", "Original Blend Coffee (Cream, Sugar)", "large", 1, 0.01m), TestContext.Current.CancellationToken);
        var swirl = await executor.ExecuteAsync(
            "update_order", Args("add", "Flavor Swirl Add-On", "standard", 1, 0.01m), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, coffee.Destination);
        Assert.Equal(ToolResultDirection.ToBoth, swirl.Destination);
        Assert.Equal(2.99m, order.Items[0].Price);
        Assert.Equal(0.75m, order.Items[1].Price);
        Assert.Equal(3.74m, order.Summary.Total);
    }

    [Fact]
    public void MediumIcedBlackCoffee_IsHappyHourDiscounted()
    {
        FreezeAt("2026-10-02T18:30:00Z");
        var order = PersonaOrderFactory.CreateOrderState(Pack());

        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 1, 0.01m);

        Assert.True(order.IsHappyHour());
        Assert.Equal(3.59m, order.Items[0].Price);
        Assert.Equal(3.59m * 0.75m, order.Summary.Total);
    }

    [Fact]
    public async Task MunchkinsFlavorCounts_AreDistinctOrderableItemsAndSizes()
    {
        FreezeAt("2026-10-02T21:30:00Z");
        var (executor, order, _) = NewExecutor();

        var glazed = await executor.ExecuteAsync(
            "update_order", Args("add", "Glazed MUNCHKINS® Donut Hole Treats", "10 count", 2, 0.01m), TestContext.Current.CancellationToken);
        var chocolate = await executor.ExecuteAsync(
            "update_order", Args("add", "Chocolate Glazed MUNCHKINS® Donut Hole Treats", "25 count", 1, 0.01m), TestContext.Current.CancellationToken);

        Assert.Equal(2, order.Items.Count);
        Assert.Equal("10 count", order.Items[0].Size);
        Assert.Equal(2, order.Items[0].Quantity);
        Assert.Equal("25 count", order.Items[1].Size);
        Assert.Equal((2 * 3.99m) + 8.99m, order.Summary.Total);
        Assert.Contains("Munch-kins Donut Hole Treats", glazed.ToText());
        Assert.Contains("Munch-kins Donut Hole Treats", chocolate.ToText());
        Assert.Contains("MUNCHKINS®", order.Items[0].Item);
        using var ticketJson = JsonDocument.Parse(chocolate.ToClientText());
        var ticketItems = ticketJson.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("item").GetString())
            .ToArray();
        Assert.Contains(ticketItems, item => item == "Glazed MUNCHKINS® Donut Hole Treats");
        Assert.Contains(ticketItems, item => item == "Chocolate Glazed MUNCHKINS® Donut Hole Treats");
        var readback = order.GetGroupedOrderForReadback();
        Assert.Contains("Glazed Munch-kins Donut Hole Treats", readback);
        Assert.DoesNotContain("MUNCHKINS", readback);
        Assert.DoesNotContain("®", readback);
    }

    [Fact]
    public void MunchkinsSpokenSubstitution_RespectsBoundariesAndTrademark()
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(Pack());

        var spoken = menu.Spoken("MUNCHKINSHIP PREMUNCHKINS MUNCHKINSON MUNCHKINS® MUNCHKINS");

        Assert.Equal("MUNCHKINSHIP PREMUNCHKINS MUNCHKINSON Munchkins Munchkins", spoken);
    }

    [Fact]
    public void HappyHourFlagsMatchPackBanner()
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(Pack());

        Assert.False(menu.IsHappyHourDiscounted("Original Blend Coffee"));
        Assert.True(menu.IsHappyHourDiscounted("Original Blend Iced Coffee"));
        Assert.True(menu.IsHappyHourDiscounted("Caramel Craze Latte"));
        Assert.True(menu.IsHappyHourDiscounted("Strawberry Dragonfruit Refresher"));
        Assert.False(menu.IsHappyHourDiscounted("Glazed Donut"));
        Assert.False(menu.IsHappyHourDiscounted("Bacon Egg & Cheese on Croissant"));
        Assert.False(menu.IsHappyHourDiscounted("Hash Browns"));
    }

    [Fact]
    public void MunchkinsSizeAliasesResolveToCountSizes()
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(Pack());
        var item = menu.ResolveMenuItem("Glazed Munchkins");

        Assert.NotNull(item);
        Assert.Equal(["10 count", "25 count", "50 count"], item!.Sizes);
        Assert.Equal("10 count", menu.CanonicalSizeKey("10-count"));
        Assert.Equal("25 count", menu.CanonicalSizeKey("25 ct"));
        Assert.Equal("50 count", menu.CanonicalSizeKey("50"));
    }
}
