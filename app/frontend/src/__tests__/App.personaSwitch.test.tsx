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
        beginSwitch: vi.fn(),
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
        expect(screen.getByText("personaSwitch.body")).toBeInTheDocument();
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

        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.cancel" }));

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
        // onReceivedResponseDone is a no-op while isSessionActiveRef is false (issue 181's
        // defense-in-depth guard) -- a real reply can only ever arrive once the guest has
        // actually started a session, so the greeting needs a tap first just like production.
        await tapMic();
        act(() => rt.params.onReceivedResponseDone(answer("Alpha greeting text")));
        await screen.findByText("Alpha greeting text");

        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.confirm" }));

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true });
        // The old persona's order and transcript must never leak into the new one.
        await waitFor(() => expect(screen.queryByText("Alpha Combo")).not.toBeInTheDocument());
        await waitFor(() => expect(screen.queryByText("Alpha greeting text")).not.toBeInTheDocument());
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
        expect(rt.api.cancelSwitch).not.toHaveBeenCalled();

        // The new persona's own greeting path works exactly as it would on a fresh load -- a
        // transcript delivered after the switch lands under test-beta with no residue. A
        // completed switch stops the old conversation (R6), so the guest needs a fresh tap
        // before the new persona's session is considered active again, same as any fresh load.
        await tapMic();
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

// Issue GH-180 round 2, R3: Radix's own default autofocus-on-close targets a `Dialog.Trigger`,
// but this dialog has none (it's opened imperatively from requestPersonaSwitch, not by a trigger
// button) -- without persona-switch-confirm-dialog.tsx's own `onCloseAutoFocus` override, focus
// was dropped on `<body>` instead of returning anywhere useful.
describe("focus returns to the persona picker after the dialog closes (issue GH-180 round 2, R3)", () => {
    it("returns focus to the persona picker after Cancel", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.cancel" }));

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        await waitFor(() => expect(document.activeElement?.id).toBe("persona-picker"));
    });

    it("returns focus to the persona picker after Escape", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        await switchTo("test-beta");
        await screen.findByRole("dialog");

        await userEvent.keyboard("{Escape}");

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        await waitFor(() => expect(document.activeElement?.id).toBe("persona-picker"));
    });
});

// Issue GH-180 round 2, R1: the picker and the whole guest-facing UI used to tear down the old
// persona's order/transcript/resume id the INSTANT the guest clicked Switch, before the new
// persona's own detail fetch had even started -- a failed fetch then left the guest staring at a
// blank menu under neither persona's identity, with the old order gone for good. These cases pin
// down the fixed contract: fetch the target FIRST, only clear anything on success, and leave a
// failed switch exactly as if it had never been attempted (plus a visible, localized error).
describe("a failed persona switch leaves the current order, transcript and persona bound intact (issue GH-180 round 2, R1)", () => {
    it("keeps the old order/transcript, shows a localized error, and never calls endSession when the target persona's fetch fails", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");
        // onReceivedResponseDone is a no-op while isSessionActiveRef is false (issue 181's
        // defense-in-depth guard) -- a real reply can only ever arrive once the guest has
        // actually started a session, so the greeting needs a tap first just like production.
        await tapMic();
        act(() => rt.params.onReceivedResponseDone(answer("Alpha greeting text")));
        await screen.findByText("Alpha greeting text");

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

        await switchTo("test-beta");
        await screen.findByRole("dialog");
        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.confirm" }));

        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        await waitFor(() => expect(rt.api.cancelSwitch).toHaveBeenCalledTimes(1));
        // The old order and transcript must survive untouched -- this is the whole point of
        // fetching the target before clearing anything.
        expect(screen.getByText("Alpha Combo")).toBeInTheDocument();
        expect(screen.getByText("Alpha greeting text")).toBeInTheDocument();
        expect(rt.api.endSession).not.toHaveBeenCalled();
        expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-alpha");
        // R1: a visible, localized error -- not a silent failure.
        expect(await screen.findByText("personaSwitch.loadError")).toBeInTheDocument();
    });

    it("allows retry: picking the same persona again once its fetch recovers switches cleanly", async () => {
        render(<RootApp />);
        await addOrderItem("Alpha Combo");
        await screen.findByText("Alpha Combo");

        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") return { ok: false, status: 500, json: async () => ({}) };
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        await switchTo("test-beta");
        await screen.findByRole("dialog");
        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.confirm" }));
        await waitFor(() => expect(rt.api.cancelSwitch).toHaveBeenCalledTimes(1));
        expect(screen.getByText("Alpha Combo")).toBeInTheDocument();

        // The fetch recovers -- retrying the exact same pick must now succeed cleanly, with the
        // same picker value, logo and localStorage all landing in agreement.
        mockPersonaFetch();
        await switchTo("test-beta");
        await screen.findByRole("dialog");
        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.confirm" }));

        await waitFor(() => expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true }));
        await waitFor(() => expect(screen.queryByText("Alpha Combo")).not.toBeInTheDocument());
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
        expect(localStorage.getItem("personaId")).toBe("test-beta");
    });
});

// Issue GH-180 round 2, R4: a second pick landing while the first switch's target-persona fetch
// is still resolving used to re-enter handleSelectPersona a second time. Against an unchanged
// `current.id`, persona-context's own selectPersona() resolves `false` for that second call (no
// real change to apply), which then called `realtime.cancelSwitch()` against a switch that was
// still genuinely in flight -- reopening a socket for the persona being switched AWAY from.
describe("a second pick while a switch is still resolving is ignored until it settles (issue GH-180 round 2, R4)", () => {
    it("ignores a second pick made while the first switch's persona fetch is still pending, and does not reopen the old persona's socket", async () => {
        let resolveBetaFetch: () => void = () => {};
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") {
                    await new Promise<void>(resolve => {
                        resolveBetaFetch = resolve;
                    });
                    return { ok: true, status: 200, json: async () => DETAIL_BETA };
                }
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);

        // Empty order, no active conversation -- switches immediately, with the target's fetch
        // left deliberately hanging.
        await switchTo("test-beta");
        // The picker reflects the pending target while the switch resolves.
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));

        // A second pick lands while the first switch is still in flight.
        await switchTo("test-alpha");

        resolveBetaFetch();
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));

        expect(rt.api.endSession).toHaveBeenCalledTimes(1);
        expect(rt.api.cancelSwitch).not.toHaveBeenCalled();
    });

    // Rick round 2, R4: the test above's "second pick" targets test-alpha -- current.id itself,
    // which requestPersonaSwitch's OWN, unrelated `personaId === current.id` guard already
    // short-circuits regardless of switchInFlightRef. That made it possible to delete the
    // switchInFlightRef gate entirely and still pass every test in this file (mutation survived).
    // This case picks the SAME in-flight target instead (test-beta again, which is neither
    // current.id nor blocked by anything else) so only switchInFlightRef can prevent a second,
    // concurrent handleSelectPersona()/fetch/endSession() for it.
    it("ignores a second pick of the SAME in-flight target: exactly one fetch and one endSession (kills removing switchInFlightRef)", async () => {
        let betaFetchCount = 0;
        const betaResolvers: Array<() => void> = [];
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") {
                    betaFetchCount += 1;
                    await new Promise<void>(resolve => {
                        betaResolvers.push(resolve);
                    });
                    return { ok: true, status: 200, json: async () => DETAIL_BETA };
                }
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);

        await switchTo("test-beta");
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));
        expect(betaFetchCount).toBe(1);

        // A second pick for the exact same target, still in flight.
        await switchTo("test-beta");
        expect(betaFetchCount).toBe(1);

        betaResolvers.forEach(resolve => resolve());
        await waitFor(() => expect(rt.api.endSession).toHaveBeenCalledTimes(1));
        expect(betaFetchCount).toBe(1);
    });
});

// Issue GH-180 round 2, R6: `void stopConversation()` let the recorder keep streaming mic audio
// for at least one more tick after `end_session` had already gone out, and nothing in the
// pre-round-2 suite ever asserted the recorder had actually stopped by the time the switch
// continued -- a mutation that deleted the whole `if (isSessionActiveRef.current) ...
// stopConversation();` line entirely survived undetected.
describe("Switch stops an active conversation before continuing (issue GH-180 round 2, R6)", () => {
    it("awaits stopConversation() so the recorder is fully stopped before endSession() runs", async () => {
        let resolveStop: () => void = () => {};
        rec.stop.mockImplementation(
            () =>
                new Promise<void>(resolve => {
                    resolveStop = resolve;
                })
        );
        render(<RootApp />);
        await tapMic(); // start recording -- isSessionActiveRef becomes true, order still empty

        await switchTo("test-beta"); // mid-conversation -- prompts for confirmation
        await screen.findByRole("dialog");
        await userEvent.click(screen.getByRole("button", { name: "personaSwitch.confirm" }));

        // stopConversation() is still in flight (rec.stop() hasn't resolved) -- a `void
        // stopConversation()` mutation would let endSession() through right away regardless.
        await Promise.resolve();
        await Promise.resolve();
        expect(rec.stop).toHaveBeenCalled();
        expect(rt.api.endSession).not.toHaveBeenCalled();

        resolveStop();
        await waitFor(() => expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true }));
    });
});

// Issue GH-180 round 3, R8 (REQUIRED): a mic tap while the new persona is still loading must not
// start the OLD persona's session -- its socket is still genuinely live at this point (endSession()
// only runs once the fetch succeeds, R7), so a naive tap would greet with the OLD persona moments
// before the switch replaces it out from under the guest. The tap must instead wait for the switch
// to settle, then start whichever persona actually ends up bound.
describe("A mic tap while a persona switch is still pending (issue GH-180 round 3, R8)", () => {
    it("defers the tap, then starts the NEW persona's session once the switch succeeds", async () => {
        let resolveBetaFetch: () => void = () => {};
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") {
                    await new Promise<void>(resolve => {
                        resolveBetaFetch = resolve;
                    });
                    return { ok: true, status: 200, json: async () => DETAIL_BETA };
                }
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);

        // Empty order, no active conversation -- switches immediately, fetch deliberately held.
        await switchTo("test-beta");
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));

        // The tap lands while the switch's own fetch is still held -- it must defer rather than
        // start a session on the still-live OLD (test-alpha) socket.
        await tapMic();
        await Promise.resolve();
        await Promise.resolve();
        expect(rt.api.startSession).not.toHaveBeenCalled();

        resolveBetaFetch();
        await waitFor(() => expect(rt.api.endSession).toHaveBeenCalledWith({ switching: true }));
        // The deferred tap is honored now that the switch has settled, targeting the NEW persona.
        await waitFor(() => expect(rt.api.startSession).toHaveBeenCalledTimes(1));
    });

    it("defers the tap, then starts the OLD persona's session again if the switch fails", async () => {
        let resolveBetaFetch: () => void = () => {};
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") {
                    await new Promise<void>(resolve => {
                        resolveBetaFetch = resolve;
                    });
                    // The switched-to persona's own detail fetch fails once released below.
                    return { ok: false, status: 500, json: async () => ({}) };
                }
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);

        await switchTo("test-beta");
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-beta"));

        await tapMic();
        await Promise.resolve();
        await Promise.resolve();
        expect(rt.api.startSession).not.toHaveBeenCalled();

        resolveBetaFetch();
        await waitFor(() => expect(rt.api.cancelSwitch).toHaveBeenCalledTimes(1));
        // The deferred tap is honored on the OLD persona's still-live socket, exactly as an
        // ordinary tap would have been -- its socket was never touched (endSession() only ever
        // runs once a switch's fetch succeeds).
        await waitFor(() => expect(rt.api.startSession).toHaveBeenCalledTimes(1));
        expect(rt.api.endSession).not.toHaveBeenCalled();
        // The picker snaps back to reflecting the persona that is actually still bound.
        await waitFor(() => expect((screen.getByLabelText("Select persona") as HTMLSelectElement).value).toBe("test-alpha"));
    });
});

