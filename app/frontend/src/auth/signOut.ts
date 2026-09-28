import { getMsalInstance } from './msalInstance';

/**
 * Shared "explicit sign-out" flag (design §18.6, issue GH-145). Any interactive sign-out --
 * whether from `EntraAuthGate`'s access-denied screen or from the in-app logout control -- must
 * mark this flag BEFORE the full-page `logoutRedirect` navigation, and `EntraAuthGate` must read
 * it on the next mount so it shows a manual "Sign in with Microsoft" button instead of
 * auto-starting `loginRedirect` again. This is what makes an explicit sign-out never loop back
 * into an automatic re-sign-in.
 */
const EXPLICIT_SIGN_OUT_KEY = 'drivethru.auth.explicitSignOut';

export function markExplicitSignOut(): void {
  try {
    window.sessionStorage.setItem(EXPLICIT_SIGN_OUT_KEY, '1');
  } catch {
    // sessionStorage may be unavailable (private mode); the worst case is one extra auto-redirect.
  }
}

export function consumeExplicitSignOut(): boolean {
  try {
    return window.sessionStorage.getItem(EXPLICIT_SIGN_OUT_KEY) === '1';
  } catch {
    return false;
  }
}

export function clearExplicitSignOut(): void {
  try {
    window.sessionStorage.removeItem(EXPLICIT_SIGN_OUT_KEY);
  } catch {
    // no-op
  }
}

/**
 * Cross-page-load guard for the `AUTH_REQUIRED_EVENT` auto-redirect (PR GH-148 review round 2,
 * item R1, ADR-002 item 12 "sign-in never loops").
 *
 * The initial-mount auto-redirect in `EntraAuthGate` (design 18.6's "an unauthenticated load
 * starts loginRedirect automatically") is fine to repeat once per fresh page load -- that's a new
 * visit, not a loop. But `AUTH_REQUIRED_EVENT` (a persistent 401 from `authorizedFetch`, or a null
 * Entra token on a WebSocket connect, GH-148 item B2) can keep firing across the FULL round trip
 * that `loginRedirect()` itself causes: the redirect navigates away, Entra signs the visitor back
 * in silently via SSO, the app reloads, the same persistent 401 fires again, and so on forever. A
 * `useRef` guard (what round 1's B3 shipped) resets on every one of those page loads and therefore
 * cannot prevent this -- it takes a flag that survives navigation, i.e. `sessionStorage`.
 *
 * This key is DELIBERATELY separate from `EXPLICIT_SIGN_OUT_KEY` above: an explicit sign-out and
 * an auth-required redirect are different situations that happen to want the same "don't
 * auto-redirect again until the visitor asks" property.
 */
const AUTH_REQUIRED_REDIRECTED_KEY = 'drivethru.auth.authRequiredRedirected';

/**
 * True once this tab session has already auto-redirected in response to `AUTH_REQUIRED_EVENT`, or
 * when `sessionStorage` itself is unavailable (private/incognito mode can throw on access) -- in
 * that failure case we fail SAFE to "already redirected", i.e. the manual Sign in button, rather
 * than risk an undetectable redirect loop.
 */
export function hasAuthRequiredRedirected(): boolean {
  try {
    return window.sessionStorage.getItem(AUTH_REQUIRED_REDIRECTED_KEY) === '1';
  } catch {
    return true;
  }
}

export function markAuthRequiredRedirected(): void {
  try {
    window.sessionStorage.setItem(AUTH_REQUIRED_REDIRECTED_KEY, '1');
  } catch {
    // sessionStorage may be unavailable; hasAuthRequiredRedirected() already fails safe above.
  }
}

/** Cleared only on a manual "Sign in" click or an explicit sign-out -- never on success. */
export function clearAuthRequiredRedirected(): void {
  try {
    window.sessionStorage.removeItem(AUTH_REQUIRED_REDIRECTED_KEY);
  } catch {
    // no-op
  }
}

/** Interactive sign-out entry point for any in-app control (e.g. the header logout button). */
export function signOutInteractive(): void {
  markExplicitSignOut();
  clearAuthRequiredRedirected();
  void getMsalInstance().logoutRedirect();
}
