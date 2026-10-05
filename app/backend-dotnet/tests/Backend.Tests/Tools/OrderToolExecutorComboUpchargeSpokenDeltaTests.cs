using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// #313 (Rick's re-review, item 2): <c>update_order</c>'s model-facing delta text (the string the
/// realtime model actually reads, <see cref="ToolResult.ToText"/>) must speak a combo component
/// upcharge in words (<see cref="Money.FormatMoneySpoken"/>), never the digit/<c>$</c> display --
/// on BOTH delta shapes that embed <see cref="OrderUpdateResult.ComboComponentUpchargeDisplay"/>
/// (OrderToolExecutor's private BuildDeltaText's "absorbed" and "resized" branches): a fresh `add`
/// that fills a still-vacant combo slot above the included size, and a later `modify`
/// that resizes an already-absorbed component. Against the real, shipped "sonic" pack (the same
/// persona/items <see cref="Backend.Tests.Ordering.ComponentUpchargeBundleTests"/> already uses
/// for the equivalent OrderState-level coverage) -- no test-only fixture needed since this is
/// pure pricing/wording behavior, not any persona-specific lexicon.
/// </summary>
public sealed class OrderToolExecutorComboUpchargeSpokenDeltaTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;
    private const string PersonaId = "sonic";
    private const string Bundle = "SuperSONIC® Double Cheeseburger Combo";
    private const string Side = "Tots";
    private const string Drink = "Cherry Limeade";

    private static OrderToolExecutor NewExecutor()
    {
        var catalog = PersonaCatalog.Load(personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        var persona = catalog.Get(PersonaId);
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        return new OrderToolExecutor(order, menu, promptLoader: null, MaxItemQuantity, MaxOrderItems, menuMode: null);
    }

    private static JsonElement Args(string action, string itemName, string size, int quantity, decimal price) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["item_name"] = itemName,
            ["size"] = size,
            ["quantity"] = quantity,
            ["price"] = price,
        });

    [Fact]
    public async Task AbsorbedLargeDrinkUpchargeDelta_HasNoDollarSign()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", Bundle, "standard", 1, 10.19m), ct);
        await executor.ExecuteAsync("update_order", Args("add", Side, "medium", 1, 2.79m), ct);

        var result = await executor.ExecuteAsync("update_order", Args("add", Drink, "large", 1, 3.39m), ct);

        var text = result.ToText();
        Assert.Contains("fifty cents upcharge", text);
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public async Task ResizedAbsorbedComponentUpchargeDelta_HasNoDollarSign()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", Bundle, "standard", 1, 10.19m), ct);
        await executor.ExecuteAsync("update_order", Args("add", Side, "medium", 1, 2.79m), ct);
        await executor.ExecuteAsync("update_order", Args("add", Drink, "medium", 1, 2.89m), ct);

        var result = await executor.ExecuteAsync("update_order", Args("modify", Drink, "large", 1, 3.39m), ct);

        var text = result.ToText();
        Assert.Contains("fifty cents upcharge", text);
        Assert.DoesNotContain("$", text);
    }
}
