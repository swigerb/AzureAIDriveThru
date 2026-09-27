import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// Rick's PR-110 review (round 3) item 3 (issue #80): the neutral "please wait" shell shown before
// a persona has resolved must use a neutral spinner color (not the brand primary, which hasn't
// been set to anything meaningful yet) and must keep `index.css`'s background veil/blobs hidden
// via `data-persona-loading` on `<html>` -- otherwise a flash of stale or wrong brand color can
// paint behind the "Loading..." shell before the requested persona is actually applied.

vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useRealtime", () => ({ default: () => ({ isConnected: false }) }));
vi.mock("@/hooks/useAudioRecorder", () => ({ default: () => ({ start: vi.fn(), stop: vi.fn(), mute: vi.fn(), unmute: vi.fn() }) }));
vi.mock("@/hooks/useAudioPlayer", () => ({ default: () => ({ reset: vi.fn(), play: vi.fn(), stop: vi.fn(), waitForDrain: vi.fn() }) }));

beforeEach(() => {
    vi.clearAllMocks();
    document.documentElement.removeAttribute("data-persona-loading");
    // Never-resolving fetch: keeps `usePersonaContext()`'s `ready` false so `App()` stays on its
    // neutral loading shell for the life of the test.
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
    document.documentElement.removeAttribute("data-persona-loading");
});

describe("App loading shell (Rick's PR-110 round-3 review item 3, issue #80)", () => {
    it("uses a neutral spinner color and hides the background blobs while no persona has been applied yet", async () => {
        render(<RootApp />);

        await screen.findByText("Loading...");
        const spinner = document.querySelector(".animate-spin");
        expect(spinner).toBeTruthy();
        expect(spinner!.className).toContain("border-muted-foreground");
        expect(spinner!.className).not.toContain("border-primary");

        await waitFor(() => expect(document.documentElement).toHaveAttribute("data-persona-loading", "true"));
    });
});
