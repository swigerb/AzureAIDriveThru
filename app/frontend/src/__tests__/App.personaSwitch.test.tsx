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
    hero: { headline: `${title} fixture pack`, callouts: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en"] },
    features: { dayparts: false },
    menuUrl: `/personas/${id}/menu.json`,
    models: { realtime: { default: "gpt-realtime-2.1", allowed: ["gpt-realtime-2.1"] } }
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
