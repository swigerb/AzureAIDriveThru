import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import PersonaPicker from "../persona-picker";
import type { PersonaSummary } from "@/types/persona";

// Issue #80 F1 (design doc §5.1/§9 row F1, ADR-001 decision 2: one URL, per-session persona
// picker, no mid-conversation switching). These tests guard the picker's accessibility contract
// (a labeled, keyboard-operable native <select>) and the disabled/locked behavior a session in
// progress requires.

const PERSONAS: PersonaSummary[] = [
    { id: "test-beta", displayName: "Test Beta", logoUrl: "/personas/test-beta/assets/logo.svg", theme: { light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 100%", foreground: "0 0% 0%" } } },
    { id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme: { light: { primary: "0 0% 0%", secondary: "0 0% 0%", background: "0 0% 100%", foreground: "0 0% 0%" } } }
];

describe("PersonaPicker", () => {
    it("labels the select for both sighted and screen-reader users", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} disabled={false} />);

        // aria-label on the control itself, plus a visible <label htmlFor> pointing at it --
        // either alone would satisfy a screen reader, but the component supplies both.
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;
        expect(select.tagName).toBe("SELECT");
        expect(screen.getByText("Persona")).toBeInTheDocument();
    });

    it("lists every persona as an option, selecting the current one", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-alpha" onSelect={() => {}} disabled={false} />);
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;

        expect(screen.getAllByRole("option").map(o => (o as HTMLOptionElement).value)).toEqual(["test-beta", "test-alpha"]);
        expect(select.value).toBe("test-alpha");
    });

    it("is keyboard-operable: selecting a new option calls onSelect with its id", async () => {
        const onSelect = vi.fn();
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={onSelect} disabled={false} />);

        const select = screen.getByLabelText("Select persona");
        await userEvent.selectOptions(select, "test-alpha");

        expect(onSelect).toHaveBeenCalledWith("test-alpha");
    });

    it("disables the control during an active session (ADR-001 decision 2: no mid-conversation switching)", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} disabled={true} />);
        const select = screen.getByLabelText("Select persona") as HTMLSelectElement;

        expect(select).toBeDisabled();
    });

    it("exposes the lock reason to screen readers via aria-describedby, not just a hover tooltip", () => {
        // Non-blocking item from Rick's PR-110 review: the visible Radix Tooltip only reaches
        // sighted users who hover/focus the trigger, so the lock reason is duplicated into an
        // always-present sr-only element and wired up with aria-describedby for AT users too.
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} disabled={true} />);
        const select = screen.getByLabelText("Select persona");

        expect(select).toBeDisabled();
        const describedById = select.getAttribute("aria-describedby");
        expect(describedById).toBeTruthy();
        expect(document.getElementById(describedById!)).toHaveTextContent("Locked for this order -- start a new order to switch");
    });

    it("does not describe the enabled control with a lock reason", () => {
        render(<PersonaPicker personas={PERSONAS} currentId="test-beta" onSelect={() => {}} disabled={false} />);
        const select = screen.getByLabelText("Select persona");

        expect(select).not.toHaveAttribute("aria-describedby");
        expect(screen.queryByText("Locked for this order -- start a new order to switch")).not.toBeInTheDocument();
    });
});
