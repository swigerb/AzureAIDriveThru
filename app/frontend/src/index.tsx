import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { I18nextProvider } from "react-i18next";
import i18next from "./i18n/config";

import App from "./App.tsx";
import "./index.css";
import { applyTheme, SONIC_THEME } from "@/lib/personaTheme";

// Issue #80 F2: runtime theming groundwork. Sonic is the only persona today, so this just
// re-asserts index.css's own defaults -- proving the CSS-variable seam works end to end before a
// persona picker (F1) exists to pick anything else.
applyTheme(SONIC_THEME);

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <I18nextProvider i18n={i18next}>
            <App />
        </I18nextProvider>
    </StrictMode>
);
