export type DemoGuestLine = {
    id: string;
    text: string;
    audio: string;
};

export type DemoGuestScript = {
    version: 1;
    voice: string;
    menuMode: "breakfast" | "lunch" | null;
    title: string;
    kicker: string;
    lines: DemoGuestLine[];
};

export type DemoScene = {
    personaId: string;
    script: DemoGuestScript;
};

export type AssistantAudioState = {
    count: number;
    activeUntilMs: number;
    completedAudioResponses?: number;
    responseInFlight?: boolean;
    followUpExpected?: boolean;
};

export type GuestTranscriptState = {
    count: number;
    lastTranscript: string;
};

export type DemoClock = {
    now(): number;
    sleep(ms: number, signal?: AbortSignal): Promise<void>;
};

export type DemoStatus =
    | { state: "scene"; scene: DemoScene }
    | { state: "guest"; scene: DemoScene; line: DemoGuestLine; speaking: boolean }
    | { state: "idle" };

export type DemoOperations = {
    prepareScene(scene: DemoScene, signal: AbortSignal): Promise<void>;
    startConversation(scene: DemoScene, signal: AbortSignal): Promise<void>;
    stopConversation(scene: DemoScene | null, signal: AbortSignal): Promise<void>;
    playGuestLine(scene: DemoScene, line: DemoGuestLine, signal: AbortSignal): Promise<void>;
    getAssistantAudioState(): AssistantAudioState;
    getGuestTranscriptState?(): GuestTranscriptState;
    setStatus(status: DemoStatus): void;
};

export type DemoTimings = {
    greetingTimeoutMs: number;
    lineTimeoutMs: number;
    guestTranscriptTimeoutMs: number;
    assistantQuietMs: number;
    finalHoldMs: number;
    pollMs: number;
};

export const DEFAULT_DEMO_TIMINGS: DemoTimings = {
    greetingTimeoutMs: 30_000,
    lineTimeoutMs: 35_000,
    guestTranscriptTimeoutMs: 5_000,
    assistantQuietMs: 2_200,
    finalHoldMs: 4_000,
    pollMs: 150
};

export const browserDemoClock: DemoClock = {
    now: () => performance.now(),
    sleep: (ms, signal) =>
        new Promise((resolve, reject) => {
            if (signal?.aborted) {
                reject(abortError());
                return;
            }
            const timer = window.setTimeout(resolve, ms);
            signal?.addEventListener(
                "abort",
                () => {
                    window.clearTimeout(timer);
                    reject(abortError());
                },
                { once: true }
            );
        })
};

export function abortError(): Error {
    const error = new Error("Demo stopped");
    error.name = "AbortError";
    return error;
}

function completedAudioResponses(state: AssistantAudioState): number {
    return state.completedAudioResponses ?? state.count;
}

function waitStart(previous: number | AssistantAudioState): { count: number; completedAudioResponses: number } {
    if (typeof previous === "number") return { count: previous, completedAudioResponses: previous };
    return { count: previous.count, completedAudioResponses: completedAudioResponses(previous) };
}

function throwIfAborted(signal: AbortSignal) {
    if (signal.aborted) throw abortError();
}

export async function waitForAssistantIdleAfter(
    previous: number | AssistantAudioState,
    getState: () => AssistantAudioState,
    clock: DemoClock,
    timings: Pick<DemoTimings, "assistantQuietMs" | "pollMs">,
    timeoutMs: number,
    signal: AbortSignal
): Promise<void> {
    const startedAt = waitStart(previous);
    const deadline = clock.now() + timeoutMs;
    while (clock.now() <= deadline) {
        throwIfAborted(signal);
        const state = getState();
        const hasCompletedAudioSinceStart =
            state.count > startedAt.count && completedAudioResponses(state) > startedAt.completedAudioResponses;
        const assistantTurnSettled = !state.responseInFlight && !state.followUpExpected;
        const playbackQuiet = clock.now() - state.activeUntilMs >= timings.assistantQuietMs;
        if (hasCompletedAudioSinceStart && assistantTurnSettled && playbackQuiet) {
            return;
        }
        const remainingMs = deadline - clock.now();
        if (remainingMs <= 0) break;
        await clock.sleep(Math.min(timings.pollMs, remainingMs), signal);
    }
    throw new Error("Timed out waiting for assistant audio to finish");
}

async function waitForGuestTranscriptAfter(
    previous: GuestTranscriptState,
    getState: () => GuestTranscriptState,
    clock: DemoClock,
    timings: Pick<DemoTimings, "pollMs">,
    timeoutMs: number,
    signal: AbortSignal
): Promise<string | null> {
    const deadline = clock.now() + timeoutMs;
    while (clock.now() <= deadline) {
        throwIfAborted(signal);
        const state = getState();
        if (state.count > previous.count) return state.lastTranscript;
        const remainingMs = deadline - clock.now();
        if (remainingMs <= 0) break;
        await clock.sleep(Math.min(timings.pollMs, remainingMs), signal);
    }
    return null;
}

export async function runDemoScenes(
    scenes: DemoScene[],
    operations: DemoOperations,
    options: { clock?: DemoClock; timings?: Partial<DemoTimings>; signal: AbortSignal }
): Promise<void> {
    const clock = options.clock ?? browserDemoClock;
    const timings = { ...DEFAULT_DEMO_TIMINGS, ...options.timings };
    let activeScene: DemoScene | null = null;

    try {
        for (const scene of scenes) {
            throwIfAborted(options.signal);
            activeScene = scene;
            operations.setStatus({ state: "scene", scene });
            await operations.prepareScene(scene, options.signal);

            const greetingStartState = { ...operations.getAssistantAudioState() };
            await operations.startConversation(scene, options.signal);
            await waitForAssistantIdleAfter(
                greetingStartState,
                operations.getAssistantAudioState,
                clock,
                timings,
                timings.greetingTimeoutMs,
                options.signal
            );

            for (const line of scene.script.lines) {
                throwIfAborted(options.signal);
                const previousState = { ...operations.getAssistantAudioState() };
                let attempts = 0;
                while (true) {
                    const transcriptState = operations.getGuestTranscriptState?.();
                    const previousTranscriptState = transcriptState ? { ...transcriptState } : undefined;
                    operations.setStatus({ state: "guest", scene, line, speaking: true });
                    await operations.playGuestLine(scene, line, options.signal);
                    operations.setStatus({ state: "guest", scene, line, speaking: false });
                    if (!previousTranscriptState || !operations.getGuestTranscriptState) break;
                    const transcript = await waitForGuestTranscriptAfter(
                        previousTranscriptState,
                        operations.getGuestTranscriptState,
                        clock,
                        timings,
                        timings.guestTranscriptTimeoutMs,
                        options.signal
                    );
                    if (transcript === null || transcript.trim() !== "" || attempts >= 1) break;
                    attempts += 1;
                }
                await waitForAssistantIdleAfter(
                    previousState,
                    operations.getAssistantAudioState,
                    clock,
                    timings,
                    timings.lineTimeoutMs,
                    options.signal
                );
            }

            await clock.sleep(timings.finalHoldMs, options.signal);
            await operations.stopConversation(scene, options.signal);
            activeScene = null;
        }
    } finally {
        operations.setStatus({ state: "idle" });
        if (activeScene !== null) {
            await operations.stopConversation(activeScene, new AbortController().signal);
        }
    }
}
