import useWebSocket, { ReadyState } from "react-use-websocket";
import { useRef, useCallback, useEffect, useState } from "react";

import { acquireApiToken } from "@/auth/tokenService";
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
 * Issue GH-145 (ADR-002, design §18.7): the `/realtime` WebSocket carries a fresh Entra bearer
 * token as `?access_token=`, acquired right before every connect/reconnect -- initial mount, the
 * `session_ended` reconnect, the 401/expired-token reconnect, and a manual `reconnect()` -- so a
 * long-idle browser tab never presents a stale token to the middle tier. Returns `null` when
 * unconfigured (Development pass-through, see `authConfig.ts`) or when the visitor is signed out,
 * in which case `access_token` is simply omitted from the URL (see `buildWsEndpoint` below);
 * `AuthGate` (not this hook) owns interactive sign-in.
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
    const [sessionToken, setSessionToken] = useState<string | null>(null);
    const [accessToken, setAccessToken] = useState<string | null>(null);
    // Don't open the socket until the token fetch settles, otherwise the first
    // socket is torn down and replaced as soon as the token arrives.
    const [tokenReady, setTokenReady] = useState(!!useDirectAoaiApi);
    const [shouldConnect, setShouldConnect] = useState(true);

    // Recomputed every render (cheap) so a `personaId` change (persona switch) is picked up by
    // every closure below on its very next render, without needing its own memoization seam.
    const resumeStore = createResumeStore(personaId);

    // Fetch a session token and a fresh Entra access token on mount (graceful — either being null
    // means no token required/available for that piece).
    useEffect(() => {
        if (useDirectAoaiApi) return;
        fetchConnectTokens().then(({ sessionToken, accessToken }) => {
            setSessionToken(sessionToken);
            setAccessToken(accessToken);
            setTokenReady(true);
        });
    }, [useDirectAoaiApi]);

    const buildWsEndpoint = () => {
        if (useDirectAoaiApi) {
            // GA realtime surface: /openai/v1/realtime addressed by `model`,
            // replacing the retired /openai/realtime?deployment=&api-version= form.
            return `${aoaiEndpointOverride}/openai/v1/realtime?api-key=${aoaiApiKeyOverride}&model=${aoaiModelOverride}`;
        }
        const base = `/realtime`;
        const params = new URLSearchParams();
        if (sessionToken) params.set("token", sessionToken);
        if (accessToken) params.set("access_token", accessToken);
        if (personaId) params.set("persona", personaId);
        if (modelId) params.set("model", modelId);
        const query = params.toString();
        return query ? `${base}?${query}` : base;
    };

    const wsEndpoint = buildWsEndpoint();

    // Ref to break circular dependency: callbacks need sendJsonMessage,
    // but sendJsonMessage comes from useWebSocket which takes the callbacks.
    const sendJsonMessageRef = useRef<(msg: object, keep?: boolean) => void>(() => {});

    // The hook owns the outgoing queue: react-use-websocket is only ever called
    // with keep=false, so its own queue stays empty and cannot flush anything
    // ahead of extension.resume, which the server honours only as the first frame.
    const openRef = useRef(false);
    const pendingRef = useRef<object[]>([]);
    // Set by endSession(): the coming 1000 session_ended close is ours, and frames
    // sent after it (e.g. a fast tap) belong to the fresh session that replaces it.
    const endingRef = useRef(false);
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

    const { sendJsonMessage, readyState } = useWebSocket(tokenReady ? wsEndpoint : null, {
        onOpen: () => {
            openRef.current = true;
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
            openRef.current = false;
            const kind = classifyClose(event);
            if (kind === "ended") {
                // Explicit new order: open a fresh session straight away, as a page load would.
                if (!endingRef.current) pendingRef.current = [];
                endingRef.current = false;
                resumeStore.clear();
                setShouldConnect(false);
                if (useDirectAoaiApi) {
                    setShouldConnect(true);
                } else {
                    fetchConnectTokens().then(({ sessionToken, accessToken }) => {
                        setSessionToken(sessionToken);
                        setAccessToken(accessToken);
                        setShouldConnect(true);
                    });
                }
            } else if (kind !== "transport") {
                // Final for this session: no background reconnect, and nothing
                // queued for it may leak into the next one.
                setShouldConnect(false);
                pendingRef.current = [];
                // 4002 keeps the id: another socket owns the session now.
                if (kind !== "superseded") resumeStore.clear();
            } else if (event.code === 4001 || event.reason?.includes("expired")) {
                // 401 close → refresh both tokens and retry
                fetchConnectTokens().then(({ sessionToken, accessToken }) => {
                    setSessionToken(sessionToken);
                    setAccessToken(accessToken);
                });
            }
            const resuming = kind === "transport" && !useDirectAoaiApi && !!resumeStore.get();
            onConnectionLost?.({ code: event.code, reason: event.reason ?? "", idle: kind === "idle", kind, resuming });
            onWebSocketClose?.();
        },
        onError: event => onWebSocketError?.(event),
        onMessage: onMessageReceived,
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

    // Re-open after an idle close or exhausted retries, with a fresh token
    // (the old one may have expired while the page sat idle).
    const reconnect = useCallback(async () => {
        if (shouldConnect) return;
        if (!useDirectAoaiApi) {
            const { sessionToken, accessToken } = await fetchConnectTokens();
            setSessionToken(sessionToken);
            setAccessToken(accessToken);
        }
        setShouldConnect(true);
    }, [shouldConnect, useDirectAoaiApi]);

    // Keep ref in sync so onMessageReceived can call sendJsonMessage
    useEffect(() => {
        sendJsonMessageRef.current = sendJsonMessage;
    }, [sendJsonMessage]);

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

    // Explicit new order: the server deletes the order and closes 1000
    // session_ended, after which a fresh socket opens. The id is dropped either
    // way so no later open resumes it; frames sent from here on wait for the new socket.
    const endSession = () => {
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
        reconnect
    };
}
