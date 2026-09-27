import { useTranslation } from "react-i18next";

import { Label } from "@/components/ui/label";
import { Tooltip } from "@/components/ui/tooltip";
import { selectableModelGroups } from "@/lib/models";
import type { SelectablePipeline } from "@/lib/models";
import type { PersonaModels } from "@/types/persona";

/** i18n keys for each pipeline's `<optgroup>` label -- kept here (rather than in `lib/models.ts`,
 * whose helpers stay plain/i18next-free so callers like `persona-context.tsx`'s translation-bundle
 * reset can import them without side effects) since only the component actually renders text. */
const PIPELINE_LABEL_KEYS: Record<SelectablePipeline, string> = {
    realtime: "picker.realtimeGroup",
    cascade: "picker.cascadeGroup"
};

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
 *
 * Rick's PR 134 review, item 1: `selectableModelGroups` never invents an option for an unlisted
 * default anymore -- a pipeline with an empty/missing `models` list is simply omitted. When that
 * leaves zero selectable options across every pipeline, the control renders as a single disabled
 * "No models available" option instead of an empty, unusable `<select>` -- there is genuinely
 * nothing this persona/backend currently allows, so the picker says so rather than silently
 * offering an id the server would reject.
 */
export default function ModelPicker({ models, currentId, onSelect, disabled }: ModelPickerProps) {
    const { t } = useTranslation();
    const groups = selectableModelGroups(models);
    const hasOptions = groups.length > 0;
    const isDisabled = disabled || !hasOptions;

    const select = (
        <select
            id="model-picker"
            value={hasOptions ? currentId : ""}
            disabled={isDisabled}
            onChange={event => onSelect(event.target.value)}
            aria-label="Select model"
            aria-describedby={isDisabled ? LOCK_HINT_ID : undefined}
            className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary disabled:cursor-not-allowed disabled:opacity-60 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
        >
            {hasOptions ? (
                groups.map(group => (
                    <optgroup key={group.pipeline} label={t(PIPELINE_LABEL_KEYS[group.pipeline])}>
                        {group.options.map(option => (
                            <option key={option.id} value={option.id}>
                                {option.reasoning ? `${option.label} ${t("picker.reasoningSuffix")}` : option.label}
                            </option>
                        ))}
                    </optgroup>
                ))
            ) : (
                <option value="">{t("picker.noModelsAvailable")}</option>
            )}
        </select>
    );

    return (
        <div className="flex-1 space-y-0.5">
            <Label htmlFor="model-picker" className="text-gray-900 dark:text-gray-100">
                {t("picker.modelLabel")}
            </Label>
            <p className="text-sm text-gray-600 dark:text-gray-400">{t("picker.modelDescription")}</p>
            <div className="pt-1">
                {disabled && hasOptions ? (
                    <Tooltip content={t("picker.lockedHint")}>
                        <div>{select}</div>
                    </Tooltip>
                ) : (
                    select
                )}
            </div>
            {isDisabled && (
                <span id={LOCK_HINT_ID} className="sr-only">
                    {hasOptions ? t("picker.lockedHint") : t("picker.noModelsAvailable")}
                </span>
            )}
        </div>
    );
}
