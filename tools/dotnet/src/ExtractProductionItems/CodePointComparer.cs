using System.Text;

namespace ExtractProductionItems;

/// <summary>
/// Compares strings by Unicode CODE POINT, matching Python 3's <c>str</c> comparison semantics --
/// NOT by UTF-16 code UNIT, which is what <see cref="StringComparer.Ordinal"/> actually does.
///
/// The two agree for every character in the Basic Multilingual Plane (codepoint &lt;= U+FFFF,
/// which .NET represents as a single <see cref="char"/>) -- which is every real menu/category name
/// in this repo's fixtures today, and why <see cref="StringComparer.Ordinal"/> looked correct here.
/// They DISAGREE once a comparison reaches an astral character (codepoint &gt; U+FFFF, e.g. most
/// emoji): .NET represents an astral character as a UTF-16 surrogate PAIR (two <see cref="char"/>s,
/// the first in the range U+D800-U+DBFF), and that leading surrogate's raw numeric value can be
/// LESS than an ordinary BMP character it should sort after by actual codepoint value (e.g.
/// U+1F600 '😀' encodes as the leading surrogate U+D83D = 55357, which is less than U+FF01 '！' =
/// 65281, even though 0x1F600 = 128512 is the larger codepoint) -- so a plain code-unit-by-code-
/// unit ordinal comparison can get astral-vs-BMP ordering backwards relative to Python.
/// </summary>
internal sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    private CodePointComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        var xi = 0;
        var yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            // Ignoring the OperationStatus: a lone/invalid surrogate (never expected in real menu
            // data) decodes to Rune.ReplacementChar with 1 code unit consumed, a safe fallback
            // that never throws.
            Rune.DecodeFromUtf16(x.AsSpan(xi), out var xRune, out var xConsumed);
            Rune.DecodeFromUtf16(y.AsSpan(yi), out var yRune, out var yConsumed);

            var comparison = xRune.Value.CompareTo(yRune.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            xi += xConsumed;
            yi += yConsumed;
        }

        // Equal so far -- the shorter string (fewer remaining code units) sorts first, same as
        // Python's prefix-ordering rule.
        return (x.Length - xi).CompareTo(y.Length - yi);
    }
}
