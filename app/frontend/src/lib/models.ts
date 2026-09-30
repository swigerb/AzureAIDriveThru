import type { PersonaModelOption, PersonaModels } from "@/types/persona";

export type SelectablePipeline = "realtime" | "cascade";

export interface ModelGroup {
    pipeline: SelectablePipeline;
    /** Human label for the `<optgroup>` this pipeline renders as (issue #80 F10). */
    label: string;
    options: PersonaModelOption[];
}

const PIPELINE_LABELS: Record<SelectablePipeline, string> = {
    realtime: "Realtime",
    cascade: "Cascade"
};

/**
 * Flattens a persona's model catalog (design doc §7, `/api/personas/{id}`'s `models` block,
 * `types/persona.ts`'s `PersonaModels`) into the groups the F10 model picker renders as
 * `<optgroup>`s, grouped by pipeline (Realtime / Cascade).
 *
 * Deliberately `realtime`/`cascade` only -- the on-device `local` pipeline was removed entirely
 * by issue 155 (reversing ADR-001 decision 7, 2026-09-28): the demo runs on Microsoft Foundry
 * exclusively now, so there is no third pipeline to ever show in this picker.
 *
 * Rick's PR 134 review, item 1: a pipeline whose `models` list is empty or missing renders NO
 * group at all -- both backends always send this list now (Python's `_selectable_models`, C#'s
 * since issue 122), so an empty/missing list means nothing is deployed or allowed for that pipeline,
 * and the persona's declared `default` is deliberately left out of it for that exact reason. The
 * previous fallback here synthesized a single option for that default id, which puts back the one
 * id the backend chose NOT to offer -- `?model=` would then carry an id the server 404s on.
 */
export function selectableModelGroups(models: PersonaModels): ModelGroup[] {
    const groups: ModelGroup[] = [];
    for (const pipeline of ["realtime", "cascade"] as const) {
        const entry = models[pipeline];
        if (!entry?.models || entry.models.length === 0) continue;
        groups.push({ pipeline, label: PIPELINE_LABELS[pipeline], options: entry.models });
    }
    return groups;
}

/** Every selectable option across pipelines, in the same order the picker renders them. */
export function flattenModelOptions(models: PersonaModels): PersonaModelOption[] {
    return selectableModelGroups(models).flatMap(group => group.options);
}

/**
 * The persona's own default model, per pipeline preference (realtime first, then cascade) --
 * but only if it's actually present among the selectable options. PR 106's review flagged that
 * an undeployed/disallowed default is deliberately left out of the `models` array rather than
 * silently offered, so a persona's declared default can be absent from its own catalog; this
 * falls through to the first selectable option in that case rather than pointing the picker at
 * an id it can't actually render as an `<option>`.
 *
 * Rick's PR 134 review, item 1: with zero selectable options (every pipeline's `models` list is
 * empty or missing), returns `""` rather than `models.realtime.default` -- that default is
 * unlisted by definition here, so returning it would still send the server an id it doesn't
 * allow. `useRealTime` already omits `model` from `/realtime`'s query string whenever it's falsy
 * (see `hooks/useRealtime.tsx`), so an empty string here means the server's own default applies.
 */
export function defaultModelId(models: PersonaModels): string {
    const options = flattenModelOptions(models);
    if (options.length === 0) return "";
    if (options.some(option => option.id === models.realtime.default)) return models.realtime.default;
    if (models.cascade && options.some(option => option.id === models.cascade!.default)) return models.cascade.default;
    return options[0].id;
}

/**
 * Resolves the model choice to use for a persona: `stored` if it's still one of this persona's
 * selectable options, else the persona's own default. Mirrors `lib/voices.ts`'s `resolveVoice` --
 * the same "trust it only if it's still valid" rule, since a stored id can go stale when the
 * guest switches to a persona (or model catalog) that no longer offers it.
 */
export function resolveModelId(stored: string | null | undefined, models: PersonaModels): string {
    const options = flattenModelOptions(models);
    return stored && options.some(option => option.id === stored) ? stored : defaultModelId(models);
}

/**
 * Per-persona localStorage key (issue #80 F10: "persisted per persona") -- unlike the single,
 * un-namespaced `voiceChoice` key in `App.tsx`, a model choice is namespaced by persona id so
 * switching personas never leaks one persona's chosen model onto another's.
 */
export function modelStorageKey(personaId: string): string {
    return `modelChoice.${personaId}`;
}
