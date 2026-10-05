import logging
import threading
import uuid
from dataclasses import dataclass
from decimal import Decimal
from typing import TYPE_CHECKING
from zoneinfo import ZoneInfo

import conformance_hooks
import default_persona
from menu_utils import _menu_key, get_catalog_for_persona
from models import OrderItem, OrderSummary
from money_utils import format_money, format_money_spoken, number_to_words, to_decimal

if TYPE_CHECKING:
    from menu_utils import MenuCatalog
    from persona_loader import Persona

__all__ = ["OrderState", "SessionIdentifiers", "order_state_singleton", "is_happy_hour"]

logger = logging.getLogger("order_state")

# PR #184 round 2 (Rick's review, items 1-3): per-instance bundle slot state now lives on each
# OrderItem itself (models.py's `_bundle_slots` PrivateAttr) rather than this session-level
# `_SLOT_KEY`/`absorbed_*` scheme the #179 engine used -- that flat, one-slot-per-SESSION design
# is exactly what let two combos in the same order share (and clobber) each other's slot state,
# a quantity>1 combo line have no way to track which physical unit was resized, and a `remove`d
# combo's stale state bleed into a brand-new one. See `_fill_bundle_component`,
# `_find_bundle_item_for_component`, `_vacate_bundle_component` below for the new design.


def is_happy_hour(session: dict | None = None) -> bool:
    """Whether *now* (store-local time) falls in a happy-hour window -- *session*'s own bound
    persona (#74) if given, else the DEFAULT persona's (for a caller with no session in hand,
    e.g. ``get_menu_catalog``/``is_happy_hour_for_session`` below for an unknown/expired session
    id, or a direct call with no session at all).

    This is the SINGLE place happy-hour is ever computed for ANY session -- ``_is_happy_hour_for``
    below always calls this, never duplicates its own copy of the window/timezone lookup -- and
    the target of every ``@patch("order_state.is_happy_hour", ...)`` call site across the test
    suite, which mocks the whole function regardless of which session (or none) it's called
    with/without."""
    if session is not None:
        mode = session.get("_happy_hour_mode", "auto")
        if mode == "on":
            return True
        if mode == "off":
            return False
        window = session.get("_happy_hour_window")
        tz = session["_tz"]
    else:
        persona = default_persona.get_default_persona()
        happy_hour_cfg = persona.manifest.pricing.happyHour
        window = (happy_hour_cfg.startHour, happy_hour_cfg.endHour) if happy_hour_cfg is not None else None
        tz = ZoneInfo(persona.manifest.store.timezone)
    if window is None:
        return False
    now = conformance_hooks.now(tz)
    start_hour, end_hour = window
    return start_hour <= now.hour < end_hour


def _compose_spoken_readback(order_items: list, menu: "MenuCatalog", final_total: Decimal) -> str:
    """Issue #304: the shared, server-composed voice read-back -- "I have ... Your total is
    ....". Groups items with the same (spoken) display name, same exact wording
    ``get_grouped_order_for_readback`` always returned, now the SINGLE implementation behind
    both that method (unchanged, per-session convenience wrapper) and ``OrderSummary.spokenReadBack``
    (``_update_summary`` below) -- the two can never drift apart because there is only ever one
    composition. *menu* is this session's own bound persona's ``MenuCatalog`` (its ``.spoken()``
    already folds in both ``sizes.spokenAs`` and any per-item ``spokenName`` -- see
    ``MenuCatalog.from_persona``); *final_total* is the exact ``Decimal`` total (#313, Rick's
    review item 2: speaking money needs the exact value, not the already-rounded "$0.00" display
    string, so the digit and word renderings of the SAME amount can never drift -- both go
    through the identical ``ROUND_HALF_UP`` rounding, one in ``format_money``, one here in
    ``format_money_spoken``)."""
    if not order_items:
        return "Your order is currently empty."

    # Aggregate quantities by display name
    counts: dict[str, int] = {}
    order: list[str] = []
    for oi in order_items:
        # #74: every session is bound to a persona (the default when none was requested), so
        # readback always speaks that persona's OWN size vocabulary (``sizes.spokenAs``) via
        # its MenuCatalog -- there is no separate, hardcoded "RT 44"/"RT44" -> "Route 44"
        # substitution path anymore (that substitution is now simply the default persona's own
        # pack data, reached through the exact same ``.spoken()`` call every persona uses).
        clean_name = menu.spoken(oi.display)
        # Convert parenthesized mods to speech-friendly format
        # e.g. "Sonic Cheeseburger (No Lettuce)" -> "Sonic Cheeseburger with no lettuce"
        if "(" in clean_name and ")" in clean_name:
            clean_name = clean_name.replace("(", "with ").replace(")", "")
        positive_upcharges = [to_decimal(upcharge) for upcharge in oi.componentUpcharges if to_decimal(upcharge) > 0]
        if positive_upcharges:
            upcharge_total = sum(positive_upcharges, Decimal("0"))
            if len(positive_upcharges) == 1:
                clean_name = f"{clean_name} with a {format_money_spoken(upcharge_total)} upcharge"
            else:
                clean_name = f"{clean_name} with {format_money_spoken(upcharge_total)} in component upcharges"
        # #313 (Rick's review, item "grouping key"): the grouping key is deliberately this
        # already-spoken display string (not the raw item/size) -- two items whose spoken form
        # collides (e.g. two differently-cased raw names that both speak as "Widget") are
        # meant to merge into one read-back line, same as two literally-identical lines would.
        # See tests/test_order_state.py::test_spoken_name_collision_groups_into_one_readback_line.
        if clean_name not in counts:
            order.append(clean_name)
        counts[clean_name] = counts.get(clean_name, 0) + oi.quantity

    # Build the natural language string
    parts = []
    for display in order:
        qty = counts[display]
        # #313 (Rick's review, item 2): a bare digit quantity read next to a count-based size
        # (e.g. "3 10 Count Glazed Munch-kins Donut Hole Treats") is ambiguous -- "three ten
        # count" or "three hundred ten"? Spelling the quantity out as a word removes it.
        prefix = f"{number_to_words(qty)} "
        parts.append(f"{prefix}{display}")

    if len(parts) > 1:
        summary_str = ", ".join(parts[:-1]) + f", and {parts[-1]}"
    else:
        summary_str = parts[0]

    return f"I have {summary_str}. Your total is {format_money_spoken(final_total)}. "


@dataclass
class SessionIdentifiers:
    session_token: str
    round_trip_index: int
    round_trip_token: str
    # #74 (Rick's PR #102 review, item 4): the persona this session is bound to -- every
    # session has one (the deployment default when none was requested), so this is never None.
    persona_id: str
    # #75: the realtime model this session is bound to -- every session has one (the
    # bound persona's own `models.realtime.default` when none was explicitly requested
    # via `?model=`), so this is never None either.
    model_id: str
    # #75/Rick's PR #106 review item 3: which pipeline `model_id` belongs to (`"realtime"` |
    # `"cascade"`) -- Morty's model picker (F10) and `/api/personas`/session
    # metadata group models by pipeline, so this travels alongside `model_id` everywhere the
    # latter does. Never None: every session's model was resolved through exactly one
    # pipeline's processor (`processors.dispatch_processor`) before the session was created.
    pipeline: str


class OrderState:
    """Per-session order state.

    #97: a session's ``OrderState`` entry is confined to the single asyncio event loop /
    thread that created it -- exactly like the C# backend's ``SessionActor`` (one actor per
    session, no cross-actor shared mutable state). It is NOT a general-purpose thread-safe data
    structure: every session-scoped method below asserts (via ``_check_owner``) that it is being
    called from the same OS thread ``create_session`` ran on, and raises ``RuntimeError``
    immediately if not, rather than silently racing on ``self.sessions[session_id]``'s list/dict
    mutations. Concurrent guests are safe because each one's session lives on its own event-loop
    task on the SAME thread (cooperative multitasking -- ``handle_order_update`` etc. contain no
    internal ``await``, so they can never be interleaved mid-mutation); concurrent sessions are
    never each given their own OS thread. See ``tests/test_performance.py``'s
    ``OrderStateThreadConfinementTests`` for the enforced contract.
    """

    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance.sessions = {}
        return cls._instance

    def _reset_order_state(self, session: dict) -> None:
        """Clear this session's order lines (#41). ``create_session`` and ``reset_order`` both
        delegate here. PR #184 round 2: there is no more session-level combo-absorption
        bookkeeping to clear alongside the lines -- every bundle instance's own slot-fill state
        now lives ON that ``OrderItem`` (models.py's ``_bundle_slots``), so clearing the list IS
        clearing all of it, structurally, with no way for the two to drift out of sync again."""
        session["order_state"] = []

    def _check_owner(self, session_id: str) -> None:
        """#97: raise if this session-scoped call is happening on a different OS thread than the
        one that created the session. See the ``OrderState`` class docstring above -- a session's
        state is confined to its owning thread/event loop, like the C# ``SessionActor``, and this
        is the single enforcement point every session-scoped public method below calls first."""
        session = self.sessions[session_id]
        owner = session.get("_owner_thread")
        current = threading.get_ident()
        if owner is not None and owner != current:
            raise RuntimeError(
                f"OrderState session {session_id} accessed from thread {current}, "
                f"but owned by thread {owner} (see #97: sessions are single-thread/event-loop confined)"
            )

    def _menu_for(self, session: dict):
        """Return this session's own :class:`~menu_utils.MenuCatalog` (#74, Rick's PR #102
        review item 2: every session is bound to a persona -- the deployment default when none
        was explicitly requested -- so this is never a module-level fallback)."""
        return session["_menu"]

    def _pricing_for(self, session: dict) -> tuple[Decimal, Decimal]:
        """Return this session's own ``(tax_rate, happy_hour_discount)`` as Decimals, from its
        bound persona's ``pricing`` config (#74; every session has one)."""
        return session["_tax_rate"], session["_happy_hour_discount"]

    def _is_happy_hour_for(self, session: dict) -> bool:
        """Whether *now* falls in this session's own happy-hour window (#74; every session has
        one). Delegates to the single module-level ``is_happy_hour(session)`` so there is
        exactly one implementation of the window/timezone lookup, not a second copy here."""
        return is_happy_hour(session)

    def _update_summary(self, session_id: str):
        session = self.sessions[session_id]
        order_items = session["order_state"]
        menu = self._menu_for(session)
        happy_hour = self._is_happy_hour_for(session)
        # #46: accumulate in exact Decimal, with NO intermediate rounding anywhere in this
        # calculation. Only the very last step below converts to float, once, at the Pydantic
        # model boundary -- eliminating the compounding float-multiplication noise that used to
        # make the spoken total drift a fraction of a cent off the golden values.
        tax_rate, happy_hour_discount = self._pricing_for(session)
        total = Decimal("0")
        for item in order_items:
            item_total = to_decimal(item.price) * item.quantity
            if happy_hour and menu.is_happy_hour_discounted(item.item):
                item_total *= happy_hour_discount
            total += item_total
        tax = total * tax_rate
        finalTotal = total + tax
        final_total_display = format_money(finalTotal)
        summary = OrderSummary(
            items=order_items,
            total=float(total),
            tax=float(tax),
            finalTotal=float(finalTotal),
            totalDisplay=format_money(total),
            taxDisplay=format_money(tax),
            finalTotalDisplay=final_total_display,
            spokenReadBack=_compose_spoken_readback(order_items, menu, finalTotal),
        )
        session["order_summary"] = summary
        # Cache the JSON representation to avoid repeated Pydantic serialization
        session["order_summary_json"] = summary.model_dump_json()
        logger.debug("Order summary updated for session %s (items=%d, total=%s)", session_id, len(order_items), finalTotal)

    def _happy_hour_discounted_line_displays(self, session: dict) -> list[str]:
        if not self._is_happy_hour_for(session):
            return []
        menu = self._menu_for(session)
        discounted = []
        for item in session["order_state"]:
            if not menu.is_happy_hour_discounted(item.item):
                continue
            display = item.display or item.item
            discounted.append(f"{item.quantity} x {display}" if item.quantity > 1 else display)
        return discounted

    def create_session(self, persona: "Persona | None" = None, model_id: str | None = None,
                        model_deployment: str | None = None, model_reasoning: bool | None = None,
                        model_pipeline: str | None = None, menu_mode: str | None = None) -> str:
        """Create a new, empty order-state session.

        *persona* (#74, Rick's PR #102 review item 2): the session is bound to *persona*, or to
        the deployment-wide default (``default_persona.get_default_persona()``) when omitted --
        every session has exactly one bound persona, through the same ``MenuCatalog``/pricing
        path either way. There is no unbound-session state and no module-level brand-only
        fallback.

        *model_id*/*model_deployment*/*model_reasoning*/*model_pipeline* (#75): this session's
        own bound realtime model, resolved once by the caller (``processors.dispatch_processor``
        + the returned processor's own ``resolve_model()``) before the session is ever created --
        no mid-conversation model switching, exactly like *persona* above. *model_id* omitted
        (``None``) binds to *persona*'s own ``models.realtime.default`` -- today's exact,
        unchanged path; *model_deployment*/*model_reasoning* stay ``None`` in that case too
        (``_forward_messages`` falls back to ``self.deployment``/the process-wide reasoning
        heuristic, never a stale/incorrect value). *model_pipeline* (Rick's PR #106 review item
        3) omitted defaults to ``"realtime"`` -- the only pipeline a session can be bound to
        before #82/#81 land.

        *menu_mode* (#165): this session's own bound daypart -- ``"breakfast"`` or ``"lunch"`` --
        for a persona that opts into ``features.dayparts``, resolved once by the caller
        (``rtmt.py``'s websocket handshake, from an optional ``?mode=`` query param, defaulting
        to ``"lunch"`` -- the original reference app's own default, #164 decision D3) before the
        session is ever created -- no mid-conversation mode switching, exactly like *persona* and
        *model_id* above. Always ``None`` for a persona that does not declare
        ``features.dayparts`` -- ``get_menu_mode``/``item_available_now`` below never gate
        anything for those sessions."""
        persona = persona or default_persona.get_default_persona()
        model_id = model_id or persona.manifest.models.realtime.default
        model_pipeline = model_pipeline or "realtime"
        # #165: a persona that doesn't declare `features.dayparts` is never mode-bound,
        # regardless of what a caller passed -- there is no daypart to switch between, so
        # `get_menu_mode`/every mode-aware gate below stays a no-op for it. A persona that DOES
        # declare it always resolves to a real mode -- "lunch" (the original reference app's own
        # default, #164 decision D3) for an omitted/unrecognized `?mode=` value, never a
        # silently-unbound session.
        if persona.manifest.features.dayparts:
            menu_mode = menu_mode if menu_mode in ("breakfast", "lunch") else "lunch"
        else:
            menu_mode = None
        session_id = str(uuid.uuid4())
        session_token = str(uuid.uuid4())
        empty_summary = OrderSummary(
            items=[],
            total=0.0,
            tax=0.0,
            finalTotal=0.0,
            totalDisplay=format_money(0),
            taxDisplay=format_money(0),
            finalTotalDisplay=format_money(0),
            spokenReadBack="Your order is currently empty.",
        )
        happy_hour_cfg = persona.manifest.pricing.happyHour
        self.sessions[session_id] = {
            "order_summary": empty_summary,
            "order_summary_json": empty_summary.model_dump_json(),
            "session_token": session_token,
            "round_trip_index": 0,
            "round_trip_token": self._format_round_trip_token(session_token, 0),
            # #97: the thread/event-loop this session is confined to for the rest of its life.
            "_owner_thread": threading.get_ident(),
            "_persona_id": persona.id,
            "_model_id": model_id,
            "_model_deployment": model_deployment,
            "_model_reasoning": model_reasoning,
            "_model_pipeline": model_pipeline,
            # #75 bug fix (this revision, PR #106 review): captured once, straight off the
            # concrete `persona` object already on hand here -- never re-looked-up later via
            # any catalog/singleton. `session_manager.py::resume()`'s "omitted `?model=`
            # resolves to the bound persona's OWN default" check reads this instead of
            # reaching for a persona catalog it has no reliable access to (the deployment-wide
            # `default_persona` catalog only knows the REAL, always-enabled persona packs --
            # not a persona bound via some other catalog, e.g. a test's own fixture catalog).
            "_persona_default_model_id": persona.manifest.models.realtime.default,
            "_menu": get_catalog_for_persona(persona),
            # #165: this session's own bound daypart (``"breakfast"``/``"lunch"``), or ``None``
            # for a persona that doesn't declare ``features.dayparts`` -- see this method's own
            # doc comment above. ``get_menu_mode`` is the single reader; ``tools.py``'s
            # ``search``/``update_order`` and ``menu_utils.MenuCatalog.item_available_now`` are
            # the only gates that act on it.
            "_menu_mode": menu_mode,
            "_tz": ZoneInfo(persona.manifest.store.timezone),
            "_tax_rate": to_decimal(persona.manifest.pricing.taxRate),
            "_happy_hour_discount": (
                to_decimal(happy_hour_cfg.priceMultiplier) if happy_hour_cfg is not None else to_decimal("1")
            ),
            "_happy_hour_window": (
                (happy_hour_cfg.startHour, happy_hour_cfg.endHour) if happy_hour_cfg is not None else None
            ),
            # #113: this session's own bound persona's happy-hour banner/announce switch --
            # tools.py's ONLY source for the text appended to update_order/get_order results.
            # A pack with `pricing.happyHour: null` (decision 5, e.g. a no-happy-hour brand pack
            # like test-beta) gets
            # `announce=False`/`banner=""` here, so it can never announce regardless of clock.
            "_happy_hour_announce": happy_hour_cfg.announce if happy_hour_cfg is not None else False,
            "_happy_hour_banner": happy_hour_cfg.banner if happy_hour_cfg is not None else "",
            "_machine_overrides": {},
            "_happy_hour_mode": "auto",
        }
        self._reset_order_state(self.sessions[session_id])
        logger.info("Session created: %s (persona=%s, model=%s)", session_id, persona.id, model_id)
        return session_id

    def delete_session(self, session_id: str) -> None:
        if session_id not in self.sessions:
            return
        self._check_owner(session_id)
        if self.sessions.pop(session_id, None) is not None:
            logger.info("Session deleted: %s", session_id)

    def _format_round_trip_token(self, session_token: str, round_trip_index: int) -> str:
        return f"{session_token}-{round_trip_index:04d}"

    @staticmethod
    def _empty_bundle_slot() -> dict:
        return {"item": "", "size": "", "display": "", "last_item": "", "last_size": "", "autofill": False, "upcharge": 0.0}

    def _sync_bundle_slot_list(self, combo_item, component: str) -> list:
        """PR #184 round 2 (Rick's review, item 2 -- "quantity-2 combos handled correctly"): each
        bundle INSTANCE's own ``_bundle_slots[component]`` is a LIST with exactly
        ``combo_item.quantity`` entries, one per physical unit this single order line
        represents -- a "2 Big Mac Meals" line has 2 independent side slots and 2 independent
        drink slots, not one shared slot (the old design's bug: a session-wide counter couldn't
        tell which of several physical units a given fill belonged to). Grows with fresh empty
        slot records when quantity increases (e.g. two identical combos merging into one line
        via a normal quantity add); truncates from the END when quantity decreases (removing one
        physical unit of a multi-quantity combo line drops that unit's own slot record, never the
        others') -- called lazily on every read/write so there is exactly one place this
        invariant is enforced."""
        slots = combo_item._bundle_slots.setdefault(component, [])
        while len(slots) < combo_item.quantity:
            slots.append(self._empty_bundle_slot())
        if len(slots) > combo_item.quantity:
            del slots[combo_item.quantity:]
        return slots

    def _component_upcharge(self, menu, item_name: str, size: str) -> float:
        if menu.bundle_resize_rule != "componentUpcharge" or not menu.bundle_included_size or not item_name:
            return 0.0
        actual_price = menu.price_for(item_name, size)
        included_price = menu.price_for(item_name, menu.bundle_included_size)
        if actual_price is None or included_price is None:
            return 0.0
        delta = to_decimal(actual_price) - to_decimal(included_price)
        return float(delta) if delta > 0 else 0.0

    def _bundle_unit_upcharge(self, menu, combo_item, unit_index: int, replacement: tuple[str, str, str] | None = None) -> Decimal:
        total = Decimal("0")
        for component in menu.bundle_slots(combo_item.item):
            slots = self._sync_bundle_slot_list(combo_item, component)
            slot = slots[unit_index] if unit_index < len(slots) else self._empty_bundle_slot()
            item = slot.get("item", "")
            size = slot.get("size", "")
            if replacement is not None and component == replacement[0]:
                item = replacement[1]
                size = replacement[2]
            total += to_decimal(self._component_upcharge(menu, item, size))
        return total

    def _reprice_bundle_from_components(self, combo_item, menu) -> None:
        own_price = menu.price_for(combo_item.item, combo_item.size)
        if own_price is None:
            return
        price = to_decimal(own_price)
        if menu.bundle_resize_rule == "componentUpcharge" and combo_item.quantity > 0:
            price += self._bundle_unit_upcharge(menu, combo_item, 0)
        combo_item.price = float(price)

    def _find_bundle_slot(
        self, order_state: list, menu, component: str, item_name: str | None = None,
        target_size: str | None = None,
    ):
        """PR #184 round 2 (Rick's review, item 2): which (bundle instance, slot index) a
        slot-fill/resize/vacate targets, now that slot state is tracked per PHYSICAL UNIT of a
        combo INSTANCE rather than per session (the old design's root cause for two combos -- or
        a combo removed without a full ``reset_order`` -- bleeding slot state into each other, and
        for a quantity-2 combo line never telling which unit a fill belonged to). Candidates are
        every (order line, slot index) pair whose line's OWN bundle slots (menu.bundle_slots)
        include *component*, across every physical unit of every such line.

        If *item_name* is given, the MOST RECENT matching instance (last in `order_state`, i.e.
        the one added or merged-into most recently) whose slot is CURRENTLY filled by that exact
        item wins -- "resize the drink of whichever combo actually has that drink" (two combos;
        the guest says "make the Coke large" and only one of them currently has a Coke). If
        *target_size* is also given, a matching slot that is NOT already that size wins first; this
        lets repeated "make the Cherry Limeade large" calls on a split quantity-2 combo advance to
        the remaining medium unit instead of no-oping on the already-large unit. Otherwise (or when
        no instance's slot holds that item), the MOST RECENT instance with a VACANT slot for
        *component* wins (lowest vacant index within that instance) -- a fresh absorption lands on
        whichever instance still needs filling, preferring the one most recently touched.
        Returns ``None`` when no slot matches *item_name* (if given) and no slot anywhere is
        vacant -- callers must never be handed an already-FULL, non-matching slot to silently
        overwrite (e.g. a second, different side added while the combo's one side slot is already
        taken must fall through to a standalone add, not clobber the existing side)."""
        candidates = [it for it in order_state if component in menu.bundle_slots(it.item)]
        if not candidates:
            return None
        if item_name is not None:
            key = _menu_key(item_name)
            if target_size is not None:
                for combo_item in reversed(candidates):
                    slots = self._sync_bundle_slot_list(combo_item, component)
                    for idx, slot in enumerate(slots):
                        if (
                            slot.get("item")
                            and _menu_key(slot["item"]) == key
                            and slot.get("size") != target_size
                        ):
                            return (combo_item, idx)
            for combo_item in reversed(candidates):
                slots = self._sync_bundle_slot_list(combo_item, component)
                for idx, slot in enumerate(slots):
                    if slot.get("item") and _menu_key(slot["item"]) == key:
                        return (combo_item, idx)
        for combo_item in reversed(candidates):
            slots = self._sync_bundle_slot_list(combo_item, component)
            for idx, slot in enumerate(slots):
                if not slot.get("item"):
                    return (combo_item, idx)
        return None

    def _rebuild_bundle_display(self, combo_item, menu=None) -> None:
        """Rebuild *combo_item*'s display string from whichever of its OWN (per-instance, PR #184
        round 2) ``_bundle_slots`` are currently filled, across every physical unit this line
        represents. The single place that " w/ <side> & <drink>" suffix is assembled, so every
        caller that changes what's filling a slot (first absorption, an in-place resize, or a
        vacate) renders identically -- including reverting to the bare bundle name (no " w/ ..."
        suffix at all) once every slot is empty again. A multi-quantity line whose units hold
        different items lists every filled unit's display, comma-separated, within its
        component's slot of the "w/ ... & ..." suffix -- the common case (quantity 1, or several
        identical units) collapses to the same single label as before."""
        display_components = []
        wire_components = []
        wire_upcharges = []
        for component in ("sides", "drinks"):
            slots = combo_item._bundle_slots.get(component, [])
            filled = []
            for slot in slots:
                if not slot.get("display"):
                    continue
                if menu is not None:
                    slot["upcharge"] = self._component_upcharge(menu, slot.get("item", ""), slot.get("size", ""))
                filled.append(slot["display"])
                wire_upcharges.append(float(slot.get("upcharge") or 0.0))
            if filled:
                display_components.append(", ".join(filled))
                wire_components.extend(filled)
        combo_item.components = wire_components
        combo_item.componentUpcharges = wire_upcharges
        raw_name = combo_item.item
        if "(" in raw_name:
            base_name = raw_name[:raw_name.find("(")].strip()
            mods = " " + raw_name[raw_name.find("("):]
        else:
            base_name = raw_name
            mods = ""
        if display_components:
            combo_item.display = f"{base_name}{mods} w/ {' & '.join(display_components)}"
        else:
            combo_item.display = f"{base_name}{mods}".strip()

    def _apply_whole_bundle_resize(self, combo_item, menu, new_size: str) -> bool:
        """PR #184 round 2 (Rick's review, item 1 -- "wholeBundleSize" packs): resize the ENTIRE
        bundle instance (every physical unit of this order line) to *new_size* -- its own
        size/price change TOGETHER, and every slot currently filling it is relabeled (never
        re-priced; a slot item is never separately priced on this persona's own rule -- see
        ``_fill_bundle_component``) to match, same as the original app this pack's pricing was
        ported from ("the side and drink sizes will automatically update to match"). An
        autofilled slot (``slot["autofill"]``, e.g. the default side no explicit `add`
        ever named) re-derives its filler text fresh from ``menu.bundle_autofill`` at the new
        size, rather than naively re-prefixing its already-size-baked-in template string (which
        would double up, e.g. "Large Medium World Famous Fries®"); every other slot holds a real
        item name and is simply re-prefixed with the new size label.

        Returns ``False`` (no-op; caller falls back to a plain size/price assignment) if
        *new_size* is already this bundle's own current size, or if this persona's own menu has
        no price for the bundle's own item at *new_size* -- a bundle this pack never extended
        with real size-tier pricing data simply keeps its current size/price, never crashing or
        silently charging an invented amount."""
        if new_size == combo_item.size:
            return False
        new_price = menu.price_for(combo_item.item, new_size)
        if new_price is None:
            return False
        combo_item.size = new_size
        combo_item.price = new_price
        resolved = menu.normalize_size(new_size)
        size_prefix = f"{resolved} " if resolved else ""
        for component, slots in combo_item._bundle_slots.items():
            for slot in slots:
                if not slot.get("item"):
                    continue
                if slot.get("autofill"):
                    fresh_display = menu.bundle_autofill(combo_item.item, resolved_size_label=resolved)
                    filler_display = fresh_display.get(component)
                    if filler_display:
                        # PR #184 round 3 (Rick's review, item E): ``slot["item"]`` must stay the
                        # BASE, on-menu item name (e.g. "World Famous Fries®"), never the
                        # size-baked-in template text -- the same invariant every non-autofill
                        # slot already holds (``display`` carries the size prefix; ``item`` never
                        # does). Otherwise a later `modify`/`remove` naming the real menu item
                        # (what the guest actually says, and what `tools.py` resolves against the
                        # menu) can never match this slot via ``_menu_key`` and is wrongly
                        # rejected as `not_in_order`.
                        fresh_base = menu.bundle_autofill_names(combo_item.item)
                        filler_item = fresh_base.get(component, filler_display)
                        slot["item"] = filler_item
                        slot["display"] = filler_display
                        slot["last_item"] = filler_item
                        slot["last_size"] = new_size
                else:
                    slot["display"] = f"{size_prefix}{slot['item']}".strip()
                    slot["last_size"] = new_size
                slot["size"] = new_size
        # Rebuild the bundle's own "w/ <side> & <drink>" display from the relabeled slots above
        # -- every caller (the direct bundle-line `modify` path, and `_fill_bundle_component`'s
        # own call for a "wholeBundleSize" slot-fill) gets a correctly rendered display with no
        # separate, easy-to-forget rebuild step of its own.
        self._rebuild_bundle_display(combo_item, menu)
        return True

    def _split_bundle_unit(self, order_state: list, menu, combo_item, unit_index: int):
        """PR #184 round 3 (Rick's review, item F -- quantity-2+ "wholeBundleSize" lines): a
        component resize under ``wholeBundleSize`` is really a whole-MEAL resize (see
        ``_apply_whole_bundle_resize``) -- on a quantity>1 line, naively applying that to
        *combo_item* would silently resize (and reprice) EVERY physical unit sharing this one
        line's single ``price``/``size`` fields, even though the guest/model only ever named ONE
        unit's component. Split the physical unit at *unit_index* off into its own new,
        independent quantity=1 ``OrderItem`` -- carrying that unit's own current side/drink slot
        contents with it -- so the resize that follows in ``_fill_bundle_component`` applies only
        to that one unit; the original line shrinks by one and keeps its old size/price for its
        remaining units, untouched.

        Returns the new split-off ``OrderItem``, inserted into *order_state* directly after
        *combo_item* so read-back order stays stable."""
        split_item = OrderItem(
            item=combo_item.item,
            size=combo_item.size,
            quantity=1,
            price=combo_item.price,
            display=combo_item.display,
            components=list(combo_item.components),
        )
        for comp_name in menu.bundle_slots(combo_item.item):
            slots = self._sync_bundle_slot_list(combo_item, comp_name)
            taken = slots.pop(unit_index) if unit_index < len(slots) else self._empty_bundle_slot()
            split_item._bundle_slots[comp_name] = [taken]
        combo_item.quantity -= 1
        # Identity (`is`), never `==` -- pydantic's `OrderItem.__eq__` compares field VALUES, so
        # two distinct lines that happen to hold identical item/size/quantity/price/display would
        # make `list.index(combo_item)` find the wrong one.
        insert_at = next(i for i, oi in enumerate(order_state) if oi is combo_item) + 1
        order_state.insert(insert_at, split_item)
        self._reprice_bundle_from_components(combo_item, menu)
        self._reprice_bundle_from_components(split_item, menu)
        self._rebuild_bundle_display(combo_item, menu)
        self._rebuild_bundle_display(split_item, menu)
        return split_item

    def _fill_bundle_component(
        self, order_state: list, combo_item, slot_index: int, menu, component: str, item_name: str, size: str,
        display: str, autofill: bool = False,
    ) -> tuple[bool, bool, "OrderItem"]:
        """PR #184 round 2 (Rick's review, item 1): the ONE place a bundle's side/drink slot gets
        (re)filled -- a first-time absorption, a bundle-slot autofill, a refill after a
        `remove`-vacate, an explicit `modify`/resize of the slot, or an `add` of the same item at
        a different size while the slot is already full all route through here. *slot_index*
        identifies WHICH physical unit of *combo_item* (a quantity-N bundle line has N
        independent slots per component) this fill targets -- callers resolve it via
        ``_find_bundle_slot`` (cross-instance resize/vacate targets) or directly (filling a
        specific just-created/just-grown unit during the bundle pivot below).

        Pricing is now a PURE function of the bundle instance's OWN current state -- never a
        stateful delta/"free reference" computation (the root cause of the #179-round-1 path
        dependence Rick's PR #184 review flagged). "includedAnySize" packs (default) always reset
        the bundle's own price to its own flat menu price, no matter what size fills a slot -- a
        side or drink is included AT ANY SIZE, so there is never a credit or an upcharge to
        compute, and ordering a size up front vs. resizing into it afterward always totals the
        same. "wholeBundleSize" packs instead resize the WHOLE bundle
        (``_apply_whole_bundle_resize``) whenever the requested size differs
        from the bundle's own current size -- PR #184 round 3 (Rick's review, item D/M4): ONLY
        when that resize can actually happen (this pack prices the bundle's own item at the
        requested size); otherwise the fill is rejected outright, so a slot is never relabeled to
        a size the bundle itself didn't (and won't) move to. Round 3 item F: a feasible resize on
        a quantity>1 line first splits the targeted unit off (``_split_bundle_unit``) so only that
        ONE unit is affected. "componentUpcharge" packs keep the bundle's own menu price plus
        the sum of positive per-component deltas over the pack-declared included size. A
        quantity>1 line also splits the targeted physical unit whenever that unit's repriced
        component-upcharge total would differ from its siblings, so ``OrderItem.price`` remains a
        per-unit price rather than an averaged line price.

        Returns ``(accepted, is_resize, combo_item)``: *accepted* is ``False`` (slot left
        untouched) when a "wholeBundleSize" RESIZE was requested but this pack has no price for
        the bundle's own item at that size -- callers must treat this as a clean rejection (no
        state changed at all), never as a successful fill. When *accepted* is ``True``,
        *is_resize* is whether *item_name* is the SAME item that last filled (or still fills)
        this slot on this bundle instance -- a genuine RESIZE, not a fresh fill of a different
        item -- so callers can report "resized" vs. "included with your combo" wording.
        *combo_item* is returned because a quantity>1 resize/upcharge change may have split
        *combo_item* into a new line -- callers must use the returned instance for any further
        reads (e.g. ``.display``), not the one they passed in.

        PR #184 round 4 (Rick's review, items 1 & 2): the "wholeBundleSize" cascade/rejection
        below now ALSO requires ``is_resize`` -- it only ever fires for a genuine RESIZE of a
        slot that ALREADY held this same item, never a first-time absorption. This fixes two
        bugs from one cause: (1) a Standard-only bundle (one priced size, e.g. a McChicken-style
        meal) could never absorb an S/M/L drink at all, because the cascade fired on that very
        first fill and found no alternate whole-meal price to move to; (2) absorbing a
        differently-sized drink/side into an S/M/L meal was path-dependent -- adding the meal
        then the drink silently resized/repriced the whole meal, while adding the drink then the
        meal did not, for the identical end state. Per the coordinator's decision on this item,
        the original app (``swigerb/McDonalds_AI_DriveThru``) was checked first: its own
        absorption path never cascades, rejects, or relabels a mismatched-size component either
        -- only an EXPLICIT resize of the meal's own line does that (see
        ``_apply_whole_bundle_resize``/the direct bundle-line `modify` branch below, and the
        resize-via-add/explicit-component-modify callers above, all of which already compute
        ``is_resize=True`` for what they target) -- so first-time absorption is simply accepted
        at its own size, with no price impact, exactly like an "includedAnySize" pack and exactly
        like the original app. This is naturally path-independent (neither order ever cascades)
        and keeps every previously-verified cascade/rejection scenario unchanged, since those are
        all genuine resizes of an already-filled slot."""
        slots = self._sync_bundle_slot_list(combo_item, component)
        slot = slots[slot_index]
        is_resize = bool(slot.get("last_item")) and _menu_key(slot["last_item"]) == _menu_key(item_name)

        if menu.bundle_resize_rule == "wholeBundleSize" and is_resize and size != combo_item.size:
            if menu.price_for(combo_item.item, size) is None:
                logger.info(
                    "Combo %s slot resize for '%s' requested size '%s' but this pack has no "
                    "whole-meal price at that size -- rejecting to avoid a mixed-size bundle",
                    component, item_name, size,
                )
                return False, False, combo_item
            if combo_item.quantity > 1:
                # `slot` is the SAME dict object either way -- `_split_bundle_unit` moves it
                # (by reference) onto the new split-off line, so no re-fetch is needed below.
                combo_item = self._split_bundle_unit(order_state, menu, combo_item, slot_index)

        if menu.bundle_resize_rule == "componentUpcharge" and combo_item.quantity > 1:
            # #205: component upcharges are per physical meal. If this fill/resize would make the
            # targeted unit's upcharge differ from its siblings, split it first so OrderItem.price
            # remains a per-unit price, never an averaged line total.
            prospective = self._bundle_unit_upcharge(menu, combo_item, slot_index, (component, item_name, size))
            sibling_upcharges = [
                self._bundle_unit_upcharge(menu, combo_item, i)
                for i in range(combo_item.quantity)
                if i != slot_index
            ]
            if any(other != prospective for other in sibling_upcharges):
                combo_item = self._split_bundle_unit(order_state, menu, combo_item, slot_index)

        slot["item"] = item_name
        slot["size"] = size
        slot["display"] = display
        slot["last_item"] = item_name
        slot["last_size"] = size
        slot["autofill"] = autofill
        slot["upcharge"] = self._component_upcharge(menu, item_name, size)

        if menu.bundle_resize_rule == "wholeBundleSize" and is_resize:
            self._apply_whole_bundle_resize(combo_item, menu, size)
        else:
            self._reprice_bundle_from_components(combo_item, menu)
        self._rebuild_bundle_display(combo_item, menu)
        return True, is_resize, combo_item

    def _vacate_bundle_component(self, order_state: list, menu, component: str, item_name: str) -> dict | None:
        """PR #184 round 2: a `remove` targeting the item CURRENTLY filling a bundle's side/drink
        slot must vacate THAT instance's THAT unit's slot (via *item_name*-aware lookup, so two
        combos -- or two units of one quantity-N combo -- vacate independently) -- there's no raw
        ``OrderItem`` for an absorbed component, so the ordinary remove-by-line lookup never finds
        one. Clears the slot's CURRENT item/size/display (so ``get_combo_requirements`` flags this
        unit incomplete again) but deliberately leaves ``last_item``/``last_size`` alone (see
        ``_fill_bundle_component``) so a follow-up `add` of that SAME item reports as a resize,
        not a second fresh absorption. Never changes the bundle's own price/size -- vacating a
        slot doesn't un-resize a "wholeBundleSize" bundle, and an "includedAnySize" bundle's price
        was never affected by what filled the slot.

        Returns ``None`` if this slot isn't actually filled by a real bundle item right now
        (should not happen -- callers only invoke this once they've matched *item_name* against
        an already-filled slot -- defensive all the same)."""
        found = self._find_bundle_slot(order_state, menu, component, item_name=item_name)
        if found is None:
            return None
        combo_item, idx = found
        slots = self._sync_bundle_slot_list(combo_item, component)
        slot = slots[idx]
        # Guard against `_find_bundle_slot`'s vacant/fallback branches handing back a slot that
        # does NOT actually hold *item_name* right now (e.g. this component is filled elsewhere
        # by a different item) -- only a genuine holder match may be vacated.
        if not slot.get("item") or _menu_key(slot["item"]) != _menu_key(item_name):
            return None
        vacated_display = slot["display"]
        slot["item"] = ""
        slot["size"] = ""
        slot["display"] = ""
        slot["autofill"] = False
        slot["upcharge"] = 0.0
        self._reprice_bundle_from_components(combo_item, menu)
        self._rebuild_bundle_display(combo_item, menu)
        return {
            "vacated_combo_component": component,
            "vacated_display": vacated_display,
            "combo_display": combo_item.display,
        }

    def is_absorbed_component(self, session_id: str, item_name: str) -> bool:
        """PR #184 round 2: whether *item_name* (any size) currently fills ANY combo instance's
        side or drink slot via absorption, across the whole order -- it has no raw ``OrderItem``
        of its own, so the ordinary ``get_order_items``-based line match (what `remove`/`modify`
        used to rely on exclusively) will never find it, even though it's a real, resizable part
        of the order. tools.py's `modify` on-menu/in-order gate calls this as a second chance
        before rejecting with `not_in_order`."""
        self._check_owner(session_id)
        session = self.sessions[session_id]
        order_state = session["order_state"]
        key = _menu_key(item_name)
        for combo_item in order_state:
            for component in ("sides", "drinks"):
                for slot in combo_item._bundle_slots.get(component, []):
                    if slot.get("item") and _menu_key(slot["item"]) == key:
                        return True
        return False

    def handle_order_update(self, session_id: str, action: str, item_name: str, size: str, quantity: int, price: float) -> dict:
        self._check_owner(session_id)
        session = self.sessions[session_id]
        order_state = session["order_state"]
        menu = self._menu_for(session)
        result_info = {}

        # #40: canonicalize the size to a single alias-resolved key BEFORE any matching/merging
        # so different spellings of the same physical size (e.g. "rt44" vs "route 44" vs "44 oz"
        # vs "ROUTE44") collapse onto one order line and can be removed with any alias, not only
        # the one it was added with.
        size = menu.canonical_size_key(size)

        resolved = menu.normalize_size(size)
        formatted_size = f"{resolved} " if resolved else ""

        display = f"{formatted_size}{item_name}".strip()

        if action in ("add", "modify"):
            # #104 / #77 (`modify` re-prices the same way an `add` does): the unit price charged
            # is always this persona's own menu price for (item_name, size) -- never the tool
            # call's own `price` argument. The model can invent a price, carry one over from the
            # wrong size, or pre-apply a discount; since #73 every accepted item already resolved
            # through the on-menu gate in tools.py, so menu.price_for() (menu_utils.MenuCatalog)
            # is the single source of truth for what a guest is charged. This is the ONE place
            # that source of truth is applied -- callers that build an order directly (tests, a
            # future admin tool) get the same guarantee as the realtime tool-call path, instead of
            # a second, easy-to-forget copy of this check in tools.py. The tool-call `price` is
            # only ever used for the debug comparison below; see docs/persona-architecture.md
            # section 6.
            menu_price = menu.price_for(item_name, size)
            if menu_price is not None:
                # The prompt no longer tells the model to send a price (0ab2119), so a null,
                # non-numeric or omitted `price` is now a likely, well-formed input -- it must
                # never crash the add. Only compare against the menu price when the tool call
                # actually sent a real number (bool is deliberately excluded: `isinstance(True,
                # int)` is True in Python, but a bare `true`/`false` is never a meaningful price).
                # Any other type (``None``, a string, a bool) is ignored with its own debug log;
                # the menu price is always charged either way.
                if isinstance(price, (int, float)) and not isinstance(price, bool):
                    if to_decimal(price) != to_decimal(menu_price):
                        logger.debug(
                            "Tool call price $%.2f for '%s' (%s) differs from menu price $%.2f; "
                            "charging the menu price (session=%s)",
                            price, item_name, size, menu_price, session_id,
                        )
                else:
                    logger.debug(
                        "Tool call price %r for '%s' (%s) is not numeric; ignoring it and "
                        "charging the menu price $%.2f (session=%s)",
                        price, item_name, size, menu_price, session_id,
                    )
                price = menu_price
            else:
                # No menu record for this exact (item, size) -- an on-menu item that somehow
                # reached here without going through tools.py's on-menu/size gate (e.g. a direct
                # caller that builds an order without going through update_order's own-menu
                # validation at all -- see docs/persona-architecture.md section 6's "direct-caller
                # fallback" note), or a pack with a missing price (guarded against by
                # test_menu_data_completeness.py's every-size-has-a-price data test). Fall back to
                # the caller-supplied price rather than silently charging $0 -- logged with %r,
                # never %.2f, since this price was never validated as numeric in the first place
                # (the null/non-numeric guard above only runs when a menu price was found).
                logger.warning(
                    "No menu price found for '%s' size '%s'; falling back to the caller-supplied "
                    "price %r (session=%s)",
                    item_name, size, price, session_id,
                )

        if action == "add":
            # #77 (shared bundle engine, docs/persona-architecture.md section 3.3 row 20): whether
            # this item's own name carries one of THIS persona's ``bundles.nameMarkers`` (one
            # pack's own ``["combo"]``, another's ``["meal"]``, a third pack's ``[]`` -- never
            # true, no bundles at all) -- replaces the old hardcoded ``"combo" in
            # item_name.lower()`` check, which only ever matched one pack's own combos and would
            # silently never fire for a differently-worded bundle name.
            # Exactly reproduces today's behavior for the pack whose own persona.json
            # ``nameMarkers`` IS ``["combo"]``, while generalizing for every other pack.
            # ``convertStandalone`` (also persona-owned) gates whether a name-marker match should
            # even attempt the standalone-removal below at all -- a pack with no bundles sets this
            # ``false`` and skips it outright regardless of its (empty) ``nameMarkers``.
            name_markers = menu.bundle_name_markers
            is_combo = menu.bundle_convert_standalone and any(
                marker in item_name.lower() for marker in name_markers
            )
            # Rick's PR #99 review, decision 1: the item's OWN bundle slots, read from the pack's
            # ``bundle.slots`` field (via menu_utils.bundle_slots) -- NOT derived from the word
            # "combo" in its name. "is_combo" above stays name-based and is used ONLY for the
            # combo-conversion logic below (auto-removing a matching standalone entree), which
            # Rick's review explicitly keeps as-is ("... Combo" only). Everything about how many
            # slots a bundle item actually absorbs -- and of which kind -- goes through
            # "own_bundle_slots"/"is_bundle" instead, so a Dinner or Wacky Pack (whose names don't
            # contain "combo" at all) still absorbs its real slots, and a drinks-only combo like
            # French Toast Sticks Combo never free-absorbs a side.
            own_bundle_slots = menu.bundle_slots(item_name)
            is_bundle = bool(own_bundle_slots)

            # ── Combo conversion: auto-remove matching standalone entree ──
            if is_combo:
                # Shared lookup-key rule (menu_utils._menu_key, PR #50 review round 4) so
                # "burger (Pickles Only)" matches "burger" using the EXACT same normalisation the
                # menu-lookup functions use elsewhere (paren-stripping, whitespace/NBSP collapse,
                # lowercasing, "®" removal) -- one rule, one implementation, not two independently
                # maintained copies of the same "®"-removal logic.
                combo_base = _menu_key(item_name)
                for marker in name_markers:
                    combo_base = combo_base.replace(f" {marker}", "")
                combo_base = combo_base.strip()
                for i, existing in enumerate(order_state):
                    if menu.bundle_slots(existing.item):
                        continue  # skip other bundle items, not just other "combo"-named ones
                    existing_base = _menu_key(existing.item)
                    if existing_base == combo_base:
                        # Carry customization mods (e.g., "Pickles Only") to the combo
                        if "(" in existing.item:
                            mods = existing.item[existing.item.find("("):]
                            item_name = f"{item_name} {mods}"
                            display = f"{formatted_size}{item_name}".strip()
                            result_info["mods_carried"] = mods
                        result_info["combo_converted_from"] = existing.item
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            order_state.pop(i)
                        logger.info("Combo conversion: removed standalone '%s' for combo '%s'", existing.item, item_name)
                        break

            # ── Post-bundle absorption: side/drink fills an incomplete bundle's slot(s) ──
            if not is_bundle:
                component = menu.infer_combo_component(item_name)
                if component in ("sides", "drinks"):
                    # PR #184 round 2 (Rick's review, item 2): capacity/fill is now derived
                    # directly from each bundle unit's own slot state (one slot per physical unit
                    # of quantity, see `_sync_bundle_slot_list`), not a session-wide counter --
                    # repeatedly ask `_find_bundle_slot` for the next vacant slot (across every
                    # instance and every unit) and fill it, until either *quantity* is exhausted
                    # or no vacant slot remains -- no capacity-minus-filled arithmetic left to
                    # drift out of sync with reality across two combos or a quantity-N line.
                    absorbed_count = 0
                    any_resize = False
                    last_combo_item = None
                    remaining = quantity
                    while remaining > 0:
                        found = self._find_bundle_slot(order_state, menu, component)
                        if found is None:
                            break
                        combo_item, idx = found
                        # #179/PR #184 round 2: `_fill_bundle_component` owns the display
                        # rebuild AND pure-function pricing. This slot is "available" (just
                        # vacated by a `remove`, or never filled), but its `last_item` may still
                        # be set from BEFORE that vacate -- refilling it with that SAME item
                        # reports as a resize, never the free "included with your combo" wording
                        # a genuinely fresh fill gets (pricing itself is identical either way for
                        # an "includedAnySize" pack -- see `_fill_bundle_component`).
                        accepted, is_resize, filled_combo_item = self._fill_bundle_component(order_state, combo_item, idx, menu, component, item_name, size, display)
                        if not accepted:
                            # Rejected (item D/M4): this pack has no whole-meal price at *size*
                            # -- the slot is still vacant, never silently retry the SAME vacant
                            # slot forever. Stop absorbing; any remaining quantity falls through
                            # to a genuine standalone add below.
                            break
                        combo_item = filled_combo_item
                        last_combo_item = combo_item
                        any_resize = any_resize or is_resize
                        absorbed_count += 1
                        remaining -= 1
                    if last_combo_item is not None:
                        if any_resize:
                            result_info["resized_combo_component"] = component
                            result_info["combo_component_resized_to_size"] = size
                            result_info["combo_display"] = last_combo_item.display
                        else:
                            result_info["absorbed_into_combo"] = True
                            result_info["absorbed_component"] = component
                            result_info["absorbed_display"] = display
                        component_upcharge = self._component_upcharge(menu, item_name, size)
                        if component_upcharge > 0:
                            result_info["combo_component_upcharge"] = component_upcharge
                            result_info["combo_component_upcharge_display"] = format_money(component_upcharge)
                        logger.info("Post-combo absorption: '%s' absorbed as combo %s", display, component)
                    if remaining <= 0 and absorbed_count > 0:
                        self._update_summary(session_id)
                        return result_info
                    if absorbed_count == 0:
                        # No vacant slot anywhere -- check whether some slot is already full
                        # with THIS SAME item at a DIFFERENT size (e.g. the combo's drink is a
                        # Medium cola and the model calls `add` for a Large cola).
                        # Resolve this as an in-place RESIZE of the slot (identical pricing to
                        # the explicit `modify` action below) instead of falling through to
                        # "Regular add" and creating a silent duplicate standalone line -- the
                        # exact #179 bug. Only ONE unit of *quantity* is ever a resize (there is
                        # only one matching slot); any remainder still becomes a genuine
                        # standalone add.
                        found = self._find_bundle_slot(
                            order_state, menu, component, item_name=item_name, target_size=size
                        )
                        if found is not None:
                            combo_item, idx = found
                            slots = self._sync_bundle_slot_list(combo_item, component)
                            slot = slots[idx]
                            current_size = slot.get("size", "")
                            current_item = slot.get("item", "")
                            if (
                                current_size
                                and current_size != size
                                and current_item
                                and _menu_key(current_item) == _menu_key(item_name)
                            ):
                                accepted, _, combo_item = self._fill_bundle_component(order_state, combo_item, idx, menu, component, item_name, size, display)
                                if accepted:
                                    result_info["resized_combo_component"] = component
                                    result_info["combo_component_resized_from_size"] = current_size
                                    result_info["combo_component_resized_to_size"] = size
                                    result_info["combo_display"] = combo_item.display
                                    component_upcharge = self._component_upcharge(menu, item_name, size)
                                    if component_upcharge > 0:
                                        result_info["combo_component_upcharge"] = component_upcharge
                                        result_info["combo_component_upcharge_display"] = format_money(component_upcharge)
                                    logger.info(
                                        "Resize-via-add: '%s' resized combo %s slot from '%s' to '%s' (session=%s)",
                                        item_name, component, current_size, size, session_id,
                                    )
                                    remaining = quantity - 1
                                    if remaining <= 0:
                                        self._update_summary(session_id)
                                        return result_info
                    quantity = remaining

            # ── Regular add / bundle-instance creation ──
            if is_bundle:
                # PR #184 round 2 (Rick's review, item 2 -- "quantity-2 combos handled
                # correctly"): a bundle line merges into an existing line for the SAME item+size
                # exactly like a standalone item (unchanged, pre-existing behavior -- e.g. "two
                # medium Cheeseburger Combos" stays ONE order line with quantity=2), but each
                # physical unit of that quantity gets its OWN independent side/drink slot (see
                # `_sync_bundle_slot_list`) -- that's what makes a quantity-N combo line and
                # "two combos" (two separate lines, e.g. different items or sizes) both
                # well-defined and mutually independent.
                existing_item_index = next(
                    (index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size),
                    -1
                )
                if existing_item_index != -1:
                    bundle_item_ref = order_state[existing_item_index]
                    bundle_item_ref.quantity += quantity
                    new_units = quantity
                    logger.debug("Updated quantity for %s in session %s", display, session_id)
                else:
                    bundle_item_ref = OrderItem(item=item_name, size=size, quantity=quantity, price=price, display=display)
                    order_state.append(bundle_item_ref)
                    new_units = quantity
                    logger.debug("Added %s to session %s", display, session_id)

                # Grow each own component's slot list to the new quantity (fresh empty records
                # for the newly added units) before the pivot/autofill below fills them.
                for component in own_bundle_slots:
                    self._sync_bundle_slot_list(bundle_item_ref, component)

                # ── Bundle pivot: absorb standalone sides/drinks into the NEWLY ADDED unit(s) ──
                for _ in range(new_units):
                    absorbed_side = False
                    absorbed_drink = False
                    items_to_remove = []
                    for i, existing in enumerate(order_state):
                        if existing is bundle_item_ref:
                            continue  # skip the bundle itself
                        component = menu.infer_combo_component(existing.item)
                        # Only absorb a component this bundle's OWN slots actually include
                        # (Rick's PR #99 review, decision 1) -- e.g. French Toast Sticks Combo
                        # (drinks-only) must never free-absorb a pre-existing standalone side.
                        if component == "sides" and "sides" in own_bundle_slots and not absorbed_side:
                            slot_idx = next(
                                (i2 for i2, s in enumerate(bundle_item_ref._bundle_slots["sides"]) if not s.get("item")),
                                None,
                            )
                            if slot_idx is not None:
                                logger.info("Absorbing '%s' into new bundle '%s'", existing.display, item_name)
                                # #77: record what this bundle actually absorbed on its OWN order
                                # line (design doc section 3.3 row 22, "components on the wire") --
                                # additive, never replaces the existing display-string rebuild
                                # elsewhere.
                                bundle_item_ref.components.append(existing.display)
                                _, _, bundle_item_ref = self._fill_bundle_component(order_state, bundle_item_ref, slot_idx, menu, "sides", existing.item, existing.size, existing.display)
                                if existing.quantity > 1:
                                    existing.quantity -= 1
                                else:
                                    items_to_remove.append(i)
                                absorbed_side = True
                        elif component == "drinks" and "drinks" in own_bundle_slots and not absorbed_drink:
                            slot_idx = next(
                                (i2 for i2, s in enumerate(bundle_item_ref._bundle_slots["drinks"]) if not s.get("item")),
                                None,
                            )
                            if slot_idx is not None:
                                logger.info("Absorbing '%s' into new bundle '%s'", existing.display, item_name)
                                bundle_item_ref.components.append(existing.display)
                                _, _, bundle_item_ref = self._fill_bundle_component(order_state, bundle_item_ref, slot_idx, menu, "drinks", existing.item, existing.size, existing.display)
                                if existing.quantity > 1:
                                    existing.quantity -= 1
                                else:
                                    items_to_remove.append(i)
                                absorbed_drink = True
                    for idx in reversed(items_to_remove):
                        order_state.pop(idx)

                    # ── #77: bundle-slot auto-fill (design doc section 3.3 row 19) -- a slot
                    # this item's OWN pack opts into filling by default (menu.bundle_autofill,
                    # keyed off menu.schema.json's ``bundle.autoFill``) that WASN'T just absorbed
                    # above from a pre-existing standalone item gets its default filler the
                    # instant the bundle is added -- e.g. a numbered meal's side defaulting to
                    # "Medium Fries" with no follow-up add call needed.
                    size_label = menu.normalize_size(size) or ""
                    autofill = menu.bundle_autofill(item_name, resolved_size_label=size_label)
                    # PR #184 round 3 (Rick's review, item E): `slot["item"]` must be the BASE,
                    # on-menu item name (e.g. "World Famous Fries®"), not the size-baked-in
                    # template text `autofill` itself holds -- see `bundle_autofill_names` and
                    # `_apply_whole_bundle_resize`'s own autofill re-derivation for the same fix.
                    autofill_base = menu.bundle_autofill_names(item_name)
                    if "sides" in autofill and not absorbed_side:
                        slot_idx = next(
                            (i2 for i2, s in enumerate(bundle_item_ref._bundle_slots["sides"]) if not s.get("item")),
                            None,
                        )
                        if slot_idx is not None:
                            filler_display = autofill["sides"]
                            filler_item = autofill_base.get("sides", filler_display)
                            bundle_item_ref.components.append(filler_display)
                            _, _, bundle_item_ref = self._fill_bundle_component(
                                order_state, bundle_item_ref, slot_idx, menu, "sides", filler_item, size, filler_display, autofill=True,
                            )
                            result_info["autofilled"] = result_info.get("autofilled", []) + [filler_display]
                    if "drinks" in autofill and not absorbed_drink:
                        slot_idx = next(
                            (i2 for i2, s in enumerate(bundle_item_ref._bundle_slots["drinks"]) if not s.get("item")),
                            None,
                        )
                        if slot_idx is not None:
                            filler_display = autofill["drinks"]
                            filler_item = autofill_base.get("drinks", filler_display)
                            bundle_item_ref.components.append(filler_display)
                            _, _, bundle_item_ref = self._fill_bundle_component(
                                order_state, bundle_item_ref, slot_idx, menu, "drinks", filler_item, size, filler_display, autofill=True,
                            )
                            result_info["autofilled"] = result_info.get("autofilled", []) + [filler_display]
            else:
                existing_item_index = next(
                    (index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size),
                    -1
                )
                if existing_item_index != -1:
                    order_state[existing_item_index].quantity += quantity
                    logger.debug("Updated quantity for %s in session %s", display, session_id)
                else:
                    order_state.append(OrderItem(item=item_name, size=size, quantity=quantity, price=price, display=display))
                    logger.debug("Added %s to session %s", display, session_id)

        elif action == "modify":
            # #77 (docs/persona-architecture.md section 3.3 row 21, "Resize in place"): change an
            # existing order line's SIZE without a separate remove+re-add -- shared engine code,
            # available whenever a persona's own tool instructions invoke it (see tools.py's
            # `update_order_tool_schema`). Finds the first existing line for *item_name* at ANY
            # size (the guest doesn't say the old size out loud -- "make that a large" -- only
            # the item and the new size).
            existing_item_index = next(
                (index for index, order_item in enumerate(order_state) if order_item.item == item_name),
                -1
            )
            if existing_item_index != -1:
                target = order_state[existing_item_index]
                old_size = target.size
                old_display = target.display
                # PR #184 round 2 (Rick's review, item 1 -- e.g. "make it a large meal"):
                # directly modifying a bundle's OWN line on a "wholeBundleSize" pack resizes the
                # WHOLE bundle -- its own size/price AND every filled slot's display together --
                # via `_apply_whole_bundle_resize`, never a bare size/price/display overwrite
                # (which would silently drop the already-absorbed "w/ <side> & <drink>" suffix).
                if menu.bundle_resize_rule == "wholeBundleSize" and menu.bundle_slots(target.item):
                    if self._apply_whole_bundle_resize(target, menu, size):
                        result_info["modified_from_size"] = old_size
                        result_info["modified_to_size"] = size
                        result_info["combo_display"] = target.display
                        logger.info(
                            "Modified whole bundle '%s' from '%s' to '%s' in session %s",
                            item_name, old_size, size, session_id,
                        )
                    else:
                        logger.info(
                            "Modify requested for whole bundle '%s' to size '%s' in session %s -- no-op "
                            "(already that size, or pack has no price at that size)",
                            item_name, size, session_id,
                        )
                else:
                    target.size = size
                    target.price = price
                    target.display = display
                    if target._bundle_slots:
                        # Re-derive the "w/ <side> & <drink>" suffix the plain display assignment
                        # above just overwrote -- resizing a non-"wholeBundleSize" bundle's own
                        # line doesn't change what's filling its slots.
                        self._rebuild_bundle_display(target, menu)
                        self._reprice_bundle_from_components(target, menu)
                    result_info["modified_from_size"] = old_size
                    result_info["modified_to_size"] = size
                    logger.info(
                        "Modified '%s' from '%s' to '%s' in session %s", item_name, old_display, display, session_id,
                    )
            else:
                # #179/PR #184 round 2: *item_name* isn't a raw order line, but it may still be a
                # combo's side or drink filling a slot via absorption -- the guest saying "make
                # that a large" about the drink that came WITH their combo. Resize that slot in
                # place with the same pure-function pricing the explicit remove-then-add and the
                # resize-via-add paths above use, instead of rejecting a perfectly resizable,
                # real part of the order as `not_in_order` just because it has no raw line.
                resized = False
                for component in ("sides", "drinks"):
                    found = self._find_bundle_slot(
                        order_state, menu, component, item_name=item_name, target_size=size
                    )
                    if found is None:
                        continue
                    combo_item, idx = found
                    slots = self._sync_bundle_slot_list(combo_item, component)
                    slot = slots[idx]
                    if slot.get("item") and _menu_key(slot["item"]) == _menu_key(item_name):
                        old_component_size = slot.get("size", "")
                        accepted, _, combo_item = self._fill_bundle_component(order_state, combo_item, idx, menu, component, item_name, size, display)
                        if accepted:
                            result_info["resized_combo_component"] = component
                            result_info["combo_component_resized_from_size"] = old_component_size
                            result_info["combo_component_resized_to_size"] = size
                            result_info["combo_display"] = combo_item.display
                            component_upcharge = self._component_upcharge(menu, item_name, size)
                            if component_upcharge > 0:
                                result_info["combo_component_upcharge"] = component_upcharge
                                result_info["combo_component_upcharge_display"] = format_money(component_upcharge)
                            logger.info(
                                "Modified combo %s slot '%s' from '%s' to '%s' in session %s",
                                component, item_name, old_component_size, size, session_id,
                            )
                        else:
                            # PR #184 round 3 (Rick's review, item D/M4): this pack has no
                            # whole-meal price at *size* -- reject cleanly (nothing was mutated)
                            # rather than leave the slot relabeled to a size the bundle itself
                            # never actually moved to. Round 4 item 3: also carry the bundle's own
                            # name/current size so tools.py can build an accurate, non-misleading
                            # rejection message instead of reading only the component name.
                            result_info["combo_component_resize_rejected"] = component
                            result_info["combo_component_resize_rejected_bundle"] = combo_item.item
                            result_info["combo_component_resize_rejected_bundle_size"] = combo_item.size
                            logger.info(
                                "Modify requested for combo %s slot '%s' to size '%s' in session %s -- "
                                "rejected (no whole-meal price at that size)",
                                component, item_name, size, session_id,
                            )
                        resized = True
                        break
                if not resized:
                    logger.warning(
                        "Modify requested for '%s' but it isn't in the order for session %s -- no-op",
                        item_name, session_id,
                    )

        elif action == "remove":
            existing_item_index = next((index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size), -1)
            if existing_item_index != -1:
                if order_state[existing_item_index].quantity > quantity:
                    order_state[existing_item_index].quantity -= quantity
                    logger.debug("Decreased quantity for %s in session %s", display, session_id)
                else:
                    order_state.pop(existing_item_index)
                    logger.debug("Removed %s from session %s", display, session_id)
            else:
                # #179/PR #184 round 2: *item_name* may be the exact size (`order_item.size ==
                # size` just failed above) -- or, like the live bug report, currently filling a
                # combo's side/drink slot via absorption, which has no raw line to match AT ALL
                # regardless of size (the model's own "remove cola Medium" call). Vacate
                # that INSTANCE's slot (determinism-aware -- two combos vacate independently)
                # instead of silently no-op'ing, so that combo goes back to incomplete and a
                # follow-up `add` refills it (reprising as a resize -- see
                # `_fill_bundle_component`) rather than creating a duplicate standalone line.
                for component in ("sides", "drinks"):
                    vacate_info = self._vacate_bundle_component(order_state, menu, component, item_name)
                    if vacate_info is not None:
                        result_info.update(vacate_info)
                        logger.info(
                            "Vacated combo %s slot ('%s') in session %s", component, item_name, session_id,
                        )
                        break

        self._update_summary(session_id)
        return result_info

    def get_order_summary(self, session_id: str) -> OrderSummary:
        self._check_owner(session_id)
        return self.sessions[session_id]["order_summary"]

    def get_order_items(self, session_id: str) -> list:
        """Return raw order item list — avoids Pydantic overhead for validation checks."""
        self._check_owner(session_id)
        return self.sessions[session_id]["order_state"]

    def get_combo_requirements(self, session_id: str) -> dict:
        """Scans the order for bundles (combos, Dinners, Wacky Packs, the Meal) and returns
        missing components. Helps the AI know exactly what to ask for next."""
        self._check_owner(session_id)
        session = self.sessions[session_id]
        order_items = session["order_state"]
        menu = self._menu_for(session)

        # PR #184 round 2 (Rick's review, item 2): completeness is now determined PER BUNDLE
        # INSTANCE and PER PHYSICAL UNIT from its own `_bundle_slots` state (a quantity-N line
        # has N independent slots per component -- see `_sync_bundle_slot_list`), not a
        # session-wide capacity/fill counter -- two combos (one fully built, one still missing
        # its drink), or two units of one quantity-2 line, report independently instead of
        # netting out against each other's counts.
        missing_components = set()
        for item in order_items:
            for component in menu.bundle_slots(item.item):
                slots = self._sync_bundle_slot_list(item, component)
                if any(not slot.get("item") for slot in slots):
                    missing_components.add(component)

        # #77 (design doc section 3.3 row 18, "missingPartText"): each pack's own wording for what
        # to ask the guest for next -- the default persona's own text is unchanged ("a side
        # (fries or tots)" / "a drink or slush"); every other pack supplies its own via
        # ``bundle.missingPartText`` in persona.json. Falls back to a generic phrase so a pack
        # that doesn't set this at all never crashes or reads as blank.
        missing = []
        if "sides" in missing_components:
            missing.append(menu.bundle_missing_part_text.get("sides", "a side"))
        if "drinks" in missing_components:
            missing.append(menu.bundle_missing_part_text.get("drinks", "a drink"))

        return {
            "is_complete": len(missing) == 0,
            "missing_items": missing,
            "prompt_hint": f"Ask the guest for {', and '.join(missing)} to finish their combo." if missing else ""
        }

    def get_grouped_order_for_readback(self, session_id: str) -> str:
        """
        Groups items with the same display name for a natural voice read-back.
        Example: 'Two Medium Cherry Limeades and one Footlong Quarter Pound Coney.'

        #304: now a thin per-session wrapper around the already-cached ``OrderSummary.
        spokenReadBack`` (computed by ``_update_summary``'s call to the shared
        ``_compose_spoken_readback`` -- the exact same composition, not re-derived here, so this
        and ``get_order``'s ``spokenReadBack`` can never drift out of sync with each other.
        """
        self._check_owner(session_id)
        return self.sessions[session_id]["order_summary"].spokenReadBack

    def reset_order(self, session_id: str):
        """Clears all items and per-session order state from the current session's order (#41)."""
        self._check_owner(session_id)
        session = self.sessions[session_id]
        self._reset_order_state(session)
        self._update_summary(session_id)
        logger.info("Order fully reset for session %s", session_id)

    def get_order_summary_json(self, session_id: str) -> str:
        """Return cached JSON string — avoids repeated Pydantic serialization."""
        self._check_owner(session_id)
        return self.sessions[session_id]["order_summary_json"]

    def get_session_identifiers(self, session_id: str) -> SessionIdentifiers:
        self._check_owner(session_id)
        session = self.sessions[session_id]
        return SessionIdentifiers(
            session_token=session["session_token"],
            round_trip_index=session["round_trip_index"],
            round_trip_token=session["round_trip_token"],
            persona_id=session["_persona_id"],
            model_id=session["_model_id"],
            pipeline=session["_model_pipeline"],
        )

    def advance_round_trip(self, session_id: str) -> SessionIdentifiers:
        self._check_owner(session_id)
        session = self.sessions[session_id]
        session["round_trip_index"] += 1
        session["round_trip_token"] = self._format_round_trip_token(
            session["session_token"], session["round_trip_index"]
        )
        logger.debug(
            "Round trip %s recorded for session %s", session["round_trip_index"], session_id
        )
        return self.get_session_identifiers(session_id)

    def get_persona_id(self, session_id: str) -> str:
        """The persona id this session is bound to -- every session has one (#74; the deployment
        default when none was explicitly requested). Falls back to the default persona's own id
        for a *session_id* that isn't a live session at all -- defensive, since callers like
        ``session_manager.py``'s resume mismatch check may probe an id that has already
        expired/ended, and there is no other, unbound notion of persona to fall back to."""
        if session_id not in self.sessions:
            return default_persona.get_default_persona().id
        self._check_owner(session_id)
        return self.sessions[session_id]["_persona_id"]

    def get_model_id(self, session_id: str) -> str:
        """The realtime model id this session is bound to -- every session has one (#75; the
        bound persona's own ``models.realtime.default`` when none was explicitly requested via
        ``?model=``). Falls back to the deployment default persona's own realtime default for a
        *session_id* that isn't a live session at all -- same defensive fallback as
        ``get_persona_id`` above, for ``session_manager.py``'s resume mismatch check on an
        already-expired id."""
        if session_id not in self.sessions:
            return default_persona.get_default_persona().manifest.models.realtime.default
        self._check_owner(session_id)
        return self.sessions[session_id]["_model_id"]

    def get_model_deployment(self, session_id: str) -> str | None:
        """This session's own bound realtime model's Foundry deployment name (#75). ``None``
        only for a session created without any model info at all (e.g. some non-WS test call
        sites) or for a *session_id* that isn't a live session at all -- the caller falls back
        to ``RTMiddleTier.deployment`` in that case. A live WebSocket session's value here is
        never ``None`` (Rick's PR #106 review item 1: every model, including a persona's own
        default, always resolves a deployment -- the deployment map or, only for the default,
        ``AZURE_OPENAI_REALTIME_DEPLOYMENT`` with a logged warning)."""
        if session_id not in self.sessions:
            return None
        self._check_owner(session_id)
        return self.sessions[session_id]["_model_deployment"]

    def get_model_reasoning(self, session_id: str) -> bool | None:
        """Whether this session's own bound realtime model is a reasoning model, per the shared
        model catalog (#75, Rick's PR #106 review item 1 -- reasoning is ALWAYS resolved from
        the catalog, including for a persona's own default model, so a live WebSocket session's
        value here is never ``None``). ``None`` only for a session created without any model
        info at all (e.g. some non-WS test call sites) or for a *session_id* that isn't a live
        session at all -- the caller falls back to the process-wide name-heuristic/config
        decision in that case."""
        if session_id not in self.sessions:
            return None
        self._check_owner(session_id)
        return self.sessions[session_id]["_model_reasoning"]

    def get_model_pipeline(self, session_id: str) -> str:
        """The pipeline (``"realtime"`` | ``"cascade"``) this session's bound
        model belongs to (#75, Rick's PR #106 review item 3) -- every live session has one
        (``dispatch_processor`` resolves it before the session is ever created). Falls back to
        ``"realtime"`` for a *session_id* that isn't a live session at all -- same defensive
        fallback as ``get_persona_id``/``get_model_id`` above, for
        ``session_manager.py``'s resume mismatch check on an already-expired id, and the only
        pipeline that exists before #82/#81 register a processor."""
        if session_id not in self.sessions:
            return "realtime"
        self._check_owner(session_id)
        return self.sessions[session_id]["_model_pipeline"]

    def get_persona_default_model_id(self, session_id: str) -> str:
        """This session's own bound persona's declared ``models.realtime.default`` (#75 bug
        fix, this revision, PR #106 review) -- captured once at ``create_session`` time straight
        off the concrete ``Persona`` object, so ``session_manager.py``'s resume mismatch check
        (an omitted ``?model=`` on resume means "this session's bound persona's own default")
        never needs a persona catalog lookup of its own: there is no reliable one to reach for
        here (the process-wide ``default_persona`` catalog only resolves the real, always-
        enabled persona packs -- a session bound via some OTHER catalog, e.g. a caller's own
        fixture/test catalog, has no entry there at all). Falls back to the deployment default
        persona's own realtime default for a *session_id* that isn't a live session at all --
        same defensive fallback as ``get_persona_id``/``get_model_id`` above."""
        if session_id not in self.sessions:
            return default_persona.get_default_persona().manifest.models.realtime.default
        self._check_owner(session_id)
        return self.sessions[session_id]["_persona_default_model_id"]

    def get_menu_catalog(self, session_id: str):
        """Public, session-scoped counterpart of ``_menu_for`` for callers outside this module
        (``tools.py``) that need this session's own persona-bound menu resolution (#74). Falls
        back to the default persona's own catalog for an unknown/expired session id (unbound
        sessions no longer exist)."""
        if session_id not in self.sessions:
            return default_persona.get_default_menu_catalog()
        self._check_owner(session_id)
        return self._menu_for(self.sessions[session_id])

    def set_machine_override(self, session_id: str, machine: str, status: str) -> bool:
        if session_id not in self.sessions:
            return False
        self._check_owner(session_id)
        # #309, S5: a non-string `machine` (e.g. a malformed/malicious client frame sending
        # `"machine": []` or `"machine": {}`) must be rejected cleanly here, BEFORE the
        # `machine not in self._menu_for(session).machines` membership test below -- `machines`
        # is a dict, and `in` against a dict raises TypeError ("unhashable type") for a
        # list/dict key rather than returning False, which would otherwise propagate uncaught
        # into rtmt.py's message-processing loop (only `(json.JSONDecodeError, KeyError)` is
        # caught there) and crash the client relay for the whole session. Mirrors how an
        # unknown/invalid machine name is already rejected below: log a WARNING, store nothing,
        # return False -- never raise.
        if not isinstance(machine, str) or not isinstance(status, str):
            logger.warning(
                "Dropped set_machine_override with non-string machine/status (machine=%r, status=%r) for session %s",
                machine, status, session_id,
            )
            return False
        if status not in ("up", "down"):
            return False
        session = self.sessions[session_id]
        if machine not in self._menu_for(session).machines:
            return False
        session["_machine_overrides"][machine] = status
        return True

    def effective_machine_status(self, session_id: str, machine: str) -> str | None:
        if session_id not in self.sessions:
            return default_persona.get_default_menu_catalog().machine_status(machine)
        self._check_owner(session_id)
        session = self.sessions[session_id]
        override = session["_machine_overrides"].get(machine)
        if override is not None:
            return override
        return self._menu_for(session).machine_status(machine)

    def get_menu_mode(self, session_id: str) -> str | None:
        """This session's own bound daypart (``"breakfast"`` | ``"lunch"``), or ``None`` for a
        persona that doesn't declare ``features.dayparts`` (#165) -- resolved once at
        ``create_session`` and fixed for the life of the session, exactly like
        ``get_persona_id``/``get_model_id`` above. Falls back to ``None`` for a *session_id* that
        isn't a live session at all (same defensive fallback as every other getter here) -- a
        caller must treat ``None`` as "no mode gate applies", never as "breakfast or lunch, TBD"."""
        if session_id not in self.sessions:
            return None
        self._check_owner(session_id)
        return self.sessions[session_id].get("_menu_mode")

    def is_happy_hour_for_session(self, session_id: str) -> bool:
        """Public, session-scoped counterpart of ``_is_happy_hour_for`` for callers outside this
        module (``tools.py``) that need this session's own happy-hour status (#74). Falls back to
        the shared module-level ``is_happy_hour()`` (the default persona's own happy-hour window)
        for an unknown/expired session id."""
        if session_id not in self.sessions:
            return is_happy_hour()
        self._check_owner(session_id)
        return self._is_happy_hour_for(self.sessions[session_id])

    def set_happy_hour_mode(self, session_id: str, mode: str) -> bool:
        if session_id not in self.sessions:
            return False
        self._check_owner(session_id)
        # #309, S5: a non-string `mode` (e.g. a malformed/malicious client frame sending
        # `"mode": []`) must be rejected exactly like any other invalid mode below -- never
        # propagate an uncaught TypeError up into rtmt.py's message-processing loop. `mode not
        # in ("auto", "on", "off")` below already handles this safely for a non-string (a list
        # is simply never equal to any of the three strings), so this guard only exists to make
        # that safety explicit and to mirror the symmetric guard in set_machine_override.
        if not isinstance(mode, str):
            logger.warning("Dropped set_happy_hour_mode with non-string mode %r for session %s", mode, session_id)
            return False
        session = self.sessions[session_id]
        if session.get("_happy_hour_window") is None or mode not in ("auto", "on", "off"):
            return False
        session["_happy_hour_mode"] = mode
        # #309 (R2): the mode change must be reflected in `order_summary_json` IMMEDIATELY, not
        # only on the next (unrelated) order mutation. `get_order` returns this cached JSON
        # verbatim while separately computing the happy-hour BANNER live
        # (`get_happy_hour_banner_for_session`) -- without this call the two could disagree
        # (e.g. banner says "HAPPY HOUR" but the cached total doesn't reflect the discount yet,
        # or vice versa) until some other, unrelated order change happened to refresh the cache.
        self._update_summary(session_id)
        return True

    def get_happy_hour_mode(self, session_id: str) -> str:
        if session_id not in self.sessions:
            return "auto"
        self._check_owner(session_id)
        return self.sessions[session_id].get("_happy_hour_mode", "auto")

    def get_happy_hour_banner_for_session(self, session_id: str) -> str:
        """The happy-hour note ``tools.py`` appends for THIS session.

        Returns a note only when happy hour is active, this persona announces it, and at least
        one current raw order line is actually being multiplied by the happy-hour price. Bundle
        components do not appear in ``order_state`` as raw lines, so they are naturally excluded
        from both pricing and this discount claim.
        """
        if session_id not in self.sessions:
            return ""
        self._check_owner(session_id)
        session = self.sessions[session_id]
        if session["_happy_hour_announce"]:
            discounted_lines = self._happy_hour_discounted_line_displays(session)
            if discounted_lines:
                return (
                    f" {session['_happy_hour_banner']} "
                    f"[HAPPY HOUR DISCOUNT APPLIED TO: {', '.join(discounted_lines)}]"
                )
        return ""

# Create a singleton instance of OrderState
order_state_singleton = OrderState()