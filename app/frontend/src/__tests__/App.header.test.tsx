import type { ReactNode } from "react";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

const auth = vi.hoisted(() => ({ isConfigured: true, signOut: vi.fn() }));
vi.mock("@/auth/authConfig", () => ({ authConfig: auth }));
vi.mock("@/auth/signOut", () => ({ signOutInteractive: auth.signOut }));
vi.mock("@/auth/AuthGate", () => ({ AuthGate: ({ children }: { children: ReactNode }) => children }));
vi.mock("@/hooks/useRealtime", () => ({
    default: () => ({
        startSession: vi.fn(), addUserAudio: vi.fn(), inputAudioBufferClear: vi.fn(),
        cancelResponse: vi.fn(), sendVerboseLogging: vi.fn(), sendLogToFile: vi.fn(),
        sendVoiceChoice: vi.fn(), setMachineStatus: vi.fn(), setHappyHourMode: vi.fn(),
        endSession: vi.fn(), reconnect: vi.fn(async () => {}), cancelSwitch: vi.fn(),
        beginSwitch: vi.fn(), isConnected: true
    })
}));
vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useAudioRecorder", () => ({
    default: () => ({ start: vi.fn(async () => true), stop: vi.fn(async () => {}), mute: vi.fn(), unmute: vi.fn() })
}));
vi.mock("@/hooks/useAudioPlayer", () => ({
    default: () => ({ reset: vi.fn(async () => {}), play: vi.fn(), stop: vi.fn(), waitForDrain: vi.fn(async () => true) })
}));

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };
const detail = {
    id: "header-fixture", title: "Header fixture", theme,
    assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
    strings: { en: {} }, hero: { headline: "Header fixture", description: "Fixture.", callouts: [], spotlight: [] },
    legal: "Fixture only.", voice: { default: "marin" }, locales: { default: "en", supported: ["en"] },
    features: { dayparts: false }, menuUrl: "/personas/header-fixture/menu.json", taxRate: "0.08",
    models: { realtime: { default: "gpt-realtime-2.1", models: [{ id: "gpt-realtime-2.1", label: "Realtime", reasoning: false }] } }
};

beforeEach(() => {
    vi.clearAllMocks();
    auth.isConfigured = true;
    localStorage.clear();
    Element.prototype.scrollIntoView = vi.fn();
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
        if (url === "/api/personas") return {
            ok: true, status: 200, json: async () => ({
                default: detail.id, personas: [{ id: detail.id, displayName: detail.title, logoUrl: "", theme }],
                backends: [{ id: "python", url: "" }, { id: "dotnet", url: "https://dotnet.example.com" }]
            })
        };
        if (url === `/api/personas/${detail.id}`) return { ok: true, status: 200, json: async () => detail };
        return { ok: false, status: 404, json: async () => ({}) };
    }));
});

describe("Responsive header controls", () => {
    it.each([true, false])("keeps all controls in a wrapping toolbar (Entra configured: %s)", async (configured) => {
        auth.isConfigured = configured;
        render(<RootApp />);
        const toolbar = await screen.findByTestId("header-controls");
        expect(toolbar).toHaveClass("flex", "flex-wrap", "items-center", "gap-2");
        expect(within(toolbar).getByLabelText("Select persona")).toBeVisible();
        expect(within(toolbar).getByLabelText("Select backend")).toBeVisible();
        const settings = await within(toolbar).findByRole("button", { name: "Open settings" });
        expect(settings).toBeVisible();
        if (configured) {
            const logout = within(toolbar).getByRole("button", { name: "Logout" });
            expect(logout).toBeVisible();
            await userEvent.click(logout);
            expect(auth.signOut).toHaveBeenCalledOnce();
        } else {
            expect(within(toolbar).queryByRole("button", { name: "Logout" })).not.toBeInTheDocument();
        }
        await userEvent.click(settings);
        expect(await screen.findByRole("dialog")).toBeVisible();
        expect(screen.getByLabelText("Select model")).toBeVisible();
        await userEvent.keyboard("{Escape}");
        expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });
});
