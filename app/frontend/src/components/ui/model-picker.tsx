import { Label } from "@/components/ui/label";
import { Tooltip } from "@/components/ui/tooltip";
import { selectableModelGroups } from "@/lib/models";
import type { PersonaModels } from "@/types/persona";

export interface ModelPickerProps {
    models: PersonaModels;
    currentId: string;
    onSelect: (id: string) => void;
    /** True while a session is active (ADR-001 decision 2: no mid-conversation model switching,
     * same rule `persona-picker.tsx` follows) -- a change here only ever takes effect on the NEXT
     * session, so the picker is disabled rather than left free to reconnect mid-order. */
    disabled: boolean;
}

const LOCK_HINT_ID = "model-picker-lock-hint";
const LOCK_HINT_TEXT = "Locked for this order -- start a new order to switch";

/**
 * Accessible model picker (issue #80 F10, design doc §7/§9 row F10). Replaces `settings.tsx`'s old
 * disabled "Model: Work in progress" placeholder AND the stale "Azure Backend" (realtime vs.
 * cascade) toggle with one real, working picker grouped by pipeline (Realtime / Cascade --
 * `lib/models.ts`'s `selectableModelGroups`) via native `<optgroup>`s, sourced from the bound
 * persona's `/api/personas/{id}` `models` block.
 *
 * Stacked layout (label on top, full-width select below) rather than the side-by-side row every
 * other Settings toggle uses -- the same restack PR-120 applied to the voice picker once a
 * side-by-side control stopped fitting the dialog's 425px width; a model picker's option labels
 * are real model names (not a short on/off Switch), so it needs the same treatment.
 *
 * A model with `reasoning: true` (`PersonaModelOption`, PR 106/#75) gets a "(reasoning)" suffix on
 * its option label -- a plain `<option>` can't contain styled markup, so this text suffix is the
 * accessible equivalent of a visual badge: a screen reader announces it exactly the way a sighted
 * guest reads it.
 */
export default function ModelPicker({ models, currentId, onSelect, disabled }: ModelPickerProps) {
    const groups = selectableModelGroups(models);

    const select = (
        <select
            id="model-picker"
            value={currentId}
            disabled={disabled}
            onChange={event => onSelect(event.target.value)}
            aria-label="Select model"
            aria-describedby={disabled ? LOCK_HINT_ID : undefined}
            className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary disabled:cursor-not-allowed disabled:opacity-60 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
        >
            {groups.map(group => (
                <optgroup key={group.pipeline} label={group.label}>
                    {group.options.map(option => (
                        <option key={option.id} value={option.id}>
                            {option.reasoning ? `${option.label} (reasoning)` : option.label}
                        </option>
                    ))}
                </optgroup>
            ))}
        </select>
    );

    return (
        <div className="flex-1 space-y-0.5">
            <Label htmlFor="model-picker" className="text-gray-900 dark:text-gray-100">
                Model
            </Label>
            <p className="text-sm text-gray-600 dark:text-gray-400">Choose which model powers the conversation</p>
            <div className="pt-1">
                {disabled ? (
                    <Tooltip content={LOCK_HINT_TEXT}>
                        <div>{select}</div>
                    </Tooltip>
                ) : (
                    select
                )}
            </div>
            {disabled && (
                <span id={LOCK_HINT_ID} className="sr-only">
                    {LOCK_HINT_TEXT}
                </span>
            )}
        </div>
    );
}
