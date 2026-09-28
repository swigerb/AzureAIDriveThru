import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import useRealTime, { resumeStorageKey } from "../useRealtime";

// Issue #80 F7, Rick's #110 review item 5: "resume key per persona (`drivethru.resumeId.<persona>`)".
// A shared, un-namespaced resume key would let switching personas read or clobber a DIFFERENT
// persona's resume id -- these tests guard that each persona gets its own sessionStorage slot.

const ws = vi.hoisted(() => ({
    calls: [] as Array<{ url: string | (() => string | Promise<string>) | null; options: any; connect: boolean }>,
    send: vi.fn()
}));

vi.mock("react-use-websocket", () => ({
    default: (url: string | (() => string | Promise<string>) | null, options: any, connect: boolean) => {
        ws.calls.push({ url, options, connect });
        return { sendJsonMessage: ws.send, readyState: 1 };
    },
    ReadyState: { UNINSTANTIATED: -1, CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 }
}));

const last = () => ws.calls[ws.calls.length - 1];
// PR GH-148 review round 2, item B2: `useRealtime` hands react-use-websocket an async URL
// factory, not a plain string -- see `useRealtime.test.tsx` for the full rationale. This mock
// only records the reference, so a test that needs the resolved URL must call this itself.
const resolveUrl = async (call = last()) => (typeof call.url === "function" ? await call.url() : call.url);
let tokenCounter = 0;

beforeEach(() => {
    ws.calls = [];
    ws.send.mockReset();
    sessionStorage.clear();
    tokenCounter = 0;
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({ ok: true, json: async () => ({ token: `tok${++tokenCounter}` }) }))
    );
});

async function renderForPersona(personaId: string | undefined) {
    const hook = renderHook(({ id }: { id: string | undefined }) => useRealTime({ personaId: id }), { initialProps: { id: personaId } });
    await waitFor(() => expect(last()).toBeDefined());
    return hook;
}

describe("resumeStorageKey", () => {
    it("namespaces the storage key per persona", () => {
        expect(resumeStorageKey("test-alpha")).toBe("drivethru.resumeId.test-alpha");
        expect(resumeStorageKey("test-beta")).toBe("drivethru.resumeId.test-beta");
    });

    it("uses a default bucket when no persona id is given", () => {
        expect(resumeStorageKey()).toBe("drivethru.resumeId.default");
        expect(resumeStorageKey(undefined)).toBe("drivethru.resumeId.default");
    });

    it("never collapses to an un-namespaced '<persona>.resumeId' key", () => {
        // Fixture id (not the earlier hardcoded default persona) per Rick's PR-110 review item 4:
        // baseline tests use test-alpha/test-beta, not a real brand's persona id.
        expect(resumeStorageKey("test-alpha")).toBe("drivethru.resumeId.test-alpha");
        expect(resumeStorageKey("test-alpha")).not.toBe("test-alpha.resumeId");
    });
});

describe("useRealTime resume id storage, keyed per persona", () => {
    it("stores an announced resume id under that persona's own key, not another persona's", async () => {
        await renderForPersona("test-alpha");

        act(() => {
            last().options.onMessage({ data: JSON.stringify({ type: "extension.session_metadata", resumeId: "RID-ALPHA" }) } as MessageEvent);
        });

        expect(sessionStorage.getItem(resumeStorageKey("test-alpha"))).toBe("RID-ALPHA");
        expect(sessionStorage.getItem(resumeStorageKey("test-beta"))).toBeNull();
    });

    it("reads back only the resume id for the persona currently in play", async () => {
        sessionStorage.setItem(resumeStorageKey("test-alpha"), "RID-ALPHA");
        sessionStorage.setItem(resumeStorageKey("test-beta"), "RID-BETA");

        await renderForPersona("test-beta");
        act(() => last().options.onOpen(new Event("open")));

        expect(ws.send).toHaveBeenCalledWith({ type: "extension.resume", resume_id: "RID-BETA" }, false);
        expect(ws.send).not.toHaveBeenCalledWith({ type: "extension.resume", resume_id: "RID-ALPHA" }, false);
    });

    it("switching personas (a fresh personaId on a later render) starts reading/writing the new persona's key", async () => {
        sessionStorage.setItem(resumeStorageKey("test-alpha"), "RID-ALPHA");
        const { rerender } = await renderForPersona("test-alpha");

        rerender({ id: "test-beta" });
        await waitFor(async () => expect(await resolveUrl()).toContain("persona=test-beta"));

        act(() => last().options.onOpen(new Event("open")));

        // test-beta has no stored resume id of its own yet: no extension.resume frame for either persona.
        expect(ws.send).not.toHaveBeenCalledWith({ type: "extension.resume", resume_id: "RID-ALPHA" }, false);
    });
});
