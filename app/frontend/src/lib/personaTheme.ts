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
 * slice's own addition: every other brand color Sonic's components had hard-coded as literal hex
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
    /** Accent hue distinct from primary/secondary (Sonic's yellow). */
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
    /** Decorative success green (used in the burger illustration only). */
    success: string;
    /** Decorative neutral gray (used in the burger illustration only). */
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
 * Sonic's theme, as the default (and, until #78/#79 land, only) persona. Every value here must
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
 * Names are roles (`primary`/`secondary`/`accent`/...), not Sonic's colors -- a future persona's
 * `primary` could be any hue and would still write through the same `--brand-primary` variable.
 * Only the *values* SONIC_THEME supplies happen to be red today (PR #91 review round 2: the
 * round-1 names, `--brand-red`/`--brand-blue`/`--brand-yellow`, baked Sonic's colors into the
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
