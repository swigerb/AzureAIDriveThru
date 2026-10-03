import { render, screen, within } from "@testing-library/react";
import OrderSummary, { calculateOrderSummary, formatMoney, OrderItem, OrderSummaryProps } from "../order-summary";

describe("OrderSummary", () => {
    const sampleItems: OrderItem[] = [
        { item: "SuperSONIC® Double Cheeseburger", size: "standard", quantity: 2, price: 6.99, display: "SuperSONIC® Double Cheeseburger" },
        { item: "Large Tots", size: "standard", quantity: 1, price: 3.29, display: "Large Tots" }
    ];

    it("renders order items with the correct totals", () => {
        const summary = calculateOrderSummary(sampleItems);
        render(<OrderSummary order={summary} />);

        // react-i18next is globally mocked (test/setup.ts) to echo the key itself, so the ticket
        // title now asserts on the i18n key ("ticket.title") rather than its pack-supplied English
        // value (the branded ticket title) -- issue #80 F3.
        expect(screen.getByText("ticket.title")).toBeInTheDocument();
        expect(screen.getByText(/SuperSONIC® Double Cheeseburger/)).toBeInTheDocument();
        expect(screen.getByText(/Large Tots/)).toBeInTheDocument();
        expect(screen.getByText(`$${summary.total.toFixed(2)}`)).toBeInTheDocument();
        expect(screen.getByText(`$${summary.finalTotal.toFixed(2)}`)).toBeInTheDocument();
    });

    it("shows the empty-state helper when no items are present", () => {
        const emptySummary: OrderSummaryProps = { items: [], total: 0, tax: 0, finalTotal: 0 };
        render(<OrderSummary order={emptySummary} />);

        // react-i18next is globally mocked (test/setup.ts) to echo the key itself -- issue #80 F3
        // (Rick's PR-110 review item 1): this text is now the neutral `ticket.emptyHint` key, with
        // the pack's own flavor text ("Add a slush, burger, or shake...") living in
        // personas/sonic/persona.json's ui.strings instead of the shared component.
        expect(screen.getByText("ticket.emptyHint")).toBeInTheDocument();
    });

    // #47: Rick's two repro values are distinct IEEE-754 doubles that both mean the same exact
    // decimal ($88.045, which rounds up to $88.05 per the ROUND_HALF_UP contract). Plain
    // `.toFixed(2)` renders them inconsistently ($88.04 vs $88.05); formatMoney must not.
    it.each([["88.04499999999999", 88.04499999999999], ["88.045", 88.045]])(
        "renders %s as $88.05 via formatMoney",
        (_label, value) => {
            expect(formatMoney(value)).toBe("$88.05");
        }
    );

    it("renders both of Rick's repro totals identically as $88.05 with no backend display string", () => {
        const values = [88.04499999999999, 88.045];
        for (const finalTotal of values) {
            const summary: OrderSummaryProps = { items: [], total: finalTotal, tax: 0, finalTotal };
            const { unmount } = render(<OrderSummary order={summary} />);
            expect(screen.getAllByText("$88.05")).toHaveLength(2); // Subtotal row + Total Due row
            unmount();
        }
    });

    // PR #50 review (should-fix 2, Rick): formatMoney must round half-cents *up*, matching the
    // backend's ROUND_HALF_UP contract. Plain `.toFixed(2)` on the noisy double under-rounds
    // 5.265 to $5.26 (its true double value is 5.264999999999999...) and 1.005 to $1.00.
    it.each([
        ["5.265", 5.265, "$5.27"],
        ["1.005", 1.005, "$1.01"]
    ])("renders %s as %s via formatMoney (round half-cent up, not down)", (_label, value, expected) => {
        expect(formatMoney(value)).toBe(expected);
    });

    it("prefers the backend-supplied *Display strings over recomputing from the numeric fields", () => {
        // Even if the numeric `finalTotal` would format differently on its own, the backend's
        // exact-Decimal-derived display string is the single source of truth and must win.
        const summary: OrderSummaryProps = {
            items: [],
            total: 5.265,
            tax: 0,
            finalTotal: 5.265,
            totalDisplay: "$5.27",
            taxDisplay: "$0.00",
            finalTotalDisplay: "$5.27"
        };
        render(<OrderSummary order={summary} />);

        expect(screen.getAllByText("$5.27")).toHaveLength(2); // Subtotal row + Total Due row
        expect(screen.getByText("$0.00")).toBeInTheDocument();
    });

    it("uses formatMoney (not raw .toFixed) for per-item line prices", () => {
        const items: OrderItem[] = [
            { item: "Route 44 Drink", size: "route44", quantity: 1, price: 88.04499999999999, display: "Route 44 Drink" }
        ];
        const summary: OrderSummaryProps = { items, total: 88.04499999999999, tax: 0, finalTotal: 88.04499999999999 };
        render(<OrderSummary order={summary} />);

        // line item + Subtotal + Total Due all agree, since no *Display strings were supplied.
        expect(screen.getAllByText("$88.05")).toHaveLength(3);
    });

    // #77/#80 F5: the ticket renders a bundle/combo/meal's `components` (design doc §9 row F5,
    // tests/conformance/README.md's "Order-summary wire schema") as included sub-lines under the
    // parent line -- they're already priced into the parent's own `price`, never their own priced
    // or removable row.
    describe("bundle components (issue #80 F5)", () => {
        it("renders each absorbed/auto-filled component under its combo/meal line, marked included", () => {
            const items: OrderItem[] = [
                {
                    item: "Cheeseburger Combo",
                    size: "standard",
                    quantity: 1,
                    price: 9.19,
                    display: "Cheeseburger Combo",
                    components: ["Medium World Famous Fries", "Medium Coca-Cola®"]
                }
            ];
            const summary: OrderSummaryProps = { items, total: 9.19, tax: 0, finalTotal: 9.19 };
            render(<OrderSummary order={summary} />);

            expect(screen.getByText("Cheeseburger Combo")).toBeInTheDocument();
            expect(screen.getByText("Medium World Famous Fries")).toBeInTheDocument();
            expect(screen.getByText("Medium Coca-Cola®")).toBeInTheDocument();
            // react-i18next is mocked (test/setup.ts) to echo the key -- "ticket.included" is the
            // real English value ("Included") once i18next itself renders it for real.
            expect(screen.getAllByText("ticket.included")).toHaveLength(2);
        });

        it("renders a component upcharge instead of Included when provided", () => {
            const items: OrderItem[] = [
                {
                    item: "SuperSONIC® Double Cheeseburger Combo",
                    size: "standard",
                    quantity: 1,
                    price: 10.69,
                    display: "SuperSONIC® Double Cheeseburger Combo",
                    components: ["Medium Tots", "Large Cherry Limeade"],
                    componentUpcharges: [0, 0.5]
                }
            ];
            const summary: OrderSummaryProps = { items, total: 10.69, tax: 0, finalTotal: 10.69 };
            render(<OrderSummary order={summary} />);

            expect(screen.getByText("Medium Tots")).toBeInTheDocument();
            expect(screen.getByText("Large Cherry Limeade")).toBeInTheDocument();
            expect(screen.getByText("ticket.included")).toBeInTheDocument();
            expect(screen.getByText("+$0.50")).toBeInTheDocument();
        });

        it("does not render a components list for an a-la-carte item with none", () => {
            const items: OrderItem[] = [{ item: "Large Tots", size: "standard", quantity: 1, price: 3.29, display: "Large Tots" }];
            const summary: OrderSummaryProps = { items, total: 3.29, tax: 0, finalTotal: 3.29 };
            render(<OrderSummary order={summary} />);

            expect(screen.queryByText("ticket.included")).not.toBeInTheDocument();
            expect(screen.queryByRole("list")).not.toBeInTheDocument();
        });

        it("renders the components sub-line as an accessible list, one <li> per component", () => {
            const items: OrderItem[] = [
                {
                    item: "$6 Meal",
                    size: "standard",
                    quantity: 1,
                    price: 6,
                    display: "$6 Meal",
                    components: ["Medium Fries", "Coca-Cola"]
                }
            ];
            const summary: OrderSummaryProps = { items, total: 6, tax: 0, finalTotal: 6 };
            render(<OrderSummary order={summary} />);

            const list = screen.getByRole("list");
            expect(list).toHaveAccessibleName();
            expect(within(list).getAllByRole("listitem")).toHaveLength(2);
        });

        it("keeps rendering an empty components array the same as no components at all", () => {
            const items: OrderItem[] = [
                { item: "Mozzarella Sticks", size: "standard", quantity: 1, price: 3.49, display: "Mozzarella Sticks", components: [] }
            ];
            const summary: OrderSummaryProps = { items, total: 3.49, tax: 0, finalTotal: 3.49 };
            render(<OrderSummary order={summary} />);

            expect(screen.queryByRole("list")).not.toBeInTheDocument();
        });

        // Rick's PR 134 review nit: for a quantity > 1 line, each component sub-line shows the
        // same "(xN)" the parent line's own display already carries, since each meal's worth of
        // the combo brings its own copy of the component.
        it("shows the parent line's quantity on each included sub-line when quantity > 1", () => {
            const items: OrderItem[] = [
                {
                    item: "Cheeseburger Combo",
                    size: "standard",
                    quantity: 2,
                    price: 9.19,
                    display: "Cheeseburger Combo",
                    components: ["Medium World Famous Fries", "Medium Coca-Cola®"]
                }
            ];
            const summary: OrderSummaryProps = { items, total: 18.38, tax: 0, finalTotal: 18.38 };
            render(<OrderSummary order={summary} />);

            expect(screen.getByText(/Medium World Famous Fries \(x2\)/)).toBeInTheDocument();
            expect(screen.getByText(/Medium Coca-Cola® \(x2\)/)).toBeInTheDocument();
        });

        it("omits the quantity suffix on included sub-lines when quantity is 1", () => {
            const items: OrderItem[] = [
                {
                    item: "Cheeseburger Combo",
                    size: "standard",
                    quantity: 1,
                    price: 9.19,
                    display: "Cheeseburger Combo",
                    components: ["Medium World Famous Fries"]
                }
            ];
            const summary: OrderSummaryProps = { items, total: 9.19, tax: 0, finalTotal: 9.19 };
            render(<OrderSummary order={summary} />);

            expect(screen.queryByText(/Medium World Famous Fries \(x/)).not.toBeInTheDocument();
        });
    });

    // Issue 164 E2: "Tax (N%)" instead of a bare "Tax" -- N comes from the `taxRate` prop
    // (persona.json's pricing.taxRate, forwarded by both backends), not a hardcoded rate.
    describe("tax rate label (issue 164 E2)", () => {
        it("appends the rounded whole-percent rate when taxRate is supplied", () => {
            const summary = calculateOrderSummary(sampleItems);
            render(<OrderSummary order={summary} taxRate="0.08" />);

            // react-i18next is mocked to echo the key ("ticket.tax"); the rate suffix is appended
            // by OrderSummary itself, not the i18n layer.
            expect(screen.getByText("ticket.tax (8%)")).toBeInTheDocument();
        });

        it("rounds a non-whole-percent rate to the nearest whole percent", () => {
            const summary = calculateOrderSummary(sampleItems);
            render(<OrderSummary order={summary} taxRate="0.0825" />);

            expect(screen.getByText("ticket.tax (8%)")).toBeInTheDocument();
        });

        it("falls back to the bare label when taxRate is omitted", () => {
            const summary = calculateOrderSummary(sampleItems);
            render(<OrderSummary order={summary} />);

            expect(screen.getByText("ticket.tax")).toBeInTheDocument();
            expect(screen.queryByText(/ticket\.tax \(/)).not.toBeInTheDocument();
        });

        it("falls back to the bare label when taxRate is not a valid number", () => {
            const summary = calculateOrderSummary(sampleItems);
            render(<OrderSummary order={summary} taxRate="not-a-number" />);

            expect(screen.getByText("ticket.tax")).toBeInTheDocument();
            expect(screen.queryByText(/ticket\.tax \(/)).not.toBeInTheDocument();
        });
    });
});
