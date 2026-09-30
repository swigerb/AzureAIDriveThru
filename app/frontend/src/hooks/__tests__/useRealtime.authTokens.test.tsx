import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// PR GH-148 review round 2, item B2: when Entra is configured, `useRealtime`'s WebSocket URL
// factory must (a) carry a fresh Entra access token on every invocation -- not just the first --
// (b) never touch tokens at all for the direct-AOAI debug mode, and (c) dispatch
// AUTH_REQUIRED_EVENT and abort the connect attempt when Entra is configured but the visitor has
// no usable token (signed out, or MSAL has no cached account). `useRealtime.test.tsx` and
// `useRealtime.personaResume.test.tsx` cover the Development pass-through path
// (`authConfig.isConfigured === false`) exhaustively; this file is dedicated to the
// Entra-configured branch, which needs `authConfig`/`tokenService` mocked to reach at all.
const acquireApiToken = vi.hoisted(() => vi.fn());

vi.mock("@/auth/tokenService", () => ({ acquireApiToken }));
vi.mock("@/auth/authConfig", () => ({ authConfig: { isConfigured: true } }));

const ws = vi.hoisted(() => ({
    calls: [] as Array<{ url: string | (() => string | Promise<string>) | null; options: any; connect: boolean }>
}));

vi.mock("react-use-websocket", () => ({
    default: (url: string | (() => string | Promise<string>) | null, options: any, connect: boolean) => {
        ws.calls.push({ url, options, connect });
        return { sendJsonMessage: vi.fn(), readyState: 1 };
    },
    ReadyState: { UNINSTANTIATED: -1, CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 }
}));

// Imported after the mocks above are registered.
import { AUTH_REQUIRED_EVENT } from "@/auth/authorizedFetch";
import useRealTime from "../useRealtime";

const last = () => ws.calls[ws.calls.length - 1];
// The real react-use-websocket invokes `url()` fresh on every connect/reconnect attempt when a
// factory function is passed (item B2) -- this mock only records the reference, so tests that
// need the resolved URL must call this to simulate that invocation themselves, exactly as many
// times as a real reconnect sequence would.
const resolveUrl = async (call = last()) => (typeof call.url === "function" ? await call.url() : call.url);

let tokenCounter = 0;

beforeEach(() => {
    ws.calls = [];
    tokenCounter = 0;
    acquireApiToken.mockReset();
    acquireApiToken.mockImplementation(async () => `entra-tok${++tokenCounter}`);
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({ ok: true, json: async () => ({ token: `session-tok${tokenCounter}` }) }))
    );
});

describe("useRealTime Entra-configured URL factory", () => {
    it("carries a fresh Entra access token on the initial connect", async () => {
        renderHook(() => useRealTime({}));
        await waitFor(() => expect(ws.calls.length).toBeGreaterThan(0));

        await expect(resolveUrl()).resolves.toContain("access_token=entra-tok1");
    });

    it("fetches a distinct token on every invocation of the SAME factory, never re-using the first one", async () => {
        // Simulates what react-use-websocket's own background reconnect does: it re-invokes the
        // identical factory reference on every attempt, never memoizing its resolved value.
        renderHook(() => useRealTime({}));
        await waitFor(() => expect(ws.calls.length).toBeGreaterThan(0));
        const factory = last();

        const first = await resolveUrl(factory);
        const second = await resolveUrl(factory);
        const third = await resolveUrl(factory);

        expect(first).toContain("access_token=entra-tok1");
        expect(second).toContain("access_token=entra-tok2");
        expect(third).toContain("access_token=entra-tok3");
        expect(acquireApiToken).toHaveBeenCalledTimes(3);
    });

    it("never puts an Entra access token on the direct-AOAI passthrough URL, and never calls MSAL for it", async () => {
        renderHook(() =>
            useRealTime({
                useDirectAoaiApi: true,
                aoaiEndpointOverride: "https://aoai.example.com",
                aoaiApiKeyOverride: "key123",
                aoaiModelOverride: "gpt-realtime"
            })
        );
        await waitFor(() => expect(ws.calls.length).toBeGreaterThan(0));

        const url = await resolveUrl();
        expect(url).toBe("https://aoai.example.com/openai/v1/realtime?api-key=key123&model=gpt-realtime");
        expect(acquireApiToken).not.toHaveBeenCalled();
    });

    it("dispatches AUTH_REQUIRED_EVENT and aborts the connect attempt (rejects) when signed out", async () => {
        acquireApiToken.mockResolvedValue(null);
        const onAuthRequired = vi.fn();
        window.addEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);

        try {
            renderHook(() => useRealTime({}));
            await waitFor(() => expect(ws.calls.length).toBeGreaterThan(0));

            // react-use-websocket's own `getUrl` awaits this and, since `retryOnError` is never
            // set, catches the rejection and resolves the whole call to `null` -- it never opens
            // a socket without a bearer token for a configured-but-signed-out visitor.
            await expect(resolveUrl()).rejects.toThrow(/access token/i);
            expect(onAuthRequired).toHaveBeenCalledTimes(1);
        } finally {
            window.removeEventListener(AUTH_REQUIRED_EVENT, onAuthRequired);
        }
    });
});
