namespace ExtractProductionItems.Tests;

/// <summary>
/// Direct unit tests for <see cref="CodePointComparer"/> (PR #243 review R2): proves it agrees with
/// <see cref="StringComparer.Ordinal"/> for ordinary BMP-only strings, but produces Python's actual
/// codepoint order -- not StringComparer.Ordinal's UTF-16 code-UNIT order -- once an astral
/// character (a UTF-16 surrogate pair) is involved.
/// </summary>
public sealed class CodePointComparerTests
{
    [Theory]
    [InlineData("apple", "Banana")] // lowercase 'a' (0x61) > uppercase 'B' (0x42): "apple" sorts after.
    [InlineData("Alpha", "Alpha")]
    [InlineData("", "a")]
    public void Compare_AgreesWithOrdinal_ForBmpOnlyStrings(string x, string y)
    {
        Assert.Equal(
            Math.Sign(StringComparer.Ordinal.Compare(x, y)),
            Math.Sign(CodePointComparer.Instance.Compare(x, y)));
    }

    [Fact]
    public void Compare_OrdersAnAstralCharacter_ByItsActualCodePointValue()
    {
        // U+1F600 '😀' (codepoint 128512, an astral/supplementary-plane character -- encoded in
        // UTF-16 as the surrogate pair U+D83D U+DE00) vs U+FF01 '！' (codepoint 65281, an ordinary
        // BMP character, single UTF-16 code unit). Python compares actual codepoints, so '！'
        // (65281) sorts before '😀' (128512).
        var emoji = "\U0001F600";
        var fullwidthExclamation = "\uFF01";

        Assert.True(CodePointComparer.Instance.Compare(fullwidthExclamation, emoji) < 0);
        Assert.True(CodePointComparer.Instance.Compare(emoji, fullwidthExclamation) > 0);

        // The bug this comparer fixes: StringComparer.Ordinal compares UTF-16 code UNITS, so it
        // compares the emoji's leading surrogate (U+D83D = 55357) against U+FF01 (65281) and gets
        // the order BACKWARDS (55357 < 65281, so Ordinal says the emoji sorts first) -- the exact
        // opposite of Python's actual codepoint order asserted above.
        Assert.True(StringComparer.Ordinal.Compare(fullwidthExclamation, emoji) > 0);
    }

    [Fact]
    public void Compare_TreatsAShorterPrefixString_AsSortingFirst()
    {
        Assert.True(CodePointComparer.Instance.Compare("Combo", "Combo Meal") < 0);
    }

    [Fact]
    public void Compare_ReturnsZero_ForEqualStrings()
    {
        Assert.Equal(0, CodePointComparer.Instance.Compare("Cherry Limeade", "Cherry Limeade"));
    }

    [Fact]
    public void Compare_HandlesNulls_LikeAnOrdinaryComparer()
    {
        Assert.True(CodePointComparer.Instance.Compare(null, "a") < 0);
        Assert.True(CodePointComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, CodePointComparer.Instance.Compare(null, null));
    }
}
