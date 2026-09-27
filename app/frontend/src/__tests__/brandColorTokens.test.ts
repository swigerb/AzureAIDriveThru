import { describe, expect, it } from "vitest";

// Guards issue #80 F2 (docs/persona-architecture.md §9, acceptance criteria on issue #80): "No
// brand hex values remain in app/frontend/src". Every color must go through a `--brand-*` CSS
// custom property (defined once in index.css, overridable at runtime by
// lib/personaTheme.ts::applyTheme) instead of being pasted as a literal color value in a component
// or stylesheet.
//
// PR #91 review round 2 (Rick): round 1 of this guard only banned Sonic's own 15 hex values, so any
// other brand's color, a one-digit-off Sonic shade, a 3/4/8-digit hex, an hsl()/hsla() literal, or a
// Tailwind arbitrary value (`bg-[#DA291C]`, `text-[rgb(...)]`) all passed it. This version instead
// bans every color literal -- any hex of 3, 4, 6 or 8 digits, and any rgb()/rgba()/hsl()/hsla() call
// with a literal numeric argument -- everywhere under src except the two token-definition files,
// with a short, explicit, commented allowlist for fully-neutral (black or white) overlays that
// carry no brand hue. Tailwind arbitrary values need no special-case handling
// here: `bg-[#DA291C]` and `shadow-[0_4px_8px_rgba(0,0,0,0.2)]` just embed the literal CSS value
// inside brackets, so the same regexes that catch a plain CSS/inline-style literal catch those too.

// The only two places a literal color may legitimately live: index.css (the token definitions
// themselves) and lib/personaTheme.ts (SONIC_THEME, the default theme object that feeds those
// tokens at runtime). Everything else under src -- every component, every other stylesheet --
// must reference a token instead. Test files are excluded because their fixtures (including this
// file's own self-test literals below) legitimately contain color literals as *data*, not as
// unguarded UI colors.
const sources: Record<string, string> = import.meta.glob<string>(
    ["../**/*.{ts,tsx,css}", "!../index.css", "!../lib/personaTheme.ts", "!../**/__tests__/**", "!../test/**"],
    { eager: true, query: "?raw", import: "default" }
);

// Matches hex color literals of exactly 3, 4, 6 or 8 hex digits -- CSS's only valid hex-color
// lengths. The negative lookahead keeps a 6-digit match from swallowing part of an adjacent
// 8-digit one (or vice versa) and rejects a would-be 5/7-digit run so neither half is reported as
// if it were valid.
const HEX_LITERAL = /#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3})(?![0-9a-f])/gi;

// Matches rgb()/rgba()/hsl()/hsla() with a literal numeric first argument -- e.g. `rgba(228,0,70,.1)`
// or `hsl(9, 74%, 47%)`. Deliberately does NOT match `hsl(var(--brand-primary))`: the character
// right after "(" there is "v", not a digit/sign/dot, which is exactly how the token files
// themselves (and any future caller) stay clean while still reading the tokens through hsl().
// Uses a negative lookbehind instead of `\b`: Tailwind arbitrary values glue class name and
// literal with underscores (`shadow-[0_4px_8px_rgba(0,0,0,0.2)]`), and `\b` does not fire between
// two word characters like `_` and `r`, which would have silently let that exact shape through.
const FUNCTION_LITERAL = /(?<![a-zA-Z])(?:rgba?|hsla?)\(\s*[-+\d.]/gi;

// Explicit, commented allowlist: the only literal colors allowed outside the two token files.
// Every entry is a fully neutral (black or white) overlay with no brand hue, so it can never hide
// a persona-specific color; extend this list only with the same justification. These two literals
// are the black shadow in App.tsx's HeroHighlightCard and menu-panel.tsx's dark-mode card shadow.
const ALLOWLIST = [/rgba\(\s*0\s*,\s*0\s*,\s*0\s*,\s*[\d.]+\s*\)/gi, /rgba\(\s*255\s*,\s*255\s*,\s*255\s*,\s*[\d.]+\s*\)/gi];

function stripAllowlisted(text: string): string {
    return ALLOWLIST.reduce((acc, pattern) => acc.replace(pattern, ""), text);
}

function findLiteralHits(text: string): string[] {
    const clean = stripAllowlisted(text);
    return [...clean.matchAll(HEX_LITERAL), ...clean.matchAll(FUNCTION_LITERAL)].map(match => match[0]);
}

describe("no raw color literals outside the theme token files (issue #80 F2, PR #91 round 2)", () => {
    const files = Object.keys(sources).sort();

    it("scanned at least the four files this slice's diff touched", () => {
        // A sanity check on the glob itself: if this ever drops to 0, the guard below would pass
        // vacuously and silently stop enforcing anything.
        expect(files.some(f => f.endsWith("App.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("order-summary.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("menu-panel.tsx"))).toBe(true);
        expect(files.some(f => f.endsWith("status-message.css"))).toBe(true);
    });

    it.each(files)("%s has no non-allowlisted color literal", file => {
        expect(findLiteralHits(sources[file])).toEqual([]);
    });

    it("does not flag the neutral allowlist itself (so real files aren't falsely blocked)", () => {
        expect(findLiteralHits('className="shadow-[0_10px_25px_rgba(0,0,0,0.08)]"')).toEqual([]);
        expect(findLiteralHits('className="dark:shadow-[0_25px_55px_rgba(0,0,0,0.65)]"')).toEqual([]);
        expect(findLiteralHits('className="bg-white/80 border-white/40"')).toEqual([]);
    });

    describe("self-test: the guard rejects every literal shape it claims to (mutation check)", () => {
        it.each([
            ["a non-Sonic brand hex (McDonald's red)", 'className="text-[#DA291C]"'],
            ["a near-shade of Sonic's own red (one hex digit off #E40046)", 'className="bg-[#E40047]"'],
            ["a 3-digit hex", "background: #fff;"],
            ["an 8-digit hex (alpha channel)", "border-color: #DA291Cff;"],
            ["an hsl() literal", "color: hsl(9, 74%, 47%);"],
            ["a Tailwind arbitrary value using rgb()", 'className="text-[rgb(218,41,28)]"'],
            ["a Tailwind arbitrary value with the literal underscore-glued to rgba() (no word boundary)", 'className="shadow-[0_4px_8px_rgba(218,41,28,0.3)]"']
        ])("flags a fixture containing %s", (_label, fixture) => {
            expect(findLiteralHits(fixture).length).toBeGreaterThan(0);
        });

        it("also flags a 4-digit hex (#rgba shorthand)", () => {
            expect(findLiteralHits("border-color: #f00a;").length).toBeGreaterThan(0);
        });

        it("negative control: a token reference alone is not flagged", () => {
            // If this fired, the guard would be so broad it'd block the legitimate `var(--brand-*)`
            // usage every real component relies on -- proving the regex targets literals, not tokens.
            expect(findLiteralHits('className="bg-brand-primary text-brand-secondary" style="color: hsl(var(--brand-primary))"')).toEqual(
                []
            );
        });
    });
});
