using System.Text.Json;
using Backend.Ordering;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// #313 (Rick's re-review, item 2): <c>update_order</c>'s model-facing delta text (the string the
/// realtime model actually reads, <see cref="ToolResult.ToText"/>) must speak a combo component
/// upcharge in words (<see cref="Money.FormatMoneySpoken"/>), never the digit/<c>$</c> display --
/// on BOTH delta shapes that embed <see cref="OrderUpdateResult.ComboComponentUpchargeDisplay"/>
/// (OrderToolExecutor's private BuildDeltaText's "absorbed" and "resized" branches): a fresh `add`
/// that fills a still-vacant combo slot above the included size, and a later `modify`
/// that resizes an already-absorbed component. Against <see cref="EtaFixture"/>'s "test-eta"
/// pack -- the only fixture pack declaring <c>bundles.resizeRule: "componentUpcharge"</c> -- since
/// this is pure pricing/wording behavior, not any persona-specific lexicon, so no real persona id
/// is needed in this file's own source.
/// </summary>
public sealed class OrderToolExecutorComboUpchargeSpokenDeltaTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    private static OrderToolExecutor NewExecutor()
    {
        var persona = EtaFixture.Load();
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
        await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Bundle, "standard", 1, 7.99m), ct);
        await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Side, "medium", 1, 2.79m), ct);

        var result = await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Drink, "large", 1, 3.39m), ct);

        var text = result.ToText();
        Assert.Contains("fifty cents upcharge", text);
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public async Task ResizedAbsorbedComponentUpchargeDelta_HasNoDollarSign()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = NewExecutor();
        await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Bundle, "standard", 1, 7.99m), ct);
        await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Side, "medium", 1, 2.79m), ct);
        await executor.ExecuteAsync("update_order", Args("add", EtaFixture.Drink, "medium", 1, 2.89m), ct);

        var result = await executor.ExecuteAsync("update_order", Args("modify", EtaFixture.Drink, "large", 1, 3.39m), ct);

        var text = result.ToText();
        Assert.Contains("fifty cents upcharge", text);
        Assert.DoesNotContain("$", text);
    }
}
