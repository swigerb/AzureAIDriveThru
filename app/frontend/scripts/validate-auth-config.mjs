// @ts-check
import { loadEnv } from 'vite';

/**
 * Build-time fail-closed guard for the Entra SPA config (ADR-002 / design 18.6, issue GH-145).
 * Ported from Retail Pulse's `scripts/validate-auth-config.mjs`, trimmed to our two-mode
 * contract (`Entra` / `Development` -- no GitHub/Anonymous). Runs as the npm `prebuild` step so
 * `npm run build` FAILS FAST on an invalid configuration, before `vite.config.ts`'s own build-time
 * guard plugin (`src/auth/authMode.ts`'s `resolveAuthMode`, the SAME rules) would otherwise be the
 * only thing to catch it.
 *
 * PR #148 review round 2, item B1 (Rick): this file's rules previously passed UNCONDITIONALLY
 * whenever `VITE_AUTH_MODE` was unset -- with no awareness that `npm run build` always produces a
 * production-style bundle -- so an unconfigured production build silently shipped the
 * unauthenticated Development pass-through. `validateAuthConfig` below now mirrors
 * `src/auth/authMode.ts`'s `resolveAuthMode` rules exactly (see that file's doc comment for the
 * full rationale); `scripts/__tests__/auth-mode-build-matrix.test.ts` runs the SAME fixture table
 * through both implementations to guarantee they can never silently drift apart -- this file
 * cannot literally `import` the `.ts` resolver (this script runs via plain `node`, not through
 * Vite/tsc, and CI pins Node 22 which does not strip TypeScript types by default), so this is a
 * deliberate, tested mirror, the same tradeoff Retail Pulse's own guard script makes.
 *
 * Also reads `.env`/`.env.production` files via Vite's own `loadEnv` (not just `process.env`), so a
 * value set only in a `.env` file (as `scripts/docker-build.sh`/`deploy.sh` write one) is seen by
 * this guard exactly like the runtime bundle sees it via `import.meta.env` -- this script always
 * represents the upcoming `vite build` (never `vite dev`), which never passes an explicit `--mode`
 * flag, so `production` is the correct mode to load env files for.
 *
 * Contract (mirrors `src/auth/authMode.ts`'s `resolveAuthMode`):
 *   - VITE_AUTH_MODE=Entra: requires non-empty, VALID tenant + client ids, else FAIL.
 *   - VITE_AUTH_MODE=Development: requires BOTH ids to be blank, else FAIL.
 *   - VITE_AUTH_MODE unset, both ids present + valid: PASS as Entra (backwards compatible).
 *   - VITE_AUTH_MODE unset, both ids blank: FAIL -- `npm run build` is always a production-style
 *     build; an unset mode with no ids must be an explicit `VITE_AUTH_MODE=Development` choice, not
 *     a silent default.
 *   - A partial id (exactly one of tenant/client) or a placeholder id: FAIL, in every mode.
 *   - Any other VITE_AUTH_MODE value: FAIL.
 */

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/** @param {string} value */
function isPlaceholder(value) {
  const v = (value ?? '').trim();
  if (v === '' || v === EMPTY_GUID) return true;
  if (/[<>]/.test(v) || /\s/.test(v)) return true;
  return /(your[-_]?|placeholder|changeme|example|todo|xxxx+|\bfixme\b)/i.test(v);
}

/**
 * @param {string} tenantId
 * @param {string} clientId
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateEntraIds(tenantId, clientId) {
  const tenant = (tenantId ?? '').trim();
  const client = (clientId ?? '').trim();
  if (isPlaceholder(tenant)) return { ok: false, error: 'Entra tenant id is missing or a placeholder.' };
  if (!GUID_RE.test(tenant)) {
    return { ok: false, error: 'Entra tenant id is not a valid GUID.' };
  }
  if (isPlaceholder(client)) return { ok: false, error: 'Entra client id is missing or a placeholder.' };
  if (!GUID_RE.test(client)) return { ok: false, error: 'Entra client id is not a valid GUID.' };
  return { ok: true };
}

/**
 * Pure validator over an env-like record so it is unit-testable with arbitrary inputs. Mirrors
 * `src/auth/authMode.ts`'s `resolveAuthMode`, but returns `{ ok, error }` instead of throwing (this
 * is a CLI guard, not a component/bootstrap call site) and never accepts a `DEV` escape hatch --
 * this script only ever gates `npm run build`, which is always a production-style build.
 * @param {Record<string, string | undefined>} env
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateAuthConfig(env) {
  const rawMode = (env.VITE_AUTH_MODE ?? '').trim();
  const tenantId = (env.VITE_ENTRA_TENANT_ID ?? '').trim();
  const clientId = (env.VITE_ENTRA_CLIENT_ID ?? '').trim();
  const idsBlank = tenantId === '' && clientId === '';

  // A partial or placeholder id is an error in EVERY mode.
  if (!idsBlank) {
    const validation = validateEntraIds(tenantId, clientId);
    if (!validation.ok) {
      return {
        ok: false,
        error:
          `Entra authentication configuration is invalid: ${validation.error} ` +
          'Set non-empty, valid VITE_ENTRA_TENANT_ID and VITE_ENTRA_CLIENT_ID for an Entra ' +
          'deployment, or leave both blank for the Development pass-through. Refusing to build ' +
          'to avoid a silent, unauthenticated shell.',
      };
    }
  }

  if (rawMode === '') {
    if (!idsBlank) return { ok: true }; // Both ids present and already validated -- Entra, backcompat.
    return {
      ok: false,
      error:
        'VITE_AUTH_MODE is not set and no Entra ids are configured. `npm run build` always ' +
        'produces a production-style bundle, so it must set VITE_AUTH_MODE=Entra with valid ' +
        'VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID, or explicitly set VITE_AUTH_MODE=Development ' +
        'for an intentional unauthenticated pass-through build (e.g. local dev or Playwright ' +
        'builds). Refusing to silently build an unauthenticated pass-through.',
    };
  }

  const normalized = rawMode.toLowerCase();
  if (normalized === 'entra') {
    if (idsBlank) {
      return {
        ok: false,
        error:
          'VITE_AUTH_MODE=Entra requires non-empty, valid VITE_ENTRA_TENANT_ID and ' +
          'VITE_ENTRA_CLIENT_ID.',
      };
    }
    return { ok: true };
  }
  if (normalized === 'development') {
    if (!idsBlank) {
      return {
        ok: false,
        error:
          'VITE_AUTH_MODE=Development must not be combined with Entra ids ' +
          '(VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID). Remove them for a real pass-through ' +
          'build, or set VITE_AUTH_MODE=Entra to use them.',
      };
    }
    return { ok: true };
  }

  return {
    ok: false,
    error: `VITE_AUTH_MODE="${env.VITE_AUTH_MODE}" is not a recognized authentication mode (one of: Entra, Development).`,
  };
}

/**
 * Loads the env this guard should validate: `.env`/`.env.production` files (via Vite's own
 * `loadEnv`, honouring the same `VITE_` prefix and file precedence the runtime bundle uses) merged
 * with `process.env` (CI/CD sets `VITE_AUTH_MODE` and the ids as real environment variables, not
 * `.env` files -- `loadEnv` already gives `process.env` priority over file values for matching
 * keys).
 * @param {string} envDir
 * @returns {Record<string, string | undefined>}
 */
export function loadGuardEnv(envDir = process.cwd()) {
  // `npm run build` never passes an explicit `--mode`, so `vite build` resolves to Vite's own
  // default mode, `production` -- loading `.env.production`/`.env.production.local` in addition to
  // the mode-agnostic `.env`/`.env.local`, exactly like the real build will.
  return loadEnv('production', envDir, 'VITE_');
}

// CLI entry point: used as the npm `prebuild` guard. Exits non-zero (fails the build) on invalid config.
const invokedDirectly =
  typeof process !== 'undefined' &&
  Array.isArray(process.argv) &&
  process.argv[1] &&
  import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href;

if (invokedDirectly) {
  const env = loadGuardEnv();
  const result = validateAuthConfig(env);
  if (!result.ok) {
    console.error(`\n[validate-auth-config] BUILD BLOCKED: ${result.error}\n`);
    process.exit(1);
  }
  const mode = (env.VITE_AUTH_MODE ?? '').trim() || '(unset)';
  console.log(`[validate-auth-config] OK -- auth mode: ${mode}`);
}

