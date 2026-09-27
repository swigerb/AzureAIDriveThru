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
 * Deliberately `realtime`/`cascade` only -- `local` is issue #81/F12's on-device pipeline, which
 * depends on work not yet done (#81) and is out of this task's scope, so it's left out of the
 * picker entirely rather than shown and then rejected by the server.
 *
 * A pipeline whose `models` list hasn't landed yet (PR 106 review: it's optional on the wire so
 * older backends -- or this branch's own backend before its final merge -- still respond with
 * just a bare `default`) falls back to a single synthetic option for that default id, so the
 * picker still has something selectable rather than an empty group.
 */
export function selectableModelGroups(models: PersonaModels): ModelGroup[] {
    const groups: ModelGroup[] = [];
    for (const pipeline of ["realtime", "cascade"] as const) {
        const entry = models[pipeline];
        if (!entry) continue;
        const options = entry.models && entry.models.length > 0 ? entry.models : [{ id: entry.default, label: entry.default, reasoning: false }];
        groups.push({ pipeline, label: PIPELINE_LABELS[pipeline], options });
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
 */
export function defaultModelId(models: PersonaModels): string {
    const options = flattenModelOptions(models);
    if (options.length === 0) return models.realtime.default;
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
