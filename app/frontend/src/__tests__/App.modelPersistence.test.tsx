import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// Issue #80 F10/F11, exercised end-to-end through the real App() wiring (the way
// `App.personaSwitch.test.tsx` covers F7): F10's model choice is (a) grouped by pipeline in the
// real <Settings>/<ModelPicker>, (b) persisted per persona rather than sharing one global key like
// `voiceChoice`, (c) reset to the new persona's own default on a persona switch, and (d) threaded
// into `useRealTime`'s `modelId` param so a new session actually requests it. F11's backend switch
// only renders once `/api/personas` reports more than one `backends[]` entry.

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
    backends: [
        { id: "python", url: "" },
        { id: "dotnet", url: "https://dotnet.example.com" }
    ]
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
    taxRate: "0.08",
    models: {
        realtime: {
            default: "gpt-realtime-2.1",
            models: [
                { id: "gpt-realtime-2.1", label: `${title} Realtime 2.1`, reasoning: false },
                { id: "gpt-realtime-mini", label: `${title} Realtime Mini`, reasoning: false }
            ]
        },
        cascade: {
            default: "gpt-5-mini",
            models: [{ id: "gpt-5-mini", label: `${title} Cascade Mini`, reasoning: true }]
        }
    }
});
const DETAIL_ALPHA = detailFor("test-alpha", "Test Alpha");
const DETAIL_BETA = detailFor("test-beta", "Test Beta");

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
    // jsdom doesn't implement navigation -- give every test a clean, param-free URL (matching
    // persona-context.test.tsx's own convention for the sibling ?persona= query param).
    window.history.pushState({}, "", "/");
    mockPersonaFetch();
});

describe("model picker persistence and reset (issue #80 F10)", () => {
    it("defaults to the persona's realtime default and threads it into useRealTime's modelId", async () => {
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));
    });

    it("persists a chosen model under a key namespaced by persona id, not a single shared key", async () => {
        render(<RootApp />);
        await openSettings();

        await userEvent.selectOptions(screen.getByLabelText("Select model"), "gpt-5-mini");

        await waitFor(() => expect(localStorage.getItem("modelChoice.test-alpha")).toBe("gpt-5-mini"));
        expect(localStorage.getItem("modelChoice.test-beta")).toBeNull();
        await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));
    });

    it("resets to the new persona's own default on a persona switch, rather than carrying over the old choice", async () => {
        localStorage.setItem("modelChoice.test-alpha", "gpt-5-mini");
        render(<RootApp />);
        await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));

        await switchTo("test-beta");

        await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));
    });

    it("recalls a persona's previously-stored model choice when switching back to it", async () => {
        localStorage.setItem("modelChoice.test-beta", "gpt-5-mini");
        render(<RootApp />);
        await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));

        await switchTo("test-beta");

        await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));
    });

    it("locks the model picker during an active session, matching the persona picker's lock", async () => {
        render(<RootApp />);
        await openSettings();
        expect(screen.getByLabelText("Select model")).not.toBeDisabled();

        const micButton = await screen.findByLabelText(/app\.(start|stop)Recording/);
        await act(async () => {
            fireEvent.click(micButton);
        });

        await waitFor(() => expect(screen.getByLabelText("Select model")).toBeDisabled());
    });

    // Rick's PR 134 review, item 2: a backend switch's `?model=` (set by `lib/backends.ts`'s
    // `backendTargetUrl`) is read on arrival, validated against THIS backend's own
    // `/api/personas/{id}` list for the bound persona, persisted per persona, and stripped from
    // the address bar so a later reload of the same URL doesn't keep re-pinning a stale choice.
    describe("reading ?model= on arrival (issue #80, Rick's PR 134 review item 2)", () => {
        it("adopts a listed ?model= value, persists it, and strips it from the address bar", async () => {
            window.history.pushState({}, "", "/?model=gpt-5-mini");
            render(<RootApp />);
            await screen.findByLabelText("Select persona");

            await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));
            expect(localStorage.getItem("modelChoice.test-alpha")).toBe("gpt-5-mini");
            expect(new URLSearchParams(window.location.search).has("model")).toBe(false);
        });

        it("falls back to the persona's default for an unlisted ?model= value, still stripping it", async () => {
            window.history.pushState({}, "", "/?model=not-a-real-model");
            render(<RootApp />);
            await screen.findByLabelText("Select persona");

            await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));
            expect(new URLSearchParams(window.location.search).has("model")).toBe(false);
        });

        it("preserves other query params (e.g. ?persona=) when stripping ?model=", async () => {
            window.history.pushState({}, "", "/?persona=test-beta&model=gpt-5-mini");
            render(<RootApp />);
            await screen.findByLabelText("Select persona");

            await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));
            expect(new URLSearchParams(window.location.search).get("persona")).toBe("test-beta");
            expect(new URLSearchParams(window.location.search).has("model")).toBe(false);
        });

        it("takes ?model= over a persona's own stored choice", async () => {
            localStorage.setItem("modelChoice.test-alpha", "gpt-realtime-mini");
            window.history.pushState({}, "", "/?model=gpt-5-mini");
            render(<RootApp />);
            await screen.findByLabelText("Select persona");

            await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));
            expect(localStorage.getItem("modelChoice.test-alpha")).toBe("gpt-5-mini");
        });
    });

    // Rick's PR 134 review, item 4: switching personas must never leave a one-render window where
    // the NEW persona's storage key (or `useRealTime`'s `modelId` param) briefly holds the OLD
    // persona's model id -- the previous two-effect split (a resolve effect plus a separate
    // effect persisting `modelId` under `current.id`) both ran on the same commit `current`
    // changed, in an order that wrote the stale id. Folding persistence into the resolve effect
    // itself (and the explicit `onModelChange` handler) closes that window.
    describe("no stale-model race on persona switch (issue #80, Rick's PR 134 review item 4)", () => {
        it("never persists the old persona's model under the new persona's storage key", async () => {
            localStorage.setItem("modelChoice.test-alpha", "gpt-5-mini");
            render(<RootApp />);
            await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));

            await switchTo("test-beta");

            await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));
            // "gpt-5-mini" (test-alpha's model) must never have been written under test-beta's key,
            // not even transiently -- assert on the final value, which is all a stale-write bug
            // would have corrupted since nothing else writes this key on a persona switch.
            expect(localStorage.getItem("modelChoice.test-beta")).toBe("gpt-realtime-2.1");
        });

        it("never writes the old persona's model to the new persona's storage key, not even transiently", async () => {
            localStorage.setItem("modelChoice.test-alpha", "gpt-5-mini");
            render(<RootApp />);
            await waitFor(() => expect(rt.params.modelId).toBe("gpt-5-mini"));

            const setItemSpy = vi.spyOn(Storage.prototype, "setItem");
            await switchTo("test-beta");
            await waitFor(() => expect(rt.params.modelId).toBe("gpt-realtime-2.1"));

            // Every write App.tsx makes to test-beta's own key during the switch, in order -- a
            // stale-write bug would show "gpt-5-mini" (test-alpha's model) written here before the
            // correct default, since the old two-effect split persisted whatever `modelId` last
            // was under the persona that had JUST become `current` on the very same commit.
            const betaWrites = setItemSpy.mock.calls.filter(([key]) => key === "modelChoice.test-beta").map(([, value]) => value);
            expect(betaWrites).not.toContain("gpt-5-mini");
            setItemSpy.mockRestore();
        });
    });
});

describe("backend switch visibility (issue #80 F11)", () => {
    it("shows the backend switch once /api/personas reports more than one backend", async () => {
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        expect(await screen.findByLabelText("Select backend")).toBeInTheDocument();
    });

    it("hides the backend switch entirely when only one backend is configured", async () => {
        vi.stubGlobal(
            "fetch",
            vi.fn(async (url: string) => {
                if (url === "/api/personas") return { ok: true, status: 200, json: async () => ({ ...FIXTURE_PERSONA_INDEX, backends: [{ id: "python", url: "" }] }) };
                if (url === "/api/personas/test-alpha") return { ok: true, status: 200, json: async () => DETAIL_ALPHA };
                if (url === "/api/personas/test-beta") return { ok: true, status: 200, json: async () => DETAIL_BETA };
                return { ok: false, status: 404, json: async () => ({}) };
            })
        );
        render(<RootApp />);
        await screen.findByLabelText("Select persona");

        expect(screen.queryByLabelText("Select backend")).not.toBeInTheDocument();
    });
});
