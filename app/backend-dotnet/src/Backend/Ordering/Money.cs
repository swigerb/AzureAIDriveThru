using System.Globalization;

namespace Backend.Ordering;

/// <summary>
/// Port of app/backend/money_utils.py (docs/dotnet_mapping.md, issue #46): the single point of
/// rounding for every guest-facing money amount. Python's <c>to_decimal</c> exists only to convert
/// a possibly-``float``-typed value into an exact <c>Decimal</c> (via its string repr) before any
/// arithmetic, avoiding binary-float noise (e.g. <c>Decimal(0.1)</c> reproducing 0.1's noisy binary
/// value verbatim). The C# port carries <c>decimal</c> end-to-end instead -- every persona-pack
/// price (<see cref="Backend.Personas.PersonaMenuItemSize.Price"/>) already deserializes straight
/// into a <c>decimal</c>, never a <c>double</c> -- so that whole class of noise cannot occur here
/// and there is no C# counterpart to <c>to_decimal</c> to port. <see cref="Format"/> is the direct
/// port of <c>format_money</c>: round-half-up to exactly two decimal places, then render with a
/// leading "$", culture-invariant.
/// </summary>
internal static class Money
{
    private static readonly string[] s_ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen",
    ];
    private static readonly string[] s_tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>Formats <paramref name="value"/> as "$X.XX", rounding the exact half-cent case UP
    /// (ROUND_HALF_UP -- <c>MidpointRounding.AwayFromZero</c> is equivalent for every non-negative
    /// amount this domain ever produces), never down and never a blanket ceiling of every
    /// fractional cent. Mirrors money_utils.py's <c>format_money</c> exactly.</summary>
    public static string Format(decimal value)
    {
        decimal rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return "$" + rounded.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string BelowHundredWords(int n)
    {
        if (n < 20)
        {
            return s_ones[n];
        }
        int tens = n / 10;
        int ones = n % 10;
        string word = s_tens[tens];
        return ones > 0 ? $"{word}-{s_ones[ones]}" : word;
    }

    private static string BelowThousandWords(int n)
    {
        if (n < 100)
        {
            return BelowHundredWords(n);
        }
        int hundreds = n / 100;
        int rest = n % 100;
        string word = $"{s_ones[hundreds]} hundred";
        return rest > 0 ? $"{word} {BelowHundredWords(rest)}" : word;
    }

    /// <summary>Port of money_utils.py's <c>number_to_words</c> (#313, Rick's review item 2): spells
    /// out a non-negative integer as spoken English words -- a realtime quantity read next to a
    /// count-based size (e.g. "3 10 Count ...") is ambiguous as digits, so the quantity is spelled
    /// out instead. Supports every value this domain can produce.</summary>
    public static string NumberToWords(int n)
    {
        if (n < 0)
        {
            return $"negative {NumberToWords(-n)}";
        }
        if (n == 0)
        {
            return "zero";
        }
        var parts = new List<string>();
        int remaining = n;
        foreach ((int scale, string? word) in new[] { (1_000_000, "million"), (1_000, "thousand") })
        {
            if (remaining >= scale)
            {
                int count = remaining / scale;
                remaining %= scale;
                parts.Add($"{BelowThousandWords(count)} {word}");
            }
        }
        if (remaining > 0)
        {
            parts.Add(BelowThousandWords(remaining));
        }
        return string.Join(" ", parts);
    }

    /// <summary>Port of money_utils.py's <c>format_money_spoken</c> (#313, Rick's review item 2):
    /// renders <paramref name="value"/> as spoken-English money, e.g. "four dollars and thirty-one
    /// cents". Uses the SAME round-half-up rounding as <see cref="Format"/> -- the two can never
    /// disagree on the rounded amount, only on whether it's rendered as digits or words.</summary>
    public static string FormatMoneySpoken(decimal value)
    {
        decimal rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        bool negative = rounded < 0;
        long centsTotal = (long)Math.Round(Math.Abs(rounded) * 100m, MidpointRounding.AwayFromZero);
        int dollars = (int)(centsTotal / 100);
        int cents = (int)(centsTotal % 100);
        string dollarWord = $"{NumberToWords(dollars)} dollar{(dollars == 1 ? "" : "s")}";
        string centWord = $"{NumberToWords(cents)} cent{(cents == 1 ? "" : "s")}";
        string spoken;
        if (cents == 0)
        {
            spoken = dollarWord;
        }
        else if (dollars == 0)
        {
            spoken = centWord;
        }
        else
        {
            spoken = $"{dollarWord} and {centWord}";
        }
        return negative ? $"negative {spoken}" : spoken;
    }
}
