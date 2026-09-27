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
});
