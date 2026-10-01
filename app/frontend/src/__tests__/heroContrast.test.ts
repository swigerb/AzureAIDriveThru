import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { HERO_BODY_TEXT_CLASS } from "../App";
import { resolveTextRole } from "@/lib/personaTextRoles";
import type { PersonaTextRole, PersonaTextRoles } from "@/types/persona";

// Issue 164 R1 (PR #167 round 1 review, Rick): `BrandHero`'s description and tech line used to
// draw `text-brand-ink/70` and `text-brand-ink/60` respectively over the hero's `bg-white/80`
// frosted card, which composited to roughly #CECFCF in dark mode and measured as low as 2.81:1
// against it -- well under WCAG AA's 4.5:1 body-text floor. The fix (App.tsx's exported
// `HERO_BODY_TEXT_CLASS`) unifies both lines on one higher alpha. A className-presence check alone
// (App.brandComponents.test.tsx) only proves the class string is applied, not that the alpha it
// encodes is actually legible, so THIS file independently recomputes the real WCAG contrast ratio
// for every persona pack's `accents.ink` at that alpha, against both the light and dark page
// backgrounds, with a mutation check (reverting the alpha to the old buggy 60) that must fail --
// proving the assertion is sensitive to the alpha, not vacuously true for any value.
//
// R2 (same review, line 34): extends this file to also pin the resolved `footerTagline`/
// `countChip` text-role color against each pack's light page background (no alpha compositing --
// these roles render at full opacity; see lib/personaTextRoles.ts's `ROLE_TEXT_CLASS`).
// `footerExtra` is deliberately exempt: it is brand logotype text (e.g. "I'm Lovin' It"-style),
// not body copy, per Rick's review.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../");
const personasDir = path.resolve(repoRoot, "personas");
const indexCssText = readFileSync(path.resolve(here, "../index.css"), "utf-8");

/** Discovers every real persona pack on disk -- same convention as brandDefaultTokens.test.ts, so
 * a future pack is covered the moment it lands rather than needing a hardcoded id list here. */
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

interface LoadedPersonaTheme {
    packId: string;
    ink: string;
    primaryHex: string;
    secondaryHex: string;
    primaryDeep: string;
    accent: string;
    lightBackground: string;
    darkBackgroundOverride: string | undefined;
    textRoles: PersonaTextRoles | undefined;
}

function loadPersonaTheme(packId: string): LoadedPersonaTheme {
    const raw = readPersonaJson(packId);
    const theme = raw.ui.theme;
    const accents = theme.light.accents;
    return {
        packId,
        ink: accents.ink,
        primaryHex: accents.primaryHex,
        secondaryHex: accents.secondaryHex,
        // Both default to the plain hex per `deriveAccents` (personaTheme.ts) when a pack omits them.
        primaryDeep: accents.primaryDeep ?? accents.primaryHex,
        accent: accents.accent,
        lightBackground: theme.light.background,
        darkBackgroundOverride: theme.dark?.background,
        textRoles: raw.ui.textRoles
    };
}

const packIds = discoverPackIds();
const packThemes = packIds.map(loadPersonaTheme);
const packThemeRows = packThemes.map(theme => [theme.packId, theme] as const);

/** Maps a resolved text role to the accent hex it renders at -- the SAME CSS custom properties
 * `lib/personaTextRoles.ts`'s `ROLE_TEXT_CLASS` points each role's Tailwind class at, just read
 * here directly from the pack's JSON instead of via a browser's computed style (vitest's jsdom has
 * no real layout/paint engine to read a rendered color back out of). */
function roleColor(theme: LoadedPersonaTheme, role: PersonaTextRole): string {
    switch (role) {
        case "primary":
            return theme.primaryHex;
        case "primaryDeep":
            return theme.primaryDeep;
        case "secondary":
            return theme.secondaryHex;
        case "accent":
            return theme.accent;
        case "ink":
            return theme.ink;
    }
}

/** The alpha fraction (0-1) a Tailwind opacity-suffixed class (e.g. "text-brand-ink/90") encodes. */
function alphaFraction(tailwindClass: string): number {
    const match = tailwindClass.match(/\/(\d+)$/);
    if (!match) throw new Error(`"${tailwindClass}" has no /NN alpha suffix to read`);
    return Number(match[1]) / 100;
}

const HERO_ALPHA = alphaFraction(HERO_BODY_TEXT_CLASS);
/** Rick's R1: "Mutation check: setting the alpha back to 60 must fail it" -- the historic buggy value. */
const MUTATED_BUGGY_ALPHA = 0.6;

/** Shared `.dark` background fallback (`--background: var(--brand-background-dark, <literal>)`),
 * read from index.css itself rather than hardcoded, so this test tracks the real shared default if
 * it ever changes. None of the real packs on disk currently override `ui.theme.dark.background`
 * (confirmed against personas/*\/persona.json), so every pack composites against this one shared
 * literal today; a pack that DOES override it is still covered via `darkBackgroundOverride` above. */
function sharedDarkBackgroundDefault(): string {
    const match = indexCssText.match(/--background:\s*var\(--brand-background-dark,\s*([^)]+)\)/);
    if (!match) throw new Error("index.css's .dark --background fallback literal was not found");
    return match[1].trim();
}

function darkPageBackground(theme: LoadedPersonaTheme): string {
    return theme.darkBackgroundOverride ?? sharedDarkBackgroundDefault();
}

// ---- WCAG 2.x contrast math -------------------------------------------------------------------
// No contrast-ratio library exists in package.json today, so this is a small, self-contained
// implementation of the W3C formulas (https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio /
// #dfn-relative-luminance) rather than a hand-wave -- verified against Rick's own measured numbers
// (the worst-case real pack's dark frosted-card description measured "~5.0:1" at alpha 90)
// before being committed.

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

/** Parses either format persona.json uses for a color ("H S% L%" triplet, or hex). */
function parseColor(value: string): Rgb {
    return value.startsWith("#") ? parseHex(value) : parseHslTriplet(value);
}

/** Alpha-composites `fg` at `alpha` (0-1) over opaque `bg`: a per-channel linear blend in sRGB
 * space, matching how a browser paints a semi-transparent color over whatever is already painted
 * underneath it (browsers do not gamma-correct alpha compositing). */
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

const WHITE: Rgb = [255, 255, 255];
const FROSTED_CARD_ALPHA = 0.8; // the hero card's `bg-white/80`.
const WCAG_AA_BODY_TEXT_MINIMUM = 4.5;

/** Composites the hero card (`bg-white/80`) over `pageBackground`, then `ink` at `heroAlpha` over
 * that card -- exactly the two-layer stack `BrandHero` paints (App.tsx's `hero-card` section plus
 * its description/tech-line text). Returns both layers so the caller can compute contrast. */
function heroTextOverCard(ink: string, heroAlpha: number, pageBackground: string): { text: Rgb; card: Rgb } {
    const card = compositeOver(WHITE, FROSTED_CARD_ALPHA, parseColor(pageBackground));
    const text = compositeOver(parseHex(ink), heroAlpha, card);
    return { text, card };
}

describe("hero description/tech-line WCAG contrast pinning (issue 164 R1, PR 167 round 1 review)", () => {
    it("sanity: at least one real persona pack was discovered", () => {
        expect(packIds.length).toBeGreaterThan(0);
    });

    it("HERO_BODY_TEXT_CLASS is the expected exported constant (so the alpha this file reads is the one App.tsx actually renders)", () => {
        expect(HERO_BODY_TEXT_CLASS).toBe("text-brand-ink/90");
        expect(HERO_ALPHA).toBe(0.9);
    });

    it.each(packThemeRows)(
        "%s: accents.ink at HERO_BODY_TEXT_CLASS's alpha reaches >= 4.5:1 over the light-mode frosted hero card",
        (_packId, theme) => {
            const { text, card } = heroTextOverCard(theme.ink, HERO_ALPHA, theme.lightBackground);
            expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
        }
    );

    it.each(packThemeRows)(
        "%s: accents.ink at HERO_BODY_TEXT_CLASS's alpha reaches >= 4.5:1 over the dark-mode frosted hero card",
        (_packId, theme) => {
            const { text, card } = heroTextOverCard(theme.ink, HERO_ALPHA, darkPageBackground(theme));
            expect(contrastRatio(text, card)).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
        }
    );

    it("mutation check: reverting the alpha to the old buggy 60 fails the worst-case real pack in both modes", () => {
        // Proves the two it.each blocks above are actually sensitive to HERO_BODY_TEXT_CLASS's
        // alpha, not vacuously true for any alpha. Rather than name a specific brand pack (R6:
        // brand words stay out of shared/test code), this picks whichever real pack has the
        // lowest dark-mode contrast ratio at the real alpha -- the worst case is exactly the one
        // most likely to regress below the WCAG floor if the alpha were ever lowered again.
        const worstCase = packThemes.reduce((worst, theme) => {
            const ratio = contrastRatio(
                heroTextOverCard(theme.ink, HERO_ALPHA, darkPageBackground(theme)).text,
                heroTextOverCard(theme.ink, HERO_ALPHA, darkPageBackground(theme)).card
            );
            const worstRatio = contrastRatio(
                heroTextOverCard(worst.ink, HERO_ALPHA, darkPageBackground(worst)).text,
                heroTextOverCard(worst.ink, HERO_ALPHA, darkPageBackground(worst)).card
            );
            return ratio < worstRatio ? theme : worst;
        });
        const lightAtBuggyAlpha = heroTextOverCard(worstCase.ink, MUTATED_BUGGY_ALPHA, worstCase.lightBackground);
        const darkAtBuggyAlpha = heroTextOverCard(worstCase.ink, MUTATED_BUGGY_ALPHA, darkPageBackground(worstCase));
        expect(contrastRatio(lightAtBuggyAlpha.text, lightAtBuggyAlpha.card)).toBeLessThan(WCAG_AA_BODY_TEXT_MINIMUM);
        expect(contrastRatio(darkAtBuggyAlpha.text, darkAtBuggyAlpha.card)).toBeLessThan(WCAG_AA_BODY_TEXT_MINIMUM);
    });
});

describe("footerTagline/countChip WCAG contrast on each pack's light background (issue 164 R2 line 34)", () => {
    it.each(packThemeRows)("%s: the resolved footerTagline role reaches >= 4.5:1 on the light page background", (_packId, theme) => {
        const role = resolveTextRole("footerTagline", theme.textRoles);
        const ratio = contrastRatio(parseHex(roleColor(theme, role)), parseColor(theme.lightBackground));
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
    });

    it.each(packThemeRows)("%s: the resolved countChip role reaches >= 4.5:1 on the light page background", (_packId, theme) => {
        const role = resolveTextRole("countChip", theme.textRoles);
        const ratio = contrastRatio(parseHex(roleColor(theme, role)), parseColor(theme.lightBackground));
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_BODY_TEXT_MINIMUM);
    });

    it("footerExtra is intentionally not asserted here -- brand logotype text, exempt per Rick's review (R2 line 34)", () => {
        expect(packThemes.length).toBeGreaterThan(0);
    });
});
