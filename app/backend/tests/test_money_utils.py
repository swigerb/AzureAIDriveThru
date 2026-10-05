"""Tests for money_utils.py (#46): exact Decimal conversion and ROUND_HALF_UP money formatting."""

import sys
from decimal import Decimal
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

from money_utils import format_money, format_money_spoken, number_to_words, to_decimal


def test_to_decimal_avoids_binary_float_noise():
    # Decimal(0.1) would reproduce the binary float's exact (noisy) value; to_decimal must not.
    assert to_decimal(0.1) == Decimal("0.1")
    assert to_decimal("2.79") == Decimal("2.79")
    assert to_decimal(Decimal("9.0396")) == Decimal("9.0396")


def test_format_money_half_cent_rounds_up_not_truncates():
    # Rick's N21/SpokenTotalHalfCentTests case: an exact half cent rounds up, not down.
    assert format_money(5.265) == "$5.27"


def test_format_money_trailing_zero_keeps_two_decimal_places():
    # Rick's N21 trailing-zero case: a whole-cent total must not truncate to "$10.8".
    assert format_money(10.80) == "$10.80"
    assert format_money(10.8) == "$10.80"


def test_format_money_rounds_up_a_non_midpoint_value():
    # Rick's N21 round-up-non-midpoint case (Tots medium x3 @ 2.79 -> 9.0396): proves the rule is
    # ROUND_HALF_UP, not a ceiling that rounds every fractional cent up regardless of the digit.
    assert format_money(9.0396) == "$9.04"


def test_format_money_does_not_ceiling_every_fractional_cent():
    # A non-half, non-multiple-of-ten fractional cent that should round DOWN, to prove the rule
    # isn't simply "always round up".
    assert format_money(2.561) == "$2.56"


def test_format_money_is_culture_invariant_and_exact():
    assert format_money(0) == "$0.00"
    assert format_money("3.5") == "$3.50"


# #313 ("item 7"/item 4c, coordinator fix-up round): direct unit tests for
# ``number_to_words``/``format_money_spoken`` -- previously only exercised indirectly through
# update_order's delta text, never pinned down with their own focused cases. Same input set as
# the C# port's MoneyTests.cs (NumberToWords_*/FormatMoneySpoken_* facts) to prove parity between
# the two legs.


def test_number_to_words_zero():
    assert number_to_words(0) == "zero"


def test_number_to_words_single_digit():
    assert number_to_words(1) == "one"


def test_number_to_words_teen():
    assert number_to_words(13) == "thirteen"


def test_number_to_words_tens_with_a_remainder_is_hyphenated():
    assert number_to_words(21) == "twenty-one"


def test_number_to_words_even_ten_has_no_trailing_hyphen():
    assert number_to_words(20) == "twenty"


def test_number_to_words_hundreds():
    assert number_to_words(354) == "three hundred fifty-four"


def test_format_money_spoken_one_dollar_is_singular():
    assert format_money_spoken(1) == "one dollar"


def test_format_money_spoken_one_cent_is_singular():
    assert format_money_spoken(0.01) == "one cent"


def test_format_money_spoken_zero_dollars_and_zero_cents():
    assert format_money_spoken(0) == "zero dollars"


def test_format_money_spoken_a_whole_dollar_amount_omits_the_cents_clause_entirely():
    assert format_money_spoken(4) == "four dollars"


def test_format_money_spoken_half_cent_rounds_up_like_format_money():
    # Mirrors test_format_money_half_cent_rounds_up_not_truncates's own 5.265 case: the two
    # renderers can never disagree on the rounded amount, only digits vs. words.
    assert format_money_spoken(5.265) == "five dollars and twenty-seven cents"


def test_format_money_spoken_dollars_and_cents_are_joined_with_and():
    assert format_money_spoken(4.31) == "four dollars and thirty-one cents"
