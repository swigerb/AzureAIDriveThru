import { describe, expect, it } from "vitest";

// Guards issue #80 F2 (docs/persona-architecture.md §9, acceptance criteria on issue #80): "No
// brand hex values remain in app/frontend/src". Every brand color must go through a `--brand-*`
// CSS custom property (defined once in index.css, overridable at runtime by
// lib/personaTheme.ts::applyTheme) instead of being pasted as a literal hex or rgb()/rgba() triple
// in a component or stylesheet. This is the ongoing enforcement half of that criterion -- the
// initial sweep is this same PR's diff.

// The only two places a literal brand hex may legitimately live: index.css (the token
// definitions themselves) and lib/personaTheme.ts (SONIC_THEME, the default theme object that
// feeds those tokens at runtime). Everything else under src -- every component, every other
// stylesheet -- must reference the tokens instead.
const sources: Record<string, string> = import.meta.glob<string>(
    ["../**/*.{ts,tsx,css}", "!../index.css", "!../lib/personaTheme.ts", "!../**/__tests__/**", "!../test/**"],
    { eager: true, query: "?raw", import: "default" }
);

// Sonic's brand palette (docs/persona-architecture.md row 39 and lib/personaTheme.ts's
// SONIC_THEME). Matched case-insensitively against literal hex codes and the equivalent decimal
// RGB triples, since both forms showed up in the pre-theming source.
const BRAND_HEX = [
    "E40046",
    "285780",
    "FEDD00",
    "18344D",
    "74D2E7",
    "137AC9",
    "FF4D7A",
    "FF6B8A",
    "FFE84D",
    "C31B24",
    "328500",
    "C9CFD4",
    "F2F8FA",
    "0F1A24",
    "152231"
];

// Same colors, as the decimal RGB triples that appeared inside rgba(...)/rgb(...) calls.
const BRAND_RGB_TRIPLES = ["228, 0, 70", "40, 87, 128", "254, 221, 0", "242, 248, 250"];

function findHexHits(text: string): string[] {
    const hits: string[] = [];
    for (const hex of BRAND_HEX) {
        const pattern = new RegExp(`#${hex}\\b`, "i");
        if (pattern.test(text)) hits.push(`#${hex}`);
    }
    return hits;
}

function findRgbHits(text: string): string[] {
    return BRAND_RGB_TRIPLES.filter(triple => text.includes(triple));
}

describe("brand colors stay behind theme tokens (issue #80 F2)", () => {
    const files = Object.keys(sources).sort();

    it("scanned at least the four files this slice's diff touched", () => {
        // A sanity check on the glob itself: if this ever drops to 0, the guard below would pass
        // vacuously and silently stop enforcing anything.
        expect(files.some(f => f.endsWith("App.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("order-summary.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("menu-panel.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("status-message.css"))).toBe(true);
    });

    it.each(files)("%s has no literal brand hex or brand rgb() triples", file => {
        const text = sources[file];
        expect({ hex: findHexHits(text), rgb: findRgbHits(text) }).toEqual({ hex: [], rgb: [] });
    });
});
