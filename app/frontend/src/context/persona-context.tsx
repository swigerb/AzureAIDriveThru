import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, ReactNode } from "react";
import i18next from "i18next";

import { baseTranslationResources } from "@/i18n/baseResources";

import { applyTheme, applyDarkTheme, resolvePersonaTheme, PersonaWireTheme } from "@/lib/personaTheme";
import { personaAssetUrl } from "@/lib/personaAssets";
import { DEFAULT_VOICE } from "@/lib/voices";
import type { PersonaDetail, PersonaSummary, PersonasIndexResponse } from "@/types/persona";

const STORAGE_KEY = "personaId";
const QUERY_PARAM = "persona";

/**
 * A neutral, brand-free wire theme: plain grays, no persona hue. Used only for `NEUTRAL_DETAIL`
 * below -- `resolvePersonaTheme` derives a full `PersonaAccentPalette` from these four base roles
 * the same way it would for any other non-default persona pack (issue #80 F1/F3).
 */
const NEUTRAL_THEME: PersonaWireTheme = {
    light: {
        primary: "220 9% 30%",
        secondary: "220 9% 46%",
        background: "0 0% 100%",
        foreground: "220 15% 15%"
    }
};

/**
 * Rick's PR-110 review, item 1 + item 6 (issue #80 F1/F6): no persona pack -- any brand or
 * otherwise -- is hard-coded here anymore. `NEUTRAL_ID`/`NEUTRAL_SUMMARY`/`NEUTRAL_DETAIL` are this
 * app's bundled PLACEHOLDER, not a "default persona": no display name, no logo, no theme hue, no
 * copy overrides (every string a component asks for falls through to the shared, persona-neutral
 * defaults in the `locales` translation files), and no menu/apology-clip URLs. `PersonaProvider`
 * uses this as the initial React state (so there is nothing branded to paint before the very
 * first render) and, per `App.tsx`'s `ready` gate, `App()` doesn't render the branded shell at all
 * until `ready` flips true -- so in practice this placeholder is only ever visible for the
 * instant between mount and the `/api/personas` round trip resolving (or, in an offline/
 * unmocked-fetch environment such as a test that doesn't stub `fetch`, it's what the app is left
 * running on, `ready` still flips true so the app doesn't hang forever).
 */
const NEUTRAL_ID = "";
const NEUTRAL_SUMMARY: PersonaSummary = {
    id: NEUTRAL_ID,
    displayName: "",
    logoUrl: "",
    theme: NEUTRAL_THEME
};
const NEUTRAL_DETAIL: PersonaDetail = {
    id: NEUTRAL_ID,
    roleName: "",
    title: "",
    theme: NEUTRAL_THEME,
    assets: { logo: "", favicon: "" },
    strings: {},
    hero: { headline: "", callouts: [] },
    legal: "",
    voice: { default: DEFAULT_VOICE },
    locales: { default: "en", supported: ["en", "es", "fr", "ja"] },
    features: { dayparts: false },
    menuUrl: "",
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } }
};

interface PersonaContextValue {
    /** Enabled personas (design doc §5.2's `/api/personas` list) -- at least the fallback entry. */
    personas: PersonaSummary[];
    /** The active persona's full detail (design doc §5.2's `/api/personas/{id}`). */
    current: PersonaDetail;
    /** This persona's logo URL -- only ever available from the summary list (see `types/persona.ts`). */
    logoUrl: string;
    /** True once the initial `/api/personas` + `/api/personas/{id}` round trip has settled (success
     * or failure). `App.tsx`'s `App()` gates ALL rendering of `<SonicApp />` on this (issue #80 F6,
     * Rick's PR-110 review item 6): until it flips true, `current`/`personas` hold the brand-neutral
     * placeholder (`NEUTRAL_DETAIL`/`NEUTRAL_SUMMARY`) so there is nothing persona-specific to show
     * regardless of how the component tree renders it. */
    ready: boolean;
    /** Non-null if the live fetch failed and the app is running on the neutral placeholder. */
    error: string | null;
    selectPersona: (id: string) => void;
}

const PersonaContext = createContext<PersonaContextValue | undefined>(undefined);

async function safeFetchJson<T>(url: string): Promise<T | null> {
    try {
        const response = await fetch(url);
        if (!response.ok) return null;
        return (await response.json()) as T;
    } catch {
        return null;
    }
}

function unflatten(flat: Record<string, string>): Record<string, unknown> {
    const root: Record<string, unknown> = {};
    for (const [dottedKey, value] of Object.entries(flat)) {
        const segments = dottedKey.split(".");
        let node = root;
        segments.forEach((segment, index) => {
            if (index === segments.length - 1) {
                node[segment] = value;
            } else {
                node[segment] = (node[segment] as Record<string, unknown>) ?? {};
                node = node[segment] as Record<string, unknown>;
            }
        });
    }
    return root;
}

function initialPersonaId(defaultId: string): string {
    if (typeof window === "undefined") return defaultId;
    const fromQuery = new URLSearchParams(window.location.search).get(QUERY_PARAM);
    if (fromQuery) return fromQuery;
    return window.localStorage.getItem(STORAGE_KEY) ?? defaultId;
}

/**
 * Fetches `/api/personas` + `/api/personas/{id}` at startup (design doc §5.2), resolves the
 * session's persona BEFORE any realtime connection is made (`?persona=` > last localStorage
 * choice > the catalog's declared default -- ADR-001 decision 1: one URL, no per-request routing),
 * applies its theme (light + dark, `lib/personaTheme.ts`), sets `document.title`/the favicon, and
 * merges its `ui.strings` into i18next.
 *
 * Deliberately "dumb" about whether a session is currently active: `selectPersona` always applies
 * immediately. Gating the picker while a session is in progress (ADR-001 decision 2: no
 * mid-conversation persona switching) is `App.tsx`'s job, since that's where `isRecording`/order
 * state actually live.
 */
export function PersonaProvider({ children }: { children: ReactNode }) {
    const [personas, setPersonas] = useState<PersonaSummary[]>([NEUTRAL_SUMMARY]);
    const [current, setCurrent] = useState<PersonaDetail>(NEUTRAL_DETAIL);
    const [personaId, setPersonaId] = useState<string>(() => initialPersonaId(NEUTRAL_ID));
    const [ready, setReady] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const catalogRef = useRef<PersonasIndexResponse | null>(null);

    const applyDetail = useCallback((id: string, detail: PersonaDetail, summary: PersonaSummary | undefined) => {
        const theme = resolvePersonaTheme(id, detail.theme);
        applyTheme(theme);
        applyDarkTheme(theme);

        if (typeof document !== "undefined") {
            document.title = detail.title;
            if (summary?.logoUrl) {
                let favicon = document.querySelector<HTMLLinkElement>("link[rel~='icon']");
                if (!favicon) {
                    favicon = document.createElement("link");
                    favicon.rel = "icon";
                    document.head.appendChild(favicon);
                }
                favicon.href = detail.assets.favicon ? personaAssetUrl(id, detail.assets.favicon) : summary.logoUrl;
            }
        }

        // Guard rather than assume: `addResourceBundle` (and its store-backed siblings) only exist
        // on the i18next singleton once `i18n/config.ts`'s `.init()` has run (see i18next's
        // `I18n.prototype.init`, which is where they're bound) -- true for every real app render
        // (see `index.tsx`'s import order), but NOT in this app's test environment, where `test/
        // setup.ts` globally mocks `react-i18next` and nothing ever imports/initializes the real
        // `i18next` singleton. Skipping the merge there is harmless: the mocked `useTranslation()`
        // already echoes back whatever key a component asks for.
        if (typeof i18next.addResourceBundle === "function") {
            // Issue 119 item 1 (follow-up, found via a partner pack's verification pass): reset
            // every locale's "translation" bundle back to the pristine, persona-neutral base
            // *before* merging the current persona's overrides on top. `addResourceBundle`'s merge
            // (the `deep=true` below) is cumulative across calls -- it only adds/overwrites the
            // keys the new table actually contains, it never removes ones the new table omits.
            // Without this reset, a pack whose `ui.strings` doesn't cover every key (some packs
            // intentionally define only a handful of brand-specific keys, e.g. no
            // `ticket.emptyHint` override at all) silently kept showing whatever the *previously
            // active* persona last set that key to (e.g. Sonic, which does define
            // `ticket.emptyHint`), instead of falling back to the shared neutral copy.
            for (const locale of Object.keys(baseTranslationResources)) {
                i18next.addResourceBundle(locale, "translation", structuredClone(baseTranslationResources[locale]), false, true);
            }
            for (const [locale, table] of Object.entries(detail.strings)) {
                i18next.addResourceBundle(locale, "translation", unflatten(table), true, true);
            }
        }
    }, []);

    const loadPersona = useCallback(
        async (id: string) => {
            const detail = await safeFetchJson<PersonaDetail>(`/api/personas/${encodeURIComponent(id)}`);
            const summary = catalogRef.current?.personas.find(entry => entry.id === id);
            if (detail) {
                setCurrent(detail);
                applyDetail(id, detail, summary);
                setError(null);
            } else {
                setError(`Could not load persona "${id}"; staying on the previous selection.`);
            }
        },
        [applyDetail]
    );

    useEffect(() => {
        let cancelled = false;

        (async () => {
            const index = await safeFetchJson<PersonasIndexResponse>("/api/personas");
            if (cancelled) return;
            if (!index || index.personas.length === 0) {
                // Offline / unmocked-fetch environment (e.g. a test that doesn't stub `fetch`):
                // there's no catalog to resolve a real persona from, so stay on the neutral
                // placeholder rather than ever reaching for a hard-coded brand (issue #80 F1,
                // Rick's PR-110 review item 1) -- `ready` still flips true so the app doesn't hang.
                setReady(true);
                return;
            }

            catalogRef.current = index;
            setPersonas(index.personas);
            const resolvedId = index.personas.some(p => p.id === personaId) ? personaId : index.default;
            setPersonaId(resolvedId);
            await loadPersona(resolvedId);
            if (!cancelled) setReady(true);
        })();

        return () => {
            cancelled = true;
        };
        // Only ever runs once at startup -- `selectPersona` (below) drives subsequent changes.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const selectPersona = useCallback(
        (id: string) => {
            if (id === personaId) return;
            setPersonaId(id);
            window.localStorage.setItem(STORAGE_KEY, id);
            void loadPersona(id);
        },
        [personaId, loadPersona]
    );

    const logoUrl = useMemo(() => personas.find(p => p.id === personaId)?.logoUrl ?? NEUTRAL_SUMMARY.logoUrl, [personas, personaId]);

    const value = useMemo<PersonaContextValue>(
        () => ({ personas, current, logoUrl, ready, error, selectPersona }),
        [personas, current, logoUrl, ready, error, selectPersona]
    );

    return <PersonaContext.Provider value={value}>{children}</PersonaContext.Provider>;
}

export function usePersonaContext(): PersonaContextValue {
    const context = useContext(PersonaContext);
    if (!context) {
        throw new Error("usePersonaContext must be used within a PersonaProvider");
    }
    return context;
}
