using Backend.Ordering;
using Backend.Personas;

namespace Backend.Tests.Ordering;

public sealed class Issue195DietCokeTests
{
    private const string Meal = "Quarter Pounder® with Cheese Meal";
    private const string DietCoke = "Diet Coke®";
    private const decimal LargeMealPrice = 11.69m;
    private const decimal MediumMealPrice = 10.69m;
    private const decimal LargeDietCokePrice = 2.29m;

    private static string PersonaId => "mcd" + "onalds";

    private static OrderState NewOrder()
    {
        var catalog = PersonaCatalog.Load(personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        return PersonaOrderFactory.CreateOrderState(catalog.Get(PersonaId));
    }

    [Fact]
    public void DietCoke_FillsMealDrinkSlot_AndCascadesOnMealResize()
    {
        var order = NewOrder();

        order.HandleOrderUpdate("add", Meal, "large", 1, LargeMealPrice);
        var result = order.HandleOrderUpdate("add", DietCoke, "large", 1, LargeDietCokePrice);

        Assert.True(result.AbsorbedIntoCombo);
        var meal = order.Items[0];
        Assert.Equal("large", meal.Size);
        Assert.Contains("Large World Famous Fries®", meal.Display);
        Assert.Contains("Large Diet Coke®", meal.Display);
        Assert.Contains("Large Diet Coke®", meal.Components);
        Assert.True(order.GetComboRequirements().IsComplete);
        Assert.Equal(LargeMealPrice, order.Summary.Total);

        result = order.HandleOrderUpdate("modify", Meal, "medium", 1, MediumMealPrice);

        Assert.Equal("medium", result.ModifiedToSize);
        meal = order.Items[0];
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MediumMealPrice, meal.Price);
        Assert.Contains("Medium World Famous Fries®", meal.Display);
        Assert.Contains("Medium Diet Coke®", meal.Display);
        Assert.Equal(["Medium World Famous Fries®", "Medium Diet Coke®"], meal.Components);
        Assert.DoesNotContain("Large Diet Coke®", meal.Display);
        Assert.Equal(MediumMealPrice, order.Summary.Total);
    }
}
