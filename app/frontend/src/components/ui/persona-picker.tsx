import { Label } from "@/components/ui/label";
import { Tooltip } from "@/components/ui/tooltip";
import type { PersonaSummary } from "@/types/persona";

export interface PersonaPickerProps {
    personas: PersonaSummary[];
    currentId: string;
    onSelect: (id: string) => void;
    /** True while a session is active (ADR-001 decision 2: no mid-conversation persona
     * switching) -- the picker is disabled rather than allowed to reset an in-progress order. */
    disabled: boolean;
}

/**
 * Accessible persona picker (issue #80 F1, design doc §5.1/§9 row F1). A native `<select>` with an
 * associated `<Label>` -- the same convention `settings.tsx`'s voice/model pickers already use, so
 * it's keyboard-operable and screen-reader-labeled for free via the browser's own `<select>`
 * semantics, with zero extra ARIA plumbing needed.
 *
 * Per ADR-001 decision 2 ("one URL, per-session persona picker... no mid-conversation switching"),
 * the picker only ever sets the persona for the NEXT session: `App.tsx` disables it (passing
 * `disabled`) whenever a session is already active, rather than letting a mid-call switch silently
 * reset the order -- switching a persona always starts a new session by design.
 */
const LOCK_HINT_ID = "persona-picker-lock-hint";
const LOCK_HINT_TEXT = "Locked for this order -- start a new order to switch";

export default function PersonaPicker({ personas, currentId, onSelect, disabled }: PersonaPickerProps) {
    const select = (
        <select
            id="persona-picker"
            value={currentId}
            disabled={disabled}
            onChange={event => onSelect(event.target.value)}
            aria-label="Select persona"
            // Non-blocking item from Rick's PR-110 review (issue #80): the visible Tooltip only
            // reaches sighted, hovering/focused users -- aria-describedby exposes the same lock
            // reason to screen readers via the always-present (but visually hidden) sr-only text
            // below, so a keyboard/AT user tabbing onto the disabled select still hears why.
            aria-describedby={disabled ? LOCK_HINT_ID : undefined}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary disabled:cursor-not-allowed disabled:opacity-60 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
        >
            {personas.map(persona => (
                <option key={persona.id} value={persona.id}>
                    {persona.displayName}
                </option>
            ))}
        </select>
    );

    return (
        <div className="flex items-center gap-2">
            <Label htmlFor="persona-picker" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Persona
            </Label>
            {disabled ? (
                <Tooltip content={LOCK_HINT_TEXT}>
                    <div>{select}</div>
                </Tooltip>
            ) : (
                select
            )}
            {disabled && (
                <span id={LOCK_HINT_ID} className="sr-only">
                    {LOCK_HINT_TEXT}
                </span>
            )}
        </div>
    );
}
