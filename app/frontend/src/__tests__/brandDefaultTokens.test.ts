import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { deriveAccents, DEFAULT_THEME_FONT } from "@/lib/personaTheme";

// Guards issue #117: "index.css default brand tokens (light and dark) become neutral (grays / a
// neutral accent), not Sonic's palette" -- and, symmetrically, that Sonic's full palette now lives
// ONLY in its own pack (personas/sonic/persona.json), not baked into the shared default tokens
// anywhere in app/frontend/src. Complements brandColorTokens.test.ts (#91, "no literal color values
// outside the two token files"): that guard says literals may only live in these two files; THIS
// guard says which literals those files may hold -- Sonic's, specifically, must not be among them.
//
// Reads personas/sonic/persona.json directly (not a frontend build artifact) so this test fails the
// moment Sonic's own theme literals reappear in the shared defaults, regardless of which file they
// were pasted into.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../");

const indexCssText = readFileSync(path.resolve(here, "../index.css"), "utf-8");
const sonicPersona = JSON.parse(readFileSync(path.resolve(repoRoot, "personas/sonic/persona.json"), "utf-8"));

/** Recursively collects every string leaf under an object/array, skipping URL-shaped values (the
 * shared font, e.g., is intentionally the SAME literal in both Sonic's persona.json and
 * `DEFAULT_THEME_FONT` -- it's the app-wide default font, not a brand color, see personaTheme.ts). */
function collectStringLeaves(value: unknown, out: string[] = []): string[] {
    if (typeof value === "string") {
        if (!value.startsWith("http")) out.push(value);
    } else if (Array.isArray(value)) {
        for (const item of value) collectStringLeaves(item, out);
    } else if (value && typeof value === "object") {
        for (const item of Object.values(value)) collectStringLeaves(item, out);
    }
    return out;
}

const sonicTheme = sonicPersona.ui.theme;
// Only the color-bearing `light`/`dark` blocks -- `font` is deliberately excluded: Sonic's
// persona.json happens to declare the SAME shared `DEFAULT_THEME_FONT` family/URL (it doesn't have
// a bespoke webfont), so comparing against it would false-positive on every persona, not just Sonic.
const sonicThemeValues = [...collectStringLeaves(sonicTheme.light), ...collectStringLeaves(sonicTheme.dark)];

describe("shared default tokens contain no Sonic brand palette values (issue #117)", () => {
    it("sanity: Sonic's persona.json theme block actually has values to compare against", () => {
        // If this ever drops to 0 (e.g. a future refactor renames `ui.theme`), the guard below
        // would pass vacuously and silently stop enforcing anything.
        expect(sonicThemeValues.length).toBeGreaterThan(20);
        expect(sonicThemeValues).toContain("#E40046"); // Sonic's primaryHex
        expect(sonicThemeValues).toContain("341 100% 45%"); // Sonic's light primary
    });

    it.each(sonicThemeValues)("index.css's shared defaults do not contain Sonic's theme value %s", value => {
        expect(indexCssText).not.toContain(value);
    });

    it("index.css's :root/.dark brand base roles are a neutral gray, not Sonic's hue", () => {
        expect(indexCssText).toContain("--brand-primary: 220 9% 30%");
        expect(indexCssText).toContain("--brand-primary-dark, 220 9% 60%");
        // Sonic's saturated pink/red/teal/yellow hues (100% saturation, non-220 hue) must be gone.
        expect(indexCssText).not.toMatch(/--brand-(primary|secondary|background|foreground):\s*(?:34[01]|208|195)\s/);
    });

    it("index.css's brand accent hex constants are desaturated grays, not Sonic's saturated brand hex", () => {
        expect(indexCssText).toContain("--brand-primary-hex: #464A53");
        expect(indexCssText).toContain("--brand-accent: #9CA3AF");
        for (const hex of ["#E40046", "#FEDD00", "#285780", "#18344D", "#0F1A24"]) {
            expect(indexCssText).not.toContain(hex);
        }
    });

    it("deriveAccents' universal success/neutral fallback hex are not Sonic's literal values", () => {
        const accents = deriveAccents({ primary: "0 0% 50%", secondary: "0 0% 50%", background: "0 0% 100%", foreground: "0 0% 0%" });
        expect(accents.success).not.toBe("#328500"); // Sonic's success hex
        expect(accents.neutral).not.toBe("#C9CFD4"); // Sonic's neutral hex
    });

    it("the shared default font is the app-wide webfont, not gated on any persona id", () => {
        // Sonic happens to declare this SAME font in its own persona.json (it uses the shared
        // default rather than a bespoke one) -- so this only proves DEFAULT_THEME_FONT isn't
        // Sonic-specific plumbing, not that the two must differ.
        expect(DEFAULT_THEME_FONT.family).toBe("Nunito Sans");
        expect(sonicTheme.light.accents.primaryHex).toBe("#E40046"); // Sonic's own pack still has its palette
    });
});
