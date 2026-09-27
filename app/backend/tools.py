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
# so nothing here reads a module-level Sonic menu/size/category global. A caller that used to
# import `canonical_size_key`/`normalize_size`/`is_extra_item`/`requires_machine`/
# `resolve_menu_item`/`infer_category`/`SIZE_MAP` straight from this module (or from
# ``menu_utils``) now resolves the exact same methods off a real ``MenuCatalog`` instance instead
# -- ``default_persona.get_default_menu_catalog()`` for the deployment default, or
# ``order_state_singleton.get_menu_catalog(session_id)`` for a specific session's own bound
# persona (see ``_menu_for`` below). Tests construct their fixture the same way (see
# tests/test_tool_calling.py's/test_tools_search.py's ``_SONIC`` fixture).
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
# Sonic-specific global -- every persona ultimately gets a real, non-None prompt_loader, either
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
# Mock "Store Telemetry" - In production, this would be an Azure Function / IoT Hub call
# ---------------------------------------------------------------------------
MOCK_MACHINE_STATUS = {
    "ice_cream_machine": "down",  # Classic "shake machine is broken" scenario
    "slush_machine": "operational",
    "fryer": "operational",
}

# #73 (Rick's PR review): the old OOS check (`_ICE_CREAM_MACHINE_KEYWORDS`, a substring list) and
# the old extras check (`EXTRAS_KEYWORDS`, also a substring list) both risked matching names that
# aren't real menu items at all. Both are now data-driven off each item's own `requiresMachine`/
# `isExtra` fields (menu_utils.requires_machine() / menu_utils.is_extra_item()), read once at
# import time from the pack. See search()'s OOS annotation and update_order()'s extras check below.

# #73 (Rick's PR #100 review, required item 3): the OOS annotation used to hard-code the single
# string "Ice cream machine is being cleaned" for ANY down machine -- a slush with
# `slush_machine: "down"` would wrongly claim the ice cream machine was the problem. Keyed per
# machine so each machine's own outage reads naturally; an unlisted machine key still degrades
# safely to a generic "<key> is down" label instead of a KeyError or silently reusing another
# machine's text.
_MACHINE_OOS_LABELS: dict[str, str] = {
    "ice_cream_machine": "Ice cream machine is being cleaned",
    "slush_machine": "Slush machine is down",
    "fryer": "Fryer is down",
}


def _machine_oos_label(machine: str) -> str:
    """Return the guest-facing out-of-stock label for *machine*, falling back to a generic
    "<machine> is down" for any machine key not in `_MACHINE_OOS_LABELS` (e.g. a future machine
    added to a persona pack before this dict is updated for it)."""
    return _MACHINE_OOS_LABELS.get(machine, f"{machine} is down")
ALLOWED_EXTRA_CATEGORIES = {"slushes & drinks", "shakes & ice cream", "burgers & sandwiches", "drinks", "slushes", "shakes", "combos"}
BLOCKED_EXTRA_CATEGORIES = {"hot dogs & tots", "sides", "hot dogs"}

# Map category keywords → mods that don't make sense for that category
INVALID_MODS = {
    "shake": ["lettuce", "tomato", "onion", "mustard", "ketchup", "pickle", "jalapeño", "relish"],
    "slush": ["lettuce", "tomato", "onion", "mustard", "ketchup", "pickle", "jalapeño", "cheese", "bacon", "patty"],
    "drink": ["lettuce", "tomato", "onion", "mustard", "ketchup", "pickle", "jalapeño", "cheese", "bacon", "patty"],
    "side": ["whipped cream", "chocolate", "vanilla", "strawberry"],
    "hot dog": ["whipped cream", "chocolate", "vanilla", "strawberry"],
}


def validate_customization(item_name: str, mods_string: str, prompt_loader=None, menu=None) -> str | None:
    """Return an error message if the mods are nonsensical for the item category, else None.

    *prompt_loader*/*menu* (#74): the caller's resolved per-session persona context, if any;
    both default to the deployment default (``_prompt_loader``, ``default_persona`` catalog)
    when omitted, so a direct call with no session in hand still classifies against a real,
    fully-loaded persona catalog -- never a Sonic-only module shortcut."""
    prompt_loader = prompt_loader if prompt_loader is not None else _prompt_loader
    menu = menu or default_persona.get_default_menu_catalog()
    # PR #50 review (third round, minor): reuse the one shared strip_modifiers() helper instead of
    # a second, independent ad-hoc `.split("(")[0]` implementation of the same paren-stripping rule
    # (menu_utils.py's classification functions and order_state.py's combo-conversion logic already
    # route through it).
    base_name = strip_modifiers(item_name)
    category = menu.infer_category(base_name)
    mods_lower = mods_string.lower()
    for cat_key, forbidden_list in INVALID_MODS.items():
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
) -> ToolResult:
    """Execute a hybrid Azure AI Search query with caching and safe fallbacks.

    *menu*/*prompt_loader*/*persona_id* (#74): the caller's resolved per-session persona
    context, if any. All three default to the deployment default (the ``default_persona``
    catalog, ``_prompt_loader``, and an unnamespaced cache) when omitted, so every existing
    direct call (e.g. in tests) keeps behaving exactly as before; ``_search_dispatch`` (used
    by the registered "search" tool) is the only caller that passes them.
    """
    menu = menu or default_persona.get_default_menu_catalog()
    prompt_loader = prompt_loader if prompt_loader is not None else _prompt_loader

    query = args["query"]
    logger.info("Knowledge search requested for query '%s'", query)

    # Check cache first — repeated questions about the same menu item are common. Namespaced
    # by persona_id so two personas asking the same question never share a cached result from
    # each other's (potentially different) search index (#74).
    cache_key = f"{persona_id or ''}::{query.strip().lower()}"
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
            **_query_kwargs(semantic_enabled),
        )
    except TimeoutError:
        logger.error("Azure AI Search timed out for query '%s'", query)
        _err = prompt_loader.render_error("search_service_unavailable") if prompt_loader else "I'm having trouble reaching our menu right now — could you try that again?"
        return ToolResult(_err, ToolResultDirection.TO_SERVER)
    except HttpResponseError as exc:
        # Gracefully handle schema/field mismatches (e.g., invalid $select fields) by retrying with a minimal projection.
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
        summary = (
            f"[{identifier}]: "
            f"Item: {item_name}, Category: {record.get('category', 'N/A')}, "
            f"Available Sizes: {size_str}"
        )

        # Flag items affected by machine outages so the AI knows not to recommend them.
        # #73: data-driven off the item's own `requiresMachine` field instead of a substring
        # keyword list, so a real menu item is the only thing ever flagged.
        machine = menu.requires_machine(item_name)
        if machine and MOCK_MACHINE_STATUS.get(machine) == "down":
            summary += f" [OOS: {_machine_oos_label(machine)}]"

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
    "description": "Update the current order by adding or removing items.",
    "parameters": {
        "type": "object",
        "properties": {
            "action": { 
                "type": "string", 
                "description": "Action to perform: 'add' or 'remove'.", 
                "enum": ["add", "remove"]
            },
            "item_name": { 
                "type": "string", 
                "description": "Name of the item to update, e.g., 'Cherry Limeade'."
            },
            "size": { 
                "type": "string", 
                "description": "Size of the item to update, e.g., 'Large'."
            },
            "quantity": { 
                "type": "integer", 
                "description": "Quantity of the item to update. Represents the number of items."
            },
            "price": { 
                "type": "number", 
                "description": "Price of a single item to add. Required only for 'add' action. Note: This is the price per individual item, not the total price for the quantity."
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
    if args["action"] == "add":
        menu_item = menu.resolve_menu_item(item_name)
        if menu_item is None:
            logger.info("Rejected off-menu item '%s' for session %s (not_on_menu)", item_name, session_id)
            _message = pl.render_error("item_not_on_menu", item_name=item_name) if pl else (
                f"I'm sorry, {item_name} isn't on our menu. Would you like to try something else instead? "
                "Use the search tool with the guest's words and offer the closest real menu item by its exact name."
            )
            return ToolResult(
                {
                    "status": "rejected",
                    "item_added": False,
                    "reason": "not_on_menu",
                    "item_name": item_name,
                    "message": _message,
                },
                ToolResultDirection.TO_SERVER,
            )

        requested_size = menu.canonical_size_key(size)
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

    # ── Customization validation (reject nonsensical mods) ──
    if "(" in item_name:
        mods_content = item_name[item_name.find("(")+1:item_name.find(")")]
        error = validate_customization(item_name, mods_content, prompt_loader=pl, menu=menu)
        if error:
            return ToolResult(error, ToolResultDirection.TO_SERVER)

    # ── Hardened price validation (add only) ──
    price = args.get("price", 0.0)
    if args["action"] == "add" and price <= 0.0:
        logger.warning("Model attempted to add item %s with invalid price $%.2f (rejecting $0 items)", item_name, price)
        _err = pl.render_error("price_validation_failed") if pl else "I'm sorry, I had a glitch with the pricing for that. Could you say that again?"
        return ToolResult(_err, ToolResultDirection.TO_SERVER)

    if args["action"] == "add" and menu.is_extra_item(item_name):
        current_items = order_state_singleton.get_order_items(session_id)
        has_allowed_base = False
        has_blocked_base = False

        for order_item in current_items:
            category = menu.infer_category(order_item.item)
            if category in ALLOWED_EXTRA_CATEGORIES:
                has_allowed_base = True
            if category in BLOCKED_EXTRA_CATEGORIES:
                has_blocked_base = True

        if not has_allowed_base:
            if has_blocked_base:
                apology = pl.render_error("extras_blocked_category") if pl else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but I can't add them to sides or hot dogs on their own."
                )
            else:
                apology = pl.render_error("extras_no_base_item") if pl else (
                    "I can add extras to drinks, slushes, shakes, or combos, "
                    "but not to sides or hot dogs on their own."
                )
            logger.info("Blocked extra '%s' for session %s", item_name, session_id)
            return ToolResult(apology, ToolResultDirection.TO_SERVER)

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

    json_order_summary = order_state_singleton.get_order_summary_json(session_id)
    summary = order_state_singleton.get_order_summary(session_id)
    logger.debug("Session %s order summary after update: %s", session_id, json_order_summary)

    # ── Delta text for voice confirmation ──
    action = args["action"]
    display_size = size if size and size.lower() not in {"", "standard", "n/a", "na", "none", "n.a."} else ""
    display_name = f"{display_size.capitalize() + ' ' if display_size else ''}{item_name}"

    absorbed = result_info.get("absorbed_into_combo", False) if result_info else False
    converted_from = result_info.get("combo_converted_from") if result_info else None

    if absorbed:
        delta_text = f"{display_name} included with your combo — your total is {summary.finalTotalDisplay}"
    elif converted_from and action == "add":
        combo_display = display_name
        mods = result_info.get("mods_carried", "")
        if mods:
            combo_display = f"{display_name} {mods}"
        delta_text = f"Upgraded to {combo_display} — your total is now {summary.finalTotalDisplay}"
    elif pl:
        tpl = pl.get_delta_template(action)
        delta_text = pl.render_template(tpl, quantity=quantity, display_name=display_name, total=summary.finalTotalDisplay)
    elif action == "add":
        delta_text = f"Added {quantity} {display_name} — your total is now {summary.finalTotalDisplay}"
    else:
        delta_text = f"Removed {quantity} {display_name} — your total is now {summary.finalTotalDisplay}"

    # ── Combo validation: flag missing components ──
    validation = order_state_singleton.get_combo_requirements(session_id)

    if not validation["is_complete"]:
        delta_text += f"\n\n[SYSTEM HINT: {validation['prompt_hint']}]"
        logger.info("Combo incomplete for session %s — missing: %s", session_id, validation["missing_items"])
    elif action == "add" and not absorbed:
        # ── Category-aware upsell hints (only when combo requirements are met) ──
        category = menu.infer_category(item_name)
        if pl:
            delta_text += pl.get_upsell_hint(category)
        else:
            if category == "combos":
                delta_text += " (UPSELL HINT: Combos are a great base! Ask if they want to upgrade to a Large size, or add a delicious Shake or Dessert!)"
            elif category in ("burgers", "burgers & sandwiches"):
                delta_text += " (UPSELL HINT: Perfect choice! Ask if they want to make it a combo meal with Tots or Fries and a refreshing Drink!)"
            elif category in ("drinks", "slushes"):
                delta_text += " (UPSELL HINT: Great drink choice! Ask if they want to add a Flavor Add-In to customize it, or pair it with a tasty side!)"
            elif category in ("shakes", "desserts", "shakes & ice cream"):
                delta_text += " (UPSELL HINT: Yum! Shakes are perfect on their own, but ask if they'd like to add Whipped Cream or pair with a snack!)"
            elif category in ("sides", "hot dogs", "hot dogs & tots"):
                delta_text += " (UPSELL HINT: Tasty! Ask if they want to add a refreshing Drink or Slush to complete their meal!)"
            else:
                delta_text += " (UPSELL HINT: Ask if they'd like to add anything else — maybe a drink, side, or dessert!)"
        logger.debug("Upsell hint for category '%s'", category)

    happy_hour_note = " [HAPPY HOUR ACTIVE: slushes and fountain drinks are half-price; shakes, Blasts and sundaes are full price]" if order_state_singleton.is_happy_hour_for_session(session_id) else ""
    return ToolResult(delta_text + happy_hour_note, ToolResultDirection.TO_BOTH, client_text=json_order_summary)


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
    happy_hour_note = " [HAPPY HOUR ACTIVE: slushes and fountain drinks are half-price; shakes, Blasts and sundaes are full price]" if order_state_singleton.is_happy_hour_for_session(session_id) else ""
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


