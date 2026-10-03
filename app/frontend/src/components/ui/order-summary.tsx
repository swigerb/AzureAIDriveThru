import { useState, memo } from "react";
import { ChevronDown, ChevronUp } from "lucide-react";
import { useTranslation } from "react-i18next";

export interface OrderItem {
    item: string;
    size: string;
    quantity: number;
    price: number;
    display: string;
    // #77/#80 F5: bundle-slot item(s) absorbed into (or auto-filled onto) this line, e.g.
    // ["Medium Fries", "Coca-Cola"] for a combo/meal (tests/conformance/README.md's
    // "Order-summary wire schema"). Optional and defaults to undefined/empty for an a-la-carte
    // line, which renders exactly as it always has -- these are already priced into this line's
    // own `price` (they aren't separate, removable, or separately priced order lines), so the
    // ticket shows each as an included, $0 sub-line rather than its own priced row.
    components?: string[];
}

export interface OrderSummaryProps {
    items: OrderItem[];
    total: number;
    tax: number;
    finalTotal: number;
    // #47: optional, additive display strings the backend computes with the exact-decimal
    // ROUND_HALF_UP contract rule (app/backend/money_utils.py::format_money) before any float
    // conversion happens. When present these are the single source of truth for what the ticket
    // shows -- prefer them over re-rounding the numeric fields with `.toFixed`, which cannot tell
    // apart e.g. 88.04499999999999 from 88.045 (both meant to be exactly $88.045) once the value
    // has already degraded into a noisy IEEE-754 double.
    totalDisplay?: string;
    taxDisplay?: string;
    finalTotalDisplay?: string;
}

/**
 * Formats a money value as an exact "$0.00" string, rounding half-cents *up* (away from zero),
 * matching the backend's ROUND_HALF_UP contract (README "Rendering money for display";
 * app/backend/money_utils.py::format_money) -- and robust to the floating-point noise that plain
 * `.toFixed(2)` alone cannot correct (#47/PR #50 review).
 *
 * Two problems, one fix:
 *   1. `.toFixed(2)` rounds directly off the noisy IEEE-754 double, so a value that is
 *      mathematically exactly on a half cent (e.g. `5.265`) can render one cent short (`$5.26`)
 *      because its true double value is `5.264999999999999...`, not `5.265` (Rick's repro).
 *   2. Two doubles that both represent the same intended decimal (e.g. `88.04499999999999` and
 *      `88.045`, both meant to be $88.045) can render two different cents if rounded to cents
 *      directly off the raw double.
 *
 * Rounding to whole cents via `(value * 100).toFixed(6)` first collapses the double's noise (which
 * only ever appears well past the 6th decimal place for these magnitudes) back to a clean number
 * of cents, then `Math.round` performs the half-up rounding itself (`Math.round` always rounds
 * .5 up, including for negative-adjacent-to-zero inputs at these magnitudes) before dividing back
 * down to dollars.
 *
 * This is a client-side safety net for values that never went through the backend's exact-Decimal
 * pipeline (the dummy-data preview and each line item's `price * quantity`). Whenever the backend
 * has already supplied a `*Display` string (see `OrderSummaryProps`), prefer that string instead —
 * it's derived from the exact Decimal before any float conversion at all, which is strictly more
 * reliable than any client-side float correction can be.
 */
export function formatMoney(value: number): string {
    const cents = Math.round(Number((value * 100).toFixed(6)));
    return `$${(cents / 100).toFixed(2)}`;
}

export function calculateOrderSummary(items: OrderItem[]): OrderSummaryProps {
    const total = items.reduce((sum, item) => sum + item.price * item.quantity, 0);
    const tax = total * 0.08; // 8% tax
    const finalTotal = total + tax;

    return {
        items,
        total,
        tax,
        finalTotal,
        totalDisplay: formatMoney(total),
        taxDisplay: formatMoney(tax),
        finalTotalDisplay: formatMoney(finalTotal)
    };
}

const OrderItemRow = memo(function OrderItemRow({ item }: { item: OrderItem }) {
    const { t } = useTranslation();
    const components = item.components ?? [];

    return (
        <div className="rounded-2xl bg-white/70 px-3 py-2 text-sm text-gray-700 shadow-xs dark:bg-white/5 dark:text-white">
            <div className="flex justify-between">
                <span className="font-semibold">
                    {item.display} {item.quantity > 1 && `(x${item.quantity})`}
                </span>
                <span className="font-mono text-brand-primary dark:text-brand-primary-tint">{formatMoney(item.price * item.quantity)}</span>
            </div>
            {/* #77/#80 F5: a combo/meal's absorbed or auto-filled sides/drinks render as included
                sub-lines under the parent line, not as their own priced (or removable) rows --
                they're already paid for by the parent line's own price above. A <ul> (rather than
                more <div>s) gives screen readers the "N items" / list-item semantics for free.
                Rick's PR 134 review nit: for a quantity > 1 line, each component is one meal's
                worth per the parent line's own quantity -- the same "(xN)" the parent line's own
                display already carries above -- rather than rendering the component name once as
                if only a single meal's worth of it were included. */}
            {components.length > 0 && (
                <ul className="mt-1 space-y-0.5 pl-4" aria-label={t("ticket.componentsLabel")}>
                    {components.map((component, index) => (
                        <li key={`${component}-${index}`} className="flex justify-between text-xs text-gray-500 dark:text-white/70">
                            <span>
                                {component} {item.quantity > 1 && `(x${item.quantity})`}
                            </span>
                            <span className="font-mono italic">{t("ticket.included")}</span>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
});

export default memo(function OrderSummary({ order, taxRate }: { order: OrderSummaryProps; taxRate?: string }) {
    const { t } = useTranslation();
    const [isExpanded, setIsExpanded] = useState(true);
    const { items, total, tax, finalTotal, totalDisplay, taxDisplay, finalTotalDisplay } = order;
    // Issue 164 E2: "Tax (N%)" instead of a bare "Tax" -- N comes from the persona's own
    // `taxRate` (persona.json's pricing.taxRate, forwarded by both backends), rounded to the
    // nearest whole percent the same way every original persona's ticket displayed it. A missing
    // or invalid `taxRate` prop renders the plain "Tax" label rather than "Tax (NaN%)".
    const taxRatePercent = taxRate !== undefined ? Number.parseFloat(taxRate) : Number.NaN;
    const taxLabel = Number.isFinite(taxRatePercent) ? `${t("ticket.tax")} (${Math.round(taxRatePercent * 100)}%)` : t("ticket.tax");

    return (
        <div
            className="rounded-3xl border border-brand-secondary/20 bg-linear-to-br from-white via-brand-surface-tint to-brand-accent/5 p-5 shadow-[0_20px_45px_var(--brand-secondary-veil-12)] dark:border-white/15 dark:bg-linear-to-br dark:from-brand-surface-dark dark:via-brand-surface-dark-alt dark:to-brand-surface-dark"
            data-testid="order-ticket"
        >
            <div className="mb-4 flex items-center justify-between">
                <div>
                    <p className="text-xs font-bold uppercase tracking-[0.3em] text-brand-primary dark:text-brand-primary-tint">{t("ticket.kicker")}</p>
                    <h2 className="text-2xl font-black text-brand-primary dark:text-brand-primary-tint">{t("ticket.title")}</h2>
                </div>
                <button onClick={() => setIsExpanded(!isExpanded)} className="flex items-center text-sm text-gray-500 dark:text-gray-300 md:hidden">
                    {isExpanded ? (
                        <>
                            Less <ChevronUp className="ml-1 h-4 w-4" />
                        </>
                    ) : (
                        <>
                            More <ChevronDown className="ml-1 h-4 w-4" />
                        </>
                    )}
                </button>
            </div>
            <div className={`space-y-2 ${isExpanded ? "block" : "hidden md:block"}`}>
                {items.length === 0 && <p className="text-sm text-muted-foreground dark:text-white/70">{t("ticket.emptyHint")}</p>}
                {items.map((item, index) => (
                    <OrderItemRow key={index} item={item} />
                ))}

                <div className="mt-4 space-y-2 border-t border-dashed border-primary/30 pt-4 dark:border-white/15">
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>Subtotal</span>
                        <span className="font-mono dark:text-white/90">{totalDisplay ?? formatMoney(total)}</span>
                    </div>
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>{taxLabel}</span>
                        <span className="font-mono dark:text-white/90">{taxDisplay ?? formatMoney(tax)}</span>
                    </div>
                </div>
            </div>
            <div className="mt-4 flex items-center justify-between rounded-2xl bg-white/90 px-4 py-3 text-lg font-semibold text-primary shadow-inner dark:bg-brand-surface-dark-alt dark:text-brand-primary-tint">
                <span>Total Due</span>
                <span className="font-mono text-brand-primary dark:text-brand-primary-tint">{finalTotalDisplay ?? formatMoney(finalTotal)}</span>
            </div>
        </div>
    );
});
