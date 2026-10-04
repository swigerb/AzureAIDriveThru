namespace ExtractProductionItems.Tests;

/// <summary>
/// Unit tests for ProductionItemsExtractor's pure helpers and its two JSON-reading entry points
/// (ExtractProductionItems, LoadUiItems), against small synthetic fixtures built directly in each
/// test -- fast, deterministic, and independent of the real checked-in persona fixtures (covered
/// separately, against the real Python twin, by PythonParityTests).
/// </summary>
public sealed class ProductionItemsExtractorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string WriteTempJson(string prefix, string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squanchy-extract-items-{prefix}-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    // ── NormalizeSizeName ────────────────────────────────────────────────

    [Theory]
    [InlineData("Mini Cherry Limeade", "Cherry Limeade", "Mini")]
    [InlineData("Sm Cherry Limeade", "Cherry Limeade", "Small")]
    [InlineData("Med Cherry Limeade", "Cherry Limeade", "Medium")]
    [InlineData("Lg Cherry Limeade", "Cherry Limeade", "Large")]
    [InlineData("RT 44 Cherry Limeade", "Cherry Limeade", "RT 44")]
    [InlineData("Rt. 44 Cherry Limeade", "Cherry Limeade", "RT 44")]
    public void NormalizeSizeName_RecognizesEachPrefix_RegardlessOfParentName(
        string childDisplayName, string parentDisplayName, string expected)
    {
        Assert.Equal(expected, ProductionItemsExtractor.NormalizeSizeName(childDisplayName, parentDisplayName));
    }

    [Fact]
    public void NormalizeSizeName_StripsParentNameAndTrademarkSymbol_WhenNoPrefixMatches()
    {
        var result = ProductionItemsExtractor.NormalizeSizeName("Footlong Ocean Water\u00ae", "Ocean Water\u00ae");

        Assert.Equal("Footlong", result);
    }

    [Fact]
    public void NormalizeSizeName_FallsBackToStandard_WhenNothingDistinctiveRemainsAfterStripping()
    {
        var result = ProductionItemsExtractor.NormalizeSizeName("Cherry Limeade", "Cherry Limeade");

        Assert.Equal("Standard", result);
    }

    [Fact]
    public void NormalizeSizeName_DoesNotThrow_WhenParentDisplayNameIsEmpty()
    {
        // string.Replace(oldValue, newValue) throws ArgumentException for an empty oldValue --
        // product.get("displayName", "") can legitimately return "" for a malformed/incomplete
        // product entry, so this guards the exact regression a naive unconditional .Replace(parent, "")
        // would hit (Python's str.replace("", "") is a harmless no-op by contrast).
        var result = ProductionItemsExtractor.NormalizeSizeName("Footlong Drink", "");

        Assert.Equal("Footlong Drink", result);
    }

    // ── Normalize ────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_LowercasesStripsTrademarkSymbolsAndCollapsesWhitespace()
    {
        var result = ProductionItemsExtractor.Normalize("  Ocean Water\u00ae & Lime\u2122  Float  ");

        Assert.Equal("ocean water lime float", result);
    }

    [Fact]
    public void Normalize_ReplacesNonAlphanumericCharactersWithASpace_ThenCollapsesRuns()
    {
        var result = ProductionItemsExtractor.Normalize("Jalapeños!!");

        Assert.Equal("jalape os", result);
    }

    // ── ExtractProductionItems ───────────────────────────────────────────

    private const string NestedCategoryProductionJson = """
        {
          "menus": {
            "menu-1": {
              "products": {
                "p-combo": { "displayName": "Combo Meal", "price": 5 },
                "p-drink": {
                  "displayName": "Drink",
                  "price": 9.99,
                  "relatedProducts": { "alternatives": { "productGroups.sizes": {} } }
                },
                "p-mini": { "displayName": "Mini Drink", "price": 1.5 },
                "p-small": { "displayName": "Small Drink", "price": 2 },
                "p-recipe": { "displayName": "Recipe Base", "isRecipe": true, "price": 0 }
              },
              "categories": {
                "cat-top": {
                  "displayName": "Top",
                  "childRefs": {
                    "categories.cat-sub": {},
                    "products.p-combo": {},
                    "products.p-recipe": {}
                  }
                },
                "cat-sub": { "displayName": "Sub", "childRefs": { "products.p-drink": {} } },
                "cat-top2": { "displayName": "Top2", "childRefs": { "products.p-combo": {} } }
              },
              "productGroups": {
                "sizes": { "childRefs": { "products.p-mini": {}, "products.p-small": {} } }
              }
            }
          }
        }
        """;

    [Fact]
    public void ExtractProductionItems_WalksNestedCategoriesInDocumentOrder_AssigningTheLeafCategoryName()
    {
        var path = WriteTempJson("nested", NestedCategoryProductionJson);

        var items = ProductionItemsExtractor.ExtractProductionItems(path);

        // p-recipe is skipped (isRecipe); p-combo is reachable from BOTH cat-top and cat-top2,
        // but cat-top is walked first (categories.keys() document order) so it wins -- p-combo is
        // NOT duplicated, and keeps cat-top's name ("Top"), not cat-top2's ("Top2"). p-drink
        // (nested under cat-sub) gets the LEAF category name ("Sub"), not the top-level one
        // ("Top"). Order is the category-walk's own first-occurrence order (p-drink's
        // "categories.cat-sub" ref appears before "products.p-combo" in cat-top's own childRefs).
        Assert.Equal(["p-drink", "p-combo"], items.Select(i => i.ProductId));
        Assert.Equal(["Sub", "Top"], items.Select(i => i.Category));
    }

    [Fact]
    public void ExtractProductionItems_ResolvesSizeVariants_InProductGroupChildRefDocumentOrder()
    {
        var path = WriteTempJson("nested", NestedCategoryProductionJson);

        var items = ProductionItemsExtractor.ExtractProductionItems(path);
        var drink = items.Single(i => i.ProductId == "p-drink");

        Assert.Equal(
            [("Mini", 1.5), ("Small", 2.0)],
            drink.Sizes.Select(s => (s.Size, s.Price)));
    }

    [Fact]
    public void ExtractProductionItems_FallsBackToAStandardSize_UsingTheProductsOwnPrice_WhenNoVariantsResolve()
    {
        var path = WriteTempJson("nested", NestedCategoryProductionJson);

        var items = ProductionItemsExtractor.ExtractProductionItems(path);
        var combo = items.Single(i => i.ProductId == "p-combo");

        Assert.Equal([("Standard", 5.0)], combo.Sizes.Select(s => (s.Size, s.Price)));
    }

    [Fact]
    public void ExtractProductionItems_SkipsRecipes()
    {
        var path = WriteTempJson("nested", NestedCategoryProductionJson);

        var items = ProductionItemsExtractor.ExtractProductionItems(path);

        Assert.DoesNotContain(items, i => i.ProductId == "p-recipe");
    }

    [Fact]
    public void ExtractProductionItems_UsesTheFirstMenuInDocumentOrder_WhenMultipleMenusExist()
    {
        const string twoMenusJson = """
            {
              "menus": {
                "menu-first": {
                  "products": { "p-a": { "displayName": "A", "price": 1 } },
                  "categories": { "cat-a": { "displayName": "Cat A", "childRefs": { "products.p-a": {} } } },
                  "productGroups": {}
                },
                "menu-second": {
                  "products": { "p-b": { "displayName": "B", "price": 2 } },
                  "categories": { "cat-b": { "displayName": "Cat B", "childRefs": { "products.p-b": {} } } },
                  "productGroups": {}
                }
              }
            }
            """;
        var path = WriteTempJson("two-menus", twoMenusJson);

        var items = ProductionItemsExtractor.ExtractProductionItems(path);

        Assert.Equal(["p-a"], items.Select(i => i.ProductId));
    }

    // ── LoadUiItems ──────────────────────────────────────────────────────

    [Fact]
    public void LoadUiItems_FlattensEveryGroupsItemsWithItsOwnCategory_InDocumentOrder()
    {
        const string menuJson = """
            {
              "menuItems": [
                { "category": "Drinks", "items": [ { "name": "Cherry Limeade" }, { "name": "Ocean Water" } ] },
                { "category": "Combos", "items": [ { "name": "Bacon Combo" } ] }
              ]
            }
            """;
        var path = WriteTempJson("ui-menu", menuJson);

        var items = ProductionItemsExtractor.LoadUiItems(path);

        Assert.Equal(
            [("Cherry Limeade", "Drinks"), ("Ocean Water", "Drinks"), ("Bacon Combo", "Combos")],
            items.Select(i => (i.Name, i.Category)));
    }

    [Fact]
    public void LoadUiItems_ReturnsEmpty_WhenMenuItemsKeyIsAbsent()
    {
        var path = WriteTempJson("empty-ui-menu", "{}");

        var items = ProductionItemsExtractor.LoadUiItems(path);

        Assert.Empty(items);
    }
}
