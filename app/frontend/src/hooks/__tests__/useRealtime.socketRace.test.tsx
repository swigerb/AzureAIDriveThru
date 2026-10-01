import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import useRealTime from "../useRealtime";

// Issue #171: "mic does nothing after a persona switch" -- a stale socket's close event arrives
// after its replacement has already opened, flips the hook's shared `openRef` back to false, and
// the queued `session.update` from `startSession()` is never flushed. Unlike every OTHER test in
// this directory, this file does NOT mock `react-use-websocket`: the race is a genuine property of
// how the real library wires a native WebSocket's `onopen`/`onclose` straight through to this
// hook's own callbacks (no identity check, unlike its own `readyState`/`lastMessage` setters), so a
// mocked `react-use-websocket` can't reproduce it -- only a fake *global `WebSocket`* driving the
// real library can.
class FakeWebSocket {
    static instances: FakeWebSocket[] = [];
    readyState = 0; // CONNECTING
    onopen: ((event: unknown) => void) | null = null;
    onclose: ((event: unknown) => void) | null = null;
    onmessage: ((event: unknown) => void) | null = null;
    onerror: ((event: unknown) => void) | null = null;
    sent: string[] = [];

    constructor(public url: string) {
        FakeWebSocket.instances.push(this);
    }

    send(data: string) {
        this.sent.push(data);
    }

    // A real browser never invokes `onclose` synchronously from `close()` -- it always fires
    // asynchronously, later, once the close handshake completes. react-use-websocket's own
    // teardown (on url/connect change) calls this when replacing a socket; the corresponding
    // close EVENT is fired by the test itself via `triggerClose`, at whatever moment it wants to
    // reproduce a given real-world ordering.
    close() {
        if (this.readyState === 3) this.readyState = 3;
        else this.readyState = 2; // CLOSING
    }

    triggerOpen() {
        this.readyState = 1; // OPEN
        this.onopen?.({ type: "open", target: this });
    }

    triggerClose(code: number, reason = "") {
        this.readyState = 3; // CLOSED
        this.onclose?.({ type: "close", target: this, code, reason, wasClean: true });
    }
}

let tokenCounter = 0;

beforeEach(() => {
    FakeWebSocket.instances = [];
    tokenCounter = 0;
    vi.stubGlobal("WebSocket", FakeWebSocket as unknown as typeof WebSocket);
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({ ok: true, json: async () => ({ token: `tok${++tokenCounter}` }) }))
    );
    sessionStorage.clear();
});

afterEach(() => {
    vi.unstubAllGlobals();
});

const sentTypes = (socket: FakeWebSocket) => socket.sent.map(raw => JSON.parse(raw).type);

describe("useRealTime against the real react-use-websocket (issue #171 repro)", () => {
    it("still flushes session.update onto the new socket when the OLD socket's close arrives after the NEW socket's open", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // Switching persona changes getSocketUrl's identity: react-use-websocket tears the old
        // socket down (calls close(), no event yet) and starts opening a replacement off the new
        // URL, independently of anything this hook does itself.
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];

        // The exact ordering from the bug report: the replacement opens first...
        act(() => socketB.triggerOpen());
        // ...and only then does socket A's belated close event arrive.
        act(() => socketA.triggerClose(1000, ""));

        act(() => result.current.startSession());

        expect(sentTypes(socketB)).toContain("session.update");
        expect(socketA.sent).toHaveLength(0);
        // The stale close must not be treated as a real connection-lost event either -- it
        // belongs to a socket this hook no longer owns.
        expect(onConnectionLost).not.toHaveBeenCalled();
    });

    it("opens exactly one replacement socket on a persona switch while idle and raises no ended/idle/superseded/lost notice, even when the old socket's close arrives before the replacement exists", async () => {
        const onConnectionLost = vi.fn();
        const { rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        rerender({ personaId: "rick" });
        // Fire the old socket's close immediately -- before the token fetch for the replacement
        // has even resolved, let alone before a new FakeWebSocket has been constructed. This is
        // the ordering the identity check (getWebSocket() !== event.target) can't catch on its
        // own, since there is no newer socket yet to compare against.
        act(() => socketA.triggerClose(1000, ""));
        expect(FakeWebSocket.instances).toHaveLength(1);

        // The url-keyed effect's own replacement socket still arrives, exactly once.
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(2);

        expect(onConnectionLost).not.toHaveBeenCalled();
    });
});
