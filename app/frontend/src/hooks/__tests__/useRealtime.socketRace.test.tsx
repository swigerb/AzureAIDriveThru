import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import useRealTime, { resumeStorageKey } from "../useRealtime";

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

    triggerMessage(data: string) {
        this.onmessage?.({ type: "message", target: this, data });
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
    // Explicitly unmount every rendered hook (and let react-use-websocket's own cleanup cancel any
    // pending reconnect `setTimeout` it scheduled for a "transport"-classified close) BEFORE
    // restoring the real global `WebSocket`/`fetch`. Without this, a still-pending reconnect timer
    // from one test can fire during a LATER test (or after the file finishes), find the real
    // `WebSocket` global back in place, and throw on the fixture's relative `/realtime?...` URL.
    cleanup();
    vi.unstubAllGlobals();
});

const sentTypes = (socket: FakeWebSocket) => socket.sent.map(raw => JSON.parse(raw).type);
// Issue GH-171 round 3 (missing round-1 coverage, item 3): `toContain("session.update")` alone
// never catches a DUPLICATE send -- e.g. a stray queued frame replayed twice onto the same
// socket, or one copy on each of two sockets. Every call site this guards against is exactly the
// kind of double-send bug this file exists to catch, so the count has to be exact, not "at least
// one".
const sessionUpdateCount = (socket: FakeWebSocket) => sentTypes(socket).filter(type => type === "session.update").length;

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

        expect(sessionUpdateCount(socketB)).toBe(1);
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

describe("useRealTime send() identity guard (issue #171 round 2, H1 regression)", () => {
    it("queues (never silently drops) a tap that lands after the replacement socket is CREATED but before it OPENS, once the old socket's close is stale-guarded away", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        rerender({ personaId: "rick" });
        // The replacement socket now exists (react-use-websocket already assigned it to its own
        // internal ref the moment `new WebSocket()` was called) but has NOT opened yet --
        // getWebSocket() already returns socketB, so socketA's belated close below is stale-
        // guarded away by the EXISTING identity check and never runs onClose's own
        // `openRef.current = false`. Without this file's render-time reset, `openRef` is left
        // stuck `true` (from socketA's own onOpen) for this entire window.
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.readyState).toBe(0); // CONNECTING -- not open yet

        act(() => socketA.triggerClose(1000, ""));

        // The regression: a tap (startSession queues a session.update) lands in exactly this
        // window -- socketB created, not yet open.
        act(() => result.current.startSession());

        act(() => socketB.triggerOpen());

        // With the bug, `openRef` was stuck `true` from socketA, so `send()` handed the frame
        // straight to react-use-websocket's own sendJsonMessage while socketB was still
        // CONNECTING; the library drops a send that isn't OPEN yet (keep=false) instead of
        // queuing it, and it never appears on socketB even after it opens.
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(socketA.sent).toHaveLength(0);
        expect(onConnectionLost).not.toHaveBeenCalled();
    });
});

describe("useRealTime send() identity guard: immediate taps right after a switch (issue #171 round 2, H1b)", () => {
    const rows: Array<[string, Record<string, string>, Record<string, string>]> = [
        ["personaId", { personaId: "jerry" }, { personaId: "rick" }],
        ["modelId", { personaId: "jerry", modelId: "gpt-realtime-2.1" }, { personaId: "jerry", modelId: "gpt-realtime-mini" }],
        ["menuMode", { personaId: "jerry", menuMode: "breakfast" }, { personaId: "jerry", menuMode: "lunch" }]
    ];

    it.each(rows)("a tap immediately after a %s switch (before the old socket even closes) queues onto the new socket, not the old one", async (_label, before, after) => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(props => useRealTime({ enableInputAudioTranscription: true, onConnectionLost, ...props }), {
            initialProps: before
        });

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // The switch's render commits (props change) well before react-use-websocket's own
        // cleanup/reconnect effect, and long before the old socket's own close event (a genuine
        // async browser event) can possibly arrive. A tap right here is the H1b ordering: the OLD
        // socket (per getWebSocket(), since no replacement exists yet) is still technically OPEN,
        // so neither onOpen/onClose's own stale guard has anything to react to -- only the
        // render-time identity reset catches this.
        rerender(after);
        act(() => result.current.startSession());

        expect(socketA.sent).toHaveLength(0); // never sent onto the superseded socket

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        act(() => socketB.triggerOpen());

        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(onConnectionLost).not.toHaveBeenCalled();
    });
});

describe("useRealTime double-connect fix on a real persona switch (issue #171 round 2, H2)", () => {
    it("opens exactly one new socket when the server's close for our own end_session arrives BEFORE the persona prop actually changes", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // App.tsx's handleSelectPersona: endSession({ switching: true }) runs synchronously, well
        // before the async persona fetch resolves and the `personaId` prop actually moves.
        act(() => result.current.endSession({ switching: true }));
        expect(sentTypes(socketA)).toContain("extension.end_session");

        // The server's close for that end_session arrives first -- the prop hasn't changed yet,
        // so `switchedSinceOpen` alone (the pre-existing guard) can't tell this apart from an
        // ordinary "start a new order" ended close.
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        // No orphan socket for the persona being switched AWAY from.
        expect(FakeWebSocket.instances).toHaveLength(1);

        // The real prop change eventually lands.
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(onConnectionLost).not.toHaveBeenCalled();
    });

    it("opens exactly one new socket when the persona prop changes BEFORE the server's close for our own end_session arrives", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        act(() => result.current.endSession({ switching: true }));
        rerender({ personaId: "rick" });
        // The close for the end_session we just sent only arrives now, after the prop change --
        // `switchedSinceOpen` alone already caught this ordering before round 2.
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        // Exactly one replacement socket total -- no orphan for "jerry".
        expect(FakeWebSocket.instances).toHaveLength(2);
        expect(onConnectionLost).not.toHaveBeenCalled();
    });

    it("a FAILED persona load: a tap's reconnect() during the still-pending switch is a no-op; cancelSwitch() -- not reconnect() -- is what recovers the old persona's session, and the queued session.update is sent exactly once (issue GH-171 round 3, H4)", async () => {
        const onConnectionLost = vi.fn();
        const { result } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        act(() => result.current.endSession({ switching: true }));
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        // The persona fetch failed: `personaId` never changes, so there is nothing for
        // react-use-websocket's own url-keyed effect to react to. No orphan socket was opened...
        expect(FakeWebSocket.instances).toHaveLength(1);
        // ...but the UI is left genuinely disconnected, not falsely reporting "connected".
        expect(result.current.isConnected).toBe(false);
        expect(onConnectionLost).not.toHaveBeenCalled();

        // Sane UI state: the next mic tap (App.tsx's onToggleListening) calls reconnect() when
        // not connected -- but from this hook's own point of view a switch is STILL pending
        // (nothing has told it the fetch failed yet). Per the H4 fix this is now a no-op: readyState
        // reads genuinely CLOSED here, exactly the state the pre-fix CLOSED-branch toggle used as
        // its (wrong) signal to reopen a socket for "jerry" right away.
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // still no orphan socket

        // Only once App.tsx's handleSelectPersona learns the switch failed (selectPersona()'s
        // returned promise resolved false) does it call cancelSwitch() -- that is what actually
        // recovers the old persona's session, not the earlier reconnect() tap.
        act(() => result.current.cancelSwitch());
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketC = FakeWebSocket.instances[1];
        // Falls back to the SAME (old) persona -- the switch never actually happened.
        expect(socketC.url).toContain("persona=jerry");
        act(() => socketC.triggerOpen());
        expect(result.current.isConnected).toBe(true);
        // The queued session.update from the earlier tap is flushed exactly once, on this
        // recovered socket -- the no-op reconnect() call never duplicated or dropped it.
        expect(sessionUpdateCount(socketC)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
    });
});

describe("useRealTime reconnect() during a pending persona switch (issue GH-171 round 3, H4)", () => {
    it("a tap while the switch is pending never opens a socket for the OLD persona; once the switch completes, session.update reaches only the new persona's socket, exactly once (H4a)", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // App.tsx's handleSelectPersona: endSession({ switching: true }) runs synchronously; the
        // server's close for it arrives well before the async persona fetch resolves.
        act(() => result.current.endSession({ switching: true }));
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // suppressed cleanly, no orphan yet

        // The guest taps the mic again right here (App.tsx's onToggleListening: `if
        // (!isConnected) reconnect()`, then startSession()) -- readyState genuinely reads CLOSED,
        // exactly what the pre-fix reconnect() used as its (wrong) signal to reopen a socket for
        // "jerry" immediately. It must now be a no-op: switchingRef is still set.
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1);

        // The persona fetch eventually resolves: the real prop change lands, and
        // react-use-websocket's own url-keyed effect opens the one real replacement.
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // never more than one replacement
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
        expect(onConnectionLost).not.toHaveBeenCalled();
    });

    it("a second, repeated tap (reconnect() called again) while still pending remains a no-op; the eventual switch still delivers session.update exactly once, only to the new persona's socket (H4b)", async () => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        act(() => result.current.endSession({ switching: true }));
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1);

        // A second, independent tap before the fetch has resolved -- still idempotent, still no
        // socket for "jerry".
        act(() => result.current.reconnect());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1);

        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2);
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
        expect(onConnectionLost).not.toHaveBeenCalled();
    });
});

describe("useRealTime onMessage identity guard (issue #171 round 2, H3)", () => {
    it("drops a stale socket's extension.session_metadata (with an old resume id) instead of storing it under the switched-to persona or firing its handler", async () => {
        const onReceivedSessionMetadata = vi.fn();
        const { rerender } = renderHook(({ personaId }) => useRealTime({ personaId, onReceivedSessionMetadata }), {
            initialProps: { personaId: "jerry" }
        });

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // The persona prop has already moved on to "rick" (the async persona fetch resolved)...
        rerender({ personaId: "rick" });
        // ...but react-use-websocket hasn't even started connecting socketA's replacement yet
        // (still awaiting a fresh token fetch), so getWebSocket() still returns socketA -- the
        // ONLY thing that can tell this frame apart from a legitimate one is comparing the
        // identity captured when socketA itself opened ("jerry") to this render's props ("rick").
        expect(FakeWebSocket.instances).toHaveLength(1);

        act(() => socketA.triggerMessage(JSON.stringify({ type: "extension.session_metadata", resumeId: "RID-JERRY-STALE" })));

        expect(onReceivedSessionMetadata).not.toHaveBeenCalled();
        expect(sessionStorage.getItem(resumeStorageKey("rick"))).toBeNull();
        expect(sessionStorage.getItem(resumeStorageKey("jerry"))).toBeNull();
    });

    it("still processes a frame from the CURRENT socket normally when no switch is in flight (guard doesn't over-fire)", async () => {
        const onReceivedSessionMetadata = vi.fn();
        renderHook(({ personaId }) => useRealTime({ personaId, onReceivedSessionMetadata }), {
            initialProps: { personaId: "jerry" }
        });

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        act(() => socketA.triggerMessage(JSON.stringify({ type: "extension.session_metadata", resumeId: "RID-JERRY-LIVE" })));

        expect(onReceivedSessionMetadata).toHaveBeenCalledTimes(1);
        expect(sessionStorage.getItem(resumeStorageKey("jerry"))).toBe("RID-JERRY-LIVE");
    });
});

// Issue GH-180 round 2, R2: the pre-existing H3 guard above only ever exercised the ordering
// where the persona PROP has already moved before the stale frame arrives. The persona-switch
// confirm flow (issue #180/#183) opens a real gap: App.tsx's handleSelectPersona calls
// endSession({ switching: true }) synchronously, right when the guest confirms, but the
// `personaId` prop itself only moves once the async persona-detail fetch resolves. Anything the
// OLD socket still delivers in between -- a reply from the old persona already in flight when the
// guest confirmed -- passes BOTH of the pre-R2 guards: getWebSocket() still returns this exact
// socket (react-use-websocket hasn't started connecting a replacement yet) AND
// socketParamsAtOpenRef still matches the current (unchanged) props. Only comparing against the
// literal socket object captured by endSession() itself (useRealtime.tsx's endedSocketRef) can
// catch this.
describe("useRealTime onMessage identity guard closes the R2 gap: endSession({ switching: true }) runs before the persona prop moves (issue GH-180 round 2, R2)", () => {
    it("drops a stale socket's extension.session_metadata (with an old resume id) sent after endSession({ switching: true }) but before the persona prop rerender lands", async () => {
        const onReceivedSessionMetadata = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ personaId, onReceivedSessionMetadata }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // App.tsx's handleSelectPersona: endSession({ switching: true }) runs synchronously, well
        // before the async persona fetch resolves and the `personaId` prop actually moves.
        act(() => result.current.endSession({ switching: true }));
        expect(sentTypes(socketA)).toContain("extension.end_session");

        // A reply already in flight when the guest confirmed lands right here, on socketA, with
        // the persona prop still "jerry" -- the pre-R2 identity guards both still pass this frame.
        act(() => socketA.triggerMessage(JSON.stringify({ type: "extension.session_metadata", resumeId: "RID-JERRY-STALE" })));

        expect(onReceivedSessionMetadata).not.toHaveBeenCalled();
        expect(sessionStorage.getItem(resumeStorageKey("jerry"))).toBeNull();
        expect(sessionStorage.getItem(resumeStorageKey("rick"))).toBeNull();

        // The real prop change eventually lands, and the new socket's own frames are unaffected.
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());
        act(() => socketB.triggerMessage(JSON.stringify({ type: "extension.session_metadata", resumeId: "RID-RICK-LIVE" })));

        expect(onReceivedSessionMetadata).toHaveBeenCalledTimes(1);
        expect(sessionStorage.getItem(resumeStorageKey("rick"))).toBe("RID-RICK-LIVE");
    });
});

describe("useRealTime: a persona switch started while the socket is already intentionally down (issue GH-171 round 4, H5)", () => {
    // Shared setup for every H5 case: open socketA, close it with the given close code (idle 4000
    // or superseded 4002 -- both leave shouldConnect false with no live socket and no pending
    // reconnect timer, exactly the precondition H5 depends on), then run App.tsx's real ordering
    // for a persona switch (endSession({ switching: true }), then later -- once the persona fetch
    // resolves -- the prop change).
    async function openThenCloseIntentionally(closeCode: number, closeReason: string) {
        const socketA = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
        act(() => socketA.triggerOpen());
        act(() => socketA.triggerClose(closeCode, closeReason));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        return socketA;
    }

    it.each([
        ["idle 4000", 4000, "idle_timeout"],
        ["superseded 4002", 4002, "superseded"]
    ])("%s, switch, prop change, tap: exactly one new socket, persona=<new>, session.update exactly once", async (_label, closeCode, closeReason) => {
        const onConnectionLost = vi.fn();
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId, onConnectionLost }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = await openThenCloseIntentionally(closeCode, closeReason);
        expect(FakeWebSocket.instances).toHaveLength(1); // socket genuinely gone, no background reconnect

        act(() => result.current.endSession({ switching: true }));

        // The persona fetch resolves and the real prop change lands BEFORE the guest taps again.
        rerender({ personaId: "rick" });
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        // H5 without the fix: switchingRef is still true here forever, nothing ever reopens.
        expect(FakeWebSocket.instances).toHaveLength(1);

        act(() => result.current.reconnect());
        act(() => result.current.startSession());

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // exactly one new socket
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
    });

    it("idle 4000, switch, tap BEFORE the prop change: no socket created; then prop change: exactly one socket for the new persona, session.update once", async () => {
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        await openThenCloseIntentionally(4000, "idle_timeout");

        act(() => result.current.endSession({ switching: true }));

        // The guest taps before the async persona fetch resolves: switchingRef is still true, so
        // this must only record the request, not reopen anything (not even the old persona).
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // no socket created yet

        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // exactly one socket, for the new persona
        expect(sessionUpdateCount(socketB)).toBe(1);
    });

    it("idle 4000, switch, tap, cancelSwitch() (failed load): exactly one socket for the OLD persona, session.update once", async () => {
        const { result } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        await openThenCloseIntentionally(4000, "idle_timeout");

        act(() => result.current.endSession({ switching: true }));
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // still no orphan

        // The persona fetch fails: App.tsx's handleSelectPersona calls cancelSwitch() instead of
        // ever changing personaId. Falls back to the OLD persona -- the switch never happened.
        act(() => result.current.cancelSwitch());
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketC = FakeWebSocket.instances[1];
        expect(socketC.url).toContain("persona=jerry");
        act(() => socketC.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // exactly one recovery socket
        expect(sessionUpdateCount(socketC)).toBe(1);
    });

    it("a SUCCESSFUL switch (new socket opens), then idle 4000 on the new socket, then a tap: one fresh socket for the new persona (covers onOpen clearing switchingRef, mutation M8)", async () => {
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // An ordinary, successful switch: no intentional-down precondition here, onOpen is what
        // clears switchingRef on this path.
        act(() => result.current.endSession({ switching: true }));
        act(() => socketA.triggerClose(1000, "session_ended"));
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());
        expect(sessionUpdateCount(socketB)).toBe(0); // no startSession() called on this leg

        // Now the NEW socket goes idle. No switch is pending (switchingRef was cleared by onOpen
        // above) -- if it were not (M8), reconnect() below would wrongly defer forever instead of
        // using the ordinary idle-recovery CLOSED-branch toggle.
        act(() => socketB.triggerClose(4000, "idle_timeout"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(2);

        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(3));
        const socketC = FakeWebSocket.instances[2];
        expect(socketC.url).toContain("persona=rick");
        act(() => socketC.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(3); // exactly one fresh socket
        expect(sessionUpdateCount(socketC)).toBe(1);
    });
});

// Issue GH-180 round 2, R1: App.tsx's handleSelectPersona now awaits the target persona's fetch
// BEFORE calling endSession({ switching: true }) (so a failed fetch tears nothing down) -- but a
// tap landing in that in-flight window, before the fetch resolves, used to have no signal to key
// off: switchingRef only ever flipped true once endSession() itself ran, so reconnect() fell
// through to its ordinary CLOSED-branch recovery and reopened the socket for the OLD persona
// instead of deferring (Rick's own scratch runs against the round-2 head: the H5 Browser cases
// -- "Idle_closed_socket_then_persona_switch_then_tap" and its held-fetch sibling -- fail 3/3).
// beginSwitch() is the fix: it marks switchingRef true the instant the guest confirms, well
// before the fetch settles, so reconnect() defers exactly as it already does once endSession()
// itself has run.
describe("useRealTime: a tap in the gap between confirm and the persona fetch resolving (issue GH-180 round 2, R1 beginSwitch)", () => {
    it("idle 4000, beginSwitch() alone (fetch not yet resolved), tap: deferred, not reopened for the OLD persona", async () => {
        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());
        act(() => socketA.triggerClose(4000, "idle_timeout"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1);

        // The guest confirms: App.tsx calls beginSwitch() synchronously, well before its awaited
        // selectPersona() fetch resolves. endSession() has NOT run yet.
        act(() => result.current.beginSwitch());

        // A tap lands in that gap -- without the fix this reopens socketA's identity (jerry).
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // still deferred, nothing reopened yet

        // The fetch now resolves successfully: App.tsx calls the real endSession(), then the
        // persona prop lands.
        act(() => result.current.endSession({ switching: true }));
        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // exactly one socket, for the NEW persona
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
    });

    it("beginSwitch() alone, then the fetch fails: cancelSwitch() recovers the OLD persona, no endSession ever sent", async () => {
        const { result } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());
        act(() => socketA.triggerClose(4000, "idle_timeout"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        act(() => result.current.beginSwitch());
        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // still no orphan while the fetch is pending

        // The fetch fails: App.tsx calls cancelSwitch() instead of endSession(). personaId never
        // changes.
        act(() => result.current.cancelSwitch());
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketC = FakeWebSocket.instances[1];
        expect(socketC.url).toContain("persona=jerry");
        act(() => socketC.triggerOpen());

        expect(FakeWebSocket.instances).toHaveLength(2); // exactly one recovery socket, OLD persona
        expect(sessionUpdateCount(socketC)).toBe(1);
    });
});

// Issue GH-171 round 4, item 4a (PR-body accuracy): `useRealtime.personaResume.test.tsx`'s
// "sends extension.resume as the literal first frame" test renders test-beta directly with a
// mocked react-use-websocket; no switch ever happens there. This is the through-a-switch version
// Rick asked for, against the real library/FakeWebSocket harness so the App-order close/tap/open
// sequencing is genuine, not just a single persona's first render.
describe("useRealTime: extension.resume is frame 0 ahead of session.update, through a real persona switch (issue GH-171 round 4, item 4a)", () => {
    it("after a switch (close, tap, prop change, open), the new socket's first two frames are extension.resume (new persona's id) then session.update, exactly once each; the old persona's own key is cleared", async () => {
        sessionStorage.setItem(resumeStorageKey("jerry"), "RID-JERRY");
        sessionStorage.setItem(resumeStorageKey("rick"), "RID-RICK");

        const { result, rerender } = renderHook(
            ({ personaId }) => useRealTime({ enableInputAudioTranscription: true, personaId }),
            { initialProps: { personaId: "jerry" } }
        );

        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1));
        const socketA = FakeWebSocket.instances[0];
        act(() => socketA.triggerOpen());

        // App.tsx's exact ordering: endSession({ switching: true }) first, then (if !isConnected)
        // reconnect() and startSession() from the guest's tap, then -- once the async persona
        // fetch resolves -- the real prop change.
        act(() => result.current.endSession({ switching: true }));
        act(() => socketA.triggerClose(1000, "session_ended"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        // endSession({ switching: true }) clears the OLD persona's own resume key immediately --
        // before the new persona's socket ever opens (matches the single-persona-render test in
        // useRealtime.personaResume.test.tsx; this is the same guarantee holding through an actual
        // switch, not just at first render).
        expect(sessionStorage.getItem(resumeStorageKey("jerry"))).toBeNull();
        expect(sessionStorage.getItem(resumeStorageKey("rick"))).toBe("RID-RICK");

        act(() => result.current.reconnect());
        act(() => result.current.startSession());
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });
        expect(FakeWebSocket.instances).toHaveLength(1); // still no orphan for "jerry"

        rerender({ personaId: "rick" });
        await waitFor(() => expect(FakeWebSocket.instances).toHaveLength(2));
        const socketB = FakeWebSocket.instances[1];
        expect(socketB.url).toContain("persona=rick");
        act(() => socketB.triggerOpen());

        const sent = sentTypes(socketB);
        expect(sent[0]).toBe("extension.resume");
        expect(JSON.parse(socketB.sent[0]).resume_id).toBe("RID-RICK");
        expect(sent[1]).toBe("session.update");
        expect(sent.filter(type => type === "extension.resume")).toHaveLength(1);
        expect(sessionUpdateCount(socketB)).toBe(1);
        expect(sessionUpdateCount(socketA)).toBe(0);
        expect(sessionStorage.getItem(resumeStorageKey("jerry"))).toBeNull();
    });
});
