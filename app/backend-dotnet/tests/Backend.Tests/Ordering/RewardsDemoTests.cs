using System.Text.Json.Nodes;
using Backend.Ordering;
using Backend.Personas;
using Backend.Sessions;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Ordering;

public sealed class RewardsDemoTests
{
    private static OrderState NewOrder() => PersonaOrderFactory.CreateOrderState(
        PersonaCatalog.Load(personasDir: Path.Combine(RepoRootLocator.Find(), "personas")).Get("tacobell"));

    private static JsonObject Rewards(OrderState order) => order.Summary.RewardsDemo!;
    private static void Add(OrderState order, string item = "Crunchy Taco", int quantity = 1) =>
        order.HandleOrderUpdate("add", item, "standard", quantity, 999m);

    [Theory]
    [InlineData(null)]
    [InlineData("123456")]
    [InlineData("demo-plus")]
    [InlineData("DEMO-UNKNOWN")]
    [InlineData("")]
    public void OnlyExactFictionalCodesAreAccepted(string? code)
    {
        var order = NewOrder();
        order.RewardsAction("check_in", code);
        Assert.False(Rewards(order)["checkedIn"]!.GetValue<bool>());
        Assert.Equal(0, Rewards(order)["balance"]!.GetValue<int>());
        Assert.StartsWith("Invalid demo code", Rewards(order)["message"]!.GetValue<string>());
    }

    [Fact]
    public void DiscountTaxAndAwardsAreServerAuthoritativeAndIdempotent()
    {
        var order = NewOrder();
        Add(order, quantity: 2);
        Add(order, "Crunchwrap Supreme");
        order.RewardsAction("check_in", "DEMO-PLUS");
        Assert.True(Rewards(order)["canRedeem"]!.GetValue<bool>());
        order.RewardsAction("redeem", null);
        Assert.Equal(7.98m, order.Summary.Total);
        Assert.Equal("$0.64", order.Summary.TaxDisplay);
        Assert.Equal("$8.62", order.Summary.FinalTotalDisplay);
        Assert.Equal(79, Rewards(order)["pointsToEarn"]!.GetValue<int>());
        Assert.Equal(1.99m, Rewards(order)["discount"]!.GetValue<decimal>());
        order.RewardsAction("redeem", null);
        order.RewardsAction("complete", null);
        Assert.Equal(329, Rewards(order)["balance"]!.GetValue<int>());
        foreach (var action in new[] { "redeem", "complete", "check_in", "unredeem" })
        {
            order.RewardsAction(action, "DEMO-PLUS");
            Assert.Equal(329, Rewards(order)["balance"]!.GetValue<int>());
        }
        Assert.Throws<InvalidOperationException>(() => Add(order));
    }

    [Fact]
    public void FutureEarningsCannotBeSpentAndRemovingAnItemReleasesReservation()
    {
        var order = NewOrder();
        Add(order, quantity: 10);
        order.RewardsAction("check_in", "DEMO-NEW");
        order.RewardsAction("redeem", null);
        Assert.False(Rewards(order)["redeemed"]!.GetValue<bool>());
        Assert.Equal(199, Rewards(order)["pointsToEarn"]!.GetValue<int>());
        var other = NewOrder();
        Add(other);
        other.RewardsAction("check_in", "DEMO-PLUS");
        other.RewardsAction("redeem", null);
        other.HandleOrderUpdate("remove", "Crunchy Taco", "standard", 1, null);
        Assert.False(Rewards(other)["redeemed"]!.GetValue<bool>());
        Assert.Equal(500, Rewards(other)["balance"]!.GetValue<int>());
        Assert.Equal(0m, other.Summary.Total);
    }

    [Fact]
    public void ResetKeepsCheckinAndBalanceButClearsAwardAndOrder()
    {
        var order = NewOrder();
        Add(order);
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.RewardsAction("complete", null);
        order.RewardsAction("reset", null);
        Assert.Empty(order.Items);
        Assert.Equal(519, Rewards(order)["balance"]!.GetValue<int>());
        Assert.Equal(0, Rewards(order)["pointsAwarded"]!.GetValue<int>());
        Assert.False(Rewards(order)["completed"]!.GetValue<bool>());
        order.RewardsAction("check_in", "DEMO-NEW");
        Assert.Equal(519, Rewards(order)["balance"]!.GetValue<int>());
        Assert.Equal(0, Rewards(NewOrder())["balance"]!.GetValue<int>());
    }

    [Fact]
    public void EmptyUncheckedAndModifiedOrdersCannotClaimReward()
    {
        var order = NewOrder();
        order.RewardsAction("complete", null);
        Assert.False(Rewards(order)["completed"]!.GetValue<bool>());
        Add(order, "Crunchy Taco (No Lettuce)");
        order.RewardsAction("complete", null);
        Assert.False(Rewards(order)["completed"]!.GetValue<bool>());
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.RewardsAction("redeem", null);
        Assert.Equal(0m, Rewards(order)["discount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ConcurrentCompletionSpendsAndAwardsOnlyOnce()
    {
        var order = NewOrder();
        Add(order, quantity: 2);
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.RewardsAction("redeem", null);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => order.RewardsAction("complete", null))));
        Assert.Equal(269, Rewards(order)["balance"]!.GetValue<int>());
    }

    [Fact]
    public void ExtensionReturnsExistingTicketContractAndOtherPersonasRemainDisabled()
    {
        var order = NewOrder();
        var persona = PersonaCatalog.Load().Get("tacobell");
        var executor = new OrderToolExecutor(order, PersonaOrderFactory.GetMenuCatalog(persona), null, 10, 25);
        var frame = RewardsDemoFrames.Handle(executor, new JsonObject { ["action"] = "check_in", ["code"] = "DEMO-PLUS" });
        Assert.Equal("extension.middle_tier_tool_response", frame["type"]!.GetValue<string>());
        Assert.Null(frame["previous_item_id"]);
        var ticket = JsonNode.Parse(frame["tool_result"]!.GetValue<string>())!;
        Assert.True(ticket["rewardsDemo"]!["checkedIn"]!.GetValue<bool>());
        var neutral = PersonaOrderFactory.CreateOrderState(PersonaCatalog.Load().Default);
        Assert.False(neutral.RewardsAction("check_in", "DEMO-PLUS"));
        Assert.Null(neutral.Summary.RewardsDemo);
    }
}
