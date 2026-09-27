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

export interface PersonaModelPipeline {
    default: string;
    allowed: string[];
}

export interface PersonaModels {
    realtime: PersonaModelPipeline;
    cascade?: PersonaModelPipeline;
    local?: PersonaModelPipeline;
}

/** `GET /api/personas/{id}` response body: the pack's raw `ui` block (theme/assets/strings/hero/
 * legal/title), spread flat, plus `voice`/`locales`/`features`/`menuUrl`/`models`. Asset paths
 * here (`assets.logo`/`assets.favicon`/`assets.apologyClip`) are the pack-relative, UNVERSIONED
 * paths from persona.json -- building a request URL from them is the caller's job (see
 * `lib/personaAssets.ts`). */
export interface PersonaDetail {
    id: string;
    title: string;
    theme: PersonaWireTheme;
    assets: { logo: string; favicon: string; apologyClip?: string };
    /** Per-locale flat string maps, e.g. `strings.en["ticket.kicker"]`. */
    strings: Record<string, Record<string, string>>;
    hero: { headline: string; callouts: string[] };
    legal: string;
    voice: { default: string };
    locales: { default: string; supported: string[] };
    features: { dayparts: boolean };
    menuUrl: string;
    models: PersonaModels;
}

export interface PersonaMenuItem {
    id: string;
    name: string;
    category: string;
    price: number;
    description?: string;
    [key: string]: unknown;
}
