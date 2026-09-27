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
from money_utils import format_money, to_decimal

if TYPE_CHECKING:
    from persona_loader import Persona

__all__ = ["OrderState", "SessionIdentifiers", "order_state_singleton", "is_happy_hour"]

logger = logging.getLogger("order_state")


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


@dataclass
class SessionIdentifiers:
    session_token: str
    round_trip_index: int
    round_trip_token: str
    # #74 (Rick's PR #102 review, item 4): the persona this session is bound to -- every
    # session has one (the deployment default when none was requested), so this is never None.
    persona_id: str


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
        """Clear every per-session order-state field (#41): the order lines themselves plus the
        combo-absorption bookkeeping (counts *and* display strings). ``create_session`` and
        ``reset_order`` both delegate here so they can never drift out of sync again — the
        original bug was ``reset_order`` clearing the absorbed counts but not the absorbed
        *display* strings, so a fresh combo's display after reset still showed the previous
        order's absorbed component names.
        """
        session["order_state"] = []
        session["absorbed_sides"] = 0
        session["absorbed_drinks"] = 0
        session["absorbed_side_display"] = ""
        session["absorbed_drink_display"] = ""

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
        summary = OrderSummary(
            items=order_items,
            total=float(total),
            tax=float(tax),
            finalTotal=float(finalTotal),
            totalDisplay=format_money(total),
            taxDisplay=format_money(tax),
            finalTotalDisplay=format_money(finalTotal),
        )
        session["order_summary"] = summary
        # Cache the JSON representation to avoid repeated Pydantic serialization
        session["order_summary_json"] = summary.model_dump_json()
        logger.debug("Order summary updated for session %s (items=%d, total=%s)", session_id, len(order_items), finalTotal)

    def create_session(self, persona: "Persona | None" = None) -> str:
        """Create a new, empty order-state session.

        *persona* (#74, Rick's PR #102 review item 2): the session is bound to *persona*, or to
        the deployment-wide default (``default_persona.get_default_persona()``) when omitted --
        every session has exactly one bound persona, through the same ``MenuCatalog``/pricing
        path either way. There is no unbound-session state and no module-level brand-only
        fallback.
        """
        persona = persona or default_persona.get_default_persona()
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
            "_menu": get_catalog_for_persona(persona),
            "_tz": ZoneInfo(persona.manifest.store.timezone),
            "_tax_rate": to_decimal(persona.manifest.pricing.taxRate),
            "_happy_hour_discount": (
                to_decimal(happy_hour_cfg.priceMultiplier) if happy_hour_cfg is not None else to_decimal("1")
            ),
            "_happy_hour_window": (
                (happy_hour_cfg.startHour, happy_hour_cfg.endHour) if happy_hour_cfg is not None else None
            ),
        }
        self._reset_order_state(self.sessions[session_id])
        logger.info("Session created: %s (persona=%s)", session_id, persona.id)
        return session_id

    def delete_session(self, session_id: str) -> None:
        if session_id not in self.sessions:
            return
        self._check_owner(session_id)
        if self.sessions.pop(session_id, None) is not None:
            logger.info("Session deleted: %s", session_id)

    def _format_round_trip_token(self, session_token: str, round_trip_index: int) -> str:
        return f"{session_token}-{round_trip_index:04d}"

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

        if action == "add":
            # #104: the unit price charged is always this persona's own menu price for
            # (item_name, size) -- never the tool call's own `price` argument. The model can
            # invent a price, carry one over from the wrong size, or pre-apply a discount; since
            # #73 every accepted item already resolved through the on-menu gate in tools.py, so
            # menu.price_for() (menu_utils.MenuCatalog) is the single source of truth for what a
            # guest is charged. This is the ONE place that source of truth is applied -- callers
            # that build an order directly (tests, a future admin tool) get the same guarantee as
            # the realtime tool-call path, instead of a second, easy-to-forget copy of this check
            # in tools.py. The tool-call `price` is only ever used for the debug comparison below;
            # see docs/persona-architecture.md section 6.
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
                # test call), or a pack with a missing price. Fall back to the caller-supplied
                # price rather than silently charging $0.
                logger.warning(
                    "No menu price found for '%s' size '%s'; falling back to the caller-supplied "
                    "price $%.2f (session=%s)",
                    item_name, size, price, session_id,
                )

            is_combo = "combo" in item_name.lower()
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
                combo_base = _menu_key(item_name).replace(" combo", "").strip()
                for i, existing in enumerate(order_state):
                    if "combo" in existing.item.lower():
                        continue  # skip other combos
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

            # ── Post-bundle absorption: side/drink fills an incomplete bundle's slot ──
            if not is_bundle:
                component = menu.infer_combo_component(item_name)
                if component in ("sides", "drinks"):
                    # Capacity for this component = sum of quantities of every bundle item
                    # already in the order whose OWN bundle_slots include this component (Rick's
                    # PR #99 review, decision 1) -- not one slot per "combo"-named item regardless
                    # of what it actually bundles. A drinks-only bundle (French Toast Sticks
                    # Combo, the Crispy Tenders Dinners) contributes 0 to side capacity; a
                    # side+drink bundle (a regular Combo, a Wacky Pack, the $6 Meal) contributes 1
                    # to each.
                    bundle_capacity = sum(
                        it.quantity for it in order_state if component in menu.bundle_slots(it.item)
                    )
                    if bundle_capacity > 0:
                        if component == "sides":
                            filled = sum(it.quantity for it in order_state if menu.infer_combo_component(it.item) == "sides")
                            filled += session.get("absorbed_sides", 0)
                        else:
                            filled = sum(it.quantity for it in order_state if menu.infer_combo_component(it.item) == "drinks")
                            filled += session.get("absorbed_drinks", 0)

                        slots_available = bundle_capacity - filled
                        if slots_available > 0:
                            to_absorb = min(quantity, slots_available)
                            if component == "sides":
                                session["absorbed_sides"] += to_absorb
                            else:
                                session["absorbed_drinks"] += to_absorb
                            remaining = quantity - to_absorb
                            result_info["absorbed_into_combo"] = True
                            result_info["absorbed_component"] = component
                            result_info["absorbed_display"] = display

                            # Update the bundle item's display to show the absorbed component --
                            # find a bundle item whose own slots actually include this component
                            # (not just any "combo"-named item).
                            for combo_item in order_state:
                                if component in menu.bundle_slots(combo_item.item):
                                    # Build component list from absorbed sides/drinks
                                    components = []
                                    if session.get("absorbed_side_display"):
                                        components.append(session["absorbed_side_display"])
                                    if session.get("absorbed_drink_display"):
                                        components.append(session["absorbed_drink_display"])
                                    # Store current component display for future reference
                                    if component == "sides":
                                        session["absorbed_side_display"] = display
                                        if display not in components:
                                            components.append(display)
                                    else:
                                        session["absorbed_drink_display"] = display
                                        if display not in components:
                                            components.append(display)
                                    # Rebuild combo display with components
                                    # Strip existing mods from base_name to avoid duplication
                                    raw_name = combo_item.item
                                    if "(" in raw_name:
                                        base_name = raw_name[:raw_name.find("(")].strip()
                                        mods = " " + raw_name[raw_name.find("("):]
                                    else:
                                        base_name = raw_name
                                        mods = ""
                                    combo_item.display = f"{base_name}{mods} w/ {' & '.join(components)}"
                                    break

                            logger.info("Post-combo absorption: '%s' absorbed as combo %s", display, component)
                            if remaining <= 0:
                                self._update_summary(session_id)
                                return result_info
                            else:
                                quantity = remaining

            # ── Regular add ──
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

            # ── Bundle pivot: absorb standalone sides/drinks into a newly added bundle ──
            if is_bundle:
                absorbed_side = False
                absorbed_drink = False
                items_to_remove = []
                for i, existing in enumerate(order_state):
                    if existing.item == item_name:
                        continue  # skip the bundle itself
                    component = menu.infer_combo_component(existing.item)
                    # Only absorb a component this bundle's OWN slots actually include (Rick's PR
                    # #99 review, decision 1) -- e.g. French Toast Sticks Combo (drinks-only) must
                    # never free-absorb a pre-existing standalone side.
                    if component == "sides" and "sides" in own_bundle_slots and not absorbed_side:
                        logger.info("Absorbing '%s' into new bundle '%s'", existing.display, item_name)
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            items_to_remove.append(i)
                        absorbed_side = True
                    elif component == "drinks" and "drinks" in own_bundle_slots and not absorbed_drink:
                        logger.info("Absorbing '%s' into new bundle '%s'", existing.display, item_name)
                        if existing.quantity > 1:
                            existing.quantity -= 1
                        else:
                            items_to_remove.append(i)
                        absorbed_drink = True
                for idx in reversed(items_to_remove):
                    order_state.pop(idx)
                if absorbed_side:
                    session["absorbed_sides"] += 1
                if absorbed_drink:
                    session["absorbed_drinks"] += 1

        elif action == "remove":
            existing_item_index = next((index for index, order_item in enumerate(order_state) if order_item.item == item_name and order_item.size == size), -1)
            if existing_item_index != -1:
                if order_state[existing_item_index].quantity > quantity:
                    order_state[existing_item_index].quantity -= quantity
                    logger.debug("Decreased quantity for %s in session %s", display, session_id)
                else:
                    order_state.pop(existing_item_index)
                    logger.debug("Removed %s from session %s", display, session_id)

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

        # Rick's PR #99 review, decision 1: per-component capacity is the sum over bundle items
        # whose OWN bundle_slots include that component -- a drinks-only bundle (French Toast
        # Sticks Combo, a Crispy Tenders Dinner) contributes 0 to side capacity, not 1.
        side_capacity = sum(item.quantity for item in order_items if "sides" in menu.bundle_slots(item.item))
        drink_capacity = sum(item.quantity for item in order_items if "drinks" in menu.bundle_slots(item.item))
        side_count = sum(item.quantity for item in order_items if menu.infer_combo_component(item.item) == "sides")
        drink_count = sum(item.quantity for item in order_items if menu.infer_combo_component(item.item) in ("drinks",))

        # Include sides/drinks absorbed into a bundle during the bundle pivot
        side_count += session.get("absorbed_sides", 0)
        drink_count += session.get("absorbed_drinks", 0)

        missing = []
        if side_count < side_capacity:
            missing.append("a side (fries or tots)")
        if drink_count < drink_capacity:
            missing.append("a drink or slush")

        return {
            "is_complete": len(missing) == 0,
            "missing_items": missing,
            "prompt_hint": f"Ask the guest for {', and '.join(missing)} to finish their combo." if missing else ""
        }

    def get_grouped_order_for_readback(self, session_id: str) -> str:
        """
        Groups items with the same display name for a natural voice read-back.
        Example: 'Two Medium Cherry Limeades and one Footlong Quarter Pound Coney.'
        """
        self._check_owner(session_id)
        session = self.sessions[session_id]
        items = session["order_state"]
        if not items:
            return "Your order is currently empty."

        # Aggregate quantities by display name
        counts = {}
        for oi in items:
            # #74: every session is bound to a persona (the default when none was requested), so
            # readback always speaks that persona's OWN size vocabulary (``sizes.spokenAs``) via
            # its MenuCatalog -- there is no separate, hardcoded "RT 44"/"RT44" -> "Route 44"
            # substitution path anymore (that substitution is now simply the default persona's own
            # pack data, reached through the exact same ``.spoken()`` call every persona uses).
            clean_name = session["_menu"].spoken(oi.display)
            # Convert parenthesized mods to speech-friendly format
            # e.g. "Sonic Cheeseburger (No Lettuce)" -> "Sonic Cheeseburger with no lettuce"
            if "(" in clean_name and ")" in clean_name:
                clean_name = clean_name.replace("(", "with ").replace(")", "")
            counts[clean_name] = counts.get(clean_name, 0) + oi.quantity

        # Build the natural language string
        parts = []
        for display, qty in counts.items():
            prefix = f"{qty} " if qty > 1 else "one "
            parts.append(f"{prefix}{display}")

        if len(parts) > 1:
            summary_str = ", ".join(parts[:-1]) + f", and {parts[-1]}"
        else:
            summary_str = parts[0]

        # #47/PR #50 follow-up: read the already-computed finalTotalDisplay directly instead of
        # re-deriving it with format_money(finalTotal) -- there must be exactly one place that
        # turns the exact Decimal total into a "$0.00" string, so every spoken/displayed money
        # surface can never drift out of sync with another.
        return f"I have {summary_str}. Your total is {session['order_summary'].finalTotalDisplay}. "

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

    def get_menu_catalog(self, session_id: str):
        """Public, session-scoped counterpart of ``_menu_for`` for callers outside this module
        (``tools.py``) that need this session's own persona-bound menu resolution (#74). Falls
        back to the default persona's own catalog for an unknown/expired session id (unbound
        sessions no longer exist)."""
        if session_id not in self.sessions:
            return default_persona.get_default_menu_catalog()
        self._check_owner(session_id)
        return self._menu_for(self.sessions[session_id])

    def is_happy_hour_for_session(self, session_id: str) -> bool:
        """Public, session-scoped counterpart of ``_is_happy_hour_for`` for callers outside this
        module (``tools.py``) that need this session's own happy-hour status (#74). Falls back to
        the shared module-level ``is_happy_hour()`` (the default persona's own happy-hour window)
        for an unknown/expired session id."""
        if session_id not in self.sessions:
            return is_happy_hour()
        self._check_owner(session_id)
        return self._is_happy_hour_for(self.sessions[session_id])

# Create a singleton instance of OrderState
order_state_singleton = OrderState()