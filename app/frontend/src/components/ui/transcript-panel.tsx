import { useEffect, useRef, memo } from "react";

interface TranscriptPanelProps {
    transcripts: Array<{ text: string; isUser: boolean; timestamp: Date }>;
    /** Classes for the scrollable container this component owns (height + overflow). */
    className?: string;
}

const formatTimestamp = (timestamp: Date) => {
    const options: Intl.DateTimeFormatOptions = {
        hour: "numeric",
        minute: "numeric",
        hour12: true
    };
    return new Intl.DateTimeFormat(navigator.language, options).format(timestamp);
};

const shouldShowTimestamp = (current: Date, next?: Date) => {
    if (!next) return true;
    const diff = (next.getTime() - current.getTime()) / 1000;
    return diff > 60;
};

const TranscriptItem = memo(function TranscriptItem({
    transcript,
    showTimestamp
}: {
    transcript: { text: string; isUser: boolean; timestamp: Date };
    showTimestamp: boolean;
}) {
    return (
        <div>
            <div
                className={`rounded-lg p-3 ${
                    transcript.isUser
                        ? "ml-auto max-w-[85%] bg-brand-secondary/15 text-brand-ink dark:bg-brand-secondary-strong/40 dark:text-white"
                        : "max-w-[85%] bg-gray-100 dark:bg-gray-800 dark:text-gray-100"
                }`}
            >
                <p className="text-sm">{transcript.text}</p>
            </div>
            {showTimestamp && (
                <div className="text-xs text-gray-500 dark:text-gray-400">{formatTimestamp(transcript.timestamp)}</div>
            )}
        </div>
    );
});

export default memo(function TranscriptPanel({ transcripts, className }: TranscriptPanelProps) {
    // GH-176: this component now owns its own scrollable container (rather than relying on a
    // sentinel `scrollIntoView()`), so autoscroll can only ever move THIS container, never the
    // window. `scrollIntoView()` scrolls every scrollable ancestor needed to bring the target
    // into view -- on a fresh load, before layout has settled, that could include the window
    // itself, which is exactly what dragged the whole page ~725-1010px past the hero with no
    // user input.
    const containerRef = useRef<HTMLDivElement>(null);
    // Skip the very first run of the transcripts effect (mount, empty or dummy-seeded transcript)
    // -- autoscroll should only happen once NEW entries arrive, never on mount.
    const hasMountedRef = useRef(false);

    useEffect(() => {
        if (!hasMountedRef.current) {
            hasMountedRef.current = true;
            return;
        }
        const container = containerRef.current;
        if (container) {
            container.scrollTop = container.scrollHeight;
        }
    }, [transcripts]);

    useEffect(() => {
        const handleResize = () => {
            const container = containerRef.current;
            if (container) {
                container.scrollTop = container.scrollHeight;
            }
        };

        window.addEventListener("resize", handleResize);
        return () => window.removeEventListener("resize", handleResize);
    }, []);

    return (
        <div ref={containerRef} className={className}>
            <div className="space-y-4">
                {transcripts.map((transcript, index) => (
                    <TranscriptItem
                        key={index}
                        transcript={transcript}
                        showTimestamp={shouldShowTimestamp(transcript.timestamp, transcripts[index + 1]?.timestamp)}
                    />
                ))}
            </div>
        </div>
    );
});
