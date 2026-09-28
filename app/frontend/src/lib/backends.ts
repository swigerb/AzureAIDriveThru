import type { PersonaBackendEntry } from "@/types/persona";

/**
 * Which of `/api/personas`' `backends[]` (design doc §5.2/§10.1) matches the app's current
 * hostname. `app/backend/app.py`'s `_backend_entries` docstring: an entry's `url` can be the
 * empty string to mean "this same origin" (the common case for the backend that's actually
 * serving the page in local/single-backend dev), so an empty-`url` entry is matched by exclusion
 * -- i.e. whenever no *other* entry's URL matches the current origin -- rather than by comparing
 * `""` to `window.location.origin` directly.
 *
 * Falls back to the first entry if nothing matches at all (e.g. this frontend is being served
 * from neither backend's declared hostname, such as the Vite dev server on its own port).
 */
export function currentBackendId(backends: PersonaBackendEntry[], origin: string = typeof window !== "undefined" ? window.location.origin : ""): string {
    if (backends.length === 0) return "";
    const explicitMatch = backends.find(backend => backend.url && originMatches(backend.url, origin));
    if (explicitMatch) return explicitMatch.id;
    const selfEntry = backends.find(backend => !backend.url);
    return (selfEntry ?? backends[0]).id;
}

function originMatches(url: string, origin: string): boolean {
    try {
        return new URL(url).origin === origin;
    } catch {
        return false;
    }
}

/**
 * Builds the URL to navigate to when switching to `backend` (issue #80 F11). The browser's
 * address bar doesn't always carry `?persona=`/`?model=` -- persona-context.tsx/App.tsx can
 * resolve either purely from localStorage -- so this constructs the query string explicitly from
 * the caller's current `personaId`/`modelId` rather than copying `window.location.search`,
 * guaranteeing both survive the hop to the other backend's hostname (design doc §10.1's "Option
 * A: two container apps, header switch navigates between hostnames, no proxy").
 *
 * Rick's PR 134 review nit: refuses any scheme other than `http:`/`https:` before returning a URL
 * a caller could hand to `location.assign` -- `backend.url` comes from `/api/personas`' server
 * config, not guest input, but rejecting anything else here is cheap defense against a
 * misconfigured entry (e.g. a stray `javascript:`/`file:` URL) turning a picker click into script
 * execution or a local file navigation.
 */
export function backendTargetUrl(backend: PersonaBackendEntry, personaId: string, modelId: string): string {
    const base = backend.url || (typeof window !== "undefined" ? window.location.origin : "");
    const url = new URL(base);
    if (url.protocol !== "http:" && url.protocol !== "https:") {
        throw new Error(`Refusing to navigate to backend "${backend.id}": unsupported URL scheme "${url.protocol}"`);
    }
    if (personaId) url.searchParams.set("persona", personaId);
    if (modelId) url.searchParams.set("model", modelId);
    return url.toString();
}
