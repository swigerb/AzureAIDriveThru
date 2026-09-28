import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { loginRequest } from '../authConfig';
import { acquireApiToken } from '../tokenService';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../authorizedFetch';
import {
  markExplicitSignOut,
  consumeExplicitSignOut,
  clearExplicitSignOut,
  hasAuthRequiredRedirected,
  markAuthRequiredRedirected,
  clearAuthRequiredRedirected,
} from '../signOut';
import { getRedirectError, clearRedirectError, classifyRedirectError, type RedirectError } from '../redirectError';

/**
 * The live Entra sign-in gate (ADR-002, design §18.6, issue GH-145). Rendered only when
 * `authConfig.isConfigured` -- see `AuthGate.tsx` for the pass-through dispatch.
 *
 * Behavior (design §18.6, verbatim): "Wraps `PersonaProvider` and `App`, so nothing
 * persona-related loads before sign-in. The sign-in screen uses neutral product branding. An
 * unauthenticated load starts `loginRedirect` automatically, which the Entra SSO cookie makes
 * silent on the second hostname. After an explicit sign-out or a 403 it shows a button instead, so
 * it can never loop. A 403 shows 'not authorized: ask Brian to assign you DriveThru.User'."
 *
 * The auto-redirect must never loop, so an EXPLICIT sign-out (here or from the in-app logout
 * control, see `../signOut.ts`) sets a sessionStorage flag that survives the full-page redirect
 * round trip (in-memory React state does not) and suppresses the next auto-redirect until the
 * visitor clicks "Sign in with Microsoft" again. The initial-mount auto-redirect below uses a
 * `useRef` guard (`autoRedirectAttempted`), which is fine to reset every fresh page load -- that's
 * a new visit, not a loop. Two runtime failure paths added in PR GH-148 review round 2 need more
 * than that:
 *   - item B3/R1: `AUTH_REQUIRED_EVENT` (a persistent 401 from `authorizedFetch`, or a null Entra
 *     token on a WebSocket connect attempt from `useRealtime`, item B2) means whatever token the
 *     app was using no longer works at all -- this drops back to a sign-in state. Round 1 shipped
 *     this guarded only by the same per-mount `useRef`, but `loginRedirect()` navigates away, so
 *     the redirect round trip ends on a BRAND NEW page load with a fresh ref -- a backend that
 *     keeps answering 401 would auto-redirect forever. `hasAuthRequiredRedirected()` /
 *     `markAuthRequiredRedirected()` (`../signOut.ts`) use `sessionStorage` instead, which
 *     survives that round trip, so this path redirects automatically at most once per tab session
 *     until the visitor clicks "Sign in" again (which clears the flag, same as an explicit
 *     sign-out).
 *   - item B4/R2: a failed `handleRedirectPromise()` (cancelled, consent required, or AADSTS50105
 *     -- see `../redirectError.ts`) is read once on mount and shown as a friendly error with a
 *     manual "Sign in" button, with the auto-redirect suppressed while it's present -- otherwise a
 *     reload would just repeat the same failing redirect forever. Round 2 also catches a
 *     `loginRedirect()` call itself rejecting -- msal-browser rejects with `user_cancelled` when
 *     the visitor presses Back from the Entra page and the browser restores the app from bfcache,
 *     which is the most common way to cancel a redirect sign-in. Before this fix that rejection
 *     was silently dropped and `redirectStarting` was never reset, so the restored page stayed
 *     stuck on "Signing you in..." forever with no button.
 */
export function EntraAuthGate({ children }: { children: ReactNode }) {
  const { instance, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [forbidden, setForbidden] = useState(false);
  const [authRequired, setAuthRequired] = useState(false);
  const [signedOut, setSignedOut] = useState(() => consumeExplicitSignOut());
  const [redirectError, setRedirectErrorState] = useState<RedirectError | null>(() => getRedirectError());
  // Item S2: msal-browser 5.23 (pinned here) has no `InteractionStatus.Login` -- Retail Pulse
  // (msal-browser ^4.30, an older major) does, which is what Rick's review referenced.
  // `loginRedirect()` navigates away synchronously, so there is no MSAL-observable "in progress"
  // status for the brief window between calling it and the browser actually unloading the page;
  // this local flag fills that gap so the sign-in button never flashes visible during an
  // automatic (or manual) redirect.
  const [redirectStarting, setRedirectStarting] = useState(false);
  const autoRedirectAttempted = useRef(false);

  const startLoginRedirect = useCallback(() => {
    setRedirectStarting(true);
    // Item R2: a rejected `loginRedirect()` (most commonly `user_cancelled` from a Back-button
    // bfcache restore -- see the class doc comment above) must clear `redirectStarting` and show
    // the classified error instead of leaving the gate stuck on "Signing you in..." forever with
    // an unhandled rejection. This never retries on its own.
    instance.loginRedirect(loginRequest).catch((error: unknown) => {
      setRedirectStarting(false);
      setRedirectErrorState(classifyRedirectError(error));
    });
  }, [instance]);

  useEffect(() => {
    const onForbidden = () => setForbidden(true);
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    return () => window.removeEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
  }, []);

  useEffect(() => {
    // Item R1 (round 2): `AUTH_REQUIRED_EVENT` can fire again after the FULL redirect round trip
    // this same handler starts (`loginRedirect()` navigates away and the visitor comes back on a
    // brand new page load) -- a per-mount `useRef` (round 1's B3) resets on that new load and
    // cannot stop a persistently-401ing backend from looping forever. `hasAuthRequiredRedirected`
    // is backed by `sessionStorage` instead, so it survives the round trip: this redirects
    // automatically at most once per tab session, and every event after that just shows the
    // auth-required message with a manual "Sign in" button.
    const onAuthRequired = () => {
      setForbidden(false);
      setAuthRequired(true);
      if (!hasAuthRequiredRedirected()) {
        markAuthRequiredRedirected();
        startLoginRedirect();
      }
    };
    window.addEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);
    return () => window.removeEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);
  }, [startLoginRedirect]);

  const signIn = useCallback(() => {
    clearExplicitSignOut();
    clearRedirectError();
    // Item R1: a manual "Sign in" click is the ONE thing (besides an explicit sign-out) allowed
    // to re-arm the auth-required auto-redirect guard, so a genuinely new session gets its one
    // automatic retry too (e.g. after the 24-hour SPA refresh token expires).
    clearAuthRequiredRedirected();
    setRedirectErrorState(null);
    setSignedOut(false);
    setForbidden(false);
    setAuthRequired(false);
    // Mark the auto-redirect effect as already handled so it never double-fires loginRedirect
    // once `signedOut` flips back to false and the effect's dependencies re-evaluate.
    autoRedirectAttempted.current = true;
    startLoginRedirect();
  }, [startLoginRedirect]);

  const signOut = useCallback(() => {
    markExplicitSignOut();
    clearAuthRequiredRedirected();
    void instance.logoutRedirect();
  }, [instance]);

  // Item S1: a 403 means the ID/access token's `roles` claim is stale relative to Brian having
  // just assigned the DriveThru.User role -- Entra bakes the roles claim in at issuance, so only a
  // genuinely NEW token (not merely clearing the local `forbidden` flag, which just re-renders
  // with whatever token is already cached) can actually pick up a newly-granted role.
  const retryForbidden = useCallback(() => {
    // Item R2: a failed forced refresh must not become an unhandled rejection.
    void acquireApiToken({ forceRefresh: true })
      .catch(() => null)
      .finally(() => setForbidden(false));
  }, []);

  const busy =
    redirectStarting ||
    inProgress === InteractionStatus.AcquireToken ||
    inProgress === InteractionStatus.HandleRedirect ||
    inProgress === InteractionStatus.Startup;

  // A fresh (never explicitly signed-out, never forbidden/auth-required/redirect-error) load
  // starts loginRedirect automatically -- silent via the Entra SSO cookie on the second hostname
  // (design §18.7). Fires at most once per mount; the ref (not state) prevents a re-trigger loop
  // even if MSAL's `inProgress`/`isAuthenticated` bounce during the redirect handshake.
  useEffect(() => {
    if (
      !isAuthenticated &&
      !forbidden &&
      !signedOut &&
      !authRequired &&
      !redirectError &&
      inProgress === InteractionStatus.None &&
      !autoRedirectAttempted.current
    ) {
      autoRedirectAttempted.current = true;
      startLoginRedirect();
    }
  }, [isAuthenticated, forbidden, signedOut, authRequired, redirectError, inProgress, startLoginRedirect]);

  if (isAuthenticated && !forbidden && !authRequired && !redirectError) {
    return <>{children}</>;
  }

  return (
    <div
      data-testid="auth-gate"
      className="fixed inset-0 flex items-center justify-center bg-background p-6"
    >
      <Card className="w-full max-w-md text-center">
        <CardHeader className="items-center gap-2">
          <CardTitle>AI Drive-Thru</CardTitle>
          {/* Item S3: a neutral, state-independent subtitle -- the old copy hardcoded "Sign in to
              continue" here even while `forbidden` (already signed in, just missing the role), or
              redirect-error/auth-required (sign-in didn't work at all). Each state's own message
              below carries the state-specific copy instead. */}
          <CardDescription>AI Drive-Thru, a Microsoft Foundry realtime demo</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col items-center gap-4">
          {forbidden ? (
            <>
              <p data-testid="auth-forbidden" className="text-sm text-destructive">
                You're signed in, but you don't have access yet. Ask Brian to assign you the{' '}
                <strong>DriveThru.User</strong> role, then try again.
              </p>
              <div className="flex w-full flex-col gap-2">
                <Button data-testid="auth-retry-button" onClick={retryForbidden}>
                  Retry
                </Button>
                <Button
                  data-testid="auth-switch-account-button"
                  variant="outline"
                  onClick={signOut}
                >
                  Sign in with a different account
                </Button>
              </div>
            </>
          ) : busy ? (
            <p data-testid="auth-signing-in" className="text-sm text-muted-foreground">
              Signing you in…
            </p>
          ) : redirectError ? (
            <>
              <p data-testid="auth-redirect-error" className="text-sm text-destructive">
                {redirectError.message}
              </p>
              {/* Item R3: Entra's own error code, so Brian can triage the exact failure (18.13
                  step 3) -- never the raw error message, which can carry the signed-in user's
                  UPN. Omitted entirely when classification found no code to show. */}
              {redirectError.code ? (
                <p data-testid="auth-redirect-error-code" className="text-xs text-muted-foreground">
                  Error code: {redirectError.code}
                </p>
              ) : null}
              <Button data-testid="auth-signin-button" size="lg" onClick={signIn}>
                Sign in with Microsoft
              </Button>
            </>
          ) : authRequired ? (
            <>
              <p data-testid="auth-required-message" className="text-sm text-muted-foreground">
                Your session needs to be refreshed. Sign in again to continue.
              </p>
              <Button data-testid="auth-signin-button" size="lg" onClick={signIn}>
                Sign in with Microsoft
              </Button>
            </>
          ) : signedOut ? (
            <>
              <p className="text-sm text-muted-foreground">
                Sign in with your organizational account to continue.
              </p>
              <Button data-testid="auth-signin-button" size="lg" onClick={signIn}>
                Sign in with Microsoft
              </Button>
            </>
          ) : (
            <>
              <p className="text-sm text-muted-foreground">
                Sign in with your organizational account to continue.
              </p>
              <Button data-testid="auth-signin-button" size="lg" onClick={signIn}>
                Sign in with Microsoft
              </Button>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

