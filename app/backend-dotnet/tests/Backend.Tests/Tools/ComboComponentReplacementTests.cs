using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Tools;

namespace Backend.Tests.Tools;

public sealed class ComboComponentReplacementTests
{
    private const string Bundle = "SuperSONIC® Double Cheeseburger Combo (Pickles)";
    private const string OldDrink = "Rainbow Slush";
    private const string NewDrink = "Medium Cherry Flavor Bubble Slush";

    private static (OrderToolExecutor Executor, OrderState Order, MenuCatalog Menu) NewExecutor()
    {
        var catalog = PersonaCatalog.Load();
        Persona persona = catalog.Ids.Select(catalog.Get).Single(p =>
            PersonaOrderFactory.GetMenuCatalog(p).ResolveMenuItem(OldDrink) is not null);
        MenuCatalog menu = PersonaOrderFactory.GetMenuCatalog(persona);
        OrderState order = PersonaOrderFactory.CreateOrderState(persona);
        return (new OrderToolExecutor(order, menu, null, 10, 25, "lunch"), order, menu);
    }

    private static Task<ToolResult> Call(
        OrderToolExecutor executor, string action, string item, string size = "medium",
        string? original = null, int quantity = 1, int? comboIndex = null) =>
        executor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
        {
            action,
            item_name = item,
            size,
            quantity,
            original_item_name = original,
            combo_index = comboIndex,
        }), TestContext.Current.CancellationToken);

    private static async Task Seed(OrderToolExecutor executor, int quantity = 1)
    {
        await Call(executor, "add", Bundle, "standard", quantity: quantity);
        await Call(executor, "add", "Tots", quantity: quantity);
        await Call(executor, "add", OldDrink, "standard", quantity: quantity);
    }

    [Theory]
    [InlineData(NewDrink, "standard", 0)]
    [InlineData("Cherry Slush", "large", 60)]
    public async Task ReplacementChangesOnlyIncludedDrinkAndUsesMenuPricing(string replacement, string size, int expectedUpchargeCents)
    {
        (OrderToolExecutor executor, OrderState order, _) = NewExecutor();
        await Seed(executor);
        ToolResult result = await Call(executor, "replace", replacement, size, OldDrink);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        OrderItem combo = Assert.Single(order.Items);
        Assert.Equal(Bundle, combo.Item);
        Assert.Contains("Medium Tots", combo.Display);
        Assert.Contains(replacement, combo.Display);
        Assert.DoesNotContain(OldDrink, combo.Display);
        decimal upcharge = expectedUpchargeCents / 100m;
        Assert.Equal(10.19m + upcharge, order.Summary.Total);
        Assert.True(order.GetComboRequirements().IsComplete);
        Assert.Contains("Replaced", result.ToText());
        Assert.DoesNotContain(OldDrink, order.Summary.SpokenReadBack);
        Assert.Contains(replacement, order.Summary.SpokenReadBack);
        Assert.Equal(OrderSummaryJson.Serialize(order.Summary), result.ToClientText());
        ToolResult readback = await executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);
        Assert.Equal(result.ToClientText(), readback.ToClientText());
    }

    [Theory]
    [InlineData(NewDrink, "standard", "", "invalid_replacement")]
    [InlineData(NewDrink, "standard", "Cherry Limeade", "replacement_target_not_found")]
    [InlineData("Tots", "medium", OldDrink, "incompatible_replacement")]
    [InlineData("Invented Slush", "medium", OldDrink, "not_on_menu")]
    [InlineData(NewDrink, "impossible", OldDrink, "size_not_available")]
    [InlineData("Vanilla Classic Shake", "medium", OldDrink, "machine_unavailable")]
    public async Task RejectedReplacementPreservesTicket(string item, string size, string original, string reason)
    {
        (OrderToolExecutor executor, OrderState order, _) = NewExecutor();
        await Seed(executor);
        string before = OrderSummaryJson.Serialize(order.Summary);
        ToolResult result = await Call(executor, "replace", item, size, original);
        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        Assert.Equal(reason, JsonElement.Parse(result.ToText()).GetProperty("reason").GetString());
        Assert.Equal(before, OrderSummaryJson.Serialize(order.Summary));
    }

    [Fact]
    public async Task ExplicitReplacementDoesNotChangeOtherPhysicalMealUnitsOrStandaloneDrinks()
    {
        (OrderToolExecutor executor, OrderState order, _) = NewExecutor();
        await Seed(executor, 2);
        await Call(executor, "add", OldDrink, "standard");
        await Call(executor, "replace", "Cherry Slush", "large", OldDrink);
        Assert.Equal(3, order.Items.Sum(i => i.Quantity));
        Assert.Contains(order.Items, i => i.Item == OldDrink);
        Assert.Contains(order.Items, i => i.Item == Bundle && i.Display.Contains(OldDrink, StringComparison.Ordinal));
        Assert.Contains(order.Items, i => i.Item == Bundle && i.Display.Contains("Cherry Slush", StringComparison.Ordinal));
        Assert.DoesNotContain(order.Items, i => i.Item == "Cherry Slush");
    }

    [Fact]
    public void LoadedPromptMakesFinishIntentOverrideOpenOrderQuestions()
    {
        var catalog = PersonaCatalog.Load();
        foreach (Persona persona in catalog.Ids.Select(catalog.Get).Where(p => p.SpeechStyle == "fullReadback"))
        {
            var loader = new PromptLoader(Path.Combine(RepoRootLocator.Find(), "personas"), persona.Id);
            Assert.Contains("FINISH INTENT OVERRIDES", loader.SystemPrompt);
            Assert.Contains("Nope, that'll be it", loader.SystemPrompt);
            Assert.Contains("Do NOT ask 'Anything else?'", loader.SystemPrompt);
            Assert.Contains("If the guest repeats that they are done and the order has not changed", loader.SystemPrompt);
            Assert.Contains("do not repeat the read-back or call get_order again", loader.SystemPrompt);
        }
    }

    [Fact]
    public async Task AmbiguousComboLinesRequireSelectionAndDoNotMutateBeforeClarification()
    {
        (OrderToolExecutor executor, OrderState order, _) = NewExecutor();
        await Seed(executor);
        await Call(executor, "add", Bundle + " (No Onion)", "standard");
        await Call(executor, "add", "Tots");
        await Call(executor, "add", OldDrink, "standard");
        string before = OrderSummaryJson.Serialize(order.Summary);
        ToolResult rejected = await Call(executor, "replace", NewDrink, "standard", OldDrink);
        Assert.Equal("ambiguous_replacement", JsonElement.Parse(rejected.ToText()).GetProperty("reason").GetString());
        Assert.Equal(before, OrderSummaryJson.Serialize(order.Summary));
        ToolResult selected = await Call(executor, "replace", NewDrink, "standard", OldDrink, comboIndex: 1);
        Assert.Equal(ToolResultDirection.ToBoth, selected.Destination);
        Assert.Contains(OldDrink, order.Items[0].Display);
        Assert.Contains(NewDrink, order.Items[1].Display);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task ReplacementRequiresExactlyOneComponent(int quantity)
    {
        (OrderToolExecutor executor, OrderState order, _) = NewExecutor();
        await Seed(executor);
        string before = OrderSummaryJson.Serialize(order.Summary);
        ToolResult result = await Call(executor, "replace", NewDrink, "standard", OldDrink, quantity);
        Assert.Equal("invalid_replacement", JsonElement.Parse(result.ToText()).GetProperty("reason").GetString());
        Assert.Equal(before, OrderSummaryJson.Serialize(order.Summary));
    }
}
