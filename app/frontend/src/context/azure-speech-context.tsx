import { createContext, useContext, useState, useEffect, ReactNode } from "react";

interface AzureSpeechContextProps {
    useAzureSpeechOn: boolean;
    setUseAzureSpeechOn: (value: boolean) => void;
}

const AzureSpeechContext = createContext<AzureSpeechContextProps | undefined>(undefined);

/**
 * Rick's PR 134 review, item 3: this legacy client-side speech path (the realtime socket and
 * `?model=`/the new model picker are both bypassed whenever it's on) used to read its initial
 * value from `localStorage.useAzureSpeechOn`, so any browser that had ever turned the toggle on
 * stayed on it forever -- there is no UI left anywhere in the app to turn it back off.
 *
 * The provider now always starts `false` and, on mount, removes the stale key outright rather than
 * just overwriting it with `"false"` -- a guest who had it on before this fix lands is switched
 * back onto the realtime path (and their chosen model) on their very next load, with nothing left
 * behind to resurrect the old behavior on a later release. `setUseAzureSpeechOn` still exists on
 * the context (nothing in this app calls it -- there's no toggle to flip it back on), so a future
 * cleanup can delete the now-fully-unreachable client-side path (`useAzureSpeech`, this provider,
 * and `App.tsx`'s six branches on it) as a follow-up under #80 without touching this contract.
 */
export const AzureSpeechProvider = ({ children }: { children: ReactNode }) => {
    const [useAzureSpeechOn, setUseAzureSpeechOn] = useState(false);

    useEffect(() => {
        localStorage.removeItem("useAzureSpeechOn");
    }, []);

    return <AzureSpeechContext.Provider value={{ useAzureSpeechOn, setUseAzureSpeechOn }}>{children}</AzureSpeechContext.Provider>;
};

export const useAzureSpeechOnContext = () => {
    const context = useContext(AzureSpeechContext);
    if (!context) {
        throw new Error("useAzureSpeechOnContext must be used within an AzureSpeechProvider");
    }
    return context;
};
