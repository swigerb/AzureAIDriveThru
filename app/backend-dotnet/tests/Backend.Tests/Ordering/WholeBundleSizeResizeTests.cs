using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Ordering;

/// <summary>
/// Neutral wholeBundleSize fixture coverage. The shared order-engine tests in this file use the
/// test-only Epsilon fixture, which has bundles.resizeRule set to wholeBundleSize, one multi-size
/// bundle, one Standard-only bundle, and Small/Medium/Large slot items.
/// </summary>
public sealed class WholeBundleSizeResizeTests
{
    private const string Meal = "Epsilon Snack Meal";
    private const decimal MealSmallPrice = 4.99m;
    private const decimal MealMediumPrice = 5.99m;
    private const decimal MealLargePrice = 6.99m;
    private const string Drink = "Epsilon Cola";
    private const decimal DrinkSmallPrice = 1.00m;
    private const decimal DrinkMediumPrice = 1.50m;
    private const decimal DrinkLargePrice = 2.00m;
    private const string Fries = "Epsilon Fries";
    private const string StandardOnlyMeal = "Epsilon Standard Meal";
    private const decimal StandardOnlyMealPrice = 3.49m;

    private static Persona WholeBundleSizePersona() => DeltaFixture.Load("test-epsilon");
    private static OrderState NewOrder() => PersonaOrderFactory.CreateOrderState(WholeBundleSizePersona());

    private static void SeedMeal(OrderState order)
    {
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);
        order.HandleOrderUpdate("add", Drink, "small", 1, DrinkSmallPrice);
    }

    [Fact]
    public void MakeItALargeMeal_ResizesFriesAndDrinkTogether()
    {
        var order = NewOrder();
        SeedMeal(order);

        var meal = order.Items[0];
        Assert.Contains("Small Epsilon Fries", meal.Display);
        Assert.Contains("Small Epsilon Cola", meal.Display);
        Assert.Equal(MealSmallPrice, meal.Price);

        var result = order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);
        Assert.Equal("large", result.ModifiedToSize);

        meal = order.Items[0];
        Assert.Equal("large", meal.Size);
        Assert.Equal(MealLargePrice, meal.Price);
        Assert.Contains("Large Epsilon Fries", meal.Display);
        Assert.Contains("Large Epsilon Cola", meal.Display);
        Assert.DoesNotContain("Medium", meal.Display);
        Assert.DoesNotContain("Small", meal.Display);
        Assert.Equal(MealLargePrice, order.Summary.Total);
    }

    [Fact]
    public void MakeItALargeMeal_IsANoop_WhenAlreadyThatSize()
    {
        var order = NewOrder();
        SeedMeal(order);

        var result = order.HandleOrderUpdate("modify", Meal, "small", 1, MealSmallPrice);
        Assert.Null(result.ModifiedToSize);
        Assert.Equal(MealSmallPrice, order.Items[0].Price);
    }

    [Fact]
    public void ModifyingTheAutofilledFries_ByTheirRealMenuName_IsFound()
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);

        var result = order.HandleOrderUpdate("modify", Fries, "medium", 1, 0m);
        Assert.Null(result.ComboComponentResizeRejected);
        Assert.Equal("sides", result.ResizedComboComponent);

        var meal = order.Items[0];
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MealMediumPrice, meal.Price);
        Assert.Contains("Medium Epsilon Fries", meal.Display);
    }

    [Fact]
    public void ComponentResize_IsRejectedCleanly_WhenThePackHasNoPriceAtThatSize()
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", StandardOnlyMeal, "standard", 1, StandardOnlyMealPrice);

        var result = order.HandleOrderUpdate("modify", Fries, "large", 1, 0m);
        Assert.Equal("sides", result.ComboComponentResizeRejected);
        Assert.Null(result.ResizedComboComponent);

        var meal = order.Items[0];
        Assert.Equal("standard", meal.Size);
        Assert.Equal(StandardOnlyMealPrice, meal.Price);
        Assert.Contains("Medium Epsilon Fries", meal.Display);
        Assert.Equal(StandardOnlyMealPrice, order.Summary.Total);
    }

    [Fact]
    public void DownsizingAComponent_ResizesTheWholeMeal_NeverLeavesMixedSizes()
    {
        var order = NewOrder();
        SeedMeal(order);
        order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);

        var result = order.HandleOrderUpdate("modify", Drink, "medium", 1, 0m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        var meal = order.Items[0];
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MealMediumPrice, meal.Price);
        Assert.Contains("Medium Epsilon Fries", meal.Display);
        Assert.Contains("Medium Epsilon Cola", meal.Display);
        Assert.DoesNotContain("Large", meal.Display);
    }

    [Fact]
    public void ResizingTheMealBackDown_RelabelsEverySlot_NoStaleSize()
    {
        var order = NewOrder();
        SeedMeal(order);
        order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);

        var result = order.HandleOrderUpdate("modify", Meal, "small", 1, MealSmallPrice);
        Assert.Equal("small", result.ModifiedToSize);

        var meal = order.Items[0];
        Assert.Equal("small", meal.Size);
        Assert.Equal(MealSmallPrice, meal.Price);
        Assert.Contains("Small Epsilon Fries", meal.Display);
        Assert.Contains("Small Epsilon Cola", meal.Display);
        Assert.DoesNotContain("Large", meal.Display);
        Assert.DoesNotContain("Medium", meal.Display);
    }

    [Fact]
    public void MakeItAMediumMeal_IsAcceptedDirectly_NotRejected()
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);

        var result = order.HandleOrderUpdate("modify", Meal, "medium", 1, MealMediumPrice);
        Assert.Equal("medium", result.ModifiedToSize);
        Assert.Equal(MealMediumPrice, order.Items[0].Price);
    }

    [Fact]
    public void ResizingOneUnitsFries_OnAQuantityTwoMeal_SplitsThatUnitOnly()
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 2, MealSmallPrice);

        var result = order.HandleOrderUpdate("modify", Fries, "medium", 1, 0m);
        Assert.Equal("sides", result.ResizedComboComponent);

        Assert.Equal(2, order.Items.Count);
        var resized = order.Items.Single(i => i.Size == "medium");
        var untouched = order.Items.Single(i => i.Size == "small");
        Assert.Equal(1, resized.Quantity);
        Assert.Equal(1, untouched.Quantity);
        Assert.Equal(MealMediumPrice, resized.Price);
        Assert.Equal(MealSmallPrice, untouched.Price);
        Assert.Contains("Medium Epsilon Fries", resized.Display);
        Assert.Contains("Small Epsilon Fries", untouched.Display);
        Assert.Equal(MealMediumPrice + MealSmallPrice, order.Summary.Total);
    }

    [Theory]
    [InlineData("small", 1.00, "Small")]
    [InlineData("medium", 1.50, "Medium")]
    [InlineData("large", 2.00, "Large")]
    public void StandardOnlyBundle_AbsorbsSmallMediumOrLargeDrink(string drinkSize, decimal drinkPrice, string drinkLabel)
    {
        var order = NewOrder();
        order.HandleOrderUpdate("add", StandardOnlyMeal, "standard", 1, StandardOnlyMealPrice);

        var result = order.HandleOrderUpdate("add", Drink, drinkSize, 1, drinkPrice);
        Assert.True(result.AbsorbedIntoCombo);

        Assert.Single(order.Items);
        var meal = order.Items[0];
        Assert.Equal("standard", meal.Size);
        Assert.Contains($"{drinkLabel} Epsilon Cola", meal.Display);
        Assert.True(order.GetComboRequirements().IsComplete);
        Assert.Equal(StandardOnlyMealPrice, order.Summary.Total);
    }

    [Fact]
    public void FirstAbsorption_IsPathIndependent_AndKeepsComponentSize()
    {
        static (string Item, string Size, decimal Price, string Display, decimal Total) Snapshot(
            IEnumerable<(string Action, string Item, string Size, int Quantity, decimal Price)> steps)
        {
            var order = NewOrder();
            foreach (var step in steps)
            {
                order.HandleOrderUpdate(step.Action, step.Item, step.Size, step.Quantity, step.Price);
            }
            var meal = order.Items[0];
            return (meal.Item, meal.Size, meal.Price, meal.Display, order.Summary.Total);
        }

        var mealThenDrink = Snapshot([
            ("add", Meal, "medium", 1, MealMediumPrice),
            ("add", Drink, "large", 1, DrinkLargePrice),
        ]);
        var drinkThenMeal = Snapshot([
            ("add", Drink, "large", 1, DrinkLargePrice),
            ("add", Meal, "medium", 1, MealMediumPrice),
        ]);

        Assert.Equal(mealThenDrink, drinkThenMeal);
        Assert.Equal(MealMediumPrice, mealThenDrink.Price);
        Assert.Equal(MealMediumPrice, mealThenDrink.Total);
        Assert.Contains("Medium Epsilon Fries", mealThenDrink.Display);
        Assert.Contains("Large Epsilon Cola", mealThenDrink.Display);
    }
}
