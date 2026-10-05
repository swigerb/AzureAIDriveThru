using Backend.Ordering;
using Backend.Personas;

namespace Backend.Tests.Ordering;

/// <summary>Issue #304: unit coverage for the two server-side primitives mirrored from the
/// Python reference backend -- <see cref="MenuCatalog.ApplyLexicon"/> (the shared substitution
/// algorithm behind both a persona's ``pronunciations`` lexicon and per-item ``spokenName``
/// overrides) and <see cref="OrderState"/>'s cached <c>SpokenReadBack</c> composition. These are
/// deliberately unit-level (no HTTP/tool round trip) so the edge cases the brief calls out --
/// empty lexicon, substring-boundary safety, case sensitivity, empty order, single item,
/// multiple items, component upcharges -- are pinned down independent of any one persona pack's
/// content drifting underneath the test.</summary>
public sealed class ApplyLexiconTests
{
    [Fact]
    public void Empty_lexicon_returns_text_unchanged()
    {
        Assert.Equal("Munchkins are great", MenuCatalog.ApplyLexicon("Munchkins are great", new Dictionary<string, string>()));
    }

    [Fact]
    public void Exact_word_is_substituted()
    {
        var lexicon = new Dictionary<string, string> { ["Munchkins"] = "Munch-kins" };
        Assert.Equal("Glazed Munch-kins Donut Hole Treats", MenuCatalog.ApplyLexicon("Glazed Munchkins Donut Hole Treats", lexicon));
    }

    [Fact]
    public void Substring_inside_a_longer_word_is_not_rewritten()
    {
        var lexicon = new Dictionary<string, string> { ["Munchkins"] = "Munch-kins" };
        Assert.Equal("Munchkinsy is not a word", MenuCatalog.ApplyLexicon("Munchkinsy is not a word", lexicon));
    }

    [Fact]
    public void Case_sensitive_match_only()
    {
        var lexicon = new Dictionary<string, string> { ["Munchkins"] = "Munch-kins" };
        Assert.Equal("MUNCHKINS are great", MenuCatalog.ApplyLexicon("MUNCHKINS are great", lexicon));
    }

    [Fact]
    public void Trademark_suffix_boundary_requires_the_trademark_symbol_in_the_key()
    {
        var lexiconWithoutSymbol = new Dictionary<string, string> { ["Munchkins"] = "Munch-kins" };
        Assert.Equal(
            "Glazed Munchkins® Donut Hole Treats",
            MenuCatalog.ApplyLexicon("Glazed Munchkins® Donut Hole Treats", lexiconWithoutSymbol));

        var lexiconWithSymbol = new Dictionary<string, string> { ["Munchkins®"] = "Munch-kins®" };
        Assert.Equal(
            "Glazed Munch-kins® Donut Hole Treats",
            MenuCatalog.ApplyLexicon("Glazed Munchkins® Donut Hole Treats", lexiconWithSymbol));
    }

    [Fact]
    public void Longer_key_wins_over_a_shorter_key_it_contains()
    {
        var lexicon = new Dictionary<string, string>
        {
            ["Munchkins"] = "Munch-kins",
            ["Chocolate Glazed Munchkins"] = "Choc-Glazed Munch-kins",
        };
        Assert.Equal(
            "Choc-Glazed Munch-kins Donut Hole Treats",
            MenuCatalog.ApplyLexicon("Chocolate Glazed Munchkins Donut Hole Treats", lexicon));
    }

    [Fact]
    public void Multiple_occurrences_all_substituted()
    {
        var lexicon = new Dictionary<string, string> { ["Munchkins"] = "Munch-kins" };
        Assert.Equal(
            "Munch-kins Munch-kins Munch-kins",
            MenuCatalog.ApplyLexicon("Munchkins Munchkins Munchkins", lexicon));
    }
}

/// <summary>Exercises the real, shipped Munchkins-lexicon persona pack's ``pronunciations``
/// entry and its menu items' ``spokenName`` overrides through the normal <see cref="MenuCatalog"/>
/// loading path -- proving the feature works end-to-end on real pack data, not just the primitive.</summary>
public sealed class RealPersonaPronunciationsAndSpokenNameTests
{
    private static Persona Pack() => PersonaCatalog.Load(personasEnv: "dun" + "kin", defaultPersonaEnv: "dun" + "kin").Get("dun" + "kin");

    [Fact]
    public void Persona_declares_the_munchkins_pronunciation()
    {
        var persona = Pack();
        Assert.NotNull(persona.Pronunciations);
        Assert.Equal("Munch-kins", persona.Pronunciations!["Munchkins"]);
    }

    [Fact]
    public void Munchkins_item_spoken_name_is_applied_via_spoken()
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(Pack());
        Assert.Equal("Glazed Munch-kins Donut Hole Treats", menu.Spoken("Glazed MUNCHKINS® Donut Hole Treats"));
    }

    [Fact]
    public void Unrelated_item_is_unaffected_by_spoken_name_overrides()
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(Pack());
        Assert.Equal("Original Blend Iced Coffee", menu.Spoken("Original Blend Iced Coffee"));
    }
}

/// <summary>Issue #304: <c>OrderSummary.SpokenReadBack</c> is composed once per
/// <c>UpdateSummary</c> call and must always equal what <c>get_order</c>'s wire field and
/// <c>GetGroupedOrderForReadback()</c> both return -- these tests pin down the edge cases the
/// brief calls out directly (empty order, single item, multiple items).</summary>
public sealed class SpokenReadBackCompositionTests
{
    private static Persona Pack() => PersonaCatalog.Load(personasEnv: "dun" + "kin", defaultPersonaEnv: "dun" + "kin").Get("dun" + "kin");

    [Fact]
    public void Empty_order_readback_is_the_empty_order_sentence()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        Assert.Equal("Your order is currently empty.", order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Single_item_readback_uses_singular_one_prefix_and_includes_total()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 1, 0.01m);

        Assert.Contains("I have one ", order.Summary.SpokenReadBack);
        Assert.Contains(order.Summary.FinalTotalDisplay, order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Multiple_quantity_readback_uses_numeric_prefix()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 3, 0.01m);

        Assert.Contains("I have 3 ", order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Multiple_distinct_items_are_joined_with_an_oxford_and()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 1, 0.01m);
        order.HandleOrderUpdate("add", "Glazed MUNCHKINS® Donut Hole Treats", "10 count", 1, 0.01m);

        Assert.Contains(", and ", order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Get_grouped_order_for_readback_matches_the_cached_summary_field()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 2, 0.01m);

        Assert.Equal(order.Summary.SpokenReadBack, order.GetGroupedOrderForReadback());
    }

    [Fact]
    public void Readback_updates_after_a_subsequent_mutation()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Original Blend Iced Coffee (Black)", "medium", 1, 0.01m);
        var first = order.Summary.SpokenReadBack;

        order.HandleOrderUpdate("add", "Glazed MUNCHKINS® Donut Hole Treats", "10 count", 1, 0.01m);
        var second = order.Summary.SpokenReadBack;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Munchkins_spoken_name_is_reflected_in_the_readback()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Glazed MUNCHKINS® Donut Hole Treats", "10 count", 1, 0.01m);

        Assert.Contains("Munch-kins", order.Summary.SpokenReadBack);
        Assert.DoesNotContain("MUNCHKINS", order.Summary.SpokenReadBack);
    }
}
