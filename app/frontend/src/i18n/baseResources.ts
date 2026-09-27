import enTranslation from "../locales/en/translation.json";
import esTranslation from "../locales/es/translation.json";
import frTranslation from "../locales/fr/translation.json";
import jaTranslation from "../locales/ja/translation.json";

/**
 * The pristine, persona-neutral "translation" bundle for each locale -- deliberately split out of
 * `i18n/config.ts` (whose import triggers a real `i18next.init()` as a side effect) so anything
 * that only needs these plain JSON tables, such as `persona-context.tsx`'s per-switch reset (issue
 * 119 item 1 follow-up), can import them without accidentally initializing the real i18next
 * singleton in test files that rely on it staying uninitialized (they mock `react-i18next`
 * globally and never otherwise touch the real `i18next` package).
 *
 * `persona-context.tsx` replays these back onto the live i18next resource store immediately
 * before merging each persona's own `ui.strings` on every switch. Without that reset,
 * `addResourceBundle`'s deep merge is cumulative across persona switches: a pack that only
 * defines a handful of brand-specific keys silently inherits whatever the *previously active*
 * persona last set an undefined key to, instead of falling back to this shared neutral default.
 */
export const baseTranslationResources: { [locale: string]: object } = {
    en: enTranslation,
    es: esTranslation,
    fr: frTranslation,
    ja: jaTranslation
};
