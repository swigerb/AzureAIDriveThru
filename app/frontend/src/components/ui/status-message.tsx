import "./status-message.css";
import { useTranslation } from "react-i18next";
import { memo } from "react";

export type ConnectionNotice =
    | "idle"
    | "lost"
    | "reconnecting"
    | "resumed"
    | "tapToResume"
    | "resumeRejected"
    | "superseded"
    | "rateLimited"
    | "rateLimitedFinal"
    | "responseBlocked"
    | null;

type Properties = {
    isRecording: boolean;
    notice?: ConnectionNotice;
};

const NOTICE_KEYS: Record<Exclude<ConnectionNotice, null>, string> = {
    idle: "status.sessionEndedIdle",
    lost: "status.connectionLost",
    reconnecting: "status.reconnecting",
    resumed: "status.resumed",
    tapToResume: "status.resumedTapToContinue",
    resumeRejected: "status.resumeRejected",
    superseded: "status.superseded",
    rateLimited: "status.rateLimited",
    rateLimitedFinal: "status.rateLimitedFinal",
    responseBlocked: "status.responseBlocked"
};

// Notices that replace "conversation in progress" while the mic is live.
const LIVE_NOTICES: ReadonlySet<ConnectionNotice> = new Set<ConnectionNotice>(["resumed", "rateLimited", "rateLimitedFinal", "responseBlocked"]);

export default memo(function StatusMessage({ isRecording, notice = null }: Properties) {
    const { t } = useTranslation();
    if (!isRecording) {
        return (
            <p className="text text-muted-foreground mt-6 mb-4 text-sm" aria-live="polite">
                {t(notice ? NOTICE_KEYS[notice] : "status.notRecordingMessage")}
            </p>
        );
    }

    return (
        <div className="flex items-center" aria-live="polite">
            <div className="listening-equalizer">
                {[...Array(4)].map((_, index) => (
                    <span key={index} className={`bar bar-${(index % 3) + 1}`} />
                ))}
            </div>
            <p className="text-primary mt-6 mb-4 ml-2 font-semibold">
                {t(notice && LIVE_NOTICES.has(notice) ? NOTICE_KEYS[notice] : "status.conversationInProgress")}
            </p>
        </div>
    );
});
