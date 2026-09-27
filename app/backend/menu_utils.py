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
    "get_catalog_for_persona",
]

logger = logging.getLogger(__name__)

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
) -> tuple[dict[str, dict], dict[str, str]]:
    """Load every menu item from a pack's menu/menuItems.json, once, keyed by ``_menu_key(name)``.

    ``menu_path``/``size_key_fn`` (#74): a specific persona's own ``menu_path`` and its OWN
    size-alias resolver -- see ``MenuCatalog.from_persona`` below, the only caller -- so two
    personas with different size vocabularies each get correctly keyed ``sizes``/``prices`` maps,
    one loader implementation, not a second copy per persona.

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
    """
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
                }
                for alias in item.get("aliases") or ():
                    alias_key = _menu_key(alias)
                    if alias_key:
                        alias_map[alias_key] = key
        return item_fields, alias_map
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
    ):
        self.persona_id = persona_id
        self.size_map = size_map
        self.size_aliases = size_aliases
        self.hidden_sizes = hidden_sizes
        self.spoken_as = spoken_as
        self.item_fields = item_fields
        self.alias_map = alias_map
        self.category_map: dict[str, str] = {key: fields["category"] for key, fields in item_fields.items()}

    @classmethod
    def from_persona(cls, persona: Persona) -> MenuCatalog:
        sizes_cfg = persona.manifest.sizes
        size_map = dict(sizes_cfg.canonical)
        size_aliases = dict(sizes_cfg.aliases)
        hidden_sizes = frozenset((s or "").strip().lower() for s in sizes_cfg.hidden)
        spoken_as = dict(sizes_cfg.spokenAs)

        def _size_key_fn(size: str) -> str:
            return _canonical_size_key_for(size, size_aliases, hidden_sizes)

        item_fields, alias_map = _load_menu_data(persona.menu_path, size_key_fn=_size_key_fn)
        return cls(persona.id, size_map, size_aliases, hidden_sizes, spoken_as, item_fields, alias_map)

    def normalize_size(self, size: str) -> str:
        return _normalize_size_for(size, self.size_map, self.size_aliases, self.hidden_sizes)

    def canonical_size_key(self, size: str) -> str:
        return _canonical_size_key_for(size, self.size_aliases, self.hidden_sizes)

    def _resolve_alias(self, normalized: str) -> str:
        return self.alias_map.get(normalized, normalized)

    def spoken(self, text: str) -> str:
        """Apply this persona's ``sizes.spokenAs`` substitutions to a display string.

        #74: replaces ``order_state.py``'s old hardcoded ``"RT 44"``/``"RT44"`` -> ``"Route 44"``
        readback substitution -- Sonic's own persona.json ``spokenAs`` block is exactly that same
        mapping, so this is a no-op behavior change for Sonic, but a future persona with its own
        size vocabulary now drives its own readback text instead of inheriting Sonic's."""
        for raw, spoken in self.spoken_as.items():
            text = text.replace(raw, spoken)
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
        }

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
