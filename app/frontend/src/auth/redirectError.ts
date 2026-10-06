/**
 * Redirect-error state for the Entra sign-in gate (ADR-002, design §18.6/§18.12, issue GH-145, PR
 * GH-148 review round 2, item B4). `msalInstance.ts`'s `initializeMsal()` runs before React ever
 * mounts, so a failed `handleRedirectPromise()` (the visitor cancelled, admin consent is required,
 * or the app isn't assigned to them at all -- AADSTS50105) has nowhere else to go: it can't throw
 * (that would send `index.tsx`'s bootstrap into `ConfigErrorScreen`, the wrong screen, with no
 * retry path) and it can't be handed straight to a not-yet-rendered component. This module is the
 * hand-off point in between: `msalInstance.ts` classifies and stashes the error here, and
 * `EntraAuthGate` reads it once on mount (same pattern as `signOut.ts`'s explicit-sign-out flag,
 * except this one is deliberately in-memory, not sessionStorage -- a genuine page reload is a new
 * user-initiated action and should get a fresh automatic redirect attempt, not keep replaying a
 * stale failure from a previous load).
 */

export type RedirectErrorKind = 'cancelled' | 'consent_required' | 'not_assigned' | 'unknown';

export interface RedirectError {
  readonly kind: RedirectErrorKind;
  /** Friendly, classified copy for the gate to show. Never the raw MSAL error message: that can
   * embed tenant/app ids and isn't meant for an end user. */
  readonly message: string;
  /**
   * Entra's own error code (PR GH-148 review round 2, item R3, design 18.6: "shows Entra's error
   * code with a short explanation"), so Brian can triage the exact Entra failure (18.13 step 3)
   * without ever seeing the raw error message, which can carry the signed-in user's UPN. The first
   * `AADSTS\d+` match in the raw error message, else the MSAL `errorCode` when it looks like a
   * plain code (`^[a-z_]+$`); `null` when neither is present.
   */
  readonly code: string | null;
}

type MsalErrorShape = {
  errorCode?: unknown;
  errorMessage?: unknown;
  message?: unknown;
};

/**
 * Duck-typed on `@azure/msal-common`'s `AuthError` base class, which every MSAL error subclass
 * (`BrowserAuthError`, `ServerError`, `InteractionRequiredAuthError`, ...) exposes plain
 * `errorCode`/`errorMessage` string properties on -- so this never needs to import (or stay in
 * sync with) every individual error class MSAL might throw here.
 */
export function classifyRedirectError(error: unknown): RedirectError {
  const shape = (error ?? {}) as MsalErrorShape;
  const errorCode = typeof shape.errorCode === 'string' ? shape.errorCode : '';
  const errorMessage =
    (typeof shape.errorMessage === 'string' && shape.errorMessage) ||
    (typeof shape.message === 'string' && shape.message) ||
    '';

  // R3 (PR GH-148 review round 2): the code Brian needs for triage, never shown without the
  // friendly message next to it. The AADSTS code (if the raw message carries one) wins over the
  // MSAL errorCode, since it's the more specific, Entra-assigned identifier.
  const aadstsMatch = /AADSTS\d+/.exec(errorMessage);
  const code = aadstsMatch ? aadstsMatch[0] : /^[a-z_]+$/.test(errorCode) ? errorCode : null;

  // item R2 (round 2): `user_cancelled` is a genuine MSAL error code for a user-cancelled
  // interactive flow. `access_denied` and AADSTS65004 ("the user declined to consent") are the
  // same outcome from the visitor's perspective -- they backed out of the Entra prompt -- so they
  // are classified the same way rather than falling through to the generic "unknown" message.
  if (errorCode === 'user_cancelled' || errorCode === 'access_denied' || /AADSTS65004/.test(errorMessage)) {
    return { kind: 'cancelled', message: 'Sign-in was cancelled.', code };
  }

  // Entra itself blocking pre-auth (the app registration exists, but this user/tenant was never
  // assigned to it) -- a DIFFERENT mechanism than the app's own DriveThru.User role check (that's
  // a 403 after a successful sign-in, handled by AUTH_FORBIDDEN_EVENT instead).
  if (/AADSTS50105/.test(errorMessage)) {
    return {
      kind: 'not_assigned',
      message: "You're not assigned to this app yet. Ask Brian to assign you access in Entra, then try again.",
      code,
    };
  }

  if (errorCode === 'consent_required' || /consent_required/i.test(errorMessage)) {
    return {
      kind: 'consent_required',
      message: 'Need admin approval to sign in. Ask Brian to grant consent for this app, then try again.',
      code,
    };
  }

  return { kind: 'unknown', message: "Sign-in didn't complete. Please try again.", code };
}

let redirectError: RedirectError | null = null;

export function setRedirectError(error: unknown): void {
  redirectError = classifyRedirectError(error);
}

export function getRedirectError(): RedirectError | null {
  return redirectError;
}

export function clearRedirectError(): void {
  redirectError = null;
}

/** Test-only: reset the module-level singleton so each test starts from a clean slate. */
export function resetRedirectErrorForTests(): void {
  clearRedirectError();
}
