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

/// <summary>Exercises EVERY shipped persona pack's own ``pronunciations`` entry and its menu
/// items' ``spokenName`` overrides through the normal <see cref="PersonaCatalog.Load"/>
/// discovery path and <see cref="MenuCatalog"/> loading -- proving the feature works end-to-end
/// on whatever real pack data happens to declare it, without naming any one pack's id.
/// #313 (coordinator fix request, round 4): made brand-neutral -- generic over every pack
/// <see cref="PersonaCatalog.Load"/> discovers from disk, so this class carries no new
/// rebrand_baseline.yaml entry. Anything needing a known, deterministic value uses
/// <see cref="ZetaFixture"/> instead of a real pack.</summary>
public sealed class ShippedPersonaPronunciationAndSpokenNameTests
{
    private static PersonaCatalog AllShippedPacks() => PersonaCatalog.Load();

    [Fact]
    public void Every_shipped_packs_pronunciation_lexicon_is_applied()
    {
        var catalog = AllShippedPacks();
        var anyPackDeclaresALexicon = false;

        foreach (var id in catalog.Ids)
        {
            var persona = catalog.Get(id);
            var pronunciations = persona.Pronunciations;
            if (pronunciations is not { Count: > 0 })
            {
                continue;
            }

            anyPackDeclaresALexicon = true;
            foreach (var (rawKey, spokenForm) in pronunciations)
            {
                var result = MenuCatalog.ApplyLexicon($"Example {rawKey} text", pronunciations);
                Assert.True(result.Contains(spokenForm), $"{id}: {rawKey} was not respelled");
                Assert.True(!result.Contains(rawKey), $"{id}: raw key {rawKey} survived");
            }
        }

        Assert.True(
            anyPackDeclaresALexicon,
            "expected at least one shipped persona pack to declare a pronunciations lexicon " +
            "(otherwise this test passes vacuously)");
    }

    [Fact]
    public void Every_shipped_packs_item_spoken_name_is_applied_via_spoken()
    {
        var catalog = AllShippedPacks();
        var anyPackDeclaresASpokenName = false;

        foreach (var id in catalog.Ids)
        {
            var persona = catalog.Get(id);
            var menu = PersonaOrderFactory.GetMenuCatalog(persona);

            foreach (var category in persona.Menu.MenuItems)
            {
                foreach (var item in category.Items)
                {
                    if (string.IsNullOrEmpty(item.SpokenName))
                    {
                        continue;
                    }

                    anyPackDeclaresASpokenName = true;
                    var result = menu.Spoken(item.Name);
                    Assert.True(result == item.SpokenName, $"{id}: {item.Name} was not spoken as {item.SpokenName}");
                    if (item.Name != item.SpokenName)
                    {
                        Assert.True(!result.Contains(item.Name), $"{id}: raw name {item.Name} survived");
                    }
                }
            }
        }

        Assert.True(
            anyPackDeclaresASpokenName,
            "expected at least one shipped persona pack to declare an item spokenName override " +
            "(otherwise this test passes vacuously)");
    }

    [Fact]
    public void Unrelated_item_is_unaffected_by_spoken_name_overrides()
    {
        // test-zeta's own fixture item has no spokenName override, so Spoken() must be a no-op.
        var menu = PersonaOrderFactory.GetMenuCatalog(ZetaFixture.Load());
        Assert.Equal("Zeta Cola", menu.Spoken("Zeta Cola"));
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
