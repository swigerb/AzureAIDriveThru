import { describe, it, expect, vi, beforeEach } from 'vitest';

// The wrapper only installs when Entra is configured; force that in tests.
vi.mock('../authConfig', () => ({ authConfig: { isConfigured: true } }));

// Central token acquisition is mocked so we can drive silent-refresh / retry behavior.
const acquireApiToken = vi.fn();
vi.mock('../tokenService', () => ({
  acquireApiToken: (...args: unknown[]) => acquireApiToken(...args),
}));

type FetchModule = typeof import('../authorizedFetch');

async function freshModule(): Promise<FetchModule> {
  vi.resetModules(); // resets the module-level `installed` guard so each test wraps cleanly
  return import('../authorizedFetch');
}

function jsonResponse(status: number): Response {
  return new Response('{}', { status, headers: { 'content-type': 'application/json' } });
}

let originalFetch: ReturnType<typeof vi.fn>;

beforeEach(() => {
  acquireApiToken.mockReset();
  originalFetch = vi.fn().mockResolvedValue(jsonResponse(200));
  window.fetch = originalFetch as unknown as typeof window.fetch;
});

function authHeader(callIndex = 0): string | null {
  const init = originalFetch.mock.calls[callIndex]?.[1] as RequestInit | undefined;
  const headers = new Headers(init?.headers);
  return headers.get('Authorization');
}

describe('isAuthorizedFetchTarget', () => {
  it('matches relative /api paths', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/api/chat')).toBe(true);
    expect(isAuthorizedFetchTarget('/api')).toBe(true);
    expect(isAuthorizedFetchTarget('/api/auth/session')).toBe(true);
  });

  it('matches a persona menu.json', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/personas/sonic/menu.json')).toBe(true);
    expect(isAuthorizedFetchTarget(`${location.origin}/personas/sonic/menu.json`)).toBe(true);
  });

  it('matches demo/other JSON under a persona assets path', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/demo/dummyOrder.json')).toBe(true);
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/demo/dummyTranscripts.json')).toBe(true);
  });

  it('does NOT match public branding assets under a persona assets path', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/logo.svg')).toBe(false);
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/favicon.ico')).toBe(false);
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/audio/apology-en.wav')).toBe(false);
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/hero.png')).toBe(false);
    expect(isAuthorizedFetchTarget('/personas/sonic/assets/hero.PNG')).toBe(false);
  });

  it('does not match the SPA shell or other same-origin static assets', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/')).toBe(false);
    expect(isAuthorizedFetchTarget('/assets/app.js')).toBe(false);
    expect(isAuthorizedFetchTarget('/favicon.ico')).toBe(false);
  });

  it('does not match third-party/cross-origin URLs', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('https://cdn.example.com/lib.js')).toBe(false);
    expect(isAuthorizedFetchTarget('https://dc.services.visualstudio.com/v2/track')).toBe(false);
  });

  it('rejects userinfo credential smuggling (trusted@evil)', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget(`https://${location.hostname}@evil.example/api/chat`)).toBe(false);
  });

  it('rejects lookalike suffix/prefix hosts', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget(`https://${location.hostname}.evil.example/api/chat`)).toBe(false);
    expect(isAuthorizedFetchTarget(`https://evil${location.hostname}/api/chat`)).toBe(false);
  });

  it('rejects scheme and port mismatches for the same host', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget(`https://${location.host}/api/chat`)).toBe(false);
    expect(isAuthorizedFetchTarget(`${location.protocol}//${location.hostname}:5999/api/chat`)).toBe(false);
  });

  it('matches an absolute same-origin /api URL', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget(`${location.origin}/api/chat`)).toBe(true);
  });

  it('does not match encoded traversal or paths that escape /api', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget('/api/../secret')).toBe(false);
    expect(isAuthorizedFetchTarget('/apidocs/public')).toBe(false);
    expect(isAuthorizedFetchTarget('/%2e%2e/api/secret')).toBe(true);
  });

  it('accepts Request objects and URL objects', async () => {
    const { isAuthorizedFetchTarget } = await freshModule();
    expect(isAuthorizedFetchTarget(new Request(`${location.origin}/api/chat`))).toBe(true);
    expect(isAuthorizedFetchTarget(new URL('/api/chat', location.origin))).toBe(true);
    expect(isAuthorizedFetchTarget(new Request('https://cdn.example.com/lib.js'))).toBe(false);
  });
});

describe('installAuthorizedFetch', () => {
  it('attaches the bearer token to /api requests', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/api/chat', { method: 'POST' });

    expect(acquireApiToken).toHaveBeenCalledTimes(1);
    expect(authHeader()).toBe('Bearer tok-1');
  });

  it('attaches the bearer token to menu.json and demo JSON, never to branding assets', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-menu');
    installAuthorizedFetch();

    await window.fetch('/personas/sonic/menu.json');
    expect(authHeader(0)).toBe('Bearer tok-menu');

    await window.fetch('/personas/sonic/assets/demo/dummyOrder.json');
    expect(authHeader(1)).toBe('Bearer tok-menu');

    acquireApiToken.mockClear();
    await window.fetch('/personas/sonic/assets/logo.svg');
    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader(2)).toBeNull();
  });

  it('does not touch non-API requests or acquire a token for them', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('https://cdn.example.com/lib.js');

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader()).toBeNull();
  });

  it('does not attach the token to same-origin non-protected paths', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/assets/app.js');
    await window.fetch(`${location.origin}/index.html`);

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader(0)).toBeNull();
  });

  it('attaches the bearer to a Request object targeting /api and retries it on 401', async () => {
    const { installAuthorizedFetch } = await freshModule();
    originalFetch.mockResolvedValueOnce(jsonResponse(401)).mockResolvedValueOnce(jsonResponse(200));
    acquireApiToken.mockResolvedValueOnce('stale').mockResolvedValueOnce('fresh');
    installAuthorizedFetch();

    const res = await window.fetch(new Request(`${location.origin}/api/chat`, { method: 'POST' }));

    expect(res.status).toBe(200);
    expect(originalFetch).toHaveBeenCalledTimes(2);
    const first = originalFetch.mock.calls[0]?.[0] as Request;
    expect(first).toBeInstanceOf(Request);
    expect(first.headers.get('Authorization')).toBe('Bearer stale');
    const second = originalFetch.mock.calls[1]?.[0] as Request;
    expect(second).toBeInstanceOf(Request);
    expect(second.headers.get('Authorization')).toBe('Bearer fresh');
  });

  it('does not acquire a token for a userinfo lookalike host', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch(`https://${location.hostname}@evil.example/api/chat`);

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader()).toBeNull();
  });

  it('force-refreshes the token and retries once on a 401', async () => {
    const { installAuthorizedFetch } = await freshModule();
    originalFetch.mockResolvedValueOnce(jsonResponse(401)).mockResolvedValueOnce(jsonResponse(200));
    acquireApiToken.mockResolvedValueOnce('stale').mockResolvedValueOnce('fresh');
    installAuthorizedFetch();

    const res = await window.fetch('/api/chat');

    expect(res.status).toBe(200);
    expect(originalFetch).toHaveBeenCalledTimes(2);
    expect(authHeader(0)).toBe('Bearer stale');
    expect(authHeader(1)).toBe('Bearer fresh');
    expect(acquireApiToken).toHaveBeenLastCalledWith({ forceRefresh: true });
  });

  it('emits an auth-required event when a 401 persists after retry', async () => {
    const { installAuthorizedFetch, AUTH_REQUIRED_EVENT } = await freshModule();
    originalFetch.mockResolvedValue(jsonResponse(401));
    acquireApiToken.mockResolvedValue('tok');
    installAuthorizedFetch();

    const handler = vi.fn();
    window.addEventListener(AUTH_REQUIRED_EVENT, handler);
    await window.fetch('/api/chat');
    window.removeEventListener(AUTH_REQUIRED_EVENT, handler);

    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('emits an auth-forbidden event on a 403 (missing DriveThru.User role)', async () => {
    const { installAuthorizedFetch, AUTH_FORBIDDEN_EVENT } = await freshModule();
    originalFetch.mockResolvedValue(jsonResponse(403));
    acquireApiToken.mockResolvedValue('tok');
    installAuthorizedFetch();

    const handler = vi.fn();
    window.addEventListener(AUTH_FORBIDDEN_EVENT, handler);
    await window.fetch('/api/personas');
    window.removeEventListener(AUTH_FORBIDDEN_EVENT, handler);

    expect(handler).toHaveBeenCalledTimes(1);
  });
});

describe('installAuthorizedFetch when unconfigured', () => {
  it('is a no-op (Development pass-through, no wrapping, no token acquisition)', async () => {
    vi.doMock('../authConfig', () => ({ authConfig: { isConfigured: false } }));
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/api/chat');

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(window.fetch).toBe(originalFetch);
  });
});
