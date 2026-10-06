import { describe, it, expect, vi, beforeEach } from 'vitest';
import { InteractionRequiredAuthError } from '@azure/msal-browser';

const acquireTokenSilent = vi.fn();
const getActiveAccount = vi.fn();
const getAllAccounts = vi.fn();

vi.mock('../msalInstance', () => ({
  getMsalInstance: () => ({
    acquireTokenSilent: (...args: unknown[]) => acquireTokenSilent(...args),
    getActiveAccount: (...args: unknown[]) => getActiveAccount(...args),
    getAllAccounts: (...args: unknown[]) => getAllAccounts(...args),
  }),
}));

let isConfigured = true;
vi.mock('../authConfig', () => ({
  get authConfig() {
    return { isConfigured, apiScopes: ['api://client/access_as_user'] };
  },
}));

import { acquireApiToken } from '../tokenService';

const ACCOUNT = { homeAccountId: 'acct-1' };

beforeEach(() => {
  isConfigured = true;
  acquireTokenSilent.mockReset();
  getActiveAccount.mockReset().mockReturnValue(ACCOUNT);
  getAllAccounts.mockReset().mockReturnValue([]);
});

describe('acquireApiToken', () => {
  it('returns null when Entra is unconfigured (Development pass-through) without touching MSAL', async () => {
    isConfigured = false;
    await expect(acquireApiToken()).resolves.toBeNull();
    expect(acquireTokenSilent).not.toHaveBeenCalled();
  });

  it('returns null when there is no active or cached account (not signed in)', async () => {
    getActiveAccount.mockReturnValue(null);
    getAllAccounts.mockReturnValue([]);
    await expect(acquireApiToken()).resolves.toBeNull();
    expect(acquireTokenSilent).not.toHaveBeenCalled();
  });

  it('falls back to the first cached account when there is no active account', async () => {
    getActiveAccount.mockReturnValue(null);
    getAllAccounts.mockReturnValue([ACCOUNT]);
    acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
    await expect(acquireApiToken()).resolves.toBe('tok');
  });

  it('returns the access token on success, requesting the configured api scopes', async () => {
    acquireTokenSilent.mockResolvedValue({ accessToken: 'tok-abc' });
    await expect(acquireApiToken()).resolves.toBe('tok-abc');
    expect(acquireTokenSilent).toHaveBeenCalledWith({
      scopes: ['api://client/access_as_user'],
      account: ACCOUNT,
      forceRefresh: false,
    });
  });

  it('passes forceRefresh through to MSAL when explicitly requested (used on a 401 retry)', async () => {
    acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
    await acquireApiToken({ forceRefresh: true });
    expect(acquireTokenSilent).toHaveBeenCalledWith(expect.objectContaining({ forceRefresh: true }));
  });

  it('returns null (never throws) on InteractionRequiredAuthError -- AuthGate owns sign-in, not a fetch', async () => {
    acquireTokenSilent.mockRejectedValue(new InteractionRequiredAuthError('interaction_required', 'test-correlation-id'));
    await expect(acquireApiToken()).resolves.toBeNull();
  });

  it('re-throws an unexpected MSAL error', async () => {
    acquireTokenSilent.mockRejectedValue(new Error('network down'));
    await expect(acquireApiToken()).rejects.toThrow('network down');
  });
});
