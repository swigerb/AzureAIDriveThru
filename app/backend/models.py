from pydantic import BaseModel, model_validator

from money_utils import format_money

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

    @model_validator(mode="after")
    def _fill_display_defaults(self) -> "OrderSummary":
        if not self.totalDisplay:
            self.totalDisplay = format_money(self.total)
        if not self.taxDisplay:
            self.taxDisplay = format_money(self.tax)
        if not self.finalTotalDisplay:
            self.finalTotalDisplay = format_money(self.finalTotal)
        return self