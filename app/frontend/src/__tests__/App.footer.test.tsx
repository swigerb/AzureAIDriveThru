import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// Issue 164 R2 (PR 167 round 1 review, Rick): "Role-to-class tests in App.brandComponents.test.tsx
// (footer, badge, chips)". The badge and chips (SessionTokenPanel) role-to-class checks already
// exist in App.brandComponents.test.tsx against their individually-exported components, but the
// footer tagline is rendered inline inside the top-level `App` component (not a separately exported
// component), so it needs a full-app render to assert its rendered class -- this file follows the
// exact render/mock harness `App.brandHero.test.tsx` already established for that same reason.

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
        cancelSwitch: vi.fn(),
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

const detailFor = (id: string, title: string, textRoles?: Record<string, string>) => ({
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
    taxRate: "0.08",
    ...(textRoles ? { textRoles } : {})
});

function mockPersonaFetch(personaId: string, detail: ReturnType<typeof detailFor>) {
    const index = { default: personaId, personas: [{ id: personaId, displayName: detail.title, logoUrl: "", theme }], backends: [] };
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

/** This test environment's i18n has no translation resources loaded (no http backend/mock), so
 * `t("app.footer")` renders the literal, untranslated key string -- the same convention
 * App.brandComponents.test.tsx's "hero.poweredBy" assertions already rely on. */
const FOOTER_TAGLINE_TEXT = "app.footer";

describe("App footer tagline role-to-class (issue 164 R2)", () => {
    it("colors the footer tagline from the shared default 'secondary' role when the pack sets no textRoles override", async () => {
        mockPersonaFetch("test-footer-default", detailFor("test-footer-default", "Test Footer Default"));
        render(<RootApp />);

        const footerTagline = await screen.findByText(FOOTER_TAGLINE_TEXT);
        expect(footerTagline.tagName).toBe("P");
        expect(footerTagline.className).toContain("text-brand-secondary");
    });

    it("colors the footer tagline from an explicit ui.textRoles.footerTagline override", async () => {
        mockPersonaFetch("test-footer-override", detailFor("test-footer-override", "Test Footer Override", { footerTagline: "primaryDeep" }));
        render(<RootApp />);

        const footerTagline = await screen.findByText(FOOTER_TAGLINE_TEXT);
        expect(footerTagline.className).toContain("text-brand-primary-deep");
        expect(footerTagline.className).not.toContain("text-brand-secondary");
    });

    it("never applies the dropped /80 opacity suffix to the footer tagline (R2(d))", async () => {
        mockPersonaFetch("test-footer-opacity", detailFor("test-footer-opacity", "Test Footer Opacity"));
        render(<RootApp />);

        const footerTagline = await screen.findByText(FOOTER_TAGLINE_TEXT);
        expect(footerTagline.className).not.toMatch(/text-brand-secondary\/\d/);
    });
});
