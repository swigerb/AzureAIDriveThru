using System.Text.Json;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// #325 ("Orders store the model's spelling of item names, not the menu's canonical name"): after
/// <see cref="Backend.Personas.MenuCatalog.ResolveMenuItem"/> resolves an add/modify/remove's own
/// <c>item_name</c> against the on-menu gate, <see cref="OrderToolExecutor"/> must store/match the
/// MENU's own canonical spelling -- never whatever casing or trademark marks the model's own tool
/// call happened to use for that turn. Uses "test-zeta"'s "ZORBS&#174; Bite Treats" -- the exact
/// fixture item #325's own write-up names -- so this proof is independent of any one real pack's
/// own marked item names. C# port of
/// test_bundle_and_extras_engine.py's <c>CanonicalItemNameTests</c>.
/// </summary>
public sealed class OrderToolExecutorCanonicalItemNameTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    private static OrderToolExecutor NewExecutor()
    {
        var persona = ZetaFixture.Load();
        var menu = Backend.Ordering.PersonaOrderFactory.GetMenuCatalog(persona);
        var order = Backend.Ordering.PersonaOrderFactory.CreateOrderState(persona);
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

    private static async Task<List<JsonElement>> GetOrderItemsAsync(OrderToolExecutor executor, CancellationToken ct)
    {
        var summary = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }), ct);
        var client = JsonDocument.Parse(summary.ToClientText());
        return client.RootElement.GetProperty("items").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Add_WithTheUnmarkedSpelling_StoresTheMenusCanonicalMarkedName()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS Bite Treats", "regular", 1, 2.99m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("ZORBS\u00ae Bite Treats", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Adding_the_same_item_with_different_spellings_merges_into_one_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS\u00ae Bite Treats", "regular", 1, 2.99m), ct);
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS Bite Treats", "regular", 1, 2.99m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("ZORBS\u00ae Bite Treats", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Remove_with_the_unmarked_spelling_matches_a_line_added_with_the_marked_spelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS\u00ae Bite Treats", "regular", 1, 2.99m), ct);
        await executor.ExecuteAsync("update_order", Args("remove", "ZORBS Bite Treats", "regular", 1), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Empty(items);
    }
}
