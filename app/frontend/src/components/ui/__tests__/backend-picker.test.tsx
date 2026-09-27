import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import BackendPicker from "../backend-picker";
import type { PersonaBackendEntry } from "@/types/persona";

// Issue #80 F11 (design doc §9 row F11, §10.1's "Option A: two container apps, header switch
// navigates between hostnames, no proxy", ADR-001 decision 2). These tests guard: the picker
// hides itself entirely below two `backends[]` entries, detects the current backend by matching
// `window.location.origin`, and builds its navigation target explicitly from `personaId`/
// `modelId` (rather than trusting the address bar to already carry them).

const TWO_BACKENDS: PersonaBackendEntry[] = [
    { id: "python", url: "" },
    { id: "dotnet", url: "https://dotnet.example.com" }
];

function stubLocation(href: string) {
    const url = new URL(href);
    // `URL`'s properties (origin, href, pathname, ...) are prototype getters, not own
    // enumerable properties, so a plain `{ ...url }` spread silently drops them all --
    // list the ones `currentBackendId`/the picker actually reads explicitly instead.
    vi.stubGlobal("location", {
        origin: url.origin,
        href: url.href,
        search: url.search,
        assign: vi.fn()
    });
}

afterEach(() => {
    vi.unstubAllGlobals();
});

describe("BackendPicker", () => {
    beforeEach(() => {
        stubLocation("https://python.example.com/");
    });

    it("renders nothing when there is only one backend", () => {
        const { container } = render(
            <BackendPicker backends={[{ id: "python", url: "" }]} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />
        );
        expect(container).toBeEmptyDOMElement();
    });

    it("renders nothing when there are no backends at all", () => {
        const { container } = render(<BackendPicker backends={[]} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />);
        expect(container).toBeEmptyDOMElement();
    });

    it("shows a Python / C# (.NET) switch once two backends are present, labeling the select", () => {
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />);

        const select = screen.getByLabelText("Select backend") as HTMLSelectElement;
        expect(select.tagName).toBe("SELECT");
        expect(screen.getAllByRole("option").map(o => (o as HTMLOptionElement).textContent)).toEqual(["Python", "C# (.NET)"]);
    });

    it("selects the backend whose url origin matches window.location.origin", () => {
        stubLocation("https://dotnet.example.com/app?persona=test-alpha");
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />);

        expect((screen.getByLabelText("Select backend") as HTMLSelectElement).value).toBe("dotnet");
    });

    it("treats an empty-string backend url as 'this origin' when no other entry matches", () => {
        stubLocation("https://python.example.com/");
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />);

        expect((screen.getByLabelText("Select backend") as HTMLSelectElement).value).toBe("python");
    });

    it("navigates to the other backend's URL with the current persona and model preserved", async () => {
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={false} />);

        await userEvent.selectOptions(screen.getByLabelText("Select backend"), "dotnet");

        expect(window.location.assign).toHaveBeenCalledTimes(1);
        const target = new URL((window.location.assign as ReturnType<typeof vi.fn>).mock.calls[0][0] as string);
        expect(target.origin).toBe("https://dotnet.example.com");
        expect(target.searchParams.get("persona")).toBe("test-alpha");
        expect(target.searchParams.get("model")).toBe("gpt-realtime-2.1");
    });

    it("disables the control during an active session (ADR-001 decision 2)", () => {
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={true} />);
        expect(screen.getByLabelText("Select backend")).toBeDisabled();
    });

    it("exposes the lock reason to screen readers via aria-describedby", () => {
        render(<BackendPicker backends={TWO_BACKENDS} personaId="test-alpha" modelId="gpt-realtime-2.1" disabled={true} />);
        const select = screen.getByLabelText("Select backend");

        const describedById = select.getAttribute("aria-describedby");
        expect(describedById).toBeTruthy();
        expect(document.getElementById(describedById!)).toHaveTextContent("Locked for this order -- start a new order to switch");
    });
});
