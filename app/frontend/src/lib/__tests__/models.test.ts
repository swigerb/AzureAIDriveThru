import { describe, expect, it } from "vitest";

import { defaultModelId, flattenModelOptions, modelStorageKey, resolveModelId, selectableModelGroups } from "../models";
import type { PersonaModels } from "@/types/persona";

// Issue #80 F10's pure helpers (design doc §7's `/api/personas/{id}` `models` block). Covered
// separately from `model-picker.test.tsx` because the grouping/default/persistence rules here
// (local pipeline exclusion, stale-default fallback, per-persona storage keys) are worth guarding
// independent of any particular rendering.

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
    },
    local: {
        default: "phi-4-mini-local",
        models: [{ id: "phi-4-mini-local", label: "Phi-4 Mini (local)", reasoning: false }]
    }
};

describe("selectableModelGroups", () => {
    it("groups realtime and cascade models, excluding local (issue #81/F12, out of scope)", () => {
        const groups = selectableModelGroups(MODELS);
        expect(groups.map(g => g.pipeline)).toEqual(["realtime", "cascade"]);
        expect(groups[0].label).toBe("Realtime");
        expect(groups[1].label).toBe("Cascade");
    });

    it("falls back to a single synthetic option for a pipeline missing its models[] list", () => {
        const bareCascade: PersonaModels = { realtime: MODELS.realtime, cascade: { default: "gpt-5-mini" } };
        const groups = selectableModelGroups(bareCascade);
        expect(groups[1].options).toEqual([{ id: "gpt-5-mini", label: "gpt-5-mini", reasoning: false }]);
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
});

describe("modelStorageKey", () => {
    it("namespaces the localStorage key by persona id (unlike the un-namespaced voiceChoice key)", () => {
        expect(modelStorageKey("test-alpha")).toBe("modelChoice.test-alpha");
        expect(modelStorageKey("test-beta")).toBe("modelChoice.test-beta");
    });
});
