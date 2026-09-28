import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import ModelPicker from "../model-picker";
import type { PersonaModels } from "@/types/persona";

// Issue #80 F10 (design doc §7/§9 row F10, ADR-001 decision 2: locked while a session is active).
// These tests guard the picker's grouping-by-pipeline, reasoning badge, and the same
// disabled/locked accessibility contract `persona-picker.test.tsx` guards for PersonaPicker.

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

describe("ModelPicker", () => {
    it("labels the select for both sighted and screen-reader users", () => {
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={false} />);

        const select = screen.getByLabelText("Select model") as HTMLSelectElement;
        expect(select.tagName).toBe("SELECT");
        // react-i18next is globally mocked (test/setup.ts) to echo the key -- "picker.modelLabel"
        // is the real English value ("Model") once i18next renders it for real (issue #80 F10,
        // Rick's PR 134 review nit: the picker's own strings are translated, not hard-coded).
        expect(screen.getByText("picker.modelLabel")).toBeInTheDocument();
    });

    it("groups models by pipeline into a Realtime and a Cascade optgroup", () => {
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={false} />);
        const select = screen.getByLabelText("Select model");

        const groups = within(select)
            .getAllByRole("group")
            .map(group => group.getAttribute("label"));
        expect(groups).toEqual(["picker.realtimeGroup", "picker.cascadeGroup"]);

        const realtimeGroup = within(select).getAllByRole("group")[0];
        expect(within(realtimeGroup).getAllByRole("option").map(o => (o as HTMLOptionElement).value)).toEqual([
            "gpt-realtime-2.1",
            "gpt-realtime-mini"
        ]);
        const cascadeGroup = within(select).getAllByRole("group")[1];
        expect(within(cascadeGroup).getAllByRole("option").map(o => (o as HTMLOptionElement).value)).toEqual(["gpt-5-mini", "phi-4"]);
    });

    it("excludes the local pipeline entirely (issue #81/F12, out of this task's scope)", () => {
        const withLocal: PersonaModels = { ...MODELS, local: { default: "phi-4-mini-local", models: [{ id: "phi-4-mini-local", label: "Phi-4 Mini (local)", reasoning: false }] } };
        render(<ModelPicker models={withLocal} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={false} />);
        const select = screen.getByLabelText("Select model");

        expect(within(select).queryByText(/local/i)).not.toBeInTheDocument();
        expect(within(select).getAllByRole("group")).toHaveLength(2);
    });

    it("shows a reasoning badge/suffix only for models flagged reasoning: true", () => {
        render(<ModelPicker models={MODELS} currentId="phi-4" onSelect={() => {}} disabled={false} />);
        const select = screen.getByLabelText("Select model");

        const phi4 = within(select).getByRole("option", { name: /Phi-4 picker\.reasoningSuffix/ }) as HTMLOptionElement;
        expect(phi4.value).toBe("phi-4");
        const gpt5mini = within(select).getByRole("option", { name: "GPT-5 Mini" }) as HTMLOptionElement;
        expect(gpt5mini.value).toBe("gpt-5-mini");
        expect(within(select).queryByRole("option", { name: /GPT-5 Mini picker\.reasoningSuffix/ })).not.toBeInTheDocument();
    });

    it("is keyboard-operable: selecting a new option calls onSelect with its id", async () => {
        const onSelect = vi.fn();
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={onSelect} disabled={false} />);

        await userEvent.selectOptions(screen.getByLabelText("Select model"), "gpt-5-mini");

        expect(onSelect).toHaveBeenCalledWith("gpt-5-mini");
    });

    it("disables the control during an active session (ADR-001 decision 2: no mid-conversation switching)", () => {
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={true} />);
        expect(screen.getByLabelText("Select model")).toBeDisabled();
    });

    it("exposes the lock reason to screen readers via aria-describedby, not just a hover tooltip", () => {
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={true} />);
        const select = screen.getByLabelText("Select model");

        const describedById = select.getAttribute("aria-describedby");
        expect(describedById).toBeTruthy();
        expect(document.getElementById(describedById!)).toHaveTextContent("picker.lockedHint");
    });

    it("does not describe the enabled control with a lock reason", () => {
        render(<ModelPicker models={MODELS} currentId="gpt-realtime-2.1" onSelect={() => {}} disabled={false} />);
        expect(screen.getByLabelText("Select model")).not.toHaveAttribute("aria-describedby");
    });

    // Rick's PR 134 review, item 1: `selectableModelGroups` never invents an option for a
    // pipeline's unlisted default anymore -- re-adding that fallback must fail every test in this
    // block, since the picker would then render a selectable (and pickable) option again instead
    // of the disabled "no models available" state.
    describe("zero selectable options (issue #80, Rick's PR 134 review item 1)", () => {
        const NO_MODELS: PersonaModels = { realtime: { default: "gpt-realtime-2.1", models: [] } };

        it("never falls back to a synthetic option for a pipeline missing its models[] list", () => {
            const bareRealtimeOnly: PersonaModels = { realtime: { default: "gpt-realtime-2.1" } };
            render(<ModelPicker models={bareRealtimeOnly} currentId="" onSelect={() => {}} disabled={false} />);
            const select = screen.getByLabelText("Select model") as HTMLSelectElement;

            expect(within(select).queryAllByRole("option").map(o => (o as HTMLOptionElement).value)).toEqual([""]);
            expect(within(select).queryByRole("group")).not.toBeInTheDocument();
        });

        it("renders a disabled 'no models available' option instead of an empty, unusable select", () => {
            render(<ModelPicker models={NO_MODELS} currentId="" onSelect={() => {}} disabled={false} />);
            const select = screen.getByLabelText("Select model") as HTMLSelectElement;

            expect(select).toBeDisabled();
            expect(within(select).getByText("picker.noModelsAvailable")).toBeInTheDocument();
        });

        it("never calls onSelect, since the control is disabled with nothing selectable", () => {
            const onSelect = vi.fn();
            render(<ModelPicker models={NO_MODELS} currentId="" onSelect={onSelect} disabled={false} />);

            expect(screen.getByLabelText("Select model")).toBeDisabled();
            expect(onSelect).not.toHaveBeenCalled();
        });
    });
});
