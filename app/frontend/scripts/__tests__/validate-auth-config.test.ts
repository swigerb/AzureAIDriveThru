import { describe, it, expect } from 'vitest';
import { validateAuthConfig, validateEntraIds } from '../validate-auth-config.mjs';

const VALID_TENANT = '11111111-1111-1111-1111-111111111111';
const VALID_CLIENT = '33333333-3333-3333-3333-333333333333';

describe('validate-auth-config -- validateAuthConfig', () => {
  it('passes when no auth mode is pinned (Development pass-through; build stays green)', () => {
    expect(validateAuthConfig({}).ok).toBe(true);
    expect(validateAuthConfig({ VITE_AUTH_MODE: '' }).ok).toBe(true);
    expect(validateAuthConfig({ VITE_AUTH_MODE: '   ' }).ok).toBe(true);
  });

  it('passes an explicit Development build unconditionally (case-insensitive)', () => {
    expect(validateAuthConfig({ VITE_AUTH_MODE: 'Development' }).ok).toBe(true);
    expect(validateAuthConfig({ VITE_AUTH_MODE: 'DEVELOPMENT' }).ok).toBe(true);
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
  it('accepts GUID tenant + GUID client and a directory-domain tenant', () => {
    expect(validateEntraIds(VALID_TENANT, VALID_CLIENT).ok).toBe(true);
    expect(validateEntraIds('contoso.onmicrosoft.com', VALID_CLIENT).ok).toBe(true);
  });

  it('rejects empty, placeholder, all-zero, and malformed ids', () => {
    expect(validateEntraIds('', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, '').ok).toBe(false);
    expect(validateEntraIds('00000000-0000-0000-0000-000000000000', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, 'not-a-guid').ok).toBe(false);
    expect(validateEntraIds('tenant', VALID_CLIENT).ok).toBe(false);
  });
});
