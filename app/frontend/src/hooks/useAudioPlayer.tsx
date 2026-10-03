import { useRef, useCallback } from "react";

import { Player } from "@/components/audio/player";

const SAMPLE_RATE = 24000;
const DECODE_CHUNK = 8192;

export default function useAudioPlayer() {
    // Reuse a single Player instance across sessions
    const audioPlayer = useRef<Player>(new Player());

    const reset = useCallback(async () => {
        await audioPlayer.current.init(SAMPLE_RATE);
    }, []);

    const play = useCallback((base64Audio: string): number => {
        const binary = atob(base64Audio);
        const len = binary.length;
        const bytes = new Uint8Array(len);

        // Decode in chunks — avoids per-char function-call overhead of Uint8Array.from()
        for (let i = 0; i < len; i += DECODE_CHUNK) {
            const end = Math.min(i + DECODE_CHUNK, len);
            for (let j = i; j < end; j++) {
                bytes[j] = binary.charCodeAt(j);
            }
        }

        const pcmData = new Int16Array(bytes.buffer);
        audioPlayer.current.play(pcmData);
        return (pcmData.length / SAMPLE_RATE) * 1000;
    }, []);

    const stop = useCallback(() => {
        audioPlayer.current.stop();
    }, []);

    const waitForDrain = useCallback(async (timeoutMs?: number) => {
        return (await audioPlayer.current.waitForDrain(timeoutMs)) ?? false;
    }, []);

    return { reset, play, stop, waitForDrain };
}
