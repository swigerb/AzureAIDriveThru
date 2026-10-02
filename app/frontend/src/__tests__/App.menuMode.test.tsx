import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// R12 (Rick's #166 review round 2, required item 12): exercised end-to-end through the real
// App() wiring, the same way App.modelPersistence.test.tsx's "locks the model picker during an
// active session" covers the model picker's lock. Mutating App.tsx's
// `menuModeDisabled={isRecording || order.items.length > 0}` to `menuModeDisabled={false}` left
// vitest fully green (561/561) because only the `Settings` component itself was tested in
// isolation -- nothing exercised the prop App.tsx actually passes in. This file closes that gap,
// and also pins that `useRealTime` is given the persona's own stored (or default) menu mode, not
// an unbound one, for a persona that declares `features.dayparts`, and the empty string for one
// that doesn't.

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
    backends: [{ id: "python", url: "" }]
};
const detailFor = (id: string, title: string, dayparts: boolean) => ({
    id,
    title,
    theme,
    assets: { logo: `assets/logo.svg`, favicon: "assets/favicon.ico" },
    strings: { en: {} },
    hero: { headline: `${title} fixture pack`, description: "Fixture description.", callouts: [], spotlight: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en"] },
    features: { dayparts },
    menuUrl: `/personas/${id}/menu.json`,
    models: {
        realtime: {
            default: "gpt-realtime-2.1",
            models: [{ id: "gpt-realtime-2.1", label: `${title} Realtime 2.1`, reasoning: false }]
        },
        cascade: { default: "gpt-5-mini", models: [{ id: "gpt-5-mini", label: `${title} Cascade Mini`, reasoning: true }] }
    }
});
// test-alpha declares the dayparts feature (so the Menu Mode toggle renders at all); test-beta
// doesn't, mirroring a real pack that never opts into it.
const DETAIL_ALPHA = detailFor("test-alpha", "Test Alpha", true);
const DETAIL_BETA = detailFor("test-beta", "Test Beta", false);

function mockPersonaFetch() {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
            if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
            if (url === "/api/personas/test-beta") return { ok: true, status: 200, json: async () => DETAIL_BETA };
            return { ok: false, status: 404, json: async () => ({}) };
        })
    );
}

const switchTo = async (personaId: string) => {
    const select = await screen.findByLabelText("Select persona");
    await userEvent.selectOptions(select, personaId);
};

const openSettings = async () => {
    await screen.findByLabelText("Select persona"); // wait past the initial "Loading..." screen
    await userEvent.click(await screen.findByRole("button", { name: /open settings/i })); // Settings is lazy-loaded
};

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    HTMLDialogElement.prototype.showModal ??= vi.fn();
    HTMLDialogElement.prototype.close ??= vi.fn();
    rec.start.mockImplementation(async () => true);
    localStorage.clear();
    window.history.pushState({}, "", "/");
    mockPersonaFetch();
});

describe("menu mode lock wiring (R12, Rick's #166 review round 2)", () => {
    it("leaves the Menu Mode radios enabled before a session starts, and locks them once one is live", async () => {
        render(<RootApp />);
        await openSettings();

        const breakfastRadio = await screen.findByRole("radio", { name: /breakfast/i });
        const lunchRadio = screen.getByRole("radio", { name: /lunch/i });
        expect(breakfastRadio).not.toBeDisabled();
        expect(lunchRadio).not.toBeDisabled();

        const micButton = await screen.findByLabelText(/app\.(start|stop)Recording/);
        await act(async () => {
            fireEvent.click(micButton);
        });

        await waitFor(() => expect(screen.getByRole("radio", { name: /breakfast/i })).toBeDisabled());
        expect(screen.getByRole("radio", { name: /lunch/i })).toBeDisabled();
    });

    it("locks the Menu Mode radios once the order already has an item, even without a live recording", async () => {
        render(<RootApp />);
        await openSettings();
        expect(screen.getByRole("radio", { name: /breakfast/i })).not.toBeDisabled();

        // Drives the same `order.items.length > 0` branch App.tsx's lock expression checks,
        // through the real order-state wiring (the tool-response handler useRealTime calls on an
        // `update_order` result) rather than reaching into React internals.
        await act(async () => {
            rt.params.onReceivedExtensionMiddleTierToolResponse({
                tool_name: "update_order",
                tool_result: JSON.stringify({
                    items: [{ item: "Test Item", size: "Standard", quantity: 1, price: 1, display: "Test Item" }],
                    total: 1,
                    tax: 0,
                    finalTotal: 1
                })
            });
        });

        await waitFor(() => expect(screen.getByRole("radio", { name: /breakfast/i })).toBeDisabled());
    });

    it("threads the persisted (or default) menu mode into useRealTime for a dayparts persona", async () => {
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        await waitFor(() => expect(rt.params.menuMode).toBe("lunch"));
    });

    it("recalls a dayparts persona's previously-stored menu mode choice", async () => {
        localStorage.setItem("menuModeChoice.test-alpha", "breakfast");
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        await waitFor(() => expect(rt.params.menuMode).toBe("breakfast"));
    });

    it("threads an unbound empty string into useRealTime for a persona without the dayparts feature", async () => {
        render(<RootApp />);
        await waitFor(() => expect(rt.params.menuMode).toBe("lunch"));

        await switchTo("test-beta");

        await waitFor(() => expect(rt.params.menuMode).toBe(""));
    });
});
