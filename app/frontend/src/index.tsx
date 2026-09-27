import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { I18nextProvider } from "react-i18next";
import i18next from "./i18n/config";

import App from "./App.tsx";
import "./index.css";

// Issue #80 F1: PersonaProvider (inside App.tsx's RootApp) now owns applying the active persona's
// theme at startup -- fetched from `/api/personas`/`/api/personas/{id}` -- superseding F2's single
// hard-coded `applyTheme(SONIC_THEME)` call that used to live here.

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <I18nextProvider i18n={i18next}>
            <App />
        </I18nextProvider>
    </StrictMode>
);
