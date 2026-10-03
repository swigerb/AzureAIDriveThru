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
    setStatus(status: DemoStatus): void;
};

export type DemoTimings = {
    greetingTimeoutMs: number;
    lineTimeoutMs: number;
    assistantQuietMs: number;
    finalHoldMs: number;
    pollMs: number;
};

export const DEFAULT_DEMO_TIMINGS: DemoTimings = {
    greetingTimeoutMs: 30_000,
    lineTimeoutMs: 35_000,
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

function throwIfAborted(signal: AbortSignal) {
    if (signal.aborted) throw abortError();
}

export async function waitForAssistantIdleAfter(
    previousCount: number,
    getState: () => AssistantAudioState,
    clock: DemoClock,
    timings: Pick<DemoTimings, "assistantQuietMs" | "pollMs">,
    timeoutMs: number,
    signal: AbortSignal
): Promise<void> {
    const deadline = clock.now() + timeoutMs;
    while (clock.now() <= deadline) {
        throwIfAborted(signal);
        const state = getState();
        if (state.count > previousCount && clock.now() - state.activeUntilMs >= timings.assistantQuietMs) {
            return;
        }
        const remainingMs = deadline - clock.now();
        if (remainingMs <= 0) break;
        await clock.sleep(Math.min(timings.pollMs, remainingMs), signal);
    }
    throw new Error("Timed out waiting for assistant audio to finish");
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

            const greetingStartCount = operations.getAssistantAudioState().count;
            await operations.startConversation(scene, options.signal);
            await waitForAssistantIdleAfter(
                greetingStartCount,
                operations.getAssistantAudioState,
                clock,
                timings,
                timings.greetingTimeoutMs,
                options.signal
            );

            for (const line of scene.script.lines) {
                throwIfAborted(options.signal);
                const previousCount = operations.getAssistantAudioState().count;
                operations.setStatus({ state: "guest", scene, line, speaking: true });
                await operations.playGuestLine(scene, line, options.signal);
                operations.setStatus({ state: "guest", scene, line, speaking: false });
                await waitForAssistantIdleAfter(
                    previousCount,
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
        if (activeScene !== null && !options.signal.aborted) {
            await operations.stopConversation(activeScene, options.signal);
        }
    }
}
