import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

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
        setMachineStatus: vi.fn(),
        setHappyHourMode: vi.fn(),
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

// Same persona-catalog fixture shape as App.resume.test.tsx, plus a declared machine and happy
// hour window so the Settings panel actually renders the toggles this suite needs to flip.
const FIXTURE_PERSONA_INDEX = {
    default: "test-alpha",
    personas: [
        { id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme: { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } } }
    ],
    backends: []
};
const FIXTURE_PERSONA_DETAIL = {
    id: "test-alpha",
    title: "Test Alpha Fixture",
    theme: FIXTURE_PERSONA_INDEX.personas[0].theme,
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
    strings: { en: {} },
    hero: { headline: "Test Alpha fixture pack", description: "Fixture description.", callouts: [], spotlight: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en", "es", "fr", "ja"] },
    features: { dayparts: false },
    menuUrl: "/personas/test-alpha/menu.json",
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } },
    taxRate: "0.08",
    roleName: "carhop",
    machines: { soda_machine: { status: "up", label: "Soda machine is down" } },
    happyHour: { startHour: 14, endHour: 17 }
};

function mockPersonaFetch() {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            if (url === "/api/personas") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_INDEX };
            if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => FIXTURE_PERSONA_DETAIL };
            return { ok: false, status: 404, json: async () => ({}) };
        })
    );
}

const tapMic = async () => {
    const micButton = await screen.findByLabelText(/app\.(start|stop)Recording/);
    await act(async () => {
        fireEvent.click(micButton);
    });
};

const openSettings = async () => fireEvent.click(await screen.findByRole("button", { name: /open settings/i }));

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    rec.start.mockImplementation(async () => true);
    rt.api.isConnected = true;
    mockPersonaFetch();
});

// issue 309 (R3): Settings UI goes stale on every new order -- App.tsx's machineStatuses/happyHourMode
// only reset when the persona changes, but "Start new order" opens a brand new session (fresh
// pack-default overrides server-side) that otherwise never hears about whatever the operator had
// already chosen. The fix replays the current overrides right after a new session starts, the
// same place sendVerboseLogging already does.
describe("settings overrides survive a new session (issue 309 R3)", () => {
    it("replays the current machine status and happy-hour mode overrides when a new order starts a fresh session", async () => {
        render(<RootApp />);
        await tapMic();
        act(() =>
            rt.params.onReceivedExtensionMiddleTierToolResponse({
                tool_name: "update_order",
                tool_result: JSON.stringify({ items: [{ item: "Tots", size: "Large", quantity: 1, price: 2.99, display: "Large Tots" }], total: 2.99, tax: 0.24, finalTotal: 3.23 }),
                previous_item_id: "x"
            })
        );

        await openSettings();
        await userEvent.click(screen.getByLabelText("Toggle Soda machine status"));
        expect(rt.api.setMachineStatus).toHaveBeenCalledWith("soda_machine", "down");

        await userEvent.click(screen.getByRole("radio", { name: /^on$/i }));
        expect(rt.api.setHappyHourMode).toHaveBeenCalledWith("on");

        rt.api.setMachineStatus.mockClear();
        rt.api.setHappyHourMode.mockClear();
        rt.api.startSession.mockClear();

        await act(async () => {
            fireEvent.click(screen.getByText("app.newOrder"));
        });
        expect(rt.api.endSession).toHaveBeenCalledTimes(1);

        await tapMic(); // tap to begin the fresh order's own session

        expect(rt.api.startSession).toHaveBeenCalledTimes(1);
        expect(rt.api.setMachineStatus).toHaveBeenCalledWith("soda_machine", "down");
        expect(rt.api.setHappyHourMode).toHaveBeenCalledWith("on");
    });
});
