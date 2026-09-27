"""Shared menu utilities — canonical size mappings and category inference.

Both ``tools.py`` and ``order_state.py`` need size normalisation and category
inference.  Keeping a single source of truth here avoids silent drift.
"""

from __future__ import annotations

import json
import logging
import os
import re
from pathlib import Path

__all__ = [
    "SIZE_MAP",
    "SIZE_ALIASES",
    "normalize_size",
    "canonical_size_key",
    "strip_modifiers",
    "_menu_key",
    "infer_category",
    "infer_combo_component",
    "is_happy_hour_discounted",
    "bundle_slots",
    "MENU_CATEGORY_MAP",
]

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Canonical size map  (display-ready values)
# ---------------------------------------------------------------------------
SIZE_MAP: dict[str, str] = {
    "mini": "Mini",
    "small": "Small",
    "medium": "Medium",
    "large": "Large",
    "xl": "Extra Large",
    "route 44": "Route 44",
    "standard": "Standard",
}

# Aliases that normalise to a canonical key above
SIZE_ALIASES: dict[str, str] = {
    "s": "small",
    "m": "medium",
    "l": "large",
    "extralarge": "xl",  # PR #50 review follow-up: "Extra Large" must canonicalize to "xl", the
                         # same key its own short form already uses -- matched via the punctuation-
                         # and-whitespace-stripped compact key below, same as the Route 44 aliases.
    "rt 44": "route 44",
    "rt44": "route 44",
    "44": "route 44",
    "44oz": "route 44",
    "route44": "route 44",
}

# Sizes that should be hidden in display strings (no prefix)
_NO_DISPLAY_SIZES = frozenset({"", "standard", "n/a", "na", "none", "n.a."})

# Punctuation ignored when compacting a size string for alias lookup (PR #50 review follow-up):
# "Route-44" and "rt. 44" must resolve identically to "route44"/"rt44" -- whitespace alone wasn't
# enough to catch the hyphen or period variants.
_SIZE_ALIAS_IGNORED_CHARS = frozenset(" .-")


def _compact_size_key(size: str) -> str:
    key = (size or "").strip().lower()
    return "".join(ch for ch in key if ch not in _SIZE_ALIAS_IGNORED_CHARS)


def normalize_size(size: str) -> str:
    """Return a human-readable size string, or ``""`` for hidden/standard sizes.

    Delegates alias resolution to ``canonical_size_key`` so the display prefix and the
    order-matching key can never disagree (PR #50 review follow-up) -- e.g. adding a drink with
    size ``"44 oz"`` must display "Route 44 ..." exactly like ``"rt44"`` does, not silently drop
    the size prefix because that specific spelling wasn't in ``SIZE_ALIASES`` verbatim.

    >>> normalize_size("rt44")
    'Route 44'
    >>> normalize_size("m")
    'Medium'
    >>> normalize_size("n/a")
    ''
    """
    key = (size or "").strip().lower()
    if key in _NO_DISPLAY_SIZES:
        return ""
    return SIZE_MAP.get(canonical_size_key(size), "")


def canonical_size_key(size: str) -> str:
    """Return the canonical, alias-resolved key used to match/merge/remove order lines (#40).

    All spellings of the same physical size must collapse to one key *before* any order-state
    matching happens, so e.g. ``"rt44"``, ``"route44"``, ``"44 oz"``, ``"Route-44"``, ``"rt. 44"``,
    ``"RT 44"`` and ``"Route 44"`` are all treated as the same line item, and ``"Extra Large"``
    collapses onto the same key as ``"xl"``. This mirrors the alias resolution ``normalize_size``
    already does for its display string, but returns the lookup key itself (not a human-readable
    label) and is case/whitespace/punctuation-normalised even for sizes with no known alias, so
    callers get consistent matching regardless of input casing or spacing.

    >>> canonical_size_key("rt44")
    'route 44'
    >>> canonical_size_key("Route-44")
    'route 44'
    >>> canonical_size_key("rt. 44")
    'route 44'
    >>> canonical_size_key("44 oz")
    'route 44'
    >>> canonical_size_key("Extra Large")
    'xl'
    >>> canonical_size_key(" Medium ")
    'medium'
    """
    key = (size or "").strip().lower()
    # Collapse whitespace/periods/hyphens so "44 oz", "Route-44" and "rt. 44" all resolve the same
    # way as their tighter spellings ("44oz", "route44", "rt44").
    compact_key = _compact_size_key(size)
    if compact_key in SIZE_ALIASES:
        return SIZE_ALIASES[compact_key]
    return SIZE_ALIASES.get(key, key)


# ---------------------------------------------------------------------------
# Modifier-suffix stripping & the ONE lookup-key normalisation rule (PR #50 review)
#
# Defined BEFORE _load_menu_data()/MENU_CATEGORY_MAP below so the map itself can be keyed
# by _menu_key() (PR #50 review, round 4) -- previously it was keyed by a bare ``name.lower()``,
# which does NOT collapse Unicode whitespace (e.g. NBSP, U+00A0) or the registered-trademark
# symbol "®". Several real menuItems.json names contain an NBSP where a normal space would be
# expected (e.g. "SONIC Blast®\xa0made with OREO®\xa0Cookie Pieces" -- verified via the raw JSON
# bytes), so that item's own exact name MISSED its own map entry and only classified correctly by
# keyword-fallback luck (the substring "blast" happened to still match). Keying the map with the
# same _menu_key() used to look items up at runtime closes that gap for good, for every current and
# future menu item, not just this one.
# ---------------------------------------------------------------------------

# Strips a trailing parenthesized customization suffix, e.g. "Tots (Extra Crispy)" -> "Tots"
# (PR #50 review: customised items were bypassing every menuItems.json-based lookup because the
# modifiers travel inside item_name, tools.py's ``update_order`` -- see ``strip_modifiers`` below).
_MODIFIER_SUFFIX_RE = re.compile(r"\s*\([^)]*\)\s*")


def strip_modifiers(item_name: str) -> str:
    """Strip parenthesized customization suffix(es) from *item_name* and collapse whitespace.

    THE single normalisation rule for turning a possibly-customised order-line name (e.g.
    ``"Chili Cheese Tots (Extra Cheese)"``, ``"Tots (Extra Crispy)"``) into its base menu-item
    name. Used both for every menuItems.json-based lookup below (combo slot / sundae / category /
    happy-hour eligibility) *and* for combo-conversion base-name matching in ``order_state.py`` --
    one rule, one implementation, so the two can never drift (Rick's PR #50 review: "reuse one
    helper, don't duplicate").

    The exact algorithm (PR #50 review round 4 -- documented in full in the conformance README):
    every ``\\s*\\([^)]*\\)\\s*`` group ANYWHERE in the string (not just a trailing one) collapses
    to a single space, then ``str.split()``/``" ".join(...)`` collapses all whitespace runs --
    including Unicode whitespace such as NBSP (U+00A0), which Python's ``str.split()`` already
    treats as a separator. A modifier group in the middle of the name is stripped exactly like a
    trailing one, and multiple groups are all stripped. Nested or unbalanced parentheses are a
    deliberate fail-safe, NOT a special case: ``[^)]*`` cannot skip over an inner ``(``, so a nested
    group only ever partially matches, leaving a stray unmatched ``)`` in the result -- that stray
    character then guarantees the cleaned name won't equal any real (or allow-listed) menu key, so
    the item is classified as unknown and charged in full rather than risking an incorrect match.

    >>> strip_modifiers("Tots (Extra Crispy)")
    'Tots'
    >>> strip_modifiers("Chili Cheese Tots (Extra Cheese)")
    'Chili Cheese Tots'
    >>> strip_modifiers("Cherry Limeade")
    'Cherry Limeade'
    >>> strip_modifiers("Tots (Extra Crispy) (No Salt)")
    'Tots'
    >>> strip_modifiers("Chili Cheese (Extra Cheese) Tots")
    'Chili Cheese Tots'
    >>> strip_modifiers("Tots (Extra (Really) Crispy)")
    'Tots Crispy)'
    """
    return " ".join(_MODIFIER_SUFFIX_RE.sub(" ", item_name or "").split())


def _menu_key(item_name: str) -> str:
    """Lowercased, modifier-stripped, symbol-normalised key used for ALL menuItems.json-based
    classification (combo slot / sundae / category / happy-hour eligibility) AND for
    ``MENU_CATEGORY_MAP``'s own keys below. A customised item must classify identically to its
    uncustomised base item -- PR #50 review: "Chili Cheese Tots (Extra Cheese)" must be charged in
    full exactly like "Chili Cheese Tots" is, and "Cherry Limeade (Extra Cherries)" must still get
    the happy-hour discount exactly like "Cherry Limeade" does.

    Three symbol-normalisation rules live here -- and ONLY here, i.e. this is the one and only
    place any of them live (PR #50 review round 4/5: they used to live, or would otherwise need to
    live, as second, independently-maintained rules elsewhere -- e.g. ``order_state.py``'s
    combo-conversion matching used to strip "®" itself, before it started calling this function):
      - The registered-trademark symbol "®" is stripped (``SONIC® Cheeseburger``).
      - The trademark symbol "™" is stripped identically (PR #50 review round 5) -- eight
        ``menuItems.json`` names carry it (the "SONIC Smasher™" family, plain and Combo variants);
        without this, a spoken "All-American SONIC Smasher" (naturally omitting an unspeakable
        symbol) would miss its own map entry exactly like the OREO Blast's NBSP used to.
      - The curly/typographic apostrophe "\u2019" is normalised to a plain ASCII apostrophe "'"
        (PR #50 review round 5) -- ``menuItems.json``'s "SONIC Blast® made with REESE'S" uses the
        curly form verbatim, so a spoken "Reese's" (naturally typed/transcribed with a plain
        apostrophe) would otherwise miss its own map entry too."""
    return (
        strip_modifiers(item_name)
        .lower()
        .replace("®", "")
        .replace("\u2122", "")
        .replace("\u2019", "'")
    )


# ---------------------------------------------------------------------------
# Menu item data (loaded once from the Sonic persona pack's menu/menuItems.json)
# ---------------------------------------------------------------------------
# issue #70 (persona pack skeleton and loader): the per-brand env var overrides
# (SONIC_MENU_ITEMS_PATH / MENU_ITEMS_PATH) are removed -- the menu now always loads from the
# persona pack, whose location is controlled by PERSONAS_DIR (same variable persona_loader.py and
# prompt_loader.py read). Hardcoded to "sonic" for now; per-session persona selection is future
# work (#74).
#
# issue #71 (#51 per-item menu fields, data-driven classification): combo-slot classification and
# happy-hour-discount eligibility used to live in name-keyed Python tables below this comment
# (``_COMBO_SIDE_ITEMS``, ``_SUNDAES``, ``_COMBO_DRINK_CATEGORIES``, ``_TOTS_ALIASES``,
# ``_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED``). They are gone. Every on-menu item now carries its
# own explicit ``comboSlot``/``happyHourDiscounted``/``aliases`` fields in menuItems.json
# (design doc sections 4.3 and 6), and this module reads them once at import time -- there is no
# Sonic item name left anywhere in this file. The keyword fallback below stays for items that
# aren't on the menu at all; #73 removes it.
_REPO_ROOT = Path(__file__).resolve().parents[2]
_PERSONAS_DIR = Path(os.environ.get("PERSONAS_DIR") or (_REPO_ROOT / "personas"))
_ACTIVE_PERSONA = os.environ.get("DEFAULT_PERSONA", "sonic")


def _load_menu_data() -> tuple[dict[str, dict], dict[str, str]]:
    """Load every menu item from the active persona's pack, once, keyed by ``_menu_key(name)``.

    Returns ``(item_fields, alias_map)``:

    - ``item_fields``: normalized item key -> ``{"category", "comboSlot", "happyHourDiscounted"}``,
      read straight from each item's #51 fields in menuItems.json. Missing fields fall back to
      their schema-documented safe defaults (``"none"`` / ``False``), matching a pack that hasn't
      been fully populated yet.
    - ``alias_map``: normalized alias key -> the canonical item's normalized key, built from each
      item's ``aliases`` list. An alias resolves the item for EVERY lookup below (category, combo
      slot, happy-hour discount) -- not just the combo side slot as #60 originally scoped it
      (design doc section 4.3/6, issue #71). For "Tots" this is observably identical to #60's
      narrower scope: see ``TotsAliasResolvesEverywhereTests`` in test_menu_utils.py.
    """
    menu_path = _PERSONAS_DIR / _ACTIVE_PERSONA / "menu" / "menuItems.json"
    if not menu_path.exists():
        return {}, {}
    try:
        with menu_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        item_fields: dict[str, dict] = {}
        alias_map: dict[str, str] = {}
        for category_entry in data.get("menuItems", []):
            category = category_entry.get("category", "").strip().lower()
            for item in category_entry.get("items", []):
                name = item.get("name")
                if not name:
                    continue
                # PR #50 review round 4: key by _menu_key(name), not a bare name.lower() -- see
                # the module comment above this section for the NBSP regression this closes.
                key = _menu_key(name)
                item_fields[key] = {
                    "category": category,
                    "comboSlot": item.get("comboSlot", "none"),
                    "happyHourDiscounted": bool(item.get("happyHourDiscounted", False)),
                    # Rick's PR #99 review, decision 1: the item's own ``bundle.slots`` -- the
                    # actual side/drink component groups this item absorbs when added (a combo,
                    # Dinner, Wacky Pack or Meal), read straight from the pack. Empty tuple for an
                    # item with no ``bundle`` field at all (most menu items absorb nothing).
                    "bundleSlots": tuple(item.get("bundle", {}).get("slots") or ()),
                }
                for alias in item.get("aliases") or ():
                    alias_key = _menu_key(alias)
                    if alias_key:
                        alias_map[alias_key] = key
        return item_fields, alias_map
    except Exception as exc:  # pragma: no cover
        logger.warning("Failed to load menu items; falling back to keyword inference: %s", exc)
        return {}, {}


_MENU_ITEM_FIELDS, _MENU_ALIAS_MAP = _load_menu_data()

# Public API (test_menu_utils.py, tools.py): normalized item key -> category. Same shape as
# before #71 -- only the loader that builds it changed (it now comes out of ``_load_menu_data``
# alongside the comboSlot/happyHourDiscounted fields, instead of being the only thing loaded).
MENU_CATEGORY_MAP: dict[str, str] = {key: fields["category"] for key, fields in _MENU_ITEM_FIELDS.items()}


def _resolve_alias(normalized: str) -> str:
    """Resolve *normalized* (already put through ``_menu_key``) via the pack's per-item
    ``aliases``, if any. An alias resolves to its canonical item for every classification lookup
    below -- category, combo slot, and happy-hour discount alike (issue #71; see
    ``_load_menu_data`` above). Returns *normalized* unchanged when it isn't a known alias
    (including when it's already a real item's own key)."""
    return _MENU_ALIAS_MAP.get(normalized, normalized)


def infer_category(item_name: str) -> str:
    """Return the menu category for *item_name* (keyword fallback if not in the JSON map)."""
    normalized = _resolve_alias(_menu_key(item_name))
    if normalized in MENU_CATEGORY_MAP:
        return MENU_CATEGORY_MAP[normalized]
    if "slush" in normalized or "limeade" in normalized or "ocean water" in normalized:
        return "slushes"
    if "shake" in normalized or "blast" in normalized or "malt" in normalized:
        return "shakes"
    if "burger" in normalized or "combo" in normalized:
        return "combos"
    if "hot dog" in normalized or "coney" in normalized:
        return "hot dogs"
    if "tot" in normalized or "fries" in normalized or "onion rings" in normalized:
        return "sides"
    if "drink" in normalized or "tea" in normalized or "lemonade" in normalized:
        return "drinks"
    return ""


# ---------------------------------------------------------------------------
# Combo-slot classification & happy-hour discount eligibility (#39, PR #50 review; data-driven
# since #71)
#
# These are two SEPARATE questions and must never be derived from one shared bucket (Rick's PR
# #50 review): "can this item fill a combo's included side/drink slot" (``infer_combo_component``)
# vs. "does this item get the happy-hour discount" (``is_happy_hour_discounted``). They agree on
# almost everything today, but that's incidental, not structural -- e.g. Shakes & Blasts are NOT
# happy-hour-discounted (Brian's decision, 2026-09-25) even though they DO fill a combo's drink
# slot, and a future change to one must not silently change the other.
#
# issue #71: every on-menu item's answer to both questions is now its own explicit
# ``comboSlot``/``happyHourDiscounted`` field in menuItems.json (read into ``_MENU_ITEM_FIELDS``
# above) -- there is no longer a name-keyed Python allow-list for combo-side items, sundaes, or
# combo-drink categories, and no more standalone "is Tots" alias table (aliases now live on the
# item in the pack and resolve for every lookup via ``_resolve_alias``). The golden category table
# (tests/conformance/testdata/golden-menu-categories.json) is checked against these same pack
# fields in test_menu_utils.py, not generated from them.
# ---------------------------------------------------------------------------

# Word-boundary so a side item merely *containing* the substring "pepper" (e.g. "Ched 'R'
# Peppers") isn't misclassified as the drink "Dr Pepper" (#39 / #28 N19 root cause).
_DR_PEPPER_RE = re.compile(r"\bdr\.?\s*pepper\b")

# Fountain-drink keywords: unconditionally eligible for both combo-drink-slot-filling and the
# happy-hour discount, matching every "Slushes & Drinks" menuItems.json item's unconditional
# behaviour. Used ONLY as a fallback for items that aren't in the menu at all (e.g. a spoken item
# never added to menuItems.json) -- on-menu items are always matched by JSON category first.
#
# Word-boundary (PR #50 review round 4): a plain substring check let "tea" match inside "steak",
# so an off-menu "Philly Cheesesteak"/"Steak Sandwich" was silently absorbed into a combo's drink
# slot AND happy-hour discounted. ``\b...\b`` requires the keyword to be its own word. Dr Pepper
# keeps its own separate, already-word-boundary regex above.
#
# PR #50 review (round 5, "keyword over-correction"): the initial word-boundary fix over-corrected
# -- ``s?`` only allows a single trailing "s", so it missed the "-es"/"-ie"/"-y" spoken variants
# entirely: "Cherry Slushes" (plural "-es"), "Blue Raspberry Slushie" ("-ie"), and a guest saying
# "Slushy" ("-y") are all genuinely off-menu names (the real items are "... Slush", singular) that
# must still hit this fallback. ``slush(?:ie|y)?`` matches the bare word plus either spoken
# variant, and the outer ``(?:e?s)?`` allows the regular "-s"/"-es" plural on TOP of that (so
# "Slushies" still matches too) without reopening the "tea"-in-"steak" hole: the boundary is still
# required immediately before the keyword.
_FOUNTAIN_DRINK_KEYWORD_RE = re.compile(
    r"\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\b"
)

# Shake/Blast/Malt keywords: same (unconditional) combo-drink-slot eligibility as fountain drinks,
# but the happy-hour DISCOUNT for this bucket must obey ``_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED``
# below -- PR #50 review: flipping that one flag must change *every* shake/blast variant, plain or
# customised, on-menu or off, not just the ones matched by JSON category.
#
# PR #50 review (round 5): a plain ``\bshake\b`` never matches "milkshake" at all -- there is no
# word boundary between "milk" and "shake" (both are word characters), so "Chocolate Milkshake"
# (a genuinely off-menu spoken variant; the real item is "... Classic Shake") fell all the way
# through to "" instead of "drinks". ``(?:\b|milk)`` is a deliberate, narrow carve-out: match
# either a normal word boundary OR the literal "milk" immediately before "shake"/"blast"/"malt",
# so "milkshake" resolves as a compound word without loosening the boundary for any other
# preceding text (a nonsense "overshake"/"bookshake" still correctly does NOT match).
_SHAKE_BLAST_KEYWORD_RE = re.compile(r"(?:\b|milk)(?:shake|blast|malt)(?:e?s)?\b")


def _keyword_fallback_combo_drink(normalized: str) -> bool:
    """Combo-drink-slot-filling fallback for items that aren't in menuItems.json at all.
    Combo-slot eligibility is unconditional for both buckets -- it never depends on the
    happy-hour-discount flag, which is a separate question (see ``is_happy_hour_discounted``)."""
    return (
        bool(_DR_PEPPER_RE.search(normalized))
        or bool(_FOUNTAIN_DRINK_KEYWORD_RE.search(normalized))
        or bool(_SHAKE_BLAST_KEYWORD_RE.search(normalized))
    )


def _keyword_fallback_happy_hour_discounted(normalized: str) -> bool:
    """Happy-hour-discount fallback for items that aren't in menuItems.json at all. Fountain
    drinks are always discounted; shakes/blasts/malts are NOT (Brian's decision, 2026-09-25,
    confirmed permanent by #71 -- every on-menu Shakes & Ice Cream item's ``happyHourDiscounted``
    field in menuItems.json is ``false``, so this fallback matches the pack for good, not a switch
    that could flip).

    Checks the shake/blast/malt regex FIRST (PR #61 review, must-fix 1): an off-menu name can
    contain both a shake/blast word AND a fountain word -- e.g. "Cherry Limeade Shake" ("limeade"
    + "shake"), "Sweet Tea Blast" ("tea" + "blast"), "Dr Pepper Shake" -- and must resolve as a
    shake/blast for the DISCOUNT question (not discounted) even though it would also match the
    fountain branch. This precedence is deliberately the opposite of
    ``_keyword_fallback_combo_drink``, which is an unconditional OR across all three regexes and
    is NOT order-dependent -- these same names must still fill the combo drink slot regardless of
    which keyword "wins" the discount question."""
    if _SHAKE_BLAST_KEYWORD_RE.search(normalized):
        return False
    if _DR_PEPPER_RE.search(normalized) or _FOUNTAIN_DRINK_KEYWORD_RE.search(normalized):
        return True
    return False


def infer_combo_component(item_name: str) -> str:
    """Classify *item_name* for combo-SLOT-FILLING only.

    Returns ``"sides"``, ``"drinks"``, or ``""`` (can't fill either combo slot). This answers
    ONLY "can this item fill a combo's included side/drink slot" -- happy-hour discount
    eligibility is a SEPARATE question, answered by ``is_happy_hour_discounted`` below, and must
    never be derived from this function's result (PR #50 review). Every on-menu item's answer is
    its own explicit ``comboSlot`` field in menuItems.json (#71) -- keyword fallback only applies
    to items that aren't in the menu at all (#39; removed entirely by #73). *item_name* may carry
    a parenthesized customization suffix (e.g. "Tots (Extra Crispy)") -- ``_menu_key`` strips it
    before any lookup so a customised item classifies identically to its base item (PR #50
    review). Any of an item's spoken ``aliases`` (e.g. plain Tots's "Tot", "Tater Tot(s)",
    "Tator Tot(s)") resolves to the same combo slot as the canonical item -- an explicit,
    exact-match alias map read from the pack (``_resolve_alias``), never a substring check
    (originally Brian's decision, 2026-09-25, #60; broadened to every lookup by #71)."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is not None:
        combo_slot = fields["comboSlot"]
        return combo_slot if combo_slot in ("sides", "drinks") else ""

    # Not in the menu at all (e.g. a spoken item never added to menuItems.json). PR #50 review:
    # an unknown item must NEVER silently fill the combo side slot for free -- a charged item is
    # visible and correctable, a free absorption is silent revenue loss -- so there is no side
    # fallback here at all, only the (unconditional) drink fallback for genuinely off-menu
    # fountain drinks/shakes/blasts.
    if _keyword_fallback_combo_drink(normalized):
        return "drinks"
    return ""


def is_happy_hour_discounted(item_name: str) -> bool:
    """Whether *item_name* gets the happy-hour discount -- a SEPARATE question from
    ``infer_combo_component`` above (PR #50 review): don't derive one from the other. Every
    on-menu item's answer is its own explicit ``happyHourDiscounted`` field in menuItems.json
    (#71): sundaes and Shakes & Blasts are ``false`` (Brian's decisions, #39 and 2026-09-25), and
    every Slushes & Drinks item is ``true``. *item_name* may carry a parenthesized customization
    suffix -- ``_menu_key`` strips it before any lookup so a customised drink is discounted (or
    not) exactly like its base item (PR #50 review). Aliases resolve here too (#71; see
    ``_resolve_alias``)."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is not None:
        return fields["happyHourDiscounted"]

    # Not in the menu at all -- same keyword fallback categories as the combo-drink-slot check.
    return _keyword_fallback_happy_hour_discounted(normalized)


def bundle_slots(item_name: str) -> tuple[str, ...]:
    """Return *item_name*'s bundle component slots -- the actual side/drink slots a combo,
    Dinner, Wacky Pack or Meal absorbs when it's added, resolved through ``_menu_key``/aliases
    against the pack's own ``bundle.slots`` field (Rick's PR #99 review, decision 1; pulls the
    slot-count piece of the shared bundle engine, #77, forward).

    This REPLACES the old ``"combo" in item_name.lower()`` heuristic in ``order_state.py``, which
    gave every "... Combo"-named item exactly one side slot and one drink slot regardless of what
    the item's own data says -- wrongly free-absorbing a side into "French Toast Sticks Combo"
    (drinks-only per the export's own ``ingredientRefs``) and never absorbing anything into
    "Corn Dog Wacky Pack" or "Crispy Tenders Dinner - 3 piece" (whose names don't contain the word
    "combo" at all, even though the export shows they bundle real component groups).

    Returns ``()`` for any item with no ``bundle`` field at all -- most menu items absorb nothing.
    For a name that isn't on the menu at all (before #73 removes the keyword fallback entirely),
    falls back to ``("sides", "drinks")`` when the name contains "combo", matching the previous
    name-based heuristic exactly -- so an off-menu/typo'd "... Combo" name still behaves like
    today until #73 rejects it outright as ``not_on_menu``.

    >>> bundle_slots("French Toast Sticks Combo")
    ('drinks',)
    >>> bundle_slots("Crispy Tenders Dinner - 3 piece")
    ('drinks',)
    >>> bundle_slots("Tots")
    ()
    """
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is not None:
        return fields["bundleSlots"]

    # Not in the menu at all -- same "combo" name heuristic order_state.py used before this
    # function existed, kept only as a fallback for genuinely off-menu names (#73 territory).
    if "combo" in normalized:
        return ("sides", "drinks")
    return ()
