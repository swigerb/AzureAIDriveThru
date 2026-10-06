namespace ExtractProductionItems.Tests;

/// <summary>
/// Unit tests for ProductionItemsExtractor.BuildReport, constructing ProductionItem/UiItem records
/// directly (no JSON fixtures needed) so each ordering/formatting nuance can be pinned down in
/// isolation: the "most_common()"-style stable tie-break (first-occurrence order wins among
/// equal-count categories, NOT alphabetical), the code-point (not culture-aware, and -- for an
/// astral character -- not the same as StringComparer.Ordinal either) name sort within a category,
/// and the gap-analysis "(none)"/"(none — UI is clean)" branches.
/// </summary>
public sealed class ReportBuilderTests
{
    private static ProductionItem Item(string productId, string name, string category, double price = 1.0) =>
        new(productId, name, Description: "", ImageUrl: "", category, Sizes: [new SizeVariant("Standard", price)]);

    [Fact]
    public void BuildReport_PreservesFirstOccurrenceOrder_ForCategoriesTiedOnCount()
    {
        // Zeta appears first in `production` and Alpha second, both with exactly 1 item --
        // Counter.most_common()'s sort is documented STABLE even with reverse=True, so ties must
        // keep "Zeta" ahead of "Alpha" in the report despite "Alpha" < "Zeta" alphabetically. A
        // naive "also sort categories alphabetically as a tie-break" change (or swapping
        // OrderByDescending for an unstable sort) would flip this and should fail this test.
        var production = new List<ProductionItem>
        {
            Item("p-1", "Zeta Thing", "Zeta"),
            Item("p-2", "Alpha Thing", "Alpha"),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems: []);
        var reportText = string.Join("\n", report);

        var zetaIndex = reportText.IndexOf("  Zeta  (1 items)", StringComparison.Ordinal);
        var alphaIndex = reportText.IndexOf("  Alpha  (1 items)", StringComparison.Ordinal);
        Assert.True(zetaIndex >= 0 && alphaIndex >= 0);
        Assert.True(zetaIndex < alphaIndex, "Zeta (first-occurring, tied count) must be listed before Alpha.");
    }

    [Fact]
    public void BuildReport_RanksCategoriesByDescendingItemCount()
    {
        var production = new List<ProductionItem>
        {
            Item("p-1", "One", "Rare"),
            Item("p-2", "Two", "Common"),
            Item("p-3", "Three", "Common"),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems: []);
        var reportText = string.Join("\n", report);

        var commonIndex = reportText.IndexOf("  Common  (2 items)", StringComparison.Ordinal);
        var rareIndex = reportText.IndexOf("  Rare  (1 items)", StringComparison.Ordinal);
        Assert.True(commonIndex >= 0 && rareIndex >= 0);
        Assert.True(commonIndex < rareIndex, "The 2-item category must be listed before the 1-item category.");
    }

    [Fact]
    public void BuildReport_SortsItemsWithinACategory_ByCodePoint_NotCultureAware()
    {
        // Python's sorted(key=lambda x: x["name"]) compares actual Unicode CODE POINTS, not
        // culture-aware: all uppercase letters sort before all lowercase letters. "apple"
        // (lowercase 'a' = 0x61) therefore sorts AFTER "Banana"/"Cherry" (uppercase 'B'/'C' =
        // 0x42/0x43) -- a culture-aware/case-insensitive comparer would wrongly place "apple"
        // first. (For plain ASCII like this, codepoint order and StringComparer.Ordinal's UTF-16
        // code-UNIT order agree -- see the astral-character test below for where they diverge.)
        var production = new List<ProductionItem>
        {
            Item("p-1", "Cherry", "Drinks"),
            Item("p-2", "apple", "Drinks"),
            Item("p-3", "Banana", "Drinks"),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems: []);

        var bulletLines = report.Where(l => l.StartsWith("  • ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["  • Banana", "  • Cherry", "  • apple"], bulletLines);
    }

    [Fact]
    public void BuildReport_SortsAnAstralCharacter_ByItsActualCodePointValue_NotItsUtf16SurrogateValue()
    {
        // PR #243 review R2: U+1F600 '😀' (an astral/supplementary-plane emoji, codepoint 128512)
        // vs U+FF01 '！' (an ordinary BMP character, codepoint 65281). Python's str comparison (by
        // actual codepoint) places '！' (65281) before '😀' (128512). .NET's
        // StringComparer.Ordinal -- which BuildReport used before this fix -- compares UTF-16 code
        // UNITS instead: '😀' encodes as the surrogate pair U+D83D U+DE00, and the leading
        // surrogate U+D83D (55357) is numerically LESS than U+FF01 (65281), so plain ordinal
        // comparison got this backwards (emoji sorting first). BuildReport must reproduce Python's
        // actual order.
        var production = new List<ProductionItem>
        {
            Item("p-1", "\U0001F600 Combo", "Drinks"),
            Item("p-2", "\uFF01 Combo", "Drinks"),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems: []);

        var bulletLines = report.Where(l => l.StartsWith("  • ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["  • \uFF01 Combo", "  • \U0001F600 Combo"], bulletLines);
    }

    [Fact]
    public void BuildReport_FormatsSizesAsLabelEqualsDollarAmount_WithTwoDecimalPlaces()
    {
        var production = new List<ProductionItem>
        {
            new("p-1", "Combo", Description: "", ImageUrl: "", Category: "Food",
                Sizes: [new SizeVariant("Mini", 1.5), new SizeVariant("Small", 2.0)]),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems: []);

        Assert.Contains("    Sizes: Mini=$1.50, Small=$2.00", report);
    }

    [Fact]
    public void BuildReport_PrintsUiIsCleanMessage_WhenEveryUiItemAlsoExistsInProduction()
    {
        var production = new List<ProductionItem> { Item("p-1", "Cherry Limeade", "Drinks") };
        var uiItems = new List<UiItem> { new("Cherry Limeade", "Drinks") };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems);

        Assert.Contains("     (none — UI is clean)", report);
    }

    [Fact]
    public void BuildReport_PrintsNoneMessage_WhenEveryProductionItemAlsoExistsInUi()
    {
        var production = new List<ProductionItem> { Item("p-1", "Cherry Limeade", "Drinks") };
        var uiItems = new List<UiItem> { new("Cherry Limeade", "Drinks") };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems);

        Assert.Contains("     (none)", report);
    }

    [Fact]
    public void BuildReport_ListsGapItemsAlphabetically_UsingTheNormalizedName()
    {
        var production = new List<ProductionItem>();
        var uiItems = new List<UiItem>
        {
            new("Zeta Combo\u00ae", "Combos"),
            new("Alpha Combo", "Combos"),
        };

        var report = ProductionItemsExtractor.BuildReport(production, uiItems);

        var zetaIndex = report.ToList().IndexOf("     • zeta combo");
        var alphaIndex = report.ToList().IndexOf("     • alpha combo");
        Assert.True(alphaIndex >= 0 && zetaIndex >= 0);
        Assert.True(alphaIndex < zetaIndex);
    }

    [Fact]
    public void BuildReport_SplitsEmbeddedNewlinesAcrossSeparateDisplayLines_LikePythonPrint()
    {
        // print(f"\nTotal production items: {n}") must contribute a BLANK line followed by the
        // text line, not one line containing a literal "\n" -- confirms the Append helper's
        // s.Split('\n') model against a case callers can assert on precisely.
        var report = ProductionItemsExtractor.BuildReport(production: [], uiItems: []);
        var lines = report.ToList();

        var totalIndex = lines.IndexOf("Total production items: 0");
        Assert.True(totalIndex > 0);
        Assert.Equal("", lines[totalIndex - 1]);
    }
}
