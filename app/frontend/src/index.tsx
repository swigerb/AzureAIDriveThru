import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { MsalProvider } from "@azure/msal-react";

import { I18nextProvider } from "react-i18next";
import i18next from "./i18n/config";

import App from "./App.tsx";
import "./index.css";

import { getMsalInstance, initializeMsal } from "@/auth/msalInstance";
import { installAuthorizedFetch } from "@/auth/authorizedFetch";
import { ConfigErrorScreen } from "@/auth/ConfigErrorScreen";
import { getResolvedAuthMode } from "@/auth/authMode";

// Issue #80 F1: PersonaProvider (inside App.tsx's RootApp) now owns applying the active persona's
// theme at startup -- fetched from `/api/personas`/`/api/personas/{id}` -- superseding F2's single
// hard-coded `applyTheme(...)` call that used to live here.

/**
 * Fail-closed MSAL bootstrap (ADR-002, design §18.6, issue GH-145).
 *
 * `getResolvedAuthMode()` (PR GH-148 review round 2, item B1) is the SINGLE source of truth for
 * whether this is a Development pass-through build or an Entra build -- shared with `AuthGate.tsx`
 * and the Vite build guard, so all three can never disagree. It is called explicitly here, inside
 * this try block (never assigned to a module-level constant -- see `authMode.ts`'s own note on
 * why a top-level-throwing singleton would not be catchable), so an invalid configuration in ANY
 * environment -- including an unconfigured PRODUCTION build, which used to silently render the
 * unauthenticated pass-through -- fails closed into `ConfigErrorScreen` instead.
 *
 * `mode === 'development'` is the intentional Development pass-through: no MSAL, no
 * `MsalProvider`; `AuthGate` (inside `<App/>`'s `RootApp`) renders straight through.
 *
 * When `mode === 'entra'`, ids are already guaranteed present and valid by `getResolvedAuthMode()`
 * itself -- before any MSAL init, API, or WebSocket call. Only once that passes do we complete any
 * pending redirect sign-in (`initializeMsal()`) and centrally attach the bearer token to every
 * protected fetch (`installAuthorizedFetch()`), then render the app inside `MsalProvider` so
 * `AuthGate` and any `useMsal()`/`useIsAuthenticated()` consumer has a working MSAL context.
 */
async function bootstrap() {
    const root = createRoot(document.getElementById("root")!);

    try {
        const { mode } = getResolvedAuthMode();

        if (mode === "development") {
            root.render(
                <StrictMode>
                    <I18nextProvider i18n={i18next}>
                        <App />
                    </I18nextProvider>
                </StrictMode>
            );
            return;
        }

        await initializeMsal();
        installAuthorizedFetch();

        root.render(
            <StrictMode>
                <MsalProvider instance={getMsalInstance()}>
                    <I18nextProvider i18n={i18next}>
                        <App />
                    </I18nextProvider>
                </MsalProvider>
            </StrictMode>
        );
    } catch (error) {
        // Never expose a protected surface on a bootstrap failure; show a safe, dependency-light
        // error screen that makes no API/WebSocket calls instead of the app shell.
        // eslint-disable-next-line no-console
        console.error("Auth bootstrap failed:", error);
        root.render(
            <StrictMode>
                <ConfigErrorScreen />
            </StrictMode>
        );
    }
}

void bootstrap();
