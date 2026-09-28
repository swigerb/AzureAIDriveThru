import { describe, it, expect } from 'vitest';
import {
  AUTH_MODE_META_NAME,
  normalizeAuthMode,
  parseAuthModeMeta,
  isProductionEntra,
  renderAuthModeMetaTag,
} from '../auth-mode-meta.mjs';

describe('normalizeAuthMode', () => {
  it('normalizes an explicit entra (any case) to Entra', () => {
    expect(normalizeAuthMode('entra')).toBe('Entra');
    expect(normalizeAuthMode('Entra')).toBe('Entra');
    expect(normalizeAuthMode('ENTRA')).toBe('Entra');
    expect(normalizeAuthMode('  entra  ')).toBe('Entra');
  });

  it('normalizes unset, blank, Development, and any unrecognized value to Development', () => {
    expect(normalizeAuthMode(undefined)).toBe('Development');
    expect(normalizeAuthMode(null)).toBe('Development');
    expect(normalizeAuthMode('')).toBe('Development');
    expect(normalizeAuthMode('development')).toBe('Development');
    expect(normalizeAuthMode('okta')).toBe('Development');
  });
});

describe('renderAuthModeMetaTag / parseAuthModeMeta round-trip', () => {
  it('renders and parses the Entra marker', () => {
    const tag = renderAuthModeMetaTag('entra');
    expect(tag).toContain(`name="${AUTH_MODE_META_NAME}"`);
    expect(tag).toContain('content="Entra"');
    expect(parseAuthModeMeta(`<head>${tag}</head>`)).toBe('Entra');
  });

  it('renders and parses the Development marker', () => {
    const tag = renderAuthModeMetaTag(undefined);
    expect(parseAuthModeMeta(`<head>${tag}</head>`)).toBe('Development');
  });

  it('returns null when there is no such meta tag', () => {
    expect(parseAuthModeMeta('<head><meta name="other" content="x" /></head>')).toBeNull();
    expect(parseAuthModeMeta('')).toBeNull();
    expect(parseAuthModeMeta(undefined)).toBeNull();
  });

  it('is tolerant of attribute order and single quotes', () => {
    const html = `<meta content='Entra' name='${AUTH_MODE_META_NAME}'>`;
    expect(parseAuthModeMeta(html)).toBe('Entra');
  });
});

describe('isProductionEntra', () => {
  it('is true only for an exact, case-insensitive Entra marker', () => {
    expect(isProductionEntra('Entra')).toBe(true);
    expect(isProductionEntra('entra')).toBe(true);
    expect(isProductionEntra('Development')).toBe(false);
    expect(isProductionEntra(null)).toBe(false);
    expect(isProductionEntra(undefined)).toBe(false);
    expect(isProductionEntra('')).toBe(false);
  });
});
