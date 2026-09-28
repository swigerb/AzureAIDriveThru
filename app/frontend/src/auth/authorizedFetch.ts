import { acquireApiToken } from './tokenService';
import { authConfig } from './authConfig';

/**
 * Global fetch interceptor that attaches the Entra bearer token to our protected same-origin
 * routes only (ADR-002, design §18.2/§18.6, issue #145).
 *
 * Rather than editing every call site (and risking a future call that forgets the header), token
 * attachment is centralized here: {@link installAuthorizedFetch} wraps `window.fetch` once at
 * startup so all protected REST/asset requests go out authenticated. This is a no-op when Entra is
 * unconfigured (Development pass-through).
 *
 * Route matrix (design §18.2, deny-by-default -- an allow-list of PUBLIC branding assets, not an
 * allow-list of protected paths):
 *   - `/api/**`                                          -> always protected.
 *   - `/personas/{id}/menu.json`                         -> protected.
 *   - `/personas/{id}/assets/**` for `.svg .png .jpg .jpeg .webp .ico .wav .mp3`
 *                                                         -> PUBLIC (favicon/`<img>`/`<audio>`/CSS
 *                                                            can't carry a bearer) -- never gets a token.
 *   - `/personas/{id}/assets/**` for anything else (today `demo/*.json`)
 *                                                         -> protected (fetched via `fetch()`).
 *   - Everything else (the SPA shell, cross-origin requests, e.g. the direct-AOAI debug mode's own key)
 *                                                         -> never gets the Entra token.
 *
 * On a 401 the wrapper transparently forces a fresh token and retries once; if it still fails it
 * emits {@link AUTH_REQUIRED_EVENT} so `AuthGate` can re-authenticate. A 403 (authenticated but
 * missing the `DriveThru.User` role) emits {@link AUTH_FORBIDDEN_EVENT} so the UI can show a
 * precise access-denied message.
 */

export const AUTH_REQUIRED_EVENT = 'drivethru:auth-required';
export const AUTH_FORBIDDEN_EVENT = 'drivethru:auth-forbidden';

const PUBLIC_ASSET_EXTENSION_RE = /\.(svg|png|jpe?g|webp|ico|wav|mp3)$/i;
const MENU_JSON_RE = /^\/personas\/[^/]+\/menu\.json$/;
const PERSONA_ASSET_RE = /^\/personas\/[^/]+\/assets\/(.+)$/;

function currentOrigin(): string | null {
  if (typeof window !== 'undefined' && window.location && window.location.origin) {
    return window.location.origin;
  }
  return null;
}

function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.toString();
  return input.url;
}

/** True for a persona asset path that is NOT a public branding file, i.e. must carry a bearer. */
function isProtectedPersonaAsset(pathname: string): boolean {
  const match = PERSONA_ASSET_RE.exec(pathname);
  if (!match) return false;
  return !PUBLIC_ASSET_EXTENSION_RE.test(match[1]);
}

/** True only for a path on our protected surface: `/api/**`, `menu.json`, or non-branding assets. */
function isProtectedPath(pathname: string): boolean {
  if (pathname === '/api' || pathname.startsWith('/api/')) return true;
  if (MENU_JSON_RE.test(pathname)) return true;
  return isProtectedPersonaAsset(pathname);
}

/**
 * Decides whether a request targets our protected surface and may therefore carry the Entra
 * bearer token. The check is deliberately strict to avoid ever leaking the token to a lookalike,
 * a third party, or a public branding asset:
 *   1. Resolve the URL against the current origin (`new URL(input, location.origin)`), so relative
 *      and absolute inputs are normalized identically. Unparseable URLs are rejected.
 *   2. Reject any URL that embeds credentials (userinfo), e.g. `https://trusted@evil.example`.
 *   3. Require an EXACT origin match (scheme + host + port) -- this app is single-origin only, so
 *      any other origin (including the direct-AOAI debug mode's own endpoint) never gets the token.
 *   4. Require the path to be on the protected surface (see {@link isProtectedPath}).
 */
export function isAuthorizedFetchTarget(input: RequestInfo | URL): boolean {
  const origin = currentOrigin();
  if (!origin) {
    return false;
  }

  let parsed: URL;
  try {
    parsed = new URL(requestUrl(input), origin);
  } catch {
    return false;
  }

  // Never attach the token to a URL that smuggles credentials in the userinfo section.
  if (parsed.username !== '' || parsed.password !== '') {
    return false;
  }

  // Exact-origin only: single-origin app, no configured secondary API origin.
  if (parsed.origin !== origin) {
    return false;
  }

  return isProtectedPath(parsed.pathname);
}

let installed = false;

/** Test-only: allow re-installing the interceptor across test files/cases. */
export function resetAuthorizedFetchForTests(): void {
  installed = false;
}

export function installAuthorizedFetch(): void {
  if (installed || !authConfig.isConfigured || typeof window === 'undefined') {
    return;
  }
  installed = true;

  const originalFetch: typeof window.fetch = window.fetch.bind(window);

  const send = (input: RequestInfo | URL, init: RequestInit | undefined, bearer: string | null) => {
    if (!bearer) {
      return originalFetch(input, init);
    }
    if (input instanceof Request) {
      const headers = new Headers(input.headers);
      headers.set('Authorization', `Bearer ${bearer}`);
      return originalFetch(new Request(input, { headers }));
    }
    const headers = new Headers(init?.headers);
    headers.set('Authorization', `Bearer ${bearer}`);
    return originalFetch(input, { ...init, headers });
  };

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    if (!isAuthorizedFetchTarget(input)) {
      return originalFetch(input, init);
    }

    let token: string | null;
    try {
      token = await acquireApiToken();
    } catch {
      token = null; // fall through unauthenticated; the 401 path below handles recovery
    }

    let response = await send(input, init, token);

    if (response.status === 401) {
      let fresh: string | null;
      try {
        fresh = await acquireApiToken({ forceRefresh: true });
      } catch {
        fresh = null;
      }
      if (fresh) {
        response = await send(input, init, fresh);
      }
      if (response.status === 401) {
        window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
      }
    } else if (response.status === 403) {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    }

    return response;
  };
}
