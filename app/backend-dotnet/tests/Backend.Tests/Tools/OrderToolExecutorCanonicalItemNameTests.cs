using System.Text.Json;
using Backend.Personas;
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
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS Bite Treats", "10 count", 1, 3.99m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("ZORBS\u00ae Bite Treats", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Adding_the_same_item_with_different_spellings_merges_into_one_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS\u00ae Bite Treats", "10 count", 1, 3.99m), ct);
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS Bite Treats", "10 count", 1, 3.99m), ct);

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
        await executor.ExecuteAsync("update_order", Args("add", "ZORBS\u00ae Bite Treats", "10 count", 1, 3.99m), ct);
        await executor.ExecuteAsync("update_order", Args("remove", "ZORBS Bite Treats", "10 count", 1), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Empty(items);
    }

    // ── B2 (Rick's PR #326 review): a canonical name that ITSELF contains a paren group ──
    // "Zeta Snack Mix (Family Size)" mirrors a shipped pack's own paren-named item (e.g. a
    // "Milk Jug (1%) - White"-style name) without coupling this proof to any one real pack.

    [Fact]
    public async Task Add_with_the_exact_canonical_paren_name_stores_it_unchanged_not_duplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "Zeta Snack Mix (Family Size)", "regular", 1, 3.49m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("Zeta Snack Mix (Family Size)", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Add_with_the_paren_less_spelling_merges_into_the_same_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "Zeta Snack Mix (Family Size)", "regular", 1, 3.49m), ct);
        await executor.ExecuteAsync("update_order", Args("add", "Zeta Snack Mix", "regular", 1, 3.49m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("Zeta Snack Mix (Family Size)", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task A_genuine_customization_stacks_on_top_of_the_canonical_paren_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync(
            "update_order", Args("add", "Zeta Snack Mix (Family Size) (Extra Spicy)", "regular", 1, 3.49m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("Zeta Snack Mix (Family Size) (Extra Spicy)", items[0].GetProperty("item").GetString());
    }
}

/// <summary>
/// #325 (B3, Rick's PR #326 review): mirrors test_tool_calling.py's shipped-pack canonical-name
/// tests (bundle/combo add, merge, modify-via-alias-with-a-size-change, remove) against the SAME
/// real shipped pack that carries this combo (found by menu data, not by id) -- not just the "test-zeta" fixture -- so the C# suite covers
/// the exact same scenario classes the Python suite does, including a genuine combo/bundle item
/// and a real alias (<see cref="Backend.Personas.MenuCatalog.ResolveMenuItem"/>'s own alias map).
/// </summary>
public sealed class OrderToolExecutorCanonicalItemNameSonicTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;
    private const string Bundle = "SuperSONIC\u00ae Double Cheeseburger Combo";
    private const string BundleUnmarked = "SuperSONIC Double Cheeseburger Combo";

    private static OrderToolExecutor NewExecutor()
    {
        // Brand-neutral: locate the shipped pack whose own menu carries this combo, instead of naming it.
        var catalog = PersonaCatalog.Load();
        var persona = catalog.Ids.Select(catalog.Get)
            .First(p => Backend.Ordering.PersonaOrderFactory.GetMenuCatalog(p).ResolveMenuItem(Bundle) is not null);
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
    public async Task Add_bundle_combo_item_with_the_unmarked_spelling_stores_the_canonical_marked_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", BundleUnmarked, "standard", 1, 10.19m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal(Bundle, items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Adding_the_same_bundle_combo_with_different_spellings_merges_into_one_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", Bundle, "standard", 1, 10.19m), ct);
        await executor.ExecuteAsync("update_order", Args("add", BundleUnmarked, "standard", 1, 10.19m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(Bundle, items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Modify_with_an_alias_and_a_size_change_resolves_and_stores_the_canonical_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "Coke", "medium", 1, 2.49m), ct);
        await executor.ExecuteAsync("update_order", Args("modify", "Coke", "large", 1, 2.99m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("Coca-Cola\u00ae", items[0].GetProperty("item").GetString());
        Assert.Equal("large", items[0].GetProperty("size").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task Remove_with_unmarked_name_matches_a_line_stored_under_the_marked_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", Bundle, "standard", 1, 10.19m), ct);
        await executor.ExecuteAsync("update_order", Args("remove", BundleUnmarked, "standard", 1), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Empty(items);
    }

    // ── B2 against a REAL menu item whose own canonical name contains parens ──

    [Fact]
    public async Task Add_with_a_real_menu_items_own_parenthetical_name_is_not_duplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "Milk Jug (1%) - White", "standard", 1, 1.39m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal("Milk Jug (1%) - White", items[0].GetProperty("item").GetString());
    }

    [Fact]
    public async Task Add_with_a_real_menu_items_paren_less_spelling_merges_into_the_same_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", "Milk Jug (1%) - White", "standard", 1, 1.39m), ct);
        await executor.ExecuteAsync("update_order", Args("add", "Milk Jug - White", "standard", 1, 1.39m), ct);

        var items = await GetOrderItemsAsync(executor, ct);
        Assert.Single(items);
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("Milk Jug (1%) - White", items[0].GetProperty("item").GetString());
    }
}
