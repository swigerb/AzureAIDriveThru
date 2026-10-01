import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// Rick's PR-110 review (round 3) item 1 (issue #80): `BrandHero`'s logo `<img>` must render for a
// pack that has one (`logoUrl` non-empty) and fall back to the persona's title as text when a pack
// has none (`logoUrl` empty) -- covering both branches of `showLogo` directly, rather than only the
// `onError` branch (already exercised indirectly by other suites). Also locks in the hardening
// classes (`max-w-[14rem] object-contain`) that stop a size-less/broken logo file from reserving
// the browser's default replaced-element box and shifting the "VOICE ORDERING DEMO" badge next to
// it -- the actual root cause of the test-alpha fixture bug this review round fixed.

const rt = vi.hoisted(() => ({
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

vi.mock("@/hooks/useRealtime", () => ({ default: () => rt.api }));
vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useAudioRecorder", () => ({ default: () => rec }));
vi.mock("@/hooks/useAudioPlayer", () => ({ default: () => player }));

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };

const detailFor = (id: string, title: string) => ({
    id,
    title,
    theme,
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
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

function mockPersonaFetch(personaId: string, logoUrl: string, detail: ReturnType<typeof detailFor>) {
    const index = { default: personaId, personas: [{ id: personaId, displayName: detail.title, logoUrl, theme }], backends: [] };
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            if (url === "/api/personas") return { ok: true, status: 200, json: async () => index };
            if (url === `/api/personas/${personaId}`) return { ok: true, status: 200, json: async () => detail };
            return { ok: false, status: 404, json: async () => ({}) };
        })
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    localStorage.clear();
});

describe("BrandHero logo (Rick's PR-110 round-3 review item 1, issue #80)", () => {
    it("renders the pack's logo image, hardened against a size-less/broken file, when logoUrl is set (test-alpha)", async () => {
        mockPersonaFetch("test-alpha", "/personas/test-alpha/assets/logo.svg?v=abc123", detailFor("test-alpha", "Test Alpha Fixture"));
        render(<RootApp />);

        const logo = await screen.findByAltText("Test Alpha Fixture logo");
        expect(logo.tagName).toBe("IMG");
        expect(logo).toHaveAttribute("src", "/personas/test-alpha/assets/logo.svg?v=abc123");
        expect(logo.className).toContain("max-w-[14rem]");
        expect(logo.className).toContain("object-contain");
    });

    it("falls back to the persona's title as text when the pack has no logo (test-beta, logoUrl empty)", async () => {
        mockPersonaFetch("test-beta", "", detailFor("test-beta", "Test Beta Fixture"));
        render(<RootApp />);

        const fallback = await screen.findByLabelText("Test Beta Fixture logo");
        expect(fallback.tagName).not.toBe("IMG");
        expect(fallback).toHaveTextContent("Test Beta Fixture");
        expect(screen.queryByAltText("Test Beta Fixture logo")).not.toBeInTheDocument();
    });
});
