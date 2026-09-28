import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { InteractionStatus } from '@azure/msal-browser';
import { AUTH_FORBIDDEN_EVENT } from '../authorizedFetch';

const { mockAuthMode } = vi.hoisted(() => ({
  mockAuthMode: { mode: 'entra' as 'entra' | 'development' },
}));
vi.mock('../authMode', () => ({
  getResolvedAuthMode: () => mockAuthMode,
}));
vi.mock('../authConfig', () => ({
  loginRequest: { scopes: ['api://client/access_as_user'] },
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
  useMsal.mockReturnValue({
    instance: { loginRedirect, logoutRedirect },
    inProgress: InteractionStatus.None,
    accounts: [],
  });
  window.sessionStorage.clear();
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

  it('Retry clears the forbidden state and re-renders the app', async () => {
    useIsAuthenticated.mockReturnValue(true);
    render(<AuthGate>{child}</AuthGate>);
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });
    await screen.findByTestId('auth-forbidden');

    await userEvent.click(screen.getByTestId('auth-retry-button'));

    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
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
});
