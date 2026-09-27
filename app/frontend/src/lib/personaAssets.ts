/**
 * Shared helper for building `/personas/{id}/assets/**` request URLs from the pack-relative paths
 * `persona.json` declares (`ui.assets.logo`/`favicon`/`apologyClip`, and this app's convention of
 * `demo/*.json` for F7 sample data). Issue #80 F3/F4/F7.
 *
 * Unlike `_persona_logo_url`'s server-computed `logoUrl` (the ONLY asset URL the wire contract
 * precomputes -- see `types/persona.ts`), favicon/apologyClip/demo URLs are never precomputed by
 * the backend, so the frontend builds them itself: unversioned (no `?v=`), which
 * `app/backend/app.py`'s docstring confirms is an intentional, supported fallback -- it just means
 * these particular assets don't get the year-long immutable cache header the versioned logo/menu
 * URLs do.
 */
export function personaAssetUrl(personaId: string, relativePath: string): string {
    const relative = relativePath.replace(/^assets\//, "").replace(/^\/+/, "");
    return `/personas/${encodeURIComponent(personaId)}/assets/${relative}`;
}
