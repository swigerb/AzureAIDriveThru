import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// Rick's PR-110 review item 5 (issue #80 F7): "Persona switch while idle must clear the previous
// persona's ticket, transcript and demo data; resume key per persona." The resume-key half is
// covered by useRealtime.personaResume.test.tsx -- this file covers App()'s
// handleSelectPersona/useDemoData clearing, exercised through the real <PersonaPicker /> control.

const rt = vi.hoisted(() => ({
    params: null as any,
    api: {
        startSession: vi.fn(),
        addUserAudio: vi.fn(),
        inputAudioBufferClear: vi.fn(),
        cancelResponse: vi.fn(),
        sendVerboseLogging: vi.fn(),
        sendLogToFile: vi.fn(),
        sendVoiceChoice: vi.fn(),
        endSession: vi.fn(),
        reconnect: vi.fn(async () => {}),
        cancelSwitch: vi.fn(),
        isConnected: true
    }
}));
const rec = vi.hoisted(() => ({ start: vi.fn(async () => true), stop: vi.fn(async () => {}), mute: vi.fn(), unmute: vi.fn() }));
const player = vi.hoisted(() => ({ reset: vi.fn(async () => {}), play: vi.fn(), stop: vi.fn(), waitForDrain: vi.fn(async () => true) }));

vi.mock("@/hooks/useRealtime", () => ({
    default: (params: any) => {
        rt.params = params;
        return rt.api;
    }
}));
vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useAudioRecorder", () => ({ default: () => rec }));
vi.mock("@/hooks/useAudioPlayer", () => ({ default: () => player }));

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };
const FIXTURE_PERSONA_INDEX = {
    default: "test-alpha",
    personas: [
        { id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme },
        { id: "test-beta", displayName: "Test Beta", logoUrl: "/personas/test-beta/assets/logo.svg", theme }
    ],
    backends: []
};
const detailFor = (id: string, title: string) => ({
    id,
    title,
    theme,
    assets: { logo: `assets/logo.svg`, favicon: "assets/favicon.ico" },
    strings: { en: {} },
    hero: { headline: `${title} fixture pack`, description: "Fixture description.", callouts: [], spotlight: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en"] },
    features: { dayparts: false },
    menuUrl: `/personas/${id}/menu.json`,
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } },
    taxRate: "0.08"
});
const DETAIL_ALPHA = detailFor("test-alpha", "Test Alpha Fixture");
const DETAIL_BETA = detailFor("test-beta", "Test Beta Fixture");

const demoOrderFor = (label: string) => [{ item: label, size: "Regular", quantity: 1, price: 1.5, display: label }];
const demoTranscriptFor = (label: string) => [{ text: label, isUser: false, timestamp: "2026-01-01T00:00:00.000Z" }];

function mockPersonaFetch() {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
            if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
            if (url === "/api/personas/test-beta") return { ok: true, status: 200, json: async () => DETAIL_BETA };
            if (url === "/personas/test-alpha/assets/demo/dummyOrder.json") return { ok: true, status: 200, json: async () => demoOrderFor("Alpha Combo") };
            if (url === "/personas/test-alpha/assets/demo/dummyTranscripts.json") return { ok: true, status: 200, json: async () => demoTranscriptFor("Alpha demo line") };
            if (url === "/personas/test-beta/assets/demo/dummyOrder.json") return { ok: true, status: 200, json: async () => demoOrderFor("Beta Combo") };
            if (url === "/personas/test-beta/assets/demo/dummyTranscripts.json") return { ok: true, status: 200, json: async () => demoTranscriptFor("Beta demo line") };
            return { ok: false, status: 404, json: async () => ({}) };
        })
    );
}

const answer = (transcript: string) => ({ type: "response.done" as const, response: { output: [{ content: [{ transcript }] }] } });

const tapMic = async () => {
    const micButton = await screen.findByLabelText(/app\.(start|stop)Recording/);
    await act(async () => {
        fireEvent.click(micButton);
    });
};

const switchTo = async (personaId: string) => {
    const select = await screen.findByLabelText("Select persona");
    await userEvent.selectOptions(select, personaId);
};

const orderSummaryWith = (label: string) => ({
    items: [{ item: label, size: "Regular", quantity: 1, price: 1.5, display: label }],
    total: 1.5,
    tax: 0.12,
    finalTotal: 1.62
});

// Puts a real (non-dummy) item on the ticket via the same realtime callback the backend's
// update_order tool response drives (see App.tsx's onReceivedExtensionMiddleTierToolResponse).
// Waits for the app to finish its initial persona-fetch render first, since `rt.params` isn't
// populated until useRealTime's mock has actually been invoked.
const addOrderItem = async (label: string) => {
    await screen.findByLabelText("Select persona");
    act(() => rt.params.onReceivedExtensionMiddleTierToolResponse({ tool_name: "update_order", tool_result: JSON.stringify(orderSummaryWith(label)) }));
};


beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    rec.start.mockImplementation(async () => true);
    localStorage.clear();
    mockPersonaFetch();
});

describe("persona switch clears state (issue #80 F7)", () => {
    it("clears the previous persona's transcript once idle", async () => {
        render(<RootApp />);
        await tapMic(); // start recording
        act(() => rt.params.onReceivedResponseDone(answer("Alpha greeting text")));
        expect(screen.getByText("Alpha greeting text")).toBeInTheDocument();

        await tapMic(); // stop recording -- the picker only unlocks once idle with an empty ticket
        await switchTo("test-beta");

        await waitFor(() => expect(screen.queryByText("Alpha greeting text")).not.toBeInTheDocument());
        expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta");
    });

    it("ends any lingering realtime session on switch", async () => {
        render(<RootApp />);
        await tapMic();
        await tapMic();
        await switchTo("test-beta");
        expect(rt.api.endSession).toHaveBeenCalled();
    });

    it("clears the previous persona's demo order and transcript (Dummy Data mode)", async () => {
        localStorage.setItem("useDummyData", "true");
        render(<RootApp />);
        await screen.findByText("Alpha Combo");
        await screen.findByText("Alpha demo line");

        await switchTo("test-beta");

        await waitFor(() => expect(screen.queryByText("Alpha Combo")).not.toBeInTheDocument());
        await waitFor(() => expect(screen.queryByText("Alpha demo line")).not.toBeInTheDocument());
        await screen.findByText("Beta Combo");
        await screen.findByText("Beta demo line");
    });
});

// Issue GH-171 round 4, item 2: every test above already mocks `cancelSwitch: vi.fn()` without
// ever asserting on it, and only checks `endSession` with `toHaveBeenCalled()` (no argument
// check). That let two mutations survive the round-3 suite entirely: M4 (handleSelectPersona
// never calling `realtime.cancelSwitch()` on a failed switch) and M5 (handleSelectPersona calling
// `realtime.endSession()` without `{ switching: true }`). These tests exercise
// useRealtime.tsx's App-facing contract directly through the real <PersonaPicker /> control.
describe("App.tsx's handleSelectPersona: realtime wiring (issue GH-171 round 4, item 2)", () => {
    it("tells useRealtime a switch is under way with endSession({ switching: true }) -- not a bare endSession() (kills mutation M5)", async () => {
        render(<RootApp />);
        await switchTo("test-beta");

        expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true });
        // Guard against a mutation that drops the argument object's shape rather than the whole
        // call -- `switching` must specifically be `true`, not merely truthy/present-by-accident.
        const calls = rt.api.endSession.mock.calls;
        const call = calls[calls.length - 1];
        expect(call?.[0]).toStrictEqual({ switching: true });
    });

    it("calls realtime.cancelSwitch() exactly once when the persona detail fetch fails (kills mutation M4)", async () => {
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                // The switched-to persona's own detail fetch fails: selectPersona() resolves false.
                if (url === "/api/personas/test-beta") return { ok: false, status: 500, json: async () => ({}) };
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);

        await switchTo("test-beta");

        await waitFor(() => expect(rt.api.cancelSwitch).toHaveBeenCalledTimes(1));
        // The picker itself falls back to reflecting the persona that is actually still live.
        expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-alpha");
    });

    it("does NOT call realtime.cancelSwitch() when the persona switch succeeds", async () => {
        render(<RootApp />);
        await switchTo("test-beta");

        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
        expect(rt.api.cancelSwitch).not.toHaveBeenCalled();
    });
});

// Issue #180: the picker used to lock itself outright ("Locked for this order -- start a new
// order to switch") once the ticket had items or a conversation was active. It's always enabled
// now -- these cases guard the new gate: immediate switch with nothing to lose, a confirmation
// dialog otherwise, Cancel changing nothing, Switch performing the exact same clean switch as
// the idle path, and the dialog's own keyboard/a11y contract (Escape cancels, focus stays
// trapped inside it while open).
describe("persona switch confirmation dialog (issue #180)", () => {
    it("switches immediately, with no dialog, when the order is empty and no conversation is active", async () => {
        render(<RootApp />);
        await switchTo("test-beta");

        expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
        await waitFor(() => expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true }));
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
    });

    it("prompts for confirmation instead of switching when the order has items", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");

        await switchTo("test-beta");

        expect(await screen.findByRole("dialog")).toBeInTheDocument();
        expect(screen.getByText(/Switching to Test Beta will start a new order\. Your current order will be cleared\./)).toBeInTheDocument();
        // Nothing has actually happened yet -- the switch is only pending confirmation.
        expect(rt.api.endSession).not.toHaveBeenCalled();
        expect(screen.getByText("Alpha Combo")).toBeInTheDocument();
    });

    it("prompts for confirmation instead of switching mid-conversation, even with an empty order", async () => {
        render(<RootApp />);
        await tapMic(); // start recording, order still empty

        await switchTo("test-beta");

        expect(await screen.findByRole("dialog")).toBeInTheDocument();
        expect(rt.api.endSession).not.toHaveBeenCalled();
    });

    it("Cancel changes nothing: the switch is abandoned, the ticket survives, and the picker still shows the current persona", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.click(screen.getByRole("button", { name: "Cancel" }));

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        expect(rt.api.endSession).not.toHaveBeenCalled();
        expect(rt.api.cancelSwitch).not.toHaveBeenCalled();
        expect(screen.getByText("Alpha Combo")).toBeInTheDocument();
        expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-alpha");
    });

    it("Switch confirms the switch: ends the session, clears the old order/transcript, and lands on the new persona", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        act(() => rt.params.onReceivedResponseDone(answer("Alpha greeting text")));
        await screen.findByText("Alpha greeting text");

        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.click(screen.getByRole("button", { name: "Switch" }));

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true });
        // The old persona's order and transcript must never leak into the new one.
        await waitFor(() => expect(screen.queryByText("Alpha Combo")).not.toBeInTheDocument());
        await waitFor(() => expect(screen.queryByText("Alpha greeting text")).not.toBeInTheDocument());
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
        expect(rt.api.cancelSwitch).not.toHaveBeenCalled();

        // The new persona's own greeting path works exactly as it would on a fresh load -- a
        // transcript delivered after the switch lands under test-beta with no residue.
        act(() => rt.params.onReceivedResponseDone(answer("Beta greeting text")));
        expect(screen.getByText("Beta greeting text")).toBeInTheDocument();
    });

    it("is keyboard-accessible: Escape cancels exactly like clicking Cancel", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.keyboard("{Escape}");

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        expect(rt.api.endSession).not.toHaveBeenCalled();
        expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-alpha");
    });

    it("traps focus inside the dialog while it is open", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        await switchTo("test-beta");
        const dialog = await screen.findByRole("dialog");

        await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

        // Tabbing forward from the last focusable control must stay inside the dialog, not
        // escape to the page behind it -- Radix's FocusScope wraps focus rather than releasing it.
        await userEvent.tab();
        expect(dialog.contains(document.activeElement)).toBe(true);
        await userEvent.tab();
        expect(dialog.contains(document.activeElement)).toBe(true);
    });
});

