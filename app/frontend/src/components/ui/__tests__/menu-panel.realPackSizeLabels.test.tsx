import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import MenuPanel from "../menu-panel";
import type { PersonaDetail } from "@/types/persona";

// Rick's #166 round-1 review, required item 1 ("pin with tests for all three packs"), reworked
// per the round-2 review (RB1): the synthetic fixture tests in menu-panel.test.tsx pin the
// *rule* (shouldShowSizeLabel); these tests pin the *real pack data* itself for EVERY real
// persona pack found on disk, discovered via readdirSync (same idea as
// brandDefaultTokens.test.ts's disk-discovery pattern) rather than naming any pack, so a future
// edit to any pack's menu data -- or a newly added pack -- is covered here even if nobody
// touches menu-panel.tsx itself.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../../../");
const personasDir = path.resolve(repoRoot, "personas");

interface Size {
    size: string;
    price: number;
}

interface RealMenuItem {
    name: string;
    sizes: Size[];
    calories?: number;
    menuPeriod?: "breakfast" | "lunch" | "allDay";
}

interface RealMenuCategory {
    category: string;
    items: RealMenuItem[];
}

interface RealMenuDocument {
    menuItems: RealMenuCategory[];
}

function loadMenu(packDirName: string): RealMenuDocument {
    return JSON.parse(readFileSync(path.resolve(personasDir, packDirName, "menu", "menuItems.json"), "utf-8"));
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

/** Mirrors menu-panel.tsx's own (unexported) shouldShowSizeLabel rule exactly, derived purely
 * from an item's own `sizes`/`calories` data -- a regression in either copy shows up as a
 * mismatch against the real, rendered DOM below rather than both sides silently agreeing on a
 * changed answer. */
function expectedShouldShowSizeLabel(size: string, calories: number | undefined): boolean {
    if (typeof calories === "number") {
        return size !== "standard" && size !== "Standard";
    }
    return size !== "standard";
}

/** RTL's default text normalizer trims and collapses runs of whitespace (including non-breaking
 * spaces, which `\s` matches) in RENDERED DOM text before a match is attempted, but does nothing
 * to the plain-string matcher itself -- a pack's item name containing a `\u00A0` (one does, for
 * trademark-symbol kerning) would otherwise never match its own rendered, now-regular-spaced
 * text. Applying the identical transform to every matcher string below keeps both sides in the
 * same normalized form regardless of which literal whitespace character a pack's own data uses. */
function normalizeWhitespace(value: string): string {
    return value.trim().replace(/\s+/g, " ");
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
    it("found at least one real persona pack's own menu data on disk to test against", () => {
        expect(packDirNames.length).toBeGreaterThan(0);
    });

    for (const packDirName of packDirNames) {
        // packDirName is a disk-discovered directory name, never a literal string in this
        // file's own source -- see the header comment.
        describe(`a real pack (${packDirNames.indexOf(packDirName) + 1} of ${packDirNames.length})`, () => {
            const menu = loadMenu(packDirName);
            // The pack's own declared menuPeriod tags (excluding "allDay", which is always
            // visible) -- rendering once per tag covers every item that's only ever visible
            // under that specific mode; a pack with no such tags at all renders once, untagged.
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
                it(
                    `renders each item's size label per the shared rule${mode ? ` (${mode} mode)` : ""}`,
                    async () => {
                        mockFetchOnce(menu);
                        render(<MenuPanel menuMode={mode} />);

                        const itemsForMode = menu.menuItems.flatMap(category =>
                            category.items.filter(
                                item => !item.menuPeriod || item.menuPeriod === "allDay" || item.menuPeriod === mode
                            )
                        );
                        expect(itemsForMode.length).toBeGreaterThan(0);

                        for (const item of itemsForMode) {
                            const expectedName = normalizeWhitespace(item.name);
                            // A matcher function, not a plain string: some real items' own
                            // `description` is identical to their `name` (a pack-data quirk, not
                            // a test bug), which would otherwise match two elements -- the plain
                            // string form requires exactly one match. Scoping to the `<span>` the
                            // name itself renders in (menu-panel.tsx) rather than the `<p>`
                            // description picks the right one without naming this item specially.
                            const nameEl = await waitFor(() =>
                                screen.getByText(
                                    (_content, element) =>
                                        element?.tagName === "SPAN" && normalizeWhitespace(element.textContent ?? "") === expectedName
                                )
                            );
                            const card = nameEl.closest("div.rounded-2xl");
                            expect(card).not.toBeNull();
                            const cardScope = within(card as HTMLElement);

                            for (const { size, price } of item.sizes) {
                                expect(cardScope.getByText(`$${price.toFixed(2)}`)).toBeInTheDocument();
                                // Rendered text is `${size}: ` with a trailing space, but RTL's
                                // default text normalizer trims it before matching -- so the
                                // expected needle here has none either.
                                const label = normalizeWhitespace(`${size}:`);
                                if (expectedShouldShowSizeLabel(size, item.calories)) {
                                    expect(cardScope.queryByText(label)).toBeInTheDocument();
                                } else {
                                    expect(cardScope.queryByText(label)).not.toBeInTheDocument();
                                }
                            }
                        }
                    },
                    // The largest real pack today has ~180 items across every size; asserting
                    // name/price/label for every one of them against the full rendered DOM is
                    // slower than vitest's 5s default -- measured ~6-9s on a dev machine but
                    // ~23s on CI runners, so this leaves generous headroom above the slower
                    // environment rather than just the one observed locally. A pack that grows
                    // further just needs more of this same headroom, not a different strategy.
                    60000
                );
            }
        });
    }
});
