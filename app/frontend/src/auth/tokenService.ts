import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { authConfig } from './authConfig';
import { getMsalInstance } from './msalInstance';

/**
 * Single-provider (Entra) token acquisition for the SPA (ADR-002, design §18.6, issue #145).
 *
 * Every protected REST call (`authorizedFetch`) and the `/realtime` WebSocket connect obtain their
 * bearer token here, so there is exactly one place that talks to MSAL. Unlike Retail Pulse (which
 * dispatches across Entra/GitHub/Anonymous providers), issue #145 is Entra-only, so this module
 * calls MSAL directly with no provider indirection.
 */
export interface AcquireTokenOptions {
  /** Force a fresh token from Entra (used on a 401 retry, and before every WebSocket connect). */
  readonly forceRefresh?: boolean;
}

/**
 * Returns a bearer token for the API, or null when unconfigured (Development pass-through) or when
 * the user is not signed in. Interactive sign-in is owned by `AuthGate`, never by an individual
 * fetch or WebSocket connect -- so this throws only on an unexpected MSAL error, never on
 * `InteractionRequiredAuthError`, which resolves to `null` instead.
 */
export async function acquireApiToken(options: AcquireTokenOptions = {}): Promise<string | null> {
  if (!authConfig.isConfigured) {
    return null;
  }

  const msal = getMsalInstance();
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null;
  if (!account) {
    return null;
  }

  try {
    const result = await msal.acquireTokenSilent({
      scopes: authConfig.apiScopes,
      account,
      forceRefresh: options.forceRefresh ?? false,
    });
    return result.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      // AuthGate owns interactive sign-in; a fetch or WS connect never triggers it directly.
      return null;
    }
    throw error;
  }
}
