import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { deriveAccents, DEFAULT_THEME_FONT } from "@/lib/personaTheme";

// Guards issue #117: "index.css default brand tokens (light and dark) become neutral (grays / a
// neutral accent), not any one persona pack's palette" -- and, symmetrically, that a pack's full
// palette lives ONLY in its own pack (personas/<id>/persona.json), never baked into the shared
// default tokens anywhere in app/frontend/src. Complements brandColorTokens.test.ts (#91, "no
// literal color values outside the two token files"): that guard says literals may only live in
// these two files; THIS guard says which literals those files may hold -- no pack's, specifically,
// may be among them.
//
// Rick review round 2 (#120): this guard is now pack-agnostic. It discovers every pack under
// personas/ from disk (the same idea as the Python backend's `_discovered_persona_ids()`, #114)
// instead of hard-coding one pack's path and palette, so it automatically covers every existing and
// future pack with no brand literals living in this file.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../");
const personasDir = path.resolve(repoRoot, "personas");

const indexCssText = readFileSync(path.resolve(here, "../index.css"), "utf-8");

/** Discovers every persona pack on disk: any directory directly under personas/ that contains a
 * persona.json. Mirrors the backend's own pack-discovery convention rather than hard-coding a
 * pack list, so a new pack (e.g. #111, #78) is covered the moment it lands on disk. */
function discoverPackIds(): string[] {
    if (!existsSync(personasDir)) return [];
    return readdirSync(personasDir, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => entry.name)
        .filter(id => existsSync(path.resolve(personasDir, id, "persona.json")))
        .sort();
}

function loadPersona(packId: string): Record<string, unknown> {
    return JSON.parse(readFileSync(path.resolve(personasDir, packId, "persona.json"), "utf-8"));
}

/** Recursively collects every string leaf under an object/array, skipping URL-shaped values (a
 * pack's font import URL can legitimately match the shared `DEFAULT_THEME_FONT` when it uses the
 * app-wide default webfont rather than a bespoke one -- see personaTheme.ts). */
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

/** True for achromatic values -- pure white/black/gray carries no brand hue, so a pack that
 * legitimately declares e.g. pure white must not false-fail this guard against the shared neutral
 * gray defaults. Recognizes HSL triplets ("H S% L%") and hex (#RGB / #RRGGBB). */
function isAchromatic(value: string): boolean {
    const hslMatch = value.match(/^-?\d+(?:\.\d+)?\s+(\d+(?:\.\d+)?)%\s+\d+(?:\.\d+)?%$/);
    if (hslMatch) return parseFloat(hslMatch[1]) === 0;

    const hexMatch = value.match(/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/);
    if (hexMatch) {
        const hex = hexMatch[1];
        const [r, g, b] =
            hex.length === 3 ? hex.split("").map(c => parseInt(c + c, 16)) : [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16));
        return r === g && g === b;
    }
    return false;
}

const packIds = discoverPackIds();

type PackThemeValueRow = [packId: string, value: string];

/** [packId, chromatic theme value] rows from every discovered pack's `ui.theme.light`/`.dark`. */
const packThemeValueRows: PackThemeValueRow[] = packIds.flatMap(packId => {
    const persona = loadPersona(packId);
    const theme = (persona.ui as Record<string, unknown> | undefined)?.theme as
        | { light?: unknown; dark?: unknown }
        | undefined;
    if (!theme) return [];
    const values = [...collectStringLeaves(theme.light ?? {}), ...collectStringLeaves(theme.dark ?? {})];
    return values.filter(v => !isAchromatic(v)).map((v): PackThemeValueRow => [packId, v]);
});

describe("shared default tokens contain no persona pack's brand palette values (issue #117)", () => {
    it("sanity: at least one pack was discovered, and every pack contributes a chromatic value", () => {
        // If discovery ever silently returned nothing (e.g. a future refactor moves personas/
        // elsewhere), or a pack's theme block were entirely achromatic, the it.each guard below
        // would pass vacuously and silently stop enforcing anything for that pack.
        expect(packIds.length).toBeGreaterThan(0);
        const packsWithChromaticValues = new Set(packThemeValueRows.map(([packId]) => packId));
        for (const packId of packIds) {
            expect(packsWithChromaticValues.has(packId)).toBe(true);
        }
    });

    it.each(packThemeValueRows)("index.css's shared defaults do not contain the %s pack's theme value %s", (_packId, value) => {
        expect(indexCssText).not.toContain(value);
    });

    it("index.css's :root/.dark brand base roles are neutral (HSL saturation <= 20%)", () => {
        // Matches both the :root direct declaration ("--brand-primary: 220 9% 30%;") and the
        // .dark var() fallback ("var(--brand-primary-dark, 220 9% 60%)") -- no hardcoded brand hue
        // list, just a real neutrality check on whatever HSL triplet index.css actually ships.
        // Threshold is 20%, not a lower round number, because the current .dark background/card/
        // popover fallback ("210 20% 5%", predates issue #117 -- introduced by the original
        // full-rebrand commit that this issue's pack-agnostic follow-up didn't touch) is the
        // highest-saturation value any of these four roles ships today.
        const roleTokenRegex = /--brand-(primary|secondary|background|foreground)(-dark)?[,:]\s*(-?\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)%\s+(\d+(?:\.\d+)?)%/g;
        const matches = [...indexCssText.matchAll(roleTokenRegex)];
        // Four base roles, each with a :root default and a .dark fallback, is the current shape --
        // asserting a minimum keeps this from passing vacuously if the regex ever stops matching.
        expect(matches.length).toBeGreaterThanOrEqual(8);
        for (const match of matches) {
            const saturation = parseFloat(match[4]);
            expect(saturation).toBeLessThanOrEqual(20);
        }
    });

    it("index.css's own neutral accent hex defaults are present", () => {
        expect(indexCssText).toContain("--brand-primary-hex: #464A53");
        expect(indexCssText).toContain("--brand-accent: #9CA3AF");
    });

    it("deriveAccents' universal success/neutral fallback are not any pack's literal values", () => {
        const accents = deriveAccents({ primary: "0 0% 50%", secondary: "0 0% 50%", background: "0 0% 100%", foreground: "0 0% 0%" });
        for (const packId of packIds) {
            const persona = loadPersona(packId);
            const theme = (persona.ui as Record<string, unknown> | undefined)?.theme as { light?: { accents?: Record<string, string> } } | undefined;
            const packAccents = theme?.light?.accents;
            if (!packAccents) continue;
            if (packAccents.success) expect(accents.success).not.toBe(packAccents.success);
            if (packAccents.neutral) expect(accents.neutral).not.toBe(packAccents.neutral);
        }
    });

    it("the shared default font is the app-wide webfont, not gated on any persona id", () => {
        // A pack may legitimately declare this SAME font family/URL when it uses the shared
        // default rather than a bespoke webfont -- that's covered by the URL-skip in
        // collectStringLeaves above, not asserted here. Pack content otherwise is the pack's own
        // business, not this guard's.
        expect(DEFAULT_THEME_FONT.family).toBe("Nunito Sans");
    });
});
