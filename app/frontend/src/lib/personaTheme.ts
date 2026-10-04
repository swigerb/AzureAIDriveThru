/**
 * Runtime persona theming groundwork (issue #80, design doc §4.2 and §9 row F2).
 *
 * `PersonaTheme` mirrors the `ui.theme` shape a persona pack's `persona.json` exposes via
 * `/api/personas/<id>`. `PersonaProvider` (context/persona-context.tsx) fetches that wire shape and
 * calls `resolvePersonaTheme`/`applyTheme`/`applyDarkTheme` below for every persona, including the
 * default one -- there is no persona-specific special case anywhere in this file (issue #117:
 * shared code must not hard-code any one persona's palette).
 *
 * The `light`/`dark` base colors hold the four keys `persona.json` documents (`primary`,
 * `secondary`, `background`, `foreground`, each an "H S% L%" triplet with no `hsl()` wrapper --
 * exactly how `index.css`'s `--brand-primary`/`--brand-secondary`/`--brand-background`/
 * `--brand-foreground` tokens already store them). `accents` is an extended hex palette (issue #80
 * F2) every persona pack may optionally author (`personas/persona.schema.json`'s `_ThemeAccents`);
 * any key a pack omits falls back to `deriveAccents`'s synthesized value. `surface` (issue #117) is
 * an optional palette of shadcn UI slot tokens (`--card-foreground`, `--secondary`, `--muted`,
 * `--accent`, `--destructive`, `--border`/`--input`, `--chart-2..5`) that `index.css` used to hard-code
 * to one persona's values; a pack that omits `surface` simply gets the shared neutral defaults
 * baked into `index.css`'s `var(--surface-x, <neutral literal>)` fallbacks.
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
    /** Issue #164 R2 (PR #167 round 1 review): deeper/darker shade of `primary`, distinct from
     * `primaryStrong`, for text needing more contrast than the plain primary hex. Defaults to this
     * pack's own merged `primaryHex` (see `resolvePersonaTheme`'s R9 fix, PR #167 round 2 review)
     * so a pack that doesn't author this key renders unchanged. */
    primaryDeep: string;
    /** Shade of `primary` legible on dark surfaces (dark-mode text/badges). */
    primaryTintOnDark: string;
    /** Exact hex of `secondary`, for the same byte-identical reason as `primaryHex`. */
    secondaryHex: string;
    /** Brighter shade of `secondary`, used as a gradient endpoint. */
    secondaryStrong: string;
    /** Issue #164 R3 (PR #167 round 1 review): lighter shade of `secondary`, for pill/gradient
     * accents brighter than `secondaryStrong`. Defaults to this pack's own merged
     * `secondaryStrong` (see `resolvePersonaTheme`'s R9 fix, PR #167 round 2 review) so a pack
     * that doesn't author this key renders unchanged. */
    secondaryLight: string;
    /** Shade of `secondary` legible on dark surfaces (dark-mode text/badges). */
    secondaryTintOnDark: string;
    /** Accent hue distinct from primary/secondary. */
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

/**
 * Optional shadcn UI slot palette (issue #117). Every key is optional: a pack that doesn't author
 * `surface` simply renders the shared neutral defaults `index.css` falls back to. This is the
 * LIGHT-mode shape; see `PersonaSurfaceDarkTokens` for the (smaller) set of dark-only overrides.
 */
export interface PersonaSurfaceTokens {
    /** Feeds `--card-foreground` and `--popover-foreground` (light only -- dark already derives from the `foreground` dark override, see `PERSONA_THEME_DARK_CSS_VARS`). */
    cardForeground: string;
    /** Feeds the shadcn `--secondary` slot (distinct from the brand role `secondary` above). */
    secondary: string;
    secondaryForeground: string;
    /** Feeds the shadcn `--muted` slot. */
    muted: string;
    mutedForeground: string;
    /** Feeds the shadcn `--accent` slot. Mode-invariant: the same value is used in both `:root` and `.dark`. */
    accent: string;
    accentForeground: string;
    /** Feeds the shadcn `--destructive` slot. */
    destructive: string;
    /** Feeds both `--border` and `--input`. */
    border: string;
    /** Feeds `--chart-4`. Mode-invariant: the same value is used in both `:root` and `.dark`. */
    chart4: string;
    /** Feeds `--chart-5` (light only -- see `PersonaSurfaceDarkTokens.chart5` for the dark value). */
    chart5: string;
}

/**
 * Dark-only shadcn UI slot overrides (issue #117). `accent`/`chart4` are intentionally absent here
 * because they're mode-invariant and only ever set once, from `PersonaSurfaceTokens` above.
 */
export interface PersonaSurfaceDarkTokens {
    secondary: string;
    muted: string;
    destructive: string;
    border: string;
    /** Feeds `--chart-2` in dark mode (light mode instead derives `--chart-2` from the brand `secondary` role -- see `index.css`). */
    chart2: string;
    /** Feeds `--chart-3` in dark mode (light mode instead derives `--chart-3` from the shadcn `--accent` slot -- see `index.css`). */
    chart3: string;
    chart5: string;
}

/**
 * Issue #169 (Rick's PR #167 round 3 review, item N13): optional menu-card/header surface tokens,
 * layered on top of the shadcn `PersonaSurfaceTokens` slots above. Every key is optional: a pack
 * that doesn't author `menuSurface` simply renders the shared neutral defaults `index.css` falls
 * back to (the exact same card/title classes `menu-panel.tsx` renders today). This is the
 * LIGHT-mode shape -- a persona sets its own card backgrounds here; see
 * `PersonaMenuSurfaceDarkTokens` for the dark-only overrides (category title color, item card).
 */
export interface PersonaMenuSurfaceTokens {
    /** Feeds `--menu-category-card` (the category section's card background). */
    categoryCardBackground: string;
    /** Feeds `--menu-item-card` (each menu item's card background). */
    itemCardBackground: string;
}

/**
 * Dark-only menu-card/header surface overrides (issue #169). `categoryCardBackground` is
 * intentionally absent here -- the originating review only asked for a dark category *title* color
 * and item *card* background change, not the category card background, so it stays the shared
 * `dark:bg-brand-surface-dark/95` literal `menu-panel.tsx` already renders.
 */
export interface PersonaMenuSurfaceDarkTokens {
    /** Feeds `--menu-category-title-dark` (the category heading's dark-mode text color). */
    categoryTitleColor: string;
    /** Feeds `--menu-item-card-dark` (each menu item's dark-mode card background). */
    itemCardBackground: string;
}

export interface PersonaThemeFont {
    family: string;
    importUrl: string;
}

export interface PersonaTheme {
    light: PersonaBaseColors & {
        accents: PersonaAccentPalette;
        surface?: Partial<PersonaSurfaceTokens>;
        menuSurface?: Partial<PersonaMenuSurfaceTokens>;
    };
    /** Only the keys a persona wants to override in dark mode -- matches persona.json's abridged `dark` block. */
    dark?: Partial<PersonaBaseColors> & {
        accents?: Partial<PersonaAccentPalette>;
        surface?: Partial<PersonaSurfaceDarkTokens>;
        menuSurface?: Partial<PersonaMenuSurfaceDarkTokens>;
    };
    font: PersonaThemeFont;
}


/**
 * The app-wide default font (used by every persona that doesn't declare its own `ui.theme.font`).
 * `index.css` already unconditionally `@import`s this same family/weights for the base UI font
 * regardless of which persona is active, so this isn't any one persona's brand font -- it's the
 * shared fallback for personas that don't bring their own.
 */
export const DEFAULT_THEME_FONT: PersonaThemeFont = {
    family: "Nunito Sans",
    importUrl: "https://fonts.googleapis.com/css2?family=Nunito+Sans:wght@400;600;700;800;900&display=swap"
};

/**
 * The `--brand-*` CSS custom properties `index.css` reads. `applyTheme` writes into these; nothing
 * else should call `style.setProperty` for them, so this map is the single source of truth for the
 * variable names on both sides.
 *
 * Names are roles (`primary`/`secondary`/`accent`/...), not brand colors -- a persona's `primary`
 * could be any hue and would still write through the same `--brand-primary` variable (PR #91 review
 * round 2: the round-1 names, `--brand-red`/`--brand-blue`/`--brand-yellow`, baked one persona's
 * colors into the variable names themselves, which would mislabel every other persona).
 */
export const PERSONA_THEME_CSS_VARS = {
    primary: "--brand-primary",
    secondary: "--brand-secondary",
    background: "--brand-background",
    foreground: "--brand-foreground",
    primaryHex: "--brand-primary-hex",
    primaryStrong: "--brand-primary-strong",
    primaryLight: "--brand-primary-light",
    primaryDeep: "--brand-primary-deep",
    primaryTintOnDark: "--brand-primary-tint",
    secondaryHex: "--brand-secondary-hex",
    secondaryStrong: "--brand-secondary-strong",
    secondaryLight: "--brand-secondary-light",
    secondaryTintOnDark: "--brand-secondary-tint",
    accent: "--brand-accent",
    accentLight: "--brand-accent-light",
    ink: "--brand-ink",
    surfaceTint: "--brand-surface-tint",
    surfaceDark: "--brand-surface-dark",
    surfaceDarkAlt: "--brand-surface-dark-alt",
    success: "--brand-success",
    neutral: "--brand-neutral",
    fontFamily: "--brand-font-family"
} as const;

/**
 * The `--surface-*` CSS custom properties `index.css` reads for the shadcn UI slot tokens (issue
 * #117). Applied the same way as `PERSONA_THEME_CSS_VARS` above -- via `applyTheme`'s inline style,
 * light mode only. `accent`/`chart4` are mode-invariant (the value set here is also what `.dark`
 * uses, via the SAME underlying var name, since `index.css` never redeclares those two in `.dark`).
 */
export const PERSONA_SURFACE_CSS_VARS = {
    cardForeground: "--surface-card-foreground",
    secondary: "--surface-secondary",
    secondaryForeground: "--surface-secondary-foreground",
    muted: "--surface-muted",
    mutedForeground: "--surface-muted-foreground",
    accent: "--surface-accent",
    accentForeground: "--surface-accent-foreground",
    destructive: "--surface-destructive",
    border: "--surface-border",
    chart4: "--surface-chart4",
    chart5: "--surface-chart5"
} as const;

/**
 * The `--menu-*` CSS custom properties `index.css` reads for the menu-card/header surface tokens
 * (issue #169). Applied the same way as `PERSONA_SURFACE_CSS_VARS` above -- via `applyTheme`'s
 * inline style, light mode only.
 */
export const PERSONA_MENU_SURFACE_CSS_VARS = {
    categoryCardBackground: "--menu-category-card",
    itemCardBackground: "--menu-item-card"
} as const;


/**
 * Applies a persona theme's light-mode tokens to `root`'s inline style, where they take priority
 * over `index.css`'s static defaults. Dark-mode overrides are left to the existing `.dark` class
 * selector -- see `applyDarkTheme` below.
 */
export function applyTheme(theme: PersonaTheme, root: HTMLElement = document.documentElement): void {
    const vars = PERSONA_THEME_CSS_VARS;
    const surfaceVars = PERSONA_SURFACE_CSS_VARS;
    const menuSurfaceVars = PERSONA_MENU_SURFACE_CSS_VARS;
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
    set(vars.primaryDeep, light.accents.primaryDeep);
    set(vars.primaryTintOnDark, light.accents.primaryTintOnDark);
    set(vars.secondaryHex, light.accents.secondaryHex);
    set(vars.secondaryStrong, light.accents.secondaryStrong);
    set(vars.secondaryLight, light.accents.secondaryLight);
    set(vars.secondaryTintOnDark, light.accents.secondaryTintOnDark);
    set(vars.accent, light.accents.accent);
    set(vars.accentLight, light.accents.accentLight);
    set(vars.ink, light.accents.ink);
    set(vars.surfaceTint, light.accents.surfaceTint);
    set(vars.surfaceDark, light.accents.surfaceDark);
    set(vars.surfaceDarkAlt, light.accents.surfaceDarkAlt);
    set(vars.success, light.accents.success);
    set(vars.neutral, light.accents.neutral);

    const surface = light.surface;
    if (surface) {
        set(surfaceVars.cardForeground, surface.cardForeground);
        set(surfaceVars.secondary, surface.secondary);
        set(surfaceVars.secondaryForeground, surface.secondaryForeground);
        set(surfaceVars.muted, surface.muted);
        set(surfaceVars.mutedForeground, surface.mutedForeground);
        set(surfaceVars.accent, surface.accent);
        set(surfaceVars.accentForeground, surface.accentForeground);
        set(surfaceVars.destructive, surface.destructive);
        set(surfaceVars.border, surface.border);
        set(surfaceVars.chart4, surface.chart4);
        set(surfaceVars.chart5, surface.chart5);
    }

    const menuSurface = light.menuSurface;
    if (menuSurface) {
        set(menuSurfaceVars.categoryCardBackground, menuSurface.categoryCardBackground);
        set(menuSurfaceVars.itemCardBackground, menuSurface.itemCardBackground);
    }

    const font = theme.font ?? DEFAULT_THEME_FONT;
    set(vars.fontFamily, font.family ? `"${font.family}"` : undefined);
    applyThemeFont(font, root.ownerDocument ?? document);
}

/** `id` of the `<link rel="stylesheet">` `applyThemeFont` injects/updates for the active persona's webfont. */
const FONT_LINK_ELEMENT_ID = "persona-font-link";

/**
 * Loads (or swaps) the active persona's webfont stylesheet by injecting/updating a single
 * `<link rel="stylesheet">` in `<head>` (issue 164 C1: a pack's `ui.theme.font` was already
 * threaded from `persona.json` all the way to `PersonaTheme.font`, but nothing ever fetched the
 * font or applied its family to the page, so every persona silently rendered the shared
 * `DEFAULT_THEME_FONT` (Nunito Sans) instead of its own brand font). Reuses the same tag across
 * persona switches (rather than appending a new `<link>` each time) and only touches `href` when
 * the URL actually changed, so re-applying the same persona's theme is a no-op.
 */
export function applyThemeFont(font: PersonaThemeFont, doc: Document = document): void {
    if (!font?.importUrl) return;
    let link = doc.getElementById(FONT_LINK_ELEMENT_ID) as HTMLLinkElement | null;
    if (!link) {
        link = doc.createElement("link");
        link.id = FONT_LINK_ELEMENT_ID;
        link.rel = "stylesheet";
        doc.head.appendChild(link);
    }
    if (link.href !== font.importUrl) {
        link.href = font.importUrl;
    }
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
 * `--muted`/`--accent`/`--destructive`/etc.) go through `PERSONA_SURFACE_DARK_CSS_VARS` below instead.
 */
export const PERSONA_THEME_DARK_CSS_VARS = {
    primary: "--brand-primary-dark",
    background: "--brand-background-dark",
    foreground: "--brand-foreground-dark"
} as const;

/**
 * The dark-only `--surface-*-dark` CSS custom properties `index.css`'s `.dark` block reads (issue
 * #117), applied the same way as `PERSONA_THEME_DARK_CSS_VARS` above -- only via `applyDarkTheme`'s
 * injected `<style>`, never inline, for the same cascade reason documented there.
 */
export const PERSONA_SURFACE_DARK_CSS_VARS = {
    secondary: "--surface-secondary-dark",
    muted: "--surface-muted-dark",
    destructive: "--surface-destructive-dark",
    border: "--surface-border-dark",
    chart2: "--surface-chart2-dark",
    chart3: "--surface-chart3-dark",
    chart5: "--surface-chart5-dark"
} as const;

/**
 * The dark-only `--menu-*-dark` CSS custom properties `index.css`'s `.dark` block reads (issue
 * #169), applied the same way as `PERSONA_SURFACE_DARK_CSS_VARS` above -- only via
 * `applyDarkTheme`'s injected `<style>`, never inline.
 */
export const PERSONA_MENU_SURFACE_DARK_CSS_VARS = {
    categoryTitleColor: "--menu-category-title-dark",
    itemCardBackground: "--menu-item-card-dark"
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
 * used to leak that persona's LIGHT `background`/`foreground` into dark mode instead. Falling back
 * to these DEFAULT dark values instead (rather than `theme.light.*`) keeps the rest of the page dark
 * like every other pack, regardless of what a pack's `dark` block chooses to override.
 */
export const DEFAULT_DARK_BACKGROUND: HslTriplet = "210 20% 5%";
export const DEFAULT_DARK_FOREGROUND: HslTriplet = "0 0% 98%";

/**
 * Injects (or updates) a `<style>` element holding a `.dark { --brand-x-dark: ...; --surface-x-dark:
 * ...; }` rule for the given persona theme's dark-mode overrides. `primary` falls back to the
 * theme's own light value (a persona with no dark accent at all still gets ITS brand hue, just at
 * the light-mode lightness); `background`/`foreground` fall back to the shared `DEFAULT_DARK_*`
 * constants above instead, so a persona with no `dark` block still renders a coherent dark page
 * instead of leaking a light-mode background/text color, a previous persona's dark colors, or the
 * default neutral values. `surface.*` dark keys have no fallback here at all -- an omitted key just
 * means the CSS rule doesn't declare that property, so `index.css`'s own
 * `var(--surface-x-dark, <neutral literal>)` fallback applies instead.
 *
 * A `<style>` tag (not inline styles) is required here specifically so the `.dark` selector keeps
 * normal cascade behavior -- see `PERSONA_THEME_DARK_CSS_VARS`'s doc comment for why inline styles
 * would not work for this.
 */
export function applyDarkTheme(theme: PersonaTheme, doc: Document = document): void {
    const vars = PERSONA_THEME_DARK_CSS_VARS;
    const surfaceVars = PERSONA_SURFACE_DARK_CSS_VARS;
    const menuSurfaceVars = PERSONA_MENU_SURFACE_DARK_CSS_VARS;
    const dark = theme.dark ?? {};
    const primary = dark.primary ?? theme.light.primary;
    const background = dark.background ?? DEFAULT_DARK_BACKGROUND;
    const foreground = dark.foreground ?? DEFAULT_DARK_FOREGROUND;
    const surface = dark.surface;
    const menuSurface = dark.menuSurface;

    const lines = [
        `  ${vars.primary}: ${primary};`,
        `  ${vars.background}: ${background};`,
        `  ${vars.foreground}: ${foreground};`
    ];
    if (surface?.secondary) lines.push(`  ${surfaceVars.secondary}: ${surface.secondary};`);
    if (surface?.muted) lines.push(`  ${surfaceVars.muted}: ${surface.muted};`);
    if (surface?.destructive) lines.push(`  ${surfaceVars.destructive}: ${surface.destructive};`);
    if (surface?.border) lines.push(`  ${surfaceVars.border}: ${surface.border};`);
    if (surface?.chart2) lines.push(`  ${surfaceVars.chart2}: ${surface.chart2};`);
    if (surface?.chart3) lines.push(`  ${surfaceVars.chart3}: ${surface.chart3};`);
    if (surface?.chart5) lines.push(`  ${surfaceVars.chart5}: ${surface.chart5};`);
    if (menuSurface?.categoryTitleColor) {
        lines.push(`  ${menuSurfaceVars.categoryTitleColor}: ${menuSurface.categoryTitleColor};`);
    }
    if (menuSurface?.itemCardBackground) {
        lines.push(`  ${menuSurfaceVars.itemCardBackground}: ${menuSurface.itemCardBackground};`);
    }

    let style = doc.getElementById(DARK_THEME_STYLE_ELEMENT_ID) as HTMLStyleElement | null;
    if (!style) {
        style = doc.createElement("style");
        style.id = DARK_THEME_STYLE_ELEMENT_ID;
        doc.head.appendChild(style);
    }

    style.textContent = `.dark {\n${lines.join("\n")}\n}`;
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
 * design doc §4.2: any persona without a curated `accents` block needs one synthesized rather than
 * authored). Not meant to be a perfect design-system generator -- just a reasonable, deterministic
 * set of tints/shades so a persona's illustrations, gradients, and dark-mode surfaces aren't flatly
 * monochrome, without requiring a color-math dependency.
 *
 * `success`/`neutral` are universal, brand-independent placeholders (not tied to any one persona's
 * palette) since no current component actually renders them -- see `PersonaAccentPalette`'s doc
 * comments.
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
        // Issue #164 R2 (PR #167 round 1 review): defaults to the same value as `primaryHex` (not
        // a new synthesis) so a pack that doesn't author `primaryDeep` renders unchanged.
        primaryDeep: hslToHex(primary.h, primary.s, primary.l),
        primaryTintOnDark: hslToHex(primary.h, clampNumber(primary.s - 10, 0, 100), clampNumber(primary.l + 25, 5, 95)),
        secondaryHex: hslToHex(secondary.h, secondary.s, secondary.l),
        secondaryStrong: hslToHex(secondary.h, clampNumber(secondary.s + 20, 0, 100), clampNumber(secondary.l + 15, 5, 95)),
        // Issue #164 R3 (PR #167 round 1 review): defaults to the same value as `secondaryStrong`
        // (not a new synthesis) so a pack that doesn't author `secondaryLight` renders unchanged.
        secondaryLight: hslToHex(secondary.h, clampNumber(secondary.s + 20, 0, 100), clampNumber(secondary.l + 15, 5, 95)),
        secondaryTintOnDark: hslToHex(secondary.h, clampNumber(secondary.s - 20, 0, 100), clampNumber(secondary.l + 35, 5, 95)),
        accent: hslToHex(accentHue, 90, 55),
        accentLight: hslToHex(accentHue, 85, 70),
        ink: hslToHex(foreground.h, foreground.s, foreground.l),
        surfaceTint: hslToHex(background.h, clampNumber(background.s, 0, 40), 97),
        surfaceDark: hslToHex(foreground.h, clampNumber(foreground.s, 0, 30), 10),
        surfaceDarkAlt: hslToHex(foreground.h, clampNumber(foreground.s, 0, 30), 14),
        success: "#16A34A",
        neutral: "#9CA3AF"
    };
}

/**
 * Builds a full `PersonaTheme` (light accents + optional dark overrides + font) from the base wire
 * shape `/api/personas/<id>` returns (`ui.theme.light`/`ui.theme.dark`, each just the four HSL
 * roles plus optional `accents`/`surface` -- see design doc §5.2 and `personas/<id>/persona.json`).
 * Every persona -- including the default one -- goes through the same generic path: a pack MAY
 * author its own accents (`personas/persona.schema.json`'s `_ThemeAccents`) -- any key it supplies
 * wins; any key it omits falls back to `deriveAccents`'s synthesized value, so a pack can override
 * just e.g. `accent` without having to author all 15 keys. `surface` is passed through unchanged
 * (never derived) since it has no synthesis fallback -- an omitted `surface` key simply lets
 * `index.css`'s own neutral default apply. `font` falls back to the shared `DEFAULT_THEME_FONT`
 * for any persona that doesn't declare its own webfont.
 */
export interface PersonaWireTheme {
    light: PersonaBaseColors & {
        accents?: Partial<PersonaAccentPalette>;
        surface?: Partial<PersonaSurfaceTokens>;
        menuSurface?: Partial<PersonaMenuSurfaceTokens>;
    };
    dark?: Partial<PersonaBaseColors> & {
        accents?: Partial<PersonaAccentPalette>;
        surface?: Partial<PersonaSurfaceDarkTokens>;
        menuSurface?: Partial<PersonaMenuSurfaceDarkTokens>;
    };
    font?: PersonaThemeFont;
}

export function resolvePersonaTheme(personaId: string, wireTheme: PersonaWireTheme): PersonaTheme {
    void personaId; // Every persona resolves the same way -- kept for API stability/logging call sites.

    // A pack MAY author its own accents (personas/persona.schema.json's `_ThemeAccents`) --
    // any key it supplies wins; any key it omits falls back to `deriveAccents`'s synthesized
    // value, so a pack can override just e.g. `accent` without having to author all 15 keys.
    const authoredAccents = wireTheme.light.accents;
    const merged: PersonaAccentPalette = {
        ...deriveAccents(wireTheme.light),
        ...authoredAccents
    };

    // Issue #164 R9 (PR #167 round 2 review): `primaryDeep`/`secondaryLight` must default to
    // THIS pack's own merged `primaryHex`/`secondaryStrong` (the value after the spread above,
    // which already reflects any authored override), not `deriveAccents`'s raw-HSL-synthesized
    // value from before the merge -- those two can differ from a pack's authored hex (rounding
    // through the HSL triplet), which silently changed a pack's rendered color even though it
    // never authored `primaryDeep`/`secondaryLight` at all (one pack's hero pill rendered a
    // visibly different shade than its own authored secondary color). A pack that DOES author
    // `primaryDeep`/`secondaryLight` directly
    // still wins, same as every other accent key.
    const accents: PersonaAccentPalette = {
        ...merged,
        primaryDeep: authoredAccents?.primaryDeep ?? merged.primaryHex,
        secondaryLight: authoredAccents?.secondaryLight ?? merged.secondaryStrong
    };

    return {
        light: {
            ...wireTheme.light,
            accents,
            surface: wireTheme.light.surface
        },
        dark: wireTheme.dark,
        font: wireTheme.font ?? DEFAULT_THEME_FONT
    };
}
