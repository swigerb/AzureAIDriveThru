export type GuestPlaybackTimings = {
    leadMs: number;
    tailMs: number;
    outputGain: number;
};

const DEFAULT_PLAYBACK_TIMINGS: GuestPlaybackTimings = {
    leadMs: 300,
    tailMs: 900,
    outputGain: 1.25
};

export class SyntheticGuestAudio {
    private audioContext: AudioContext | null = null;
    private micDestination: MediaStreamAudioDestinationNode | null = null;
    private silentSource: ConstantSourceNode | null = null;
    private silentGain: GainNode | null = null;
    private stream: MediaStream | null = null;
    private activeSources = new Set<AudioBufferSourceNode>();

    async createStream(): Promise<MediaStream> {
        const context = await this.ensureContext();
        this.disposeStream();

        this.micDestination = context.createMediaStreamDestination();
        this.silentSource = context.createConstantSource();
        this.silentGain = context.createGain();
        this.silentGain.gain.value = 0.00001;
        this.silentSource.connect(this.silentGain).connect(this.micDestination);
        this.silentSource.start();
        this.stream = this.micDestination.stream;
        return this.stream;
    }

    async playClip(url: string, signal?: AbortSignal, timings: Partial<GuestPlaybackTimings> = {}): Promise<void> {
        const context = await this.ensureContext();
        if (!this.micDestination) {
            await this.createStream();
        }
        const resolvedTimings = { ...DEFAULT_PLAYBACK_TIMINGS, ...timings };
        const response = await fetch(url, { signal });
        if (!response.ok) throw new Error(`Guest audio request failed: ${response.status}`);
        const arrayBuffer = await response.arrayBuffer();
        const decoded = await context.decodeAudioData(arrayBuffer.slice(0));
        const combined = this.withSilence(decoded, resolvedTimings.leadMs, resolvedTimings.tailMs);

        const micSource = context.createBufferSource();
        micSource.buffer = combined;
        micSource.connect(this.micDestination!);

        const speakerSource = context.createBufferSource();
        speakerSource.buffer = combined;
        const speakerGain = context.createGain();
        speakerGain.gain.value = resolvedTimings.outputGain;
        speakerSource.connect(speakerGain).connect(context.destination);

        await new Promise<void>((resolve, reject) => {
            const cleanup = () => {
                this.activeSources.delete(micSource);
                this.activeSources.delete(speakerSource);
                try {
                    micSource.disconnect();
                    speakerSource.disconnect();
                    speakerGain.disconnect();
                } catch {
                    // Best-effort Web Audio cleanup.
                }
            };
            const abort = () => {
                cleanup();
                try {
                    micSource.stop();
                    speakerSource.stop();
                } catch {
                    // Already stopped.
                }
                reject(Object.assign(new Error("Demo stopped"), { name: "AbortError" }));
            };

            signal?.addEventListener("abort", abort, { once: true });
            speakerSource.onended = () => {
                signal?.removeEventListener("abort", abort);
                cleanup();
                resolve();
            };
            this.activeSources.add(micSource);
            this.activeSources.add(speakerSource);
            micSource.start();
            speakerSource.start();
        });
    }

    dispose(): void {
        for (const source of this.activeSources) {
            try {
                source.stop();
            } catch {
                // Already stopped.
            }
        }
        this.activeSources.clear();
        this.disposeStream();
    }

    private async ensureContext(): Promise<AudioContext> {
        if (!this.audioContext || this.audioContext.state === "closed") {
            this.audioContext = new AudioContext({ sampleRate: 24_000 });
        }
        if (this.audioContext.state === "suspended") {
            await this.audioContext.resume();
        }
        return this.audioContext;
    }

    private withSilence(decoded: AudioBuffer, leadMs: number, tailMs: number): AudioBuffer {
        const sampleRate = decoded.sampleRate;
        const leadSamples = Math.round((sampleRate * leadMs) / 1000);
        const tailSamples = Math.round((sampleRate * tailMs) / 1000);
        const combined = new AudioBuffer({
            length: leadSamples + decoded.length + tailSamples,
            numberOfChannels: 1,
            sampleRate
        });
        combined.getChannelData(0).set(decoded.getChannelData(0), leadSamples);
        return combined;
    }

    private disposeStream(): void {
        try {
            this.silentSource?.stop();
        } catch {
            // Already stopped.
        }
        this.silentSource?.disconnect();
        this.silentGain?.disconnect();
        this.stream?.getTracks().forEach(track => track.stop());
        this.silentSource = null;
        this.silentGain = null;
        this.micDestination = null;
        this.stream = null;
    }
}
