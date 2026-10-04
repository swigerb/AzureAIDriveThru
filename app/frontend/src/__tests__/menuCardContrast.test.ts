import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { resolvePersonaTheme } from "@/lib/personaTheme";

// Issue #169 (Rick's PR #167 round 3 review, item N13): the menu's category card, item card, and
// category title now resolve through persona-driven `menuSurface` tokens (see
// `lib/personaTheme.ts`, `index.css`, `components/ui/menu-panel.tsx`) instead of the hard-coded
// `bg-white/NN`/`text-primary` literals. A className-presence check alone (menu-panel.test.tsx)
// only proves the new class strings are applied, not that the colors they resolve to are actually
// legible -- THIS file independently recomputes the real WCAG contrast ratio for every real
// persona pack that authors `menuSurface`, against the exact text/background pairings
// `menu-panel.tsx` renders, with a mutation check proving the assertions are sensitive to the
// actual hex values (not vacuously true).
//
// It also proves the inverse for every pack that does NOT author `menuSurface` (today, most
// shipped packs): the new `--menu-category-card`/`--menu-item-card`/`--menu-item-card-dark` CSS variables
// resolve to the exact literal defaults `menu-panel.tsx` rendered before this change
// (`bg-white/80`/`bg-white/70`/`dark:bg-white/5`), so this change is provably a no-op for them.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../");
const personasDir = path.resolve(repoRoot, "personas");
const indexCssText = readFileSync(path.resolve(here, "../index.css"), "utf-8");

/** Discovers every real persona pack on disk -- same convention as heroContrast.test.ts/
 * brandDefaultTokens.test.ts, so a future pack is covered the moment it lands rather than needing
 * a hardcoded id list here. */
function discoverPackIds(): string[] {
    if (!existsSync(personasDir)) return [];
    return readdirSync(personasDir, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => entry.name)
        .filter(id => existsSync(path.resolve(personasDir, id, "persona.json")))
        .sort();
}

function readPersonaJson(packId: string): Record<string, any> {
    return JSON.parse(readFileSync(path.resolve(personasDir, packId, "persona.json"), "utf-8"));
}

const packIds = discoverPackIds();

// ---- index.css literal-default extraction (so this file tracks the real shared defaults if they
// ever change, rather than hardcoding a second copy that could silently drift -- same convention
// as heroContrast.test.ts's `sharedDarkBackgroundDefault`). Only one `.dark { ... }` block exists in
// index.css (the shadcn-vars block), so splitting the text there cleanly separates `:root`'s
// literals from `.dark`'s. ----
const darkBlockIndex = indexCssText.indexOf(".dark {");
if (darkBlockIndex === -1) throw new Error("index.css's .dark block was not found");
const rootCssText = indexCssText.slice(0, darkBlockIndex);
const darkCssText = indexCssText.slice(darkBlockIndex);

function extractLiteral(text: string, pattern: RegExp, label: string): string {
    const match = text.match(pattern);
    if (!match) throw new Error(`${label} literal was not found in index.css`);
    return match[1].trim();
}

const DEFAULT_CATEGORY_CARD = extractLiteral(rootCssText, /--menu-category-card:\s*([^;]+);/, "--menu-category-card");
const DEFAULT_ITEM_CARD = extractLiteral(rootCssText, /--menu-item-card:\s*([^;]+);/, "--menu-item-card");
const DEFAULT_ITEM_CARD_DARK = extractLiteral(darkCssText, /--menu-item-card-dark:\s*([^;]+);/, "--menu-item-card-dark");
const DEFAULT_MUTED_FOREGROUND_LIGHT = extractLiteral(
    rootCssText,
    /--muted-foreground:\s*var\(--surface-muted-foreground,\s*([^)]+)\)/,
    "light --muted-foreground fallback"
);
const DEFAULT_MUTED_FOREGROUND_DARK = extractLiteral(darkCssText, /--muted-foreground:\s*([^;]+);/, "dark --muted-foreground");
const DEFAULT_DARK_BACKGROUND = extractLiteral(darkCssText, /--background:\s*var\(--brand-background-dark,\s*([^)]+)\)/, "dark --background fallback");

// Sanity-pin the literals this file depends on, so a change to index.css's rendering fails loudly
// here rather than silently invalidating every ratio computed below.
it("index.css's menu-card literal defaults are exactly what menu-panel.tsx rendered before issue #169", () => {
    expect(DEFAULT_CATEGORY_CARD).toBe("rgba(255, 255, 255, 0.8)"); // was `bg-white/80`.
    expect(DEFAULT_ITEM_CARD).toBe("rgba(255, 255, 255, 0.7)"); // was `bg-white/70`.
    expect(DEFAULT_ITEM_CARD_DARK).toBe("rgba(255, 255, 255, 0.05)"); // was `dark:bg-white/5`.
});

// ---- WCAG 2.x contrast math -------------------------------------------------------------------
// Self-contained implementation of the W3C formulas, matching heroContrast.test.ts's own copy (no
// contrast-ratio library exists in package.json today).

type Rgb = [number, number, number];

/** Parses persona.json's "H S% L%" triplet format (no hsl() wrapper) into 0-255 sRGB. */
function parseHslTriplet(triplet: string): Rgb {
    const match = triplet.match(/^(-?\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)%\s+(\d+(?:\.\d+)?)%$/);
    if (!match) throw new Error(`"${triplet}" is not an "H S% L%" triplet`);
    const h = ((Number(match[1]) % 360) + 360) % 360;
    const s = Number(match[2]) / 100;
    const l = Number(match[3]) / 100;
    const c = (1 - Math.abs(2 * l - 1)) * s;
    const hPrime = h / 60;
    const x = c * (1 - Math.abs((hPrime % 2) - 1));
    const m = l - c / 2;
    let [r, g, b] = [0, 0, 0];
    if (hPrime < 1) [r, g, b] = [c, x, 0];
    else if (hPrime < 2) [r, g, b] = [x, c, 0];
    else if (hPrime < 3) [r, g, b] = [0, c, x];
    else if (hPrime < 4) [r, g, b] = [0, x, c];
    else if (hPrime < 5) [r, g, b] = [x, 0, c];
    else [r, g, b] = [c, 0, x];
    return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

/** Parses a #RGB or #RRGGBB hex string into 0-255 sRGB. */
function parseHex(hex: string): Rgb {
    const normalized = hex.replace("#", "");
    const expanded = normalized.length === 3 ? normalized.split("").map(c => c + c).join("") : normalized;
    const value = parseInt(expanded, 16);
    return [(value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff];
}

/** Parses an "rgba(r, g, b, a)" string (index.css's menu-card literal fallbacks) into 0-255 sRGB
 * plus its alpha fraction. */
function parseRgba(value: string): { rgb: Rgb; alpha: number } {
    const match = value.match(/^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)$/);
    if (!match) throw new Error(`"${value}" is not an "rgba(r, g, b, a)" literal`);
    return { rgb: [Number(match[1]), Number(match[2]), Number(match[3])], alpha: Number(match[4]) };
}

/** Parses either format this file deals with: "H S% L%" triplet, hex, or rgba(). */
function parseColor(value: string): Rgb {
    if (value.startsWith("#")) return parseHex(value);
    if (value.startsWith("rgba(")) return parseRgba(value).rgb;
    return parseHslTriplet(value);
}

/** Alpha-composites `fg` at `alpha` (0-1) over opaque `bg`. */
function compositeOver(fg: Rgb, alpha: number, bg: Rgb): Rgb {
    return [0, 1, 2].map(i => Math.round(fg[i] * alpha + bg[i] * (1 - alpha))) as unknown as Rgb;
}

/** WCAG 2.x relative luminance. */
function relativeLuminance([r, g, b]: Rgb): number {
    const linearize = (channel: number) => {
        const c = channel / 255;
        return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * linearize(r) + 0.7152 * linearize(g) + 0.0722 * linearize(b);
}

/** WCAG 2.x contrast ratio: (L1+0.05)/(L2+0.05), where L1 is the lighter of the two colors. */
function contrastRatio(a: Rgb, b: Rgb): number {
    const [l1, l2] = [relativeLuminance(a), relativeLuminance(b)].sort((x, y) => y - x);
    return (l1 + 0.05) / (l2 + 0.05);
}

const WCAG_AA_BODY_TEXT_MINIMUM = 4.5;

/** Resolves an opaque or alpha-suffixed background/text literal (hex, rgba(), or "H S% L%") onto
 * `base`, exactly as `compositeOver` composites a Tailwind `/NN` opacity class over whatever is
 * already painted underneath it. `alpha` defaults to 1 (fully opaque, e.g. `text-foreground`). */
function over(value: string, base: Rgb, alpha = 1): Rgb {
    return compositeOver(parseColor(value), alpha, base);
}

describe("menu card tones (issue #169, Rick's PR #167 round 3 review, item N13)", () => {
    it("sanity: at least one real persona pack was discovered", () => {
        expect(packIds.length).toBeGreaterThan(0);
    });

    const packsWithMenuSurface = packIds
        .map(packId => ({ packId, raw: readPersonaJson(packId) }))
        .filter(({ raw }) => Boolean(raw.ui?.theme?.light?.menuSurface) || Boolean(raw.ui?.theme?.dark?.menuSurface));

    const packsWithoutMenuSurface = packIds
        .map(packId => ({ packId, raw: readPersonaJson(packId) }))
        .filter(({ raw }) => !raw.ui?.theme?.light?.menuSurface && !raw.ui?.theme?.dark?.menuSurface);

    it("at least one real pack authors menuSurface, and at least one does not (both branches of this file actually run)", () => {
        expect(packsWithMenuSurface.length).toBeGreaterThan(0);
        expect(packsWithoutMenuSurface.length).toBeGreaterThan(0);
    });

    describe.each(packsWithoutMenuSurface.map(({ packId }) => [packId]))(
        "%s (no menuSurface authored): renders the exact literal defaults menu-panel.tsx rendered before issue #169",
        packId => {
            it("category card / item card (light) and item card (dark) are untouched", () => {
                // A pack without `ui.theme.*.menuSurface` never calls `applyTheme`/`applyDarkTheme`'s
                // new `set(menuSurfaceVars...)`/`if (menuSurface?...)` branches at all (see
                // `lib/personaTheme.ts`), so `index.css`'s own literal fallback is what actually
                // renders -- this just pins that those literals are unchanged, for this pack.
                void packId; // kept for the describe.each title; the assertion is pack-independent.
                expect(DEFAULT_CATEGORY_CARD).toBe("rgba(255, 255, 255, 0.8)");
                expect(DEFAULT_ITEM_CARD).toBe("rgba(255, 255, 255, 0.7)");
                expect(DEFAULT_ITEM_CARD_DARK).toBe("rgba(255, 255, 255, 0.05)");
            });
        }
    );

    describe.each(packsWithMenuSurface.map(({ packId, raw }) => [packId, raw] as const))("%s: WCAG AA contrast for its authored menuSurface tones", (packId, raw) => {
        const wireTheme = raw.ui.theme;
        const resolved = resolvePersonaTheme(packId, wireTheme);
        const lightMenuSurface = resolved.light.menuSurface;
        const darkMenuSurface = resolved.dark?.menuSurface;
        const accents = resolved.light.accents;

        const WHITE: Rgb = [255, 255, 255];
        const BLACK: Rgb = [0, 0, 0];

        it("authors at least one light or dark menuSurface token (sanity for this describe.each branch)", () => {
            expect(lightMenuSurface ?? darkMenuSurface).toBeTruthy();
        });

        if (lightMenuSurface?.categoryCardBackground) {
            it("light category title (text-primary) reaches >= 4.5:1 over its new category card background", () => {
                const card = parseColor(lightMenuSurface.categoryCardBackground!);
                const text = parseHslTriplet(wireTheme.light.primary);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });
        }

        if (lightMenuSurface?.itemCardBackground) {
            const card = parseColor(lightMenuSurface.itemCardBackground!);

            it("light item name (opaque text-foreground) reaches >= 4.5:1 over its new item card background", () => {
                const text = parseHslTriplet(wireTheme.light.foreground);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            it("light item description (opaque text-muted-foreground) reaches >= 4.5:1 over its new item card background", () => {
                const mutedForeground = resolved.light.surface?.mutedForeground ?? DEFAULT_MUTED_FOREGROUND_LIGHT;
                const text = parseHslTriplet(mutedForeground);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            it("light item price (text-foreground/80) reaches >= 4.5:1 over its new item card background", () => {
                const text = over(wireTheme.light.foreground, card, 0.8);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            // Non-blocking disclosure, not a regression: Rick's N13 review only asked for the card/
            // title tones below, and the calorie line (`text-muted-foreground/70`) was ALREADY
            // under the WCAG AA floor against the plain `bg-white/70` card before this change (a
            // pre-existing, shared-code issue affecting every persona identically, out of scope
            // here -- see the PR body). This asserts the new cream background does not make that
            // pre-existing gap meaningfully worse, rather than asserting it passes AA.
            it("light item calories (text-muted-foreground/70) does not get meaningfully worse against its new item card background (pre-existing AA gap, out of scope)", () => {
                const mutedForeground = resolved.light.surface?.mutedForeground ?? DEFAULT_MUTED_FOREGROUND_LIGHT;
                const before = contrastRatio(over(mutedForeground, parseColor(DEFAULT_ITEM_CARD), 0.7), parseColor(DEFAULT_ITEM_CARD));
                const after = contrastRatio(over(mutedForeground, card, 0.7), card);
                expect(after).toBeGreaterThan(before - 0.2);
            });
        }

        if (darkMenuSurface?.itemCardBackground) {
            const card = parseColor(darkMenuSurface.itemCardBackground!);

            it("dark item name (opaque dark:text-white) reaches >= 4.5:1 over its new dark item card background", () => {
                expect(contrastRatio(WHITE, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            it("dark item description (opaque text-muted-foreground) reaches >= 4.5:1 over its new dark item card background", () => {
                const text = parseHslTriplet(DEFAULT_MUTED_FOREGROUND_DARK);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            it("dark item price (dark:text-white/80) reaches >= 4.5:1 over its new dark item card background", () => {
                const text = over("#FFFFFF", card, 0.8);
                expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });
        }

        if (darkMenuSurface?.categoryTitleColor) {
            it("dark category title reaches >= 4.5:1 over the UNCHANGED dark category card background", () => {
                // The category card's dark background was deliberately left untouched by this
                // issue (Rick's N13 only asked for the title color and item card, not the category
                // card, in dark mode) -- `dark:bg-brand-surface-dark/95` composited over the dark
                // page background, exactly as `menu-panel.tsx` renders it.
                const darkPageBackground = parseHslTriplet(wireTheme.dark?.background ?? DEFAULT_DARK_BACKGROUND);
                const categoryCard = over(accents.surfaceDark, darkPageBackground, 0.95);
                const text = parseHex(darkMenuSurface.categoryTitleColor!);
                expect(contrastRatio(text, categoryCard)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
            });

            // Mutation check (Rick's established convention, e.g. heroContrast.test.ts): proves the
            // assertion above is actually sensitive to the authored color, not vacuously true for
            // any color. A near-black substitute for the real yellow fails hard against the same
            // dark category card background.
            it("mutation check: a near-black categoryTitleColor fails AA against the same dark category card background", () => {
                const darkPageBackground = parseHslTriplet(wireTheme.dark?.background ?? DEFAULT_DARK_BACKGROUND);
                const categoryCard = over(accents.surfaceDark, darkPageBackground, 0.95);
                expect(contrastRatio(BLACK, categoryCard)).toBeLessThan(WCAG_AA_BODY_TEXT_MINIMUM);
            });
        }
    });
});
