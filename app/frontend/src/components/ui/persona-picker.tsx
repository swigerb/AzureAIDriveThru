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
export default function PersonaPicker({ personas, currentId, onSelect, disabled }: PersonaPickerProps) {
    const select = (
        <select
            id="persona-picker"
            value={currentId}
            disabled={disabled}
            onChange={event => onSelect(event.target.value)}
            aria-label="Select persona"
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
                <Tooltip content="Locked for this order -- start a new order to switch">
                    <div>{select}</div>
                </Tooltip>
            ) : (
                select
            )}
        </div>
    );
}
