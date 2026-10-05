using Backend.Ordering;
using Backend.Personas;
using Backend.Tests.TestSupport;

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
/// loading path -- proving the feature works end-to-end on real pack data, not just the primitive.
/// #313 (Rick's re-review, item 1): this class's whole point is to validate the REAL, shipped
/// pack's own lexicon data (the synthetic test-zeta fixture has no Munchkins-equivalent
/// ``pronunciations`` entry to exercise), so it names the real persona id directly rather than
/// string-concatenating around rebrand_scan.py's brand-word guard -- see this file's own
/// rebrand_baseline.yaml entry (issue #304).</summary>
public sealed class RealPersonaPronunciationsAndSpokenNameTests
{
    private static Persona Pack() => PersonaCatalog.Load(personasEnv: "dunkin", defaultPersonaEnv: "dunkin").Get("dunkin");

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
/// brief calls out directly (empty order, single item, multiple items). #313 (Rick's re-review,
/// item 1): none of these cases needs the real, shipped Munchkins-lexicon pack's own pronunciation
/// data -- only its generic SpokenReadBack composition AND (the last test) a <c>spokenName</c>
/// override being reflected in the read-back, which the synthetic <see cref="ZetaFixture"/>'s own
/// "ZORBS® Bite Treats" item (spokenName "Zorb Bite Treats") already covers -- so this class runs
/// against that TEST-ONLY fixture pack instead of naming a real persona id in its own source.
/// </summary>
public sealed class SpokenReadBackCompositionTests
{
    private static Persona Pack() => ZetaFixture.Load();

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
        order.HandleOrderUpdate("add", "Zeta Cola", "regular", 1, 0.01m);

        Assert.Contains("I have one ", order.Summary.SpokenReadBack);
        // #313 (Rick's review, item 1/2): the read-back speaks the total in words
        // (Money.FormatMoneySpoken), never the "$X.XX" digit display -- a realtime model must
        // never see two different renderings of the same total.
        Assert.Contains(Money.FormatMoneySpoken(order.Summary.FinalTotal), order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Multiple_quantity_readback_uses_numeric_prefix()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Zeta Cola", "regular", 3, 0.01m);

        // #313 (Rick's review, item 1/2): a bare digit quantity is ambiguous next to a count-based
        // size, so the read-back spells it out as a word.
        Assert.Contains("I have three ", order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Multiple_distinct_items_are_joined_with_an_oxford_and()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Zeta Cola", "regular", 1, 0.01m);
        order.HandleOrderUpdate("add", "ZORBS® Bite Treats", "10 count", 1, 0.01m);

        Assert.Contains(", and ", order.Summary.SpokenReadBack);
    }

    [Fact]
    public void Get_grouped_order_for_readback_matches_the_cached_summary_field()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Zeta Cola", "regular", 2, 0.01m);

        Assert.Equal(order.Summary.SpokenReadBack, order.GetGroupedOrderForReadback());
    }

    [Fact]
    public void Readback_updates_after_a_subsequent_mutation()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "Zeta Cola", "regular", 1, 0.01m);
        var first = order.Summary.SpokenReadBack;

        order.HandleOrderUpdate("add", "ZORBS® Bite Treats", "10 count", 1, 0.01m);
        var second = order.Summary.SpokenReadBack;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Zorbs_spoken_name_is_reflected_in_the_readback()
    {
        var order = PersonaOrderFactory.CreateOrderState(Pack());
        order.HandleOrderUpdate("add", "ZORBS® Bite Treats", "10 count", 1, 0.01m);

        Assert.Contains("Zorb Bite Treats", order.Summary.SpokenReadBack);
        Assert.DoesNotContain("ZORBS", order.Summary.SpokenReadBack);
    }
}
