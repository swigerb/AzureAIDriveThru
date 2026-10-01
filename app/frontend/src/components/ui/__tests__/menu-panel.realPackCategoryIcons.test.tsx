import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Rick's PR #167 round-3 review (merge of #166 and #164 E1): a pack's categoryIcons map can
// silently go stale relative to the pack's own menu-data category names (exactly what happened
// here -- a rename/reorg of one pack's literal categories left several keyed to names nobody
// renders anymore, so those categories quietly fell back to the shared neutral icon). This file
// pins, for EVERY real persona pack found on disk and EVERY menu mode it actually uses, that
// every one of the pack's own literal (non-synthesized) categories resolves to a configured icon
// (its own `icon`, a `modeDisplay` override, or the pack's `categoryIcons` map) rather than the
// shared neutral fallback -- discovered via readdirSync (same disk-discovery idea as
// menu-panel.realPackSizeLabels.test.tsx and brandDefaultTokens.test.ts), so no pack name appears
// literally in this file, and a future rename/reorg of any pack's categories (or a newly added
// pack) is caught here automatically.
//
// Deliberately out of scope: the `mealNumber`-synthesized "value meals" virtual category (built
// at runtime from a persona string, not one of the pack's own `menuItems.json` categories). This
// suite's global `useTranslation` mock (src/test/setup.ts) returns every key untranslated, so
// under test that virtual category's name is always the literal translation key, never a pack's
// real string -- exercising its icon resolution needs the real translator and is already covered,
// for the one pack that uses it, by menu-panel.test.tsx's own synthesized-category tests.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../../../");
const personasDir = path.resolve(repoRoot, "personas");

interface RealMenuItem {
    menuPeriod?: "breakfast" | "lunch" | "allDay";
}

interface RealMenuCategory {
    category: string;
    modeDisplay?: Partial<Record<"breakfast" | "lunch" | "allDay", { displayName?: string; icon?: string }>>;
    items: RealMenuItem[];
}

interface RealMenuDocument {
    menuItems: RealMenuCategory[];
}

function loadMenu(packDirName: string): RealMenuDocument {
    return JSON.parse(readFileSync(path.resolve(personasDir, packDirName, "menu", "menuItems.json"), "utf-8"));
}

function loadCategoryIcons(packDirName: string): Record<string, string> | undefined {
    const manifest = JSON.parse(readFileSync(path.resolve(personasDir, packDirName, "persona.json"), "utf-8"));
    return manifest.ui?.categoryIcons;
}

// Every pack directory under personas/ that actually has its own menu data -- no pack named
// here, no pack assumed to exist; a pack added or removed later is picked up automatically.
const packDirNames = readdirSync(personasDir, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => entry.name)
    .filter(name => {
        try {
            loadMenu(name);
            return true;
        } catch {
            return false;
        }
    });

// Mirrors menu-panel.tsx's own (unexported) DEFAULT_CATEGORY_ICON -- the shared, brand-neutral
// glyph every category falls back to when it has no configured icon of its own anywhere. Kept as
// a local constant (not re-derived from the component) because the whole point of this test is
// to notice a divergence between what a pack configures and what actually renders -- if this
// value ever changes, the component's own doc comment at DEFAULT_CATEGORY_ICON must change too,
// and this file's own assertion below.
const SHARED_NEUTRAL_FALLBACK_ICON = "🍹";

// Mirrors menu-panel.tsx's own (unexported) isItemVisible, for the subset of its rule that
// depends only on `menuPeriod` (no `item.mealNumber` gating needed here -- a mealNumber item
// still belongs to, and keeps, its own data category's icon in this file's scope; only its
// render POSITION moves to the synthesized category, which is out of scope above).
function isVisibleForMode(item: RealMenuItem, mode: string | undefined): boolean {
    if (!mode) {
        return true;
    }
    return !item.menuPeriod || item.menuPeriod === "allDay" || item.menuPeriod === mode;
}

const context = vi.hoisted(() => ({
    menuUrl: "/personas/test-pack/menu.json?v=test",
    categoryIcons: undefined as Record<string, string> | undefined
}));
vi.mock("@/context/persona-context", () => ({
    usePersonaContext: () => ({ current: { menuUrl: context.menuUrl, categoryIcons: context.categoryIcons } as PersonaDetail })
}));

function mockFetchOnce(body: unknown) {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => body })));
}

afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    context.categoryIcons = undefined;
});

describe("MenuPanel category icons against real pack data (R11 merge pin)", () => {
    it("found at least one real persona pack's own menu data on disk to test against", () => {
        expect(packDirNames.length).toBeGreaterThan(0);
    });

    for (const packDirName of packDirNames) {
        // packDirName is a disk-discovered directory name, never a literal string in this
        // file's own source -- see the header comment.
        describe(`a real pack (${packDirNames.indexOf(packDirName) + 1} of ${packDirNames.length})`, () => {
            const menu = loadMenu(packDirName);
            const categoryIcons = loadCategoryIcons(packDirName);
            // The pack's own declared menuPeriod tags (excluding "allDay", which is always
            // visible) -- rendering once per tag covers every mode's own set of visible
            // categories/mode-renamed display, same convention as the sibling size-label pin.
            const explicitPeriods = Array.from(
                new Set(
                    menu.menuItems.flatMap(category =>
                        category.items
                            .map(item => item.menuPeriod)
                            .filter((period): period is "breakfast" | "lunch" => period !== undefined && period !== "allDay")
                    )
                )
            );
            const modesToRender = explicitPeriods.length > 0 ? explicitPeriods : [undefined];

            for (const mode of modesToRender) {
                it(`renders every one of this pack's own categories with a configured icon, never the shared fallback${mode ? ` (${mode} mode)` : ""}`, async () => {
                    context.categoryIcons = categoryIcons;
                    mockFetchOnce(menu);
                    render(<MenuPanel menuMode={mode} />);

                    // A category with zero items visible under this mode is never rendered at
                    // all (menu-panel.tsx's own buildDisplayCategories drops it entirely), so it
                    // has no icon to check here -- same filter the component itself applies.
                    const renderedCategories = menu.menuItems.filter(category =>
                        category.items.some(item => isVisibleForMode(item, mode))
                    );
                    expect(renderedCategories.length).toBeGreaterThan(0);

                    for (const category of renderedCategories) {
                        const override = mode ? category.modeDisplay?.[mode as "breakfast" | "lunch"] : undefined;
                        const expectedHeading = override?.displayName ?? category.category;

                        const heading = await screen.findByText(expectedHeading);
                        const headerButton = heading.closest("button");
                        expect(headerButton).not.toBeNull();

                        const iconSpan = headerButton!.querySelector('span.text-2xl[aria-hidden="true"]');
                        expect(iconSpan).not.toBeNull();
                        expect(iconSpan!.textContent).not.toBe(SHARED_NEUTRAL_FALLBACK_ICON);
                        expect(iconSpan!.textContent?.trim()).not.toBe("");
                    }
                });
            }
        });
    }
});
