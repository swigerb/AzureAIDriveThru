using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

public sealed class OrderToolExecutorMachineOverrideTests
{
    private static OrderToolExecutor CreateExecutor(Persona persona, out OrderState order)
    {
        MenuCatalog menu = PersonaOrderFactory.GetMenuCatalog(persona);
        order = PersonaOrderFactory.CreateOrderState(persona);
        return new OrderToolExecutor(order, menu, promptLoader: null, maxItemQuantity: 10, maxOrderItems: 25);
    }

    private static JsonElement UpdateOrderArgs(string itemName, string size, int quantity = 1) =>
        JsonElement.Parse($$"""
            {"action":"add","item_name":"{{itemName}}","size":"{{size}}","quantity":{{quantity}}}
            """);

    [Fact]
    public async Task UpdateOrder_accepts_an_item_when_a_down_machine_is_overridden_up()
    {
        OrderToolExecutor executor = CreateExecutor(DeltaFixture.Load("test-alpha"), out _);
        Assert.True(executor.SetMachineOverride("soda_machine", "up"));

        ToolResult result = await executor.ExecuteAsync("update_order", UpdateOrderArgs("Alpha Cola", "small"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToBoth, result.Destination);
        Assert.Contains("Alpha Cola", result.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateOrder_rejects_an_item_when_an_up_machine_is_overridden_down()
    {
        OrderToolExecutor executor = CreateExecutor(DeltaFixture.Load("test-beta"), out _);
        Assert.True(executor.SetMachineOverride("soda_machine", "down"));

        ToolResult result = await executor.ExecuteAsync("update_order", UpdateOrderArgs("Beta Root Beer", "regular"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultDirection.ToServer, result.Destination);
        var payload = JsonElement.Parse(result.ToText());
        Assert.Equal("machine_unavailable", payload.GetProperty("reason").GetString());
        Assert.Equal("Beta Root Beer", payload.GetProperty("item_name").GetString());
    }
}
