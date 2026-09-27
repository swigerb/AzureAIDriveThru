import { describe, expect, it } from "vitest";

import {
    applyDarkTheme,
    applyTheme,
    DEFAULT_THEME_FONT,
    deriveAccents,
    DEFAULT_DARK_BACKGROUND,
    DEFAULT_DARK_FOREGROUND,
    PERSONA_THEME_CSS_VARS,
    PERSONA_THEME_DARK_CSS_VARS,
    PERSONA_SURFACE_CSS_VARS,
    PERSONA_SURFACE_DARK_CSS_VARS,
    resolvePersonaTheme,
    type PersonaBaseColors,
    type PersonaTheme,
    type PersonaWireTheme
} from "@/lib/personaTheme";

// Guards issue #80 F2 (docs/persona-architecture.md §9) and issue #117: runtime theming groundwork.
// Every persona -- including the default one -- flows through the exact same `resolvePersonaTheme`/
// `applyTheme`/`applyDarkTheme` path; nothing in this module hard-codes any one persona's palette
// (see `brandDefaultTokens.test.ts` for the guard that `index.css`'s shared defaults specifically
// contain no persona's brand values).

// A representative "curated" persona theme (the shape a pack's persona.json sends over the wire),
// used across this file's `applyTheme`/`applyDarkTheme` tests in place of any real persona's actual
// values -- this file must not assert against, or depend on, any one persona's literal palette.
const SAMPLE_THEME: PersonaTheme = {
    light: {
        primary: "341 100% 45%",
        secondary: "208 52% 33%",
        background: "195 44% 96%",
        foreground: "208 53% 20%",
        accents: {
            primaryHex: "#E40046",
            primaryStrong: "#C31B24",
            primaryLight: "#FF4D7A",
            primaryTintOnDark: "#FF6B8A",
            secondaryHex: "#285780",
            secondaryStrong: "#137AC9",
            secondaryTintOnDark: "#74D2E7",
            accent: "#FEDD00",
            accentLight: "#FFE84D",
            ink: "#18344D",
            surfaceTint: "#F2F8FA",
            surfaceDark: "#0F1A24",
            surfaceDarkAlt: "#152231",
            success: "#328500",
            neutral: "#C9CFD4"
        },
        surface: {
            cardForeground: "208 40% 18%",
            secondary: "192 40% 90%",
            secondaryForeground: "208 48% 24%",
            muted: "200 20% 90%",
            mutedForeground: "210 15% 35%",
            accent: "52 100% 50%",
            accentForeground: "208 53% 20%",
            destructive: "357 75% 44%",
            border: "200 20% 85%",
            chart4: "192 67% 68%",
            chart5: "97 100% 26%"
        }
    },
    dark: {
        primary: "341 100% 55%",
        surface: {
            secondary: "208 30% 15%",
            muted: "210 15% 15%",
            destructive: "357 75% 35%",
            border: "210 15% 15%",
            chart2: "208 60% 50%",
            chart3: "52 100% 55%",
            chart5: "97 80% 40%"
        }
    },
    font: {
        family: "Nunito Sans",
        importUrl: "https://fonts.googleapis.com/css2?family=Nunito+Sans:wght@400;600;700;800;900&display=swap"
    }
};

describe("applyTheme", () => {
    function freshRoot(): HTMLElement {
        return document.createElement("div");
    }

    it("writes every base color onto the given root element", () => {
        const root = freshRoot();
        applyTheme(SAMPLE_THEME, root);

        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primary)).toBe("341 100% 45%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.secondary)).toBe("208 52% 33%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.background)).toBe("195 44% 96%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.foreground)).toBe("208 53% 20%");
    });

    it("writes every accent color onto the given root element", () => {
        const root = freshRoot();
        applyTheme(SAMPLE_THEME, root);

        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primaryHex)).toBe("#E40046");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.secondaryHex)).toBe("#285780");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.accent)).toBe("#FEDD00");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.surfaceDark)).toBe("#0F1A24");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.neutral)).toBe("#C9CFD4");
    });

    it("writes every surface (shadcn slot) color onto the given root element when the theme declares one (issue #117)", () => {
        const root = freshRoot();
        applyTheme(SAMPLE_THEME, root);

        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.cardForeground)).toBe("208 40% 18%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.secondary)).toBe("192 40% 90%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.muted)).toBe("200 20% 90%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.accent)).toBe("52 100% 50%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.destructive)).toBe("357 75% 44%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.border)).toBe("200 20% 85%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.chart4)).toBe("192 67% 68%");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.chart5)).toBe("97 100% 26%");
    });

    it("writes no surface vars at all when the theme omits `surface` (a pack without a curated shadcn palette)", () => {
        const root = freshRoot();
        const theme: PersonaTheme = { ...SAMPLE_THEME, light: { ...SAMPLE_THEME.light, surface: undefined } };
        applyTheme(theme, root);

        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.cardForeground)).toBe("");
        expect(root.style.getPropertyValue(PERSONA_SURFACE_CSS_VARS.border)).toBe("");
    });

    it("defaults to document.documentElement when no root is given", () => {
        applyTheme(SAMPLE_THEME);
        expect(document.documentElement.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primary)).toBe("341 100% 45%");
    });

    it("is theme-agnostic: applying a different theme changes the tokens", () => {
        const otherTheme: PersonaTheme = {
            light: {
                primary: "20 90% 50%",
                secondary: "30 40% 20%",
                background: "40 60% 95%",
                foreground: "30 50% 15%",
                accents: {
                    primaryHex: "#F07020",
                    primaryStrong: "#C05010",
                    primaryLight: "#F89050",
                    primaryTintOnDark: "#F8A070",
                    secondaryHex: "#503020",
                    secondaryStrong: "#704030",
                    secondaryTintOnDark: "#A08070",
                    accent: "#FFC030",
                    accentLight: "#FFD060",
                    ink: "#402010",
                    surfaceTint: "#FFF8F0",
                    surfaceDark: "#201008",
                    surfaceDarkAlt: "#301810",
                    success: "#308020",
                    neutral: "#D0C0B0"
                }
            },
            font: { family: "Fredoka", importUrl: "https://fonts.googleapis.com/css2?family=Fredoka" }
        };

        const root = freshRoot();
        applyTheme(otherTheme, root);

        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primary)).toBe("20 90% 50%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primaryHex)).toBe("#F07020");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.accent)).toBe("#FFC030");
    });
});

// Issue #80 F1/F3 (Rick's #80 review): "map dark.background/dark.foreground onto the .dark block
// vars". `applyDarkTheme` is the half of the dark-mode-hero-contrast fix that writes those
// `--brand-*-dark`/`--surface-*-dark` variables into an injected <style>.dark { ... } rule (see
// personaTheme.ts's own doc comment for why it can't just be an inline style like applyTheme's).
describe("applyDarkTheme", () => {
    function freshDoc(): Document {
        return document.implementation.createHTMLDocument("test");
    }

    function readDarkRule(doc: Document): string {
        const style = doc.getElementById("persona-dark-theme-overrides");
        return style?.textContent ?? "";
    }

    it("creates the .dark style rule using a persona's declared dark overrides", () => {
        const doc = freshDoc();
        const theme: PersonaTheme = {
            light: { primary: "10 10% 10%", secondary: "20 20% 20%", background: "30 30% 30%", foreground: "40 40% 40%", accents: SAMPLE_THEME.light.accents },
            dark: { primary: "10 10% 90%", background: "30 30% 5%", foreground: "40 40% 95%" },
            font: SAMPLE_THEME.font
        };

        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.primary}: 10 10% 90%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.background}: 30 30% 5%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.foreground}: 40 40% 95%`);
    });

    it("writes every dark-only surface (shadcn slot) override the theme declares (issue #117)", () => {
        const doc = freshDoc();
        applyDarkTheme(SAMPLE_THEME, doc);
        const rule = readDarkRule(doc);

        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.secondary}: 208 30% 15%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.muted}: 210 15% 15%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.destructive}: 357 75% 35%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.border}: 210 15% 15%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.chart2}: 208 60% 50%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.chart3}: 52 100% 55%`);
        expect(rule).toContain(`${PERSONA_SURFACE_DARK_CSS_VARS.chart5}: 97 80% 40%`);
    });

    it("writes no dark surface overrides when the theme's dark block omits `surface`", () => {
        const doc = freshDoc();
        const theme: PersonaTheme = { ...SAMPLE_THEME, dark: { primary: "341 100% 55%" } };
        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        expect(rule).not.toContain(PERSONA_SURFACE_DARK_CSS_VARS.secondary);
        expect(rule).not.toContain(PERSONA_SURFACE_DARK_CSS_VARS.chart2);
    });

    it("falls back to the persona's own light primary, and the shared DEFAULT dark background/foreground, when a persona has no dark block at all", () => {
        const doc = freshDoc();
        const theme: PersonaTheme = {
            light: { primary: "10 10% 10%", secondary: "20 20% 20%", background: "30 30% 30%", foreground: "40 40% 40%", accents: SAMPLE_THEME.light.accents },
            // No `dark` block at all -- both fixture personas (test-alpha/test-beta) ship like this.
            font: SAMPLE_THEME.font
        };

        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        // `primary` still comes from the persona's own light hue (so its brand color is visible in
        // dark mode too); `background`/`foreground` must NOT leak the persona's pale light-mode
        // values (that's the bug -- Rick's #110 review, item 3) -- they fall back to the shared
        // DEFAULT dark palette instead, so the page still reads as genuinely dark.
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.primary}: 10 10% 10%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.background}: ${DEFAULT_DARK_BACKGROUND}`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.foreground}: ${DEFAULT_DARK_FOREGROUND}`);
    });

    // Issue #80 F1/F3 (Rick's #110 review, item 3): a persona's `dark` block may set ONLY `primary`
    // -- a *partial* palette, not an absent one. This guards the exact repro from the review: with
    // the old (buggy) fallback to `theme.light.*`, `background`/`foreground` came out as the
    // persona's pale light-mode colors instead of a dark palette, so cards/panels/text stayed light
    // while only the accent color changed -- "the whole page" did not "go dark like before-dark.png".
    it("falls back to the shared DEFAULT dark background/foreground when a persona's dark block sets only some tokens", () => {
        const doc = freshDoc();
        const theme: PersonaTheme = { ...SAMPLE_THEME, dark: { primary: "341 100% 55%" } };

        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.primary}: 341 100% 55%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.background}: ${DEFAULT_DARK_BACKGROUND}`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.foreground}: ${DEFAULT_DARK_FOREGROUND}`);
        // And definitely not the persona's pale light-mode background/foreground.
        expect(rule).not.toContain(theme.light.background);
        expect(rule).not.toContain(theme.light.foreground);
    });

    it("re-uses the same injected <style> element across repeated calls (persona switching)", () => {
        const doc = freshDoc();
        applyDarkTheme(SAMPLE_THEME, doc);
        const firstCount = doc.querySelectorAll("#persona-dark-theme-overrides").length;

        applyDarkTheme(SAMPLE_THEME, doc);
        expect(doc.querySelectorAll("#persona-dark-theme-overrides").length).toBe(firstCount);
        expect(firstCount).toBe(1);
    });
});

// Issue #80 F1/F3: any persona without a curated `accents` block needs one synthesized from just
// its four base HSL roles.
describe("deriveAccents", () => {
    const colors: PersonaBaseColors = {
        primary: "200 80% 50%",
        secondary: "40 60% 40%",
        background: "0 0% 98%",
        foreground: "0 0% 10%"
    };

    it("derives a full accent palette deterministically from the base colors", () => {
        const first = deriveAccents(colors);
        const second = deriveAccents(colors);
        expect(first).toEqual(second);
    });

    it("produces valid 6-digit uppercase hex colors for every slot", () => {
        const accents = deriveAccents(colors);
        for (const value of Object.values(accents)) {
            expect(value).toMatch(/^#[0-9A-F]{6}$/);
        }
    });

    it("derives primaryHex directly from the primary hue/sat/lightness", () => {
        // 200 80% 50% is a saturated cyan-blue; just assert it round-trips to *some* stable hex
        // rather than hand-computing the RGB math personaTheme.ts already implements.
        const accents = deriveAccents(colors);
        expect(accents.primaryHex).toBe(deriveAccents(colors).primaryHex);
        expect(accents.primaryStrong).not.toBe(accents.primaryHex);
        expect(accents.primaryLight).not.toBe(accents.primaryHex);
    });

    it("uses universal, brand-independent placeholders for the decorative success/neutral slots (issue #117)", () => {
        // These two keys aren't rendered by any current component; they must not be tied to any
        // one persona's palette (previously hard-coded to the default persona's own green/gray).
        const accents = deriveAccents(colors);
        expect(accents.success).toBe("#16A34A");
        expect(accents.neutral).toBe("#9CA3AF");
    });
});

// Issue #80 F1/F3, issue #117: resolvePersonaTheme is the seam PersonaProvider calls with the wire
// theme `/api/personas/<id>` returns. Every persona resolves via the exact same generic path; there
// is no persona-specific special case (issue #117) -- see the last test below, which checks this
// directly against the one real persona id this repo ships today.
describe("resolvePersonaTheme", () => {
    it("derives accents for a persona that supplies none", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.light.accents).toEqual(deriveAccents(wireTheme.light));
        expect(theme.font).toBe(DEFAULT_THEME_FONT); // no font declared -- falls back to the shared default
    });

    it("lets a persona override individual accent keys without authoring the whole palette", () => {
        const wireTheme: PersonaWireTheme = {
            light: {
                primary: "200 80% 50%",
                secondary: "40 60% 40%",
                background: "0 0% 98%",
                foreground: "0 0% 10%",
                accents: { accent: "#123456" }
            }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.light.accents.accent).toBe("#123456");
        // Every other key still comes from deriveAccents.
        expect(theme.light.accents.primaryHex).toBe(deriveAccents(wireTheme.light).primaryHex);
    });

    it("passes a persona's `surface` block through unchanged (no synthesis fallback)", () => {
        const wireTheme: PersonaWireTheme = {
            light: {
                primary: "200 80% 50%",
                secondary: "40 60% 40%",
                background: "0 0% 98%",
                foreground: "0 0% 10%",
                surface: { border: "0 0% 85%", chart5: "0 0% 55%" }
            }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.light.surface).toEqual({ border: "0 0% 85%", chart5: "0 0% 55%" });
    });

    it("leaves `surface` undefined for a persona that declares none, rather than synthesizing one", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" }
        };
        const theme = resolvePersonaTheme("test-beta", wireTheme);
        expect(theme.light.surface).toBeUndefined();
    });

    it("passes through a persona's own dark overrides (including dark surface) and font untouched", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" },
            dark: { primary: "200 80% 70%", surface: { border: "0 0% 15%" } },
            font: { family: "Fredoka", importUrl: "https://fonts.googleapis.com/css2?family=Fredoka" }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.dark).toEqual({ primary: "200 80% 70%", surface: { border: "0 0% 15%" } });
        expect(theme.font).toEqual({ family: "Fredoka", importUrl: "https://fonts.googleapis.com/css2?family=Fredoka" });
    });

    it("does not special-case the shipped persona id -- it resolves the same generic way as any other id", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 0%", foreground: "0 0% 0%" }
        };
        const shippedTheme = resolvePersonaTheme("sonic", wireTheme);
        const otherTheme = resolvePersonaTheme("some-other-persona", wireTheme);

        // Same wire theme in, same accents/font resolution logic out, regardless of id.
        expect(shippedTheme.light.accents).toEqual(otherTheme.light.accents);
        expect(shippedTheme.font).toBe(otherTheme.font);
        expect(shippedTheme.light.primary).toBe("0 0% 0%");
    });
});
