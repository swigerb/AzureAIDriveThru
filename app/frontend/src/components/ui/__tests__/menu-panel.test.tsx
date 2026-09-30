import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Issue #80 F4 (design doc §5.2, §9 row F4): the menu panel now fetches `current.menuUrl` instead
// of reading a bundled `src/data/menuItems.json` copy. `usePersonaContext` is mocked here so each
// test controls `current.menuUrl` directly without spinning up a full PersonaProvider fetch chain.

const context = vi.hoisted(() => ({ menuUrl: "/personas/test-alpha/menu.json?v=test" }));
vi.mock("@/context/persona-context", () => ({
    usePersonaContext: () => ({ current: { menuUrl: context.menuUrl } as PersonaDetail })
}));

function mockFetchOnce(body: unknown, ok = true, status = 200) {
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({
            ok,
            status,
            json: async () => body
        }))
    );
}

afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
});

const SAMPLE_MENU = {
    menuItems: [
        {
            category: "Burgers & Sandwiches",
            items: [{ name: "Test Burger", sizes: [{ size: "standard", price: 4.99 }], description: "A fixture burger." }]
        }
    ]
};

describe("MenuPanel", () => {
    it("shows a loading state before the fetch resolves", () => {
        vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {}))); // never resolves
        render(<MenuPanel />);
        expect(screen.getByText("menu.loading")).toBeInTheDocument();
    });

    it("fetches current.menuUrl and renders the returned categories/items", async () => {
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(fetch).toHaveBeenCalledWith(context.menuUrl);
        expect(screen.getByText("A fixture burger.")).toBeInTheDocument();
        expect(screen.getByText("$4.99")).toBeInTheDocument();
        expect(screen.getByText("Burgers & Sandwiches")).toBeInTheDocument();
    });

    it("renders an alert when the fetch responds with a non-ok status", async () => {
        mockFetchOnce(null, false, 500);
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument());
        expect(screen.getByRole("alert")).toHaveTextContent("menu.loadError");
    });

    it("renders an alert when the fetch itself rejects (network failure)", async () => {
        vi.stubGlobal(
            "fetch",
            vi.fn(async () => {
                throw new Error("network down");
            })
        );
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument());
    });

    it("toggles a category's items open and closed", async () => {
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);
        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());

        const toggle = screen.getByRole("button", { name: /Burgers & Sandwiches/ });
        expect(toggle).toHaveAttribute("aria-expanded", "true");

        act(() => fireEvent.click(toggle));
        expect(toggle).toHaveAttribute("aria-expanded", "false");
    });

    // Non-blocking item from Rick's PR-110 review: singular/plural count label ("1 item", not "1 items").
    it("uses the singular 'item' label for a one-item category", async () => {
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("1 item")).toBeInTheDocument();
        expect(screen.queryByText("1 items")).not.toBeInTheDocument();
    });

    it("uses the plural 'items' label for a multi-item category", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        { name: "Test Burger", sizes: [{ size: "standard", price: 4.99 }], description: "A fixture burger." },
                        { name: "Test Fries", sizes: [{ size: "standard", price: 2.49 }], description: "A fixture side." }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("2 items")).toBeInTheDocument();
    });

    // Issue #119 (owner follow-up comment): category icons must come from the pack's own menu
    // data, not a hardcoded pack-keyed lookup table that silently fell back to a generic icon
    // for every other persona's categories.
    it("renders a category's own icon when the pack's menu data supplies one", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Iced Coffee",
                    icon: "☕",
                    items: [{ name: "Test Latte", sizes: [{ size: "standard", price: 3.49 }], description: "A fixture latte." }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Latte")).toBeInTheDocument());
        expect(screen.getByText("☕")).toBeInTheDocument();
    });

    it("falls back to the shared neutral icon when a category has none", async () => {
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("🍹")).toBeInTheDocument();
    });

    // issue 165: a single-size item from a pack that also supplies `calories` (the existing
    // menu-fidelity signal) never shows a size label, regardless of the size name's
    // capitalization -- matching the target reference card design.
    it("shows no size label for a single-size item with calories, even when the size name is capitalized", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        { name: "Test Burger", sizes: [{ size: "Standard", price: 4.99 }], description: "A fixture burger.", calories: 540 }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("$4.99")).toBeInTheDocument();
        expect(screen.queryByText(/Standard:/)).not.toBeInTheDocument();
    });

    // issue 165: some existing packs never supply `calories` on their menu items, so a
    // single-size item from a pack that doesn't declare calories must keep its "Standard:"
    // label exactly as it renders today -- this is the regression this issue must not
    // introduce for those packs.
    it("keeps the size label for a single-size item from a pack without calories (today's non-calorie packs)", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [{ name: "Test Burger", sizes: [{ size: "Standard", price: 4.99 }], description: "A fixture burger." }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("$4.99")).toBeInTheDocument();
        expect(screen.getByText("Standard:")).toBeInTheDocument();
    });

    it("still shows per-size labels for a genuinely multi-size item", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Sides & Drinks",
                    items: [
                        {
                            name: "Test Fries",
                            sizes: [
                                { size: "Small", price: 2.49 },
                                { size: "Medium", price: 2.99 },
                                { size: "Large", price: 3.49 }
                            ],
                            description: "A fixture side."
                        }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Fries")).toBeInTheDocument());
        expect(screen.getByText("Small:")).toBeInTheDocument();
        expect(screen.getByText("Medium:")).toBeInTheDocument();
        expect(screen.getByText("Large:")).toBeInTheDocument();
    });

    // #165: calorie line, shown only when the pack's item data supplies `calories`.
    it("renders a calorie line only for an item that has one", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        { name: "Test Burger", sizes: [{ size: "Standard", price: 4.99 }], description: "A fixture burger.", calories: 540 },
                        { name: "Test Salad", sizes: [{ size: "Standard", price: 3.99 }], description: "A fixture salad." }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Burger")).toBeInTheDocument());
        expect(screen.getByText("540 Cal")).toBeInTheDocument();
        expect(screen.getByText("Test Salad")).toBeInTheDocument();
        expect(screen.queryByText("Cal", { exact: false })).toHaveTextContent("540 Cal"); // only the one item's line exists
    });

    // #165: `mealNumber` items are pulled into a synthesized "Extra Value Meals" category shown
    // first, sorted by meal number, and removed from their original data category.
    it("synthesizes an Extra Value Meals category from mealNumber items, sorted and shown first", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        { name: "Combo Two", sizes: [{ size: "Standard", price: 8.99 }], description: "Second combo.", mealNumber: "2" },
                        { name: "Plain Burger", sizes: [{ size: "Standard", price: 4.99 }], description: "No meal number." }
                    ]
                },
                {
                    category: "Chicken",
                    items: [
                        { name: "Combo One", sizes: [{ size: "Standard", price: 7.99 }], description: "First combo.", mealNumber: "1" },
                        { name: "Plain Nuggets", sizes: [{ size: "Standard", price: 3.99 }], description: "No meal number." }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Extra Value Meals")).toBeInTheDocument());
        // The virtual category renders first, ahead of every real data category.
        const headings = screen.getAllByRole("heading", { level: 3 }).map(h => h.textContent);
        expect(headings).toEqual(["Extra Value Meals", "Burgers & Sandwiches", "Chicken"]);
        // Sorted by meal number ascending, not by the order items appeared in the source data.
        expect(screen.getByText("1")).toBeInTheDocument();
        expect(screen.getByText("2")).toBeInTheDocument();
        // Plain (non-mealNumber) item stays in its own data category.
        expect(screen.getByText("Plain Burger")).toBeInTheDocument();
        expect(screen.getByText("Plain Nuggets")).toBeInTheDocument();
    });

    // #165: a data category whose every item was pulled into Extra Value Meals is dropped
    // entirely -- never rendered as an empty shell.
    it("drops a data category entirely once every one of its items is pulled into Extra Value Meals", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Combos Only",
                    items: [{ name: "Combo One", sizes: [{ size: "Standard", price: 7.99 }], description: "Only combo.", mealNumber: "1" }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Extra Value Meals")).toBeInTheDocument());
        expect(screen.queryByText("Combos Only")).not.toBeInTheDocument();
    });

    // #165: the red numbered "value meal" circle badge, shown only for an item that has a
    // `mealNumber`.
    it("renders the meal-number badge only for an item that has one", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        { name: "Combo One", sizes: [{ size: "Standard", price: 7.99 }], description: "Has a meal number.", mealNumber: "1" },
                        { name: "Plain Burger", sizes: [{ size: "Standard", price: 4.99 }], description: "No meal number." }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Combo One")).toBeInTheDocument());
        expect(screen.getByText("1")).toBeInTheDocument();
        expect(screen.getByText("Plain Burger")).toBeInTheDocument();
    });

    // #165: an item with a `menuPeriod` that doesn't match the active `menuMode` is hidden
    // entirely -- from both its data category and the synthesized Extra Value Meals category.
    it("filters items by menuMode via menuPeriod, hiding out-of-mode items", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Breakfast",
                    items: [
                        {
                            name: "Egg Combo",
                            sizes: [{ size: "Standard", price: 6.49 }],
                            description: "Breakfast combo.",
                            mealNumber: "1",
                            menuPeriod: "breakfast"
                        }
                    ]
                },
                {
                    category: "Burgers & Sandwiches",
                    items: [
                        {
                            name: "Burger Combo",
                            sizes: [{ size: "Standard", price: 8.99 }],
                            description: "Lunch combo.",
                            mealNumber: "1",
                            menuPeriod: "lunch"
                        }
                    ]
                },
                {
                    category: "Sides & Drinks",
                    items: [{ name: "Fries", sizes: [{ size: "Standard", price: 2.99 }], description: "Always available.", menuPeriod: "allDay" }]
                }
            ]
        });
        render(<MenuPanel menuMode="lunch" />);

        await waitFor(() => expect(screen.getByText("Burger Combo")).toBeInTheDocument());
        expect(screen.queryByText("Egg Combo")).not.toBeInTheDocument();
        expect(screen.queryByText("Breakfast")).not.toBeInTheDocument(); // the category itself is dropped, empty after filtering
        expect(screen.getByText("Fries")).toBeInTheDocument(); // allDay item stays visible regardless of mode
    });
});
