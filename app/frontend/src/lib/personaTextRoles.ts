import type { PersonaTextRole, PersonaTextRoles } from "@/types/persona";

/**
 * Issue 164 R2 (PR 167 round 1 review): a handful of shared text elements (the hero badge, the
 * footer tagline/extra line, the item-count chip, and the chips-variant session label) draw their
 * color from one of a persona's brand roles rather than a hardcoded Tailwind class, so each
 * original's specific per-element choice -- for example one pack's near-black footer tagline next
 * to its golden italic brand tagline -- can be reproduced without a persona-specific branch inside
 * the shared component itself. A pack authors `ui.textRoles` to override any slot; an omitted
 * slot falls back to `DEFAULT_TEXT_ROLES` below.
 */
export const DEFAULT_TEXT_ROLES: Required<PersonaTextRoles> = {
    badge: "primary",
    countChip: "secondary",
    footerTagline: "secondary",
    footerExtra: "secondary"
};

/** Light-mode (page-background-agnostic) Tailwind text-color class for each role. */
const ROLE_TEXT_CLASS: Record<PersonaTextRole, string> = {
    primary: "text-brand-primary",
    primaryDeep: "text-brand-primary-deep",
    secondary: "text-brand-secondary",
    accent: "text-brand-accent",
    ink: "text-brand-ink"
};

/**
 * Dark-mode Tailwind text-color class for each role, used only by components -- currently just
 * the item-count chip -- that already render a distinct dark-mode surface. Issue 164 R2(c):
 * `primaryDeep` reuses the shared primary "tint" color on dark surfaces (there is no separate
 * primaryDeep-tint token), and `accent` reuses the shared secondary "tint" color (this is what
 * makes one pack's dark-mode count chip render its brand-accent-gold hex instead of the
 * unreadable light-mode near-black accent hex).
 */
const ROLE_DARK_TEXT_CLASS: Record<PersonaTextRole, string> = {
    primary: "dark:text-brand-primary-tint",
    primaryDeep: "dark:text-brand-primary-tint",
    secondary: "dark:text-brand-secondary-tint",
    accent: "dark:text-brand-secondary-tint",
    ink: "dark:text-white/80"
};

/** Resolves which role a given slot uses: the pack's own override if it set one, else the shared default. */
export function resolveTextRole(slot: keyof PersonaTextRoles, roles: PersonaTextRoles | undefined): PersonaTextRole {
    return roles?.[slot] ?? DEFAULT_TEXT_ROLES[slot];
}

/** Light-mode Tailwind text-color class for a resolved role. */
export function textRoleClass(role: PersonaTextRole): string {
    return ROLE_TEXT_CLASS[role];
}

/** Dark-mode Tailwind text-color class for a resolved role (see `ROLE_DARK_TEXT_CLASS` above). */
export function textRoleDarkClass(role: PersonaTextRole): string {
    return ROLE_DARK_TEXT_CLASS[role];
}

/** Convenience for components, like the item-count chip, that render in both light and dark modes: resolves a slot and returns its combined light+dark class string. */
export function textRoleClasses(slot: keyof PersonaTextRoles, roles: PersonaTextRoles | undefined): string {
    const role = resolveTextRole(slot, roles);
    return `${textRoleClass(role)} ${textRoleDarkClass(role)}`;
}
