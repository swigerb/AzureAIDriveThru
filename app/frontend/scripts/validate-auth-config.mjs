// @ts-check
/**
 * Build-time fail-closed guard for the Entra SPA config (ADR-002 / design 18.6, issue GH-145).
 * Ported from Retail Pulse's `scripts/validate-auth-config.mjs`, trimmed to our two-mode
 * contract (`Entra` / `Development` -- no GitHub/Anonymous). Runs as the npm `prebuild` step so
 * `npm run build` FAILS FAST when an explicit Entra build is missing/placeholder tenant/client ids.
 *
 * Contract (mirrors the backend's AUTH_MODE resolver, design 18.5, and authConfig.ts's
 * validateEntraConfig so the build guard and the runtime fail-closed guard agree):
 *   - VITE_AUTH_MODE unset/blank   -> PASS (Development pass-through; no ids required).
 *   - VITE_AUTH_MODE = Entra       -> require VALID, non-placeholder tenant + client ids, else FAIL.
 *   - VITE_AUTH_MODE = Development -> PASS (ids never required).
 *   - VITE_AUTH_MODE = anything else -> FAIL (an unknown selector can never boot).
 */

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
const TENANT_DOMAIN_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/i;
const KNOWN_MODES = ['entra', 'development'];

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
  if (!(GUID_RE.test(tenant) || TENANT_DOMAIN_RE.test(tenant))) {
    return { ok: false, error: 'Entra tenant id is not a valid GUID or directory domain.' };
  }
  if (isPlaceholder(client)) return { ok: false, error: 'Entra client id is missing or a placeholder.' };
  if (!GUID_RE.test(client)) return { ok: false, error: 'Entra client id is not a valid GUID.' };
  return { ok: true };
}

/**
 * Pure validator over an env-like record so it is unit-testable with arbitrary inputs.
 * @param {Record<string, string | undefined>} env
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateAuthConfig(env) {
  const mode = (env.VITE_AUTH_MODE ?? '').trim().toLowerCase();

  // No pinned mode: Development pass-through, nothing to validate.
  if (mode === '') return { ok: true };

  if (!KNOWN_MODES.includes(mode)) {
    return {
      ok: false,
      error:
        `VITE_AUTH_MODE="${env.VITE_AUTH_MODE}" is not a recognized authentication mode ` +
        `(one of: Entra, Development).`,
    };
  }

  // Only Entra requires the SPA-embedded tenant/client ids; Development never does.
  if (mode === 'entra') {
    const result = validateEntraIds(env.VITE_ENTRA_TENANT_ID ?? '', env.VITE_ENTRA_CLIENT_ID ?? '');
    if (!result.ok) {
      return {
        ok: false,
        error:
          `VITE_AUTH_MODE=Entra but ${result.error} ` +
          'Set non-empty, valid VITE_ENTRA_TENANT_ID and VITE_ENTRA_CLIENT_ID before building. ' +
          'Refusing to build an Entra SPA with empty/placeholder configuration.',
      };
    }
  }

  return { ok: true };
}

// CLI entry point: used as the npm `prebuild` guard. Exits non-zero (fails the build) on invalid config.
const invokedDirectly =
  typeof process !== 'undefined' &&
  Array.isArray(process.argv) &&
  process.argv[1] &&
  import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href;

if (invokedDirectly) {
  const result = validateAuthConfig(/** @type {Record<string,string|undefined>} */ (process.env));
  if (!result.ok) {
    console.error(`\n[validate-auth-config] BUILD BLOCKED: ${result.error}\n`);
    process.exit(1);
  }
  const mode = (process.env.VITE_AUTH_MODE ?? '').trim() || '(unset -- Development pass-through)';
  console.log(`[validate-auth-config] OK -- auth mode: ${mode}`);
}
