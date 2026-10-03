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
        Assert.Equal("$0.50", result.ComboComponentUpchargeDisplay);
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
