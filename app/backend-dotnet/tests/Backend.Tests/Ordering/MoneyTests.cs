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

    // #313 ("item 7"/item 4c, coordinator fix-up round): direct unit tests for
    // NumberToWords/FormatMoneySpoken -- previously only exercised indirectly through
    // update_order's delta text, never pinned down with their own focused cases. Same input set
    // as the Python leg's test_money_utils.py (test_number_to_words_*/test_format_money_spoken_*)
    // to prove parity between the two legs.

    [Fact]
    public void NumberToWords_Zero() => Assert.Equal("zero", Money.NumberToWords(0));

    [Fact]
    public void NumberToWords_SingleDigit() => Assert.Equal("one", Money.NumberToWords(1));

    [Fact]
    public void NumberToWords_Teen() => Assert.Equal("thirteen", Money.NumberToWords(13));

    [Fact]
    public void NumberToWords_TensWithARemainder_IsHyphenated() =>
        Assert.Equal("twenty-one", Money.NumberToWords(21));

    [Fact]
    public void NumberToWords_EvenTen_HasNoTrailingHyphen() =>
        Assert.Equal("twenty", Money.NumberToWords(20));

    [Fact]
    public void NumberToWords_Hundreds() =>
        Assert.Equal("three hundred fifty-four", Money.NumberToWords(354));

    [Fact]
    public void FormatMoneySpoken_OneDollar_IsSingular() =>
        Assert.Equal("one dollar", Money.FormatMoneySpoken(1m));

    [Fact]
    public void FormatMoneySpoken_OneCent_IsSingular() =>
        Assert.Equal("one cent", Money.FormatMoneySpoken(0.01m));

    [Fact]
    public void FormatMoneySpoken_ZeroDollarsAndZeroCents() =>
        Assert.Equal("zero dollars", Money.FormatMoneySpoken(0m));

    [Fact]
    public void FormatMoneySpoken_AWholeDollarAmount_OmitsTheCentsClauseEntirely() =>
        Assert.Equal("four dollars", Money.FormatMoneySpoken(4m));

    [Fact]
    public void FormatMoneySpoken_HalfCent_RoundsUpLikeFormat() =>
        // Mirrors Format_HalfCent_RoundsUpNotTruncates's own 5.265 case: the two renderers can
        // never disagree on the rounded amount, only digits vs. words.
        Assert.Equal("five dollars and twenty-seven cents", Money.FormatMoneySpoken(5.265m));

    [Fact]
    public void FormatMoneySpoken_DollarsAndCents_AreJoinedWithAnd() =>
        Assert.Equal("four dollars and thirty-one cents", Money.FormatMoneySpoken(4.31m));
}
