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
public static class Money
{
    /// <summary>Formats <paramref name="value"/> as "$X.XX", rounding the exact half-cent case UP
    /// (ROUND_HALF_UP -- <c>MidpointRounding.AwayFromZero</c> is equivalent for every non-negative
    /// amount this domain ever produces), never down and never a blanket ceiling of every
    /// fractional cent. Mirrors money_utils.py's <c>format_money</c> exactly.</summary>
    public static string Format(decimal value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return "$" + rounded.ToString("F2", CultureInfo.InvariantCulture);
    }
}
