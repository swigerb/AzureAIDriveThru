import { useState, useEffect, useRef, useCallback, lazy, Suspense, memo } from "react";
import { Mic, MicOff, Menu, MessageSquare, LogOut, ChevronDown } from "lucide-react";
import { FaGithub } from "react-icons/fa";
import { AnimatePresence, motion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

import StatusMessage, { ConnectionNotice } from "@/components/ui/status-message";
import { resolveTextRole, textRoleClass } from "@/lib/personaTextRoles";
import MenuPanel from "@/components/ui/menu-panel";
import OrderSummary, { calculateOrderSummary, OrderItem, OrderSummaryProps } from "@/components/ui/order-summary";
import TranscriptPanel from "@/components/ui/transcript-panel";
import PersonaPicker from "@/components/ui/persona-picker";
import PersonaSwitchConfirmDialog from "@/components/ui/persona-switch-confirm-dialog";
import BackendPicker from "@/components/ui/backend-picker";
const Settings = lazy(() => import("@/components/ui/settings"));
import useRealTime from "@/hooks/useRealtime";
import useAzureSpeech from "@/hooks/useAzureSpeech";
import useAudioRecorder from "@/hooks/useAudioRecorder";
import useAudioPlayer from "@/hooks/useAudioPlayer";

import { ExtensionMiddleTierToolResponse, ExtensionRateLimited, ExtensionRoundTripToken, ExtensionSessionMetadata, ExtensionSessionResumed } from "./types";

import { ThemeProvider, useTheme } from "./context/theme-context";
import { DummyDataProvider, useDummyDataContext } from "@/context/dummy-data-context";
import { AzureSpeechProvider, useAzureSpeechOnContext } from "@/context/azure-speech-context";
import { AuthGate } from "@/auth/AuthGate";
import { authConfig } from "@/auth/authConfig";
import { signOutInteractive } from "@/auth/signOut";
import { PersonaProvider, usePersonaContext } from "@/context/persona-context";
import { resolveVoice } from "@/lib/voices";
import { resolveModelId, modelStorageKey } from "@/lib/models";
import { resolveMenuMode, menuModeStorageKey } from "@/lib/menuMode";
import { apologyClipUrl, playApologyClip } from "@/lib/apology";
import { personaAssetUrl } from "@/lib/personaAssets";
import type { PersonaDetail, PersonaHeroSpotlight, PersonaTextRoles } from "@/types/persona";

import azureLogo from "@/assets/azurelogo.svg";

export type SessionIdentifiersState = {
    sessionToken: string;
    roundTripIndex: number;
    roundTripToken: string;
};

/**
 * Issue #80 F7: the dummy/demo transcripts and order the "Dummy Data" settings toggle shows are no
 * longer bundled with the frontend (`src/data/dummyTranscripts.json`/`dummyOrder.json` are
 * retired) -- they're fetched from the active persona's pack at the conventional
 * `assets/demo/{dummyOrder,dummyTranscripts}.json` paths (same `personaAssetUrl` fallback the
 * apology clip already uses, since these aren't declared fields on `PersonaDetail.assets`). A
 * persona that ships no demo data simply renders the empty state -- this is a debug/demo-only
 * feature, so that's an acceptable, non-crashing degradation rather than something to paper over
 * with a re-embedded copy of the persona's data.
 */
function useDemoData(personaId: string, enabled: boolean) {
    const [dummyOrder, setDummyOrder] = useState<OrderSummaryProps>({ items: [], total: 0, tax: 0, finalTotal: 0 });
    const [dummyTranscripts, setDummyTranscripts] = useState<Array<{ text: string; isUser: boolean; timestamp: Date }>>([]);

    useEffect(() => {
        if (!enabled) return;
        let cancelled = false;
        // Rick's PR-110 review item 5 (issue #80 F7): clear immediately on a persona switch so
        // the previous pack's demo data doesn't linger on screen while the new pack's fetch
        // (below) is in flight.
        setDummyOrder({ items: [], total: 0, tax: 0, finalTotal: 0 });
        setDummyTranscripts([]);

        (async () => {
            try {
                const response = await fetch(personaAssetUrl(personaId, "assets/demo/dummyOrder.json"));
                if (!response.ok) throw new Error(`dummyOrder.json request failed: ${response.status}`);
                const items = (await response.json()) as OrderItem[];
                if (!cancelled) setDummyOrder(calculateOrderSummary(items));
            } catch {
                if (!cancelled) setDummyOrder({ items: [], total: 0, tax: 0, finalTotal: 0 });
            }
        })();

        (async () => {
            try {
                const response = await fetch(personaAssetUrl(personaId, "assets/demo/dummyTranscripts.json"));
                if (!response.ok) throw new Error(`dummyTranscripts.json request failed: ${response.status}`);
                const raw = (await response.json()) as Array<{ text: string; isUser: boolean; timestamp: string }>;
                if (!cancelled) setDummyTranscripts(raw.map(transcript => ({ ...transcript, timestamp: new Date(transcript.timestamp) })));
            } catch {
                if (!cancelled) setDummyTranscripts([]);
            }
        })();

        return () => {
            cancelled = true;
        };
    }, [personaId, enabled]);

    return { dummyOrder, dummyTranscripts };
}

function SonicApp() {
    const { t, i18n } = useTranslation();
    const [isRecording, setIsRecording] = useState(false);
    const [isMobile, setIsMobile] = useState(false);
    const { useAzureSpeechOn } = useAzureSpeechOnContext();
    const { useDummyData } = useDummyDataContext();
    const { theme } = useTheme();
    const { personas, backends, current, logoUrl, error: personaError, selectPersona } = usePersonaContext();

    const [transcripts, setTranscripts] = useState<Array<{ text: string; isUser: boolean; timestamp: Date }>>([]);
    const { dummyOrder, dummyTranscripts } = useDemoData(current.id, useDummyData);
    // Issue GH-180: the persona id a guest picked while a switch still needs confirming (a
    // non-empty order or an active conversation) -- null means no confirmation dialog is open.
    const [pendingPersonaSwitchId, setPendingPersonaSwitchId] = useState<string | null>(null);
    // Issue GH-180 round 2, R4: the persona a switch is currently resolving TO, from the moment
    // handleSelectPersona starts running until it settles (success or failure). The ref is read
    // synchronously by requestPersonaSwitch -- immune to React's batched state updates -- so a
    // second pick landing during the async persona-fetch window is a plain no-op instead of
    // re-entering handleSelectPersona (which would call endSession() a second time) or reaching
    // selectPersona's own "already selected" early return, whose `false` result used to make
    // handleSelectPersona call realtime.cancelSwitch() against a switch that was still genuinely
    // pending -- reopening a socket for the persona being switched AWAY from. The state mirror
    // drives the picker's displayed value (the pending target, not a snap back to the old persona)
    // while a switch is in flight.
    const switchInFlightRef = useRef<string | null>(null);
    const [switchTargetId, setSwitchTargetId] = useState<string | null>(null);

    const initialOrder: OrderSummaryProps = {
        items: [],
        total: 0,
        tax: 0,
        finalTotal: 0
    };

    const [order, setOrder] = useState<OrderSummaryProps>(initialOrder);
    const [sessionIdentifiers, setSessionIdentifiers] = useState<SessionIdentifiersState | null>(null);
    const [tokenHistory, setTokenHistory] = useState<SessionIdentifiersState[]>([]);
    const [showSessionTokens, setShowSessionTokens] = useState<boolean>(() => {
        if (typeof window === "undefined") return true;
        const stored = localStorage.getItem("showSessionTokens");
        return stored === null ? true : stored === "true";
    });
    const [verboseLogging, setVerboseLogging] = useState<boolean>(() => {
        return localStorage.getItem("verboseLogging") === "true";
    });
    const [logToFile, setLogToFile] = useState<boolean>(() => {
        return localStorage.getItem("verboseLogToFile") === "true";
    });
    const [voiceChoice, setVoiceChoice] = useState<string>(() => {
        return resolveVoice(localStorage.getItem("voiceChoice"), current.voice.default);
    });
    // Issue #80 F10: persisted per persona (unlike `voiceChoice` above), so switching personas
    // never leaks one persona's chosen model onto another's -- `resolveModelId` falls back to
    // this persona's own default whenever there's no stored choice yet, or the stored one is no
    // longer one of this persona's selectable options.
    const [modelId, setModelId] = useState<string>(() => {
        return resolveModelId(localStorage.getItem(modelStorageKey(current.id)), current.models);
    });
    // issue 165: only meaningful for a persona that declares `features.dayparts` -- persisted per
    // persona (same rule as `modelId` above) so switching personas never leaks one persona's
    // chosen mode onto another's. A persona with no `features.dayparts` never reads/writes this
    // key at all (see the resolve effect below) and never sends `?mode=` (`useRealTime` only sets
    // it when truthy), so this is a genuine no-op for every persona but one today.
    const [menuMode, setMenuMode] = useState<string>(() => {
        return current.features.dayparts ? resolveMenuMode(localStorage.getItem(menuModeStorageKey(current.id))) : "";
    });

    useEffect(() => {
        localStorage.setItem("showSessionTokens", showSessionTokens.toString());
    }, [showSessionTokens]);

    useEffect(() => {
        localStorage.setItem("verboseLogging", verboseLogging.toString());
    }, [verboseLogging]);

    useEffect(() => {
        localStorage.setItem("verboseLogToFile", logToFile.toString());
    }, [logToFile]);

    useEffect(() => {
        localStorage.setItem("voiceChoice", voiceChoice);
    }, [voiceChoice]);

    // Re-resolves whenever the bound persona changes (initial load -- including a backend switch
    // that carried `?model=` -- or a persona switch via `handleSelectPersona` below).
    //
    // Rick's PR 134 review, item 2: prefers `?model=` from the address bar (set by
    // `lib/backends.ts`'s `backendTargetUrl` when hopping backends) over this persona's stored
    // choice, validated through `resolveModelId` against THIS backend's own `/api/personas/{id}`
    // list for the bound persona (`current.models` -- the C# catalog can differ from Python's): a
    // listed id is adopted, an unlisted one falls through to the persona's default exactly like
    // any other stale/invalid stored choice. Once consumed, `model` is stripped from the address
    // bar with `history.replaceState` (leaving any other query params, e.g. `?persona=`, alone) so
    // a later reload of this same URL doesn't keep re-pinning a choice the guest may since have
    // changed via the picker.
    //
    // Rick's PR 134 review, item 4: persists the resolved id right here, in the same effect that
    // computes it, rather than in a second effect keyed on `modelId` alone -- the previous split
    // wrote whatever `modelId` last was under the NEW persona's storage key on the same commit
    // `current` swapped to that persona (both effects run, in order, on the render where
    // `current.id` changed), briefly leaving the new persona's key holding the OLD persona's model
    // id. The only other write site is `onModelChange` below (an explicit user choice).
    useEffect(() => {
        const fromQuery = typeof window !== "undefined" ? new URLSearchParams(window.location.search).get("model") : null;
        const resolved = resolveModelId(fromQuery ?? localStorage.getItem(modelStorageKey(current.id)), current.models);
        setModelId(resolved);
        localStorage.setItem(modelStorageKey(current.id), resolved);
        if (fromQuery !== null && typeof window !== "undefined") {
            const url = new URL(window.location.href);
            url.searchParams.delete("model");
            window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [current.id, current.models]);

    // issue 165: same "re-resolve on persona change" rule as the `modelId` effect above, simplified --
    // there's no `?mode=` address-bar handoff to consume (no analogous backend-hop path sets one
    // yet), just this persona's own stored choice (or the shared default) gated on whether this
    // persona declares `features.dayparts` at all. A persona with no `features.dayparts` always
    // resolves to `""` (never a stale mode from a PREVIOUS dayparts-capable persona) and never
    // touches localStorage, exactly mirroring `menu_mode`'s backend-side normalization
    // (order_state.OrderState.create_session).
    useEffect(() => {
        if (!current.features.dayparts) {
            setMenuMode("");
            return;
        }
        const resolved = resolveMenuMode(localStorage.getItem(menuModeStorageKey(current.id)));
        setMenuMode(resolved);
        localStorage.setItem(menuModeStorageKey(current.id), resolved);
    }, [current.id, current.features.dayparts]);

    const handleSessionIdentifiers = useCallback((message: ExtensionSessionMetadata | ExtensionRoundTripToken) => {
        const snapshot: SessionIdentifiersState = {
            sessionToken: message.sessionToken,
            roundTripIndex: message.roundTripIndex,
            roundTripToken: message.roundTripToken
        };
        setSessionIdentifiers(snapshot);
        setTokenHistory(prev => [snapshot, ...prev]);
    }, []);

    const isSessionActiveRef = useRef(false);
    const awaitingGreetingDoneRef = useRef(false);
    const greetingAudioSeenRef = useRef(false);
    const startMicInFlightRef = useRef<Promise<void> | null>(null);
    const isAiSpeakingRef = useRef(false);

    // A transport drop (1001/1002/1006/1011) is resumable: the hook reconnects
    // and presents the tab's resume id, and the server holds the order for a
    // short grace period. Anything else (idle 4000, rejected resume, retries
    // exhausted) means the server-side order is gone and the next tap starts fresh.
    const [connectionNotice, setConnectionNotice] = useState<ConnectionNotice>(null);
    const serverSessionLostRef = useRef(false);
    // null: no resume in flight; otherwise whether the guest was mid-conversation at the drop.
    const resumePendingRef = useRef<boolean | null>(null);
    // This socket carries a resumed session: no greeting will come, so a tap restarts the mic at once.
    const resumedSessionRef = useRef(false);
    const resumedNoticeTimerRef = useRef<number | null>(null);
    const orderItemCountRef = useRef(0);
    useEffect(() => {
        orderItemCountRef.current = order.items.length;
    }, [order]);

    // Rate-limit recovery (docs/rate_limit_recovery.md): the apology clip is playing.
    const apologyPlayingRef = useRef(false);
    const clearRateLimitNotice = useCallback(() => {
        setConnectionNotice(current => (current === "rateLimited" || current === "rateLimitedFinal" ? null : current));
    }, []);

    const flashResumedNotice = useCallback(() => {
        setConnectionNotice("resumed");
        if (resumedNoticeTimerRef.current !== null) window.clearTimeout(resumedNoticeTimerRef.current);
        resumedNoticeTimerRef.current = window.setTimeout(() => {
            resumedNoticeTimerRef.current = null;
            setConnectionNotice(current => (current === "resumed" ? null : current));
        }, 4000);
    }, []);

    const realtime = useRealTime({
        personaId: current.id,
        modelId,
        menuMode,
        enableInputAudioTranscription: true,
        onWebSocketOpen: () => console.log("WebSocket connection opened"),
        onWebSocketClose: () => console.log("WebSocket connection closed"),
        onConnectionLost: ({ code, reason, idle, kind, resuming }) => {
            console.warn(`WebSocket closed (code=${code}${reason ? `, reason=${reason}` : ""})`);
            if (useAzureSpeechOn) return;
            // Our own "New order": a fresh socket follows by itself, and a tap made
            // meanwhile carries straight on into it.
            if (kind === "ended") return;
            const wasActive = isSessionActiveRef.current;
            if (wasActive) void stopConversation();
            if (resuming) {
                // A failed reconnect attempt closes again; remember the first answer.
                resumePendingRef.current = wasActive || resumePendingRef.current === true;
                if (wasActive || orderItemCountRef.current > 0) setConnectionNotice("reconnecting");
                return;
            }
            resumePendingRef.current = null;
            resumedSessionRef.current = false;
            serverSessionLostRef.current = true;
            if (idle || kind === "superseded" || wasActive || orderItemCountRef.current > 0) {
                setConnectionNotice(idle ? "idle" : kind === "superseded" ? "superseded" : "lost");
            }
        },
        onReconnectGaveUp: () => {
            if (resumePendingRef.current === null) return;
            resumePendingRef.current = null;
            serverSessionLostRef.current = true;
            setConnectionNotice("lost");
        },
        onWebSocketError: event => console.error("WebSocket error:", event),
        onReceivedError: message => console.error("error", message),
        onReceivedResponseCreated: () => {
            if (!isSessionActiveRef.current) return;
            // Mute mic at the EARLIEST response signal — before audio deltas arrive.
            // The server also receives input_audio_buffer.clear (sent by useRealTime)
            // to flush any echo already in the pipeline.
            if (!isAiSpeakingRef.current) {
                isAiSpeakingRef.current = true;
                muteAudioRecording();
            }
        },
        onReceivedResponseAudioDelta: message => {
            if (!isSessionActiveRef.current) return;
            greetingAudioSeenRef.current = true;
            playAudio(message.delta);
        },
        onReceivedInputAudioBufferSpeechStarted: () => {
            // User speech detected - stop AI playback (barge-in) and unmute mic
            stopAudioPlayer();
            clearRateLimitNotice();
            if (isAiSpeakingRef.current) {
                isAiSpeakingRef.current = false;
                unmuteAudioRecording();
            }
        },
        onReceivedExtensionMiddleTierToolResponse: ({ tool_name, tool_result }: ExtensionMiddleTierToolResponse) => {
            if (tool_name === "update_order" || tool_name === "get_order" || tool_name === "reset_order") {
                const orderSummary: OrderSummaryProps = JSON.parse(tool_result);
                setOrder(orderSummary);

                console.log("Order Total:", orderSummary.total);
                console.log("Tax:", orderSummary.tax);
                console.log("Final Total:", orderSummary.finalTotal);
            }
        },
        onReceivedSessionMetadata: message => {
            resumedSessionRef.current = false;
            handleSessionIdentifiers(message);
        },
        onReceivedSessionResumed: (message: ExtensionSessionResumed) => {
            // Same shape as a tool result: the ticket comes back exactly as it was.
            setOrder(message.order_summary);
            handleSessionIdentifiers({
                type: "extension.round_trip_token",
                sessionToken: message.session_token,
                roundTripIndex: message.round_trip_index,
                roundTripToken: message.round_trip_token
            });
            serverSessionLostRef.current = false;
            resumedSessionRef.current = true;
            const wasActive = resumePendingRef.current === true;
            resumePendingRef.current = null;
            if (isSessionActiveRef.current) {
                // The guest tapped while we were reconnecting; that conversation carries on.
                flashResumedNotice();
            } else if (wasActive) {
                void resumeConversation();
            } else {
                setConnectionNotice(message.order_summary.items.length > 0 ? "tapToResume" : null);
            }
        },
        onReceivedResumeRejected: ({ reason }) => {
            console.warn(`Order resume rejected (${reason}); starting a fresh order`);
            resumedSessionRef.current = false;
            const wasPending = resumePendingRef.current !== null;
            resumePendingRef.current = null;
            const hadItems = orderItemCountRef.current > 0;
            setOrder(initialOrder);
            if (isSessionActiveRef.current) return; // the guest's own tap already started the fresh session
            serverSessionLostRef.current = true;
            if (wasPending || hadItems) setConnectionNotice("resumeRejected");
        },
        onReceivedRoundTripToken: handleSessionIdentifiers,
        onReceivedRateLimited: ({ final }: ExtensionRateLimited) => {
            if (!isSessionActiveRef.current) return;
            // The failed response never finished, so nothing unmuted the mic.
            isAiSpeakingRef.current = false;
            if (final) {
                // Out of retries: ask the guest to say it again, and let them.
                setConnectionNotice("rateLimitedFinal");
                if (!apologyPlayingRef.current) unmuteAudioRecording();
                return;
            }
            // The silent retry failed too; a second one is coming. Say sorry, with
            // the mic muted so the clip can't echo into server VAD (guest speech
            // would cancel that retry).
            setConnectionNotice("rateLimited");
            if (apologyPlayingRef.current) return;
            apologyPlayingRef.current = true;
            muteAudioRecording();
            const clipUrl = apologyClipUrl(current.id, current.assets.apologyClip, i18n.language);
            const finishApology = () => {
                apologyPlayingRef.current = false;
                if (isSessionActiveRef.current && !isAiSpeakingRef.current) unmuteAudioRecording();
            };
            if (clipUrl) {
                void playApologyClip(clipUrl).finally(finishApology);
            } else {
                // This persona declares no apology clip -- unmute right away instead of playing
                // nothing for the usual clip duration.
                finishApology();
            }
        },
        onReceivedInputAudioTranscriptionCompleted: message => {
            const newTranscriptItem = {
                text: message.transcript,
                isUser: true,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onReceivedResponseDone: message => {
            const transcript = message.response.output.map(output => output.content?.map(content => content.transcript).join(" ")).join(" ");
            // TEMP DIAGNOSTIC (removed once the real cause is found): isolate whether this handler
            // itself runs/extracts the transcript correctly, versus a later effect clobbering it.
            console.log("DIAG onReceivedResponseDone:", JSON.stringify({ transcript, willReturnEarly: !transcript }));
            if (!transcript) return;
            try {
                clearRateLimitNotice();
                console.log("DIAG after clearRateLimitNotice, about to call setTranscripts");
            } catch (error) {
                console.log("DIAG clearRateLimitNotice THREW:", String(error), (error as Error | undefined)?.stack);
                throw error;
            }

            const newTranscriptItem = {
                text: transcript,
                isUser: false,
                timestamp: new Date()
            };
            try {
                setTranscripts(prev => {
                    const next = [...prev, newTranscriptItem];
                    console.log("DIAG setTranscripts updater:", JSON.stringify({ prevLength: prev.length, nextLength: next.length }));
                    return next;
                });
                console.log("DIAG after setTranscripts call (call itself did not throw)");
            } catch (error) {
                console.log("DIAG setTranscripts THREW:", String(error), (error as Error | undefined)?.stack);
                throw error;
            }

            // AI finished speaking - unmute the microphone
            if (isAiSpeakingRef.current) {
                isAiSpeakingRef.current = false;
                unmuteAudioRecording();
            }

            if (awaitingGreetingDoneRef.current && isSessionActiveRef.current) {
                awaitingGreetingDoneRef.current = false;

                if (!startMicInFlightRef.current) {
                    startMicInFlightRef.current = (async () => {
                        // If we received audio deltas for the greeting, wait until playback drains.
                        if (greetingAudioSeenRef.current) {
                            await waitForAudioDrain(2000);
                        }

                        if (!isSessionActiveRef.current) return;
                        await startAudioRecording();
                    })().finally(() => {
                        startMicInFlightRef.current = null;
                    });
                }
            }
        }
    });

    const azureSpeech = useAzureSpeech({
        onReceivedToolResponse: ({ tool_name, tool_result }: ExtensionMiddleTierToolResponse) => {
            if (tool_name === "update_order") {
                const orderSummary: OrderSummaryProps = JSON.parse(tool_result);
                setOrder(orderSummary);

                console.log("Order Total:", orderSummary.total);
                console.log("Tax:", orderSummary.tax);
                console.log("Final Total:", orderSummary.finalTotal);
            }
        },
        onSpeechToTextTranscriptionCompleted: (message: { transcript: string }) => {
            const newTranscriptItem = {
                text: message.transcript,
                isUser: true,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onModelResponseDone: (message: { response: { output: Array<{ content?: Array<{ transcript: string }> }> } }) => {
            const transcript = message.response.output
                .map(output => output.content?.map(content => content.transcript).join(" "))
                .join(" ");
            if (!transcript) return;

            const newTranscriptItem = {
                text: transcript,
                isUser: false,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onError: (error: unknown) => console.error("Error:", error)
    });

    const { reset: resetAudioPlayer, play: playAudio, stop: stopAudioPlayer, waitForDrain: waitForAudioDrain } =
        useAudioPlayer();

    // Barge-in handler: when the Recorder detects the user speaking while
    // the mic is muted (AI is talking), unmute, cancel the AI response,
    // and stop audio playback so the user can be heard immediately.
    // BLOCKED during greeting to prevent echo-driven greeting loop.
    const handleBargeIn = useCallback(() => {
        if (!isAiSpeakingRef.current) return;
        if (awaitingGreetingDoneRef.current) return;
        console.log("Barge-in detected — interrupting AI");
        isAiSpeakingRef.current = false;
        stopAudioPlayer();
        // Cancel the AI's in-flight response so the middleware also
        // resets echo suppression and lets our audio through.
        realtime.cancelResponse();
    }, [stopAudioPlayer, realtime]);

    const { start: startAudioRecording, stop: stopAudioRecording, mute: muteAudioRecording, unmute: unmuteAudioRecording } = useAudioRecorder({
        onAudioRecorded: useAzureSpeechOn ? azureSpeech.addUserAudio : realtime.addUserAudio,
        onBargeIn: handleBargeIn
    });

    const stopConversation = async () => {
        await stopAudioRecording();
        stopAudioPlayer();
        isSessionActiveRef.current = false;
        isAiSpeakingRef.current = false;
        awaitingGreetingDoneRef.current = false;
        clearRateLimitNotice();
        if (useAzureSpeechOn) {
            azureSpeech.inputAudioBufferClear();
        } else {
            realtime.inputAudioBufferClear();
        }
        setIsRecording(false);
    };

    // Mid-conversation drop, resumed: pick the conversation straight back up.
    // Voice/VAD come back via session.update (the server suppresses the greeting),
    // and the mic restarts without a tap when the browser allows it.
    const resumeConversation = async () => {
        isSessionActiveRef.current = true;
        isAiSpeakingRef.current = false;
        awaitingGreetingDoneRef.current = false;
        greetingAudioSeenRef.current = false;
        setIsRecording(true);
        realtime.startSession();
        if (verboseLogging) {
            realtime.sendVerboseLogging(true);
            if (logToFile) realtime.sendLogToFile(true);
        }
        let micStarted = false;
        try {
            micStarted = await startAudioRecording();
        } catch (error) {
            console.warn("Mic could not restart after reconnect:", error);
        }
        if (!isSessionActiveRef.current) return;
        if (!micStarted) {
            // Needs a user gesture (suspended AudioContext / permission prompt).
            await stopConversation();
            setConnectionNotice("tapToResume");
            return;
        }
        flashResumedNotice();
    };

    const startNewOrder = async () => {
        if (isRecording) await stopConversation();
        realtime.endSession();
        resumePendingRef.current = null;
        resumedSessionRef.current = false;
        serverSessionLostRef.current = false;
        setOrder(initialOrder);
        setTranscripts([]);
        setSessionIdentifiers(null);
        setTokenHistory([]);
        setConnectionNotice(null);
    };

    const onToggleListening = async () => {
        if (!isRecording) {
            const continuing = !useAzureSpeechOn && resumedSessionRef.current && !serverSessionLostRef.current;
            if (!continuing) setSessionIdentifiers(null);
            setConnectionNotice(null);
            if (!useAzureSpeechOn) {
                if (serverSessionLostRef.current) {
                    serverSessionLostRef.current = false;
                    setOrder(initialOrder);
                }
                // Idle close / exhausted retries leave the socket down on purpose.
                // startSession() below is queued and sent once the new socket opens.
                if (!realtime.isConnected) void realtime.reconnect();
            }

            // Start session and playback immediately, but delay mic capture until the greeting finishes.
            isSessionActiveRef.current = true;
            isAiSpeakingRef.current = false;
            awaitingGreetingDoneRef.current = !useAzureSpeechOn && !continuing;
            greetingAudioSeenRef.current = false;

            await resetAudioPlayer();

            if (useAzureSpeechOn) {
                // AzureSpeech mode doesn't play a synthesized greeting audio stream.
                azureSpeech.startSession();
                await startAudioRecording();
            } else {
                realtime.startSession();
                if (verboseLogging) {
                    realtime.sendVerboseLogging(true);
                    if (logToFile) {
                        realtime.sendLogToFile(true);
                    }
                }

                if (continuing && !startMicInFlightRef.current) {
                    // Resumed session: no greeting is coming.
                    startMicInFlightRef.current = startAudioRecording()
                        .then(() => undefined)
                        .finally(() => {
                            startMicInFlightRef.current = null;
                        });
                }

                // Safety: if we never receive the greeting completion, start the mic after a short timeout.
                window.setTimeout(() => {
                    if (!isSessionActiveRef.current) return;
                    if (!awaitingGreetingDoneRef.current) return;
                    awaitingGreetingDoneRef.current = false;
                    if (startMicInFlightRef.current) return;
                    startMicInFlightRef.current = startAudioRecording()
                        .then(() => undefined)
                        .finally(() => {
                            startMicInFlightRef.current = null;
                        });
                }, 3500);
            }

            setIsRecording(true);
        } else {
            await stopConversation();
        }
    };

    useEffect(() => {
        const checkMobile = () => {
            setIsMobile(window.innerWidth < 768);
        };
        checkMobile();
        window.addEventListener("resize", checkMobile);
        return () => window.removeEventListener("resize", checkMobile);
    }, []);

    // Rick's PR-110 review item 5 (issue #80 F7): a switch clears the previous persona's ticket,
    // transcript and session identifiers (and ends any lingering realtime session) so the new
    // persona starts on a genuinely fresh slate. useRealTime already namespaces the resume id per
    // persona.id, so no separate handling is needed there.
    //
    // Issue GH-180: this is the CONFIRMED switch executor -- it performs the actual switch
    // unconditionally and is called either immediately (empty order, no active conversation) or
    // after the guest confirms `PersonaSwitchConfirmDialog` (non-empty order and/or an active
    // conversation). It must never run without one of those two gates having already decided the
    // switch should happen; `requestPersonaSwitch` below owns that decision.
    const handleSelectPersona = async (personaId: string) => {
        // Issue GH-180 round 2, R4: marks this switch in flight from the very first line, before
        // any await below -- requestPersonaSwitch reads this synchronously (immune to React's
        // batched state updates), so a second pick landing anywhere in this window is a plain
        // no-op instead of re-entering this function.
        switchInFlightRef.current = personaId;
        setSwitchTargetId(personaId);
        // Issue GH-180 round 2, R1: marks the switch as pending in useRealtime the instant the
        // guest confirms -- well before the fetch below resolves -- so a mic tap landing in that
        // window is deferred (see useRealtime's reconnect()/beginSwitch()) instead of reconnecting
        // the OLD persona's dead socket out from under the switch. endSession() itself (the actual
        // teardown) still waits for a successful fetch; cancelSwitch() on the failure path below
        // undoes this mark exactly the same way it already undoes endSession()'s.
        realtime.beginSwitch();
        try {
            // A conversation can genuinely be active here now that the picker is never disabled
            // (issue GH-180) -- stop it cleanly first rather than letting `endSession()` tear down
            // the socket out from under an in-progress recording/greeting. Issue GH-180 round 2,
            // R6: now AWAITED (was fire-and-forget `void stopConversation()`) -- the mic recorder
            // used to keep streaming audio buffers for one more tick after `end_session` had
            // already gone out, and nothing ever asserted the recorder actually stopped, so a
            // mutation that deleted this line entirely survived the whole suite.
            if (isSessionActiveRef.current) await stopConversation();
            // Issue GH-180 round 2, R1: fetch the target persona FIRST -- only on success do we
            // tear down the old session/order/transcript below. A failed fetch now leaves
            // everything exactly as it was (persona-context.tsx's own `selectPersona` mirrors
            // this: `personaId`/localStorage only move once `loadPersona` succeeds), with a
            // visible error and the picker still reflecting the persona actually bound.
            const switched = await selectPersona(personaId);
            if (!switched) {
                // issue GH-171 round 3, H4: the only signal useRealtime has for a FAILED switch --
                // nothing else ever changed (current/personaId stayed put), so there is nothing
                // for react-use-websocket's own url-keyed effect to react to on its own.
                realtime.cancelSwitch();
                return;
            }
            // issue GH-171 round 2, H2: tells useRealtime a persona switch is under way, so its
            // own onClose doesn't force an extra reconnect for the persona being switched AWAY
            // from once the server's close for this end_session arrives (see useRealtime.tsx's
            // switchingRef). Issue GH-180 round 2, R2: endSession() also records the exact socket
            // that is open right now, so anything it still delivers afterward (a reply already in
            // flight when the guest confirmed) can never be mistaken for the new persona's own
            // frames.
            realtime.endSession({ switching: true });
            resumePendingRef.current = null;
            resumedSessionRef.current = false;
            serverSessionLostRef.current = false;
            setOrder(initialOrder);
            setTranscripts([]);
            setSessionIdentifiers(null);
            setTokenHistory([]);
            setConnectionNotice(null);
        } finally {
            switchInFlightRef.current = null;
            setSwitchTargetId(null);
        }
    };

    // Issue GH-180: the picker is now always enabled, so every switch request lands here first.
    // With an empty order and no active conversation there's nothing to lose -- switch straight
    // away, matching the picker's pre-GH-180 "idle" behavior exactly. Otherwise, hold the switch
    // behind `PersonaSwitchConfirmDialog` instead of either silently blocking it (the old bug) or
    // silently clearing the guest's order.
    const requestPersonaSwitch = (personaId: string) => {
        // Issue GH-180 round 2, R4: a switch is already resolving (the target's detail fetch
        // hasn't settled either way yet) -- ignore any further pick until it does, rather than
        // re-entering handleSelectPersona (a second endSession() call) or reaching
        // persona-context's own "already selected" early return, whose `false` result used to
        // make handleSelectPersona call realtime.cancelSwitch() against a switch that was still
        // genuinely pending -- reopening a socket for the persona being switched AWAY from.
        if (switchInFlightRef.current) return;
        if (personaId === current.id) return;
        const hasActiveConversation = isSessionActiveRef.current;
        const hasOrderItems = order.items.length > 0;
        if (!hasActiveConversation && !hasOrderItems) {
            void handleSelectPersona(personaId);
            return;
        }
        setPendingPersonaSwitchId(personaId);
    };

    const confirmPersonaSwitch = () => {
        const personaId = pendingPersonaSwitchId;
        setPendingPersonaSwitchId(null);
        if (personaId) void handleSelectPersona(personaId);
    };

    // Nothing changes on cancel: the dialog closes and the (controlled) picker already reflects
    // `current.id` again on its own, with no extra state to unwind.
    const cancelPersonaSwitch = () => setPendingPersonaSwitchId(null);

    // Rick's PR 134 review, item 4: the model picker's own explicit user choice -- the other
    // persist site is the resolve effect above (a persona switch or an initial `?model=` arrival).
    // Written under the CURRENT persona's key, matching whichever persona is bound at the moment
    // of the click.
    const handleModelChange = (id: string) => {
        setModelId(id);
        localStorage.setItem(modelStorageKey(current.id), id);
    };

    // issue 165: mirrors `handleModelChange` above -- the toggle's own explicit user choice, the other
    // persist site being the resolve effect (a persona switch). Unreachable for a persona with no
    // `features.dayparts` since `<Settings>` never renders the toggle for one (see below).
    const handleMenuModeChange = (mode: string) => {
        setMenuMode(mode);
        localStorage.setItem(menuModeStorageKey(current.id), mode);
    };

    return (
        <div className={`min-h-screen bg-background p-4 text-foreground ${theme}`}>
            <div className="mx-auto max-w-7xl space-y-6">
                <div className="flex flex-col gap-3 text-sm font-semibold text-primary md:flex-row md:items-center md:justify-between">
                    <a
                        href="https://github.com/swigerb/SonicAIDriveThru"
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1 rounded-full bg-white/80 px-3 py-1 text-primary transition hover:text-accent"
                        title="View source on GitHub"
                    >
                        <FaGithub className="h-4 w-4" />
                        <span>Source on GitHub</span>
                    </a>
                    <div className="flex items-center gap-2">
                        {/* Issue GH-180: always enabled -- requestPersonaSwitch decides whether the
                            switch runs immediately (empty order, no active conversation) or waits
                            on PersonaSwitchConfirmDialog's confirmation first. Issue GH-180 round
                            2, R4: shows the pending target while a switch is still resolving,
                            rather than snapping back to the persona that's merely still bound. */}
                        <PersonaPicker personas={personas} currentId={switchTargetId ?? current.id} onSelect={requestPersonaSwitch} />
                        {/* Issue #80 F11: hides itself entirely below two `backends[]` entries. */}
                        <BackendPicker
                            backends={backends}
                            personaId={current.id}
                            modelId={modelId}
                            disabled={isRecording || order.items.length > 0}
                        />
                        <Suspense fallback={null}>
                            <Settings
                                isMobile={isMobile}
                                showSessionTokens={showSessionTokens}
                                onShowSessionTokensChange={setShowSessionTokens}
                                verboseLogging={verboseLogging}
                                onVerboseLoggingChange={(checked: boolean) => {
                                    setVerboseLogging(checked);
                                    realtime.sendVerboseLogging(checked);
                                    if (!checked && logToFile) {
                                        setLogToFile(false);
                                        realtime.sendLogToFile(false);
                                    }
                                }}
                                logToFile={logToFile}
                                onLogToFileChange={(checked: boolean) => {
                                    setLogToFile(checked);
                                    realtime.sendLogToFile(checked);
                                }}
                                voiceChoice={voiceChoice}
                                onVoiceChoiceChange={(voice: string) => {
                                    setVoiceChoice(voice);
                                    realtime.sendVoiceChoice(voice);
                                }}
                                roleName={current.roleName}
                                voiceLabelOverride={t("settings.voiceLabel", { defaultValue: "" }) || undefined}
                                defaultVoiceId={current.voice.default}
                                models={current.models}
                                modelId={modelId}
                                onModelChange={handleModelChange}
                                modelDisabled={isRecording || order.items.length > 0}
                                menuModeEnabled={current.features.dayparts}
                                menuMode={menuMode}
                                onMenuModeChange={handleMenuModeChange}
                                // Rick's PR 166 round-1 review, required item 3: same lock rule as
                                // persona/model above -- menuMode is a getSocketUrl dependency,
                                // so toggling it mid-session tears down and reconnects the live
                                // socket (react-use-websocket keys its connect effect on `url`),
                                // dropping the in-progress order.
                                menuModeDisabled={isRecording || order.items.length > 0}
                            />
                        </Suspense>
                        {authConfig.isConfigured && (
                            <Button variant="ghost" size="icon" className="rounded-full" onClick={signOutInteractive} title="Logout">
                                <LogOut className="h-4 w-4" />
                            </Button>
                        )}
                    </div>
                </div>

                {personaError && (
                    <p role="alert" className="rounded-md bg-destructive/10 px-3 py-2 text-sm font-medium text-destructive">
                        {personaError}
                    </p>
                )}

                {sessionIdentifiers && showSessionTokens && (
                    <SessionTokenPanel
                        identifiers={sessionIdentifiers}
                        history={tokenHistory}
                        variant={current.sessionBar?.variant}
                        textRoles={current.textRoles}
                    />
                )}

                <BrandHero logoUrl={logoUrl} persona={current} />

                <div className="grid grid-cols-1 gap-4 md:grid-cols-3 md:gap-8">
                    {/* Mobile Menu Button */}
                    <Sheet>
                        <SheetTrigger asChild>
                            <Button variant="outline" className="mb-4 flex w-full items-center justify-center md:hidden">
                                <Menu className="mr-2 h-4 w-4" />
                                {t("menu.button")}
                            </Button>
                        </SheetTrigger>
                        <SheetContent side="left" className="w-[300px] sm:w-[400px]">
                            <SheetHeader>
                                <SheetTitle>{t("menu.title")}</SheetTitle>
                            </SheetHeader>
                            <div className="h-[calc(100vh-4rem)] overflow-auto pr-4">
                                <MenuPanel menuMode={menuMode} />
                            </div>
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Menu Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">{t("menu.title")}</h2>
                        <div className="h-[calc(100vh-13rem)] overflow-auto pr-4">
                            <MenuPanel menuMode={menuMode} />
                        </div>
                    </Card>

                    {/* Center Panel - Recording Button and Order Summary */}
                    <Card className="p-6 md:overflow-auto">
                        <div className="space-y-8">
                            <OrderSummary order={useDummyData ? dummyOrder : order} taxRate={current.taxRate} />
                            <div className="mb-4 flex flex-col items-center justify-center">
                                <Button
                                    onClick={onToggleListening}
                                    className={`h-12 w-60 border-none font-semibold shadow-lg transition-colors ${
                                        isRecording ? "bg-brand-secondary text-white hover:bg-brand-ink" : "bg-brand-primary text-white hover:bg-brand-primary-strong"
                                    }`}
                                    aria-label={isRecording ? t("app.stopRecording") : t("app.startRecording")}
                                >
                                    {isRecording ? (
                                        <>
                                            <MicOff className="mr-2 h-4 w-4" />
                                            {t("app.stopConversation")}
                                        </>
                                    ) : (
                                        <>
                                            <Mic className="mr-2 h-6 w-6" />
                                        </>
                                    )}
                                </Button>
                                <StatusMessage isRecording={isRecording} notice={connectionNotice} />
                                {!useDummyData && !useAzureSpeechOn && order.items.length > 0 && (
                                    <Button variant="ghost" size="sm" onClick={startNewOrder} className="text-xs text-muted-foreground">
                                        {t("app.newOrder")}
                                    </Button>
                                )}
                            </div>
                        </div>
                    </Card>

                    {/* Mobile Transcript Button */}
                    <Sheet>
                        <SheetTrigger asChild>
                            <Button variant="outline" className="mt-4 flex w-full items-center justify-center md:hidden">
                                <MessageSquare className="mr-2 h-4 w-4" />
                                Transcript
                            </Button>
                        </SheetTrigger>
                        <SheetContent side="right" className="w-[300px] sm:w-[400px]">
                            <SheetHeader>
                                <SheetTitle>Guest Conversation</SheetTitle>
                            </SheetHeader>
                            <TranscriptPanel
                                transcripts={useDummyData ? dummyTranscripts : transcripts}
                                className="h-[calc(100vh-4rem)] overflow-auto pr-4"
                            />
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Transcript Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">Guest Conversation</h2>
                        <TranscriptPanel
                            transcripts={useDummyData ? dummyTranscripts : transcripts}
                            className="h-[calc(100vh-13rem)] overflow-auto pr-4"
                        />
                    </Card>
                </div>
            </div>
            <footer className="mx-auto mt-8 max-w-4xl space-y-2 text-center text-xs text-muted-foreground">
                {/* Issue 164 A7: only one pack currently defines "footer.extra" (one original's
                    italic brand tagline); `defaultValue: ""` keeps every other pack's footer from
                    showing the literal key string when the override is absent. */}
                {t("footer.extra", { defaultValue: "" }) && (
                    <p className={`text-base font-bold italic ${textRoleClass(resolveTextRole("footerExtra", current.textRoles))}`}>
                        {t("footer.extra")}
                    </p>
                )}
                {/* Issue 164 R2(d) (PR 167 round 1 review): full opacity, not /80 -- one pack's
                    footer tagline color at /80 only reached 3.51:1 in the original. */}
                <p className={`font-semibold uppercase tracking-[0.35em] ${textRoleClass(resolveTextRole("footerTagline", current.textRoles))}`}>
                    {t("app.footer")}
                </p>
                <p className="text-[11px] leading-relaxed text-brand-ink/80 dark:text-white/80">{current.legal}</p>
            </footer>
            <PersonaSwitchConfirmDialog
                open={pendingPersonaSwitchId !== null}
                personaName={personas.find(p => p.id === pendingPersonaSwitchId)?.displayName ?? ""}
                onConfirm={confirmPersonaSwitch}
                onCancel={cancelPersonaSwitch}
            />
        </div>
    );
}

// Issue 164 A1/A2/A3/A4/A5/A8/D2: this two-column hero (headline + description + callout pills
// + spotlight cards) and every one of its colors/copy/icons is now driven entirely by the pack's
// `persona.hero` (types/persona.ts::PersonaHero) -- nothing brand-specific is hard-coded here.
// The hero card itself intentionally carries NO `dark:` variant anywhere (A8/D2): all three
// original apps render this card as a uniformly light frosted surface regardless of the page's
// light/dark theme, and the pre-issue 164 unified app's `dark:border-white/10 dark:bg-brand-ink/80`
// override (plus matching dark: text overrides throughout) was itself the discrepancy to fix.
export const BrandHero = memo(function BrandHero({ logoUrl, persona }: { logoUrl: string; persona: PersonaDetail }) {
    const { t } = useTranslation();
    // Issue #80 F3, Rick's PR-110 review item 2: a pack with no logo (`logoUrl` empty) or one whose
    // image fails to load (a bad/missing asset path) must never show a broken-image icon -- fall
    // back to the persona's own display name rendered as text instead. `logoUrl` empty happens
    // for real before the persona catalog has loaded (see the neutral loading shell in `App()`
    // below), and `onError` covers a live pack whose declared logo path 404s.
    const [logoFailed, setLogoFailed] = useState(false);
    const showLogo = Boolean(logoUrl) && !logoFailed;

    // Reset the failure flag whenever the logo URL itself changes (e.g. a persona switch) --
    // otherwise a previous persona's broken logo would permanently hide every later persona's
    // working one, since `BrandHero` stays mounted across a switch.
    useEffect(() => {
        setLogoFailed(false);
    }, [logoUrl]);

    const logoImg = (
        <img
            src={logoUrl}
            alt={`${persona.title} logo`}
            className="h-20 w-auto max-w-[14rem] object-contain drop-shadow-xs"
            loading="lazy"
            onError={() => setLogoFailed(true)}
        />
    );

    return (
        // Issue 172: the hero's height is driven purely by its content (no min/fixed height
        // anywhere in this tree) and every original element is still present, just regrouped --
        // see the comments below for what moved and why.
        <section className="hero-card rounded-[32px] border border-white/40 bg-white/80 p-6 shadow-[0_25px_70px_var(--brand-secondary-veil-18)] backdrop-blur-lg sm:p-8">
            {/* Issue 172 round 2 (Rick's review): the logo/badge row and the headline/description
                block live in one left column (`xl:col-span-3 xl:flex xl:flex-col xl:gap-6`), and
                the spotlight stack is a completely separate right column (`xl:col-span-2`) -- both
                are plain columns on the shared `xl:items-start` grid, with NO row placement at
                all. Round 1 placed the spotlight stack with `xl:row-span-2` across both the logo
                row's and the headline block's rows, which forced the grid to stretch the logo
                row's row-track tall enough to match the (taller) stack, pushing the headline ~45px
                below its natural position -- a shared grid row was the wrong tool for a two-column
                layout. With both columns decoupled, the logo-to-headline gap stays the natural
                `gap-6` (24px, same as below xl), the spotlight stack's top aligns with the logo
                row's top, and any leftover height between the two columns simply sits under
                whichever one is shorter instead of stretching a row. Below xl this is plain
                stacked block flow in the same DOM order (logo/badge, then headline/description,
                then spotlight under the text) -- a single narrow column can't support a meaningful
                column split, so the cards instead go edge-to-edge under the text, side by side,
                which reads far better than a cramped narrow column (see PR body for the
                before/after numbers). */}
            <div className="xl:grid xl:grid-cols-5 xl:items-start xl:gap-x-8">
                <div className="xl:col-span-3 xl:flex xl:flex-col xl:gap-6">
                    <div className="flex flex-wrap items-center gap-3">
                        {showLogo ? (
                            // Issue 164 B2: some packs' original PNG logo has an opaque white background
                            // and was shown on a white rounded tile against the app's colored hero card --
                            // render that tile only when the pack asks for it.
                            persona.assets.logoTile ? <span className="rounded-2xl bg-white p-3 shadow-xs">{logoImg}</span> : logoImg
                        ) : (
                            <span className="text-2xl font-black text-brand-primary" role="img" aria-label={`${persona.title} logo`}>
                                {persona.title}
                            </span>
                        )}
                        <span className={`rounded-full bg-brand-primary/10 px-3 py-1 text-xs font-black uppercase tracking-[0.3em] ${textRoleClass(resolveTextRole("badge", persona.textRoles))}`}>
                            {/* Issue 164 A5: pack-specific badge copy overrides the shared neutral default. */}
                            {persona.hero.badge ?? t("hero.badge")}
                        </span>
                    </div>
                    <div className="mt-5 space-y-4 sm:mt-6 xl:mt-0">
                        <h1 className="text-4xl font-black leading-tight text-brand-primary sm:text-5xl">{persona.hero.headline}</h1>
                        {/* Issue 164 A4: the persona's own hero sentence, replacing the shared i18n subhead. */}
                        <p className={`max-w-2xl text-base ${HERO_BODY_TEXT_CLASS}`}>{persona.hero.description}</p>
                    </div>
                </div>
                {persona.hero.spotlight.length > 0 && (
                    // Issue 172: `grid-cols-1` (stacked) by default and at `xl:` -- each card is the
                    // full width of this column, so both read comfortably instead of the squeezed
                    // half-width cards a side-by-side split produced in the narrow xl column. At
                    // `sm:` (and up, until xl's column split takes over) the cards go side by side
                    // under the text instead, since there the full hero width is available to them.
                    <div className="relative mt-6 grid grid-cols-1 items-stretch gap-4 sm:mt-8 sm:grid-cols-2 xl:col-span-2 xl:mt-0 xl:grid-cols-1">
                        <div className="absolute inset-0 -z-10 rounded-[32px] bg-linear-to-br from-brand-primary/10 via-brand-surface-tint to-brand-accent/15 opacity-80 blur-3xl"></div>
                        {persona.hero.spotlight.map((card, index) => (
                            <SpotlightCard key={card.title} card={card} personaId={persona.id} isSecond={index === 1} />
                        ))}
                    </div>
                )}
            </div>
            {/* Issue 172: the 3 callout pills + tech line stay in one full-width footer row below
                the grid above -- this is what lets the logo/headline/spotlight grid balance on its
                own content height alone, instead of the text column also having to carry this
                row's height. */}
            <div className="mt-6 border-t border-brand-primary/10 pt-5 sm:mt-8 sm:pt-6">
                <div className="grid gap-3 sm:grid-cols-3">
                    {persona.hero.callouts.map(callout => (
                        <CalloutPill key={callout.title} title={callout.title} detail={callout.detail} tone={callout.tone} />
                    ))}
                </div>
                <div className={`mt-4 flex flex-wrap items-center gap-2 text-xs font-semibold uppercase tracking-[0.3em] ${HERO_BODY_TEXT_CLASS}`}>
                    <img src={azureLogo} alt="Microsoft Azure" className="h-6 w-auto" loading="lazy" />
                    <span>{t("hero.poweredBy")}</span>
                </div>
            </div>
        </section>
    );
});

type BrandTone = "primary" | "secondary" | "accent";

// Issue 164 R1 (PR 167 round 1 review): `/90` ink alpha over the hero's frosted white/80 card,
// meeting >= 4.5:1 contrast for every pack in both light and dark page themes (the worst
// case computes to about 5.0:1 on the dark frosted card). Exported so heroContrast.test.ts can
// read the exact alpha back out rather than re-stating it as a magic literal in the test.
export const HERO_BODY_TEXT_CLASS = "text-brand-ink/90";

export function CalloutPill({ title, detail, tone }: { title: string; detail: string; tone: BrandTone }) {
    const gradientMap: Record<BrandTone, string> = {
        primary: "from-brand-primary to-brand-primary-light",
        // Issue 164 R3 (PR 167 round 1 review): "-light" (brighter), not "-strong" (darker) --
        // the original gradients end brighter than they start for the two packs that override
        // secondaryLight; "-strong" rendered both packs' pills as a solid dark wash.
        secondary: "from-brand-secondary to-brand-secondary-light",
        accent: "from-brand-accent to-brand-accent-light"
    };

    return (
        <div className={`rounded-2xl bg-linear-to-br ${gradientMap[tone]} p-3 text-white shadow-[0_10px_25px_rgba(0,0,0,0.08)]`}>
            <p className="text-xs uppercase tracking-[0.25em] text-white/80">{title}</p>
            <p className="text-sm font-semibold leading-tight">{detail}</p>
        </div>
    );
}

// Issue 164 A2: the second ("body") spotlight card's border/wash/kicker/accent-line all draw
// from ONE of the persona's brand roles (`card.tone`, default "secondary") so the shared
// component stays brand-free while still matching each original's own card-2 color choice
// (most packs use their secondary role; one pack overrides to "accent" in persona.json).
const SPOTLIGHT_BODY_STYLES: Record<BrandTone, { border: string; gradient: string; kicker: string; accent: string; shadow: string }> = {
    primary: {
        border: "border-brand-primary/25",
        gradient: "bg-linear-to-br from-brand-primary/10 to-brand-accent/10",
        kicker: "text-brand-primary",
        accent: "text-brand-secondary",
        shadow: "shadow-[0_25px_45px_var(--brand-primary-veil-12)]"
    },
    secondary: {
        border: "border-brand-secondary/25",
        gradient: "bg-linear-to-br from-brand-secondary/10 to-brand-accent/10",
        kicker: "text-brand-secondary",
        accent: "text-brand-primary",
        shadow: "shadow-[0_25px_45px_var(--brand-secondary-veil-15)]"
    },
    accent: {
        border: "border-brand-accent/25",
        gradient: "bg-linear-to-br from-brand-accent/10 to-brand-secondary/10",
        kicker: "text-brand-accent",
        accent: "text-brand-primary",
        shadow: "shadow-[0_25px_45px_var(--brand-accent-veil-15)]"
    }
};

// Issue 164 A1/A2: card 1 (`rows`) always draws its border/icon-wash/kicker from "primary" --
// every original's card 1 does this identically, so it isn't parameterized. Card 2 (`body`) reads
// `card.tone`/`card.tint` instead. Each card's icon is the pack's own SVG illustration
// (`personas/<id>/assets/spotlight-*.svg`), replacing the old brand-neutral sound-wave mark.
export function SpotlightCard({ card, personaId, isSecond }: { card: PersonaHeroSpotlight; personaId: string; isSecond: boolean }) {
    const iconUrl = personaAssetUrl(personaId, card.icon);

    if (!isSecond) {
        return (
            // Issue 172: `flex h-full flex-col justify-center` -- when this card's sibling (card
            // 2) is taller, the shared grid's `items-stretch` (BrandHero) grows this card's box to
            // match it; centering the content vertically inside that box reads as breathing room
            // around a compact card rather than a dead gap pinned to one edge.
            //
            // Issue 172 round 2 (Rick's review): decoupling BrandHero's two columns (see the
            // comment there) left up to ~91px of leftover height under the shorter column for some
            // personas -- too much. `p-4` (was `p-5`), the header row's `mb-3` (was `mb-4`), and
            // the icon tile's smaller `p-2.5`/`h-10 w-10` (was `p-3`/`h-12 w-12`) below all trim
            // this card's own natural height back down without stretching or truncating its
            // content, keeping the leftover at or under 48px for every persona (PR body has the
            // exact numbers).
            <div className="flex h-full flex-col justify-center rounded-3xl border border-brand-primary/20 bg-white/90 p-4 shadow-[0_25px_45px_var(--brand-primary-veil-12)]">
                <div className="mb-3 flex items-center gap-3">
                    {/* Issue 172: `shrink-0` on the icon tile, `min-w-0` on the text column --
                        without these the narrower 3:2 spotlight column at 1024px let the icon
                        tile get squeezed down to a sliver instead of the kicker/title text
                        simply wrapping. */}
                    <div className="shrink-0 rounded-2xl bg-brand-primary/10 p-2.5">
                        <img src={iconUrl} alt={card.kicker} className="h-10 w-10" loading="lazy" />
                    </div>
                    <div className="min-w-0">
                        <p className="text-xs font-bold uppercase tracking-wide text-brand-primary">{card.kicker}</p>
                        <p className="text-sm font-semibold text-brand-ink">{card.title}</p>
                    </div>
                </div>
                {card.rows && (
                    <ul className="space-y-2 text-xs font-medium text-brand-ink/80">
                        {card.rows.map(row => (
                            // Issue 172 round 2 (Rick's review): copy is pack-driven, so a row's
                            // label/value pair can't assume it will always fit one line -- an
                            // unconditional `whitespace-nowrap` overflowed its pill at 360/390px
                            // for more than one real pack. `sm:whitespace-nowrap` keeps today's
                            // single-line look from `sm:` (640px) up, where every pack's current
                            // copy fits, while letting the pair wrap naturally below that, with
                            // `gap-3` keeping a minimum breathing space between them either way.
                            <li key={row.label} className="flex items-center justify-between gap-3 rounded-full bg-white/80 px-3 py-1.5">
                                <span className="sm:whitespace-nowrap">{row.label}</span>
                                <span className={`sm:whitespace-nowrap ${textRoleClass(row.tone ?? "primary")}`}>{row.value}</span>
                            </li>
                        ))}
                    </ul>
                )}
            </div>
        );
    }

    const tone = card.tone ?? "secondary";
    const style = SPOTLIGHT_BODY_STYLES[tone];
    // Issue 164 A2: an explicit `tint` (e.g. one original's tinted beverage card) replaces the
    // shared two-role gradient with a flat wash of the pack's own hex -- this is pack DATA, not a
    // literal baked into the component, so it doesn't trip the no-hex-literal guard (brandColorTokens.test.ts).
    const tintStyle = card.tint ? { backgroundColor: `${card.tint}26` } : undefined;
    // Issue 164 R4 (PR 167 round 1 review): `accentTone`, when a pack sets it, overrides the
    // pairing-line color independently of `tone` (`style.accent` above). One pack needs this: its
    // card-2 `tone` stays "secondary" for the border/wash/kicker, but the pairing line itself must
    // render `secondary` too (not `style.accent`'s tone-derived "primary"), matching that
    // original's pairing line rather than the pre-fix mistinted one.
    const accentClass = card.accentTone ? textRoleClass(card.accentTone) : style.accent;

    return (
        // Issue 172 round 2 (Rick's review): `p-4` (was `p-5`), `mb-3` (was `mb-4`), and the
        // smaller icon tile below match card 1's same tightening -- see that card's comment for
        // why (decoupled columns can leave too much leftover height under the shorter one).
        <div
            className={`flex h-full flex-col justify-center rounded-3xl border p-4 ${style.border} ${style.shadow} ${card.tint ? "" : style.gradient}`}
            style={tintStyle}
        >
            <div className="mb-3 flex items-center gap-3">
                <div className="shrink-0 rounded-2xl bg-white/60 p-2.5">
                    <img src={iconUrl} alt={card.kicker} className="h-10 w-10" loading="lazy" />
                </div>
                <div className="min-w-0">
                    <p className={`text-xs font-bold uppercase tracking-wide ${style.kicker}`}>{card.kicker}</p>
                    <p className="text-sm font-semibold text-brand-ink">{card.title}</p>
                </div>
            </div>
            {card.body && (
                <div className="rounded-2xl bg-white/80 p-3 text-sm font-semibold text-brand-ink">
                    <p>{card.body}</p>
                    {card.accent && <p className={`text-xs ${accentClass}`}>{card.accent}</p>}
                </div>
            )}
        </div>
    );
}

export const SessionTokenPanel = memo(function SessionTokenPanel({
    identifiers,
    history,
    variant = "plain",
    textRoles
}: {
    identifiers: SessionIdentifiersState;
    history: SessionIdentifiersState[];
    variant?: "plain" | "chips";
    textRoles?: PersonaTextRoles;
}) {
    const [expanded, setExpanded] = useState(false);

    if (variant === "chips") {
        // Issue 164 C3/E4: this original renders as a fixed, always-light pill-chip strip (two
        // truncated-id chips, one per brand role) -- no expand/collapse chevron, no history
        // dropdown, and critically no `dark:` override, matching the original exactly (the
        // pre-164 unified app's solid-color dark bar was itself part of the discrepancy).
        return (
            <div className="flex flex-wrap gap-2 rounded-3xl border border-white/40 bg-white/90 p-3 font-mono text-xs text-brand-primary shadow-xs">
                <div className="flex items-center gap-2" title={identifiers.sessionToken}>
                    {/* Issue 164 R2 (PR 167 round 1 review): the "Session Token" chip uses the
                        pack's badge role (one pack overrides it to primaryDeep, matching the original's
                        readable pairing on its tinted background) instead of a hardcoded primary. */}
                    <span className={`rounded-full bg-brand-primary/15 px-2 py-1 font-semibold uppercase tracking-widest ${textRoleClass(resolveTextRole("badge", textRoles))}`}>
                        Session Token
                    </span>
                    <span className="text-sm text-brand-ink">{formatSessionToken(identifiers.sessionToken)}</span>
                </div>
                <div className="flex items-center gap-2" title={identifiers.roundTripToken}>
                    <span className="rounded-full bg-brand-secondary/15 px-2 py-1 font-semibold uppercase tracking-widest text-brand-secondary">
                        Round {identifiers.roundTripIndex}
                    </span>
                    <span className="text-sm text-brand-ink">{formatSessionToken(identifiers.roundTripToken, 6)}</span>
                </div>
            </div>
        );
    }

    return (
        <div className="rounded-xl border border-white/30 bg-white/90 font-mono text-xs shadow-xs dark:border-white/10 dark:bg-brand-ink/90">
            <button
                type="button"
                onClick={() => setExpanded(prev => !prev)}
                className="flex w-full items-center justify-between gap-2 px-3 py-2 text-left transition-colors hover:bg-white/50 dark:hover:bg-white/5"
                aria-expanded={expanded}
                aria-label="Toggle session token history"
            >
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                    <span className="font-semibold text-brand-secondary dark:text-brand-secondary-tint">Session:</span>
                    <span className="break-all text-brand-ink dark:text-gray-200">
                        {identifiers.sessionToken || ""}
                    </span>
                    <span className="mx-1 text-brand-ink/40 dark:text-gray-500">|</span>
                    <span className="whitespace-nowrap rounded-full bg-brand-secondary/10 px-1.5 py-0.5 font-semibold text-brand-secondary dark:bg-brand-secondary-tint/10 dark:text-brand-secondary-tint">
                        Round #{identifiers.roundTripIndex}
                    </span>
                </div>
                <motion.span
                    animate={{ rotate: expanded ? 180 : 0 }}
                    transition={{ duration: 0.2 }}
                    className="shrink-0 text-brand-secondary/60 dark:text-white/50"
                >
                    <ChevronDown size={18} />
                </motion.span>
            </button>

            <AnimatePresence initial={false}>
                {expanded && history.length > 0 && (
                    <motion.div
                        initial={{ height: 0, opacity: 0 }}
                        animate={{ height: "auto", opacity: 1 }}
                        exit={{ height: 0, opacity: 0 }}
                        transition={{ duration: 0.25, ease: "easeInOut" }}
                        className="overflow-hidden"
                    >
                        <div className="max-h-40 overflow-y-auto border-t border-white/30 px-3 py-2 dark:border-white/10">
                            <div className="space-y-1">
                                {history.map((entry, i) => (
                                    <div
                                        key={`${entry.roundTripIndex}-${entry.roundTripToken}-${i}`}
                                        className={`flex items-start gap-2 rounded px-2 py-1 ${i === 0 ? "bg-brand-primary/5 dark:bg-brand-primary/10" : ""}`}
                                    >
                                        <span className="w-16 shrink-0 font-semibold text-brand-secondary dark:text-brand-secondary-tint">
                                            Round #{entry.roundTripIndex}
                                        </span>
                                        <span className="break-all text-brand-ink/60 dark:text-gray-400">
                                            {entry.roundTripToken || ""}
                                        </span>
                                    </div>
                                ))}
                            </div>
                        </div>
                    </motion.div>
                )}
            </AnimatePresence>
        </div>
    );
});

// Issue 164 C3: truncates a long identifier to `prefix…suffix` (the "chips" variant's original
// treatment), leaving short tokens untouched.
export function formatSessionToken(token: string, prefix: number = 8, suffix: number = 4): string {
    if (!token) {
        return "";
    }
    if (token.length <= prefix + suffix + 3) {
        return token;
    }
    return `${token.slice(0, prefix)}…${token.slice(-suffix)}`;
}

// Main app component
function App() {
    // Issue #80 F6, Rick's PR-110 review item 6: no persona's content -- logo, hero
    // copy, ticket strings -- ever paints before the requested persona (`?persona=` / localStorage
    // / the catalog's default) has actually been resolved and applied. Reusing the same neutral
    // shell the auth-loading gate already shows keeps this a single, familiar "please wait" state
    // instead of a second bespoke one.
    const { ready: personaReady } = usePersonaContext();

    // Rick's PR-110 review (round 3) item 3, issue #80: the `index.css` veil/blob background must
    // stay hidden while this neutral shell is showing, so a size-less/mid-fetch persona never
    // paints brand-colored decoration behind a "please wait" state that hasn't applied a persona
    // yet. `data-persona-loading` is read by `index.css`'s `[data-persona-loading]` selector.
    useEffect(() => {
        if (personaReady) {
            document.documentElement.removeAttribute("data-persona-loading");
        } else {
            document.documentElement.setAttribute("data-persona-loading", "true");
        }
        return () => {
            document.documentElement.removeAttribute("data-persona-loading");
        };
    }, [personaReady]);

    if (!personaReady) {
        return (
            <div className="flex min-h-screen items-center justify-center">
                <div className="text-center">
                    <div className="mx-auto mb-4 h-12 w-12 animate-spin rounded-full border-4 border-muted-foreground border-t-transparent"></div>
                    <p className="text-lg">Loading...</p>
                </div>
            </div>
        );
    }

    return <SonicApp />;
}

export default function RootApp() {
    return (
        <AuthGate>
            <PersonaProvider>
                <ThemeProvider>
                    <DummyDataProvider>
                        <AzureSpeechProvider>
                            <App />
                        </AzureSpeechProvider>
                    </DummyDataProvider>
                </ThemeProvider>
            </PersonaProvider>
        </AuthGate>
    );
}
