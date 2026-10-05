from pydantic import BaseModel, PrivateAttr, model_validator

from money_utils import format_money, format_money_spoken

__all__ = ["OrderItem", "OrderSummary"]


class OrderItem(BaseModel):
    item: str
    size: str
    quantity: int
    price: float
    display: str
    # #77: additive, defaults to [] for every persona -- the bundle-slot component(s) absorbed
    # into (or auto-filled onto) this line, e.g. ["Medium Fries", "Coca-Cola"] for a meal/combo.
    # Every OTHER field on this model stays exactly as-is for an item that bundles nothing (an
    # a-la-carte item's ``components`` is simply []); the wire ticket renders it only when present
    # (docs/persona-architecture.md section 3.3 row 22, tests/conformance/README.md wire schema).
    components: list[str] = []
    # Optional, index-aligned with ``components``. A value > 0 means the component is included only
    # up to the pack's declared included size and this slot adds the listed per-unit upcharge.
    componentUpcharges: list[float] = []

    # PR #184 round 2 (Rick's review, item 2/3): per-INSTANCE, per-PHYSICAL-UNIT bundle slot-fill
    # state -- keyed by component ("sides"/"drinks") -> a LIST of per-unit slot records
    # ({"item", "size", "display", "last_item", "last_size", "autofill"}), one entry per unit of
    # `quantity` this line represents (a "2 Big Mac Meals" line has 2 independent side slots and
    # 2 independent drink slots -- see order_state.py's `_sync_bundle_slot_list`). This replaces
    # the old session-level flat `absorbed_{side,drink}_*` fields order_state.py used to keep,
    # which is what let two combos in the same order (or a combo removed without a full
    # `reset_order`) bleed slot state into each other or into a brand-new combo, and a quantity-N
    # combo line have no way to track which physical unit was resized. Living on the OrderItem
    # instance itself means this state is destroyed automatically the moment that line is removed
    # from the order -- no extra cleanup code needed for Rick's requirement 3.
    # "last_item"/"last_size" deliberately SURVIVE a vacate of the same item (see
    # order_state.py's `_fill_bundle_component`) purely so a refill can be reported as a "resize"
    # rather than a fresh "included with your combo" absorption; they drive wording only, never
    # pricing (pricing is a pure function of current state -- see `_fill_bundle_component`).
    # A PrivateAttr is never part of model_dump()/JSON serialization, so the `items[].components`
    # wire contract (tests/conformance/README.md "Order-summary wire schema") is unaffected.
    _bundle_slots: dict[str, list[dict[str, str]]] = PrivateAttr(default_factory=dict)


class OrderSummary(BaseModel):
    items: list[OrderItem]
    total: float
    tax: float
    finalTotal: float
    # #47: exact, contract-rounded display strings (money_utils.format_money) computed from the
    # same Decimal values as total/tax/finalTotal, before any float conversion. Additive -- the
    # plain numeric fields above are unchanged and still present -- so the frontend ticket can
    # render these strings directly instead of re-rounding an already-noisy float with `.toFixed`.
    # Left as "" (rather than required) so existing call sites that only pass the numeric fields
    # keep working; the validator below fills them in from the numeric fields when omitted.
    totalDisplay: str = ""
    taxDisplay: str = ""
    finalTotalDisplay: str = ""
    # #304: server-composed, voice-ready order read-back -- every line (quantity, size, and
    # spoken item name) followed by "Your total is ...". Computed once per OrderState._update_summary
    # call (order_state.py's `_compose_spoken_readback`) from the SAME grouping/spoken-name logic
    # `get_grouped_order_for_readback` already used, so the two can never drift. Every persona's
    # system prompt (#304) instructs the model to call `get_order` and speak this field VERBATIM
    # before the final total, after any change, and when the guest says they're done -- never a
    # total-only reply. Left as "" (never required) for the same backward-compatible reason
    # totalDisplay/taxDisplay/finalTotalDisplay are: existing call sites that build an
    # OrderSummary with only the numeric/display fields keep working unchanged.
    spokenReadBack: str = ""
    # #313 (Rick's review, item 6): the same finalTotal, spoken out in words (money_utils
    # .format_money_spoken) -- the model-facing `update_order` delta text used to append the
    # digit/`$`-formatted finalTotalDisplay ahead of the voice read-back, giving the realtime
    # model TWO different renderings of the same total in one tool result (a `$9.71` followed
    # moments later by "nine dollars and seventy-one cents") and risking it speaking the wrong
    # one. tools.py's update_order now builds its delta text from this field instead. Left as
    # "" (never required) for the same backward-compatible reason the other *Display fields
    # are: existing call sites that only pass the numeric fields keep working unchanged.
    finalTotalSpoken: str = ""

    @model_validator(mode="after")
    def _fill_display_defaults(self) -> "OrderSummary":
        if not self.totalDisplay:
            self.totalDisplay = format_money(self.total)
        if not self.taxDisplay:
            self.taxDisplay = format_money(self.tax)
        if not self.finalTotalDisplay:
            self.finalTotalDisplay = format_money(self.finalTotal)
        if not self.finalTotalSpoken:
            self.finalTotalSpoken = format_money_spoken(self.finalTotal)
        return self