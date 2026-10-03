"""Shared menu utilities — per-persona size mappings, classification, and menu data.

Both ``tools.py`` and ``order_state.py`` need size normalisation and category
inference. :class:`MenuCatalog` is the single source of truth for both -- one
implementation, instantiated once per enabled persona from that persona's OWN
``sizes``/``menu/menuItems.json`` data (design doc 4.4, #74), so two personas with
different size vocabularies or menus can never leak into each other. There is no
module-level brand-specific fallback: #74 (Rick's PR #102 review, item 2) removes
the old module-level ``SIZE_MAP``/``SIZE_ALIASES``/``MENU_CATEGORY_MAP`` globals and
their free functions entirely -- a session with no persona explicitly bound resolves
its menu through :mod:`default_persona`'s catalog default, the exact same
``MenuCatalog`` path as any other persona, never a second, single-brand-only code path.

#73 (ADR-001 decision 4: "No off-menu. If it's not on the menu in our source data, you cannot
order it."): every keyword/substring fallback that used to classify a name NOT found in the
persona pack's own data is deleted outright, and ``MenuCatalog.resolve_menu_item`` below is now
THE single gate ``tools.py``'s ``update_order`` calls before adding anything to an order -- an
item resolves if and only if its normalized name or one of its own ``aliases`` is an exact key in
the pack's data. A name that doesn't resolve gets each classification method's safe default
(``""`` / ``False`` / ``()`` / ``None``), never a keyword guess.
"""

from __future__ import annotations

import json
import logging
import re
from pathlib import Path
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from persona_loader import Persona

__all__ = [
    "strip_modifiers",
    "_menu_key",
    "MenuCatalog",
    "MenuKeyCollisionError",
    "validate_menu_key_collisions",
    "get_catalog_for_persona",
]

_SPOKEN_LEFT_BOUNDARY = r"(?<![A-Za-z0-9])"
_SPOKEN_TRADEMARK_CHARS = "®™"


def _spoken_substitution_pattern(raw: str) -> str:
    """Literal spokenAs match that will not rewrite the middle of another token."""
    right_boundary_chars = "A-Za-z0-9" if raw.endswith(tuple(_SPOKEN_TRADEMARK_CHARS)) else f"A-Za-z0-9{_SPOKEN_TRADEMARK_CHARS}"
    return rf"{_SPOKEN_LEFT_BOUNDARY}{re.escape(raw)}(?![{right_boundary_chars}])"

logger = logging.getLogger(__name__)


class MenuKeyCollisionError(Exception):
    """Raised when a persona pack's ``menu/menuItems.json`` has two menu items whose
    ``_menu_key(name)`` collide, or an alias whose ``_menu_key`` collides with another item's own
    lookup key or another item's own alias (issue #128).

    ``_menu_key``'s modifier-stripping (see its own doc comment) is BY DESIGN -- "Tots" and "Tots
    (Extra Crispy)" must classify identically -- but that exact same stripping silently collapsed
    a draft pack's five differently-priced "(N piece)" nugget-size items into one key, with
    whichever one loaded last winning and the other four unreachable/mispriced (the class of
    correctness bug #87 warns about, first caught for issue #128). Raising here, at load time,
    means a colliding pack can never load a degraded/last-write-wins catalog: the message always
    names the persona pack and every colliding name, so the fix is obvious from the error message
    alone -- the same fail-fast convention as ``persona_loader.PersonaValidationError`` (design doc
    section 6)."""


def validate_menu_key_collisions(menu_raw: dict, pack_label: str, menu_path: Path | str = "") -> None:
    """Raise :class:`MenuKeyCollisionError` if *menu_raw* (a pack's own parsed ``menuItems.json``)
    has two menu items whose ``_menu_key(name)`` collide, or an alias whose ``_menu_key`` collides
    with another item's own lookup key or another item's own alias (issue #128; design doc
    section 6). *pack_label* (usually the persona id) and *menu_path* (if given) are folded into
    the error message so a broken pack is fixable from the message alone.

    Two passes, not one: every item's own key must be fully known (pass 1) before any alias is
    checked (pass 2), so an alias declared on an EARLIER item that collides with a LATER item's own
    key is still caught, regardless of file order.

    This is the single implementation of the rule -- both ``_load_menu_data`` below (the actual
    per-persona ``MenuCatalog`` loader) and ``persona_loader.PersonaCatalog.load()`` (process
    startup, every enabled persona, before any session ever binds to it) call this same function,
    so a colliding pack fails identically and immediately no matter which path constructs it first.
    """
    location_suffix = f" in {menu_path}" if menu_path else ""
    item_names_by_key: dict[str, str] = {}
    ordered_items: list[tuple[str, list[str]]] = []
    for category_entry in menu_raw.get("menuItems", []):
        for item in category_entry.get("items", []):
            name = item.get("name")
            if not name:
                continue
            key = _menu_key(name)
            if key in item_names_by_key:
                raise MenuKeyCollisionError(
                    f"Persona '{pack_label}': menu items '{item_names_by_key[key]}' and '{name}' "
                    f"both normalize to the same lookup key '{key}'{location_suffix} -- rename "
                    "one so they resolve distinctly (design doc section 6)."
                )
            item_names_by_key[key] = name
            ordered_items.append((name, list(item.get("aliases") or ())))

    alias_owner: dict[str, str] = {}
    for name, aliases in ordered_items:
        for alias in aliases:
            alias_key = _menu_key(alias)
            if not alias_key:
                continue
            if alias_key in item_names_by_key and item_names_by_key[alias_key] != name:
                raise MenuKeyCollisionError(
                    f"Persona '{pack_label}': alias '{alias}' on item '{name}' normalizes to "
                    f"'{alias_key}', which collides with menu item "
                    f"'{item_names_by_key[alias_key]}''s own lookup key{location_suffix} -- an "
                    "ambiguous alias must not resolve silently (design doc section 6)."
                )
            if alias_key in alias_owner and alias_owner[alias_key] != name:
                raise MenuKeyCollisionError(
                    f"Persona '{pack_label}': alias '{alias}' (normalized '{alias_key}') is "
                    f"declared on both '{alias_owner[alias_key]}' and '{name}'{location_suffix} -- "
                    "an ambiguous alias must not resolve silently (design doc section 6)."
                )
            alias_owner[alias_key] = name

# #74: the two functions below are parameterized on (size_map, size_aliases, hidden_sizes) so
# ``MenuCatalog`` (below) can share the EXACT SAME algorithm for every persona's own sizes block --
# one implementation, not a second, independently-maintained copy per persona.
def _compact_size_key(size: str) -> str:
    key = (size or "").strip().lower()
    return "".join(ch for ch in key if ch not in _SIZE_ALIAS_IGNORED_CHARS)


# Punctuation ignored when compacting a size string for alias lookup (PR #50 review follow-up):
# "Route-44" and "rt. 44" must resolve identically to "route44"/"rt44" -- whitespace alone wasn't
# enough to catch the hyphen or period variants. Generic (not persona-specific): every persona's
# own alias table is matched through this same punctuation-stripping rule.
_SIZE_ALIAS_IGNORED_CHARS = frozenset(" .-")


def _normalize_size_for(size: str, size_map: dict[str, str], size_aliases: dict[str, str],
                        hidden_sizes: frozenset[str]) -> str:
    key = (size or "").strip().lower()
    if key in hidden_sizes:
        return ""
    return size_map.get(_canonical_size_key_for(size, size_aliases, hidden_sizes), "")


def _canonical_size_key_for(size: str, size_aliases: dict[str, str], hidden_sizes: frozenset[str]) -> str:
    key = (size or "").strip().lower()
    if key in hidden_sizes:
        return "standard"
    compact_key = _compact_size_key(size)
    if compact_key in size_aliases:
        return size_aliases[compact_key]
    return size_aliases.get(key, key)


# ---------------------------------------------------------------------------
# Modifier-suffix stripping & the ONE lookup-key normalisation rule (PR #50 review)
#
# Defined BEFORE _load_menu_data()/MenuCatalog below so a persona's own item map can be keyed
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
    resolution) AND for each persona's own ``MenuCatalog.category_map`` keys below. A customised
    item must classify
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
# Menu item data loader -- reads one persona pack's menu/menuItems.json into the shape
# MenuCatalog needs (item_fields, alias_map). #74: no longer hardcoded to any one persona --
# MenuCatalog.from_persona (below) passes each enabled persona's own menu_path.
# ---------------------------------------------------------------------------
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
# found in the map above is deleted entirely (it lived in ``MenuCatalog.infer_category``,
# ``infer_combo_component``, ``is_happy_hour_discounted``, and ``bundle_slots``, plus the
# ``_keyword_fallback_combo_drink``/``_keyword_fallback_happy_hour_discounted`` helpers and their
# ``_DR_PEPPER_RE``/``_FOUNTAIN_DRINK_KEYWORD_RE``/``_SHAKE_BLAST_KEYWORD_RE`` regexes, all now
# removed). ``MenuCatalog.resolve_menu_item`` below is the single on-menu gate ``tools.py``'s
# ``update_order`` calls before adding anything; every other classification method now simply
# returns its safe default for a name that doesn't resolve, never a keyword guess.


def _load_menu_data(
    menu_path: Path,
    size_key_fn: Any,
    persona_id: str = "",
) -> tuple[dict[str, dict], dict[str, str]]:
    """Load every menu item from a pack's menu/menuItems.json, once, keyed by ``_menu_key(name)``.

    ``menu_path``/``size_key_fn`` (#74): a specific persona's own ``menu_path`` and its OWN
    size-alias resolver -- see ``MenuCatalog.from_persona`` below, the only caller -- so two
    personas with different size vocabularies each get correctly keyed ``sizes``/``prices`` maps,
    one loader implementation, not a second copy per persona. ``persona_id`` (#128) is only used to
    name the pack in a :class:`MenuKeyCollisionError` message; it falls back to the menu file's own
    grandparent directory name (``.../personas/<id>/menu/menuItems.json``) when omitted.

    Returns ``(item_fields, alias_map)``:

    - ``item_fields``: normalized item key -> ``{"name", "category", "comboSlot",
      "happyHourDiscounted", "bundleSlots", "requiresMachine", "isExtra", "sizes", "prices"}``,
      read straight from each item's fields in menuItems.json. Missing fields fall back to their
      schema-documented safe defaults (``"none"`` / ``False`` / ``None`` / ``()``), matching a
      pack that hasn't been fully populated yet. ``prices``: canonical size key -> that size's
      unit price (float), read straight from the pack (Rick's #74 follow-up: the unit price a
      guest is charged comes from this menu record, never from the tool call's own ``price``
      argument).
    - ``alias_map``: normalized alias key -> the canonical item's normalized key, built from each
      item's ``aliases`` list. An alias resolves the item for EVERY lookup below (category, combo
      slot, happy-hour discount, machine requirement, extra-item classification, and #73's
      on-menu resolution) -- not just the combo side slot as #60 originally scoped it (design doc
      section 4.3/6, issue #71). For "Tots" this is observably identical to #60's narrower scope:
      see ``TotsAliasResolvesEverywhereTests`` in test_menu_utils.py.

    Raises :class:`MenuKeyCollisionError` (#128, issue #87's correctness-bug class) if two items
    normalize to the same lookup key, or an alias collides with another item's own key or another
    item's own alias -- see :func:`validate_menu_key_collisions`, called below BEFORE this
    function's own ``item_fields``/``alias_map`` are built, so a colliding pack never gets a
    partial/last-write-wins catalog, not even transiently.
    """
    if not menu_path.exists():
        return {}, {}
    try:
        with menu_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        pack_label = persona_id or menu_path.parent.parent.name
        validate_menu_key_collisions(data, pack_label, menu_path)
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
                item_sizes = item.get("sizes") or ()
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
                    "sizes": tuple(dict.fromkeys(size_key_fn(s.get("size", "")) for s in item_sizes)),
                    # #74 (Rick's follow-up): each offered size's own unit price, keyed the same
                    # way -- the single source of truth ``tools.py``'s ``update_order`` now charges
                    # from, instead of trusting whatever ``price`` the tool call itself supplied.
                    "prices": {
                        size_key_fn(s.get("size", "")): s["price"]
                        for s in item_sizes
                        if isinstance(s.get("price"), (int, float))
                    },
                    # #77: this item's own bundle-slot auto-fill map (menu.schema.json
                    # ``bundle.autoFill``) -- slot -> a guest-facing filler description, e.g.
                    # {"sides": "Medium Fries"}. A slot present in ``bundleSlots`` above but absent
                    # here stays an "absorb only" slot: unchanged behavior, the guest still has to
                    # name a real side/drink for it. Empty dict for an item with no ``bundle``
                    # field, or one that bundles slots but never auto-fills any of them (every
                    # real pack today, until a pack owner opts a slot in).
                    "bundleAutoFill": dict(item.get("bundle", {}).get("autoFill") or {}),
                    # #77: this bundle's own default size (menu.schema.json ``bundle.defaultSize``)
                    # -- used only to resolve a ``{size}`` placeholder in a ``bundleAutoFill``
                    # template when the guest ordered the bundle itself with no size given.
                    "bundleDefaultSize": item.get("bundle", {}).get("defaultSize"),
                    # #77 (`strategies.searchQueryRewrite: "meal_numbers"`, design doc section
                    # 3.3 row 23): this item's own numbered-meal id ("1", "2", ...) and the
                    # daypart it's offered in ("allDay"/"breakfast"/"lunch"), straight from the
                    # pack -- replaces the old hardcoded ``MEAL_NUMBER_MAP`` + regex. ``None`` for
                    # any item that isn't a numbered meal at all (every pack today except one
                    # that opts into "meal_numbers").
                    "mealNumber": item.get("mealNumber"),
                    "menuPeriod": item.get("menuPeriod"),
                }
                for alias in item.get("aliases") or ():
                    alias_key = _menu_key(alias)
                    if alias_key:
                        alias_map[alias_key] = key
        return item_fields, alias_map
    except MenuKeyCollisionError:
        # #128: a colliding pack must fail fast, never be logged-and-swallowed into an empty/
        # degraded catalog by the generic handler below -- re-raise before it's reached.
        raise
    except Exception as exc:  # pragma: no cover
        logger.warning("Failed to load menu items: %s", exc)
        return {}, {}


# ---------------------------------------------------------------------------
# MenuCatalog (#74): one persona's own size vocabulary + menu/classification data.
#
# ``_normalize_size_for``/``_canonical_size_key_for``/``_load_menu_data``/``_menu_key`` above are
# all shared, parameterized algorithms -- exactly one implementation, never a second, independently
# maintained copy -- instantiated once per enabled persona from that persona's OWN
# ``sizes``/``menu/menuItems.json`` data (design doc 4.4). ``order_state.py`` and ``tools.py``
# resolve every session's bound persona (including the deployment-wide default, #74 item 2 -- see
# ``default_persona.py``) to one of these; there is no other, module-level way to classify a menu
# item in this codebase.
#
# #39 / PR #50 review: combo-slot-filling (``infer_combo_component``) and happy-hour discount
# eligibility (``is_happy_hour_discounted``) are two SEPARATE questions and must never be derived
# from one shared bucket -- they agree on almost everything today, but that's incidental, not
# structural (e.g. Shakes & Blasts fill a combo's drink slot but are NOT happy-hour-discounted,
# Brian's decision 2026-09-25).
# ---------------------------------------------------------------------------


class MenuCatalog:
    """A persona's own size vocabulary + menu item data, built once and cached per persona id."""

    def __init__(
        self,
        persona_id: str,
        size_map: dict[str, str],
        size_aliases: dict[str, str],
        hidden_sizes: frozenset[str],
        spoken_as: dict[str, str],
        item_fields: dict[str, dict],
        alias_map: dict[str, str],
        machines: dict[str, tuple[str, str]] | None = None,
        allowed_extra_categories: list[str] | None = None,
        blocked_extra_categories: list[str] | None = None,
        invalid_modifiers: dict[str, list[str]] | None = None,
        bundle_name_markers: list[str] | None = None,
        bundle_convert_standalone: bool = False,
        bundle_missing_part_text: dict[str, str] | None = None,
        bundle_resize_rule: str = "includedAnySize",
        bundle_included_size: str | None = None,
        split_combined_names: bool = False,
        search_query_rewrite: str = "",
    ):
        self.persona_id = persona_id
        self.size_map = size_map
        self.size_aliases = size_aliases
        self.hidden_sizes = hidden_sizes
        self.spoken_as = spoken_as
        self.item_fields = item_fields
        self.alias_map = alias_map
        self.category_map: dict[str, str] = {key: fields["category"] for key, fields in item_fields.items()}
        # #74 (Rick's PR #102 review, round 3, required item 1): this persona's OWN "store
        # telemetry"/extras-gate/invalid-modifier rules, straight from its pack -- replaces the
        # module-level `MOCK_MACHINE_STATUS`/`ALLOWED_EXTRA_CATEGORIES`/`BLOCKED_EXTRA_CATEGORIES`/
        # `INVALID_MODS` globals tools.py used to read directly (always the default persona's own
        # data, no matter which persona a session was bound to). `MenuCatalog` is already the one "this persona's
        # resolved data" object threaded through every tools.py call site (`_menu_for`), so these
        # live here rather than adding a fourth per-session resolution helper alongside it.
        # #77: each machine's own (status, guest-facing label) pair -- replaces the bare
        # ``{"machine": "down"}`` string-status map (and the module-level, name-keyed
        # ``tools._MACHINE_OOS_LABELS`` dict, now deleted) with data every persona owns for
        # itself: `machine_status()`/`machine_label()` below.
        self.machines: dict[str, tuple[str, str]] = dict(machines or {})
        self.allowed_extra_categories: frozenset[str] = frozenset(
            (c or "").strip().lower() for c in (allowed_extra_categories or ())
        )
        self.blocked_extra_categories: frozenset[str] = frozenset(
            (c or "").strip().lower() for c in (blocked_extra_categories or ())
        )
        self.invalid_modifiers: dict[str, list[str]] = {
            (k or "").strip().lower(): list(v) for k, v in (invalid_modifiers or {}).items()
        }
        # #77 (shared bundle engine, design doc 3.3 rows 20/21): this persona's own
        # ``bundles.nameMarkers``/``convertStandalone``/``missingPartText`` -- e.g. one pack's own
        # ``["combo"]``/``true``/{"sides": "a side (fries or tots)", ...}`` (an exact,
        # behavior-neutral match for what used to be hardcoded in order_state.py), another pack's
        # ``["meal"]``, and a pack with no bundles at all owning ``[]``/``false``.
        self.bundle_name_markers: tuple[str, ...] = tuple(
            (m or "").strip().lower() for m in (bundle_name_markers or ()) if (m or "").strip()
        )
        self.bundle_convert_standalone: bool = bool(bundle_convert_standalone)
        self.bundle_missing_part_text: dict[str, str] = dict(bundle_missing_part_text or {})
        # PR #184 round 2 plus #205: "includedAnySize" (default), "wholeBundleSize", or
        # "componentUpcharge" -- see persona_loader.py's `_Bundles.resizeRule` doc comment. Falls
        # back to the default for any unrecognized value rather than crashing, matching every
        # other persona-data field's "never trust the pack blindly" posture.
        self.bundle_resize_rule: str = (
            bundle_resize_rule
            if bundle_resize_rule in ("includedAnySize", "wholeBundleSize", "componentUpcharge")
            else "includedAnySize"
        )
        self.bundle_included_size: str = self.canonical_size_key(bundle_included_size or "")
        # #77 (shared extras engine): whether a not-on-menu name that's really two known items
        # joined by a connector word ("Latte with Extra Shot") should be split into two
        # ``suggested_calls`` instead of a flat rejection -- only a pack that opts in sets this.
        self.split_combined_names: bool = bool(split_combined_names)
        # #77 (`strategies.searchQueryRewrite`, design doc 3.3 row 23): this persona's own named
        # extension point -- "meal_numbers" is the only named strategy today; anything else
        # (including "") is a no-op, so ``rewrite_search_query`` below degrades safely for every
        # persona that doesn't opt in.
        self.search_query_rewrite: str = search_query_rewrite or ""
        # #77: number (as the pack's own string, e.g. "1") -> every real item name that claims
        # it, built once here so a breakfast/lunch overlap ("#1" in both dayparts) returns BOTH
        # real names rather than picking one -- disambiguation is left to the model/prompt, which
        # already has the guest's own wording (and the search result's own daypart) to go on.
        self.meal_number_index: dict[str, list[str]] = {}
        for fields in item_fields.values():
            number = fields.get("mealNumber")
            if number:
                self.meal_number_index.setdefault(str(number), []).append(fields["name"])

    @classmethod
    def from_persona(cls, persona: Persona) -> MenuCatalog:
        sizes_cfg = persona.manifest.sizes
        size_map = dict(sizes_cfg.canonical)
        size_aliases = dict(sizes_cfg.aliases)
        hidden_sizes = frozenset((s or "").strip().lower() for s in sizes_cfg.hidden)
        spoken_as = dict(sizes_cfg.spokenAs)

        def _size_key_fn(size: str) -> str:
            return _canonical_size_key_for(size, size_aliases, hidden_sizes)

        item_fields, alias_map = _load_menu_data(
            persona.menu_path, size_key_fn=_size_key_fn, persona_id=persona.id
        )
        extras_cfg = persona.manifest.extras
        bundles_cfg = persona.manifest.bundles
        machines = {key: (m.status, m.label) for key, m in persona.manifest.machines.items()}
        return cls(
            persona.id, size_map, size_aliases, hidden_sizes, spoken_as, item_fields, alias_map,
            machines=machines,
            allowed_extra_categories=extras_cfg.allowedBaseCategories,
            blocked_extra_categories=extras_cfg.blockedBaseCategories,
            invalid_modifiers=persona.manifest.invalidModifiers,
            bundle_name_markers=bundles_cfg.nameMarkers,
            bundle_convert_standalone=bundles_cfg.convertStandalone,
            bundle_missing_part_text=bundles_cfg.missingPartText,
            bundle_resize_rule=bundles_cfg.resizeRule,
            bundle_included_size=bundles_cfg.includedSize,
            split_combined_names=extras_cfg.splitCombinedNames,
            search_query_rewrite=persona.manifest.strategies.searchQueryRewrite,
        )

    def machine_status(self, machine: str) -> str | None:
        """This persona's own reported status ("down"/"operational") for *machine* (its
        ``requiresMachine`` key), or ``None`` if this persona's pack never mentions it."""
        entry = self.machines.get(machine)
        return entry[0] if entry else None

    def machine_label(self, machine: str) -> str:
        """This persona's own guest-facing OOS label for *machine* (persona.json
        ``machines.<key>.label``), falling back to a generic "<machine> is down" for a machine key
        the pack never mentions (e.g. a future machine added to a pack before its label is)."""
        entry = self.machines.get(machine)
        return entry[1] if entry else f"{machine} is down"

    def normalize_size(self, size: str) -> str:
        return _normalize_size_for(size, self.size_map, self.size_aliases, self.hidden_sizes)

    def canonical_size_key(self, size: str) -> str:
        return _canonical_size_key_for(size, self.size_aliases, self.hidden_sizes)

    def _resolve_alias(self, normalized: str) -> str:
        return self.alias_map.get(normalized, normalized)

    def spoken(self, text: str) -> str:
        """Apply this persona's ``sizes.spokenAs`` spoken-readback substitutions.

        #74: replaces ``order_state.py``'s old hardcoded ``"RT 44"``/``"RT44"`` -> ``"Route 44"``
        readback substitution -- Sonic's own persona.json ``spokenAs`` block is exactly that same
        mapping, so this is a no-op behavior change for Sonic, but a future persona with its own
        size vocabulary now drives its own readback text instead of inheriting Sonic's.
        Longer keys are applied first, and matches require alphanumeric/trademark boundaries so
        item-name pronunciation hints (e.g. MUNCHKINS® -> Munchkins) cannot rewrite unrelated
        tokens or leave a registered mark behind."""
        for raw, spoken in sorted(self.spoken_as.items(), key=lambda item: len(item[0]), reverse=True):
            if not raw:
                continue
            text = re.sub(_spoken_substitution_pattern(raw), spoken, text)
        return text

    def infer_category(self, item_name: str) -> str:
        normalized = self._resolve_alias(_menu_key(item_name))
        return self.category_map.get(normalized, "")

    def infer_combo_component(self, item_name: str) -> str:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return ""
        combo_slot = fields["comboSlot"]
        return combo_slot if combo_slot in ("sides", "drinks") else ""

    def is_happy_hour_discounted(self, item_name: str) -> bool:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        return bool(fields["happyHourDiscounted"]) if fields else False

    def bundle_slots(self, item_name: str) -> tuple[str, ...]:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        return fields["bundleSlots"] if fields else ()

    def bundle_default_size(self, item_name: str) -> str:
        """PR #184 round 4 (Rick's review, item 4): this bundle item's own ``menu.schema.json``
        ``bundle.defaultSize`` (e.g. "Medium" on an S/M/L meal), or ``""`` for an item with no
        bundle data or no configured default size. The single place ``tools.update_order``'s
        size-validation gate reads to map a guest's missing/"Standard" size onto the pack's real
        default meal size instead of rejecting it outright -- see ``bundle_autofill``'s own,
        pre-existing use of this same field for the autofilled component's display label."""
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        return (fields.get("bundleDefaultSize") or "") if fields else ""

    def bundle_autofill(self, item_name: str, resolved_size_label: str = "") -> dict[str, str]:
        """This bundle item's own slot -> filler-description map (menu.schema.json
        ``bundle.autoFill``), with a literal ``{size}`` token in a template replaced by
        *resolved_size_label* (falling back to the bundle's own ``bundle.defaultSize`` when the
        guest ordered it with no usable size). Only the slots a pack actually opts in to
        auto-filling are returned; every other one of this item's ``bundle_slots()`` stays
        absorb-only, unchanged from today's behavior for every real pack (#77: no real pack
        populates ``autoFill`` yet -- see docs/persona-architecture.md section 3.3 row 19)."""
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return {}
        template_map = fields.get("bundleAutoFill") or {}
        if not template_map:
            return {}
        default_size = fields.get("bundleDefaultSize") or ""
        size_label = default_size if not resolved_size_label or resolved_size_label.lower() == "standard" else resolved_size_label
        return {slot: template.replace("{size}", size_label).strip() for slot, template in template_map.items()}

    def bundle_autofill_names(self, item_name: str) -> dict[str, str]:
        """PR #184 round 3 (Rick's review, item E): the same slot -> filler map as
        ``bundle_autofill``, but with the literal ``{size}`` token (and the trailing space its
        template builds in) stripped rather than substituted with a real size label -- the BASE,
        on-menu item name an autofilled slot's ``item`` field should hold (e.g. "World Famous
        Fries®"), independent of whatever size currently fills it. A template with no ``{size}``
        token at all (e.g. "Hash Browns", a single-size autofill) is returned unchanged -- it was
        never size-baked-in to begin with."""
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return {}
        template_map = fields.get("bundleAutoFill") or {}
        if not template_map:
            return {}
        return {slot: template.replace("{size}", "").strip() for slot, template in template_map.items()}

    def meal_number_candidates(self, number: str) -> list[str]:
        """Every real menu item name that claims numbered-meal id *number* (persona.json
        ``mealNumber``) -- possibly more than one when a breakfast and a lunch meal share the same
        number, e.g. a pack's own "#1" (design doc section 3.3 row 23). Empty for a persona that
        has no numbered meals at all."""
        return list(self.meal_number_index.get(str(number).strip(), ()))

    def rewrite_search_query(self, query: str) -> str:
        """Apply this persona's own ``strategies.searchQueryRewrite`` to *query*, or return it
        unchanged for any persona that doesn't opt into a named strategy.

        "meal_numbers": a guest asking for "number 1" or "meal 6" doesn't literally
        say any menu item's own name, so a plain-text search can miss it entirely. When the query
        contains a bare integer, every real item name that claims that number (``mealNumber``) is
        appended to the query text -- never REPLACING the guest's own words, so the search index
        still sees "number 1" too -- letting Azure Search's own text index match on the real
        name(s) it actually indexed."""
        if self.search_query_rewrite != "meal_numbers":
            return query
        match = re.search(r"\d+", query)
        if not match:
            return query
        candidates = self.meal_number_candidates(match.group())
        if not candidates:
            return query
        return f"{query} {' '.join(candidates)}"

    def try_split_combined_name(self, item_name: str) -> tuple[str, str] | None:
        """#77 (shared extras engine, ``extras.splitCombinedNames``): if *item_name* isn't on the
        menu but is really a known base item plus a known extra joined by a connector word (e.g.
        "Caramel Latte with Extra Shot"), return ``(base_name, extra_name)`` using each item's own
        real menu name -- ``None`` if this persona doesn't opt in, or the name doesn't split into
        exactly one resolvable base item + one resolvable ``isExtra`` item."""
        if not self.split_combined_names:
            return None
        for connector in (" with ", " and ", " + ", " & "):
            pattern = re.compile(re.escape(connector.strip()), re.IGNORECASE)
            m = pattern.search(item_name)
            if not m:
                continue
            base_part = item_name[: m.start()].strip()
            extra_part = item_name[m.end():].strip()
            if not base_part or not extra_part:
                continue
            base_item = self.resolve_menu_item(base_part)
            if base_item is None or not self.is_extra_item(extra_part):
                continue
            extra_item = self.resolve_menu_item(extra_part)
            if extra_item is None:
                continue
            return base_item["name"], extra_item["name"]
        return None

    def requires_machine(self, item_name: str) -> str | None:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        return fields["requiresMachine"] if fields else None

    def is_extra_item(self, item_name: str) -> bool:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        return bool(fields["isExtra"]) if fields else False

    def resolve_menu_item(self, item_name: str) -> dict | None:
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return None
        return {
            "name": fields["name"],
            "category": fields["category"],
            "sizes": fields["sizes"],
            "prices": dict(fields.get("prices", {})),
            # #165: this item's own declared daypart ("breakfast"/"lunch"/"allDay"), or ``None``
            # for an item that doesn't carry one (every item on a pack with no
            # ``features.dayparts`` today). ``item_available_now`` below is the single reader.
            "menuPeriod": fields.get("menuPeriod"),
        }

    def item_available_now(self, item_name: str, active_mode: str | None) -> bool:
        """#165: whether *item_name* is orderable in *active_mode* (``"breakfast"``/``"lunch"``),
        this session's own bound daypart (``order_state.OrderState.get_menu_mode``) -- ``True``
        for *active_mode* ``None`` (a persona with no ``features.dayparts`` at all, or an
        unresolved item name -- #73's own on-menu gate, not this one, is the right place to
        reject an unknown name). An item with no ``menuPeriod`` of its own, or ``"allDay"``, is
        available in every mode a pack declares -- only an item whose own ``menuPeriod`` names
        the OTHER daypart is rejected. Mirrors the original app's real daypart split (design doc
        section on #165): standalone entrees/sandwiches/meals carry their own single daypart,
        while fries/drinks/desserts/sides carry ``"allDay"`` and are never gated."""
        if active_mode is None:
            return True
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return True
        period = fields.get("menuPeriod")
        if not period or period == "allDay":
            return True
        return period == active_mode

    def price_for(self, item_name: str, size_key: str) -> float | None:
        """Rick's #74 follow-up: the unit price for *item_name* at *size_key*, straight from this
        persona's own menu record -- ``None`` if the item or that size isn't on the menu."""
        normalized = self._resolve_alias(_menu_key(item_name))
        fields = self.item_fields.get(normalized)
        if fields is None:
            return None
        return fields.get("prices", {}).get(size_key)


_catalog_cache: dict[str, MenuCatalog] = {}


def get_catalog_for_persona(persona: Persona) -> MenuCatalog:
    """Return (building and caching, if needed) *persona*'s own :class:`MenuCatalog`."""
    cached = _catalog_cache.get(persona.id)
    if cached is None:
        cached = MenuCatalog.from_persona(persona)
        _catalog_cache[persona.id] = cached
    return cached
