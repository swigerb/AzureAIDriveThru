import { EventType, PublicClientApplication, type AuthenticationResult } from '@azure/msal-browser';
import { authConfig } from './authConfig';
import { setRedirectError, clearRedirectError } from './redirectError';

/**
 * Lazily-created MSAL PublicClientApplication singleton (ADR-002, design §18.6, issue GH-145).
 *
 * Construction is deferred so importing this module never throws when auth is unconfigured
 * (Development) -- MSAL requires a non-empty clientId. Only a configured Entra build calls
 * {@link getMsalInstance}/{@link initializeMsal}; the Development build renders straight to the
 * app shell as a transparent pass-through.
 */
let instance: PublicClientApplication | null = null;
let initialized: Promise<void> | null = null;

export function getMsalInstance(): PublicClientApplication {
  if (!authConfig.isConfigured) {
    throw new Error(
      'MSAL requested but Entra auth is not configured (VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID missing).',
    );
  }
  if (!instance) {
    instance = new PublicClientApplication(authConfig.msalConfig);
  }
  return instance;
}

/** Test-only: reset the lazily-created singleton so each test starts from a clean slate. */
export function resetMsalInstanceForTests(): void {
  instance = null;
  initialized = null;
}

/**
 * Initializes MSAL exactly once: completes any redirect sign-in, sets the active account, and
 * keeps it fresh on subsequent login/token events. Idempotent under React StrictMode.
 */
export function initializeMsal(): Promise<void> {
  if (initialized) {
    return initialized;
  }

  initialized = (async () => {
    const msal = getMsalInstance();
    await msal.initialize();

    // `navigateToLoginRequestUrl: true` restores the pre-sign-in URL (e.g. ?persona=&model=)
    // after the redirect round-trip -- this option moved from `Configuration.auth` to a
    // `handleRedirectPromise` call option in this MSAL major version.
    //
    // Item B4 (PR GH-148 review round 2, ADR-002 item 12, design §18.6/§18.12): a failed redirect
    // (the visitor cancelled, admin consent is required, or AADSTS50105 -- this user/tenant was
    // never assigned to the app) must NOT reject this promise. Before this fix it did: the
    // rejection propagated to `index.tsx`'s bootstrap() catch block, which rendered
    // `ConfigErrorScreen` (the wrong screen -- that's reserved for configuration failures, not a
    // sign-in outcome) with no retry path, and a page reload just re-ran the same failing
    // auto-redirect (a loop from the visitor's perspective). Catching it here keeps MSAL itself
    // usable (accounts/silent acquisition still work) and stashes the classified error for
    // `EntraAuthGate` to read once on mount instead, which shows a friendly message and a manual
    // "Sign in" button -- and suppresses its own auto-redirect while that error is present.
    let redirectResult: AuthenticationResult | null = null;
    try {
      redirectResult = await msal.handleRedirectPromise({ navigateToLoginRequestUrl: true });
      clearRedirectError();
    } catch (error) {
      setRedirectError(error);
    }

    if (redirectResult?.account) {
      msal.setActiveAccount(redirectResult.account);
    } else if (!msal.getActiveAccount()) {
      const [firstAccount] = msal.getAllAccounts();
      if (firstAccount) {
        msal.setActiveAccount(firstAccount);
      }
    }

    msal.addEventCallback((event) => {
      if (
        (event.eventType === EventType.LOGIN_SUCCESS || event.eventType === EventType.ACQUIRE_TOKEN_SUCCESS) &&
        event.payload
      ) {
        const payload = event.payload as AuthenticationResult;
        if (payload.account) {
          msal.setActiveAccount(payload.account);
        }
      }
    });
  })();

  return initialized;
}
