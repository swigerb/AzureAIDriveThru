import { render, screen, waitFor } from "@testing-library/react";
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
        endSession: vi.fn(),
        reconnect: vi.fn(async () => {}),
        cancelSwitch: vi.fn(),
        beginSwitch: vi.fn(),
        isConnected: true
    }
}));
const rec = vi.hoisted(() => ({ start: vi.fn(async () => true), stop: vi.fn(async () => {}), mute: vi.fn(), unmute: vi.fn() }));
const player = vi.hoisted(() => ({ reset: vi.fn(async () => {}), play: vi.fn(() => 0), stop: vi.fn(), waitForDrain: vi.fn(async () => true) }));

vi.mock("@/hooks/useRealtime", () => ({
    default: (params: any) => {
        rt.params = params;
        return rt.api;
    },
    resumeStorageKey: (personaId?: string) => `drivethru.resumeId.${personaId ?? "default"}`
}));
vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useAudioRecorder", () => ({ default: () => rec }));
vi.mock("@/hooks/useAudioPlayer", () => ({ default: () => player }));

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };
const INDEX = {
    default: "test-alpha",
    personas: [{ id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme }],
    backends: []
};
const DETAIL = {
    id: "test-alpha",
    title: "Test Alpha Fixture",
    theme,
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
    strings: { en: {} },
    hero: { headline: "Fixture pack", description: "Fixture description.", callouts: [], spotlight: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en"] },
    features: { dayparts: false },
    menuUrl: "/personas/test-alpha/menu.json",
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } },
    taxRate: "0"
};
const SCRIPT = {
    version: 1,
    voice: "en-US-AvaMultilingualNeural",
    menuMode: null,
    title: "Fixture scene",
    kicker: "FIXTURE KICKER",
    lines: [{ id: "01", text: "Fixture line", audio: "demo/guest/01.mp3" }]
};

function mockFetch() {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            if (url === "/api/personas") return { ok: true, status: 200, json: async () => INDEX };
            if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL };
            if (url === "/personas/test-alpha/assets/demo/guestScript.json") return { ok: true, status: 200, json: async () => SCRIPT };
            return { ok: false, status: 404, json: async () => ({}) };
        })
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Element.prototype.scrollIntoView = vi.fn();
    mockFetch();
});

describe("App demo mode", () => {
    it("hides demo controls when the setting is off", async () => {
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        expect(screen.queryByRole("button", { name: /run demo/i })).not.toBeInTheDocument();
    });

    it("persists the setting and shows demo controls when a script exists", async () => {
        render(<RootApp />);
        await screen.findByLabelText("Select persona");
        await userEvent.click(await screen.findByRole("button", { name: /open settings/i }));
        await userEvent.click(await screen.findByRole("checkbox", { name: "settings.demoMode.aria" }));

        expect(localStorage.getItem("demoModeEnabled")).toBe("true");
        await waitFor(() => expect(screen.getByRole("button", { name: /run demo: this brand/i })).toBeInTheDocument());
        expect(screen.getByRole("button", { name: /full tour/i })).toBeInTheDocument();
    });
});
