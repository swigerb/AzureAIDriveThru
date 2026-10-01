using Backend.Ordering;
using Backend.Personas;

namespace Backend.Tests.Ordering;

/// <summary>
/// C# port of app/backend/tests/test_combo_orders.py's TestMcDonaldsWholeMealResize (PR #184
/// round 2, Rick's review, item 1 -- "wholeBundleSize" packs): McDonald's "make it a large meal"
/// ports the original McDonald's app's own meal-size pricing/phrasing -- resizing the MEAL (not a
/// single component) changes the meal's own price and relabels fries/drink together, via the
/// real production McDonald's persona pack (no brand names in shared code; this test file is
/// itself allowed to name a real pack to prove its OWN data drives this, exactly like the Python
/// test file it mirrors).
///
/// <para>Loaded explicitly (<c>personasEnv</c>/<c>defaultPersonaEnv</c> pinned to "mcdonalds" so
/// this test can never be affected by this process's own <c>PERSONAS</c>/<c>DEFAULT_PERSONA</c>
/// env vars), mirroring Python's own <c>PersonaCatalog.load(enabled=["mcdonalds"],
/// default_persona_id="mcdonalds")</c>. Exercised directly against
/// <see cref="OrderState.HandleOrderUpdate"/>, exactly like the Python test suite's own
/// technique.</para>
/// </summary>
public sealed class McDonaldsWholeMealResizeTests
{
    private const string Meal = "Big Mac® Meal";
    private const decimal MealStandardPrice = 8.99m;
    private const decimal MealLargePrice = 10.09m;
    private const string Drink = "Coca-Cola®";
    private const decimal DrinkMediumPrice = 1.79m;

    private static OrderState NewMcDonaldsOrder()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(
            personasDir: personasDir, personasEnv: "mcdonalds", defaultPersonaEnv: "mcdonalds");
        return PersonaOrderFactory.CreateOrderState(catalog.Get("mcdonalds"));
    }

    private static void SeedMeal(OrderState order)
    {
        order.HandleOrderUpdate("add", Meal, "standard", 1, MealStandardPrice);
        order.HandleOrderUpdate("add", Drink, "medium", 1, DrinkMediumPrice);
    }

    [Fact]
    public void MakeItALargeMeal_ResizesFriesAndDrinkTogether()
    {
        // "Make it a large meal" on a "wholeBundleSize" pack resizes the WHOLE bundle -- its own
        // price changes to the meal's Large price, and BOTH the autofilled fries and the guest's
        // chosen drink are relabeled to Large, matching the original McDonald's app's own
        // behavior/phrasing.
        var order = NewMcDonaldsOrder();
        SeedMeal(order);

        Assert.Single(order.Items);
        var meal = order.Items[0];
        Assert.Contains("Medium World Famous Fries®", meal.Display); // bundle.autoFill default
        Assert.Contains("Medium Coca-Cola®", meal.Display);
        Assert.Equal(MealStandardPrice, meal.Price);

        var result = order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);
        Assert.Equal("large", result.ModifiedToSize);

        Assert.Single(order.Items);
        meal = order.Items[0];
        Assert.Equal("large", meal.Size);
        Assert.Equal(MealLargePrice, meal.Price);
        Assert.Contains("Large World Famous Fries®", meal.Display);
        Assert.Contains("Large Coca-Cola®", meal.Display);
        Assert.DoesNotContain("Medium", meal.Display);

        Assert.Equal(MealLargePrice, order.Summary.Total);
    }

    [Fact]
    public void MakeItALargeMeal_IsANoop_WhenAlreadyThatSize()
    {
        // Resizing to the meal's own CURRENT size must no-op -- never a double-charge or a
        // redundant relabel.
        var order = NewMcDonaldsOrder();
        SeedMeal(order);

        var result = order.HandleOrderUpdate("modify", Meal, "standard", 1, MealStandardPrice);
        Assert.Null(result.ModifiedToSize);

        var meal = order.Items[0];
        Assert.Equal(MealStandardPrice, meal.Price);
        Assert.Equal(MealStandardPrice, order.Summary.Total);
    }
}
