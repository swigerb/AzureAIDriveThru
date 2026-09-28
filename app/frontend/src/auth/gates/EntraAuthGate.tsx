import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { loginRequest } from '../authConfig';
import { acquireApiToken } from '../tokenService';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../authorizedFetch';
import { markExplicitSignOut, consumeExplicitSignOut, clearExplicitSignOut } from '../signOut';
import { getRedirectError, clearRedirectError, type RedirectError } from '../redirectError';

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
 * visitor clicks "Sign in with Microsoft" again. The same "at most once per page load" guard
 * (`autoRedirectAttempted`) also covers two runtime failure paths added in PR GH-148 review round
 * 2:
 *   - item B3: `AUTH_REQUIRED_EVENT` (a persistent 401 from `authorizedFetch`, or a null Entra
 *     token on a WebSocket connect attempt from `useRealtime`, item B2) means whatever token the
 *     app was using no longer works at all -- this drops back to a sign-in state rather than
 *     leave the app rendered with a token that will never succeed.
 *   - item B4: a failed `handleRedirectPromise()` (cancelled, consent required, or AADSTS50105 --
 *     see `../redirectError.ts`) is read once on mount and shown as a friendly error with a
 *     manual "Sign in" button, with the auto-redirect suppressed while it's present -- otherwise a
 *     reload would just repeat the same failing redirect forever.
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
    void instance.loginRedirect(loginRequest);
  }, [instance]);

  useEffect(() => {
    const onForbidden = () => setForbidden(true);
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    return () => window.removeEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
  }, []);

  useEffect(() => {
    // Item B3: see the class doc comment above for why this exists and shares
    // `autoRedirectAttempted` with the initial-mount auto-redirect effect below.
    const onAuthRequired = () => {
      setForbidden(false);
      setAuthRequired(true);
      if (!autoRedirectAttempted.current) {
        autoRedirectAttempted.current = true;
        startLoginRedirect();
      }
    };
    window.addEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);
    return () => window.removeEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);
  }, [startLoginRedirect]);

  const signIn = useCallback(() => {
    clearExplicitSignOut();
    clearRedirectError();
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
    void instance.logoutRedirect();
  }, [instance]);

  // Item S1: a 403 means the ID/access token's `roles` claim is stale relative to Brian having
  // just assigned the DriveThru.User role -- Entra bakes the roles claim in at issuance, so only a
  // genuinely NEW token (not merely clearing the local `forbidden` flag, which just re-renders
  // with whatever token is already cached) can actually pick up a newly-granted role.
  const retryForbidden = useCallback(() => {
    void acquireApiToken({ forceRefresh: true }).finally(() => setForbidden(false));
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

