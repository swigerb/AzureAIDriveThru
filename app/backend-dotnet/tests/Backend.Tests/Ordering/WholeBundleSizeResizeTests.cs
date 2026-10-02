using Backend.Ordering;
using Backend.Personas;

namespace Backend.Tests.Ordering;

/// <summary>
/// C# port of app/backend/tests/test_wholebundlesize_resize.py (PR #184 round 2, Rick's review,
/// item 1 in round 2; items D/E/F in round 3 -- "wholeBundleSize" packs): "make it a large meal"
/// resizes the WHOLE bundle (not a single component) -- the bundle's own price moves to its Large
/// price, and every slot filling it (autofilled or explicitly added) is relabeled to match,
/// exactly like the original app this pack's pricing and phrasing was ported from. Shared
/// order-engine code stays brand-agnostic the whole time, driven entirely by whichever pack's own
/// <c>bundles.resizeRule</c> is set to <c>"wholeBundleSize"</c> -- this file finds that pack
/// DYNAMICALLY (see <see cref="WholeBundleSizePersona"/> below) rather than naming it directly in
/// source, so it never grows this repo's own brand-word baseline no matter which real pack opts
/// into the rule.
///
/// <para>Exercised directly against <see cref="OrderState.HandleOrderUpdate"/>, exactly like the
/// Python test suite's own technique.</para>
/// </summary>
public sealed class WholeBundleSizeResizeTests
{
    private const string Meal = "Big Mac\u00ae Meal";
    private const decimal MealSmallPrice = 8.99m;
    private const decimal MealMediumPrice = 10.29m;
    private const decimal MealLargePrice = 11.29m;
    private const string Drink = "Coca-Cola\u00ae";
    private const decimal DrinkSmallPrice = 1.29m;
    private const string Fries = "World Famous Fries\u00ae";
    // A meal this pack never extended with a Large (or even Medium) tier -- matching the
    // original app's own menu, where this exact meal is Standard-only.
    private const string NoLargeMeal = "McChicken\u00ae Meal";
    private const decimal NoLargeMealPrice = 5.99m;

    /// <summary>Finds the one enabled, real production persona pack whose own
    /// <c>bundles.resizeRule</c> is <c>"wholeBundleSize"</c> -- without naming that pack directly
    /// in source -- so this file can prove the shared engine mechanism actually produces a REAL
    /// pack's real end-to-end behavior without ever hardcoding which pack that is.</summary>
    private static Persona WholeBundleSizePersona()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);
        foreach (var id in catalog.Ids)
        {
            var persona = catalog.Get(id);
            if (persona.Bundles.ResizeRule == "wholeBundleSize")
            {
                return persona;
            }
        }
        throw new InvalidOperationException("no enabled persona pack has bundles.resizeRule == 'wholeBundleSize'");
    }

    private static OrderState NewOrder() => PersonaOrderFactory.CreateOrderState(WholeBundleSizePersona());

    private static void SeedMeal(OrderState order)
    {
        // Drink added at the SAME size as the meal -- a "wholeBundleSize" pack has exactly ONE
        // size per bundle instance, so this is the only internally-consistent starting point;
        // the resize itself is what each test below exercises afterward.
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);
        order.HandleOrderUpdate("add", Drink, "small", 1, DrinkSmallPrice);
    }

    [Fact]
    public void MakeItALargeMeal_ResizesFriesAndDrinkTogether()
    {
        // "Make it a large meal" on a "wholeBundleSize" pack resizes the WHOLE bundle -- its own
        // price changes to the meal's Large price, and BOTH the autofilled fries and the guest's
        // chosen drink are relabeled to Large, matching the original app's own
        // behavior/phrasing this pack's pricing was ported from.
        var order = NewOrder();
        SeedMeal(order);

        Assert.Single(order.Items);
        var meal = order.Items[0];
        Assert.Contains("Small World Famous Fries\u00ae", meal.Display); // bundle.autoFill default
        Assert.Contains("Small Coca-Cola\u00ae", meal.Display);
        Assert.Equal(MealSmallPrice, meal.Price);

        var result = order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);
        Assert.Equal("large", result.ModifiedToSize);

        Assert.Single(order.Items);
        meal = order.Items[0];
        Assert.Equal("large", meal.Size);
        Assert.Equal(MealLargePrice, meal.Price);
        Assert.Contains("Large World Famous Fries\u00ae", meal.Display);
        Assert.Contains("Large Coca-Cola\u00ae", meal.Display);
        Assert.DoesNotContain("Medium", meal.Display);
        Assert.DoesNotContain("Small", meal.Display);

        Assert.Equal(MealLargePrice, order.Summary.Total);
    }

    [Fact]
    public void MakeItALargeMeal_IsANoop_WhenAlreadyThatSize()
    {
        // Resizing to the meal's own CURRENT size must no-op -- never a double-charge or a
        // redundant relabel.
        var order = NewOrder();
        SeedMeal(order);

        var result = order.HandleOrderUpdate("modify", Meal, "small", 1, MealSmallPrice);
        Assert.Null(result.ModifiedToSize);

        var meal = order.Items[0];
        Assert.Equal(MealSmallPrice, meal.Price);
        Assert.Equal(MealSmallPrice, order.Summary.Total);
    }

    [Fact]
    public void ModifyingTheAutofilledFries_ByTheirRealMenuName_IsFound()
    {
        // Rick's round-3 review, item E (open since round 1): the autofilled side slot must be
        // found by the REAL, base on-menu item name the guest/model actually says ("World
        // Famous Fries(R)"), never only by its already-size-baked display text ("Small World
        // Famous Fries(R)") -- otherwise this exact resize is wrongly rejected as not_in_order,
        // and the fries can never be changed at all once auto-added.
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);

        var result = order.HandleOrderUpdate("modify", Fries, "medium", 1, 0m);
        Assert.Null(result.ComboComponentResizeRejected);
        Assert.Equal("sides", result.ResizedComboComponent);

        var meal = order.Items[0];
        // "wholeBundleSize": resizing the (only) filled slot moves the WHOLE bundle with it.
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MealMediumPrice, meal.Price);
        Assert.Contains("Medium World Famous Fries\u00ae", meal.Display);
    }

    [Fact]
    public void ComponentResize_IsRejectedCleanly_WhenThePackHasNoPriceAtThatSize()
    {
        // Rick's round-3 review, item D (mutation M4): if this pack never priced the bundle's
        // own item at the requested size (e.g. a Standard-only meal with no Large tier, matching
        // the original app), resizing one of its slots must be REJECTED outright -- the slot
        // (and the whole bundle) stays exactly as it was, never silently relabeled to a size the
        // bundle itself doesn't (and can't) charge for.
        var order = NewOrder();
        order.HandleOrderUpdate("add", NoLargeMeal, "standard", 1, NoLargeMealPrice);

        var result = order.HandleOrderUpdate("modify", Fries, "large", 1, 0m);
        Assert.Equal("sides", result.ComboComponentResizeRejected);
        Assert.Null(result.ResizedComboComponent);

        var meal = order.Items[0];
        Assert.Equal("standard", meal.Size);
        Assert.Equal(NoLargeMealPrice, meal.Price);
        Assert.Contains("Medium World Famous Fries\u00ae", meal.Display); // unchanged -- no relabel at all

        Assert.Equal(NoLargeMealPrice, order.Summary.Total);
    }

    [Fact]
    public void DownsizingAComponent_ResizesTheWholeMeal_NeverLeavesMixedSizes()
    {
        // Rick's round-3 review, item D (M4): on a Large meal, resizing just the drink down
        // (e.g. "make the Coke a medium") must resize the WHOLE bundle down with it -- never
        // leave the meal at Large while a slot shows a smaller size, and never silently
        // no-op just because the new size is smaller than the bundle's current one.
        var order = NewOrder();
        SeedMeal(order);
        order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);

        var result = order.HandleOrderUpdate("modify", Drink, "medium", 1, 0m);
        Assert.Equal("drinks", result.ResizedComboComponent);

        var meal = order.Items[0];
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MealMediumPrice, meal.Price);
        Assert.Contains("Medium World Famous Fries\u00ae", meal.Display);
        Assert.Contains("Medium Coca-Cola\u00ae", meal.Display);
        Assert.DoesNotContain("Large", meal.Display);

        Assert.Equal(MealMediumPrice, order.Summary.Total);
    }

    [Fact]
    public void ResizingTheMealBackDown_RelabelsEverySlot_NoStaleSize()
    {
        // Rick's round-3 review, item D (M1b): resizing the bundle itself back down (Large
        // then back to Small) must relabel EVERY filled slot to the new size -- a slot must
        // never keep a stale, larger size label (or lose its size label outright) after the
        // bundle it belongs to has moved to a smaller size.
        var order = NewOrder();
        SeedMeal(order);
        order.HandleOrderUpdate("modify", Meal, "large", 1, MealLargePrice);

        var result = order.HandleOrderUpdate("modify", Meal, "small", 1, MealSmallPrice);
        Assert.Equal("small", result.ModifiedToSize);

        var meal = order.Items[0];
        Assert.Equal("small", meal.Size);
        Assert.Equal(MealSmallPrice, meal.Price);
        Assert.Contains("Small World Famous Fries\u00ae", meal.Display);
        Assert.Contains("Small Coca-Cola\u00ae", meal.Display);
        Assert.DoesNotContain("Large", meal.Display);
        Assert.DoesNotContain("Medium", meal.Display);

        Assert.Equal(MealSmallPrice, order.Summary.Total);
    }

    [Fact]
    public void MakeItAMediumMeal_IsAcceptedDirectly_NotRejected()
    {
        // Rick's round-3 review, item D (M1c): "make it a medium meal" must be accepted
        // directly (this pack genuinely prices a Medium tier) -- never rejected as
        // size_not_available.
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 1, MealSmallPrice);

        var result = order.HandleOrderUpdate("modify", Meal, "medium", 1, MealMediumPrice);
        Assert.Equal("medium", result.ModifiedToSize);

        var meal = order.Items[0];
        Assert.Equal("medium", meal.Size);
        Assert.Equal(MealMediumPrice, meal.Price);
    }

    [Fact]
    public void ResizingOneUnitsFries_OnAQuantityTwoMeal_SplitsThatUnitOnly()
    {
        // Rick's round-3 review, item F: a feasible resize on a quantity>1 "wholeBundleSize"
        // line must split the ONE physical unit actually named off into its own line -- never
        // silently resize (and reprice) BOTH units sharing that single order line.
        var order = NewOrder();
        order.HandleOrderUpdate("add", Meal, "small", 2, MealSmallPrice);
        Assert.Single(order.Items);
        Assert.Equal(2, order.Items[0].Quantity);

        var result = order.HandleOrderUpdate("modify", Fries, "medium", 1, 0m);
        Assert.Equal("sides", result.ResizedComboComponent);

        Assert.Equal(2, order.Items.Count); // split into its own line -- the other unit is untouched
        var resized = order.Items.Single(i => i.Size == "medium");
        var untouched = order.Items.Single(i => i.Size == "small");
        Assert.Equal(1, resized.Quantity);
        Assert.Equal(1, untouched.Quantity);
        Assert.Equal(MealMediumPrice, resized.Price);
        Assert.Equal(MealSmallPrice, untouched.Price);
        Assert.Contains("Medium World Famous Fries\u00ae", resized.Display);
        Assert.Contains("Small World Famous Fries\u00ae", untouched.Display);

        Assert.Equal(MealMediumPrice + MealSmallPrice, order.Summary.Total);
    }
}
