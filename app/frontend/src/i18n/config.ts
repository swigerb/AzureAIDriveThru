import i18next from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import HttpApi from "i18next-http-backend";
import { initReactI18next } from "react-i18next";

import { baseTranslationResources } from "./baseResources";

export const supportedLngs: { [key: string]: { name: string; locale: string } } = {
    en: {
        name: "English",
        locale: "en-US"
    },
    es: {
        name: "Español",
        locale: "es-ES"
    },
    fr: {
        name: "Français",
        locale: "fr-FR"
    },
    ja: {
        name: "日本語",
        locale: "ja-JP"
    }
};

i18next
    .use(HttpApi)
    .use(LanguageDetector)
    .use(initReactI18next)
    // init i18next
    // for all options read: https://www.i18next.com/overview/configuration-options
    .init({
        resources: {
            en: { translation: baseTranslationResources.en },
            es: { translation: baseTranslationResources.es },
            fr: { translation: baseTranslationResources.fr },
            ja: { translation: baseTranslationResources.ja }
        },
        fallbackLng: "en",
        supportedLngs: Object.keys(supportedLngs),
        debug: import.meta.env.DEV,
        interpolation: {
            escapeValue: false // not needed for react as it escapes by default
        },
        // Issue 119: `persona-context.tsx` calls `i18next.addResourceBundle(...)` on every
        // persona switch to merge that pack's strings into the running app. react-i18next's
        // `useTranslation()` only re-renders on the events named by `bindI18n` (default
        // 'languageChanged loaded') plus, if set, `bindI18nStore`'s store-level events -- which
        // is '' (nothing) by default. `addResourceBundle` never fires a `languageChanged`/`loaded`
        // event, only the resource store's own 'added' event, so already-mounted consumers (the
        // order ticket, the status message) silently kept rendering the previous persona's
        // strings after a switch even though `t(key)` would already return the new value. Binding
        // 'added removed' here is the documented react-i18next option for exactly this case.
        react: {
            bindI18nStore: "added removed"
        }
    });

export default i18next;
