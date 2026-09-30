import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Issue #80 F4 (design doc §5.2, §9 row F4): the menu panel now fetches `current.menuUrl` instead
// of reading a bundled `src/data/menuItems.json` copy. `usePersonaContext` is mocked here so each
// test controls `current.menuUrl` directly without spinning up a full PersonaProvider fetch chain.

const context = vi.hoisted(() => ({ menuUrl: "/personas/test-alpha/menu.json?v=test", categoryIcons: undefined as Record<string, string> | undefined }));
vi.mock("@/context/persona-context", () => ({
    usePersonaContext: () => ({ current: { menuUrl: context.menuUrl, categoryIcons: context.categoryIcons } as PersonaDetail })
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
});
