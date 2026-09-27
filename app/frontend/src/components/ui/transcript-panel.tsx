import { useEffect, useRef, memo } from "react";

interface TranscriptPanelProps {
    transcripts: Array<{ text: string; isUser: boolean; timestamp: Date }>;
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

export default memo(function TranscriptPanel({ transcripts }: TranscriptPanelProps) {
    const transcriptEndRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (transcriptEndRef.current) {
            transcriptEndRef.current.scrollIntoView({ behavior: "smooth" });
        }
    }, [transcripts]);

    useEffect(() => {
        const handleResize = () => {
            if (transcriptEndRef.current) {
                transcriptEndRef.current.scrollIntoView({ behavior: "smooth" });
            }
        };

        window.addEventListener("resize", handleResize);
        return () => window.removeEventListener("resize", handleResize);
    }, []);

    return (
        <div>
            <div className="space-y-4">
                {transcripts.map((transcript, index) => (
                    <TranscriptItem
                        key={index}
                        transcript={transcript}
                        showTimestamp={shouldShowTimestamp(transcript.timestamp, transcripts[index + 1]?.timestamp)}
                    />
                ))}
                <div ref={transcriptEndRef} />
            </div>
        </div>
    );
});
