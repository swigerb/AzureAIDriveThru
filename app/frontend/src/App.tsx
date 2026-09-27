import { useState, useEffect, useRef, useCallback, lazy, Suspense, memo } from "react";
import { Mic, MicOff, Menu, MessageSquare, LogOut, ChevronDown } from "lucide-react";
import { FaGithub } from "react-icons/fa";
import { AnimatePresence, motion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

import StatusMessage, { ConnectionNotice } from "@/components/ui/status-message";
import MenuPanel from "@/components/ui/menu-panel";
import OrderSummary, { calculateOrderSummary, OrderItem, OrderSummaryProps } from "@/components/ui/order-summary";
import TranscriptPanel from "@/components/ui/transcript-panel";
import PersonaPicker from "@/components/ui/persona-picker";
const Settings = lazy(() => import("@/components/ui/settings"));
import useRealTime from "@/hooks/useRealtime";
import useAzureSpeech from "@/hooks/useAzureSpeech";
import useAudioRecorder from "@/hooks/useAudioRecorder";
import useAudioPlayer from "@/hooks/useAudioPlayer";

import { ExtensionMiddleTierToolResponse, ExtensionRateLimited, ExtensionRoundTripToken, ExtensionSessionMetadata, ExtensionSessionResumed } from "./types";

import { ThemeProvider, useTheme } from "./context/theme-context";
import { DummyDataProvider, useDummyDataContext } from "@/context/dummy-data-context";
import { AzureSpeechProvider, useAzureSpeechOnContext } from "@/context/azure-speech-context";
import { AuthProvider, useAuth } from "@/context/auth-context";
import { PersonaProvider, usePersonaContext } from "@/context/persona-context";
import { resolveVoice } from "@/lib/voices";
import { apologyClipUrl, playApologyClip } from "@/lib/apology";
import { personaAssetUrl } from "@/lib/personaAssets";
import type { PersonaDetail } from "@/types/persona";

import azureLogo from "@/assets/azurelogo.svg";

type SessionIdentifiersState = {
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
    const { logout, authEnabled } = useAuth();
    const { personas, current, logoUrl, selectPersona } = usePersonaContext();

    const [transcripts, setTranscripts] = useState<Array<{ text: string; isUser: boolean; timestamp: Date }>>([]);
    const { dummyOrder, dummyTranscripts } = useDemoData(current.id, useDummyData);

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
            if (!transcript) return;
            clearRateLimitNotice();

            const newTranscriptItem = {
                text: transcript,
                isUser: false,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);

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

    // Rick's PR-110 review item 5 (issue #80 F7): the picker only allows a switch once
    // recording has stopped and the ticket is empty, but a finished conversation can still
    // leave transcripts/session identifiers on screen -- clear all of that (and end any
    // lingering realtime session) so the new persona starts on a genuinely fresh slate.
    // useRealTime already namespaces the resume id per persona.id, so no separate handling
    // is needed there.
    const handleSelectPersona = (personaId: string) => {
        realtime.endSession();
        resumePendingRef.current = null;
        resumedSessionRef.current = false;
        serverSessionLostRef.current = false;
        setOrder(initialOrder);
        setTranscripts([]);
        setSessionIdentifiers(null);
        setTokenHistory([]);
        setConnectionNotice(null);
        selectPersona(personaId);
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
                        {/* Issue #80 F1: the picker sets the persona for the NEXT session only
                            (ADR-001 decision 2) -- disabled once a conversation is active or the
                            guest has items on their ticket, rather than resetting either mid-flight. */}
                        <PersonaPicker
                            personas={personas}
                            currentId={current.id}
                            onSelect={handleSelectPersona}
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
                            />
                        </Suspense>
                        {authEnabled && (
                            <Button variant="ghost" size="icon" className="rounded-full" onClick={logout} title="Logout">
                                <LogOut className="h-4 w-4" />
                            </Button>
                        )}
                    </div>
                </div>

                {sessionIdentifiers && showSessionTokens && <SessionTokenPanel identifiers={sessionIdentifiers} history={tokenHistory} />}

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
                                <MenuPanel />
                            </div>
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Menu Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">{t("menu.title")}</h2>
                        <div className="h-[calc(100vh-13rem)] overflow-auto pr-4">
                            <MenuPanel />
                        </div>
                    </Card>

                    {/* Center Panel - Recording Button and Order Summary */}
                    <Card className="p-6 md:overflow-auto">
                        <div className="space-y-8">
                            <OrderSummary order={useDummyData ? dummyOrder : order} />
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
                            <div className="h-[calc(100vh-4rem)] overflow-auto pr-4">
                                <TranscriptPanel transcripts={useDummyData ? dummyTranscripts : transcripts} />
                            </div>
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Transcript Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">Guest Conversation</h2>
                        <div className="h-[calc(100vh-13rem)] overflow-auto pr-4">
                            <TranscriptPanel transcripts={useDummyData ? dummyTranscripts : transcripts} />
                        </div>
                    </Card>
                </div>
            </div>
            <footer className="mx-auto mt-8 max-w-4xl space-y-2 text-center text-xs text-muted-foreground">
                <p className="font-semibold uppercase tracking-[0.35em] text-brand-secondary/80">{t("app.footer")}</p>
                <p className="text-[11px] leading-relaxed text-brand-ink/80">{current.legal}</p>
            </footer>
        </div>
    );
}

// Issue #80 F3: every persona-flavored piece of hero copy below now comes from the pack
// (`logoUrl`/`persona.hero.headline`/`persona.hero.callouts`) rather than a brand-specific literal
// baked into this component. The three highlight cards and the "powered by" strip are genuinely
// app-level (not persona) chrome, so they stay i18n keys under `hero.*` -- decision 8 (docs/
// persona-architecture.md, ADR-001) says neutral app strings lead with Microsoft Foundry, so
// that's where "Azure Speech" was dropped from (the persona's own `hero.headline` is pack content
// this component doesn't otherwise touch).
const HERO_HIGHLIGHT_KEYS = [
    { key: "fastOrders", tone: "red" as const },
    { key: "foundryPowered", tone: "blue" as const },
    { key: "liveMenu", tone: "yellow" as const }
];

const BrandHero = memo(function BrandHero({ logoUrl, persona }: { logoUrl: string; persona: PersonaDetail }) {
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

    return (
        <section className="hero-card rounded-[32px] border border-white/40 bg-white/80 p-6 shadow-[0_25px_70px_var(--brand-secondary-veil-18)] backdrop-blur-lg dark:border-white/10 dark:bg-brand-ink/80">
            <div className="flex flex-col gap-8 lg:flex-row lg:items-center">
                <div className="flex-1 space-y-5">
                    <div className="flex flex-wrap items-center gap-3">
                        {showLogo ? (
                            <img
                                src={logoUrl}
                                alt={`${persona.title} logo`}
                                className="h-20 w-auto drop-shadow-xs"
                                loading="lazy"
                                onError={() => setLogoFailed(true)}
                            />
                        ) : (
                            <span className="text-2xl font-black text-brand-primary dark:text-brand-primary-tint" role="img" aria-label={`${persona.title} logo`}>
                                {persona.title}
                            </span>
                        )}
                        <span className="rounded-full bg-brand-primary/10 px-3 py-1 text-xs font-black uppercase tracking-[0.3em] text-brand-primary dark:bg-white/10 dark:text-brand-primary-tint">
                            {t("hero.badge")}
                        </span>
                    </div>
                    <h1 className="text-4xl font-black leading-tight text-brand-primary sm:text-5xl dark:text-brand-primary-tint">{persona.hero.headline}</h1>
                    <p className="max-w-2xl text-base text-muted-foreground">{t("hero.subhead")}</p>
                    <div className="grid gap-3 sm:grid-cols-3">
                        {HERO_HIGHLIGHT_KEYS.map(({ key, tone }) => (
                            <HeroHighlightCard key={key} title={t(`hero.highlights.${key}.title`)} detail={t(`hero.highlights.${key}.detail`)} tone={tone} />
                        ))}
                    </div>
                    <div className="flex flex-wrap items-center gap-2 text-xs font-semibold uppercase tracking-[0.3em] text-muted-foreground">
                        <img src={azureLogo} alt="Microsoft Azure" className="h-6 w-auto" loading="lazy" />
                        <span>{t("hero.poweredBy")}</span>
                    </div>
                </div>
                {persona.hero.callouts.length > 0 && (
                    <div className="relative flex flex-1 items-center justify-center">
                        <div className="absolute inset-0 -z-10 rounded-[32px] bg-linear-to-br from-brand-primary/10 via-brand-surface-tint to-brand-accent/15 opacity-80 blur-3xl dark:from-brand-primary/20 dark:via-brand-surface-dark-alt dark:to-brand-accent/20"></div>
                        <div className="w-full rounded-3xl border border-brand-primary/20 bg-white/90 p-4 shadow-[0_25px_45px_var(--brand-primary-veil-12)] dark:border-white/10 dark:bg-brand-ink/90">
                            <div className="mb-3 flex items-center gap-3">
                                <div className="rounded-2xl bg-brand-primary/10 p-3 dark:bg-white/10">
                                    <VoiceArt />
                                </div>
                                <p className="text-xs font-bold uppercase tracking-wide text-brand-primary dark:text-brand-primary-tint">
                                    {t("hero.calloutsTitle")}
                                </p>
                            </div>
                            <ul className="space-y-1 text-xs font-medium text-brand-ink/80 dark:text-white/80">
                                {persona.hero.callouts.map(callout => (
                                    <li key={callout} className="rounded-full bg-white/80 px-3 py-1 dark:bg-white/10">
                                        {callout}
                                    </li>
                                ))}
                            </ul>
                        </div>
                    </div>
                )}
            </div>
        </section>
    );
});

type HighlightTone = "red" | "blue" | "yellow";

function HeroHighlightCard({ title, detail, tone }: { title: string; detail: string; tone: HighlightTone }) {
    const gradientMap: Record<HighlightTone, string> = {
        red: "from-brand-primary to-brand-primary-light",
        blue: "from-brand-secondary to-brand-secondary-strong",
        yellow: "from-brand-accent to-brand-accent-light"
    };

    return (
        <div className={`rounded-2xl bg-linear-to-br ${gradientMap[tone]} p-3 text-white shadow-[0_10px_25px_rgba(0,0,0,0.08)]`}>
            <p className="text-xs uppercase tracking-[0.25em] text-white/80">{title}</p>
            <p className="text-sm font-semibold leading-tight">{detail}</p>
        </div>
    );
}

const SessionTokenPanel = memo(function SessionTokenPanel({
    identifiers,
    history
}: {
    identifiers: SessionIdentifiersState;
    history: SessionIdentifiersState[];
}) {
    const [expanded, setExpanded] = useState(false);

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

// Issue #80 F3: one persona-neutral decorative mark (a stylized sound wave) replaces the two
// brand-specific illustrations (a slush cup, a burger) that used to sit beside the hero callouts.
function VoiceArt() {
    return (
        <svg width="48" height="48" viewBox="0 0 48 48" fill="none" role="img" aria-label="Voice ordering illustration">
            <rect x="6" y="20" width="4" height="8" rx="2" fill="var(--brand-secondary-hex)" />
            <rect x="14" y="14" width="4" height="20" rx="2" fill="var(--brand-primary-hex)" />
            <rect x="22" y="8" width="4" height="32" rx="2" fill="var(--brand-accent)" />
            <rect x="30" y="14" width="4" height="20" rx="2" fill="var(--brand-primary-hex)" />
            <rect x="38" y="20" width="4" height="8" rx="2" fill="var(--brand-secondary-hex)" />
        </svg>
    );
}

// Main app component with authentication wrapper
function App() {
    const { isAuthenticated, isLoading, authEnabled } = useAuth();
    // Issue #80 F6, Rick's PR-110 review item 6: no persona's content -- logo, hero
    // copy, ticket strings -- ever paints before the requested persona (`?persona=` / localStorage
    // / the catalog's default) has actually been resolved and applied. Reusing the same neutral
    // shell the auth-loading gate already shows keeps this a single, familiar "please wait" state
    // instead of a second bespoke one.
    const { ready: personaReady } = usePersonaContext();

    if (isLoading || !personaReady) {
        return (
            <div className="flex min-h-screen items-center justify-center">
                <div className="text-center">
                    <div className="mx-auto mb-4 h-12 w-12 animate-spin rounded-full border-4 border-primary border-t-transparent"></div>
                    <p className="text-lg">Loading...</p>
                </div>
            </div>
        );
    }

    if (!isAuthenticated && authEnabled) {
        return null; // Auth provider will handle redirect
    }

    return <SonicApp />;
}

export default function RootApp() {
    return (
        <PersonaProvider>
            <AuthProvider>
                <ThemeProvider>
                    <DummyDataProvider>
                        <AzureSpeechProvider>
                            <App />
                        </AzureSpeechProvider>
                    </DummyDataProvider>
                </ThemeProvider>
            </AuthProvider>
        </PersonaProvider>
    );
}
