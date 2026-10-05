"""Shared money formatting utilities (#46).

The Sonic AI Drive-Thru's spoken and displayed money values are computed with ``Decimal`` from
``menuItems.json`` prices and the config business-rule rates, with no intermediate rounding
anywhere in the calculation. Only the *final* result is rounded, once, when it needs to be shown
or spoken -- using ``ROUND_HALF_UP`` (an exact half cent rounds up; this is NOT the same as always
rounding up / a ceiling for non-half values).

Every spoken/displayed money surface in the backend (tools.py's tool responses, order_state.py's
readback) must go through ``format_money`` so they can never drift out of sync with each other or
with the conformance suite's golden values.
"""

from __future__ import annotations

from decimal import ROUND_HALF_UP, Decimal

__all__ = ["to_decimal", "format_money", "number_to_words", "format_money_spoken"]

_CENTS = Decimal("0.01")

_ONES = (
    "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
    "nineteen",
)
_TENS = ("", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety")


def to_decimal(value) -> Decimal:
    """Convert *value* to an exact ``Decimal``.

    Values are converted via ``str()`` first -- ``Decimal(0.1)`` reproduces the binary float's
    exact (noisy) value, while ``Decimal(str(0.1))`` gives the clean decimal ``0.1`` a human
    actually meant. JSON numbers and config values always arrive as ``float``/``int``/``str``, so
    this is the correct, safe conversion for all of our money inputs.
    """
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def format_money(value) -> str:
    """Render *value* as an exact, culture-invariant ``"$0.00"`` string.

    Rounds to the cent with ``ROUND_HALF_UP`` (an exact half cent, e.g. 5.265, rounds up to 5.27
    -- not a ceiling that would also round 5.261 up to 5.27). This must be the only place cents
    get rounded for display/speech; everywhere upstream of this call should stay in exact
    ``Decimal`` (or float) arithmetic with no intermediate rounding.
    """
    cents = to_decimal(value).quantize(_CENTS, rounding=ROUND_HALF_UP)
    return f"${cents:.2f}"


def _below_100_words(n: int) -> str:
    if n < 20:
        return _ONES[n]
    tens, ones = divmod(n, 10)
    word = _TENS[tens]
    return f"{word}-{_ONES[ones]}" if ones else word


def _below_1000_words(n: int) -> str:
    if n < 100:
        return _below_100_words(n)
    hundreds, rest = divmod(n, 100)
    word = f"{_ONES[hundreds]} hundred"
    return f"{word} {_below_100_words(rest)}" if rest else word


def number_to_words(n: int) -> str:
    """Spell out a non-negative integer *n* as spoken English words (#313, Rick's review item
    2): a realtime quantity read alongside a count-based size (e.g. "3 10 Count ...") is
    ambiguous when both are digits -- could be heard as "three ten count" or "three hundred
    ten". Spelling the quantity out as a word removes the ambiguity. Supports every value this
    domain can produce (order quantities/totals never reach a million, but the million/thousand
    scale words are included for completeness)."""
    if n < 0:
        return f"negative {number_to_words(-n)}"
    if n == 0:
        return "zero"
    parts: list[str] = []
    remaining = n
    for scale, word in ((1_000_000, "million"), (1_000, "thousand")):
        if remaining >= scale:
            count, remaining = divmod(remaining, scale)
            parts.append(f"{_below_1000_words(count)} {word}")
    if remaining:
        parts.append(_below_1000_words(remaining))
    return " ".join(parts)


def format_money_spoken(value) -> str:
    """Render *value* as spoken-English money, e.g. ``"four dollars and thirty-one cents"``
    (#313, Rick's review item 2: "money in words"). Uses the SAME ``ROUND_HALF_UP`` rounding as
    :func:`format_money` -- the two can never disagree on the rounded amount, only on whether
    it's rendered as digits or words."""
    cents_total = int(to_decimal(value).quantize(_CENTS, rounding=ROUND_HALF_UP) * 100)
    negative = cents_total < 0
    cents_total = abs(cents_total)
    dollars, cents = divmod(cents_total, 100)
    dollar_word = f"{number_to_words(dollars)} dollar{'' if dollars == 1 else 's'}"
    cent_word = f"{number_to_words(cents)} cent{'' if cents == 1 else 's'}"
    if cents == 0:
        spoken = dollar_word
    elif dollars == 0:
        spoken = cent_word
    else:
        spoken = f"{dollar_word} and {cent_word}"
    return f"negative {spoken}" if negative else spoken
