export type MenuMode = "breakfast" | "lunch";

/** issue 165: the original reference app's own default -- an omitted/invalid stored choice always
 * resolves to "lunch", never an unbound/undefined mode, for any persona that declares
 * `features.dayparts`. */
export const DEFAULT_MENU_MODE: MenuMode = "lunch";

/**
 * Resolves the menu mode choice to use for a persona: `stored` if it's still a recognized mode,
 * else the shared default ("lunch", issue 164 decision D3). Mirrors `lib/models.ts`'s
 * `resolveModelId`/`lib/voices.ts`'s `resolveVoice` -- the same "trust it only if it's still
 * valid" rule, since a stored value can go stale (e.g. localStorage tampering, a prior app
 * version that stored something else under this key).
 */
export function resolveMenuMode(stored: string | null | undefined): MenuMode {
    return stored === "breakfast" || stored === "lunch" ? stored : DEFAULT_MENU_MODE;
}

/**
 * Per-persona localStorage key (same "persisted per persona" rule as `lib/models.ts`'s
 * `modelStorageKey`): a menu mode choice is namespaced by persona id so switching personas never
 * leaks one persona's chosen mode onto another's, and a persona with no `features.dayparts` never
 * even reads/writes this key (`App.tsx` only resolves/persists it when `current.features.dayparts`
 * is true).
 */
export function menuModeStorageKey(personaId: string): string {
    return `menuModeChoice.${personaId}`;
}
