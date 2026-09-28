import type { ReactNode } from 'react';
import { getResolvedAuthMode } from './authMode';
import { EntraAuthGate } from './gates/EntraAuthGate';

/**
 * Sign-in gate for the SPA (ADR-002, design §18.6, issue GH-145). Single-provider (Entra) --
 * unlike Retail Pulse's provider-neutral dispatcher, there is no mode switch here.
 *
 * `AuthGate` wraps `PersonaProvider` and `App` (see `src/App.tsx`'s `RootApp`), so nothing
 * persona-related loads before sign-in.
 *
 * `getResolvedAuthMode()` (PR GH-148 review round 2, item B1) is the SAME shared resolver
 * `index.tsx`'s bootstrap already used to decide whether to render `MsalProvider` at all, so this
 * can never disagree with that decision -- by the time `AuthGate` renders, `index.tsx` has already
 * either shown `ConfigErrorScreen` (invalid config) or rendered a tree consistent with this mode.
 *
 * When `mode === 'development'` (the Development pass-through), this is a transparent no-op: no
 * `MsalProvider` is required in the tree, so unconfigured/test environments never need to mock
 * MSAL. `AuthGate` itself calls no hooks before this early return -- only `EntraAuthGate` (rendered
 * exclusively inside a configured, MSAL-backed tree) calls `useMsal`/`useIsAuthenticated`
 * unconditionally on every render.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  if (getResolvedAuthMode().mode !== 'entra') {
    return <>{children}</>;
  }

  return <EntraAuthGate>{children}</EntraAuthGate>;
}
