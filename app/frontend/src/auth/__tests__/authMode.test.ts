import { describe, it, expect } from 'vitest';
import { resolveAuthMode, validateEntraIds, type RawAuthModeEnv } from '../authMode';

const VALID_TENANT = '11111111-1111-1111-1111-111111111111';
const VALID_CLIENT = '33333333-3333-3333-3333-333333333333';
// R5 (PR GH-148 review round 2): the tenant id must be a GUID -- a directory domain like
// `contoso.onmicrosoft.com` is REJECTED, matching design 18.6's "non-GUID id fails" and 18.5's
// "the backend applies the same rule as the frontend".
const DOMAIN_TENANT = 'contoso.onmicrosoft.com';

function env(overrides: Partial<RawAuthModeEnv> = {}): RawAuthModeEnv {
  return { DEV: false, ...overrides };
}

describe('authMode -- resolveAuthMode', () => {
  describe('explicit VITE_AUTH_MODE=Entra', () => {
    it('resolves to entra with valid GUID ids', () => {
      expect(
        resolveAuthMode(
          env({ VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT }),
        ),
      ).toEqual({ mode: 'entra' });
    });

    it('throws on a directory-domain tenant (R5: tenant id must be a GUID, case-insensitive mode)', () => {
      expect(() =>
        resolveAuthMode(
          env({ VITE_AUTH_MODE: 'ENTRA', VITE_ENTRA_TENANT_ID: DOMAIN_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT }),
        ),
      ).toThrow(/not a valid GUID/i);
    });

    it('throws when both ids are blank', () => {
      expect(() => resolveAuthMode(env({ VITE_AUTH_MODE: 'Entra' }))).toThrow(/requires non-empty, valid/i);
    });

    it('throws on a partial id (tenant only)', () => {
      expect(() =>
        resolveAuthMode(env({ VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: VALID_TENANT })),
      ).toThrow(/client/i);
    });

    it('throws on a partial id (client only)', () => {
      expect(() =>
        resolveAuthMode(env({ VITE_AUTH_MODE: 'Entra', VITE_ENTRA_CLIENT_ID: VALID_CLIENT })),
      ).toThrow(/tenant/i);
    });

    it('throws on a placeholder id', () => {
      expect(() =>
        resolveAuthMode(
          env({
            VITE_AUTH_MODE: 'Entra',
            VITE_ENTRA_TENANT_ID: '<your-tenant-id>',
            VITE_ENTRA_CLIENT_ID: VALID_CLIENT,
          }),
        ),
      ).toThrow(/placeholder/i);
    });
  });

  describe('explicit VITE_AUTH_MODE=Development', () => {
    it('resolves to development with no ids set (case-insensitive)', () => {
      expect(resolveAuthMode(env({ VITE_AUTH_MODE: 'Development' }))).toEqual({ mode: 'development' });
      expect(resolveAuthMode(env({ VITE_AUTH_MODE: 'DEVELOPMENT' }))).toEqual({ mode: 'development' });
    });

    it('throws when combined with any Entra id, even valid ones', () => {
      expect(() =>
        resolveAuthMode(
          env({ VITE_AUTH_MODE: 'Development', VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT }),
        ),
      ).toThrow(/must not be combined/i);
    });

    it('throws when combined with a partial id', () => {
      expect(() =>
        resolveAuthMode(env({ VITE_AUTH_MODE: 'Development', VITE_ENTRA_TENANT_ID: VALID_TENANT })),
      ).toThrow(/client/i);
    });
  });

  describe('VITE_AUTH_MODE unset', () => {
    it('resolves to entra when both ids are present and valid (backwards compatible)', () => {
      expect(
        resolveAuthMode(env({ VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT })),
      ).toEqual({ mode: 'entra' });
    });

    it('throws on a partial id even with mode unset', () => {
      expect(() => resolveAuthMode(env({ VITE_ENTRA_TENANT_ID: VALID_TENANT }))).toThrow(/client/i);
    });

    it('throws on a placeholder id even with mode unset', () => {
      expect(() =>
        resolveAuthMode(env({ VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: '<client>' })),
      ).toThrow(/placeholder/i);
    });

    it('resolves to development when no ids are set AND DEV is true (the Vite dev server)', () => {
      expect(resolveAuthMode(env({ DEV: true }))).toEqual({ mode: 'development' });
    });

    it('throws when no ids are set and DEV is false (every `vite build`, incl. an unconfigured local build) -- the crux of B1', () => {
      expect(() => resolveAuthMode(env({ DEV: false }))).toThrow(/production builds must set/i);
    });
  });

  it('throws on an unknown mode value', () => {
    expect(() => resolveAuthMode(env({ VITE_AUTH_MODE: 'okta' }))).toThrow(/Unknown VITE_AUTH_MODE/);
  });
});

describe('authMode -- validateEntraIds', () => {
  it('accepts a GUID tenant + GUID client', () => {
    expect(validateEntraIds(VALID_TENANT, VALID_CLIENT).ok).toBe(true);
  });

  it('rejects empty, placeholder, all-zero, malformed, and directory-domain tenant ids (R5)', () => {
    expect(validateEntraIds('', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, '').ok).toBe(false);
    expect(validateEntraIds('00000000-0000-0000-0000-000000000000', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(VALID_TENANT, 'not-a-guid').ok).toBe(false);
    expect(validateEntraIds('tenant', VALID_CLIENT).ok).toBe(false);
    expect(validateEntraIds(DOMAIN_TENANT, VALID_CLIENT).ok).toBe(false);
  });
});
