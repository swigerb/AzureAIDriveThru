import { describe, expect, it } from "vitest";

import { applyTheme, PERSONA_THEME_CSS_VARS, SONIC_THEME, type PersonaTheme } from "@/lib/personaTheme";

// Guards issue #80 F2 (docs/persona-architecture.md §9): runtime theming groundwork. Sonic must
// stay the default theme and applying it must not change any value from what index.css already
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

    it("uses the Sonic font already imported by index.css", () => {
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
