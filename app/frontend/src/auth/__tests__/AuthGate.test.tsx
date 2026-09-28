import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { InteractionStatus } from '@azure/msal-browser';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../authorizedFetch';
import { setRedirectError, resetRedirectErrorForTests } from '../redirectError';

const { mockAuthMode } = vi.hoisted(() => ({
  mockAuthMode: { mode: 'entra' as 'entra' | 'development' },
}));
vi.mock('../authMode', () => ({
  getResolvedAuthMode: () => mockAuthMode,
}));
vi.mock('../authConfig', () => ({
  loginRequest: { scopes: ['api://client/access_as_user'] },
  authConfig: { isConfigured: true },
}));

const acquireApiToken = vi.fn();
vi.mock('../tokenService', () => ({
  acquireApiToken: (...args: unknown[]) => acquireApiToken(...args),
}));

const useIsAuthenticated = vi.fn();
const loginRedirect = vi.fn();
const logoutRedirect = vi.fn();
const useMsal = vi.fn();
vi.mock('@azure/msal-react', () => ({
  useIsAuthenticated: () => useIsAuthenticated(),
  useMsal: () => useMsal(),
}));

// Imported after the mocks above are registered.
import { AuthGate } from '../AuthGate';

const child = <div data-testid="protected-child">app</div>;

beforeEach(() => {
  mockAuthMode.mode = 'entra';
  useIsAuthenticated.mockReset();
  useMsal.mockReset();
  loginRedirect.mockReset();
  logoutRedirect.mockReset();
  acquireApiToken.mockReset();
  acquireApiToken.mockResolvedValue('fresh-token');
  useMsal.mockReturnValue({
    instance: { loginRedirect, logoutRedirect },
    inProgress: InteractionStatus.None,
    accounts: [],
  });
  window.sessionStorage.clear();
  resetRedirectErrorForTests();
});

describe('AuthGate — unconfigured (Development pass-through)', () => {
  it('renders children directly and never touches MSAL hooks', () => {
    mockAuthMode.mode = 'development';
    render(<AuthGate>{child}</AuthGate>);

    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
    expect(useMsal).not.toHaveBeenCalled();
    expect(useIsAuthenticated).not.toHaveBeenCalled();
  });
});

describe('AuthGate — Entra gate (configured)', () => {
  it('renders the protected app when the user is authenticated', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
  });

  it('automatically starts loginRedirect on a fresh unauthenticated load (no click needed)', () => {
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);

    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });

  it('does NOT auto-redirect after an explicit sign-out; shows a manual button instead', async () => {
    window.sessionStorage.setItem('drivethru.auth.explicitSignOut', '1');
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);

    expect(loginRedirect).not.toHaveBeenCalled();
    const button = screen.getByTestId('auth-signin-button');
    expect(button).toHaveTextContent(/Sign in with Microsoft/i);

    await userEvent.click(button);
    expect(loginRedirect).toHaveBeenCalledTimes(1);
    expect(window.sessionStorage.getItem('drivethru.auth.explicitSignOut')).toBeNull();
  });

  it('shows a spinner (not the button) while sign-in is already in progress', () => {
    useIsAuthenticated.mockReturnValue(false);
    useMsal.mockReturnValue({
      instance: { loginRedirect, logoutRedirect },
      inProgress: InteractionStatus.AcquireToken,
      accounts: [],
    });
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByTestId('auth-signing-in')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-signin-button')).not.toBeInTheDocument();
    // Busy (inProgress !== None) suppresses the auto-redirect effect entirely.
    expect(loginRedirect).not.toHaveBeenCalled();
  });

  it('surfaces a DriveThru.User access-denied message on a 403 event', async () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });

    const forbidden = await screen.findByTestId('auth-forbidden');
    expect(forbidden).toHaveTextContent(/DriveThru\.User/);
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });

  it('Retry forces a fresh token (S1: roles claim is baked in at issuance) and re-renders the app', async () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });
    await screen.findByTestId('auth-forbidden');

    await userEvent.click(screen.getByTestId('auth-retry-button'));

    expect(acquireApiToken).toHaveBeenCalledWith({ forceRefresh: true });
    await waitFor(() => expect(screen.getByTestId('protected-child')).toBeInTheDocument());
  });

  it('"Sign in with a different account" signs out and marks an explicit sign-out', async () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });
    await screen.findByTestId('auth-forbidden');

    await userEvent.click(screen.getByTestId('auth-switch-account-button'));

    expect(logoutRedirect).toHaveBeenCalledTimes(1);
    expect(window.sessionStorage.getItem('drivethru.auth.explicitSignOut')).toBe('1');
  });

  it('shows the neutral Foundry subtitle regardless of state (S3)', () => {
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByText('AI Drive-Thru, a Microsoft Foundry realtime demo')).toBeInTheDocument();
    expect(screen.queryByText('Sign in to continue')).not.toBeInTheDocument();
  });
});

describe('AuthGate — B3: AUTH_REQUIRED_EVENT (token permanently unusable)', () => {
  it('drops a "still authenticated" session back to a sign-in state and auto-redirects once', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });

    // Busy (redirectStarting) outranks the auth-required message so the button never flashes (S2).
    expect(screen.getByTestId('auth-signing-in')).toBeInTheDocument();
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });

  it('does not loop: a second AUTH_REQUIRED_EVENT does not call loginRedirect again', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });

    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });

  it('clears a forbidden state when auth is required instead (they cannot both show)', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });

    expect(screen.queryByTestId('auth-forbidden')).not.toBeInTheDocument();
  });
});

describe('AuthGate — B4: redirect-error state (handleRedirectPromise failed)', () => {
  it('shows the AADSTS50105 "not assigned" message and a manual sign-in button, without auto-redirecting', () => {
    setRedirectError({ errorCode: 'server_error', errorMessage: 'AADSTS50105: user not assigned to app' });
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);

    expect(screen.getByTestId('auth-redirect-error')).toHaveTextContent(/not assigned to this app/i);
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
    expect(loginRedirect).not.toHaveBeenCalled();
  });

  it('shows a generic message for an unclassified redirect failure (never the raw MSAL message)', () => {
    setRedirectError({ errorCode: 'network_error', errorMessage: 'tenant abc123 client def456 failed' });
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);

    const message = screen.getByTestId('auth-redirect-error');
    expect(message).toHaveTextContent(/didn.t complete/i);
    expect(message).not.toHaveTextContent(/abc123|def456/);
  });

  it('clicking "Sign in" clears the redirect error and starts a fresh loginRedirect', async () => {
    setRedirectError({ errorCode: 'user_cancelled' });
    useIsAuthenticated.mockReturnValue(false);
    render(<AuthGate>{child}</AuthGate>);
    expect(screen.getByTestId('auth-redirect-error')).toHaveTextContent(/cancelled/i);

    await userEvent.click(screen.getByTestId('auth-signin-button'));

    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });
});
