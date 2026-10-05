import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// #247/#262: a "failed" response.done (see _send_failed_response_done/SendFailedResponseDoneAsync
// in both backends) always carries an empty `output`, so the transcript-derived early return in
// App.tsx's onReceivedResponseDone handler must not be allowed to skip the "AI finished speaking"
// mic-unmute step -- otherwise a turn that fails AFTER TTS has already started streaming some
// audio would leave the mic muted until the guest's next utterance happens to be loud enough to
// trip the separate barge-in recovery path instead.

const lang = vi.hoisted(() => ({ current: "en" }));
vi.mock("react-i18next", () => ({
    useTranslation: () => ({
        t: (key: string) => key,
        i18n: {
            get language() {
                return lang.current;
            },
            changeLanguage: () => Promise.resolve()
        }
    }),
    initReactI18next: { type: "3rdParty", init: () => undefined }
}));

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
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico", apologyClip: "assets/audio/apology-{lang}.wav" },
    strings: { en: {} },
    hero: { headline: "Test Alpha fixture pack", description: "Fixture description.", callouts: [], spotlight: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en", "es", "fr", "ja"] },
    features: { dayparts: false },
    menuUrl: "/personas/test-alpha/menu.json",
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } },
    taxRate: "0.08"
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

const FAILED_RESPONSE_DONE = {
    type: "response.done" as const,
    response: { id: "resp_1", status: "failed", output: [] }
};

const tapMic = async () => {
    const micButton = await screen.findByLabelText(/app\.(start|stop)Recording/);
    await act(async () => {
        fireEvent.click(micButton);
    });
};

async function startConversation() {
    render(<RootApp />);
    await tapMic();
    // The guest's turn starts: mic muted while the AI response is in flight.
    act(() => rt.params.onReceivedResponseCreated({ type: "response.created" }));
    rec.mute.mockClear();
    rec.unmute.mockClear();
}

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    rec.start.mockImplementation(async () => true);
    lang.current = "en";
    mockPersonaFetch();
});

afterEach(() => {
    vi.unstubAllGlobals();
});

describe("failed response.done recovery in the app", () => {
    it("unmutes the mic when a failed response.done arrives (mic was muted at response.created)", async () => {
        await startConversation();
        // startConversation()'s onReceivedResponseCreated already muted the mic (isAiSpeakingRef
        // flips true at the EARLIEST response signal, before any audio ever arrives -- see
        // App.tsx's own onReceivedResponseCreated handler) -- exactly the state a turn that fails
        // (whether before TTS ever runs, #262, or partway through it) leaves behind.
        act(() => rt.params.onReceivedResponseDone(FAILED_RESPONSE_DONE));

        expect(rec.unmute).toHaveBeenCalledTimes(1);
    });

    it("does not unmute a second time if the mic was already unmuted", async () => {
        await startConversation();
        act(() => rt.params.onReceivedResponseDone(FAILED_RESPONSE_DONE));
        rec.unmute.mockClear();

        // A second failed response.done for an unrelated later turn, arriving after the first
        // already recovered -- isAiSpeakingRef is already false, so this must be a no-op.
        act(() => rt.params.onReceivedResponseDone(FAILED_RESPONSE_DONE));

        expect(rec.unmute).not.toHaveBeenCalled();
    });

    it("still unmutes on a normal (non-failed) response.done carrying a real transcript", async () => {
        await startConversation();

        act(() =>
            rt.params.onReceivedResponseDone({
                type: "response.done",
                response: { id: "resp_2", output: [{ content: [{ transcript: "Sure, one large tots." }] }] }
            })
        );

        expect(rec.unmute).toHaveBeenCalledTimes(1);
        expect(screen.getByText("Sure, one large tots.")).toBeInTheDocument();
    });
});
