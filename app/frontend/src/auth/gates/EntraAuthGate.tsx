import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { loginRequest } from '../authConfig';
import { AUTH_FORBIDDEN_EVENT } from '../authorizedFetch';
import { markExplicitSignOut, consumeExplicitSignOut, clearExplicitSignOut } from '../signOut';

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
 * visitor clicks "Sign in with Microsoft" again.
 */
export function EntraAuthGate({ children }: { children: ReactNode }) {
  const { instance, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [forbidden, setForbidden] = useState(false);
  const [signedOut, setSignedOut] = useState(() => consumeExplicitSignOut());
  const autoRedirectAttempted = useRef(false);

  useEffect(() => {
    const onForbidden = () => setForbidden(true);
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    return () => window.removeEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
  }, []);

  const signIn = useCallback(() => {
    clearExplicitSignOut();
    setSignedOut(false);
    setForbidden(false);
    // Mark the auto-redirect effect as already handled so it never double-fires loginRedirect
    // once `signedOut` flips back to false and the effect's dependencies re-evaluate.
    autoRedirectAttempted.current = true;
    void instance.loginRedirect(loginRequest);
  }, [instance]);

  const signOut = useCallback(() => {
    markExplicitSignOut();
    void instance.logoutRedirect();
  }, [instance]);

  const busy =
    inProgress === InteractionStatus.Login ||
    inProgress === InteractionStatus.HandleRedirect ||
    inProgress === InteractionStatus.Startup;

  // A fresh (never explicitly signed-out, never forbidden) unauthenticated load starts
  // loginRedirect automatically -- silent via the Entra SSO cookie on the second hostname (design
  // §18.7). Fires at most once per mount; the ref (not state) prevents a re-trigger loop even if
  // MSAL's `inProgress`/`isAuthenticated` bounce during the redirect handshake.
  useEffect(() => {
    if (
      !isAuthenticated &&
      !forbidden &&
      !signedOut &&
      inProgress === InteractionStatus.None &&
      !autoRedirectAttempted.current
    ) {
      autoRedirectAttempted.current = true;
      void instance.loginRedirect(loginRequest);
    }
  }, [isAuthenticated, forbidden, signedOut, inProgress, instance]);

  if (isAuthenticated && !forbidden) {
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
          <CardDescription>Sign in to continue</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col items-center gap-4">
          {forbidden ? (
            <>
              <p data-testid="auth-forbidden" className="text-sm text-destructive">
                You're signed in, but you don't have access yet. Ask Brian to assign you the{' '}
                <strong>DriveThru.User</strong> role, then try again.
              </p>
              <div className="flex w-full flex-col gap-2">
                <Button data-testid="auth-retry-button" onClick={() => setForbidden(false)}>
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
          ) : signedOut ? (
            <>
              <p className="text-sm text-muted-foreground">
                Sign in with your organizational account to continue.
              </p>
              <Button data-testid="auth-signin-button" size="lg" onClick={signIn}>
                Sign in with Microsoft
              </Button>
            </>
          ) : busy ? (
            <p data-testid="auth-signing-in" className="text-sm text-muted-foreground">
              Signing you in…
            </p>
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
