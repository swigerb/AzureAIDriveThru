using UpdateMenuSizes;

namespace UpdateMenuSizes.Tests;

/// <summary>
/// Unit tests for MenuSizeUpdater (issue #16's first C# tooling port, the twin of
/// scripts/update_menu_sizes.py) against small synthetic fixtures -- fast, deterministic, and
/// independent of the real checked-in persona menu data. See PythonParityTests for the
/// real-Python-twin, real-fixture output-parity proof.
/// </summary>
public sealed class MenuSizeUpdaterTests : IDisposable
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
        var path = Path.Combine(Path.GetTempPath(), $"squanchy-update-menu-sizes-{prefix}-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// A synthetic product-search-map fixture, standing in for a persona's own
    /// product_search_map.json (externalized from this library per PR #224 review R1 -- see
    /// MenuSizeUpdater.LoadProductSearchMap). Covers only the product name these UpdateMenu tests
    /// use ("Cherry Limeade"); "Not A Tracked Item" is deliberately absent so the
    /// not-in-the-map SKIP path stays exercised.
    /// </summary>
    private string WriteDefaultProductSearchMap() =>
        WriteTempJson("search-map", """{ "Cherry Limeade": "Cherry Limeade" }""");

    // ---- ExtractSize -------------------------------------------------------------------------

    [Theory]
    [InlineData("Mini Cherry Limeade", "mini", "Cherry Limeade")]
    [InlineData("Sm Cherry Limeade", "small", "Cherry Limeade")]
    [InlineData("Small Cherry Limeade", "small", "Cherry Limeade")]
    [InlineData("Med Cherry Limeade", "medium", "Cherry Limeade")]
    [InlineData("Medium Cherry Limeade", "medium", "Cherry Limeade")]
    [InlineData("Lg Cherry Limeade", "large", "Cherry Limeade")]
    [InlineData("Large Cherry Limeade", "large", "Cherry Limeade")]
    [InlineData("RT 44\u00ae Cherry Limeade", "rt 44", "Cherry Limeade")]
    public void ExtractSize_MatchesKnownPrefixes(string displayName, string expectedSizeKey, string expectedBase)
    {
        var (sizeKey, baseName) = MenuSizeUpdater.ExtractSize(displayName);

        Assert.Equal(expectedSizeKey, sizeKey);
        Assert.Equal(expectedBase, baseName);
    }

    [Fact]
    public void ExtractSize_NoKnownPrefix_ReturnsNullKeyAndOriginalName()
    {
        var (sizeKey, baseName) = MenuSizeUpdater.ExtractSize("Cherry Limeade");

        Assert.Null(sizeKey);
        Assert.Equal("Cherry Limeade", baseName);
    }

    [Fact]
    public void ExtractSize_LiteralEscapedRtPrefix_NeverMatchesRealDisplayNames()
    {
        // The Python twin's SIZE_PREFIXES has a raw-string entry r"RT 44\u00ae " that is the 12
        // literal characters "RT 44\u00ae " (backslash, u, 0, 0, a, e), not the (R) symbol -- dead
        // code that can never match a real production displayName. This port must reproduce that
        // exactly (not "fix" it), so a real (R) name must still match via the NEXT prefix entry.
        var (sizeKey, baseName) = MenuSizeUpdater.ExtractSize("RT 44\u00ae Blue Raspberry Slush");

        Assert.Equal("rt 44", sizeKey);
        Assert.Equal("Blue Raspberry Slush", baseName);
    }

    // ---- FindSizesForProduct ------------------------------------------------------------------

    [Fact]
    public void FindSizesForProduct_FirstMatchPerSizeKeyWins_InDocumentOrder()
    {
        List<MenuSizeUpdater.ProductEntry> products =
        [
            new("Small Cherry Limeade", 2.49m),
            new("Small Cherry Limeade", 9.99m), // a later duplicate must NOT win
            new("Large Cherry Limeade", 3.39m),
        ];

        var sizes = MenuSizeUpdater.FindSizesForProduct(products, "Cherry Limeade");

        Assert.Equal(2.49m, sizes["small"].Price);
        Assert.Equal(3.39m, sizes["large"].Price);
    }

    [Fact]
    public void FindSizesForProduct_SkipsNonPositivePrice()
    {
        List<MenuSizeUpdater.ProductEntry> products =
        [
            new("Small Cherry Limeade", 0m),
            new("Large Cherry Limeade", -1m),
        ];

        var sizes = MenuSizeUpdater.FindSizesForProduct(products, "Cherry Limeade");

        Assert.Empty(sizes);
    }

    [Fact]
    public void FindSizesForProduct_ExcludesCherryLimeadeSlushAndDietVariants()
    {
        List<MenuSizeUpdater.ProductEntry> products =
        [
            new("Small Cherry Limeade", 2.49m),
            new("Large Cherry Limeade Slush", 2.99m), // different size key than the legit entry
            new("Medium Diet Cherry Limeade", 1.99m),  // so a broken exclusion is actually visible
        ];

        var sizes = MenuSizeUpdater.FindSizesForProduct(products, "Cherry Limeade");

        var size = Assert.Single(sizes);
        Assert.Equal("small", size.Key);
        Assert.Equal(2.49m, size.Value.Price);
    }

    [Fact]
    public void FindSizesForProduct_ExcludesOceanWaterDietVariants()
    {
        // The twin's Ocean Water exclusion tests the FULL display name (`name.lower()`) for
        // "diet", not the prefix-stripped base -- preserved exactly here, though in practice no
        // SizePrefixes entry contains "diet", so checking the full name vs. the stripped base is
        // not observably different for any realistic input (the distinction is about matching the
        // twin's exact source, not a reachable behavior difference).
        List<MenuSizeUpdater.ProductEntry> products =
        [
            new("Small Ocean Water", 2.49m),
            new("Large Diet Ocean Water", 1.99m), // different size key, so a broken exclusion shows up
        ];

        var sizes = MenuSizeUpdater.FindSizesForProduct(products, "Ocean Water");

        var size = Assert.Single(sizes);
        Assert.Equal("small", size.Key);
    }

    [Fact]
    public void FindSizesForProduct_RequiresSearchTermSubstringMatch()
    {
        List<MenuSizeUpdater.ProductEntry> products = [new("Small Blue Raspberry Slush", 2.19m)];

        var sizes = MenuSizeUpdater.FindSizesForProduct(products, "Cherry Limeade");

        Assert.Empty(sizes);
    }

    // ---- UpdateMenu ----------------------------------------------------------------------------

    private const string ProductionFixture = """
        {
          "menus": {
            "menu-1": {
              "products": {
                "p-mini": { "displayName": "Mini Cherry Limeade", "price": 1.59 },
                "p-small": { "displayName": "Small Cherry Limeade", "price": 2.49 },
                "p-large": { "displayName": "Large Cherry Limeade", "price": 3.39 },
                "p-zero": { "displayName": "Medium Cherry Limeade", "price": 0 }
              }
            }
          }
        }
        """;

    [Fact]
    public void UpdateMenu_AddsMissingSizes_AndPreservesOtherFields()
    {
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Drinks",
                  "items": [
                    {
                      "name": "Cherry Limeade",
                      "description": "unchanged description",
                      "sizes": [ { "size": "small", "price": 2.49 } ]
                    }
                  ]
                }
              ]
            }
            """);

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Equal(1, result.UpdatedCount);
        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("\"description\": \"unchanged description\"", updatedJson);
        Assert.Contains("\"mini\"", updatedJson);
        Assert.Contains("\"large\"", updatedJson);
    }

    [Fact]
    public void UpdateMenu_SkipsItemsNotInProductSearchMap()
    {
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Food",
                  "items": [
                    { "name": "Not A Tracked Item", "sizes": [ { "size": "small", "price": 1.0 } ] }
                  ]
                }
              ]
            }
            """);
        var before = File.ReadAllText(menuPath);

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Equal(0, result.UpdatedCount);
        // Untracked items must round-trip unchanged in content (formatting may differ).
        Assert.Contains("\"Not A Tracked Item\"", File.ReadAllText(menuPath));
        Assert.Contains("Not A Tracked Item", before);
    }

    [Fact]
    public void UpdateMenu_SkipsWhenNoProductionDataFound()
    {
        var productionPath = WriteTempJson("prod", """{ "menus": { "menu-1": { "products": {} } } }""");
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Drinks",
                  "items": [
                    { "name": "Cherry Limeade", "sizes": [ { "size": "small", "price": 2.49 } ] }
                  ]
                }
              ]
            }
            """);

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Equal(0, result.UpdatedCount);
        Assert.Contains(result.Log, line => line.Contains("SKIP Cherry Limeade: no production data found"));
    }

    [Fact]
    public void UpdateMenu_IsIdempotent_SecondRunReportsZeroUpdates()
    {
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Drinks",
                  "items": [
                    { "name": "Cherry Limeade", "sizes": [ { "size": "small", "price": 2.49 } ] }
                  ]
                }
              ]
            }
            """);

        var first = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());
        var second = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Equal(1, first.UpdatedCount);
        Assert.Equal(0, second.UpdatedCount);
    }

    // ---- LoadProductSearchMap (PR #224 review R1: externalized to a persona data file) --------

    [Fact]
    public void LoadProductSearchMap_ParsesFlatJsonObject()
    {
        var path = WriteTempJson("search-map", """
            { "Cherry Limeade": "Cherry Limeade", "Ocean Water\u00ae": "Ocean Water" }
            """);

        var map = MenuSizeUpdater.LoadProductSearchMap(path);

        Assert.Equal("Cherry Limeade", map["Cherry Limeade"]);
        Assert.Equal("Ocean Water", map["Ocean Water\u00ae"]);
    }

    [Fact]
    public void LoadProductSearchMap_RejectsEmptyMap()
    {
        var path = WriteTempJson("search-map", "{}");

        Assert.Throws<InvalidDataException>(() => MenuSizeUpdater.LoadProductSearchMap(path));
    }

    [Fact]
    public void LoadProductSearchMap_RejectsNonStringValue()
    {
        // PR #224 review R3: a non-string value (null, a number, an object/array -- here, a typo'd
        // null) must not silently become "" -- FindSizesForProduct's `name.Contains(searchLower)`
        // check would then match EVERY production displayName (an empty string is a substring of
        // everything), corrupting that menu item's sizes with whatever product happens to be
        // first in production order instead of visibly failing. Mutation check: reverting
        // LoadProductSearchMap's non-string branch back to `string.Empty` makes this assertion
        // fail (no exception thrown).
        var path = WriteTempJson("search-map", """{ "Cherry Limeade": null }""");

        var ex = Assert.Throws<InvalidDataException>(() => MenuSizeUpdater.LoadProductSearchMap(path));
        Assert.Contains("Cherry Limeade", ex.Message);
    }

    // ---- Output parity with the Python twin (PR #224 review R2/R3) ----------------------------

    [Theory]
    [InlineData(1.50, "1.5")] // the issue's own example: Python's float repr drops the trailing zero
    [InlineData(6.00, "6.0")] // but a "whole" float still gets ".0", unlike .NET's default double.ToString()
    [InlineData(3.49, "3.49")]
    [InlineData(0.50, "0.5")]
    [InlineData(100.00, "100.0")]
    public void PythonFloatRepr_MatchesPythonJsonFloatFormatting(decimal value, string expected)
    {
        Assert.Equal(expected, MenuSizeUpdater.PythonFloatRepr(value));
    }

    [Fact]
    public void UpdateMenu_WritesTrailingZeroPricesTheWayPythonWould()
    {
        // A production price of 1.50 is what motivated this fix (PR #224 review R2): a C#
        // decimal parsed from "1.50" keeps its trailing zero, but Python's json.load parses the
        // same literal as the float 1.5, and json.dump then writes "1.5" -- not "1.50". Mutation
        // check: reverting MenuSizeUpdater.PythonFloatRepr (or the ["price"] = ... line back to
        // assigning the decimal directly) makes this assertion fail with "1.50" in the output.
        var productionPath = WriteTempJson("prod", """
            {
              "menus": { "menu-1": { "products": {
                "p-small": { "displayName": "Small Cherry Limeade", "price": 1.50 }
              } } }
            }
            """);
        var menuPath = WriteTempJson("menu", """
            { "menuItems": [ { "name": "Drinks", "items": [
                { "name": "Cherry Limeade", "sizes": [] }
            ] } ] }
            """);

        MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("\"price\": 1.5", updatedJson);
        Assert.DoesNotContain("1.50", updatedJson);
    }

    [Fact]
    public void UpdateMenu_WritesWholeNumberPricesAsIntegers_LikePythonWould()
    {
        // PR #224 review R4: a production "price": 2 (no decimal point) is a Python int --
        // json.load parses it as 2, and json.dump writes it back as "2", never "2.0". The prior
        // fix (PythonFloatRepr, above) assumed every production price has a decimal point and
        // always appended ".0" when the price happened to be a whole number, which was correct for
        // "price": 2.0 but wrong for "price": 2. Mutation check: reverting
        // MenuSizeUpdater.FormatPriceLikePython to always call PythonFloatRepr (the pre-fix
        // behavior) makes this assertion fail with "2.0" in the output.
        var productionPath = WriteTempJson("prod", """
            {
              "menus": { "menu-1": { "products": {
                "p-small": { "displayName": "Small Cherry Limeade", "price": 2 }
              } } }
            }
            """);
        var menuPath = WriteTempJson("menu", """
            { "menuItems": [ { "name": "Drinks", "items": [
                { "name": "Cherry Limeade", "sizes": [] }
            ] } ] }
            """);

        MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("\"price\": 2", updatedJson);
        Assert.DoesNotContain("2.0", updatedJson);
    }

    // ---- FormatPriceLikePython (PR #224 review R4) -----------------------------------------

    [Theory]
    [InlineData(2, "2", "2")] // a whole-number production price: Python's int stays "2", not "2.0"
    [InlineData(1.50, "1.50", "1.5")] // a decimal-point price still goes through PythonFloatRepr
    [InlineData(2, null, "2.0")] // no original text available (e.g. a test-constructed ProductEntry)
    public void FormatPriceLikePython_PrefersOriginalIntegerTextOverFloatRepr(
        decimal price, string? priceText, string expected)
    {
        Assert.Equal(expected, MenuSizeUpdater.FormatPriceLikePython(price, priceText));
    }

    [Fact]
    public void LoadProductionProducts_CapturesOriginalPriceTextForIntAndFloatLiterals()
    {
        var productionPath = WriteTempJson("prod", """
            {
              "menus": { "menu-1": { "products": {
                "p-int": { "displayName": "Small Cherry Limeade", "price": 2 },
                "p-float": { "displayName": "Large Cherry Limeade", "price": 1.50 }
              } } }
            }
            """);

        var products = MenuSizeUpdater.LoadProductionProducts(productionPath);

        Assert.Equal("2", products.Single(p => p.DisplayName == "Small Cherry Limeade").PriceText);
        Assert.Equal("1.50", products.Single(p => p.DisplayName == "Large Cherry Limeade").PriceText);
    }

    [Fact]
    public void UpdateMenu_UpdatedLogLine_UsesPythonListReprFormat()
    {
        // Python's log line embeds `[s['size'] for s in new_sizes]`, i.e. Python's list repr:
        // ['small'], single-quoted -- not C#'s default [small].
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            { "menuItems": [ { "name": "Drinks", "items": [
                { "name": "Cherry Limeade", "sizes": [] }
            ] } ] }
            """);

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Contains(result.Log, line => line.Contains("['mini', 'small', 'large']"));
    }

    [Fact]
    public void UpdateMenu_NonAsciiCharactersAreWrittenRaw_LikePythonEnsureAsciiFalse()
    {
        // Python's json.dump(..., ensure_ascii=False) writes non-ASCII characters (here, an
        // emoji and a registered-trademark sign) as raw UTF-8 bytes rather than \uXXXX escapes.
        // Mutation check: swapping PythonJsonEncoder back for JavaScriptEncoder.Default or
        // .UnsafeRelaxedJsonEscaping makes this assertion fail (both still escape these).
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            { "menuItems": [ { "name": "Drinks", "items": [
                { "name": "Cherry Limeade", "description": "Fan favorite \ud83d\ude00 Ocean Water\u00ae", "sizes": [] }
            ] } ] }
            """);

        MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("Fan favorite \U0001F600 Ocean Water\u00ae", updatedJson);
        Assert.DoesNotContain("\\u00ae", updatedJson);
        Assert.DoesNotContain("\\ud83d", updatedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateMenu_WritesEnvironmentNewLine_IncludingTrailingNewline()
    {
        // Python's open(..., "w") text-mode translates every "\n" it writes to the OS line
        // separator (CRLF on Windows, LF elsewhere), including the final explicit f.write("\n").
        // File.WriteAllText does not do this translation on its own -- MenuSizeUpdater must use
        // Environment.NewLine explicitly for the trailing newline (and configure
        // JsonSerializerOptions.NewLine the same way) to match.
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            { "menuItems": [ { "name": "Drinks", "items": [
                { "name": "Cherry Limeade", "sizes": [] }
            ] } ] }
            """);

        MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        var bytes = File.ReadAllBytes(menuPath);
        var expectedTrailer = System.Text.Encoding.UTF8.GetBytes(Environment.NewLine);
        Assert.Equal(expectedTrailer, bytes[^expectedTrailer.Length..]);
    }

    [Fact]
    public void UpdateMenu_NormalizesUntouchedTrailingZeroNumbers_ElsewhereInTheTree()
    {
        // Rick's case 1 (PR #224 review R3): Python's json.dump re-serializes EVERY float-literal
        // number it parsed, not only the size/price fields UpdateMenu itself changed. An untouched
        // item's price (here, "Not A Tracked Item" -- absent from the product search map, so
        // UpdateMenu never looks at it) must still come out as "2.5", not "2.50", because Python's
        // json.load/json.dump round-trip would rewrite it too. Mutation check: removing the
        // NormalizeNumberLiteralsLikePythonJsonDump call (or its whole-tree recursion into objects
        // it didn't directly construct) makes this assertion fail with "2.50" still present.
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Food",
                  "items": [
                    { "name": "Not A Tracked Item", "sizes": [ { "size": "small", "price": 2.50 } ] }
                  ]
                }
              ]
            }
            """);

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        Assert.Equal(0, result.UpdatedCount); // never touched by the reconciliation logic itself
        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("\"price\": 2.5", updatedJson);
        Assert.DoesNotContain("2.50", updatedJson);
    }

    [Fact]
    public void UpdateMenu_LeavesWholeNumberIntegerLiteralsUnchanged_ElsewhereInTheTree()
    {
        // Rick's case 2 (PR #224 review R3): a number token with no '.', 'e', or 'E' parses as a
        // Python int, not a float, and json.dump writes an int back exactly as-is -- "2" stays
        // "2", it must NOT become "2.0" (unlike a genuine float literal such as "6.0", which stays
        // "6.0" -- see PythonFloatRepr_MatchesPythonJsonFloatFormatting). Mutation check: applying
        // PythonFloatRepr to every number regardless of its original token (dropping the
        // IndexOfAny('.', 'e', 'E') >= 0 guard in NormalizeChildInPlace) makes this assertion fail
        // by turning "quantity": 2 into "quantity": 2.0.
        var productionPath = WriteTempJson("prod", ProductionFixture);
        var menuPath = WriteTempJson("menu", """
            {
              "menuItems": [
                {
                  "name": "Food",
                  "items": [
                    {
                      "name": "Not A Tracked Item",
                      "quantity": 2,
                      "sizes": [ { "size": "small", "price": 1.0 } ]
                    }
                  ]
                }
              ]
            }
            """);

        MenuSizeUpdater.UpdateMenu(productionPath, menuPath, WriteDefaultProductSearchMap());

        var updatedJson = File.ReadAllText(menuPath);
        Assert.Contains("\"quantity\": 2,", updatedJson);
        Assert.DoesNotContain("\"quantity\": 2.0", updatedJson);
    }
}
