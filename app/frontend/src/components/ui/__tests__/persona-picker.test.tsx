import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import PersonaPicker from "../persona-picker";
import type { PersonaSummary } from "@/types/persona";

// Issue #80 F1 (design doc §5.1/§9 row F1). These tests guard the picker's accessibility contract
// (a labeled, keyboard-operable native <select>). Issue #180 removed the picker's own
// disabled/locked state -- it's now always enabled, with App.tsx's PersonaSwitchConfirmDialog
// gating mid-order/mid-conversation switches instead -- so the lock-specific cases that used to
// live here no longer apply; see App.personaSwitch.test.tsx for the confirm-dialog coverage.

const PERSONAS: PersonaSummary[] = [
    { id: "test-beta", displayName: "Test Beta", logoUrl: "/personas/test-beta/assets/logo.svg", theme: { light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 100%", foreground: "0 0% 0%" } } },
    { id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme: { light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 100%", foreground: "0 0% 0%" } } }
];

describe("PersonaPicker", () => {
    it("labels the select for both sighted and screen-reader users", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} />);

        // aria-label on the control itself, plus a visible <label htmlFor> pointing at it --
        // either alone would satisfy a screen reader, but the component supplies both.
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;
        expect(select.tagName).toBe("SELECT");
        expect(screen.getByText("Persona")).toBeInTheDocument();
    });

    it("lists every persona as an option, selecting the current one", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-alpha" onSelect={() => {}} />);
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;

        expect(screen.getAllByRole("option").map(o => (o as HTMLOptionElement).value)).toEqual(["test-beta", "test-alpha"]);
        expect(select.value).toBe("test-alpha");
    });

    it("is keyboard-operable: selecting a new option calls onSelect with its id", async () => {
        const onSelect = vi.fn();
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={onSelect} />);

        const select = screen.getByLabelText("Select persona");
        await userEvent.selectOptions(select, "test-alpha");

        expect(onSelect).toHaveBeenCalledWith("test-alpha");
    });

    it("is never disabled, regardless of session/order state (issue #180: picker stays enabled; a confirm dialog gates the switch instead)", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} />);
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;

        expect(select).not.toBeDisabled();
        expect(select).not.toHaveAttribute("aria-describedby");
        expect(screen.queryByText("Locked for this order -- start a new order to switch")).not.toBeInTheDocument();
    });
});
