import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { AzureSpeechProvider, useAzureSpeechOnContext } from "../azure-speech-context";

// Rick's PR 134 review, item 3: the legacy "Azure Backend" toggle used to read its initial value
// from `localStorage.useAzureSpeechOn`, so a browser that had ever turned it on stayed on the old
// client-side speech path forever -- ignoring the realtime socket entirely, and with it the model
// picker's own `?model=` choice (six branches in App.tsx key off this flag). There is no UI left
// anywhere in the app that can turn it back on, so the fix is to force it off and clear the stale
// key on every load.

function Probe() {
    const { useAzureSpeechOn } = useAzureSpeechOnContext();
    return <div data-testid="value">{String(useAzureSpeechOn)}</div>;
}

afterEach(() => {
    localStorage.clear();
});

describe("AzureSpeechProvider", () => {
    it("starts false even when localStorage.useAzureSpeechOn was previously stored true", () => {
        localStorage.setItem("useAzureSpeechOn", "true");

        render(
            <AzureSpeechProvider>
                <Probe />
            </AzureSpeechProvider>
        );

        expect(screen.getByTestId("value")).toHaveTextContent("false");
    });

    it("clears the stale localStorage key on mount rather than just overwriting it with 'false'", () => {
        localStorage.setItem("useAzureSpeechOn", "true");

        render(
            <AzureSpeechProvider>
                <Probe />
            </AzureSpeechProvider>
        );

        // Removed outright (not merely set to "false") -- so no later code path can ever read a
        // truthy-looking leftover string back out of storage for this key.
        expect(localStorage.getItem("useAzureSpeechOn")).toBeNull();
    });

    it("starts false and has nothing to clear when no key was ever stored", () => {
        render(
            <AzureSpeechProvider>
                <Probe />
            </AzureSpeechProvider>
        );

        expect(screen.getByTestId("value")).toHaveTextContent("false");
        expect(localStorage.getItem("useAzureSpeechOn")).toBeNull();
    });
});
