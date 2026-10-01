import { render, screen, waitFor } from "@testing-library/react";
import { act } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Issue #119 item 1: every other test file in this suite runs against `test/setup.ts`'s global
// `react-i18next` stub (a `t` that just echoes its key), which can never catch a re-render
// regression in the real i18next wiring. This is the one file that needs the REAL hook plus a
// REAL, initialized i18next singleton, so it must opt out of that stub and pull in the real
// `i18n/config.ts` (whose `.init()` call is what sets `react.bindI18nStore` -- the actual fix).
vi.unmock("react-i18next");

import "../../i18n/config";
import i18next from "i18next";
import { PersonaProvider, usePersonaContext } from "../persona-context";
import MenuPanel from "@/components/ui/menu-panel";
import OrderSummary from "@/components/ui/order-summary";
import StatusMessage from "@/components/ui/status-message";
import type { PersonaDetail, PersonasIndexResponse } from "@/types/persona";

const TWO_PERSONA_INDEX: PersonasIndexResponse = {
    default: "test-beta",
    personas: [
        {
            id: "test-beta",
            displayName: "Test Beta",
            logoUrl: "/personas/test-beta/assets/logo.svg",
            theme: { light: { primary: "341 100% 45%", secondary: "208 52% 33%", background: "195 44% 96%", foreground: "208 53% 20%" } }
        },
        {
            id: "test-alpha",
            displayName: "Test Alpha",
            logoUrl: "/personas/test-alpha/assets/logo.svg",
            theme: { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } }
        }
    ],
    backends: []
};

function detailFor(id: string, strings: Record<string, string>): PersonaDetail {
    const summary = TWO_PERSONA_INDEX.personas.find(p => p.id === id)!;
    return {
        id,
        roleName: "tester",
        title: `${summary.displayName} Fixture`,
        theme: summary.theme,
        assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
        strings: { en: strings },
        hero: { headline: `${summary.displayName} fixture pack`, description: "Fixture description.", callouts: [], spotlight: [] },
        legal: "Fixture-only disclaimer.",
        voice: { default: "marin" },
        locales: { default: "en", supported: ["en"] },
        features: { dayparts: false },
        menuUrl: `/personas/${id}/menu.json`,
        models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "GPT Realtime 2.1", reasoning: true }] } },
        taxRate: "0.08"
    };
}

const BETA_DETAIL = detailFor("test-beta", {
    "ticket.kicker": "BETA TICKET",
    "ticket.title": "Your Beta Order",
    "status.notRecordingMessage": "Let's order from Beta!",
    // Only test-beta overrides this key -- test-alpha (below) deliberately leaves it undefined,
    // mirroring a real-world pack (issue #119 item 1 follow-up) whose `ui.strings` intentionally
    // covers only a handful of brand-specific keys and never touches `ticket.emptyHint` at all.
    "ticket.emptyHint": "Add a beta widget to kick things off."
});
const ALPHA_DETAIL = detailFor("test-alpha", {
    "ticket.kicker": "ALPHA TICKET",
    "ticket.title": "Your Alpha Order",
    "status.notRecordingMessage": "Let's order from Alpha!"
});

function mockFetchSequence(handler: (url: string) => { ok: boolean; body: unknown }) {
    vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
            const { ok, body } = handler(url);
            return { ok, status: ok ? 200 : 404, json: async () => body };
        })
    );
}

/** Mirrors the real ticket + status-message pair as they're actually mounted together in
 * `App.tsx`, so the assertions below cover the exact consumer components #119 reported as stale
 * after a switch, not a synthetic probe. */
function TicketAndStatus() {
    const { ready, selectPersona } = usePersonaContext();
    if (!ready) return null;
    return (
        <div>
            <OrderSummary order={{ items: [], total: 0, tax: 0, finalTotal: 0 }} />
            <StatusMessage isRecording={false} />
            <button onClick={() => selectPersona("test-alpha")}>select alpha</button>
        </div>
    );
}

function renderApp() {
    return render(
        <PersonaProvider>
            <TicketAndStatus />
        </PersonaProvider>
    );
}

/** Rick's PR 166 round-1 review, required item 9: mounts the real `MenuPanel` (not the "echo the
 * key" `react-i18next` stub every other test file uses) alongside `selectPersona`, so the
 * value-meals category name's `t("menu.valueMealsCategory")` lookup exercises the REAL i18next
 * merge -- the shared, brand-neutral base string unless the active persona overrides it. */
function MenuAndSelect() {
    const { ready, selectPersona } = usePersonaContext();
    if (!ready) return null;
    return (
        <div>
            <MenuPanel />
            <button onClick={() => selectPersona("test-alpha")}>select alpha</button>
        </div>
    );
}

function renderMenuApp() {
    return render(
        <PersonaProvider>
            <MenuAndSelect />
        </PersonaProvider>
    );
}

beforeEach(() => {
    localStorage.clear();
    window.history.pushState({}, "", "/");
});

afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.documentElement.removeAttribute("style");
    document.title = "";
});

describe("ticket/status copy on persona switch (issue #119 item 1)", () => {
    it("updates already-mounted ticket and status copy when selectPersona switches persona", async () => {
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: TWO_PERSONA_INDEX };
            if (url === "/api/personas/test-beta") return { ok: true, body: BETA_DETAIL };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            return { ok: false, body: null };
        });
        renderApp();

        // Starts on the catalog default (test-beta)'s copy.
        await waitFor(() => expect(screen.getByText("BETA TICKET")).toBeInTheDocument());
        expect(screen.getByText("Your Beta Order")).toBeInTheDocument();
        expect(screen.getByText("Let's order from Beta!")).toBeInTheDocument();

        // This is the regression: before `i18n/config.ts` set `react.bindI18nStore`, these same
        // mounted OrderSummary/StatusMessage instances kept showing test-beta's copy forever,
        // because `useTranslation()` never re-rendered on `persona-context.tsx`'s
        // `addResourceBundle` merge -- only a full remount (or a `languageChanged`/`loaded` event,
        // neither of which `addResourceBundle` fires) would have picked up the new strings.
        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByText("ALPHA TICKET")).toBeInTheDocument());
        expect(screen.getByText("Your Alpha Order")).toBeInTheDocument();
        expect(screen.getByText("Let's order from Alpha!")).toBeInTheDocument();
        expect(screen.queryByText("BETA TICKET")).not.toBeInTheDocument();
        expect(screen.queryByText("Your Beta Order")).not.toBeInTheDocument();
        expect(screen.queryByText("Let's order from Beta!")).not.toBeInTheDocument();
    });

    it("falls back to the neutral base copy -- not the previous persona's override -- for a key the new persona doesn't define", async () => {
        // Regression for a bug surfaced by a partner squad's verification of an unmerged pack
        // (issue #119 item 1 follow-up): that pack's real `ui.strings` only defines a handful of
        // brand-specific keys and never touches `ticket.emptyHint`, yet after switching from the
        // app's default persona the ticket kept showing that default persona's `ticket.emptyHint`
        // override instead of the shared neutral copy. test-beta here plays the default persona's
        // role (it overrides `ticket.emptyHint`); test-alpha plays the other pack's role (it
        // doesn't override that key at all).
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: TWO_PERSONA_INDEX };
            if (url === "/api/personas/test-beta") return { ok: true, body: BETA_DETAIL };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            return { ok: false, body: null };
        });
        renderApp();

        await waitFor(() => expect(screen.getByText("Add a beta widget to kick things off.")).toBeInTheDocument());

        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByText("ALPHA TICKET")).toBeInTheDocument());
        // The bug: without a reset-to-base before each merge, this assertion would still find
        // test-beta's override live in the resource store even though test-alpha is now active.
        expect(screen.queryByText("Add a beta widget to kick things off.")).not.toBeInTheDocument();
        expect(screen.getByText("Add items to get started.")).toBeInTheDocument();
    });

    it("clears a previous persona's key that has no neutral-base counterpart at all when switching personas (Rick's #120 review round 2 nit: a true reset, not a shallow overlay)", async () => {
        // The two earlier tests above only exercise keys that DO exist in `baseTranslationResources`
        // (e.g. `ticket.emptyHint`) -- the old `addResourceBundle(base, deep=false, overwrite=true)`
        // reset already happened to clear those correctly, because `deep=false` still replaces any
        // top-level key that IS present in the object it's given. The bug is a top-level key a pack
        // invents that the base translation table never defines at all: that overlay never touches
        // it, so it silently outlived every later switch. `test-beta` here plays a pack whose
        // `ui.strings` defines such a key; `test-alpha` never defines it.
        const BETA_WITH_CUSTOM_KEY = detailFor("test-beta", {
            ...(BETA_DETAIL.strings.en as Record<string, string>),
            "ticket.betaOnlyPromo": "Beta-only promo copy that translation.json never defines"
        });
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: TWO_PERSONA_INDEX };
            if (url === "/api/personas/test-beta") return { ok: true, body: BETA_WITH_CUSTOM_KEY };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            return { ok: false, body: null };
        });
        renderApp();

        await waitFor(() => expect(screen.getByText("BETA TICKET")).toBeInTheDocument());
        expect(i18next.exists("ticket.betaOnlyPromo")).toBe(true);

        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByText("ALPHA TICKET")).toBeInTheDocument());
        // Regression: without `removeResourceBundle` emptying the namespace first, this key --
        // never part of `baseTranslationResources` -- would still resolve here even though
        // test-alpha (which never defines it) is now the active persona.
        expect(i18next.exists("ticket.betaOnlyPromo")).toBe(false);
    });
});

describe("value-meals category name on persona switch (Rick's PR 166 round-1 review, required item 9)", () => {
    it("resolves the shared base string, then a persona's own ui.strings override, through the real i18next merge", async () => {
        // test-beta plays a pack with its own override (shaped like a real pack's: "Extra Value Meals");
        // test-alpha plays a pack that never touches this key, so it must fall back to the shared,
        // brand-neutral `translation.json` copy ("Value Meals") -- not silently inherit test-beta's.
        const BETA_WITH_VALUE_MEALS_OVERRIDE = detailFor("test-beta", {
            ...(BETA_DETAIL.strings.en as Record<string, string>),
            "menu.valueMealsCategory": "Extra Value Meals"
        });
        const MEAL_ITEM = {
            name: "Combo One",
            sizes: [{ size: "Standard", price: 7.99 }],
            description: "A fixture combo.",
            mealNumber: "1"
        };
        mockFetchSequence(url => {
            if (url === "/api/personas") return { ok: true, body: TWO_PERSONA_INDEX };
            if (url === "/api/personas/test-beta") return { ok: true, body: BETA_WITH_VALUE_MEALS_OVERRIDE };
            if (url === "/api/personas/test-alpha") return { ok: true, body: ALPHA_DETAIL };
            if (url === "/personas/test-beta/menu.json") return { ok: true, body: { menuItems: [{ category: "Combos", items: [MEAL_ITEM] }] } };
            if (url === "/personas/test-alpha/menu.json") return { ok: true, body: { menuItems: [{ category: "Combos", items: [MEAL_ITEM] }] } };
            return { ok: false, body: null };
        });
        renderMenuApp();

        await waitFor(() => expect(screen.getByText("Extra Value Meals")).toBeInTheDocument());

        await act(async () => {
            screen.getByText("select alpha").click();
        });

        await waitFor(() => expect(screen.getByText("Value Meals")).toBeInTheDocument());
        expect(screen.queryByText("Extra Value Meals")).not.toBeInTheDocument();
    });
});
