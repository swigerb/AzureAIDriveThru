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

/** Interactive sign-out entry point for any in-app control (e.g. the header logout button). */
export function signOutInteractive(): void {
  markExplicitSignOut();
  void getMsalInstance().logoutRedirect();
}
