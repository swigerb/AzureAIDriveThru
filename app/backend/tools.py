import asyncio
import json
import logging
import time
from typing import Any

from azure.core.credentials import AzureKeyCredential
from azure.core.exceptions import HttpResponseError
from azure.identity import DefaultAzureCredential
from azure.search.documents.aio import SearchClient
from azure.search.documents.models import VectorizableTextQuery

import default_persona
from config_loader import get_config
from menu_utils import strip_modifiers
from order_state import order_state_singleton
from rtmt import RTMiddleTier, Tool, ToolResult, ToolResultDirection

logger = logging.getLogger(__name__)

# #74 (Rick's PR #102 review, item 2): every session is bound to a persona (the deployment
# default when none was explicitly requested) -- there is no unbound/no-persona code path left,
# so nothing here reads a module-level brand-specific menu/size/category global. A caller that
# used to import `canonical_size_key`/`normalize_size`/`is_extra_item`/`requires_machine`/
# `resolve_menu_item`/`infer_category`/`SIZE_MAP` straight from this module (or from
# ``menu_utils``) now resolves the exact same methods off a real ``MenuCatalog`` instance instead
# -- ``default_persona.get_default_menu_catalog()`` for the deployment default, or
# ``order_state_singleton.get_menu_catalog(session_id)`` for a specific session's own bound
# persona (see ``_menu_for`` below). Tests construct their fixture the same way (see
# tests/test_tool_calling.py's/test_tools_search.py's default-persona menu-catalog fixture).
__all__ = [
    "attach_tools_rtmt",
]

# Load centralized config
_config = get_config()
_cache_cfg = _config.get("cache", {})
_search_cfg = _config.get("search", {})
_biz_cfg = _config.get("business_rules", {})

# Module-level prompt loader — set by attach_tools_rtmt() at startup. This is the
# DEPLOYMENT-WIDE default a persona with no registered override falls back to (not a
# brand-specific global -- every persona ultimately gets a real, non-None prompt_loader, either
# its own registered one or this shared one); see _prompt_loader_for() below.
_prompt_loader = None

# #74: per-persona search/prompt runtime context, keyed by persona id, registered by
# attach_tools_rtmt()'s `personas` argument.
_persona_registry: dict[str, dict[str, Any]] = {}

# #74: the single deployment-wide search context (client + field config), captured by
# attach_tools_rtmt()'s own positional params -- the fallback _search_dispatch() uses for a
# persona with no registered override of its own.
_default_search_ctx: dict[str, Any] = {}


def _menu_for(session_id: str | None):
    """This session's bound persona :class:`~menu_utils.MenuCatalog` (#74; every session has
    one -- ``order_state_singleton.get_menu_catalog`` itself falls back to the deployment
    default for an unknown/expired id), or the deployment default catalog directly when there is
    no *session_id* at all (a direct call, e.g. from a test)."""
    if session_id is None:
        return default_persona.get_default_menu_catalog()
    return order_state_singleton.get_menu_catalog(session_id)


def _prompt_loader_for(session_id: str | None):
    """This session's bound persona's ``PromptLoader``, or the module-level default
    (``_prompt_loader``, set by ``attach_tools_rtmt()``) if the session's persona has no
    registered runtime context (#74)."""
    if session_id is not None:
        pid = order_state_singleton.get_persona_id(session_id)
        ctx = _persona_registry.get(pid)
        if ctx is not None and ctx.get("prompt_loader") is not None:
            return ctx["prompt_loader"]
    return _prompt_loader

# ---------------------------------------------------------------------------
# Search result cache — avoids redundant Azure AI Search round-trips for
# repeated menu queries within a short window (e.g. "what sizes do you have?").
# ---------------------------------------------------------------------------
_SEARCH_CACHE_TTL_SEC = _cache_cfg.get("search_ttl_seconds", 60.0)
_SEARCH_CACHE_MAX_SIZE = _cache_cfg.get("search_max_size", 128)

class _SearchCache:
    """Simple TTL cache for search results. Not thread-safe, but fine for
    single-threaded asyncio where all access is from the event loop."""
    __slots__ = ("_store", "_max_size")

    def __init__(self, max_size: int = _SEARCH_CACHE_MAX_SIZE):
        self._store: dict[str, tuple[float, ToolResult]] = {}
        self._max_size = max_size

    def get(self, key: str) -> ToolResult | None:
        entry = self._store.get(key)
        if entry is None:
            return None
        ts, result = entry
        if time.monotonic() - ts > _SEARCH_CACHE_TTL_SEC:
            del self._store[key]
            return None
        return result

    def put(self, key: str, result: ToolResult) -> None:
        if len(self._store) >= self._max_size:
            oldest_key = min(self._store, key=lambda k: self._store[k][0])
            del self._store[oldest_key]
        self._store[key] = (time.monotonic(), result)

    def clear(self) -> None:
        self._store.clear()

_search_cache = _SearchCache()


# ---------------------------------------------------------------------------
# Order quantity limits — prevents abuse (e.g. ordering 100 burgers) while
# staying realistic for a drive-thru window.
# ---------------------------------------------------------------------------
MAX_QUANTITY_PER_ITEM = _biz_cfg.get("max_item_quantity", 10)
MAX_TOTAL_ITEMS = _biz_cfg.get("max_order_items", 25)


# ---------------------------------------------------------------------------
# "Store Telemetry" (which machine, if any, is down right now) -- #74 (Rick's PR #102
# review, round 3, required item 1): this used to be a single module-level
# `MOCK_MACHINE_STATUS` dict, always the default persona's own machines no matter which
# pack a session was bound to. It is now each session's own bound persona's `machines` data
# (``persona.json``'s ``machines`` block), read off the resolved `MenuCatalog` (see
# `_menu_for` above / `menu.machine_status()`) -- never a module-level global. In
# production, a real deployment would still source this from an Azure Function / IoT
# Hub call keyed by the guest's own store/persona, not this mocked pack data.
# ---------------------------------------------------------------------------

# #73 (Rick's PR review): the old OOS check (`_ICE_CREAM_MACHINE_KEYWORDS`, a substring list) and
# the old extras check (`EXTRAS_KEYWORDS`, also a substring list) both risked matching names that
# aren't real menu items at all. Both are now data-driven off each item's own `requiresMachine`/
# `isExtra` fields (menu_utils.requires_machine() / menu_utils.is_extra_item()), read once at
# import time from the pack. See search()'s OOS annotation and update_order()'s extras check below.

# #77: the OOS label used to be a module-level, name-keyed Python dict here
# (`_MACHINE_OOS_LABELS`/`_machine_oos_label`) that only the default persona's own two machine
# keys ever populated -- a second persona's machine silently fell back to a generic "<key> is
# down" string with no way to override it. Every persona now owns its own machines' labels in its
# OWN persona.json (`machines.<key>.label`), read via `menu.machine_label()` below -- see
# menu_utils.MenuCatalog.machine_label / machine_status and persona_loader._Machine.

# #74 (Rick's PR #102 review, round 3, required item 1): `ALLOWED_EXTRA_CATEGORIES`/
# `BLOCKED_EXTRA_CATEGORIES`/`INVALID_MODS` used to be module-level globals here, always the
# default persona's own rules no matter which pack a session was bound to. They are now each session's own bound
# persona's ``extras.allowedBaseCategories``/``blockedBaseCategories``/``invalidModifiers`` data
# (``persona.json``), read off the resolved `MenuCatalog` (see `_menu_for` above /
# `menu.allowed_extra_categories`/`menu.blocked_extra_categories`/`menu.invalid_modifiers`) --
# never a module-level global. See update_order()'s extras check and validate_customization()
# below.


def validate_customization(item_name: str, mods_string: str, prompt_loader=None, menu=None) -> str | None:
    """Return an error message if the mods are nonsensical for the item category, else None.

    *prompt_loader*/*menu* (#74): the caller's resolved per-session persona context, if any;
    both default to the deployment default (``_prompt_loader``, ``default_persona`` catalog)
    when omitted, so a direct call with no session in hand still classifies against a real,
    fully-loaded persona catalog -- never a single-brand-only module shortcut."""
    prompt_loader = prompt_loader if prompt_loader is not None else _prompt_loader
    menu = menu or default_persona.get_default_menu_catalog()
    # PR #50 review (third round, minor): reuse the one shared strip_modifiers() helper instead of
    # a second, independent ad-hoc `.split("(")[0]` implementation of the same paren-stripping rule
    # (menu_utils.py's classification functions and order_state.py's combo-conversion logic already
    # route through it).
    base_name = strip_modifiers(item_name)
    category = menu.infer_category(base_name)
    mods_lower = mods_string.lower()
    for cat_key, forbidden_list in menu.invalid_modifiers.items():
        if cat_key in category.lower():
            for forbidden in forbidden_list:
                if forbidden in mods_lower:
                    if prompt_loader:
                        return prompt_loader.render_error("invalid_mod", forbidden_item=forbidden, base_name=base_name)
                    return f"I can't add {forbidden} to a {base_name} — that's a new one! Want to try a different topping?"
    return None


def _format_size_human_readable(size: str, menu=None) -> str:
    """Convert size codes to human-readable format using *menu*'s (or the deployment default
    persona's) size map."""
    menu = menu or default_persona.get_default_menu_catalog()
    result = menu.normalize_size(size)
    return result if result else size.capitalize()



search_tool_schema = {
    "type": "function",
    "name": "search",
    "description": "Search the knowledge base. The knowledge base is in English, translate to and from English if " + \
                   "needed. Results are formatted as a source name first in square brackets, followed by the text " + \
                   "content, and a line with '-----' at the end of each result.",
    "parameters": {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "Search query"
            }
        },
        "required": ["query"],
        "additionalProperties": False
    }
}

async def search(
    search_client: SearchClient,
    semantic_configuration: str,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    use_vector_query: bool,
    args: Any,
    use_semantic_ranker: bool = True,
    *,
    menu=None,
    prompt_loader=None,
    persona_id: str | None = None,
    menu_mode: str | None = None,
) -> ToolResult:
    """Execute a hybrid Azure AI Search query with caching and safe fallbacks.

    *menu*/*prompt_loader*/*persona_id* (#74): the caller's resolved per-session persona
    context, if any. All three default to the deployment default (the ``default_persona``
    catalog, ``_prompt_loader``, and an unnamespaced cache) when omitted, so every existing
    direct call (e.g. in tests) keeps behaving exactly as before; ``_search_dispatch`` (used
    by the registered "search" tool) is the only caller that passes them.

    *menu_mode* (#165): this session's own bound daypart (``order_state.OrderState
    .get_menu_mode``), or ``None`` for a persona with no ``features.dayparts`` (every existing
    direct call keeps passing nothing, so this is a pure no-op addition). When set, results are
    restricted server-side to items whose own ``menuPeriod`` is ``menu_mode``, ``"allDay"``, or
    unset (``""`` -- setup_search_index.py's own sentinel for an item with no declared daypart at
    all) -- the same OData filter ``setup_search_index.py`` documents. Rick's PR 166 round-1
    review, required item 6: this third clause is what keeps ``item_available_now``'s own
    always-available treatment of a period-less item (below) and this search filter in sync --
    without it, a period-less item in a `features.dayparts` pack could be added to an order in
    either mode yet never surface in a mode-filtered search, a latent split this repo's only
    dayparts pack never exercised because every one of its real items happens to carry a
    ``menuPeriod``. If the search index hasn't been rebuilt with the ``menuPeriod`` field yet, the
    same "unknown property" fallback below drops the filter (as well as the full ``$select``)
    rather than failing the lookup outright.
    """
    menu = menu or default_persona.get_default_menu_catalog()
    prompt_loader = prompt_loader if prompt_loader is not None else _prompt_loader
    mode_filter = (
        f"menuPeriod eq '{menu_mode}' or menuPeriod eq 'allDay' or menuPeriod eq ''" if menu_mode else None
    )

    query = args["query"]
    # #77 (`strategies.searchQueryRewrite: "meal_numbers"`, design doc section 3.3 row 23): this
    # persona's own named extension point -- a no-op for every persona that doesn't opt into it.
    # Applied to `query` itself (not a separate variable) so the cache key, the vector query, and
    # every `search_text=query` call below all see the same, already-rewritten text -- one
    # rewrite, not three copies of it.
    query = menu.rewrite_search_query(query)
    logger.info("Knowledge search requested for query '%s'", query)

    # Check cache first — repeated questions about the same menu item are common. Namespaced
    # by persona_id so two personas asking the same question never share a cached result from
    # each other's (potentially different) search index (#74).
    cache_key = f"{persona_id or ''}::{menu_mode or ''}::{query.strip().lower()}"
    cached = _search_cache.get(cache_key)
    if cached is not None:
        logger.debug("Search cache hit for '%s'", query)
        return cached

    vector_queries = []
    if use_vector_query and embedding_field:
        vector_queries.append(VectorizableTextQuery(text=query, k_nearest_neighbors=_search_cfg.get("k_nearest_neighbors", 15), fields=embedding_field))

    # Only request fields we actually format into the result string
    select_fields = [
        identifier_field or "id",
        "name",
        "category",
        "description",
        "sizes",
    ]

    _top = _search_cfg.get("top_results", 3)

    # The semantic ranker is a service-level capability and is unavailable on the
    # free search SKU. Issuing query_type="semantic" against a service without it
    # returns HTTP 400 rather than degrading, which would fail every menu lookup,
    # so only ask for it when the deployment actually provides it.
    def _query_kwargs(semantic: bool) -> dict[str, Any]:
        kwargs: dict[str, Any] = {"query_type": "semantic"} if semantic else {}
        if semantic:
            kwargs["semantic_configuration_name"] = semantic_configuration
        return kwargs

    semantic_enabled = bool(use_semantic_ranker and semantic_configuration)

    # #37: azure-search-documents' async SearchClient.search(...) is lazy -- it returns an
    # async-iterable immediately without making any HTTP request. The request (and therefore any
    # HttpResponseError, including the "Could not find a property named" 400 the fallback below
    # exists to catch) only happens once the results are actually iterated. So the first-page
    # fetch has to live INSIDE the try, not just the initial `search_client.search(...)` call --
    # otherwise a field-mismatch 400 raised during iteration propagates unhandled and tears down
    # the whole realtime connection instead of triggering the minimal-select retry.
    #
    # PR #50 review (should-fix 4): `asyncio.wait_for` must wrap the ENTIRE collect -- the
    # `await search_client.search(...)` call AND the `async for` iteration that triggers the real
    # HTTP request -- not just the (non-blocking, no-HTTP-yet) initial call. Wrapping only the
    # `await search_client.search(...)` bounded nothing useful, since that call does no network
    # I/O per the comment above; the iteration below it (where the request actually happens) ran
    # completely outside the timeout, so a slow/hanging search service could block indefinitely
    # despite `timeout_seconds` being configured.
    async def _fetch_records(**search_kwargs) -> list[dict]:
        async def _search_and_collect() -> list[dict]:
            search_results = await search_client.search(**search_kwargs)
            return [record async for record in search_results]

        return await asyncio.wait_for(_search_and_collect(), timeout=_search_cfg.get("timeout_seconds", 10))

    try:
        records = await _fetch_records(
            search_text=query,
            top=_top,
            vector_queries=vector_queries or None,
            select=select_fields,
            filter=mode_filter,
            **_query_kwargs(semantic_enabled),
        )
    except TimeoutError:
        logger.error("Azure AI Search timed out for query '%s'", query)
        _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I'm having trouble reaching our menu right now — could you try that again?"
        return ToolResult(_err, ToolResultDirection.TO_SERVER)
    except HttpResponseError as exc:
        # Gracefully handle schema/field mismatches (e.g., invalid $select fields, or -- #165 --
        # a `menuPeriod` filter against an index that hasn't been rebuilt with that field yet) by
        # retrying with a minimal projection AND no filter. Dropping the mode filter here means a
        # stale index degrades to "unfiltered search" rather than failing the lookup outright.
        if "Could not find a property named" in str(exc):
            logger.warning("Retrying search with minimal fields after select mismatch: %s", exc)
            fallback_select = [identifier_field or "id", content_field or "description"]
            try:
                records = await _fetch_records(
                    search_text=query,
                    top=_top,
                    vector_queries=vector_queries or None,
                    select=[f for f in fallback_select if f],
                    **_query_kwargs(semantic_enabled),
                )
            except Exception as exc2:
                logger.error("Search retry with minimal select also failed: %s", exc2)
                _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I'm sorry, I can't reach our menu data right now."
                return ToolResult(_err, ToolResultDirection.TO_SERVER)
        elif semantic_enabled and "semantic" in str(exc).lower():
            # Belt and braces: the service rejected the semantic query even though
            # configuration said it was available (e.g. the SKU was changed after
            # deployment). Retry without the ranker rather than failing the lookup.
            logger.warning("Semantic ranker unavailable, retrying without it: %s", exc)
            try:
                records = await _fetch_records(
                    search_text=query,
                    top=_top,
                    vector_queries=vector_queries or None,
                    select=select_fields,
                    filter=mode_filter,
                )
            except Exception as exc2:
                logger.error("Search retry without semantic ranker also failed: %s", exc2)
                _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I'm sorry, I can't reach our menu data right now."
                return ToolResult(_err, ToolResultDirection.TO_SERVER)
        else:
            logger.error("Azure AI Search request failed: %s", exc)
            _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I'm sorry, I can't reach our menu data right now."
            return ToolResult(_err, ToolResultDirection.TO_SERVER)
    except Exception as exc:
        logger.error("Unexpected error during search for '%s': %s", query, exc)
        _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I had a little glitch looking that up — could you say that again?"
        return ToolResult(_err, ToolResultDirection.TO_SERVER)

    results = []
    for record in records:
        identifier = record.get(identifier_field) or record.get("id", "unknown")

        # Format sizes into human-readable list so the Realtime API can speak them naturally
        raw_sizes = record.get('sizes', 'N/A')
        try:
            sizes_json = json.loads(raw_sizes)
            size_str = ", ".join([f"{_format_size_human_readable(s['size'], menu=menu)} (${s['price']})" for s in sizes_json])
        except Exception:
            size_str = raw_sizes

        item_name = record.get('name', 'N/A')
        # #313 (Rick's review, 1.1): every persona prompt requires calling `search` BEFORE
        # `update_order`, so this is the model's first (and often only) exposure to the item's
        # name -- it previously saw only the raw catalog string (e.g. a trademarked "WIDGET®")
        # and had to improvise a pronunciation. The canonical name stays first (it
        # is what the model must still pass back to `update_order`); the spoken form is appended
        # so the model has a correct pronunciation to actually say out loud.
        spoken_name = menu.spoken(item_name) if menu else item_name
        name_for_speech = f"{item_name} (say: {spoken_name})" if spoken_name != item_name else item_name
        summary = (
            f"[{identifier}]: "
            f"Item: {name_for_speech}, Category: {record.get('category', 'N/A')}, "
            f"Available Sizes: {size_str}"
        )

        # Flag items affected by machine outages so the AI knows not to recommend them.
        # #73: data-driven off the item's own `requiresMachine` field instead of a substring
        # keyword list, so a real menu item is the only thing ever flagged.
        machine = menu.requires_machine(item_name)
        if machine and menu.machine_status(machine) == "down":
            summary += f" [OOS: {menu.machine_label(machine)}]"

        results.append(summary)

    joined_results = "\n-----\n".join(results)
    logger.debug("Search results returned %d documents", len(results))
    _no_results = prompt_loader.get_error_messages().get("search_no_results", "No matching menu entries found.") if prompt_loader else "No matching menu entries found."
    result = ToolResult(joined_results or _no_results, ToolResultDirection.TO_SERVER)

    # Cache the result for repeated queries
    _search_cache.put(cache_key, result)
    return result


update_order_tool_schema = {
    "type": "function",
    "name": "update_order",
    "description": "Update the current order by adding, removing, or resizing items.",
    "parameters": {
        "type": "object",
        "properties": {
            "action": { 
                "type": "string", 
                "description": (
                    "Action to perform: 'add', 'remove', or 'modify' (change an existing item's "
                    "size in place, e.g. resizing a meal/combo from Medium to Large -- only offer "
                    "'modify' if this persona's own tool instructions mention it)."
                ),
                "enum": ["add", "remove", "modify"]
            },
            "item_name": { 
                "type": "string", 
                "description": "Name of the item to update, e.g., 'Cherry Limeade'."
            },
            "size": { 
                "type": "string", 
                "description": "Size of the item to update, e.g., 'Large'. For 'modify', this is the NEW size."
            },
            "quantity": { 
                "type": "integer", 
                "description": "Quantity of the item to update. Represents the number of items."
            },
            "price": { 
                "type": "number", 
                "description": "Ignored; the server prices from the menu."
            }
        },
        "required": ["action", "item_name", "size", "quantity"],
        "additionalProperties": False
    }
}

async def update_order(args, session_id: str) -> ToolResult:
    """Update the current order by adding or removing items."""

    logger.info("Updating order for session %s with payload %s", session_id, args)

    # #74: this session's own bound persona menu/prompt-loader context (or the shared
    # module-level defaults for an unbound session -- unchanged behavior for the
    # single-persona-deployment default path).
    menu = _menu_for(session_id)
    pl = _prompt_loader_for(session_id)

    # ── #36: validate required args up front instead of letting a bare
    # args["..."] raise an unhandled KeyError deep inside this handler. Before this
    # check, a malformed/incomplete tool call (e.g. missing "item_name") propagated a
    # raw KeyError all the way up through rtmt.py's response.output_item.done dispatch,
    # tearing down the guest's whole WebSocket connection instead of giving the model a
    # graceful, recoverable error. ──
    required = ("action", "item_name", "size", "quantity")
    missing = [k for k in required if k not in args]
    if missing:
        logger.warning("update_order called with missing required argument(s) %s (session=%s)", missing, session_id)
        _err = pl.render_error("tool_execution_failed") if pl else (
            "I'm sorry, something went wrong with that. Could you try again?"
        )
        return ToolResult(_err, ToolResultDirection.TO_SERVER)

    item_name = args["item_name"]
    size = args["size"]

    # ── #73 (ADR-001 decision 4: "No off-menu"): the on-menu gate, first thing in the add path,
    # before any other validation (customization, price, extras, quantity limits). An item is
    # on the menu iff its normalized name or one of its exact-match aliases resolves against the
    # persona's own menu data (menu.resolve_menu_item) -- never a keyword/substring guess.
    # Anything else is rejected outright as not_on_menu; nothing is added to the order, and the
    # model is told so (via error_messages.yaml's item_not_on_menu) so the carhop can offer an
    # on-menu alternative instead of silently accepting or absorbing it.
    #
    # Rick's PR #100 review, required item 1: both rejections below return a STRUCTURED JSON
    # result (TO_SERVER-only, exactly like every other add-time rejection direction-wise), not a
    # bare apology string, so the C# port can match this contract field-for-field:
    #   {"status": "rejected", "item_added": false, "reason": <"not_on_menu"|"size_not_available">,
    #    "item_name": ..., "message": <error_messages.yaml text>}
    #   (+ "available_sizes": [...] for size_not_available.)
    # ToolResult.to_text() already json.dumps()s a non-str `text` payload (rtmt.py), so passing a
    # dict here is exactly what every OTHER JSON-carrying ToolResult in this module does. ──
    if args["action"] in ("add", "modify"):
        menu_item = menu.resolve_menu_item(item_name)
        if menu_item is None:
            logger.info("Rejected off-menu item '%s' for session %s (not_on_menu)", item_name, session_id)
            _message = pl.render_error("item_not_on_menu", item_name=item_name) if pl else (
                f"I'm sorry, {item_name} isn't on our menu. Would you like to try something else instead? "
                "Use the search tool with the guest's words and offer the closest real menu item by its exact name."
            )
            _rejection = {
                "status": "rejected",
                "item_added": False,
                "reason": "not_on_menu",
                "item_name": item_name,
                "message": _message,
            }
            # #77 (shared extras engine, `extras.splitCombinedNames`): a combined name like
            # "Caramel Latte with Extra Shot" is really a known base item plus a known extra --
            # never on the menu as one single item, but the guest's intent is unambiguous. Offer
            # the model two real `add` calls instead of a flat "isn't on our menu" dead end.
            split = menu.try_split_combined_name(item_name)
            if split is not None:
                base_name, extra_name = split
                _rejection["suggested_calls"] = [
                    {"action": "add", "item_name": base_name},
                    {"action": "add", "item_name": extra_name},
                ]
                logger.info(
                    "Split combined name '%s' -> '%s' + '%s' for session %s",
                    item_name, base_name, extra_name, session_id,
                )
            return ToolResult(_rejection, ToolResultDirection.TO_SERVER)

        # #165: this session's own bound menu mode gate -- an item that's real and on the menu,
        # but not offered in the active daypart (e.g. a breakfast-only item add while the session
        # is bound to "lunch"). Runs for "add" only: a "modify" target is already IN the order,
        # which means it passed this same gate at add time and the mode never changes
        # mid-session (#165 design: no mid-conversation `?mode=` switching, exactly like
        # persona/model -- order_state.OrderState.create_session), so gating it again here would
        # be a no-op at best and a spurious reject at worst. A persona with no
        # `features.dayparts` always resolves `menu_mode=None`, and `item_available_now` is a
        # no-op (returns True) for `active_mode=None` -- so this block never rejects anything for
        # those packs.
        menu_mode = order_state_singleton.get_menu_mode(session_id)
        if args["action"] == "add" and not menu.item_available_now(item_name, menu_mode):
            logger.info(
                "Rejected out-of-mode item '%s' for session %s (item_out_of_mode; active_mode=%s, item_period=%s)",
                item_name, session_id, menu_mode, menu_item.get("menuPeriod"),
            )
            _message = pl.render_error(
                "item_out_of_mode", item_name=menu_item["name"], mode_label=menu_item.get("menuPeriod") or ""
            ) if pl else (
                f"I'm sorry, {menu_item['name']} isn't on our menu right now. Would you like to try something else instead?"
            )
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": "item_out_of_mode",
                    "item_name": menu_item["name"],
                    "message": _message,
                },
                ToolResultDirection.TO_SERVER,
            )

        requested_size = menu.canonical_size_key(size)
        if requested_size not in menu_item["sizes"] and requested_size in ("", "standard"):
            # PR #184 round 4 (Rick's review, item 4): a bundle meal ordered with no size at all,
            # or a bare "standard"/"regular" ask, on a pack whose own `sizes` list is S/M/L ONLY
            # (no "standard" tier -- e.g. a numbered-meal pack with S/M/L sizing) isn't actually
            # an invalid size; it's the guest not naming one. Map it onto the bundle's own
            # configured `bundle.defaultSize` (the original app's `_get_default_side` default)
            # instead of rejecting a perfectly normal numbered-meal order. An item
            # with NO bundle data, or no configured default size, falls through unchanged to the
            # rejection below exactly as before; so does any OTHER explicitly-named size that
            # the pack doesn't price (e.g. an explicit "large" on a true Standard-only item).
            default_size = menu.bundle_default_size(item_name)
            default_key = menu.canonical_size_key(default_size) if default_size else ""
            if default_key and default_key in menu_item["sizes"]:
                logger.info(
                    "Defaulted missing/standard size for bundle '%s' to '%s' in session %s",
                    item_name, default_key, session_id,
                )
                requested_size = default_key
                size = default_size
        if requested_size not in menu_item["sizes"]:
            size_map = menu.size_map
            available_sizes = [size_map.get(s, s.capitalize()) for s in menu_item["sizes"]]
            logger.info(
                "Rejected unsupported size '%s' for '%s' in session %s (size_not_available); available: %s",
                size, item_name, session_id, menu_item["sizes"],
            )
            _message = pl.render_error(
                "size_not_available", item_name=menu_item["name"], available_sizes=", ".join(available_sizes)
            ) if pl else (
                f"I'm sorry, {menu_item['name']} isn't available in that size. "
                f"We have it in {', '.join(available_sizes)} -- would you like one of those?"
            )
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": "size_not_available",
                    "item_name": menu_item["name"],
                    "message": _message,
                    "available_sizes": available_sizes,
                },
                ToolResultDirection.TO_SERVER,
            )

        # #77: `modify` resizes an existing line, so an on-menu item that isn't in the order has
        # nothing to resize. Reject it with the same structured shape instead of letting the
        # success delta tell the guest it was changed (docs/persona-architecture.md section 6).
        # Same line-matching rule as order_state.handle_order_update's modify branch.
        # #179: a combo's side/drink filling a slot via absorption is ALSO a real, resizable part
        # of the order even though it has no raw ``OrderItem`` line of its own -- the guest
        # saying "make that a large" about the drink that came with their combo.
        # ``is_absorbed_component`` is the second chance before this rejects it.
        if args["action"] == "modify" and not any(
            order_item.item == item_name for order_item in order_state_singleton.get_order_items(session_id)
        ) and not order_state_singleton.is_absorbed_component(session_id, item_name):
            logger.info("Rejected modify of '%s' for session %s (not_in_order)", item_name, session_id)
            _message = pl.render_error("item_not_in_order", item_name=menu_item["name"]) if pl else (
                f"{menu_item['name']} isn't in the order, so nothing was changed. "
                "Ask the guest whether they'd like to add it."
            )
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": "not_in_order",
                    "item_name": menu_item["name"],
                    "message": _message,
                },
                ToolResultDirection.TO_SERVER,
            )

    # ── #77: add-time `machine_unavailable` structured rejection -- an on-menu item that
    # `requiresMachine` a machine this persona's OWN `machines.<key>.status` currently reports
    # "down". Same TO_SERVER structured-JSON shape as not_on_menu/size_not_available above (Rick's
    # PR #100 review, required item 1; docs/persona-architecture.md section 6), so nothing is
    # silently added while a real machine outage is in effect. `modify` never reaches this check
    # -- resizing an item already in the order doesn't newly require the machine it already
    # required when it was added.
    if args["action"] == "add":
        machine = menu.requires_machine(item_name)
        if machine and menu.machine_status(machine) == "down":
            machine_label = menu.machine_label(machine)
            logger.info(
                "Rejected '%s' for session %s (machine_unavailable: %s)", item_name, session_id, machine,
            )
            _message = pl.render_error(
                "machine_unavailable", item_name=item_name, machine_label=machine_label
            ) if pl else (
                f"I'm sorry, {item_name} isn't available right now -- {machine_label}. "
                "Would you like to try something else instead?"
            )
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": "machine_unavailable",
                    "item_name": item_name,
                    "message": _message,
                },
                ToolResultDirection.TO_SERVER,
            )

    # ── Customization validation (reject nonsensical mods) ──
    if "(" in item_name:
        mods_content = item_name[item_name.find("(")+1:item_name.find(")")]
        error = validate_customization(item_name, mods_content, prompt_loader=pl, menu=menu)
        if error:
            return ToolResult(error, ToolResultDirection.TO_SERVER)

    # ── #104: the tool call's own `price` is no longer validated or trusted here. The unit
    # price a guest is charged always comes from the resolved menu item's own per-size price
    # (menu_utils.MenuCatalog.price_for, applied in order_state.handle_order_update -- the single
    # source of truth for both this realtime path and any direct caller). A model-supplied price
    # of $0, a negative number, or an arbitrary/stale value can no longer zero out or under/over-
    # charge a real menu item; it is only ever logged (debug) when it disagrees with the menu
    # price. See docs/persona-architecture.md section 6.

    if args["action"] == "add" and menu.is_extra_item(item_name):
        current_items = order_state_singleton.get_order_items(session_id)
        has_allowed_base = False
        has_blocked_base = False

        for order_item in current_items:
            category = menu.infer_category(order_item.item)
            if category in menu.allowed_extra_categories:
                has_allowed_base = True
            if category in menu.blocked_extra_categories:
                has_blocked_base = True

        if not has_allowed_base:
            # #77 (shared extras engine, Rick's PR #100 review pattern reused): a STRUCTURED
            # (TO_SERVER) rejection, matching not_on_menu/size_not_available/machine_unavailable
            # field-for-field -- `reason` is "extras_blocked_category" (a real base item is
            # present, but its category is explicitly blocked) or "extras_no_base_item" (no
            # allowed base item at all yet). This used to be a bare apology string; every OTHER
            # add-time rejection in this module is already this shape, and #14 (C# port) needs one
            # contract, not two.
            if has_blocked_base:
                _reason = "extras_blocked_category"
                _message = pl.render_error("extras_blocked_category") if pl else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but I can't add them to sides or hot dogs on their own."
                )
            else:
                _reason = "extras_no_base_item"
                _message = pl.render_error("extras_no_base_item") if pl else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but not to sides or hot dogs on their own."
                )
            logger.info("Blocked extra '%s' for session %s (%s)", item_name, session_id, _reason)
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": _reason,
                    "item_name": item_name,
                    "message": _message,
                },
                ToolResultDirection.TO_SERVER,
            )

    # ── Quantity limit validation (add only) ──
    quantity = args.get("quantity", 0)
    if args["action"] == "add":
        current_items = order_state_singleton.get_order_items(session_id)

        # Per-item limit: check resulting quantity for this item+size combo
        existing_qty = 0
        for order_item in current_items:
            if order_item.item == item_name and order_item.size == size:
                existing_qty = order_item.quantity
                break
        new_item_qty = existing_qty + quantity
        if new_item_qty > MAX_QUANTITY_PER_ITEM:
            allowed = MAX_QUANTITY_PER_ITEM - existing_qty
            if allowed <= 0:
                if pl:
                    msg = pl.render_error("per_item_limit_maxed", item_name=item_name, max_per_item=MAX_QUANTITY_PER_ITEM, existing_qty=existing_qty)
                else:
                    msg = (
                        f"That's a lot of {item_name}! Our drive-thru can handle up to "
                        f"{MAX_QUANTITY_PER_ITEM} of any item. You already have {existing_qty} — "
                        f"would you like to keep it at {existing_qty}?"
                    )
            else:
                if pl:
                    msg = pl.render_error("per_item_limit_partial", item_name=item_name, max_per_item=MAX_QUANTITY_PER_ITEM, allowed=allowed)
                else:
                    msg = (
                        f"That's a lot of {item_name}! Our drive-thru can handle up to "
                        f"{MAX_QUANTITY_PER_ITEM} of any item. I can add {allowed} more — "
                        f"would you like me to do that?"
                    )
            logger.info("Per-item limit hit for '%s' in session %s (requested %d, existing %d)",
                        item_name, session_id, quantity, existing_qty)
            return ToolResult(msg, ToolResultDirection.TO_SERVER)

        # Total order limit: check total items across the whole order
        total_qty = sum(oi.quantity for oi in current_items) + quantity
        if total_qty > MAX_TOTAL_ITEMS:
            remaining = MAX_TOTAL_ITEMS - sum(oi.quantity for oi in current_items)
            if remaining <= 0:
                if pl:
                    msg = pl.render_error("total_order_limit_maxed", max_total=MAX_TOTAL_ITEMS)
                else:
                    msg = (
                        f"Wow, that's a big order! Our drive-thru tops out at "
                        f"{MAX_TOTAL_ITEMS} items total so we can keep things moving. "
                        f"You're already at the max — would you like to swap anything out?"
                    )
            else:
                if pl:
                    msg = pl.render_error("total_order_limit_partial", max_total=MAX_TOTAL_ITEMS, remaining=remaining)
                else:
                    msg = (
                        f"Wow, that's a big order! Our drive-thru tops out at "
                        f"{MAX_TOTAL_ITEMS} items total so we can keep things moving. "
                        f"I can add {remaining} more — would you like me to do that?"
                    )
            logger.info("Total order limit hit in session %s (would be %d items)", session_id, total_qty)
            return ToolResult(msg, ToolResultDirection.TO_SERVER)

    result_info = order_state_singleton.handle_order_update(
        session_id,
        args["action"],
        item_name,
        size,
        quantity,
        args.get("price", 0.0),
    )

    # PR #184 round 4 (Rick's review, item 3): a "wholeBundleSize" pack's `combo_component_
    # resize_rejected` flag means NOTHING was mutated (order_state.OrderState._fill_bundle_
    # component rejected the resize outright because this pack has no whole-meal price at the
    # requested size) -- read it BEFORE any of the success-delta branches below so this returns a
    # structured (TO_SERVER) rejection, same shape as not_on_menu/size_not_available, instead of
    # falling through to the generic `modify` branch's "Changed ..., your total is now ..."
    # wording, which used to tell the guest a change happened when the order was left untouched.
    rejected_component = result_info.get("combo_component_resize_rejected") if result_info else None
    if rejected_component:
        bundle_name = result_info.get("combo_component_resize_rejected_bundle", "") if result_info else ""
        bundle_size = result_info.get("combo_component_resize_rejected_bundle_size", "") if result_info else ""
        bundle_size_label = bundle_size.capitalize() if bundle_size and bundle_size.lower() != "standard" else ""
        logger.info(
            "Rejected combo %s resize of '%s' for session %s (combo_component_resize_rejected; "
            "bundle=%s)", rejected_component, item_name, session_id, bundle_name,
        )
        _message = pl.render_error(
            "combo_component_resize_rejected", item_name=item_name, bundle_name=bundle_name,
            bundle_size_label=bundle_size_label,
        ) if pl else (
            f"I'm sorry, {item_name} comes with the {bundle_name} at its own size, so it can't be "
            "resized by itself. Would you like to make the whole meal that size instead?"
        )
        return ToolResult(
            {
                "status": "rejected",
                "item_added": False,
                "reason": "combo_component_resize_rejected",
                "item_name": item_name,
                "message": _message,
            },
            ToolResultDirection.TO_SERVER,
        )

    json_order_summary = order_state_singleton.get_order_summary_json(session_id)
    summary = order_state_singleton.get_order_summary(session_id)
    logger.debug("Session %s order summary after update: %s", session_id, json_order_summary)

    # ── Delta text for voice confirmation ──
    action = args["action"]
    display_size = size if size and size.lower() not in {"", "standard", "n/a", "na", "none", "n.a."} else ""
    display_name = f"{display_size.capitalize() + ' ' if display_size else ''}{item_name}"
    spoken_item_name = menu.spoken(item_name)
    spoken_display_name = menu.spoken(display_name)

    absorbed = result_info.get("absorbed_into_combo", False) if result_info else False
    converted_from = result_info.get("combo_converted_from") if result_info else None
    # #179: set by order_state.handle_order_update whenever a combo's side/drink slot was
    # (re)sized in place -- via an `add` of the same item at a different size while the slot was
    # already full, or an explicit `modify` targeting an absorbed component. Checked before the
    # generic action-keyed branches below so both paths confirm the resize, never a duplicate-add
    # or a rejected-modify message.
    resized_component = result_info.get("resized_combo_component") if result_info else None
    component_upcharge_display = result_info.get("combo_component_upcharge_display") if result_info else None
    # PR #184 round 3 (Rick's review, item C): set by order_state.handle_order_update whenever
    # `modify` actually changed an existing order line's OWN size (a bare resize, "wholeBundleSize"
    # or not) -- distinct from resized_component above, which is a combo's SIDE/DRINK slot
    # resizing in place. Matches the original app's exact wording/verb for this case.
    modified_from_size = result_info.get("modified_from_size") if result_info else None
    modified_to_size = result_info.get("modified_to_size") if result_info else None
    # #179: handle_order_update's `remove` branch also sets "vacated_combo_component" when
    # *item_name* was vacating a combo slot rather than removing a raw order line -- the
    # existing generic "Removed ..." wording below is already accurate for that case (the item
    # IS being removed from the guest's perspective), so no separate branch reads it here.

    if absorbed:
        if component_upcharge_display:
            delta_text = f"{spoken_display_name} included with your combo with a {component_upcharge_display} upcharge — your total is {summary.finalTotalDisplay}"
        else:
            delta_text = f"{spoken_display_name} included with your combo — your total is {summary.finalTotalDisplay}"
    elif converted_from and action == "add":
        combo_display = spoken_display_name
        mods = result_info.get("mods_carried", "")
        if mods:
            combo_display = f"{spoken_display_name} {mods}"
        delta_text = f"Upgraded to {combo_display} — your total is now {summary.finalTotalDisplay}"
    elif resized_component:
        if component_upcharge_display:
            delta_text = f"Changed {spoken_display_name} with a {component_upcharge_display} upcharge, your total is now {summary.finalTotalDisplay}"
        else:
            delta_text = f"Changed {spoken_display_name}, your total is now {summary.finalTotalDisplay}"
    elif modified_from_size and modified_to_size and modified_from_size != modified_to_size:
        old_label = modified_from_size.capitalize()
        new_label = modified_to_size.capitalize()
        # #313 (Rick's review, 1.7): "Upgraded" implies the new size is always bigger, but a
        # resize can go either way (Brian's bug report: 25 count -> 10 count). Use the neutral
        # "Changed" for every size change, same verb already used by the resized_component and
        # `modify` action branches above/below.
        delta_text = f"Changed {spoken_item_name} from {old_label} to {new_label}, your total is now {summary.finalTotalDisplay}"
    elif pl:
        tpl = pl.get_delta_template(action)
        delta_text = pl.render_template(tpl, quantity=quantity, display_name=spoken_display_name, total=summary.finalTotalDisplay)
    elif action == "add":
        delta_text = f"Added {quantity} {spoken_display_name} — your total is now {summary.finalTotalDisplay}"
    elif action == "modify":
        delta_text = f"Changed {spoken_display_name} — your total is now {summary.finalTotalDisplay}"
    else:
        delta_text = f"Removed {quantity} {spoken_display_name} — your total is now {summary.finalTotalDisplay}"

    # ── Combo validation: flag missing components ──
    validation = order_state_singleton.get_combo_requirements(session_id)

    if not validation["is_complete"]:
        delta_text += f"\n\n[SYSTEM HINT: {validation['prompt_hint']}]"
        logger.info("Combo incomplete for session %s — missing: %s", session_id, validation["missing_items"])
    elif action == "add" and not absorbed:
        # ── Category-aware upsell hints (only when combo requirements are met) ──
        # #168 follow-up (Rick's PR #217 review): an "extra" (Flavor Add-In, Add Bacon,
        # Whipped Topping, Sweet Cream, Jalapeños, etc.) shares its base item's own category
        # (e.g. this persona's own "Extras & Sides", alongside genuine stand-alone sides like
        # Cheese Tots) but is its own order line, not a side the guest is choosing instead of/along
        # with a combo -- a category-specific hint written for that category's REAL items
        # (e.g. "add a refreshing Drink or Slush to complete your meal!") reads as non-
        # sequitur stacked right after the item it modifies. Pass "" instead of the real
        # category so get_upsell_hint/GetUpsellHint always miss every bucket and fall through
        # to "generic", matching exactly what an extra got before hints.yaml's "extras & sides"
        # mapping was added in #168 (back then "extras & sides" didn't match ANY bucket either).
        category = "" if menu.is_extra_item(item_name) else menu.infer_category(item_name)
        if pl:
            # #313 (Rick's review, 1.2): the upsell hint text (e.g. hints.yaml's "maybe a coffee,
            # a Widget, or a donut!") is guest-facing speech, same as every other string appended
            # to delta_text -- it must go through the same menu.spoken() pronunciation lexicon or
            # the model reads the raw brand string verbatim right after a correctly-spoken item name.
            delta_text += menu.spoken(pl.get_upsell_hint(category))
        logger.debug("Upsell hint for category '%s'", category)

    # #113: the banner text (and whether to announce at all) is this session's OWN bound
    # persona's `pricing.happyHour.banner`/`announce` -- never a hardcoded string here. See
    # order_state.OrderState.get_happy_hour_banner_for_session for the single place that's
    # decided (mirrors is_happy_hour_for_session's per-session lookup just above it).
    happy_hour_note = order_state_singleton.get_happy_hour_banner_for_session(session_id)
    # #313 (Rick's review, 1.4): Brian's bug was that the read-back after a *modify* never
    # reached the model unless it made a second `get_order` call a prompt told it to -- the same
    # model behavior that dropped the read-back in the original #304 report. Appending the
    # already-composed, server-side `spokenReadBack` to EVERY successful add/remove/modify
    # `function_call_output` means the read-back is in the model's context the instant it has to
    # speak, with no second tool call required. `summary` here is this same call's freshly
    # recomputed `OrderSummary` (see `get_order_summary` above), so it always reflects the change
    # that was just made.
    spoken_read_back = summary.spokenReadBack
    delta_text_with_readback = f"{delta_text}{happy_hour_note}\n\n{spoken_read_back}" if spoken_read_back else delta_text + happy_hour_note
    return ToolResult(delta_text_with_readback, ToolResultDirection.TO_BOTH, client_text=json_order_summary)


get_order_tool_schema = {
    "type": "function",
    "name": "get_order",
    "description": "Retrieve the current order summary.",
    "parameters": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": False
    }
}

async def get_order(_args: Any, session_id: str) -> ToolResult:
    """Retrieve the current order summary."""

    logger.info("Retrieving order summary for session %s", session_id)
    readback = order_state_singleton.get_grouped_order_for_readback(session_id)
    json_summary = order_state_singleton.get_order_summary_json(session_id)
    # #113: same pack-sourced lookup as update_order above -- this session's own bound
    # persona's banner/announce switch, never a hardcoded string.
    happy_hour_note = order_state_singleton.get_happy_hour_banner_for_session(session_id)
    return ToolResult(readback + happy_hour_note, ToolResultDirection.TO_BOTH, client_text=json_summary)


reset_order_tool_schema = {
    "type": "function",
    "name": "reset_order",
    "description": "Clear all items from the current order and start fresh.",
    "parameters": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": False
    }
}

async def reset_order(_args: Any, session_id: str) -> ToolResult:
    """Clear the entire order ticket."""
    logger.info("Resetting entire order for session %s", session_id)
    order_state_singleton.reset_order(session_id)
    json_summary = order_state_singleton.get_order_summary_json(session_id)
    return ToolResult(f"Order cleared. {json_summary}", ToolResultDirection.TO_BOTH, client_text=json_summary)


async def _search_dispatch(args, session_id: str | None) -> ToolResult:
    """Resolve *this session's* bound persona search client/field-config (or the shared
    deployment-wide default for an unbound session or a persona with no registered override,
    #74) and execute the unchanged ``search()`` above with it.

    This is the function actually registered as ``rtmt.tools["search"].target`` -- it is the
    one place a per-persona ``SearchClient``/index gets selected, keeping ``search()`` itself
    100% backward compatible (same positional signature every existing direct test call uses).
    """
    pid = order_state_singleton.get_persona_id(session_id) if session_id else None
    cfg = (_persona_registry.get(pid) if pid else None) or _default_search_ctx
    menu = _menu_for(session_id)
    pl = _prompt_loader_for(session_id)
    menu_mode = order_state_singleton.get_menu_mode(session_id) if session_id else None
    return await search(
        cfg["search_client"],
        cfg["semantic_configuration"],
        cfg["identifier_field"],
        cfg["content_field"],
        cfg["embedding_field"],
        cfg["use_vector_query"],
        args,
        cfg.get("use_semantic_ranker", True),
        menu=menu,
        prompt_loader=pl,
        persona_id=pid,
        menu_mode=menu_mode,
    )


def attach_tools_rtmt(
    rtmt: RTMiddleTier,
    credentials: AzureKeyCredential | DefaultAzureCredential,
    search_endpoint: str,
    search_index: str,
    semantic_configuration: str,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    title_field: str,
    use_vector_query: bool,
    prompt_loader=None,
    use_semantic_ranker: bool = True,
    *,
    personas: dict[str, dict[str, Any]] | None = None,
) -> None:
    """Attach search and order tools to the RTMiddleTier instance.

    *personas* (#74, optional): ``{persona_id: {"search_client", "semantic_configuration",
    "identifier_field", "content_field", "embedding_field", "use_vector_query",
    "use_semantic_ranker", "prompt_loader"}}`` for a multi-persona deployment -- each bound
    session's search/prompt calls are then routed through its own persona's entry via
    ``_search_dispatch``/``_prompt_loader_for`` instead of the single deployment-wide default
    below. Omitted (the default): identical to today's single-persona behavior."""
    global _prompt_loader
    _prompt_loader = prompt_loader

    # Use tool schemas from YAML if available, else fall back to hardcoded
    if prompt_loader:
        yaml_schemas = prompt_loader.get_tool_schemas()
        schema_map = {s["name"]: s for s in yaml_schemas}
    else:
        schema_map = {}

    if not isinstance(credentials, AzureKeyCredential):
        credentials.get_token("https://search.azure.com/.default")  # warm up prior to first call
    search_client = SearchClient(search_endpoint, search_index, credentials, user_agent="RTMiddleTier")

    _default_search_ctx.clear()
    _default_search_ctx.update({
        "search_client": search_client,
        "semantic_configuration": semantic_configuration,
        "identifier_field": identifier_field,
        "content_field": content_field,
        "embedding_field": embedding_field,
        "use_vector_query": use_vector_query,
        "use_semantic_ranker": use_semantic_ranker,
    })

    _persona_registry.clear()
    if personas:
        _persona_registry.update(personas)

    rtmt.tools["search"] = Tool(schema=schema_map.get("search", search_tool_schema), target=lambda args, session_id: _search_dispatch(args, session_id))
    rtmt.tools["update_order"] = Tool(schema=schema_map.get("update_order", update_order_tool_schema), target=lambda args, session_id: update_order(args, session_id))
    rtmt.tools["get_order"] = Tool(schema=schema_map.get("get_order", get_order_tool_schema), target=lambda args, session_id: get_order(args, session_id))
    rtmt.tools["reset_order"] = Tool(schema=schema_map.get("reset_order", reset_order_tool_schema), target=lambda args, session_id: reset_order(args, session_id))

    # #170 R4 (Rick's PR #175 round-2 review): every registered persona's own tool
    # schemas -- not just the deployment default's above -- so a bound session's
    # session.tools[].description names ITS OWN brand's menu/ticket, never the
    # deployment default persona's (#170, the live bug: every session's tool list used
    # the default persona's descriptions). `Tool.target` above stays shared (the
    # lambdas are identical regardless of persona); only the schema TEXT varies, keyed
    # by persona id here and resolved once per connection by `_forward_messages`
    # (rtmt.py), next to `persona_prompt_loaders`, the same pattern `system_message`
    # already uses. Per-tool-name fallback (a persona pack missing an entry for one of
    # the four tools) is the hardcoded schema, exactly like the deployment default's
    # own `schema_map.get(name, hardcoded)` above.
    rtmt.persona_tool_schemas.clear()
    if personas:
        for persona_id, ctx in personas.items():
            persona_loader = ctx.get("prompt_loader")
            if persona_loader is not None:
                persona_schema_map = {s["name"]: s for s in persona_loader.get_tool_schemas()}
            else:
                persona_schema_map = {}
            rtmt.persona_tool_schemas[persona_id] = [
                persona_schema_map.get("search", search_tool_schema),
                persona_schema_map.get("update_order", update_order_tool_schema),
                persona_schema_map.get("get_order", get_order_tool_schema),
                persona_schema_map.get("reset_order", reset_order_tool_schema),
            ]
