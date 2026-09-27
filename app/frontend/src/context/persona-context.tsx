import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, ReactNode } from "react";
import i18next from "i18next";

import { SONIC_THEME, applyTheme, applyDarkTheme, resolvePersonaTheme } from "@/lib/personaTheme";
import { personaAssetUrl } from "@/lib/personaAssets";
import type { PersonaDetail, PersonaSummary, PersonasIndexResponse } from "@/types/persona";

const STORAGE_KEY = "personaId";
const QUERY_PARAM = "persona";

/**
 * Sonic's real content, hard-coded here as this app's bundled default/fallback (issue #80 F1).
 *
 * This is NOT a "frontend copy" of the kind #80's drift guard retires (design doc §9, issue #80
 * comment 3): it isn't a duplicated FILE that could drift from `personas/sonic/persona.json` bytes
 * -- it's the minimum data `PersonaProvider` needs to render something coherent the instant the
 * app mounts, before `/api/personas` has had a chance to respond (or if it never does -- e.g. an
 * existing test that renders `<RootApp />` without mocking `fetch`, or a genuinely offline dev
 * session). Once the live fetch resolves, its response always wins and overwrites this. Every
 * value below is copied verbatim from `personas/sonic/persona.json` so there's no observable
 * difference between "fallback" and "freshly fetched" for Sonic -- the one persona this app has
 * always shipped with.
 */
const FALLBACK_ID = "sonic";
const FALLBACK_SUMMARY: PersonaSummary = {
    id: FALLBACK_ID,
    displayName: "Sonic Drive-In",
    logoUrl: personaAssetUrl(FALLBACK_ID, "logo.svg"),
    theme: {
        light: { ...SONIC_THEME.light },
        dark: SONIC_THEME.dark,
        font: SONIC_THEME.font
    }
};
const FALLBACK_DETAIL: PersonaDetail = {
    id: FALLBACK_ID,
    title: "Sonic Drive-In Voice Ordering",
    theme: FALLBACK_SUMMARY.theme,
    assets: {
        logo: "assets/logo.svg",
        favicon: "assets/favicon.ico",
        apologyClip: "assets/audio/apology-{lang}.wav"
    },
    strings: {
        en: {
            "app.title": "Sonic Voice Ordering",
            "status.notRecordingMessage": "Let's order from America's Drive-In!",
            "ticket.kicker": "Carhop ticket",
            "ticket.title": "Your Sonic Order",
            "menu.button": "View Sonic Menu"
        }
    },
    hero: { headline: "Sonic ordering powered by Azure conversation intelligence", callouts: [] },
    legal:
        "Disclaimer: This project is a non-commercial demo application created for educational and illustrative purposes only. It is not affiliated with, endorsed, or sponsored by Inspire Brands, Inc. or Sonic Corp. Any references to Sonic Drive-In or use of Sonic-inspired colors or themes are solely for demonstration and do not represent an official product.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en", "es", "fr", "ja"] },
    features: { dayparts: false },
    menuUrl: `/personas/${FALLBACK_ID}/menu.json`,
    models: { realtime: { default: "gpt-realtime-2.1", allowed: ["gpt-realtime-2.1", "gpt-realtime-mini"] } }
};

interface PersonaContextValue {
    /** Enabled personas (design doc §5.2's `/api/personas` list) -- at least the fallback entry. */
    personas: PersonaSummary[];
    /** The active persona's full detail (design doc §5.2's `/api/personas/{id}`). */
    current: PersonaDetail;
    /** This persona's logo URL -- only ever available from the summary list (see `types/persona.ts`). */
    logoUrl: string;
    /** True once the initial `/api/personas` + `/api/personas/{id}` round trip has settled (success
     * or failure) -- consumers can use this to show a subtle "still loading" affordance, but nothing
     * blocks on it: `current`/`personas` are always populated (with the fallback, at worst). */
    ready: boolean;
    /** Non-null if the live fetch failed and the app is running on fallback data. */
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
 * choice > the catalog's declared default > the bundled Sonic fallback -- ADR-001 decision 1: one
 * URL, no per-request routing), applies its theme (light + dark, `lib/personaTheme.ts`), sets
 * `document.title`/the favicon, and merges its `ui.strings` into i18next.
 *
 * Deliberately "dumb" about whether a session is currently active: `selectPersona` always applies
 * immediately. Gating the picker while a session is in progress (ADR-001 decision 2: no
 * mid-conversation persona switching) is `App.tsx`'s job, since that's where `isRecording`/order
 * state actually live.
 */
export function PersonaProvider({ children }: { children: ReactNode }) {
    const [personas, setPersonas] = useState<PersonaSummary[]>([FALLBACK_SUMMARY]);
    const [current, setCurrent] = useState<PersonaDetail>(FALLBACK_DETAIL);
    const [personaId, setPersonaId] = useState<string>(() => initialPersonaId(FALLBACK_ID));
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
            } else if (id !== FALLBACK_ID) {
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
                // Offline / unmocked-fetch test environment: keep the fallback and stop here --
                // `current`/`personas` are already populated, so the app still renders.
                applyDetail(FALLBACK_ID, FALLBACK_DETAIL, FALLBACK_SUMMARY);
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

    const logoUrl = useMemo(() => personas.find(p => p.id === personaId)?.logoUrl ?? FALLBACK_SUMMARY.logoUrl, [personas, personaId]);

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
