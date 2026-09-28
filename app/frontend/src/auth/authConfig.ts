import type { Configuration, RedirectRequest } from '@azure/msal-browser';
import { LogLevel } from '@azure/msal-browser';

/**
 * Single-tenant Microsoft Entra SPA configuration (ADR-002, design doc §18.6, issue GH-145).
 *
 * The SPA uses the OAuth authorization-code + PKCE flow with NO client secret. Tenant id, client
 * id, and the delegated API scope are build-time CONFIGURATION (not secrets) injected as
 * `VITE_ENTRA_*` values at build time. Left blank in Development so the app runs as an
 * unauthenticated pass-through shell without contacting Entra -- the backend enforces Production
 * separately (see `Verify-ProductionAuth.ps1`, issue GH-146); this frontend gate only covers
 * Development pass-through vs. an explicit Entra build.
 *
 * Trimmed from Retail Pulse's `src/auth/authConfig.ts`: single provider only (Entra), so there is
 * no provider-neutral dispatcher, and no `VITE_ENTRA_AUDIENCE`/`VITE_ENTRA_INSTANCE` overrides --
 * the audience is always the App ID URI form `api://{clientId}` and the instance is always the
 * public Microsoft identity platform, per design §18.6.
 */
export interface RawAuthEnv {
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  readonly VITE_ENTRA_API_SCOPE?: string;
}

export interface ResolvedAuthConfig {
  /** True only when a tenant and client id are both present -- gates all MSAL usage. */
  readonly isConfigured: boolean;
  readonly tenantId: string;
  readonly clientId: string;
  /** Fully-qualified delegated scope(s) the SPA requests, e.g. api://{clientId}/access_as_user. */
  readonly apiScopes: string[];
  readonly msalConfig: Configuration;
  readonly loginRequest: RedirectRequest;
}

const INSTANCE = 'https://login.microsoftonline.com';
const DEFAULT_API_SCOPE = 'access_as_user';

/**
 * Pure resolver so the configuration is unit-testable with arbitrary env inputs. `origin` is
 * injected (defaults to the browser origin) for the redirect URIs -- required for the two-hostname
 * backend switch (design §18.7): each hostname's SPA registers its own current origin as the
 * redirect URI, with no cross-origin coupling.
 */
export function buildAuthConfig(
  env: RawAuthEnv,
  origin: string = typeof window !== 'undefined' ? window.location.origin : '',
): ResolvedAuthConfig {
  const tenantId = (env.VITE_ENTRA_TENANT_ID ?? '').trim();
  const clientId = (env.VITE_ENTRA_CLIENT_ID ?? '').trim();
  const apiScope = (env.VITE_ENTRA_API_SCOPE ?? '').trim() || DEFAULT_API_SCOPE;

  const isConfigured = Boolean(tenantId && clientId);
  const apiScopes = isConfigured ? [`api://${clientId}/${apiScope}`] : [];

  const msalConfig: Configuration = {
    auth: {
      clientId,
      authority: `${INSTANCE}/${tenantId}`,
      redirectUri: origin,
      postLogoutRedirectUri: origin,
      // Restores the pre-sign-in URL (e.g. ?persona=&model=) after the redirect round-trip.
      navigateToLoginRequestUrl: true,
    },
    cache: {
      // sessionStorage keeps tokens out of long-lived localStorage; cleared on tab close.
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        // Never log PII; keep noise at Error level.
        piiLoggingEnabled: false,
        logLevel: LogLevel.Error,
        loggerCallback: () => {},
      },
    },
  };

  return {
    isConfigured,
    tenantId,
    clientId,
    apiScopes,
    msalConfig,
    loginRequest: { scopes: apiScopes },
  };
}

export const authConfig: ResolvedAuthConfig = buildAuthConfig(import.meta.env as unknown as RawAuthEnv);

/** A canonical GUID (accepts any case); rejects the all-zero GUID as a placeholder. */
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/** A single-tenant directory: a real GUID, or a verified domain (e.g. fabrikam.onmicrosoft.com). */
const TENANT_DOMAIN_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/i;

/**
 * True when a configuration value is obviously a placeholder rather than a real id: empty, an
 * angle-bracket template (`<your-tenant-id>`), the all-zero GUID, or a well-known scaffold token.
 * Live Entra must never boot on any of these.
 */
function isPlaceholder(value: string): boolean {
  const v = value.trim();
  if (v === '' || v === EMPTY_GUID) return true;
  if (/[<>]/.test(v) || /\s/.test(v)) return true;
  return /(your[-_]?|placeholder|changeme|example|todo|xxxx+|\bfixme\b)/i.test(v);
}

export interface EntraConfigValidation {
  readonly ok: boolean;
  readonly error?: string;
}

/**
 * Pure validator (unit-testable) proving that an explicit Entra deployment carries a NON-EMPTY,
 * VALID single-tenant tenant id and client id. A placeholder, empty, or malformed id fails -- so a
 * live Entra build can never silently fall back to an unauthenticated shell.
 */
export function validateEntraConfig(tenantId: string, clientId: string): EntraConfigValidation {
  const tenant = (tenantId ?? '').trim();
  const client = (clientId ?? '').trim();

  if (isPlaceholder(tenant)) {
    return { ok: false, error: 'Entra tenant id is missing or a placeholder.' };
  }
  if (!(GUID_RE.test(tenant) || TENANT_DOMAIN_RE.test(tenant))) {
    return { ok: false, error: 'Entra tenant id is not a valid GUID or directory domain.' };
  }
  if (isPlaceholder(client)) {
    return { ok: false, error: 'Entra client id is missing or a placeholder.' };
  }
  if (!GUID_RE.test(client)) {
    return { ok: false, error: 'Entra client id is not a valid GUID.' };
  }
  return { ok: true };
}

/**
 * Fail-closed guard for the live Entra path. Throws a deterministic configuration error when the
 * tenant/client configuration is present but invalid (placeholder or malformed) -- BEFORE any MSAL
 * initialization, API, or WebSocket call. The bootstrap in `src/index.tsx` catches this and renders
 * a safe, dependency-light configuration-error screen instead of the app shell.
 *
 * Note: an UNCONFIGURED app (both ids blank) does not throw here -- that is the intentional
 * Development pass-through, distinct from a misconfigured Entra deployment.
 */
export function assertEntraConfigured(config: ResolvedAuthConfig = authConfig): void {
  if (!config.tenantId && !config.clientId) return;
  const { ok, error } = validateEntraConfig(config.tenantId, config.clientId);
  if (!ok) {
    throw new Error(
      `Entra authentication configuration is invalid: ${error} ` +
        'Set non-empty, valid VITE_ENTRA_TENANT_ID and VITE_ENTRA_CLIENT_ID for this deployment, ' +
        'or leave both blank for the Development pass-through. ' +
        'Refusing to start to avoid a silent, unauthenticated shell.',
    );
  }
}
