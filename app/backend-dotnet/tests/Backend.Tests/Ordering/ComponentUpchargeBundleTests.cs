using Backend.Ordering;
using Backend.Personas;

namespace Backend.Tests.Ordering;

public sealed class ComponentUpchargeBundleTests
{
    private const string PersonaId = "sonic";
    private const string Bundle = "SuperSONIC® Double Cheeseburger Combo";
    private const string Side = "Tots";
    private const string Drink = "Cherry Limeade";

    private static OrderState NewOrder()
    {
        var catalog = PersonaCatalog.Load(personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        return PersonaOrderFactory.CreateOrderState(catalog.Get(PersonaId));
    }

    [Fact]
    public void MediumComponentsAreIncludedAndLargerComponentsAddPositiveDeltas()
    {
        var order = NewOrder();

        order.HandleOrderUpdate("add", Bundle, "standard", 1, 10.19m);
        order.HandleOrderUpdate("add", Side, "medium", 1, 2.79m);
        order.HandleOrderUpdate("add", Drink, "medium", 1, 2.89m);
        Assert.Equal(10.19m, order.Summary.Total);

        var result = order.HandleOrderUpdate("modify", Drink, "large", 1, 3.39m);
        Assert.Equal(0.50m, result.ComboComponentUpcharge);
        Assert.Equal("fifty cents", result.ComboComponentUpchargeDisplay);
        Assert.Equal(10.69m, order.Summary.Total);
        Assert.Equal(new[] { 0m, 0.50m }, order.Items[0].ComponentUpcharges);

        order.HandleOrderUpdate("modify", Side, "large", 1, 3.49m);
        Assert.Equal(11.39m, order.Summary.Total);
        Assert.Equal(new[] { 0.70m, 0.50m }, order.Items[0].ComponentUpcharges);
    }

    [Fact]
    public void OrderingLargeUpFrontMatchesResizingLaterAndBackToMediumRemovesUpcharge()
    {
        var upfront = NewOrder();
        upfront.HandleOrderUpdate("add", Bundle, "standard", 1, 10.19m);
        upfront.HandleOrderUpdate("add", Side, "medium", 1, 2.79m);
        upfront.HandleOrderUpdate("add", Drink, "large", 1, 3.39m);

        var resized = NewOrder();
        resized.HandleOrderUpdate("add", Bundle, "standard", 1, 10.19m);
        resized.HandleOrderUpdate("add", Side, "medium", 1, 2.79m);
        resized.HandleOrderUpdate("add", Drink, "medium", 1, 2.89m);
        resized.HandleOrderUpdate("modify", Drink, "large", 1, 3.39m);

        Assert.Equal(10.69m, upfront.Summary.Total);
        Assert.Equal(upfront.Summary.Total, resized.Summary.Total);

        resized.HandleOrderUpdate("modify", Drink, "medium", 1, 2.89m);
        Assert.Equal(10.19m, resized.Summary.Total);
    }

    [Fact]
    public void QuantityTwoComponentUpchargesSplitAndRepeatedResizeTargetsRemainingUnit()
    {
        var order = NewOrder();

        order.HandleOrderUpdate("add", Bundle, "standard", 2, 10.19m);
        order.HandleOrderUpdate("add", Side, "medium", 2, 2.79m);
        order.HandleOrderUpdate("add", Drink, "medium", 2, 2.89m);

        var first = order.HandleOrderUpdate("modify", Drink, "large", 1, 3.39m);
        Assert.Equal("fifty cents", first.ComboComponentUpchargeDisplay);
        Assert.Equal(2, order.Items.Count);
        Assert.Contains(order.Items, item => item.Quantity == 1 && item.Price == 10.19m && item.ComponentUpcharges.SequenceEqual([0m, 0m]));
        Assert.Contains(order.Items, item => item.Quantity == 1 && item.Price == 10.69m && item.ComponentUpcharges.SequenceEqual([0m, 0.50m]));
        Assert.Equal(20.88m, order.Summary.Total);

        var second = order.HandleOrderUpdate("modify", Drink, "large", 1, 3.39m);
        Assert.Equal("medium", second.ComboComponentResizedFromSize);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, item =>
        {
            Assert.Equal(1, item.Quantity);
            Assert.Equal(10.69m, item.Price);
            Assert.Equal(new[] { 0m, 0.50m }, item.ComponentUpcharges);
        });
        Assert.Equal(21.38m, order.Summary.Total);
        // #313 (Rick's review, item 1/2): the read-back spells the quantity out as a word and
        // speaks the upcharge in words, never as "$0.50" digit money.
        var readback = order.GetGroupedOrderForReadback();
        Assert.Contains("two SuperSONIC", readback);
        Assert.Contains($"{Money.FormatMoneySpoken(0.50m)} upcharge", readback);
    }

    [Theory]
    [MemberData(nameof(PathIndependentLargeSideAndDrinkSteps))]
    public void ComponentUpchargeTotalIsPathIndependentAcrossFillAndResizeOrders((string Action, string Item, string Size, int Quantity, decimal Price)[] steps)
    {
        var order = NewOrder();

        foreach (var (action, item, size, quantity, price) in steps)
        {
            order.HandleOrderUpdate(action, item, size, quantity, price);
        }

        Assert.Equal(11.39m, order.Summary.Total);
        Assert.Equal(new[] { 0.70m, 0.50m }, order.Items[0].ComponentUpcharges);
    }

    public static TheoryData<(string Action, string Item, string Size, int Quantity, decimal Price)[]> PathIndependentLargeSideAndDrinkSteps => new()
    {
        new[]
        {
            ("add", Bundle, "standard", 1, 10.19m),
            ("add", Side, "large", 1, 3.49m),
            ("add", Drink, "large", 1, 3.39m),
        },
        new[]
        {
            ("add", Side, "large", 1, 3.49m),
            ("add", Drink, "large", 1, 3.39m),
            ("add", Bundle, "standard", 1, 10.19m),
        },
        new[]
        {
            ("add", Bundle, "standard", 1, 10.19m),
            ("add", Drink, "medium", 1, 2.89m),
            ("add", Side, "medium", 1, 2.79m),
            ("modify", Side, "large", 1, 3.49m),
            ("modify", Drink, "large", 1, 3.39m),
        },
        new[]
        {
            ("add", Bundle, "standard", 1, 10.19m),
            ("add", Side, "medium", 1, 2.79m),
            ("add", Drink, "medium", 1, 2.89m),
            ("modify", Drink, "large", 1, 3.39m),
            ("modify", Side, "large", 1, 3.49m),
        },
    };

    [Fact]
    public void SmallerComponentsGiveNoCreditAndRoute44AddsOnlyAboveMediumDelta()
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", Bundle, "standard", 1, 10.19m);
        order.HandleOrderUpdate("add", Side, "small", 1, 2.19m);
        order.HandleOrderUpdate("add", Drink, "rt44", 1, 3.79m);

        Assert.Equal(11.09m, order.Summary.Total);
        Assert.Equal(new[] { 0m, 0.90m }, order.Items[0].ComponentUpcharges);
    }
}
