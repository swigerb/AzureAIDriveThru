using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Ordering;
using Backend.Personas;
using Backend.Sessions;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Ordering;

public sealed class RewardsDemoTests
{
    [Fact]
    public async Task NaturalTurnsKeepTotalsPrivateAndSaveSauceQuantitiesWithoutChangingRewardsMath()
    {
        var persona = PersonaCatalog.Load().Get("tacobell");
        var order = NewOrder();
        var executor = new OrderToolExecutor(order, MenuCatalog.FromPersona(persona), null, 10, 25);
        Task<ToolResult> GetOrder() => executor.ExecuteAsync("get_order",
            JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        var initial = await GetOrder();
        Assert.False(ModelRewards(initial)["checkedIn"]!.GetValue<bool>());
        Assert.Contains("only when checkedIn is true", initial.ToText());
        order.RewardsAction("check_in", "DEMO-PLUS");
        var checkedIn = ModelRewards(await GetOrder());
        Assert.True(checkedIn["checkedIn"]!.GetValue<bool>());
        Assert.Equal(500, checkedIn["balance"]!.GetValue<int>());

        foreach (var (action, item, size, quantity) in new[]
        {
            ("add", "3 Crunchy Tacos Combo", "Standard", 1),
            ("remove", "3 Crunchy Tacos Combo", "Standard", 1),
            ("add", "3 Soft Tacos Combo (No Tomatoes)", "Standard", 1),
            ("add", "Chips and Nacho Cheese Sauce", "Standard", 1),
            ("add", "Mountain Dew Baja Blast", "Large", 1),
            ("add", "Fire Sauce Packet", "Standard", 2),
        })
        {
            var result = await executor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
            {
                action, item_name = item, size, quantity,
            }), TestContext.Current.CancellationToken);
            var text = result.ToText();
            Assert.Contains("PRIVATE ORDER CONTEXT", text);
            var acknowledgement = text.Split("PRIVATE ORDER CONTEXT")[0];
            Assert.DoesNotMatch(@"(?i)demo|server|ticket|tool|read.?back|total|\$\d", acknowledgement);
            Assert.Contains("Do not announce totals or the full order after routine changes", text);
            Assert.DoesNotContain("The preceding order read-back is the guest-facing text.", text);
            Assert.Equal(executor.CurrentOrderSummaryJson, result.ToClientText());
            AssertNoCodes(result);
        }
        var sauce = Assert.Single(order.Items, i => i.Item == "Fire Sauce Packet");
        Assert.Equal(2, sauce.Quantity);
        Assert.Equal(0m, sauce.Price);
        Assert.Equal("$12.94", order.Summary.FinalTotalDisplay);
        Assert.False(Rewards(order)["canRedeem"]!.GetValue<bool>());
        var final = await GetOrder();
        Assert.StartsWith("\n\nPRIVATE ORDER CONTEXT:", final.ToText());
        Assert.Contains("\"finalTotalDisplay\":\"$12.94\"", final.ToText());
        Add(order, "Cinnamon Twists");
        Assert.Equal("$14.55", order.Summary.FinalTotalDisplay);
        order.RewardsAction("redeem", null);
        Assert.True(ModelRewards(await GetOrder())["redeemed"]!.GetValue<bool>());
        Assert.Equal("$12.94", order.Summary.FinalTotalDisplay);
        order.RewardsAction("complete", null);
        Assert.Equal(119, Rewards(order)["pointsAwarded"]!.GetValue<int>());
        Assert.Equal(369, Rewards(order)["balance"]!.GetValue<int>());
        var reset = await executor.ExecuteAsync("reset_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);
        Assert.StartsWith("Order cleared.", reset.ToText());
        AssertNoCodes(reset);
        Assert.Empty(order.Items);
    }

    [Theory]
    [InlineData("Mild Sauce Packet")]
    [InlineData("Hot Sauce Packet")]
    [InlineData("Fire Sauce Packet")]
    [InlineData("Diablo Sauce Packet")]
    public void ZeroPriceSaucesCannotEarnOrRedeemRewards(string item)
    {
        var order = NewOrder();
        Add(order, item, quantity: 2);
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.RewardsAction("redeem", null);
        Assert.Equal(2, Assert.Single(order.Items).Quantity);
        Assert.Equal(0m, order.Summary.Total);
        Assert.False(Rewards(order)["canRedeem"]!.GetValue<bool>());
        Assert.Equal(0m, Rewards(order)["discount"]!.GetValue<decimal>());
        Assert.Equal(0, Rewards(order)["pointsToEarn"]!.GetValue<int>());
    }

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
    public void CustomizedMealLateSweetRewardAndExactTotals()
    {
        var order = NewOrder();
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.HandleOrderUpdate("add", "3 Crunchy Tacos Combo", "standard", 1, 999m);
        order.HandleOrderUpdate("remove", "3 Crunchy Tacos Combo", "standard", 1, null);
        order.HandleOrderUpdate("add", "3 Soft Tacos Combo (No Tomatoes)", "standard", 1, 999m);
        Add(order, "Chips and Nacho Cheese Sauce");
        order.HandleOrderUpdate("add", "Mountain Dew Baja Blast", "large", 1, 999m);
        Assert.Equal(2, order.Items.Count);
        var meal = order.Items[0];
        Assert.Equal("3 Soft Tacos Combo (No Tomatoes)", meal.Item);
        Assert.Equal("standard", meal.Size);
        Assert.Equal(9.49m, meal.Price);
        Assert.Equal(["Large Mountain Dew Baja Blast"], meal.Components);
        Assert.Equal("$12.94", order.Summary.FinalTotalDisplay);
        Assert.False(Rewards(order)["completed"]!.GetValue<bool>());
        Add(order, "Cinnamon Twists");
        Assert.Equal("$14.55", order.Summary.FinalTotalDisplay);
        Assert.Equal("Cinnamon Twists", Rewards(order)["rewardItem"]!.GetValue<string>());
        order.RewardsAction("redeem", null);
        Assert.Equal(1.49m, Rewards(order)["discount"]!.GetValue<decimal>());
        Assert.Equal("$11.98", order.Summary.TotalDisplay);
        Assert.Equal("$0.96", order.Summary.TaxDisplay);
        Assert.Equal("$12.94", order.Summary.FinalTotalDisplay);
        order.RewardsAction("complete", null);
        order.RewardsAction("complete", null);
        Assert.Equal(369, Rewards(order)["balance"]!.GetValue<int>());
        Assert.Equal(119, Rewards(order)["pointsAwarded"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task RealToolsReplaceOmissionsInsteadOfRenamingWithModify(bool drinkBeforeOmission, int quantity)
    {
        var persona = PersonaCatalog.Load().Get("tacobell");
        var order = NewOrder();
        var executor = new OrderToolExecutor(order, PersonaOrderFactory.GetMenuCatalog(persona), null, 10, 25);
        Task<ToolResult> Update(string action, string item, string size = "Standard") =>
            executor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
            {
                action, item_name = item, size, quantity,
            }), TestContext.Current.CancellationToken);
        await Update("add", "3 Crunchy Tacos Combo");
        await Update("remove", "3 Crunchy Tacos Combo");
        await Update("add", "3 Soft Tacos Combo");
        if (drinkBeforeOmission) await Update("add", "Mountain Dew Baja Blast", "Large");
        var rejected = await Update("modify", "3 Soft Tacos Combo (No Tomatoes)");
        using var payload = JsonDocument.Parse(rejected.ToText());
        Assert.Equal("not_in_order", payload.RootElement.GetProperty("reason").GetString());
        Assert.All(order.Items, meal => Assert.Equal("3 Soft Tacos Combo", meal.Item));
        Assert.Equal(quantity, order.Items.Sum(meal => meal.Quantity));
        await Update("remove", "3 Soft Tacos Combo");
        Assert.Empty(order.Items);
        await Update("add", "3 Soft Tacos Combo (No Tomatoes)");
        await Update("add", "Mountain Dew Baja Blast", "Large");
        Assert.Equal(quantity, order.Items.Sum(meal => meal.Quantity));
        Assert.All(order.Items, meal =>
        {
            Assert.Equal("3 Soft Tacos Combo (No Tomatoes)", meal.Item);
            Assert.Equal(9.49m, meal.Price);
        });
        Assert.Equal(Enumerable.Repeat("Large Mountain Dew Baja Blast", quantity), order.Items.SelectMany(meal => meal.Components));
        Assert.Equal(Enumerable.Repeat(0.5m, quantity), order.Items.SelectMany(meal => meal.ComponentUpcharges));
        Assert.Equal(9.49m * quantity, order.Summary.Total);
    }

    [Fact]
    public async Task RealToolTextExposesOnlyCurrentRewardsStateAndKeepsClientTicketUnchanged()
    {
        var persona = PersonaCatalog.Load().Get("tacobell");
        var order = NewOrder();
        var executor = new OrderToolExecutor(order, PersonaOrderFactory.GetMenuCatalog(persona), null, 10, 25);
        Task<ToolResult> Update(string action, string item) =>
            executor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
            {
                action, item_name = item, size = "Standard", quantity = 1,
            }), TestContext.Current.CancellationToken);
        Task<ToolResult> GetOrder() =>
            executor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }),
                TestContext.Current.CancellationToken);

        var uncheckedResult = await GetOrder();
        var uncheckedSnapshot = ModelRewards(uncheckedResult);
        Assert.False(uncheckedSnapshot["checkedIn"]!.GetValue<bool>());
        Assert.False(uncheckedSnapshot["canRedeem"]!.GetValue<bool>());
        Assert.False(uncheckedSnapshot["eligibleItemInOrder"]!.GetValue<bool>());
        AssertNoCodes(uncheckedResult);

        await Update("add", "Cinnamon Twists");
        executor.RewardsAction("check_in", "DEMO-NEW");
        var insufficient = await GetOrder();
        var insufficientSnapshot = ModelRewards(insufficient);
        Assert.True(insufficientSnapshot["eligibleItemInOrder"]!.GetValue<bool>());
        Assert.False(insufficientSnapshot["canRedeem"]!.GetValue<bool>());
        Assert.Equal(0, insufficientSnapshot["balance"]!.GetValue<int>());
        AssertNoCodes(insufficient);

        var eligibleOrder = NewOrder();
        var eligibleExecutor = new OrderToolExecutor(
            eligibleOrder, PersonaOrderFactory.GetMenuCatalog(persona), null, 10, 25);
        eligibleExecutor.RewardsAction("check_in", "DEMO-PLUS");
        await eligibleExecutor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
        {
            action = "add", item_name = "Crunchy Taco", size = "Standard", quantity = 1,
        }), TestContext.Current.CancellationToken);
        var eligibleResult = await eligibleExecutor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
        {
            action = "add", item_name = "Cinnamon Twists", size = "Standard", quantity = 1,
        }), TestContext.Current.CancellationToken);
        var eligibleSnapshot = ModelRewards(eligibleResult);
        Assert.True(eligibleSnapshot["canRedeem"]!.GetValue<bool>());
        Assert.True(eligibleSnapshot["eligibleItemInOrder"]!.GetValue<bool>());
        Assert.Equal("Cinnamon Twists", eligibleSnapshot["rewardItem"]!.GetValue<string>());
        Assert.False(eligibleSnapshot["redeemed"]!.GetValue<bool>());
        Assert.Contains("Use reward", eligibleResult.ToText());
        Assert.Contains("do not redeem automatically", eligibleResult.ToText());
        Assert.Equal(eligibleExecutor.CurrentOrderSummaryJson, eligibleResult.ToClientText());
        using (var clientTicket = JsonDocument.Parse(eligibleResult.ToClientText()))
            Assert.True(clientTicket.RootElement.GetProperty("rewardsDemo").GetProperty("demoCodes").GetArrayLength() > 0);
        AssertNoCodes(eligibleResult);

        eligibleExecutor.RewardsAction("redeem", null);
        var reserved = await eligibleExecutor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);
        var reservedSnapshot = ModelRewards(reserved);
        Assert.True(reservedSnapshot["redeemed"]!.GetValue<bool>());
        Assert.Equal(1.49m, reservedSnapshot["discount"]!.GetValue<decimal>());
        Assert.Equal(eligibleOrder.Summary.TotalDisplay, reservedSnapshot["postDiscountTotal"]!.GetValue<string>());
        AssertNoCodes(reserved);

        await eligibleExecutor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
        {
            action = "remove", item_name = "Cinnamon Twists", size = "Standard", quantity = 1,
        }), TestContext.Current.CancellationToken);
        var afterRemove = ModelRewards(await eligibleExecutor.ExecuteAsync("get_order",
            JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken));
        Assert.True(afterRemove["eligibleItemInOrder"]!.GetValue<bool>());
        Assert.True(afterRemove["canRedeem"]!.GetValue<bool>());
        Assert.Equal("Crunchy Taco", afterRemove["rewardItem"]!.GetValue<string>());
        Assert.False(afterRemove["redeemed"]!.GetValue<bool>());
        Assert.Equal(0m, afterRemove["discount"]!.GetValue<decimal>());
        Assert.Equal(500, afterRemove["balance"]!.GetValue<int>());

        await eligibleExecutor.ExecuteAsync("reset_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);
        var afterReset = ModelRewards(await eligibleExecutor.ExecuteAsync("get_order",
            JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken));
        Assert.False(afterReset["eligibleItemInOrder"]!.GetValue<bool>());
        Assert.False(afterReset["redeemed"]!.GetValue<bool>());
        Assert.Equal(500, afterReset["balance"]!.GetValue<int>());
        AssertNoCodes(await eligibleExecutor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken));

        var completedOrder = NewOrder();
        var completedExecutor = new OrderToolExecutor(
            completedOrder, PersonaOrderFactory.GetMenuCatalog(persona), null, 10, 25);
        completedExecutor.RewardsAction("check_in", "DEMO-PLUS");
        await completedExecutor.ExecuteAsync("update_order", JsonSerializer.SerializeToElement(new
        {
            action = "add", item_name = "Crunchy Taco", size = "Standard", quantity = 1,
        }), TestContext.Current.CancellationToken);
        completedExecutor.RewardsAction("complete", null);
        var completed = await completedExecutor.ExecuteAsync("get_order", JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);
        var completedSnapshot = ModelRewards(completed);
        Assert.True(completedSnapshot["completed"]!.GetValue<bool>());
        Assert.False(completedSnapshot["canRedeem"]!.GetValue<bool>());
        Assert.Equal(519, completedSnapshot["balance"]!.GetValue<int>());
        AssertNoCodes(completed);

        var neutralPersona = PersonaCatalog.Load().Default;
        var neutralOrder = PersonaOrderFactory.CreateOrderState(neutralPersona);
        var neutralExecutor = new OrderToolExecutor(
            neutralOrder, PersonaOrderFactory.GetMenuCatalog(neutralPersona), null, 10, 25);
        var neutralReadback = await neutralExecutor.ExecuteAsync("get_order",
            JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        Assert.Equal(neutralOrder.GetGroupedOrderForReadback() + neutralOrder.HappyHourBanner,
            neutralReadback.ToText());
    }

    private static JsonObject ModelRewards(ToolResult result)
    {
        const string marker = "[SERVER-AUTHORITATIVE FICTIONAL REWARDS STATE: ";
        var text = result.ToText();
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Tool text should include the server rewards snapshot.");
        start += marker.Length;
        var end = text.IndexOf(']', start);
        Assert.True(end >= 0, "Tool text should close the server rewards snapshot.");
        return JsonNode.Parse(text[start..end])!.AsObject();
    }

    private static void AssertNoCodes(ToolResult result)
    {
        Assert.Contains("Private model context; do not read this structured block or its markers aloud.", result.ToText());
        Assert.DoesNotContain("DEMO-PLUS", result.ToText());
        Assert.DoesNotContain("DEMO-NEW", result.ToText());
        Assert.DoesNotContain("demoCodes", result.ToText());
    }

    [Fact]
    public void ReservationCannotSilentlySwitchToAnotherReward()
    {
        var order = NewOrder();
        Add(order);
        order.RewardsAction("check_in", "DEMO-PLUS");
        order.RewardsAction("redeem", null);
        Add(order, "Cinnamon Twists");
        Assert.Equal("Crunchy Taco", Rewards(order)["rewardItem"]!.GetValue<string>());
        Assert.Equal(1.99m, Rewards(order)["discount"]!.GetValue<decimal>());
        order.HandleOrderUpdate("remove", "Crunchy Taco", "standard", 1, null);
        Assert.False(Rewards(order)["redeemed"]!.GetValue<bool>());
        Assert.Equal(0m, Rewards(order)["discount"]!.GetValue<decimal>());
        Assert.Equal(500, Rewards(order)["balance"]!.GetValue<int>());
        Assert.Equal("Cinnamon Twists", Rewards(order)["rewardItem"]!.GetValue<string>());
        order.RewardsAction("redeem", null);
        Assert.Equal(1.49m, Rewards(order)["discount"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MealDrinkUpgradeIsPathIndependentAndResizeDoesNotDuplicate(bool drinkFirst)
    {
        var order = NewOrder();
        void Drink() => order.HandleOrderUpdate("add", "Mountain Dew Baja Blast", "large", 1, 999m);
        if (drinkFirst) Drink();
        Add(order, "3 Soft Tacos Combo (No Tomatoes)");
        if (!drinkFirst) Drink();
        Assert.Single(order.Items);
        Assert.Equal(9.49m, order.Items[0].Price);
        order.HandleOrderUpdate("modify", "Mountain Dew Baja Blast", "medium", 1, 999m);
        Assert.Equal(8.99m, order.Items[0].Price);
        order.HandleOrderUpdate("modify", "Mountain Dew Baja Blast", "large", 1, 999m);
        Assert.Single(order.Items);
        Assert.Equal(9.49m, order.Items[0].Price);
        Assert.Equal(["Large Mountain Dew Baja Blast"], order.Items[0].Components);
        Assert.Equal([0.5m], order.Items[0].ComponentUpcharges);
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
