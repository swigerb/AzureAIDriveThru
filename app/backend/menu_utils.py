"""Shared menu utilities — canonical size mappings and category inference.

Both ``tools.py`` and ``order_state.py`` need size normalisation and category
inference.  Keeping a single source of truth here avoids silent drift.

#73 (ADR-001 decision 4: "No off-menu. If it's not on the menu in our source data, you cannot
order it."): every keyword/substring fallback that used to classify a name NOT found in the
persona pack's own data is deleted outright, and ``resolve_menu_item`` below is now THE single
gate ``tools.py``'s ``update_order`` calls before adding anything to an order -- an item resolves
if and only if its normalized name or one of its own ``aliases`` is an exact key in the pack's
data. A name that doesn't resolve gets each classification function's safe default (``""`` /
``False`` / ``()`` / ``None``), never a keyword guess.
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
    "requires_machine",
    "is_extra_item",
    "resolve_menu_item",
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
    """Return the canonical, alias-resolved key used to match/merge/remove order lines (#40), and
    (#73) the key ``resolve_menu_item``'s caller checks against an item's own ``sizes`` tuple.

    All spellings of the same physical size must collapse to one key *before* any order-state
    matching (or #73 size-availability check) happens, so e.g. ``"rt44"``, ``"route44"``,
    ``"44 oz"``, ``"Route-44"``, ``"rt. 44"``, ``"RT 44"`` and ``"Route 44"`` are all treated as
    the same line item, and ``"Extra Large"`` collapses onto the same key as ``"xl"``. This
    mirrors the alias resolution ``normalize_size`` already does for its display string, but
    returns the lookup key itself (not a human-readable label) and is case/whitespace/
    punctuation-normalised even for sizes with no known alias, so callers get consistent matching
    regardless of input casing or spacing.

    #73: every one of ``_NO_DISPLAY_SIZES`` (``""``, ``"standard"``, ``"n/a"``, ``"na"``,
    ``"none"``, ``"n.a."``) canonicalizes to the literal ``"standard"`` key too -- previously only
    ``normalize_size`` treated these as hidden-display synonyms of ``"standard"``; this function
    left them as their own distinct (non-``"standard"``) keys. That was harmless before #73 (no
    caller checked this key against a real item's size list), but the new on-menu size gate does
    exactly that: a real single-size ("standard"-only) item ordered with e.g. ``"n/a"`` must still
    resolve as that item's one real size, not be wrongly rejected as ``size_not_available``.

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
    >>> canonical_size_key("n/a")
    'standard'
    >>> canonical_size_key("")
    'standard'
    """
    key = (size or "").strip().lower()
    if key in _NO_DISPLAY_SIZES:
        return "standard"
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
    happy-hour eligibility / #73's on-menu resolution) *and* for combo-conversion base-name
    matching in ``order_state.py`` -- one rule, one implementation, so the two can never drift
    (Rick's PR #50 review: "reuse one helper, don't duplicate").

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
    classification (combo slot / sundae / category / happy-hour eligibility / #73's on-menu
    resolution) AND for ``MENU_CATEGORY_MAP``'s own keys below. A customised item must classify
    identically to its uncustomised base item -- PR #50 review: "Chili Cheese Tots (Extra
    Cheese)" must be charged in full exactly like "Chili Cheese Tots" is, and "Cherry Limeade
    (Extra Cherries)" must still get the happy-hour discount exactly like "Cherry Limeade" does.

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
# Sonic item name left anywhere in this file.
#
# #73 (ADR-001 decision 4 "No off-menu"): the keyword fallback that used to classify a name NOT
# found in the map above is deleted entirely (it lived in ``infer_category``,
# ``infer_combo_component``, ``is_happy_hour_discounted``, and ``bundle_slots`` below, plus the
# ``_keyword_fallback_combo_drink``/``_keyword_fallback_happy_hour_discounted`` helpers and their
# ``_DR_PEPPER_RE``/``_FOUNTAIN_DRINK_KEYWORD_RE``/``_SHAKE_BLAST_KEYWORD_RE`` regexes, all now
# removed). ``resolve_menu_item`` below is the new single on-menu gate ``tools.py``'s
# ``update_order`` calls before adding anything; every other classification function now simply
# returns its safe default for a name that doesn't resolve, never a keyword guess.
_REPO_ROOT = Path(__file__).resolve().parents[2]
_PERSONAS_DIR = Path(os.environ.get("PERSONAS_DIR") or (_REPO_ROOT / "personas"))
_ACTIVE_PERSONA = os.environ.get("DEFAULT_PERSONA", "sonic")


def _load_menu_data() -> tuple[dict[str, dict], dict[str, str]]:
    """Load every menu item from the active persona's pack, once, keyed by ``_menu_key(name)``.

    Returns ``(item_fields, alias_map)``:

    - ``item_fields``: normalized item key -> ``{"name", "category", "comboSlot",
      "happyHourDiscounted", "bundleSlots", "requiresMachine", "isExtra", "sizes"}``, read
      straight from each item's fields in menuItems.json. Missing fields fall back to their
      schema-documented safe defaults (``"none"`` / ``False`` / ``None`` / ``()``), matching a
      pack that hasn't been fully populated yet.
    - ``alias_map``: normalized alias key -> the canonical item's normalized key, built from each
      item's ``aliases`` list. An alias resolves the item for EVERY lookup below (category, combo
      slot, happy-hour discount, machine requirement, extra-item classification, and #73's
      on-menu resolution) -- not just the combo side slot as #60 originally scoped it (design doc
      section 4.3/6, issue #71). For "Tots" this is observably identical to #60's narrower scope:
      see ``TotsAliasResolvesEverywhereTests`` in test_menu_utils.py.
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
                    "name": name,
                    "category": category,
                    "comboSlot": item.get("comboSlot", "none"),
                    "happyHourDiscounted": bool(item.get("happyHourDiscounted", False)),
                    # Rick's PR #99 review, decision 1: the item's own ``bundle.slots`` -- the
                    # actual side/drink component groups this item absorbs when added (a combo,
                    # Dinner, Wacky Pack or Meal), read straight from the pack. Empty tuple for an
                    # item with no ``bundle`` field at all (most menu items absorb nothing).
                    "bundleSlots": tuple(item.get("bundle", {}).get("slots") or ()),
                    # #73 (Rick's PR review): the machine (if any) this item needs to make -- e.g.
                    # "slush_machine"/"ice_cream_machine" -- or None for an item that needs
                    # neither. Replaces the old ``_ICE_CREAM_MACHINE_KEYWORDS`` substring list in
                    # tools.py, which risked flagging a name that isn't even a real menu item.
                    "requiresMachine": item.get("requiresMachine"),
                    # #73 (Rick's PR review): whether this item is a chargeable "extra" add-on
                    # (e.g. "Add Bacon", "Whipped Topping") rather than a full menu item in its own
                    # right. Replaces the old ``EXTRAS_KEYWORDS`` name-substring list in tools.py,
                    # which risked matching a name that isn't a real menu item at all (e.g. an
                    # off-menu "Extra Patty").
                    "isExtra": bool(item.get("isExtra", False)),
                    # #73: every size this item is actually offered in, as a tuple of
                    # ``canonical_size_key(...)`` values -- read once here so ``resolve_menu_item``
                    # can hand it straight to its caller for the size-availability check.
                    "sizes": tuple(
                        dict.fromkeys(canonical_size_key(s.get("size", "")) for s in item.get("sizes") or ())
                    ),
                }
                for alias in item.get("aliases") or ():
                    alias_key = _menu_key(alias)
                    if alias_key:
                        alias_map[alias_key] = key
        return item_fields, alias_map
    except Exception as exc:  # pragma: no cover
        logger.warning("Failed to load menu items: %s", exc)
        return {}, {}


_MENU_ITEM_FIELDS, _MENU_ALIAS_MAP = _load_menu_data()

# Public API (test_menu_utils.py, tools.py): normalized item key -> category. Same shape as
# before #71 -- only the loader that builds it changed (it now comes out of ``_load_menu_data``
# alongside the comboSlot/happyHourDiscounted/requiresMachine/isExtra/sizes fields, instead of
# being the only thing loaded).
MENU_CATEGORY_MAP: dict[str, str] = {key: fields["category"] for key, fields in _MENU_ITEM_FIELDS.items()}


def _resolve_alias(normalized: str) -> str:
    """Resolve *normalized* (already put through ``_menu_key``) via the pack's per-item
    ``aliases``, if any. An alias resolves to its canonical item for every classification lookup
    below -- category, combo slot, happy-hour discount, machine requirement, extra-item
    classification, and #73's on-menu resolution alike (issue #71; see ``_load_menu_data``
    above). Returns *normalized* unchanged when it isn't a known alias (including when it's
    already a real item's own key)."""
    return _MENU_ALIAS_MAP.get(normalized, normalized)


def infer_category(item_name: str) -> str:
    """Return the menu category for *item_name*, or ``""`` if it isn't on the menu at all.

    #73 (ADR-001 decision 4 "No off-menu"): the keyword-guessing fallback that used to run for a
    name not found in ``MENU_CATEGORY_MAP`` (matching "slush"/"limeade"/"shake"/"burger"/etc. as a
    substring) is deleted outright. An unresolved name -- including one that would have matched a
    keyword before -- is now classified purely as ``""``, never by guessing."""
    normalized = _resolve_alias(_menu_key(item_name))
    return MENU_CATEGORY_MAP.get(normalized, "")


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
#
# #73: the keyword fallback that used to apply to a name not in the menu at all -- unconditional
# combo-drink-slot eligibility plus a conditional happy-hour discount, both matched by regex
# against "slush"/"limeade"/"shake"/"blast"/"tea"/etc. -- is deleted entirely, along with the
# regexes and helper functions that implemented it. A name that doesn't resolve now simply answers
# ``""``/``False``.
# ---------------------------------------------------------------------------


def infer_combo_component(item_name: str) -> str:
    """Classify *item_name* for combo-SLOT-FILLING only.

    Returns ``"sides"``, ``"drinks"``, or ``""`` (can't fill either combo slot, including when the
    name isn't on the menu at all). This answers ONLY "can this item fill a combo's included
    side/drink slot" -- happy-hour discount eligibility is a SEPARATE question, answered by
    ``is_happy_hour_discounted`` below, and must never be derived from this function's result (PR
    #50 review). Every on-menu item's answer is its own explicit ``comboSlot`` field in
    menuItems.json (#71); there is no keyword fallback for a name that isn't on the menu (#73
    removes it entirely -- ``tools.py``'s ``update_order`` rejects such a name outright as
    ``not_on_menu`` before classification is even reached). *item_name* may carry a parenthesized
    customization suffix (e.g. "Tots (Extra Crispy)") -- ``_menu_key`` strips it before any lookup
    so a customised item classifies identically to its base item (PR #50 review). Any of an
    item's spoken ``aliases`` (e.g. plain Tots's "Tot", "Tater Tot(s)", "Tator Tot(s)") resolves to
    the same combo slot as the canonical item -- an explicit, exact-match alias map read from the
    pack (``_resolve_alias``), never a substring check (originally Brian's decision, 2026-09-25,
    #60; broadened to every lookup by #71)."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return ""
    combo_slot = fields["comboSlot"]
    return combo_slot if combo_slot in ("sides", "drinks") else ""


def is_happy_hour_discounted(item_name: str) -> bool:
    """Whether *item_name* gets the happy-hour discount -- a SEPARATE question from
    ``infer_combo_component`` above (PR #50 review): don't derive one from the other. Every
    on-menu item's answer is its own explicit ``happyHourDiscounted`` field in menuItems.json
    (#71): sundaes and Shakes & Blasts are ``false`` (Brian's decisions, #39 and 2026-09-25), and
    every Slushes & Drinks item is ``true``. A name that isn't on the menu at all answers
    ``False`` -- no keyword fallback (#73 removes it entirely). *item_name* may carry a
    parenthesized customization suffix -- ``_menu_key`` strips it before any lookup so a
    customised drink is discounted (or not) exactly like its base item (PR #50 review). Aliases
    resolve here too (#71; see ``_resolve_alias``)."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return False
    return fields["happyHourDiscounted"]


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

    Returns ``()`` for any item with no ``bundle`` field at all -- most menu items absorb nothing
    -- INCLUDING a name that isn't on the menu at all. #73 (ADR-001 decision 4 "No off-menu")
    removes the ``"combo" in name`` keyword heuristic this function used to fall back to for such
    a name: ``tools.py``'s ``update_order`` now rejects anything not on the menu outright as
    ``not_on_menu`` before it would ever reach ``order_state.py``'s bundle-slot accounting, so
    there is no longer any off-menu name for this fallback to apply to.

    >>> bundle_slots("French Toast Sticks Combo")
    ('drinks',)
    >>> bundle_slots("Crispy Tenders Dinner - 3 piece")
    ('drinks',)
    >>> bundle_slots("Tots")
    ()
    """
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return ()
    return fields["bundleSlots"]


def requires_machine(item_name: str) -> str | None:
    """Return the machine key (e.g. ``"slush_machine"``, ``"ice_cream_machine"``) *item_name*
    needs to be made, or ``None`` if it needs neither (or isn't on the menu at all).

    #73 (Rick's PR review): replaces the old ``_ICE_CREAM_MACHINE_KEYWORDS`` substring list in
    tools.py, which risked flagging an off-menu name (or missing a real one whose name didn't
    happen to contain a matched keyword) -- this is now purely data-driven off each item's own
    ``requiresMachine`` field in menuItems.json, resolved through the same ``_menu_key``/alias
    pipeline as every other classification here."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return None
    return fields["requiresMachine"]


def is_extra_item(item_name: str) -> bool:
    """Whether *item_name* is a chargeable "extra" add-on (e.g. "Add Bacon", "Whipped Topping")
    rather than a full menu item in its own right.

    #73 (Rick's PR review): replaces the old ``EXTRAS_KEYWORDS`` name-substring list in tools.py,
    which risked matching a name that isn't a real menu item at all (e.g. an off-menu "Extra
    Patty") -- this is now purely data-driven off each item's own ``isExtra`` field in
    menuItems.json, resolved through the same ``_menu_key``/alias pipeline as every other
    classification here (so "Whipped Cream" -- an alias of the real "Whipped Topping" item --
    resolves to the same answer as its canonical name). A name that isn't on the menu at all
    answers ``False``."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return False
    return fields["isExtra"]


def resolve_menu_item(item_name: str) -> dict | None:
    """Resolve *item_name* to its on-menu record, or ``None`` if it isn't on the menu at all.

    THE single "is this a real, orderable item" gate (#73, ADR-001 decision 4: "No off-menu. If
    it's not on the menu in our source data, you cannot order it."). An item is on the menu iff
    its normalized name (``_menu_key``, which also strips a parenthesized customization suffix) or
    one of its ``aliases`` is an exact key in the pack's own data -- never a substring/keyword
    match. ``tools.py``'s ``update_order`` calls this first, before any other add-time validation,
    and rejects anything it returns ``None`` for as ``not_on_menu``.

    Returns a dict with the item's canonical ``name`` (the exact menuItems.json spelling, for
    error messages), ``category``, and ``sizes`` (a tuple of ``canonical_size_key(...)`` values,
    for the caller's own size-availability check) -- a defensive copy, not the shared internal
    fields dict, so a caller can't accidentally mutate module state."""
    normalized = _resolve_alias(_menu_key(item_name))
    fields = _MENU_ITEM_FIELDS.get(normalized)
    if fields is None:
        return None
    return {"name": fields["name"], "category": fields["category"], "sizes": fields["sizes"]}
