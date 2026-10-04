using UpdateMenuSizes;

namespace UpdateMenuSizes.Tests;

/// <summary>
/// Unit tests for MenuSizeUpdater (issue #16's first C# tooling port, the twin of
/// scripts/update_menu_sizes.py) against small synthetic fixtures -- fast, deterministic, and
/// independent of the real personas/sonic/menu/** data. See PythonParityTests for the
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

        Assert.Equal(2.49m, sizes["small"]);
        Assert.Equal(3.39m, sizes["large"]);
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
        Assert.Equal(2.49m, size.Value);
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

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);

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

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);

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

        var result = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);

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

        var first = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);
        var second = MenuSizeUpdater.UpdateMenu(productionPath, menuPath);

        Assert.Equal(1, first.UpdatedCount);
        Assert.Equal(0, second.UpdatedCount);
    }
}
