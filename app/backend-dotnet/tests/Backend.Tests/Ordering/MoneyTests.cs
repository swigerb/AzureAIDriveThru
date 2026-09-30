using Backend.Ordering;

namespace Backend.Tests.Ordering;

/// <summary>
/// Direct C# port of app/backend/tests/test_money_utils.py's format_money cases (#46). No
/// to_decimal counterpart exists/needs porting -- see Money.cs's own doc comment for why the C#
/// port carries `decimal` end-to-end and never needs a binary-float-noise workaround.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Format_HalfCent_RoundsUpNotTruncates() =>
        // Rick's N21/SpokenTotalHalfCentTests case: an exact half cent rounds up, not down.
        Assert.Equal("$5.27", Money.Format(5.265m));

    [Fact]
    public void Format_TrailingZero_KeepsTwoDecimalPlaces()
    {
        // Rick's N21 trailing-zero case: a whole-cent total must not truncate to "$10.8".
        Assert.Equal("$10.80", Money.Format(10.80m));
        Assert.Equal("$10.80", Money.Format(10.8m));
    }

    [Fact]
    public void Format_RoundsUpANonMidpointValue() =>
        // Rick's N21 round-up-non-midpoint case (Tots medium x3 @ 2.79 -> 9.0396): proves the rule
        // is ROUND_HALF_UP, not a ceiling that rounds every fractional cent up regardless of digit.
        Assert.Equal("$9.04", Money.Format(9.0396m));

    [Fact]
    public void Format_DoesNotCeilingEveryFractionalCent() =>
        // A non-half, non-multiple-of-ten fractional cent that should round DOWN, proving the rule
        // isn't simply "always round up".
        Assert.Equal("$2.56", Money.Format(2.561m));

    [Fact]
    public void Format_IsCultureInvariantAndExact()
    {
        Assert.Equal("$0.00", Money.Format(0m));
        Assert.Equal("$3.50", Money.Format(3.5m));
    }
}
