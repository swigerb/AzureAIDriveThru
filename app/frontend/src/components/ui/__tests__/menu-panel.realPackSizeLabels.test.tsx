import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Rick's #166 round-1 review, required item 1: "pin with tests for all three packs". The
// synthetic fixture tests in menu-panel.test.tsx pin the *rule* (shouldShowSizeLabel); these
// tests pin the *real pack data* itself for each of the three existing packs, reading each
// pack's actual personas/<id>/menu/menuItems.json straight off disk (same idea as
// brandDefaultTokens.test.ts's disk-discovery pattern) so a future edit to any pack's menu data
// that reintroduces the Dunkin regression -- or regresses Sonic's or McDonald's own card -- fails
// here even if nobody touches menu-panel.tsx itself.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../../../");
const personasDir = path.resolve(repoRoot, "personas");

function loadMenu(packId: string): unknown {
    return JSON.parse(readFileSync(path.resolve(personasDir, packId, "menu", "menuItems.json"), "utf-8"));
}

const context = vi.hoisted(() => ({ menuUrl: "/personas/test-pack/menu.json?v=test" }));
vi.mock("@/context/persona-context", () => ({
    usePersonaContext: () => ({ current: { menuUrl: context.menuUrl } as PersonaDetail })
}));

function mockFetchOnce(body: unknown) {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => body })));
}

afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
});

describe("MenuPanel size labels against real pack data (R1 pin)", () => {
    it.skipIf(!existsSync(path.resolve(personasDir, "dunkin", "menu", "menuItems.json")))(
        "hides the size label for Dunkin's real lowercase 'standard' single-size items (Glazed Donut)",
        async () => {
            mockFetchOnce(loadMenu("dunkin"));
            render(<MenuPanel />);

            await waitFor(() => expect(screen.getByText("Glazed Donut")).toBeInTheDocument());
            expect(screen.queryByText(/standard:/i)).not.toBeInTheDocument();
        }
    );

    it.skipIf(!existsSync(path.resolve(personasDir, "sonic", "menu", "menuItems.json")))(
        "keeps the size label for Sonic's real capitalized 'Standard' single-size items without calories (dev behavior unchanged)",
        async () => {
            mockFetchOnce(loadMenu("sonic"));
            render(<MenuPanel />);

            await waitFor(() => expect(screen.getByText("SONIC® Cheeseburger")).toBeInTheDocument());
            expect(screen.getAllByText("Standard:").length).toBeGreaterThan(0);
        }
    );

    it.skipIf(!existsSync(path.resolve(personasDir, "mcdonalds", "menu", "menuItems.json")))(
        "hides the size label for McDonald's real 'Standard' single-size items that carry calories (original card look)",
        async () => {
            mockFetchOnce(loadMenu("mcdonalds"));
            // Big Mac® is a lunch-period item (personas/mcdonalds/menu/menuItems.json); the
            // panel only shows it when bound to that mode (isItemVisible), mirroring how App.tsx
            // always supplies a real menuMode for a dayparts pack.
            render(<MenuPanel menuMode="lunch" />);

            await waitFor(() => expect(screen.getByText("Big Mac®")).toBeInTheDocument());
            expect(screen.queryByText(/standard:/i)).not.toBeInTheDocument();
        }
    );
});
