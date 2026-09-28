// @vitest-environment node
//
// This suite's target imports `vite`'s `loadEnv` (for `loadGuardEnv`), which pulls in esbuild.
// Under the repo's default jsdom test environment, esbuild's Node-only bootstrap collides with
// jsdom's polyfilled globals (`TextEncoder`) and throws an "environment is broken" invariant error
// before any test runs. This CLI guard script only ever executes under plain Node in real life
// (`node scripts/validate-auth-config.mjs`), so testing it under Node here is also the more
// faithful environment, not just a workaround.
import { describe, it, expect, afterEach } from 'vitest';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { validateAuthConfig, validateEntraIds, loadGuardEnv } from '../validate-auth-config.mjs';

const VALID_TENANT = '11111111-1111-1111-1111-111111111111';
const VALID_CLIENT = '33333333-3333-3333-3333-333333333333';

describe('validate-auth-config -- validateAuthConfig', () => {
  it('fails when no auth mode is pinned and no ids are set (this guard only ever gates `npm run build`, a production-style bundle)', () => {
    expect(validateAuthConfig({}).ok).toBe(false);
    expect(validateAuthConfig({ VITE_AUTH_MODE: '' }).ok).toBe(false);
    expect(validateAuthConfig({ VITE_AUTH_MODE: '   ' }).ok).toBe(false);
  });

  it('passes an unset mode with both valid ids present (backwards compatible with pre-VITE_AUTH_MODE builds)', () => {
    expect(
      validateAuthConfig({ VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT }).ok,
    ).toBe(true);
  });

  it('passes an explicit Development build with no ids set (case-insensitive)', () => {
    expect(validateAuthConfig({ VITE_AUTH_MODE: 'Development' }).ok).toBe(true);
    expect(validateAuthConfig({ VITE_AUTH_MODE: 'DEVELOPMENT' }).ok).toBe(true);
  });

  it('fails an explicit Development build that also carries Entra ids', () => {
    const result = validateAuthConfig({
      VITE_AUTH_MODE: 'Development',
      VITE_ENTRA_TENANT_ID: VALID_TENANT,
      VITE_ENTRA_CLIENT_ID: VALID_CLIENT,
    });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/must not be combined/i);
  });

  it('fails an explicit Entra build with missing tenant/client ids (partially set)', () => {
    const result = validateAuthConfig({ VITE_AUTH_MODE: 'Entra' });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/tenant/i);
  });

  it('fails an explicit Entra build with only the tenant id set (partially set)', () => {
    const result = validateAuthConfig({ VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: VALID_TENANT });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/client/i);
  });

  it('fails an unset mode with only the tenant id set (partially set, no mode to fall back on)', () => {
    const result = validateAuthConfig({ VITE_ENTRA_TENANT_ID: VALID_TENANT });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/client/i);
  });

  it('fails an explicit Entra build with placeholder ids', () => {
    const result = validateAuthConfig({
      VITE_AUTH_MODE: 'entra',
      VITE_ENTRA_TENANT_ID: '<your-tenant-id>',
      VITE_ENTRA_CLIENT_ID: '<your-client-id>',
    });
    expect(result.ok).toBe(false);
  });

  it('passes an explicit Entra build with valid ids (case-insensitive mode)', () => {
    expect(
      validateAuthConfig({
        VITE_AUTH_MODE: 'ENTRA',
        VITE_ENTRA_TENANT_ID: VALID_TENANT,
        VITE_ENTRA_CLIENT_ID: VALID_CLIENT,
      }).ok,
    ).toBe(true);
  });

  it('fails an unknown auth mode deterministically (no github/anonymous in this app)', () => {
    for (const mode of ['okta', 'github', 'anonymous']) {
      const result = validateAuthConfig({ VITE_AUTH_MODE: mode });
      expect(result.ok).toBe(false);
      expect(result.error).toMatch(/not a recognized authentication mode/i);
    }
  });
});

describe('validate-auth-config -- validateEntraIds', () => {
  it('accepts a GUID tenant + GUID client', () => {
    expect(validateEntraIds(VALID_TENANT, VALID_CLIENT).ok).toBe(true);
  });

  it('rejects a directory-domain tenant (R5: tenant id must be a GUID)', () => {
    expect(validateEntraIds('contoso.onmicrosoft.com', VALID_CLIENT).ok).toBe(false);
  });

  it('rejects empty, placeholder, all-zero, and malformed ids', () => {
    expect(validateEntraIds('', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, '').ok).toBe(false);
    expect(validateEntraIds('00000000-0000-0000-0000-000000000000', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, 'not-a-guid').ok).toBe(false);
    expect(validateEntraIds('tenant', VALID_CLIENT).ok).toBe(false);
  });
});

describe('validate-auth-config -- loadGuardEnv (reads .env files, not just process.env)', () => {
  let fixtureDir: string | undefined;

  afterEach(() => {
    if (fixtureDir) rmSync(fixtureDir, { recursive: true, force: true });
    fixtureDir = undefined;
  });

  it('sees a VITE_ prefixed value set only in .env.production, exactly like the runtime bundle would via import.meta.env', () => {
    fixtureDir = mkdtempSync(join(process.cwd(), 'auth-config-fixture-'));
    writeFileSync(
      join(fixtureDir, '.env.production'),
      ['VITE_AUTH_MODE=Entra', `VITE_ENTRA_TENANT_ID=${VALID_TENANT}`, `VITE_ENTRA_CLIENT_ID=${VALID_CLIENT}`].join('\n'),
    );

    const env = loadGuardEnv(fixtureDir);
    expect(env.VITE_AUTH_MODE).toBe('Entra');
    expect(validateAuthConfig(env).ok).toBe(true);
  });

  it('lets a real process.env value override the same key from a .env file (CI/CD wins over checked-in files)', () => {
    fixtureDir = mkdtempSync(join(process.cwd(), 'auth-config-fixture-'));
    writeFileSync(join(fixtureDir, '.env.production'), 'VITE_AUTH_MODE=Entra\n');

    const previous = process.env.VITE_AUTH_MODE;
    process.env.VITE_AUTH_MODE = 'Development';
    try {
      const env = loadGuardEnv(fixtureDir);
      expect(env.VITE_AUTH_MODE).toBe('Development');
    } finally {
      if (previous === undefined) delete process.env.VITE_AUTH_MODE;
      else process.env.VITE_AUTH_MODE = previous;
    }
  });
});
