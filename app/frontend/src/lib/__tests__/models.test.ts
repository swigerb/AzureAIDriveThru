import { describe, expect, it } from "vitest";

import { defaultModelId, flattenModelOptions, modelStorageKey, resolveModelId, selectableModelGroups } from "../models";
import type { PersonaModels } from "@/types/persona";

// Issue #80 F10's pure helpers (design doc §7's `/api/personas/{id}` `models` block). Covered
// separately from `model-picker.test.tsx` because the grouping/default/persistence rules here
// (stale-default fallback, per-persona storage keys) are worth guarding independent of any
// particular rendering.

const MODELS: PersonaModels = {
    realtime: {
        default: "gpt-realtime-2.1",
        models: [
            { id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: false },
            { id: "gpt-realtime-mini", label: "GPT Realtime Mini", reasoning: false }
        ]
    },
    cascade: {
        default: "gpt-5-mini",
        models: [
            { id: "gpt-5-mini", label: "GPT-5 Mini", reasoning: false },
            { id: "phi-4", label: "Phi-4", reasoning: true }
        ]
    }
};

describe("selectableModelGroups", () => {
    it("groups realtime and cascade models (the only two pipelines Microsoft Foundry supports)", () => {
        const groups = selectableModelGroups(MODELS);
        expect(groups.map(g => g.pipeline)).toEqual(["realtime", "cascade"]);
        expect(groups[0].label).toBe("Realtime");
        expect(groups[1].label).toBe("Cascade");
    });

    // Rick's PR 134 review, item 1: a pipeline with an empty or missing `models` list must never
    // fall back to a synthetic option for its `default` -- both backends always send this list
    // now (Python's `_selectable_models`, C#'s since #122), so an empty list means nothing is
    // deployed/allowed, and the persona's declared default is deliberately left out of it for
    // that exact reason. Re-introducing the old fallback (a single `{ id: entry.default, ... }`
    // option whenever `models` is empty/missing) must fail this test.
    it("renders no group at all for a pipeline whose models[] list is empty", () => {
        const emptyCascade: PersonaModels = { realtime: MODELS.realtime, cascade: { default: "gpt-5-mini", models: [] } };
        expect(selectableModelGroups(emptyCascade).map(g => g.pipeline)).toEqual(["realtime"]);
    });

    it("renders no group at all for a pipeline whose models[] list hasn't landed (missing entirely)", () => {
        const bareCascade: PersonaModels = { realtime: MODELS.realtime, cascade: { default: "gpt-5-mini" } };
        expect(selectableModelGroups(bareCascade).map(g => g.pipeline)).toEqual(["realtime"]);
    });

    it("returns zero groups when every pipeline's models[] list is empty", () => {
        const allEmpty: PersonaModels = { realtime: { default: "gpt-realtime-2.1", models: [] } };
        expect(selectableModelGroups(allEmpty)).toEqual([]);
    });

    it("omits a pipeline entirely when the persona doesn't offer it", () => {
        const realtimeOnly: PersonaModels = { realtime: MODELS.realtime };
        expect(selectableModelGroups(realtimeOnly).map(g => g.pipeline)).toEqual(["realtime"]);
    });
});

describe("flattenModelOptions", () => {
    it("returns every selectable option across pipelines, in picker order", () => {
        expect(flattenModelOptions(MODELS).map(o => o.id)).toEqual(["gpt-realtime-2.1", "gpt-realtime-mini", "gpt-5-mini", "phi-4"]);
    });
});

describe("defaultModelId", () => {
    it("prefers the realtime pipeline's default when it's a selectable option", () => {
        expect(defaultModelId(MODELS)).toBe("gpt-realtime-2.1");
    });

    it("falls back to the cascade default when realtime's declared default isn't actually offered", () => {
        // PR 106 review: an undeployed/disallowed default is left out of `models` rather than
        // silently offered, so a persona's own declared default can be missing from its catalog.
        const staleRealtimeDefault: PersonaModels = {
            realtime: { default: "gpt-realtime-retired", models: [{ id: "gpt-realtime-mini", label: "GPT Realtime Mini", reasoning: false }] },
            cascade: MODELS.cascade
        };
        expect(defaultModelId(staleRealtimeDefault)).toBe("gpt-5-mini");
    });

    it("falls back to the first selectable option when neither declared default is offered", () => {
        const bothStale: PersonaModels = {
            realtime: { default: "gone-realtime", models: [{ id: "gpt-realtime-mini", label: "GPT Realtime Mini", reasoning: false }] },
            cascade: { default: "gone-cascade", models: [{ id: "gpt-5-mini", label: "GPT-5 Mini", reasoning: false }] }
        };
        expect(defaultModelId(bothStale)).toBe("gpt-realtime-mini");
    });

    // Rick's PR 134 review, item 1: with zero selectable options across every pipeline, this must
    // return `""`, never `models.realtime.default` -- that default is unlisted by definition here
    // (an empty `models[]` means nothing is deployed/allowed), so returning it would still send
    // the server an id it doesn't offer. `useRealTime` omits `?model=` entirely when this is
    // falsy, so the server's own default applies instead.
    it("returns an empty string when there are zero selectable options anywhere", () => {
        const zeroOptions: PersonaModels = { realtime: { default: "gpt-realtime-2.1", models: [] } };
        expect(defaultModelId(zeroOptions)).toBe("");
    });
});

describe("resolveModelId", () => {
    it("trusts a stored id that's still a selectable option", () => {
        expect(resolveModelId("phi-4", MODELS)).toBe("phi-4");
    });

    it("falls back to the persona's default when the stored id is stale or missing", () => {
        expect(resolveModelId("no-longer-offered", MODELS)).toBe("gpt-realtime-2.1");
        expect(resolveModelId(null, MODELS)).toBe("gpt-realtime-2.1");
        expect(resolveModelId(undefined, MODELS)).toBe("gpt-realtime-2.1");
    });

    // Rick's PR 134 review, item 1: with zero selectable options, nothing is ever trusted --
    // not even a stored id that happens to match `models.realtime.default` -- because it isn't
    // one of the (zero) selectable options either. Resolves to `""`, so `useRealTime` sends no
    // `?model=` param at all and the server's own default applies.
    it("resolves to an empty string (sends no ?model=) when there are zero selectable options", () => {
        const zeroOptions: PersonaModels = { realtime: { default: "gpt-realtime-2.1", models: [] } };
        expect(resolveModelId("gpt-realtime-2.1", zeroOptions)).toBe("");
        expect(resolveModelId(null, zeroOptions)).toBe("");
    });
});

describe("modelStorageKey", () => {
    it("namespaces the localStorage key by persona id (unlike the un-namespaced voiceChoice key)", () => {
        expect(modelStorageKey("test-alpha")).toBe("modelChoice.test-alpha");
        expect(modelStorageKey("test-beta")).toBe("modelChoice.test-beta");
    });
});
