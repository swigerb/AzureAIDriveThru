import { describe, expect, it } from "vitest";

import {
    applyDarkTheme,
    applyTheme,
    deriveAccents,
    PERSONA_THEME_CSS_VARS,
    PERSONA_THEME_DARK_CSS_VARS,
    resolvePersonaTheme,
    SONIC_THEME,
    type PersonaBaseColors,
    type PersonaTheme,
    type PersonaWireTheme
} from "@/lib/personaTheme";

// Guards issue #80 F2 (docs/persona-architecture.md §9): runtime theming groundwork. The default
// persona must stay the default theme and applying it must not change any value from what index.css already
// hard-codes -- these tests would fail if a future edit let SONIC_THEME and index.css's `:root`/
// `.dark` defaults drift apart, which is the one way this "seam" could silently break the "looks
// IDENTICAL today" requirement.

describe("SONIC_THEME", () => {
    it("matches index.css's :root brand tokens exactly", () => {
        expect(SONIC_THEME.light.primary).toBe("341 100% 45%");
        expect(SONIC_THEME.light.secondary).toBe("208 52% 33%");
        expect(SONIC_THEME.light.background).toBe("195 44% 96%");
        expect(SONIC_THEME.light.foreground).toBe("208 53% 20%");
    });

    it("matches index.css's .dark override for primary", () => {
        expect(SONIC_THEME.dark?.primary).toBe("341 100% 55%");
    });

    it("carries every accent color index.css's :root previously hard-coded per component", () => {
        expect(SONIC_THEME.light.accents).toEqual({
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
        });
    });

    it("uses the default persona's font already imported by index.css", () => {
        expect(SONIC_THEME.font.family).toBe("Nunito Sans");
        expect(SONIC_THEME.font.importUrl).toMatch(/^https:\/\/fonts\.googleapis\.com\//);
    });
});

describe("applyTheme", () => {
    function freshRoot(): HTMLElement {
        return document.createElement("div");
    }

    it("writes every base color onto the given root element", () => {
        const root = freshRoot();
        applyTheme(SONIC_THEME, root);

        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primary)).toBe("341 100% 45%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.secondary)).toBe("208 52% 33%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.background)).toBe("195 44% 96%");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.foreground)).toBe("208 53% 20%");
    });

    it("writes every accent color onto the given root element", () => {
        const root = freshRoot();
        applyTheme(SONIC_THEME, root);

        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primaryHex)).toBe("#E40046");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.secondaryHex)).toBe("#285780");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.accent)).toBe("#FEDD00");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.surfaceDark)).toBe("#0F1A24");
        expect(root.style.getPropertyValue(PERSONA_THEME_CSS_VARS.neutral)).toBe("#C9CFD4");
    });

    it("defaults to document.documentElement when no root is given", () => {
        applyTheme(SONIC_THEME);
        expect(document.documentElement.style.getPropertyValue(PERSONA_THEME_CSS_VARS.primary)).toBe("341 100% 45%");
    });

    it("is the seam a future persona theme would use: applying a different theme changes the tokens", () => {
        // Stands in for a theme F1's PersonaProvider would fetch from /api/personas/<id> once #74
        // exists -- this test only proves applyTheme itself is theme-agnostic, not that any other
        // persona is wired up yet (it isn't; personas/** is out of this slice's scope).
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
// vars". `applyDarkTheme` is the half of the dark-mode-hero-contrast fix that writes those three
// `--brand-*-dark` variables into an injected <style>.dark { ... } rule (see personaTheme.ts's own
// doc comment for why it can't just be an inline style like applyTheme's).
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
            light: { primary: "10 10% 10%", secondary: "20 20% 20%", background: "30 30% 30%", foreground: "40 40% 40%", accents: SONIC_THEME.light.accents },
            dark: { primary: "10 10% 90%", background: "30 30% 5%", foreground: "40 40% 95%" },
            font: SONIC_THEME.font
        };

        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.primary}: 10 10% 90%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.background}: 30 30% 5%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.foreground}: 40 40% 95%`);
    });

    it("falls back to the light values for any key a persona's dark block omits", () => {
        const doc = freshDoc();
        const theme: PersonaTheme = {
            light: { primary: "10 10% 10%", secondary: "20 20% 20%", background: "30 30% 30%", foreground: "40 40% 40%", accents: SONIC_THEME.light.accents },
            // No `dark` block at all -- both fixture personas (test-alpha/test-beta) ship like this.
            font: SONIC_THEME.font
        };

        applyDarkTheme(theme, doc);
        const rule = readDarkRule(doc);

        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.primary}: 10 10% 10%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.background}: 30 30% 30%`);
        expect(rule).toContain(`${PERSONA_THEME_DARK_CSS_VARS.foreground}: 40 40% 40%`);
    });

    it("re-uses the same injected <style> element across repeated calls (persona switching)", () => {
        const doc = freshDoc();
        applyDarkTheme(SONIC_THEME, doc);
        const firstCount = doc.querySelectorAll("#persona-dark-theme-overrides").length;

        applyDarkTheme(SONIC_THEME, doc);
        expect(doc.querySelectorAll("#persona-dark-theme-overrides").length).toBe(firstCount);
        expect(firstCount).toBe(1);
    });
});

// Issue #80 F1/F3: any non-default persona needs its accent palette synthesized from just its
// four base HSL roles, since `accents` isn't in persona.schema.json yet.
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
});

// Issue #80 F1/F3: resolvePersonaTheme is the seam PersonaProvider calls with the wire theme
// `/api/personas/<id>` returns -- the default persona must keep resolving to the literal SONIC_THEME
// object (same identity) so F2's byte-identical guarantee can never regress via this path.
describe("resolvePersonaTheme", () => {
    it("resolves the default persona id to the literal SONIC_THEME object, ignoring the wire theme given", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 0%", foreground: "0 0% 0%" }
        };
        expect(resolvePersonaTheme("sonic", wireTheme)).toBe(SONIC_THEME);
    });

    it("derives accents for a non-default persona that supplies none", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.light.accents).toEqual(deriveAccents(wireTheme.light));
        expect(theme.font).toBe(SONIC_THEME.font); // no font declared -- falls back to the default persona's
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

    it("passes through a persona's own dark overrides and font untouched", () => {
        const wireTheme: PersonaWireTheme = {
            light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" },
            dark: { primary: "200 80% 70%" },
            font: { family: "Fredoka", importUrl: "https://fonts.googleapis.com/css2?family=Fredoka" }
        };
        const theme = resolvePersonaTheme("test-alpha", wireTheme);
        expect(theme.dark).toEqual({ primary: "200 80% 70%" });
        expect(theme.font).toEqual({ family: "Fredoka", importUrl: "https://fonts.googleapis.com/css2?family=Fredoka" });
    });
});
