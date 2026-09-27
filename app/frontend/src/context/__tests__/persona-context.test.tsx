import { render, screen, waitFor } from "@testing-library/react";
import { act } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { PersonaProvider, usePersonaContext } from "../persona-context";
import type { PersonasIndexResponse, PersonaDetail } from "@/types/persona";

// Issue #80 F1 (design doc §5.1/§5.2, ADR-001 decisions 1/2): PersonaProvider fetches
// `/api/personas` + `/api/personas/{id}` at startup, resolves the session's persona (query param >
// last localStorage choice > catalog default > bundled Sonic fallback), applies its theme/title/
// favicon, and merges its strings into i18next -- all BEFORE any realtime connection is made.

const SECOND_INDEX: PersonasIndexResponse = {
    default: "sonic",
    personas: [
        { id: "sonic", displayName: "Sonic Drive-In", logoUrl: "/personas/sonic/assets/logo.svg", theme: { light: { primary: "341 100% 45%", secondary: "208 52% 33%", background: "195 44% 96%", foreground: "208 53% 20%" } } },
        { id: "test-alpha", displayName: "Test Alpha", logoUrl: "/personas/test-alpha/assets/logo.svg", theme: { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } } }
    ],
    backends: []
};

const ALPHA_DETAIL: PersonaDetail = {
    id: "test-alpha",
    title: "Test Alpha Fixture",
    theme: SECOND_INDEX.personas[1].theme,
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
    strings: { en: { "app.title": "Test Alpha Fixture", "menu.button": "View menu" } },
    hero: { headline: "Test Alpha fixture pack", callouts: [] },
    legal: "Fixture-only disclaimer.",
    voice: { default: "marin" },
    locales: { default: "en", supported: ["en"] },
    features: { dayparts: false },
    menuUrl: "/personas/test-alpha/menu.json",
    models: { realtime: { default: "gpt-realtime-2.1", allowed: ["gpt-realtime-2.1"] } }
};

function Probe() {
    const { personas, current, logoUrl, ready, error, selectPersona } = usePersonaContext();
    return (
        <div>
            <span data-testid="current-id">{current.id}</span>
            <span data-testid="ready">{String(ready)}</span>
            <span data-testid="error">{error ?? ""}</span>
            <span data-testid="persona-count">{personas.length}</span>
            <span data-testid="logo-url">{logoUrl}</span>
            <button onClick={() => selectPersona("test-alpha")}>select alpha</button>
            <button onClick={() => selectPersona("sonic")}>select sonic</button>
        </div>
    );
}

function renderProvider() {
    return render(
        <PersonaProvider>
            <Probe />
        </PersonaProvider>
    );
}

function mockFetchSequence(handler: (url: string) => { ok: boolean; body: unknown }) {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            const { ok, body } = handler(url);
            return { ok, status: ok ? 200 : 404, json: async () => body };
        })
    );
}

beforeEach(() => {
    localStorage.clear();
    // jsdom doesn't implement navigation -- give every test a clean, param-free URL.
    window.history.pushState({}, "", "/");
});

afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.documentElement.removeAttribute("style");
    document.title = "";
});

describe("PersonaProvider", () => {
    it("renders the bundled Sonic fallback immediately, before any fetch resolves", () => {
        vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {}))); // never resolves
        renderProvider();

        expect(screen.getByTestId("current-id")).toHaveTextContent("sonic");
        expect(screen.getByTestId("ready")).toHaveTextContent("false");
    });

    it("stays on the fallback and still reports ready when /api/personas can't be reached (offline)", async () => {
        vi.stubGlobal(
            "fetch",
            vi.fn(async () => {
                throw new Error("network down");
            })
        );
        renderProvider();

        await waitFor(() => expect(screen.getByTestId("ready")).toHaveTextContent("true"));
        expect(screen.getByTestId("current-id")).toHaveTextContent("sonic");
        expect(screen.getByTestId("error")).toHaveTextContent("");
    });

    it("loads the catalog's declared default persona and applies its title/theme", async () => {
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: SECOND_INDEX };
            if (url === "/api/personas/sonic") return { ok: true, body: { ...ALPHA_DETAIL, id: "sonic", title: "Sonic Voice Ordering" } };
            return { ok: false, body: null };
        });
        renderProvider();

        await waitFor(() => expect(screen.getByTestId("ready")).toHaveTextContent("true"));
        expect(screen.getByTestId("persona-count")).toHaveTextContent("2");
        expect(document.title).toBe("Sonic Voice Ordering");
        // The primary theme var is set from the catalog entry's own theme via resolvePersonaTheme.
        expect(document.documentElement.style.getPropertyValue("--brand-primary")).toBe("341 100% 45%");
    });

    it("resolves a persona chosen via ?persona= over the catalog default", async () => {
        window.history.pushState({}, "", "/?persona=test-alpha");
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: SECOND_INDEX };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            return { ok: false, body: null };
        });
        renderProvider();

        await waitFor(() => expect(screen.getByTestId("current-id")).toHaveTextContent("test-alpha"));
        expect(document.title).toBe("Test Alpha Fixture");
    });

    it("remembers the last persona chosen via selectPersona in localStorage", async () => {
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: SECOND_INDEX };
            if (url === "/api/personas/sonic") return { ok: true, body: { ...ALPHA_DETAIL, id: "sonic" } };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            return { ok: false, body: null };
        });
        renderProvider();
        await waitFor(() => expect(screen.getByTestId("ready")).toHaveTextContent("true"));

        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByTestId("current-id")).toHaveTextContent("test-alpha"));
        expect(localStorage.getItem("personaId")).toBe("test-alpha");
    });

    it("surfaces a non-fatal error and keeps the previous selection when a persona detail fetch fails", async () => {
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: SECOND_INDEX };
            if (url === "/api/personas/sonic") return { ok: true, body: { ...ALPHA_DETAIL, id: "sonic" } };
            return { ok: false, body: null }; // test-alpha's detail fetch fails
        });
        renderProvider();
        await waitFor(() => expect(screen.getByTestId("ready")).toHaveTextContent("true"));

        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByTestId("error")).not.toHaveTextContent(""));
        // Stays on Sonic rather than showing a half-applied/blank persona.
        expect(screen.getByTestId("current-id")).toHaveTextContent("sonic");
    });

    it("never throws even though the real i18next singleton is uninitialized in tests (addResourceBundle guard)", async () => {
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: SECOND_INDEX };
            if (url === "/api/personas/sonic") return { ok: true, body: { ...ALPHA_DETAIL, id: "sonic" } };
            return { ok: false, body: null };
        });
        // This is the regression case: before persona-context.tsx guarded the merge with
        // `typeof i18next.addResourceBundle === "function"`, this render would reject with
        // "i18next.addResourceBundle is not a function" because nothing in the test import graph
        // ever calls the real i18n/config.ts's .init().
        expect(() => renderProvider()).not.toThrow();
        await waitFor(() => expect(screen.getByTestId("ready")).toHaveTextContent("true"));
    });
});
