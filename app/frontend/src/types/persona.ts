/**
 * TypeScript mirror of the wire contract `app/backend/app.py`'s `_persona_summary_body`/
 * `_persona_detail_body` return (design doc §5.2), which in turn mirrors
 * `app/backend/persona_loader.py`'s pydantic models (`personas/persona.schema.json`).
 *
 * Kept intentionally close to the Python field names (`camelCase`, same nesting) rather than
 * renamed for frontend taste, so a diff against the backend source is easy to eyeball.
 */
import type { PersonaWireTheme } from "@/lib/personaTheme";

export type { PersonaWireTheme };

/** One entry of `GET /api/personas`'s `personas` list. Notably: the ONLY place `logoUrl` appears
 * on the wire -- `/api/personas/{id}`'s detail body does not repeat it. */
export interface PersonaSummary {
    id: string;
    displayName: string;
    logoUrl: string;
    theme: PersonaWireTheme;
}

export interface PersonaBackendEntry {
    id: string;
    url: string;
}

export interface PersonasIndexResponse {
    default: string;
    personas: PersonaSummary[];
    backends: PersonaBackendEntry[];
}

/** One selectable model for a pipeline, per PR 106 (Birdperson round 3, issue #75)'s
 * `/api/personas/{id}` shape: each entry is now an object (id/label/whether it does visible
 * reasoning), not a bare model-id string. */
export interface PersonaModelOption {
    id: string;
    label: string;
    reasoning: boolean;
}

export interface PersonaModelPipeline {
    default: string;
    /** Optional: PR 106 renamed `allowed` -> `models` and richened the entries (see
     * `PersonaModelOption`), but hasn't merged into this branch's backend yet, so callers must
     * tolerate its absence until then (falls back to just `default`). */
    models?: PersonaModelOption[];
}

export interface PersonaModels {
    realtime: PersonaModelPipeline;
    cascade?: PersonaModelPipeline;
}

/** `GET /api/personas/{id}`'s `machines.<key>` entry. The backend normalizes pack-authored machine
 * states to the public UI wire contract: `"down"` stays `"down"` and `"operational"` becomes
 * `"up"`. */
export interface PersonaMachine {
    status: "up" | "down";
    label: string;
}

/** `GET /api/personas/{id}`'s `happyHour`, mirroring `app/backend/persona_loader.py`'s
 * `_HappyHour` pydantic model's public hour window fields. */
export interface PersonaHappyHour {
    startHour: number;
    endHour: number;
}

/** `GET /api/personas/{id}` response body: the pack's raw `ui` block (theme/assets/strings/hero/
 * legal/title), spread flat, plus `voice`/`locales`/`features`/`menuUrl`/`models`. Asset paths
 * here (`assets.logo`/`assets.favicon`/`assets.apologyClip`) are the pack-relative, UNVERSIONED
 * paths from persona.json -- building a request URL from them is the caller's job (see
 * `lib/personaAssets.ts`). */
export interface PersonaDetail {
    id: string;
    /** Persona.schema.json's required top-level role name (e.g. "carhop"), used to build a
     * persona-aware voice label instead of hard-coding one brand's role (issue 119). */
    roleName: string;
    title: string;
    theme: PersonaWireTheme;
    assets: {
        logo: string;
        favicon: string;
        apologyClip?: string;
        /** Issue 164 B2: true for a pack whose original PNG logo has an opaque white
         * background and was shown on a white rounded tile -- `BrandHero` only renders that
         * tile when this is set. */
        logoTile?: boolean;
    };
    /** Per-locale flat string maps, e.g. `strings.en["ticket.kicker"]`. */
    strings: Record<string, Record<string, string>>;
    hero: PersonaHero;
    legal: string;
    voice: { default: string };
    locales: { default: string; supported: string[] };
    features: { dayparts: boolean };
    menuUrl: string;
    models: PersonaModels;
    /** Issue 164 E2: the pack's own `pricing.taxRate` (e.g. "0.08"), forwarded here so the
     * ticket can render "Tax (N%)" instead of a bare "Tax". */
    taxRate: string;
    /** Issue 305: pack-declared machine availability, normalized to the public `"up"`/`"down"`
     * wire contract. Empty when the persona declares no machine-specific operational toggles. */
    machines: Record<string, PersonaMachine>;
    /** Issue 305: optional happy-hour window from `app/backend/persona_loader.py`'s
     * `_HappyHour` (`pricing.happyHour`); `null` means this pack has no happy-hour control. */
    happyHour: PersonaHappyHour | null;
    /** Issue 164 C3/E4: 'plain' (default when omitted) is the mono session-token bar most
     * originals used; 'chips' is one original's colored pill-chip bar that always stays
     * on its light background regardless of page theme. */
    sessionBar?: { variant?: "plain" | "chips" };
    /** Issue 164 E1: optional menu-category-name -> emoji/icon override, keyed by the
     * category's exact menuItems.json name. Lets a pack whose menu data is shared territory
     * (issue 165's menu-data work) restore its original category icons WITHOUT editing menuItems.json. */
    categoryIcons?: Record<string, string>;
    /** Issue 164 R2 (PR 167 round 1 review): optional per-slot override of which brand role a
     * handful of shared text elements draw their color from. Any key a pack omits falls back to
     * the shared default (badge=primary, countChip=secondary, footerTagline=secondary,
     * footerExtra=secondary) -- see `lib/personaTextRoles.ts`. */
    textRoles?: PersonaTextRoles;
}

/** Issue 164 R2 (PR 167 round 1 review): which brand role a shared text element's color is
 * drawn from. `primaryDeep` is a deeper/darker shade of `primary`, distinct from the hover/pressed
 * `primaryStrong` state. */
export type PersonaTextRole = "primary" | "primaryDeep" | "secondary" | "accent" | "ink";

export interface PersonaTextRoles {
    badge?: PersonaTextRole;
    countChip?: PersonaTextRole;
    footerTagline?: PersonaTextRole;
    footerExtra?: PersonaTextRole;
}

/** Issue 164 A3: one of the hero's three compact callout pills. `tone` names which of the
 * persona's own theme roles (primary/secondary/accent) the pill's gradient is drawn from -- the
 * shared component stays brand-free by never hard-coding a color itself. */
export interface PersonaHeroCallout {
    title: string;
    detail: string;
    tone: "primary" | "secondary" | "accent";
}

export interface PersonaHeroSpotlightRow {
    label: string;
    value: string;
    /** Issue 164 R4 (PR 167 round 1 review): which brand role this row's value text is drawn
     * from. Defaults to "primary" when omitted, matching each original's own per-row coloring. */
    tone?: "primary" | "secondary" | "accent" | "ink";
}

/** Issue 164 A1/A2: one of the hero's two spotlight cards. The first card shape uses `rows`
 * (label/value pairs); the second instead pairs a `body` sentence with an `accent` pairing
 * suggestion -- matching each original's two distinct card layouts. */
export interface PersonaHeroSpotlight {
    /** Pack-relative path under assets/ to this card's brand illustration. */
    icon: string;
    kicker: string;
    title: string;
    rows?: PersonaHeroSpotlightRow[];
    body?: string;
    accent?: string;
    /** Issue 164 A2: which brand role the second ("body") card's border/wash/kicker
     * draw from. Defaults to "secondary" when omitted. */
    tone?: "primary" | "secondary" | "accent";
    /** Issue 164 R4 (PR 167 round 1 review): which brand role the second ("body") card's accent
     * pairing-line text is drawn from, independent of `tone` above. Defaults to the tone-derived
     * mapping already used before this field existed, so a pack that omits it renders unchanged. */
    accentTone?: "primary" | "secondary" | "accent" | "ink";
    /** Issue 164 A2: optional hex wash for this card's background (e.g. a pack's tinted
     * beverage card). Omit for the shared neutral surface. */
    tint?: string;
}

export interface PersonaHero {
    headline: string;
    /** Issue 164 A5: overrides the shared neutral "Voice Ordering Demo" hero pill copy. Omit
     * to use the shared `hero.badge` i18n default. */
    badge?: string;
    /** Issue 164 A4: the persona's own hero sub-headline sentence. */
    description: string;
    callouts: PersonaHeroCallout[];
    spotlight: PersonaHeroSpotlight[];
}


export interface PersonaMenuItem {
    id: string;
    name: string;
    category: string;
    price: number;
    description?: string;
    [key: string]: unknown;
}
