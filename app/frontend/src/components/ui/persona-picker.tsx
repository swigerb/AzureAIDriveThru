import { Label } from "@/components/ui/label";
import type { PersonaSummary } from "@/types/persona";

export interface PersonaPickerProps {
    personas: PersonaSummary[];
    currentId: string;
    onSelect: (id: string) => void;
}

/**
 * Accessible persona picker (issue #80 F1, design doc §5.1/§9 row F1). A native `<select>` with an
 * associated `<Label>` -- the same convention `settings.tsx`'s voice/model pickers already use, so
 * it's keyboard-operable and screen-reader-labeled for free via the browser's own `<select>`
 * semantics, with zero extra ARIA plumbing needed.
 *
 * Issue GH-180: the picker used to lock itself outright (disabled, with a "Locked for this order --
 * start a new order to switch" hint) whenever a session was active or the ticket had items,
 * leaving no path forward except abandoning the order. It's now ALWAYS enabled -- `App.tsx`'s
 * `onSelect` handler decides whether a switch needs confirmation (a `PersonaSwitchConfirmDialog`)
 * before it actually runs, rather than this control refusing the interaction altogether.
 */
export default function PersonaPicker({ personas, currentId, onSelect }: PersonaPickerProps) {
    return (
        <div className="flex items-center gap-2">
            <Label htmlFor="persona-picker" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Persona
            </Label>
            <select
                id="persona-picker"
                value={currentId}
                onChange={event => onSelect(event.target.value)}
                aria-label="Select persona"
                className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
            >
                {personas.map(persona => (
                    <option key={persona.id} value={persona.id}>
                        {persona.displayName}
                    </option>
                ))}
            </select>
        </div>
    );
}
