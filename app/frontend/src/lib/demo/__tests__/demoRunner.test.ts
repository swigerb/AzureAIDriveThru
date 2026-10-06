import { describe, expect, it, vi } from "vitest";

import { runDemoScenes, waitForAssistantIdleAfter, type DemoClock, type DemoOperations, type DemoScene } from "../demoRunner";

function script(lineIds: string[]) {
    return {
        version: 1 as const,
        voice: "en-US-AvaMultilingualNeural",
        menuMode: null,
        title: "Fixture scene",
        kicker: "FIXTURE KICKER",
        lines: lineIds.map(id => ({ id, text: `Line ${id}`, audio: `demo/guest/${id}.mp3` }))
    };
}

class FakeClock implements DemoClock {
    time = 0;
    sleeps: number[] = [];
    onSleep: (() => void) | null = null;

    now() {
        return this.time;
    }

    async sleep(ms: number, signal?: AbortSignal) {
        if (signal?.aborted) {
            const error = new Error("Demo stopped");
            error.name = "AbortError";
            throw error;
        }
        this.sleeps.push(ms);
        this.time += ms;
        this.onSleep?.();
    }
}

function operationsFor(scenesSeen: string[] = []) {
    const state = { count: 0, activeUntilMs: -10_000 };
    const played: string[] = [];
    const stopped: string[] = [];
    const operations: DemoOperations = {
        prepareScene: vi.fn(async scene => {
            scenesSeen.push(scene.personaId);
        }),
        startConversation: vi.fn(async () => {
            state.count += 1;
            state.activeUntilMs = -10_000;
        }),
        stopConversation: vi.fn(async scene => {
            if (scene) stopped.push(scene.personaId);
        }),
        playGuestLine: vi.fn(async (_scene, line) => {
            played.push(line.id);
            state.count += 1;
            state.activeUntilMs = -10_000;
        }),
        getAssistantAudioState: () => state,
        setStatus: vi.fn()
    };
    return { operations, state, played, stopped };
}

describe("demo runner", () => {
    it("plays guest lines in script order", async () => {
        const clock = new FakeClock();
        const { operations, played } = operationsFor();
        const controller = new AbortController();

        await runDemoScenes([{ personaId: "test-alpha", script: script(["01", "02", "03"]) }], operations, {
            clock,
            timings: { finalHoldMs: 1 },
            signal: controller.signal
        });

        expect(played).toEqual(["01", "02", "03"]);
    });

    it("retries a guest line once when the received transcript is empty", async () => {
        const clock = new FakeClock();
        const assistantState = { count: 0, activeUntilMs: -10_000, completedAudioResponses: 0 };
        const transcriptState = { count: 0, lastTranscript: "" };
        const played: string[] = [];
        const attemptsByLine: Record<string, number> = {};
        const operations: DemoOperations = {
            prepareScene: vi.fn(async () => undefined),
            startConversation: vi.fn(async () => {
                assistantState.count += 1;
                assistantState.completedAudioResponses += 1;
            }),
            stopConversation: vi.fn(async () => undefined),
            playGuestLine: vi.fn(async (_scene, line) => {
                played.push(line.id);
                attemptsByLine[line.id] = (attemptsByLine[line.id] ?? 0) + 1;
                transcriptState.count += 1;
                transcriptState.lastTranscript = line.id === "01" && attemptsByLine[line.id] === 1 ? "" : `Line ${line.id}`;
                if (transcriptState.lastTranscript) {
                    assistantState.count += 1;
                    assistantState.completedAudioResponses += 1;
                }
            }),
            getAssistantAudioState: () => assistantState,
            getGuestTranscriptState: () => transcriptState,
            setStatus: vi.fn()
        };

        await runDemoScenes([{ personaId: "test-alpha", script: script(["01", "02"]) }], operations, {
            clock,
            timings: { finalHoldMs: 1, pollMs: 100 },
            signal: new AbortController().signal
        });

        expect(played).toEqual(["01", "01", "02"]);
    });

    it("waits until new assistant audio is quiet before continuing", async () => {
        const clock = new FakeClock();
        const state = { count: 1, activeUntilMs: 3_000 };
        const controller = new AbortController();

        await waitForAssistantIdleAfter(0, () => state, clock, { assistantQuietMs: 2_200, pollMs: 500 }, 10_000, controller.signal);

        expect(clock.now()).toBeGreaterThanOrEqual(5_200);
    });

    it("waits through a tool gap for the answer response before playing the next line", async () => {
        const clock = new FakeClock();
        const state = {
            count: 0,
            activeUntilMs: -10_000,
            completedAudioResponses: 0,
            responseInFlight: false,
            followUpExpected: false
        };
        const playedAt: Record<string, number> = {};
        const events = [
            () => {
                if (clock.time >= 100 && state.count === 1) {
                    state.count = 2;
                    state.activeUntilMs = 150;
                }
            },
            () => {
                if (clock.time >= 250 && state.completedAudioResponses === 1) {
                    state.completedAudioResponses = 2;
                    state.responseInFlight = false;
                    state.followUpExpected = true;
                }
            },
            () => {
                if (clock.time >= 4_000 && state.followUpExpected) {
                    state.responseInFlight = true;
                    state.followUpExpected = false;
                }
            },
            () => {
                if (clock.time >= 4_100 && state.count === 2) {
                    state.count = 3;
                    state.activeUntilMs = 4_500;
                }
            },
            () => {
                if (clock.time >= 4_600 && state.completedAudioResponses === 2) {
                    state.completedAudioResponses = 3;
                    state.responseInFlight = false;
                }
            }
        ];
        clock.onSleep = () => events.forEach(apply => apply());
        const operations: DemoOperations = {
            prepareScene: vi.fn(async () => undefined),
            startConversation: vi.fn(async () => {
                state.count = 1;
                state.completedAudioResponses = 1;
            }),
            stopConversation: vi.fn(async () => undefined),
            playGuestLine: vi.fn(async (_scene, line) => {
                playedAt[line.id] = clock.now();
                if (line.id === "01") state.responseInFlight = true;
                if (line.id === "02") {
                    state.count += 1;
                    state.completedAudioResponses += 1;
                    state.activeUntilMs = -10_000;
                }
            }),
            getAssistantAudioState: () => state,
            setStatus: vi.fn()
        };

        await runDemoScenes([{ personaId: "test-alpha", script: script(["01", "02"]) }], operations, {
            clock,
            timings: { finalHoldMs: 1, pollMs: 100 },
            signal: new AbortController().signal
        });

        expect(playedAt["02"]).toBeGreaterThanOrEqual(6_700);
    });

    it("times out when assistant audio never arrives", async () => {
        const clock = new FakeClock();
        const controller = new AbortController();

        await expect(
            waitForAssistantIdleAfter(0, () => ({ count: 0, activeUntilMs: 0 }), clock, { assistantQuietMs: 2_200, pollMs: 500 }, 1_000, controller.signal)
        ).rejects.toThrow(/Timed out/);
    });

    it("aborts promptly when stopped", async () => {
        const clock = new FakeClock();
        const { operations, played } = operationsFor();
        const controller = new AbortController();
        vi.mocked(operations.playGuestLine).mockImplementationOnce(async () => {
            played.push("01");
            controller.abort();
        });

        await expect(
            runDemoScenes([{ personaId: "test-alpha", script: script(["01", "02"]) }], operations, {
                clock,
                timings: { finalHoldMs: 1 },
                signal: controller.signal
            })
        ).rejects.toMatchObject({ name: "AbortError" });

        expect(played).toEqual(["01"]);
        expect(operations.stopConversation).toHaveBeenCalledTimes(1);
    });

    it("runs full tours by preparing each persona scene fresh", async () => {
        const clock = new FakeClock();
        const scenesSeen: string[] = [];
        const { operations, stopped } = operationsFor(scenesSeen);
        const scenes: DemoScene[] = [
            { personaId: "test-alpha", script: script(["01"]) },
            { personaId: "test-beta", script: script(["01"]) }
        ];

        await runDemoScenes(scenes, operations, {
            clock,
            timings: { finalHoldMs: 1 },
            signal: new AbortController().signal
        });

        expect(scenesSeen).toEqual(["test-alpha", "test-beta"]);
        expect(stopped).toEqual(["test-alpha", "test-beta"]);
    });
});
