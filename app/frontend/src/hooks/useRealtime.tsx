import useWebSocket, { ReadyState } from "react-use-websocket";
import { useRef, useCallback, useEffect, useState } from "react";

import { acquireApiToken } from "@/auth/tokenService";
import { authConfig } from "@/auth/authConfig";
import { AUTH_REQUIRED_EVENT } from "@/auth/authorizedFetch";
import {
    InputAudioBufferAppendCommand,
    InputAudioBufferClearCommand,
    Message,
    ResponseAudioDelta,
    ResponseAudioTranscriptDelta,
    ResponseDone,
    SessionUpdateCommand,
    ExtensionMiddleTierToolResponse,
    ResponseInputAudioTranscriptionCompleted,
    ExtensionSessionMetadata,
    ExtensionRoundTripToken,
    ExtensionSessionResumed,
    ExtensionResumeRejected,
    ExtensionRateLimited
} from "@/types";

type Parameters = {
    useDirectAoaiApi?: boolean; // If true, the middle tier will be skipped and the AOAI ws API will be called directly
    aoaiEndpointOverride?: string;
    aoaiApiKeyOverride?: string;
    aoaiModelOverride?: string;

    /** Issue #80 F1: the session's persona, set once before connecting (ADR-001 decision 2 --
     * no mid-conversation switching) and sent as `/realtime?persona=<id>` (rtmt.py reads it via
     * `request.query.get("persona")`, falling back to the catalog default when omitted). */
    personaId?: string;
    /** Issue #80 F10: the session's chosen model, set once before connecting (same
     * ADR-001 decision 2 rule as `personaId` -- no mid-conversation switching, locked while a
     * session is active) and sent as `/realtime?model=<id>` (rtmt.py's `request.query.get("model")`,
     * already merged via PR-106/PR-122, falls back to the persona's pipeline default when omitted). */
    modelId?: string;
    /** issue 165: the session's bound menu mode ("breakfast"/"lunch"), set once before connecting --
     * same ADR-001 decision 2 rule as `personaId`/`modelId` (no mid-conversation switching) --
     * and sent as `/realtime?mode=<mode>` (rtmt.py's `request.query.get("mode")`, only meaningful
     * for a persona that declares `features.dayparts`; falls back to that persona's own default
     * -- "lunch" -- when omitted, and is silently ignored for every other persona). */
    menuMode?: string;

    enableInputAudioTranscription?: boolean;
    onWebSocketOpen?: () => void;
    onWebSocketClose?: () => void;
    /** Fired whenever an open socket closes. The server session (and its order) is gone. */
    onConnectionLost?: (info: ConnectionLostInfo) => void;
    onWebSocketError?: (event: Event) => void;
    onWebSocketMessage?: (event: MessageEvent<any>) => void;

    onReceivedResponseCreated?: (message: Message) => void;
    onReceivedResponseAudioDelta?: (message: ResponseAudioDelta) => void;
    onReceivedInputAudioBufferSpeechStarted?: (message: Message) => void;
    onReceivedResponseDone?: (message: ResponseDone) => void;
    onReceivedExtensionMiddleTierToolResponse?: (message: ExtensionMiddleTierToolResponse) => void;
    onReceivedSessionMetadata?: (message: ExtensionSessionMetadata) => void;
    onReceivedSessionResumed?: (message: ExtensionSessionResumed) => void;
    onReceivedResumeRejected?: (message: ExtensionResumeRejected) => void;
    /** Background reconnect gave up (retries exhausted); the socket stays down until reconnect(). */
    onReconnectGaveUp?: () => void;
    onReceivedRoundTripToken?: (message: ExtensionRoundTripToken) => void;
    /** A model response was rate-limited and the server's silent retry failed too (docs/rate_limit_recovery.md). */
    onReceivedRateLimited?: (message: ExtensionRateLimited) => void;
    onReceivedResponseAudioTranscriptDelta?: (message: ResponseAudioTranscriptDelta) => void;
    onReceivedInputAudioTranscriptionCompleted?: (message: ResponseInputAudioTranscriptionCompleted) => void;
    onReceivedError?: (message: Message) => void;
};

// Server closes idle sessions with this code (session_manager.IDLE_CLOSE_CODE).
// It is intentional, so the hook stays disconnected until the guest taps again
// instead of silently opening a new socket that mic audio could leak into.
// The session is already gone server-side: idle is never resumable.
export const WS_CLOSE_IDLE_TIMEOUT = 4000;
// Another socket resumed this session (session_manager.SUPERSEDED_CLOSE_CODE).
export const WS_CLOSE_SUPERSEDED = 4002;
// Reply to extension.end_session: 1000 with this reason.
export const WS_CLOSE_SESSION_ENDED_REASON = "session_ended";

// Per-tab resume credential (docs/order_resume.md), namespaced per persona (issue #80 F7,
// Rick's PR-110 review item 5): each persona gets its own sessionStorage slot so switching personas
// never reads or clobbers a DIFFERENT persona's resume id -- switching back to a persona later
// still finds (or doesn't find) exactly the resume state that persona itself left behind.
const RESUME_STORAGE_KEY_PREFIX = "drivethru.resumeId.";
const DEFAULT_RESUME_BUCKET = "default";

export function resumeStorageKey(personaId?: string): string {
    return `${RESUME_STORAGE_KEY_PREFIX}${personaId ?? DEFAULT_RESUME_BUCKET}`;
}

export function createResumeStore(personaId?: string) {
    const key = resumeStorageKey(personaId);
    return {
        get(): string | null {
            try {
                return sessionStorage.getItem(key);
            } catch {
                return null;
            }
        },
        set(id: string) {
            try {
                sessionStorage.setItem(key, id);
            } catch {
                // storage unavailable: resume just won't work in this tab
            }
        },
        clear() {
            try {
                sessionStorage.removeItem(key);
            } catch {
                // ignore
            }
        }
    };
}

/** idle: 4000; superseded: 4002; ended: 1000 session_ended; transport: anything else (resumable). */
export type CloseKind = "idle" | "superseded" | "ended" | "transport";

export function classifyClose(event: Pick<CloseEvent, "code" | "reason">): CloseKind {
    if (event.code === WS_CLOSE_IDLE_TIMEOUT) return "idle";
    if (event.code === WS_CLOSE_SUPERSEDED) return "superseded";
    if (event.code === 1000 && event.reason === WS_CLOSE_SESSION_ENDED_REASON) return "ended";
    return "transport";
}

/** The three identity props that select which socket is "current" (issue GH-171 round 2):
 * a persona/model/menuMode change is what makes react-use-websocket replace the socket, so every
 * stale-vs-current comparison in this file (render-time openRef reset, onClose's switchedSinceOpen,
 * onMessage's guard) needs to agree on exactly the same three fields. */
type SocketIdentity = { personaId?: string; modelId?: string; menuMode?: string };

function sameIdentity(a: SocketIdentity, b: SocketIdentity): boolean {
    return a.personaId === b.personaId && a.modelId === b.modelId && a.menuMode === b.menuMode;
}

export type ConnectionLostInfo = {
    code: number;
    reason: string;
    idle: boolean;
    kind: CloseKind;
    /** A background reconnect will follow and present the stored resume id. */
    resuming: boolean;
};

// Exponential backoff: 1s, 2s, 4s, 8s, 16s, max 30s
const MAX_RETRIES = 10;
const BASE_DELAY_MS = 1000;
const MAX_DELAY_MS = 30000;

async function fetchSessionToken(): Promise<string | null> {
    try {
        const resp = await fetch("/api/auth/session");
        if (!resp.ok) return null;
        const data = await resp.json();
        return data.token ?? null;
    } catch {
        // Endpoint doesn't exist or server unavailable — graceful fallback
        return null;
    }
}

/**
 * Issue GH-145 (ADR-002, design §18.7, PR GH-148 review round 2 item B2): the `/realtime`
 * WebSocket carries a fresh Entra bearer token as `?access_token=`, acquired right before every
 * connect/reconnect -- initial mount, every background reconnect after a transport close, the
 * `session_ended` reconnect, and a manual `reconnect()` -- so a long-idle browser tab never
 * presents a stale token to the middle tier. Returns `null` when unconfigured (Development
 * pass-through, see `authConfig.ts`) or when the visitor is signed out, in which case
 * `access_token` is simply omitted from the URL (see `getSocketUrl` below); `AuthGate` (not this
 * hook) owns interactive sign-in.
 */
async function fetchAccessToken(): Promise<string | null> {
    try {
        return await acquireApiToken();
    } catch {
        return null;
    }
}

/** Refreshes both the HMAC session token (`/api/auth/session`) and the Entra access token in
 * parallel -- used at every connect/reconnect site so neither one is ever stale relative to the
 * other. */
async function fetchConnectTokens(): Promise<{ sessionToken: string | null; accessToken: string | null }> {
    const [sessionToken, accessToken] = await Promise.all([fetchSessionToken(), fetchAccessToken()]);
    return { sessionToken, accessToken };
}

export default function useRealTime({
    useDirectAoaiApi,
    aoaiEndpointOverride,
    aoaiApiKeyOverride,
    aoaiModelOverride,
    personaId,
    modelId,
    menuMode,
    enableInputAudioTranscription,
    onWebSocketOpen,
    onWebSocketClose,
    onConnectionLost,
    onWebSocketError,
    onWebSocketMessage,
    onReceivedResponseCreated,
    onReceivedResponseDone,
    onReceivedResponseAudioDelta,
    onReceivedResponseAudioTranscriptDelta,
    onReceivedInputAudioBufferSpeechStarted,
    onReceivedExtensionMiddleTierToolResponse,
    onReceivedInputAudioTranscriptionCompleted,
    onReceivedSessionMetadata,
    onReceivedSessionResumed,
    onReceivedResumeRejected,
    onReconnectGaveUp,
    onReceivedRoundTripToken,
    onReceivedRateLimited,
    onReceivedError
}: Parameters) {
    const [shouldConnect, setShouldConnect] = useState(true);

    // Recomputed every render (cheap) so a `personaId` change (persona switch) is picked up by
    // every closure below on its very next render, without needing its own memoization seam.
    const resumeStore = createResumeStore(personaId);

    /**
     * PR GH-148 review round 2, item B2: an async URL FACTORY, not a plain string. react-use-
     * websocket's `getUrl` (`node_modules/react-use-websocket/dist/lib/get-url.js`) awaits `url()`
     * fresh on every call to `startRef.current()` -- initial mount, every background reconnect
     * (`shouldReconnect` returning true after a transport close), and every explicit `reconnect()`
     * below (which just flips `shouldConnect`) -- so this factory, not a value computed once into
     * React state, is what has to fetch a fresh HMAC session token and a fresh Entra access token
     * per attempt. The previous implementation fetched both tokens ONCE on mount into
     * `sessionToken`/`accessToken` state and built a plain string URL from that state; react-use-
     * websocket's own auto-reconnect logic re-used that SAME string on every retry, so a
     * long-idle tab kept presenting an increasingly stale token on every background reconnect and
     * resume attempt (stale HMAC token after its 900s TTL, and eventually a stale Entra token too).
     *
     * When Entra is configured (`authConfig.isConfigured`) but `acquireApiToken()` resolves to
     * `null` (signed out, or MSAL has no cached account), this dispatches `AUTH_REQUIRED_EVENT`
     * (item B3 -- `EntraAuthGate` listens and drops back to sign-in) and THROWS. `getUrl` catches
     * that; react-use-websocket never sets `retryOnError` here, so it does not retry and instead
     * resolves the whole call to `null`, which makes the library log "Failed to get a valid URL.
     * WebSocket connection aborted." and leaves the socket CLOSED -- i.e. this hook never opens a
     * `/realtime` socket without a bearer token for a configured-but-signed-out visitor. This is
     * distinct from the Development pass-through (`authConfig.isConfigured === false`), where a
     * `null` access token is the expected, intentional case and the connection proceeds without
     * `access_token` in the URL.
     */
    const getSocketUrl = useCallback(async (): Promise<string> => {
        if (useDirectAoaiApi) {
            // GA realtime surface: /openai/v1/realtime addressed by `model`,
            // replacing the retired /openai/realtime?deployment=&api-version= form. Never carries
            // an Entra token: this is the direct-AOAI debug mode, a different origin entirely.
            return `${aoaiEndpointOverride}/openai/v1/realtime?api-key=${aoaiApiKeyOverride}&model=${aoaiModelOverride}`;
        }

        const { sessionToken, accessToken } = await fetchConnectTokens();
        if (authConfig.isConfigured && !accessToken) {
            window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
            throw new Error("Entra access token unavailable for /realtime connect; aborting.");
        }

        const base = `/realtime`;
        const params = new URLSearchParams();
        if (sessionToken) params.set("token", sessionToken);
        if (accessToken) params.set("access_token", accessToken);
        if (personaId) params.set("persona", personaId);
        if (modelId) params.set("model", modelId);
        if (menuMode) params.set("mode", menuMode);
        const query = params.toString();
        return query ? `${base}?${query}` : base;
    }, [useDirectAoaiApi, aoaiEndpointOverride, aoaiApiKeyOverride, aoaiModelOverride, personaId, modelId, menuMode]);

    // Ref to break circular dependency: callbacks need sendJsonMessage,
    // but sendJsonMessage comes from useWebSocket which takes the callbacks.
    const sendJsonMessageRef = useRef<(msg: object, keep?: boolean) => void>(() => {});
    // Ref to react-use-websocket's own `getWebSocket` accessor (issue GH-171): lets onOpen/onClose
    // tell whether the native event they were handed still belongs to the CURRENT socket. A
    // persona/model/mode switch replaces the socket (getSocketUrl's identity changes), and the
    // OLD socket's close event can arrive after the NEW one has already opened -- without this
    // check, that stale close unconditionally flips `openRef` back to false and the queued
    // session.update is never flushed. `getWebSocket` is undefined in every existing mocked test
    // (they don't return it from their `react-use-websocket` mock), so the guards below always
    // short-circuit to "proceed as before" there, preserving all prior behaviour.
    const getWebSocketRef = useRef<(() => WebSocket | EventSource | null) | undefined>(undefined);

    // The hook owns the outgoing queue: react-use-websocket is only ever called
    // with keep=false, so its own queue stays empty and cannot flush anything
    // ahead of extension.resume, which the server honours only as the first frame.
    const openRef = useRef(false);
    const pendingRef = useRef<object[]>([]);
    // Set by endSession(): the coming 1000 session_ended close is ours, and frames
    // sent after it (e.g. a fast tap) belong to the fresh session that replaces it.
    const endingRef = useRef(false);
    // Snapshot of the identity props in effect when the current socket opened (issue GH-171). Used
    // by onClose to tell a persona/model/mode switch (these differ from the current render's
    // props by the time the close arrives) apart from an ordinary same-identity close (new order,
    // idle timeout, transport drop): a switch is already being handled by react-use-websocket's
    // own url-keyed effect, so treating it like a normal close would both duplicate the reconnect
    // (the reported "two sockets open in quick succession") and surface a spurious ended/idle/
    // superseded/lost notice for what is really just a clean handover to the next persona.
    const socketParamsAtOpenRef = useRef<SocketIdentity>({});
    // Set by endSession({ switching: true }) (issue GH-171 round 2, H2): a persona switch is in
    // flight via App.tsx's handleSelectPersona, which calls endSession() synchronously but only
    // updates the `personaId` prop later, once the async persona fetch resolves. onClose uses this
    // (together with switchedSinceOpen) to tell "this ended close is the first half of a switch"
    // apart from an ordinary explicit-new-order ended close, in BOTH possible arrival orders.
    // Issue GH-171 round 3, H4: this now stays `true` for the WHOLE pending-switch window --
    // onClose's own branch below deliberately no longer clears it once the old socket's close has
    // been suppressed. It is cleared only by onOpen (the switch succeeded: some socket, new or
    // recovered, is live again) or by cancelSwitch() (the switch failed). reconnect() reads it to
    // tell "a switch is pending, the url just hasn't moved yet" apart from a genuinely dead socket.
    const switchingRef = useRef(false);
    // Issue GH-171 round 3, H4: set by reconnect() when it is called while switchingRef is still
    // true -- a tap landed before the pending switch resolved one way or the other. Neither onOpen
    // (success) nor cancelSwitch() (failure) know on their own whether the guest actually asked to
    // reconnect in the meantime; this is the only record of that. Cleared by whichever of the two
    // runs first.
    const reconnectRequestedRef = useRef(false);

    // Synchronous render-time reset (issue GH-171 round 2, H1/H1b): a persona/model/menuMode
    // change makes react-use-websocket replace the socket, but the OLD socket's own close event is
    // a genuine async browser event that can arrive at any time relative to the NEW socket being
    // constructed -- including after the new socket already exists (assigned to react-use-
    // websocket's internal ref) but before it has actually opened. onClose's own stale guard
    // (getWebSocketRef identity check, below) correctly no-ops for that late close since it no
    // longer targets the current socket -- but that means nothing else ever flips `openRef` back
    // to false for the switch, leaving it stuck `true` from the OLD socket's onOpen for the entire
    // window the NEW socket is still connecting. A send() during that window reads openRef as
    // "open", hands the frame to react-use-websocket's sendJsonMessage, and that silently drops it
    // (keep=false; the new socket's readyState isn't OPEN yet) -- the exact bug this reset closes:
    // render always happens before any event/effect from this same prop change can run, so by the
    // time any tap handler or socket event fires, openRef already reflects the fact that whatever
    // socket is still around belongs to a superseded identity.
    if (openRef.current && !sameIdentity(socketParamsAtOpenRef.current, { personaId, modelId, menuMode })) {
        openRef.current = false;
    }

    const send = useCallback((msg: object, keep = true) => {
        if (openRef.current) {
            sendJsonMessageRef.current(msg, false);
        } else if (keep) {
            pendingRef.current.push(msg);
        }
    }, []);

    const onMessageReceived = useCallback((event: MessageEvent<any>) => {
        onWebSocketMessage?.(event);

        let message: Message;
        try {
            message = JSON.parse(event.data);
        } catch (e) {
            console.error("Failed to parse JSON message:", e);
            throw e;
        }

        switch (message.type) {
            case "response.created":
                // Earliest signal that the AI is about to speak.
                // Flush any buffered mic audio on the server to prevent echo.
                sendJsonMessageRef.current({ type: "input_audio_buffer.clear" }, false);
                onReceivedResponseCreated?.(message);
                break;
            case "response.done":
                onReceivedResponseDone?.(message as ResponseDone);
                break;
            case "response.audio.delta":
                onReceivedResponseAudioDelta?.(message as ResponseAudioDelta);
                break;
            case "response.audio_transcript.delta":
                onReceivedResponseAudioTranscriptDelta?.(message as ResponseAudioTranscriptDelta);
                break;
            case "input_audio_buffer.speech_started":
                onReceivedInputAudioBufferSpeechStarted?.(message);
                break;
            case "conversation.item.input_audio_transcription.completed":
                onReceivedInputAudioTranscriptionCompleted?.(message as ResponseInputAudioTranscriptionCompleted);
                break;
            case "extension.middle_tier_tool_response":
                onReceivedExtensionMiddleTierToolResponse?.(message as ExtensionMiddleTierToolResponse);
                break;
            case "extension.session_metadata": {
                const metadata = message as ExtensionSessionMetadata;
                if (!useDirectAoaiApi && metadata.resumeId) resumeStore.set(metadata.resumeId);
                onReceivedSessionMetadata?.(metadata);
                break;
            }
            case "extension.session_resumed": {
                const resumed = message as ExtensionSessionResumed;
                if (resumed.resume_id) resumeStore.set(resumed.resume_id);
                onReceivedSessionResumed?.(resumed);
                break;
            }
            case "extension.resume_rejected":
                // The fresh session's extension.session_metadata (with a new id) follows.
                resumeStore.clear();
                onReceivedResumeRejected?.(message as ExtensionResumeRejected);
                break;
            case "extension.round_trip_token":
                onReceivedRoundTripToken?.(message as ExtensionRoundTripToken);
                break;
            case "extension.rate_limited":
                onReceivedRateLimited?.(message as ExtensionRateLimited);
                break;
            case "error":
                onReceivedError?.(message);
                break;
        }
    }, [
        onWebSocketMessage,
        onReceivedResponseCreated,
        onReceivedResponseDone,
        onReceivedResponseAudioDelta,
        onReceivedResponseAudioTranscriptDelta,
        onReceivedInputAudioBufferSpeechStarted,
        onReceivedInputAudioTranscriptionCompleted,
        onReceivedExtensionMiddleTierToolResponse,
        onReceivedSessionMetadata,
        onReceivedSessionResumed,
        onReceivedResumeRejected,
        onReceivedRoundTripToken,
        onReceivedRateLimited,
        onReceivedError,
        useDirectAoaiApi,
        personaId,
        modelId
    ]);

    // Stale-frame guard (issue GH-171 round 2, H3): react-use-websocket's `onMessage` has no
    // staleness check of its own, so a frame already in flight on the OLD socket when a persona/
    // model/menuMode switch starts can still reach `onMessageReceived` after the switch has begun
    // -- e.g. an `extension.session_metadata` carrying the OLD persona's resume id, which would
    // otherwise be stored via `resumeStore.set` closing over the NEW render's `personaId`, writing
    // it into the NEW persona's sessionStorage bucket instead of the old one it actually belongs
    // to. Two independent checks, mirroring onOpen/onClose below: `getWebSocketRef` catches a frame
    // that isn't even from the socket react-use-websocket considers current; `sameIdentity` catches
    // the narrower case where it IS the current socket, but this hook's own bookkeeping
    // (`socketParamsAtOpenRef`, set when that socket opened) no longer matches the render that owns
    // it -- a switch is already under way and this socket's remaining frames are stale.
    const onMessageGuarded = useCallback(
        (event: MessageEvent<any>) => {
            if (getWebSocketRef.current && getWebSocketRef.current() !== event.target) return;
            if (!sameIdentity(socketParamsAtOpenRef.current, { personaId, modelId, menuMode })) return;
            onMessageReceived(event);
        },
        [onMessageReceived, personaId, modelId, menuMode]
    );

    const { sendJsonMessage, readyState, getWebSocket } = useWebSocket(getSocketUrl, {
        onOpen: (event) => {
            // Stale guard (issue GH-171): if a newer socket already exists, this onOpen belongs to
            // a socket react-use-websocket has already superseded (shouldn't normally fire, but
            // costs nothing to check symmetrically with onClose below).
            if (getWebSocketRef.current && getWebSocketRef.current() !== event.target) return;
            socketParamsAtOpenRef.current = { personaId, modelId, menuMode };
            openRef.current = true;
            // Any socket opening -- the new persona's own socket on a successful switch, or a
            // reconnect to the OLD persona after a FAILED one (issue GH-171 round 2, H2) -- ends
            // whatever switch attempt was pending. Left set, a later ordinary endSession() (e.g.
            // "start a new order") would be wrongly suppressed as "still switching" in onClose.
            // Issue GH-171 round 3, H4: also clears any reconnect() that was recorded (not acted
            // on) while this switch was pending -- it was for whichever persona is NOW live, so
            // there is nothing left to do with it.
            switchingRef.current = false;
            reconnectRequestedRef.current = false;
            // Literal first frame on every open when this tab holds a resume id.
            const resumeId = useDirectAoaiApi ? null : resumeStore.get();
            if (resumeId) {
                sendJsonMessageRef.current({ type: "extension.resume", resume_id: resumeId }, false);
            }
            for (const queued of pendingRef.current.splice(0)) {
                sendJsonMessageRef.current(queued, false);
            }
            onWebSocketOpen?.();
        },
        onClose: (event) => {
            // Stale guard (issue GH-171): react-use-websocket invokes this hook's own onClose
            // unconditionally, even for a socket it has already torn down and replaced (its
            // internal readyState/lastMessage setters ARE guarded this way, but onOpen/onClose
            // are not). If a newer socket already exists, this close is for the one we just
            // replaced -- openRef, pendingRef and the connection-lost notice all belong to the
            // new socket now, so there is nothing to do.
            if (getWebSocketRef.current && getWebSocketRef.current() !== event.target) return;

            // A persona/model/mode switch changes getSocketUrl's identity, which makes
            // react-use-websocket's own url-keyed effect replace the socket on its own --
            // independently of whatever close code/reason this one actually closed with. Detect
            // that by comparing the identity props captured when THIS socket opened to the
            // current render's props: if they differ, a switch is already in flight and this
            // close is just the old half of a clean handover, not a real ended/idle/superseded/
            // transport event.
            const paramsAtOpen = socketParamsAtOpenRef.current;
            const switchedSinceOpen = !sameIdentity(paramsAtOpen, { personaId, modelId, menuMode });
            // issue GH-171 round 2, H2: the app's REAL ordering calls endSession({ switching: true })
            // synchronously, but the `personaId` prop it is switching to only lands once the async
            // persona fetch resolves -- so the server's close for the end_session we just sent can
            // arrive BEFORE that prop change (switchedSinceOpen still false here) just as easily as
            // after it (switchedSinceOpen already true). switchingRef catches the first ordering;
            // switchedSinceOpen alone already caught the second. Without the switchingRef half, this
            // close falls through to the "ended" branch below, which forces its OWN reconnect for
            // the CURRENT (still old) identity -- opening a second, orphaned socket for the persona
            // being switched AWAY from, alongside whatever socket the real prop change opens next.
            const wasSwitching = switchingRef.current;
            openRef.current = false;
            if (switchedSinceOpen || (endingRef.current && wasSwitching)) {
                // Don't touch pendingRef/resumeStore/shouldConnect: react-use-websocket is
                // already opening (or has already opened) the replacement off this render's new
                // getSocketUrl, and the outgoing queue may already hold a frame meant for it
                // (e.g. startSession() called right after the switch). Manufacturing our own
                // reconnect here, or clearing state the new socket needs, is exactly the double
                // -connect / dropped-session.update bug this guard exists to prevent.
                endingRef.current = false;
                // Issue GH-171 round 3, H4: deliberately NOT `switchingRef.current = false` here
                // anymore. This close can land well before the persona fetch it belongs to has
                // settled either way -- clearing the flag this early is exactly what let a tap's
                // reconnect() (readyState is genuinely CLOSED here, same-identity url hasn't
                // moved yet) mistake "a switch is pending" for "the session is just dead", and
                // reopen a socket for the OLD persona. switchingRef now stays true for the whole
                // pending-switch window and is cleared only by onOpen (success) or cancelSwitch()
                // (failure) -- see reconnect() and cancelSwitch() below.
                return;
            }
            const kind = classifyClose(event);
            if (kind === "ended") {
                // Explicit new order: open a fresh session straight away, as a page load would.
                if (!endingRef.current) pendingRef.current = [];
                endingRef.current = false;
                resumeStore.clear();
                setShouldConnect(false);
                // A genuine async gap (not two synchronous calls) so React commits the `false`
                // state as its own render before flipping back to `true`: React 18 batches
                // same-net-value setState calls made within one synchronous event into a single
                // no-op commit (`Object.is(true, true)` bails out of scheduling a render), and the
                // socket-managing effect (keyed on this `connect` boolean) would never see a
                // transition -- so no fresh socket would open for this explicit "start a new
                // order" close (`shouldReconnect` deliberately returns `false` for `kind ===
                // "ended"`, so the library's own auto-reconnect never picks this up either).
                // `getSocketUrl` above fetches its own fresh token(s) on the resulting connect
                // attempt (item B2), so this gap no longer needs to pre-fetch anything itself.
                Promise.resolve().then(() => setShouldConnect(true));
            } else if (kind !== "transport") {
                // Final for this session: no background reconnect, and nothing
                // queued for it may leak into the next one.
                setShouldConnect(false);
                pendingRef.current = [];
                // 4002 keeps the id: another socket owns the session now.
                if (kind !== "superseded") resumeStore.clear();
            }
            // A 401/expired-token close (kind === "transport", incl. code 4001) needs no explicit
            // token refresh here (item B2): `shouldReconnect` below returns `true` for any
            // transport close, and react-use-websocket's own background reconnect calls
            // `getSocketUrl` fresh on that next attempt, which fetches a brand new session token
            // and Entra access token itself.
            const resuming = kind === "transport" && !useDirectAoaiApi && !!resumeStore.get();
            onConnectionLost?.({ code: event.code, reason: event.reason ?? "", idle: kind === "idle", kind, resuming });
            onWebSocketClose?.();
        },
        onError: event => onWebSocketError?.(event),
        onMessage: onMessageGuarded,
        shouldReconnect: (event: CloseEvent) => classifyClose(event) === "transport",
        onReconnectStop: () => {
            setShouldConnect(false);
            onReconnectGaveUp?.();
        },
        reconnectAttempts: MAX_RETRIES,
        reconnectInterval: (attemptNumber: number) => {
            const delay = Math.min(BASE_DELAY_MS * Math.pow(2, attemptNumber), MAX_DELAY_MS);
            // Add jitter to prevent thundering herd
            return delay + Math.random() * 500;
        }
    }, shouldConnect);

    const isConnected = readyState === ReadyState.OPEN;

    // Re-open after an idle close or exhausted retries. No token pre-fetch needed here (item B2):
    // `getSocketUrl` fetches a fresh session token and Entra access token itself on this attempt.
    const reconnect = useCallback(() => {
        // Issue GH-171 round 3, H4: a persona switch is still pending (the old socket's close has
        // already been suppressed above, but the fetch it's waiting on hasn't settled either way
        // -- so getSocketUrl's identity, and therefore react-use-websocket's own url-keyed effect,
        // hasn't moved yet). readyState is genuinely CLOSED right now, same as a real dead
        // session -- that is exactly what let the old CLOSED-branch toggle below mistake this for
        // one and reopen a socket for the persona being switched AWAY from, which then received
        // the queued session.update while the new persona's eventual socket got nothing. Record
        // the request instead of acting on it: onOpen (switch succeeds) or cancelSwitch() (switch
        // fails) decide what happens next.
        if (switchingRef.current) {
            reconnectRequestedRef.current = true;
            return;
        }
        if (shouldConnect) {
            // issue GH-171 round 2, H2: a FAILED persona switch leaves `shouldConnect` already
            // `true` with no socket actually open. endSession({ switching: true }) closed the old
            // socket server-side, but since the persona load failed, `personaId` (and every other
            // identity prop) never actually changes -- so react-use-websocket's own url-keyed
            // effect, which is what normally opens the replacement socket, never re-runs (its url
            // never changed), and onClose above deliberately didn't force its own reconnect either
            // (that's exactly the orphan-socket bug this file now avoids). Nothing else will ever
            // open a new socket for this identity on its own, so a manual reconnect() (e.g. the
            // guest tapping the mic again) has to force react-use-websocket's effect to re-fire.
            // (By the time this runs, cancelSwitch() has already cleared switchingRef -- see
            // above -- so this branch is reached only once the failure is confirmed.)
            if (readyState === ReadyState.CLOSED) {
                setShouldConnect(false);
                // Same async-gap trick as the "ended" dance above: a same-tick false->true toggle
                // is a no-op React never commits, so the socket-managing effect would never see a
                // transition and no new socket would open.
                Promise.resolve().then(() => setShouldConnect(true));
            }
            return;
        }
        setShouldConnect(true);
    }, [shouldConnect, readyState]);

    // Issue GH-171 round 3, H4: the explicit failure signal from App.tsx's handleSelectPersona,
    // called once selectPersona()'s returned promise resolves `false` (the persona detail fetch
    // failed, or the id hadn't actually changed). Clears switchingRef so reconnect() (and onClose's
    // branch selection above) stop treating this as a pending switch. If a tap already called
    // reconnect() while the switch was still in flight (reconnectRequestedRef), or the socket is
    // simply sitting CLOSED with nothing else ever going to reopen it (the orphan-prevention above
    // left it that way on purpose), this performs the same false-then-true toggle reconnect()
    // itself uses -- recovering the OLD persona's session (the switch never actually happened)
    // rather than leaving the guest stranded on a dead socket.
    const cancelSwitch = useCallback(() => {
        switchingRef.current = false;
        const wasRequested = reconnectRequestedRef.current;
        reconnectRequestedRef.current = false;
        // Issue GH-171 round 4, H5: the switch started with the socket already intentionally down
        // (idle 4000, superseded 4002, retries exhausted -- shouldConnect is false, so there is no
        // url-keyed effect that will ever open anything on its own). There is nothing to recover
        // TO here (the fetch failed, so personaId never changed): only reopen the old persona if
        // the guest actually tapped while the switch was pending.
        if (!shouldConnect) {
            if (wasRequested) {
                setShouldConnect(true);
            }
            return;
        }
        if (readyState === ReadyState.CLOSED || wasRequested) {
            setShouldConnect(false);
            Promise.resolve().then(() => setShouldConnect(true));
        }
    }, [shouldConnect, readyState]);

    // Issue GH-171 round 4, H5: a persona switch started while the socket was already
    // intentionally down (shouldConnect false: idle 4000, superseded 4002, retries exhausted).
    // endSession({ switching: true }) sets switchingRef but there is no open socket to close and
    // no url-keyed effect waiting to fire once personaId/modelId/menuMode land, so nothing would
    // ever clear switchingRef or open the new persona's socket: the mic stays dead until "New
    // order". This effect is the hook's own finish line for that case -- it fires once the persona
    // fetch resolves and the new identity actually lands in props. If a switch is still pending at
    // that point and the socket is still intentionally down, the switch is done: clear switchingRef
    // so reconnect() stops deferring, and if the guest already tapped in the meantime
    // (reconnectRequestedRef), open the new persona's socket now (getSocketUrl reads the identity
    // props at call time, so by the time this effect runs they already point at the new persona).
    useEffect(() => {
        if (switchingRef.current && !shouldConnect) {
            switchingRef.current = false;
            if (reconnectRequestedRef.current) {
                reconnectRequestedRef.current = false;
                setShouldConnect(true);
            }
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [personaId, modelId, menuMode]);

    // Keep refs in sync so onMessageReceived can call sendJsonMessage, and so onOpen/onClose can
    // tell a stale socket's event apart from the current one (issue GH-171).
    useEffect(() => {
        sendJsonMessageRef.current = sendJsonMessage;
        getWebSocketRef.current = getWebSocket;
    }, [sendJsonMessage, getWebSocket]);

    const startSession = () => {
        const command: SessionUpdateCommand = {
            type: "session.update",
            session: {
                turn_detection: {
                    type: "server_vad",
                    threshold: 0.7,
                    prefix_padding_ms: 300,
                    silence_duration_ms: 500
                }
            }
        };

        if (enableInputAudioTranscription) {
            command.session.input_audio_transcription = {
                model: "whisper-1"
            };
        }

        // Kept for the next socket; sent after extension.resume when one is pending.
        send(command);
    };

    const addUserAudio = (base64Audio: string) => {
        const command: InputAudioBufferAppendCommand = {
            type: "input_audio_buffer.append",
            audio: base64Audio
        };

        // keep=false: drop, never queue, audio while the socket isn't open —
        // queued frames are replayed onto the next socket ahead of session.update.
        send(command, false);
    };

    const inputAudioBufferClear = () => {
        const command: InputAudioBufferClearCommand = {
            type: "input_audio_buffer.clear"
        };

        send(command, false);
    };

    const cancelResponse = () => {
        send({ type: "response.cancel" }, false);
    };

    const sendVerboseLogging = (enabled: boolean) => {
        send({ type: "extension.set_verbose_logging", enabled });
    };

    const sendLogToFile = (enabled: boolean) => {
        send({ type: "extension.set_log_to_file", enabled });
    };

    const sendVoiceChoice = (voice: string) => {
        send({ type: "extension.set_voice", voice });
    };

    // Explicit new order (switching undefined/false): the server deletes the order and closes 1000
    // session_ended, after which a fresh socket opens for the SAME identity. Persona switch
    // (switching: true, issue GH-171 round 2, H2): App.tsx's handleSelectPersona calls this
    // synchronously, before the new persona's id has actually landed in `personaId` -- marking
    // `switchingRef` here (regardless of whether a frame was actually sent below) is what lets
    // onClose tell this apart from an ordinary ended close no matter which order the close and the
    // prop change arrive in, so it doesn't force its own extra reconnect for the OLD identity on
    // top of whatever socket the persona change opens next. The id is dropped either way so no
    // later open resumes it; frames sent from here on wait for the new socket.
    const endSession = (options?: { switching?: boolean }) => {
        switchingRef.current = !!options?.switching;
        resumeStore.clear();
        pendingRef.current = [];
        if (!useDirectAoaiApi && openRef.current) {
            send({ type: "extension.end_session" }, false);
            endingRef.current = true;
            openRef.current = false;
        }
    };

    return {
        startSession,
        addUserAudio,
        inputAudioBufferClear,
        cancelResponse,
        sendVerboseLogging,
        sendLogToFile,
        sendVoiceChoice,
        endSession,
        isConnected,
        reconnect,
        cancelSwitch
    };
}
