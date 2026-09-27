/**
 * Runtime persona theming groundwork (issue #80, design doc §4.2 and §9 row F2).
 *
 * `PersonaTheme` mirrors the `ui.theme` shape a persona pack's `persona.json` will expose once
 * `PersonaProvider` (F1) can fetch `/api/personas/<id>` (blocked on #74). Until then, `SONIC_THEME`
 * below is the only theme in play and `applyTheme` is called once, at bootstrap, with that
 * hard-coded object -- so the UI renders visually identical to the pre-theming build while proving
 * the runtime plumbing works end to end.
 *
 * The `light`/`dark` base colors hold the four keys the design doc's abridged `persona.json` sample
 * documents today (`primary`, `secondary`, `background`, `foreground`, each an "H S% L%" triplet
 * with no `hsl()` wrapper -- exactly how persona.json and index.css's existing `--brand-primary`/
 * `--brand-secondary`/`--brand-background`/`--brand-foreground` tokens already store them). `accents` is this
 * slice's own addition: every other brand color this persona's components had hard-coded as literal hex
 * (87 of them per the design doc's diff, ~96 by this repo's current count) collapses into these
 * named slots. `accents` isn't in persona.schema.json yet -- promoting it there, if a second
 * persona needs it, is a follow-up for whoever picks up F1/F3+ (and for Summer, who owns the
 * schema).
 */

/** An "H S% L%" triplet, unwrapped -- the same string format persona.json uses for its theme colors. */
export type HslTriplet = string;

export interface PersonaBaseColors {
    primary: HslTriplet;
    secondary: HslTriplet;
    background: HslTriplet;
    foreground: HslTriplet;
}

/**
 * Extended brand accent palette. Plain hex (not HSL) so a persona's exact pre-theming colors carry
 * over with zero rounding drift -- HSL round-trips through integer-degree hue lose a couple of RGB
 * units per channel, which is invisible on screen but not byte-identical, and this slice's bar is
 * "looks IDENTICAL today".
 */
export interface PersonaAccentPalette {
    /** Exact hex of `primary`, for spots (SVG fill/stroke, plain solid colors) that need it byte-identical rather than via `hsl(var(...))`. */
    primaryHex: string;
    /** Hover/pressed shade of `primary`. */
    primaryStrong: string;
    /** Lighter shade of `primary`, used as a gradient endpoint. */
    primaryLight: string;
    /** Shade of `primary` legible on dark surfaces (dark-mode text/badges). */
    primaryTintOnDark: string;
    /** Exact hex of `secondary`, for the same byte-identical reason as `primaryHex`. */
    secondaryHex: string;
    /** Brighter shade of `secondary`, used as a gradient endpoint. */
    secondaryStrong: string;
    /** Shade of `secondary` legible on dark surfaces (dark-mode text/badges). */
    secondaryTintOnDark: string;
    /** Accent hue distinct from primary/secondary (the default persona's yellow). */
    accent: string;
    /** Lighter shade of `accent`, used as a gradient endpoint. */
    accentLight: string;
    /** Dark ink used for body copy over light brand surfaces. */
    ink: string;
    /** Very light brand-tinted surface (hero card backgrounds, gradients). */
    surfaceTint: string;
    /** Dark-mode panel surface. */
    surfaceDark: string;
    /** Dark-mode secondary panel surface. */
    surfaceDarkAlt: string;
    /** Decorative success green (unused by any current component; kept for pack compatibility). */
    success: string;
    /** Decorative neutral gray (unused by any current component; kept for pack compatibility). */
    neutral: string;
}

export interface PersonaThemeFont {
    family: string;
    importUrl: string;
}

export interface PersonaTheme {
    light: PersonaBaseColors & { accents: PersonaAccentPalette };
    /** Only the keys a persona wants to override in dark mode -- matches persona.json's abridged `dark` block. */
    dark?: Partial<PersonaBaseColors> & { accents?: Partial<PersonaAccentPalette> };
    font: PersonaThemeFont;
}

/**
 * The default persona's theme (and, until #78/#79 land, the only one). Every value here must
 * match the corresponding hard-coded default already baked into `index.css`'s `:root`/`.dark`
 * blocks -- `personaTheme.test.ts` guards that the two never drift apart.
 */
export const SONIC_THEME: PersonaTheme = {
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
        }
    },
    dark: {
        primary: "341 100% 55%"
    },
    font: {
        family: "Nunito Sans",
        importUrl: "https://fonts.googleapis.com/css2?family=Nunito+Sans:wght@400;600;700;800;900&display=swap"
    }
};

/**
 * The `--brand-*` CSS custom properties `index.css` reads. `applyTheme` writes into these; nothing
 * else should call `style.setProperty` for them, so this map is the single source of truth for the
 * variable names on both sides.
 *
 * Names are roles (`primary`/`secondary`/`accent`/...), not brand colors -- a future persona's
 * `primary` could be any hue and would still write through the same `--brand-primary` variable.
 * Only the *values* SONIC_THEME supplies happen to be red today (PR #91 review round 2: the
 * round-1 names, `--brand-red`/`--brand-blue`/`--brand-yellow`, baked the default persona's colors into the
 * variable names themselves, which would mislabel every other persona).
 */
export const PERSONA_THEME_CSS_VARS = {
    primary: "--brand-primary",
    secondary: "--brand-secondary",
    background: "--brand-background",
    foreground: "--brand-foreground",
    primaryHex: "--brand-primary-hex",
    primaryStrong: "--brand-primary-strong",
    primaryLight: "--brand-primary-light",
    primaryTintOnDark: "--brand-primary-tint",
    secondaryHex: "--brand-secondary-hex",
    secondaryStrong: "--brand-secondary-strong",
    secondaryTintOnDark: "--brand-secondary-tint",
    accent: "--brand-accent",
    accentLight: "--brand-accent-light",
    ink: "--brand-ink",
    surfaceTint: "--brand-surface-tint",
    surfaceDark: "--brand-surface-dark",
    surfaceDarkAlt: "--brand-surface-dark-alt",
    success: "--brand-success",
    neutral: "--brand-neutral"
} as const;

/**
 * Applies a persona theme's light-mode tokens to `root`'s inline style, where they take priority
 * over `index.css`'s static defaults. Dark-mode overrides are left to the existing `.dark` class
 * selector for now -- `theme.dark` is accepted here only so its shape is exercised; wiring it into
 * the live `.dark` variables is F1's job, once `PersonaProvider` exists to decide when a dark-mode
 * switch actually happens.
 *
 * This is the seam: `App`/`index.tsx` calls `applyTheme(SONIC_THEME)` once today. Swapping that
 * argument for a theme fetched from `/api/personas/<id>` is the only change F1 needs to make here.
 */
export function applyTheme(theme: PersonaTheme, root: HTMLElement = document.documentElement): void {
    const vars = PERSONA_THEME_CSS_VARS;
    const { light } = theme;
    const set = (name: string, value: string | undefined) => {
        if (value) root.style.setProperty(name, value);
    };

    set(vars.primary, light.primary);
    set(vars.secondary, light.secondary);
    set(vars.background, light.background);
    set(vars.foreground, light.foreground);

    set(vars.primaryHex, light.accents.primaryHex);
    set(vars.primaryStrong, light.accents.primaryStrong);
    set(vars.primaryLight, light.accents.primaryLight);
    set(vars.primaryTintOnDark, light.accents.primaryTintOnDark);
    set(vars.secondaryHex, light.accents.secondaryHex);
    set(vars.secondaryStrong, light.accents.secondaryStrong);
    set(vars.secondaryTintOnDark, light.accents.secondaryTintOnDark);
    set(vars.accent, light.accents.accent);
    set(vars.accentLight, light.accents.accentLight);
    set(vars.ink, light.accents.ink);
    set(vars.surfaceTint, light.accents.surfaceTint);
    set(vars.surfaceDark, light.accents.surfaceDark);
    set(vars.surfaceDarkAlt, light.accents.surfaceDarkAlt);
    set(vars.success, light.accents.success);
    set(vars.neutral, light.accents.neutral);
}

/**
 * The three `--brand-*-dark` CSS custom properties `index.css`'s `.dark` block reads via a
 * `var(--brand-x-dark, <literal fallback>)` chain (issue #80 F1/F3, Rick's #80 review: "map
 * dark.background/dark.foreground onto the .dark block vars").
 *
 * These are DELIBERATELY separate property names from `PERSONA_THEME_CSS_VARS`, not the same names
 * written a second time. An inline style set on `documentElement` (which is exactly what
 * `applyTheme` above does) always wins over any stylesheet rule targeting the same element,
 * regardless of selector specificity or source order -- so a `.dark { --brand-primary: ... }` rule
 * could never override `applyTheme`'s inline `--brand-primary`. Giving dark overrides their own
 * variable names, applied only via `applyDarkTheme`'s injected `<style>` (never inline), sidesteps
 * that entirely: `.dark`'s selector can win over `:root`'s defaults normally, and light mode is
 * completely unaffected since `:root` never mentions the `-dark` names.
 *
 * Only `primary`/`background`/`foreground` are listed (no `secondary`) because those are the only
 * three base roles `index.css`'s `.dark` block derives from brand tokens today -- `--secondary` (and
 * `--muted`/`--accent`/`--destructive`/etc.) are intentionally independent literal grays in both
 * `:root` and `.dark`, unchanged by this slice.
 */
export const PERSONA_THEME_DARK_CSS_VARS = {
    primary: "--brand-primary-dark",
    background: "--brand-background-dark",
    foreground: "--brand-foreground-dark"
} as const;

const DARK_THEME_STYLE_ELEMENT_ID = "persona-dark-theme-overrides";

/**
 * The app-wide default dark palette for `background`/`foreground` (and, via the same two CSS
 * vars, `.dark`'s `card`/`popover`/`card-foreground`/`popover-foreground` -- see
 * `PERSONA_THEME_DARK_CSS_VARS`'s doc comment). Byte-identical to `index.css`'s own literal
 * `var(--brand-background-dark, 210 20% 5%)` / `var(--brand-foreground-dark, 0 0% 98%)`
 * fallbacks, so a pack that omits these two keys still gets the SAME neutral-dark page `index.css`
 * would render on its own if `applyDarkTheme` never ran at all.
 *
 * Issue #80 F1/F3 (Rick's #110 review, item 3): a persona whose `dark` block only sets some tokens
 * (the default persona sets only `primary`) used to leak that persona's LIGHT `background`/
 * `foreground` into dark mode instead -- for the default persona that's a pale blue-white
 * (`"195 44% 96%"`), which reads as "half the page didn't go dark". Falling back to these DEFAULT
 * dark values instead (rather than `theme.light.*`) keeps the rest of the page dark like every
 * other pack, regardless of what a pack's `dark` block chooses to override.
 */
export const DEFAULT_DARK_BACKGROUND: HslTriplet = "210 20% 5%";
export const DEFAULT_DARK_FOREGROUND: HslTriplet = "0 0% 98%";

/**
 * Injects (or updates) a `<style>` element holding a `.dark { --brand-x-dark: ...; }` rule for the
 * given persona theme's dark-mode overrides. `primary` falls back to the theme's own light value
 * (a persona with no dark accent at all still gets ITS brand hue, just at the light-mode
 * lightness); `background`/`foreground` fall back to the shared `DEFAULT_DARK_*` constants above
 * instead, so a persona with no `dark` block (both fixture personas in
 * `app/backend/tests/fixtures/personas`) or one that only overrides `primary` (the default
 * persona) still renders a coherent dark page instead of leaking a light-mode background/text
 * color, a previous persona's dark colors, or the default persona's.
 *
 * A `<style>` tag (not inline styles) is required here specifically so the `.dark` selector keeps
 * normal cascade behavior -- see `PERSONA_THEME_DARK_CSS_VARS`'s doc comment for why inline styles
 * would not work for this.
 */
export function applyDarkTheme(theme: PersonaTheme, doc: Document = document): void {
    const vars = PERSONA_THEME_DARK_CSS_VARS;
    const dark = theme.dark ?? {};
    const primary = dark.primary ?? theme.light.primary;
    const background = dark.background ?? DEFAULT_DARK_BACKGROUND;
    const foreground = dark.foreground ?? DEFAULT_DARK_FOREGROUND;

    let style = doc.getElementById(DARK_THEME_STYLE_ELEMENT_ID) as HTMLStyleElement | null;
    if (!style) {
        style = doc.createElement("style");
        style.id = DARK_THEME_STYLE_ELEMENT_ID;
        doc.head.appendChild(style);
    }

    style.textContent = `.dark {\n  ${vars.primary}: ${primary};\n  ${vars.background}: ${background};\n  ${vars.foreground}: ${foreground};\n}`;
}

function parseHslTriplet(triplet: HslTriplet): { h: number; s: number; l: number } {
    const match = /^(-?\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)%\s+(\d+(?:\.\d+)?)%$/.exec(triplet.trim());
    if (!match) return { h: 0, s: 0, l: 50 };
    return { h: Number(match[1]), s: Number(match[2]), l: Number(match[3]) };
}

function clampNumber(value: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, value));
}

function hslToHex(h: number, s: number, l: number): string {
    const hue = ((h % 360) + 360) % 360;
    const sat = clampNumber(s, 0, 100) / 100;
    const light = clampNumber(l, 0, 100) / 100;
    const c = (1 - Math.abs(2 * light - 1)) * sat;
    const x = c * (1 - Math.abs(((hue / 60) % 2) - 1));
    const m = light - c / 2;
    let r = 0,
        g = 0,
        b = 0;
    if (hue < 60) [r, g, b] = [c, x, 0];
    else if (hue < 120) [r, g, b] = [x, c, 0];
    else if (hue < 180) [r, g, b] = [0, c, x];
    else if (hue < 240) [r, g, b] = [0, x, c];
    else if (hue < 300) [r, g, b] = [x, 0, c];
    else [r, g, b] = [c, 0, x];
    const toHex = (v: number) => Math.round((v + m) * 255)
        .toString(16)
        .padStart(2, "0")
        .toUpperCase();
    return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
}

/**
 * Derives a full `PersonaAccentPalette` from just a persona's four base HSL roles (issue #80 F1/F3,
 * design doc §4.2: `accents` isn't in `persona.schema.json`, so any non-default persona needs one
 * synthesized rather than authored). Not meant to be a perfect design-system generator -- just a
 * reasonable, deterministic set of tints/shades so a second persona's illustrations, gradients, and
 * dark-mode surfaces aren't flatly monochrome, without requiring a color-math dependency.
 *
 * The default persona itself never calls this: `resolvePersonaTheme` below keeps returning
 * `SONIC_THEME`'s exact, hand-tuned hex constants for that persona's id so nothing here can
 * regress F2's byte-identical guarantee (`personaTheme.test.ts`).
 */
export function deriveAccents(colors: PersonaBaseColors): PersonaAccentPalette {
    const primary = parseHslTriplet(colors.primary);
    const secondary = parseHslTriplet(colors.secondary);
    const background = parseHslTriplet(colors.background);
    const foreground = parseHslTriplet(colors.foreground);
    const accentHue = (primary.h + 150) % 360;

    return {
        primaryHex: hslToHex(primary.h, primary.s, primary.l),
        primaryStrong: hslToHex(primary.h, primary.s, clampNumber(primary.l - 10, 5, 95)),
        primaryLight: hslToHex(primary.h, primary.s, clampNumber(primary.l + 15, 5, 95)),
        primaryTintOnDark: hslToHex(primary.h, clampNumber(primary.s - 10, 0, 100), clampNumber(primary.l + 25, 5, 95)),
        secondaryHex: hslToHex(secondary.h, secondary.s, secondary.l),
        secondaryStrong: hslToHex(secondary.h, clampNumber(secondary.s + 20, 0, 100), clampNumber(secondary.l + 15, 5, 95)),
        secondaryTintOnDark: hslToHex(secondary.h, clampNumber(secondary.s - 20, 0, 100), clampNumber(secondary.l + 35, 5, 95)),
        accent: hslToHex(accentHue, 90, 55),
        accentLight: hslToHex(accentHue, 85, 70),
        ink: hslToHex(foreground.h, foreground.s, foreground.l),
        surfaceTint: hslToHex(background.h, clampNumber(background.s, 0, 40), 97),
        surfaceDark: hslToHex(foreground.h, clampNumber(foreground.s, 0, 30), 10),
        surfaceDarkAlt: hslToHex(foreground.h, clampNumber(foreground.s, 0, 30), 14),
        success: "#328500",
        neutral: "#C9CFD4"
    };
}

/**
 * Builds a full `PersonaTheme` (light accents + optional dark overrides + font) from the base wire
 * shape `/api/personas/<id>` returns (`ui.theme.light`/`ui.theme.dark`, each just the four HSL
 * roles -- see design doc §5.2 and `personas/<id>/persona.json`). The default persona's id always
 * resolves to the literal `SONIC_THEME` (same object identity for `accents`/`font`) so this never
 * regresses F2's pixel-identical guarantee; any other persona id gets `deriveAccents` and a
 * `font` fallback of the default persona's own (until a persona pack declares its own webfont,
 * which is out of scope here).
 */
export interface PersonaWireTheme {
    light: PersonaBaseColors & { accents?: Partial<PersonaAccentPalette> };
    dark?: Partial<PersonaBaseColors> & { accents?: Partial<PersonaAccentPalette> };
    font?: PersonaThemeFont;
}

export function resolvePersonaTheme(personaId: string, wireTheme: PersonaWireTheme): PersonaTheme {
    if (personaId === "sonic") {
        return SONIC_THEME;
    }

    // A pack MAY author its own accents (personas/persona.schema.json's `_ThemeAccents`) --
    // any key it supplies wins; any key it omits falls back to `deriveAccents`'s synthesized
    // value, so a pack can override just e.g. `accent` without having to author all 15 keys.
    const accents: PersonaAccentPalette = {
        ...deriveAccents(wireTheme.light),
        ...wireTheme.light.accents
    };

    return {
        light: {
            ...wireTheme.light,
            accents
        },
        dark: wireTheme.dark,
        font: wireTheme.font ?? SONIC_THEME.font
    };
}
