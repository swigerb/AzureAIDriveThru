import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { MsalProvider } from "@azure/msal-react";

import { I18nextProvider } from "react-i18next";
import i18next from "./i18n/config";

import App from "./App.tsx";
import "./index.css";

import { assertEntraConfigured, authConfig } from "@/auth/authConfig";
import { getMsalInstance, initializeMsal } from "@/auth/msalInstance";
import { installAuthorizedFetch } from "@/auth/authorizedFetch";
import { ConfigErrorScreen } from "@/auth/ConfigErrorScreen";

// Issue #80 F1: PersonaProvider (inside App.tsx's RootApp) now owns applying the active persona's
// theme at startup -- fetched from `/api/personas`/`/api/personas/{id}` -- superseding F2's single
// hard-coded `applyTheme(...)` call that used to live here.

/**
 * Fail-closed MSAL bootstrap (ADR-002, design §18.6, issue GH-145).
 *
 * `authConfig.isConfigured === false` is the intentional Development pass-through: no MSAL, no
 * `MsalProvider`; `AuthGate` (inside `<App/>`'s `RootApp`) renders straight through.
 *
 * When Entra IS configured, `assertEntraConfigured()` fails closed FIRST -- before any MSAL init,
 * API, or WebSocket call -- if the tenant/client ids are present but invalid (placeholder or
 * malformed). Only once that passes do we complete any pending redirect sign-in
 * (`initializeMsal()`) and centrally attach the bearer token to every protected fetch
 * (`installAuthorizedFetch()`), then render the app inside `MsalProvider` so `AuthGate` and any
 * `useMsal()`/`useIsAuthenticated()` consumer has a working MSAL context.
 */
async function bootstrap() {
    const root = createRoot(document.getElementById("root")!);

    try {
        if (!authConfig.isConfigured) {
            root.render(
                <StrictMode>
                    <I18nextProvider i18n={i18next}>
                        <App />
                    </I18nextProvider>
                </StrictMode>
            );
            return;
        }

        assertEntraConfigured();
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
