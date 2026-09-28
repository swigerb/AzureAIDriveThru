/**
 * Shared, pure auth-mode resolver (ADR-002, design §18.6, issue GH-145, PR GH-148 review round 2
 * item B1).
 *
 * This is the SINGLE source of truth for whether the SPA runs in `entra` mode (MSAL-backed sign-in
 * required) or `development` mode (unauthenticated pass-through), used both by runtime code
 * (`index.tsx`'s bootstrap, `AuthGate.tsx`) AND `vite.config.ts`'s build-time guard/marker plugin --
 * so the enforced mode and the meta-tag marker baked into `index.html` can never disagree, and the
 * exact same rules apply whether the guard runs as an npm `prebuild` script, inside the Vite build
 * itself, or inside the running app.
 *
 * Two-mode contract only (`Entra` / `Development`) -- unlike Retail Pulse's provider-neutral
 * dispatcher (Entra/GitHub/Anonymous), ADR-002 explicitly rejects porting that 3-provider model, so
 * there is no `resolveProvider`-style indirection here.
 *
 * Resolution rules (fail closed):
 *  - `VITE_AUTH_MODE=Entra` (case-insensitive): requires non-empty, valid tenant + client ids.
 *  - `VITE_AUTH_MODE=Development`: requires BOTH ids to be blank -- a Development build that also
 *    carries Entra ids is refused, since that combination almost certainly means the wrong env var
 *    leaked into the wrong build rather than an intentional pass-through.
 *  - `VITE_AUTH_MODE` unset, and both ids present + valid: resolves to `entra` (backwards
 *    compatible with builds from before this variable existed).
 *  - `VITE_AUTH_MODE` unset, and both ids blank: resolves to `development` ONLY when
 *    `import.meta.env.DEV` (i.e. the Vite dev server, `vite dev`/`vite serve`) -- every `vite
 *    build` (staging, production, or a plain unconfigured local build) has `DEV === false`, so an
 *    unset mode with no ids in any BUILT bundle fails closed instead of silently shipping an
 *    unauthenticated production app. This is the crux of B1: pass-through must be impossible in a
 *    production build.
 *  - ANY partial id (exactly one of tenant/client set) or placeholder id (blank, `<template>`,
 *    the all-zero GUID, `changeme`, etc.) is an error in EVERY mode -- there is no mode under which
 *    a half-configured or scaffold Entra id is silently accepted.
 *  - Any other `VITE_AUTH_MODE` value is an error.
 */
export type AuthMode = 'entra' | 'development';

export interface RawAuthModeEnv {
  readonly VITE_AUTH_MODE?: string;
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  /** `import.meta.env.DEV` -- true only for the Vite dev server, false for every `vite build`. */
  readonly DEV?: boolean;
}

export interface ResolvedAuthMode {
  readonly mode: AuthMode;
}

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

export interface EntraIdValidation {
  readonly ok: boolean;
  readonly error?: string;
}

/**
 * Pure validator (unit-testable) proving that a tenant id and client id are NON-EMPTY and VALID.
 * Canonical id-shape logic, shared by the mode resolver below and re-exported (via
 * `authConfig.ts`'s thin `validateEntraConfig` wrapper) for the existing `buildAuthConfig`/
 * `assertEntraConfigured` call sites.
 */
export function validateEntraIds(tenantId: string, clientId: string): EntraIdValidation {
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
 * Resolves the auth mode from a raw env snapshot. Pure and side-effect-free (no top-level throwing
 * singleton, unlike Retail Pulse's `authMode.ts`): a static import chain that reaches a throwing
 * module-eval-time singleton would NOT be catchable by `index.tsx`'s own bootstrap try/catch (the
 * throw happens during ES module linking/evaluation, before any function body -- including that
 * try/catch -- runs), so callers MUST invoke this function explicitly inside their own try/catch
 * (`index.tsx`'s `bootstrap()`) rather than relying on a module-level constant.
 *
 * Throws a plain `Error` with a deterministic, non-sensitive message (ids are build-time
 * configuration, not secrets, per `authConfig.ts`) on every invalid configuration.
 */
export function resolveAuthMode(env: RawAuthModeEnv): ResolvedAuthMode {
  const rawMode = (env.VITE_AUTH_MODE ?? '').trim();
  const tenantId = (env.VITE_ENTRA_TENANT_ID ?? '').trim();
  const clientId = (env.VITE_ENTRA_CLIENT_ID ?? '').trim();
  const idsBlank = tenantId === '' && clientId === '';

  // A partial or placeholder id is an error in EVERY mode -- checked once, up front, regardless of
  // which branch below is ultimately taken.
  if (!idsBlank) {
    const validation = validateEntraIds(tenantId, clientId);
    if (!validation.ok) {
      throw new Error(
        `Entra authentication configuration is invalid: ${validation.error} ` +
          'Set non-empty, valid VITE_ENTRA_TENANT_ID and VITE_ENTRA_CLIENT_ID for an Entra ' +
          'deployment, or leave both blank for the Development pass-through. Refusing to start ' +
          'to avoid a silent, unauthenticated shell.',
      );
    }
  }

  if (rawMode === '') {
    if (!idsBlank) {
      // Both ids present and already validated above -- Entra, for backwards compatibility with
      // builds that predate VITE_AUTH_MODE.
      return { mode: 'entra' };
    }
    if (env.DEV) {
      return { mode: 'development' };
    }
    throw new Error(
      'VITE_AUTH_MODE is not set and no Entra ids are configured. Production builds must set ' +
        'VITE_AUTH_MODE=Entra with valid VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID, or explicitly ' +
        'set VITE_AUTH_MODE=Development for an intentional unauthenticated pass-through build. ' +
        'Refusing to silently fall back to an unauthenticated pass-through in a production build.',
    );
  }

  const normalized = rawMode.toLowerCase();
  if (normalized === 'entra') {
    if (idsBlank) {
      throw new Error(
        'VITE_AUTH_MODE=Entra requires non-empty, valid VITE_ENTRA_TENANT_ID and ' +
          'VITE_ENTRA_CLIENT_ID.',
      );
    }
    return { mode: 'entra' };
  }
  if (normalized === 'development') {
    if (!idsBlank) {
      throw new Error(
        'VITE_AUTH_MODE=Development must not be combined with Entra ids ' +
          '(VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID). Remove them for a real pass-through ' +
          'build, or set VITE_AUTH_MODE=Entra to use them.',
      );
    }
    return { mode: 'development' };
  }

  throw new Error(`Unknown VITE_AUTH_MODE "${rawMode}". Expected "Entra" or "Development".`);
}

/**
 * Runtime convenience wrapper over `resolveAuthMode`, reading `import.meta.env` directly. Callers
 * (`index.tsx`, `AuthGate.tsx`) MUST call this inside their own try/catch or component render --
 * never assign its result to a module-level constant (see the top-level-throwing-singleton note
 * above).
 */
export function getResolvedAuthMode(): ResolvedAuthMode {
  return resolveAuthMode({
    VITE_AUTH_MODE: import.meta.env.VITE_AUTH_MODE as string | undefined,
    VITE_ENTRA_TENANT_ID: import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined,
    VITE_ENTRA_CLIENT_ID: import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined,
    DEV: import.meta.env.DEV,
  });
}
