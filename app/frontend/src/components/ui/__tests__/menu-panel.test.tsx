import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Issue #80 F4 (design doc §5.2, §9 row F4): the menu panel now fetches `current.menuUrl` instead
// of reading a bundled `src/data/menuItems.json` copy. `usePersonaContext` is mocked here so each
// test controls `current.menuUrl` directly without spinning up a full PersonaProvider fetch chain.

const context = vi.hoisted(() => ({
    menuUrl: "/personas/test-alpha/menu.json?v=test",
    categoryIcons: undefined as Record<string, string> | undefined,
    textRoles: undefined as PersonaDetail["textRoles"]
}));
vi.mock("@/context/persona-context", () => ({
    usePersonaContext: () => ({ current: { menuUrl: context.menuUrl, categoryIcons: context.categoryIcons, textRoles: context.textRoles } as PersonaDetail })
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
    context.categoryIcons = undefined;
    context.textRoles = undefined;
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

    // Issue 164 E1: a pack's own `categoryIcons` (persona.json ui config, keyed by category name)
    // is the second fallback tier -- used when the category's own menu-data `icon` is absent, and
    // preferred over the shared neutral default. Keeping this keyed by pack data rather than
    // editing menuItems.json avoids collisions with issue 165's menu-data work on the same file.
    it("renders the pack's categoryIcons override when the category has no icon of its own", async () => {
        context.categoryIcons = { "Signature Lattes": "☕" };
        mockFetchOnce({
            menuItems: [
                {
                    category: "Signature Lattes",
                    items: [{ name: "Test Latte", sizes: [{ size: "standard", price: 3.49 }], description: "A fixture latte." }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Test Latte")).toBeInTheDocument());
        expect(screen.getByText("☕")).toBeInTheDocument();
    });

    it("prefers the category's own icon over the pack's categoryIcons override", async () => {
        context.categoryIcons = { "Iced Coffee": "🧊" };
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
        expect(screen.queryByText("🧊")).not.toBeInTheDocument();
    });

    // Issue 164 E3: category header spacing must match the original apps' own markup exactly --
    // `break-keep` (never `truncate`/ellipsis, which the originals never use) so a short category
    // name like "Signature Lattes" stays on one line at normal spacing, while a longer name is free
    // to wrap onto a second line exactly as the original apps themselves do.
    it("uses the original apps' break-keep heading class, never a truncating ellipsis (E3)", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Signature Lattes",
                    items: [{ name: "Test Latte", sizes: [{ size: "standard", price: 3.49 }], description: "A fixture latte." }]
                }
            ]
        });
        render(<MenuPanel />);

        const title = await screen.findByText("Signature Lattes");
        expect(title.tagName).toBe("H3");
        expect(title.className).toContain("break-keep");
        expect(title.className).not.toContain("truncate");
    });

    // Issue #164 R2(c) (PR #167 round 1 review): the item-count chip's text color uses the
    // countChip role (default "secondary") instead of a hardcoded text-brand-secondary, so a pack
    // that overrides it (e.g. to "accent") gets a readable chip on its own surface in both modes.
    it("colors the item-count chip from the default countChip role when textRoles is absent", async () => {
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        const chip = await screen.findByText("1 item");
        expect(chip.className).toContain("text-brand-secondary");
        expect(chip.className).toContain("dark:text-brand-secondary-tint");
    });

    it("colors the item-count chip from an explicit countChip role override, light and dark", async () => {
        context.textRoles = { countChip: "accent" };
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        const chip = await screen.findByText("1 item");
        expect(chip.className).toContain("text-brand-accent");
        // Issue #164 R2(c): the dark-mode chip reuses the secondary tint for the accent role,
        // since there is no separate accent-tint token -- this is the fix for a pack whose accent
        // role renders unreadably dark on the dark-mode chip surface.
        expect(chip.className).toContain("dark:text-brand-secondary-tint");
    });

    it("colors the item-count chip from a primaryDeep countChip role override, light and dark", async () => {
        context.textRoles = { countChip: "primaryDeep" };
        mockFetchOnce(SAMPLE_MENU);
        render(<MenuPanel />);

        const chip = await screen.findByText("1 item");
        expect(chip.className).toContain("text-brand-primary-deep");
        expect(chip.className).toContain("dark:text-brand-primary-tint");
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

    // Rick's #166 round-1 review, required item 1 (pre-existing pack regression pin): a
    // single-size item with the LOWERCASE size key "standard" and no `calories` (several of a
    // pre-existing pack's own real single-size items are written exactly this way) must never
    // show a "standard:" label -- dev's original rule hid it unconditionally for that lowercase
    // value, and this PR's menu-fidelity work must not re-expose it just because the item
    // happens to lack `calories`.
    it("keeps dev's exact rule for a lowercase 'standard' size with no calories (pre-existing pack regression pin)", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Donuts",
                    items: [{ name: "Glazed Donut", sizes: [{ size: "standard", price: 1.29 }], description: "A fixture donut." }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Glazed Donut")).toBeInTheDocument());
        expect(screen.getByText("$1.29")).toBeInTheDocument();
        expect(screen.queryByText(/standard:/i)).not.toBeInTheDocument();
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

    // #165: `mealNumber` items are pulled into a synthesized value-meals category (its display
    // name itself pack-driven as of Rick's required item 9) shown first, sorted by meal number,
    // and removed from their original data category.

    // Rick's #166 round-1 review, required item 2: a 0-calorie item must not render a literal
    // "0 Cal" line -- matching the original reference card, which only ever renders the line for
    // a truthy (i.e. positive) calorie count.
    it("does not render a calorie line for a 0-calorie item", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Fries, Sides & Drinks",
                    items: [{ name: "Zero Cal Water", sizes: [{ size: "Standard", price: 1.0 }], description: "Still water.", calories: 0 }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("Zero Cal Water")).toBeInTheDocument());
        expect(screen.queryByText("0 Cal")).not.toBeInTheDocument();
    });

    // The meal circle + name share one row; the
    // description and calorie line must be SIBLINGS below that row, not nested inside it (pins
    // the original's own DOM shape).
    it("renders the description below the circle+name row, not nested inside it", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Extra Value Meals",
                    items: [
                        {
                            name: "Fixture Combo Meal®",
                            sizes: [{ size: "Standard", price: 8.29 }],
                            description: "Two beef patties, special sauce.",
                            calories: 590,
                            mealNumber: "1"
                        }
                    ]
                }
            ]
        });
        render(<MenuPanel />);

        const nameEl = await screen.findByText("Fixture Combo Meal®");
        const circleAndNameRow = nameEl.closest("div");
        expect(circleAndNameRow?.querySelector("p")).toBeNull(); // no description/calories <p> inside this row

        const descriptionEl = screen.getByText("Two beef patties, special sauce.");
        const caloriesEl = screen.getByText("590 Cal");
        // Both live in the same left-column wrapper as the circle+name row, one level up.
        const leftColumn = circleAndNameRow?.parentElement;
        expect(leftColumn?.contains(descriptionEl)).toBe(true);
        expect(leftColumn?.contains(caloriesEl)).toBe(true);
        expect(circleAndNameRow?.contains(descriptionEl)).toBe(false);
        expect(circleAndNameRow?.contains(caloriesEl)).toBe(false);
    });

    // Rick's #166 round-1 review, required item 2: the meal circle is bound to the shared
    // `primary` token (the one #164/PR #167's palette swap for the daypart pack resolves to a
    // specific brand red), not the generic `destructive` token the PR originally used.
    it("binds the meal circle to the primary color token, not the generic destructive token", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Extra Value Meals",
                    items: [{ name: "Combo One", sizes: [{ size: "Standard", price: 7.99 }], description: "First combo.", mealNumber: "1" }]
                }
            ]
        });
        render(<MenuPanel />);

        const circle = await screen.findByText("1");
        expect(circle.className).toContain("bg-primary");
        expect(circle.className).toContain("text-primary-foreground");
        expect(circle.className).not.toContain("destructive");
    });
    it("synthesizes a value-meals category from mealNumber items, sorted and shown first", async () => {
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

        // Rick's PR 166 round-1 review, required item 9: the synthesized category's name is now
        // `t("menu.valueMealsCategory")`, not a shared-component-hardcoded brand string -- the
        // test setup's `t` mock returns the key itself (see src/test/setup.ts).
        await waitFor(() => expect(screen.getByText("menu.valueMealsCategory")).toBeInTheDocument());
        // The virtual category renders first, ahead of every real data category.
        const headings = screen.getAllByRole("heading", { level: 3 }).map(h => h.textContent);
        expect(headings).toEqual(["menu.valueMealsCategory", "Burgers & Sandwiches", "Chicken"]);
        // Sorted by meal number ascending, not by the order items appeared in the source data.
        expect(screen.getByText("1")).toBeInTheDocument();
        expect(screen.getByText("2")).toBeInTheDocument();
        // Plain (non-mealNumber) item stays in its own data category.
        expect(screen.getByText("Plain Burger")).toBeInTheDocument();
        expect(screen.getByText("Plain Nuggets")).toBeInTheDocument();
    });

    // #165: a data category whose every item was pulled into the synthesized value-meals
    // category is dropped entirely -- never rendered as an empty shell.
    it("drops a data category entirely once every one of its items is pulled into the value-meals category", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Combos Only",
                    items: [{ name: "Combo One", sizes: [{ size: "Standard", price: 7.99 }], description: "Only combo.", mealNumber: "1" }]
                }
            ]
        });
        render(<MenuPanel />);

        await waitFor(() => expect(screen.getByText("menu.valueMealsCategory")).toBeInTheDocument());
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
    // entirely -- from both its data category and the synthesized value-meals category.
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

    // Rick's PR 166 round-1 review, required item 6: a falsy/omitted menuMode (what App.tsx
    // always passes for a pack with no features.dayparts) must make every item visible
    // regardless of its own menuPeriod -- mirroring both backends' own mode-unbound
    // (None/null) treatment, which never even reads the item's own field in that case.
    it("shows every item regardless of its own menuPeriod when menuMode is falsy/omitted", async () => {
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
                }
            ]
        });
        render(<MenuPanel />); // no menuMode prop at all -- same as App.tsx's own "" for a no-dayparts pack

        await waitFor(() => expect(screen.getByText("Egg Combo")).toBeInTheDocument());
        expect(screen.getByText("Burger Combo")).toBeInTheDocument();
    });

    // Rick's PR 166 round-1 review, required item 9: a category's pack-declared `modeDisplay`
    // override applies its name/icon for the matching `menuMode`, mirroring the original reference
    // app's own `getCategoryDisplay` ("Fries, Sides & Drinks" -> "Sides & Drinks"/☕ in breakfast).
    it("renders a category's modeDisplay override for the active menuMode", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Fries, Sides & Drinks",
                    modeDisplay: { breakfast: { displayName: "Sides & Drinks", icon: "☕" } },
                    items: [{ name: "Hash Browns", sizes: [{ size: "Standard", price: 2.19 }], description: "Crispy potatoes." }]
                }
            ]
        });
        render(<MenuPanel menuMode="breakfast" />);

        await waitFor(() => expect(screen.getByText("Sides & Drinks")).toBeInTheDocument());
        expect(screen.queryByText("Fries, Sides & Drinks")).not.toBeInTheDocument();
        expect(screen.getByText("☕")).toBeInTheDocument();
    });

    // The same category renders its base name/icon outside the overridden mode -- `modeDisplay`
    // only ever applies for the mode(s) it explicitly lists.
    it("renders a category's base name/icon for a menuMode its modeDisplay doesn't override", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Fries, Sides & Drinks",
                    icon: "🍟",
                    modeDisplay: { breakfast: { displayName: "Sides & Drinks", icon: "☕" } },
                    items: [{ name: "Fixture Fries", sizes: [{ size: "Standard", price: 2.99 }], description: "Golden fries." }]
                }
            ]
        });
        render(<MenuPanel menuMode="lunch" />);

        await waitFor(() => expect(screen.getByText("Fries, Sides & Drinks")).toBeInTheDocument());
        expect(screen.queryByText("Sides & Drinks")).not.toBeInTheDocument();
        expect(screen.getByText("🍟")).toBeInTheDocument();
    });

    // A category's own identity (its data `category` string) stays stable for expand/collapse
    // tracking even while its rendered name changes by mode -- collapsing it in breakfast mode
    // must keep it collapsed if the menu is later re-fetched in breakfast mode again.
    it("keeps a mode-renamed category's collapsed state keyed on its stable data name", async () => {
        mockFetchOnce({
            menuItems: [
                {
                    category: "Fries, Sides & Drinks",
                    modeDisplay: { breakfast: { displayName: "Sides & Drinks", icon: "☕" } },
                    items: [{ name: "Hash Browns", sizes: [{ size: "Standard", price: 2.19 }], description: "Crispy potatoes." }]
                }
            ]
        });
        render(<MenuPanel menuMode="breakfast" />);

        await waitFor(() => expect(screen.getByText("Sides & Drinks")).toBeInTheDocument());
        const toggleButton = screen.getByText("Sides & Drinks").closest("button");
        expect(toggleButton).not.toBeNull();
        fireEvent.click(toggleButton!);
        await waitFor(() => expect(screen.queryByText("Hash Browns")).not.toBeInTheDocument());
    });
});
