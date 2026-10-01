import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Settings from "../settings";
import { DummyDataProvider } from "@/context/dummy-data-context";
import { AzureSpeechProvider } from "@/context/azure-speech-context";

// Rick's #166 round-1 review, required item 3: the Menu Mode toggle must lock (disabled buttons,
// same shape as persona-picker.tsx/model-picker.tsx) while a session is already live, because
// `menuMode` is a `getSocketUrl` dependency -- toggling it mid-session tears down and reconnects
// the live socket (react-use-websocket's connect effect keys on `url`), which on Python rejects
// the stale resume id as `mode_mismatch` and on C# silently starts a fresh session, either way
// dropping the in-progress order. Also pins the selected-button color token: `secondary`, not
// `primary` (which becomes one daypart pack's brand red once #164/PR #167's palette swap lands,
// while `secondary` becomes its gold, matching the original card's own selected-state color).

function renderSettings(props: Partial<React.ComponentProps<typeof Settings>> = {}) {
    return render(
        <AzureSpeechProvider>
            <DummyDataProvider>
                <Settings
                    isMobile={false}
                    showSessionTokens={false}
                    onShowSessionTokensChange={() => {}}
                    verboseLogging={false}
                    onVerboseLoggingChange={() => {}}
                    logToFile={false}
                    onLogToFileChange={() => {}}
                    voiceChoice="marin"
                    onVoiceChoiceChange={() => {}}
                    menuModeEnabled={true}
                    menuMode="lunch"
                    onMenuModeChange={() => {}}
                    {...props}
                />
            </DummyDataProvider>
        </AzureSpeechProvider>
    );
}

describe("Settings menu mode toggle", () => {
    it("is enabled and clickable when no session is active", async () => {
        const onMenuModeChange = vi.fn();
        renderSettings({ menuModeDisabled: false, onMenuModeChange });
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const breakfastButton = await screen.findByRole("radio", { name: /breakfast/i });
        const lunchButton = screen.getByRole("radio", { name: /lunch/i });
        expect(breakfastButton).not.toBeDisabled();
        expect(lunchButton).not.toBeDisabled();

        await userEvent.click(breakfastButton);
        expect(onMenuModeChange).toHaveBeenCalledWith("breakfast");
    });

    it("disables both buttons and exposes a lock hint when a session is live", async () => {
        const onMenuModeChange = vi.fn();
        renderSettings({ menuModeDisabled: true, onMenuModeChange });
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const breakfastButton = await screen.findByRole("radio", { name: /breakfast/i });
        const lunchButton = screen.getByRole("radio", { name: /lunch/i });
        expect(breakfastButton).toBeDisabled();
        expect(lunchButton).toBeDisabled();

        // A disabled native button ignores clicks entirely -- confirms the lock isn't purely
        // cosmetic (e.g. a style-only "disabled" look with the handler still wired).
        await userEvent.click(breakfastButton, { pointerEventsCheck: 0 });
        expect(onMenuModeChange).not.toHaveBeenCalled();

        expect(screen.getByText(/locked for this order/i)).toBeInTheDocument();
        const radiogroup = screen.getByRole("radiogroup", { name: /menu mode/i });
        expect(radiogroup.getAttribute("aria-describedby")).toBeTruthy();
    });

    it("styles the selected mode with the secondary token, not the primary (pack brand-red) token", async () => {
        renderSettings({ menuMode: "lunch" });
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const lunchButton = await screen.findByRole("radio", { name: /lunch/i });
        const breakfastButton = screen.getByRole("radio", { name: /breakfast/i });
        expect(lunchButton.className).toContain("bg-secondary");
        expect(lunchButton.className).not.toContain("bg-primary");
        expect(breakfastButton.className).not.toContain("bg-secondary");
    });

    it("renders nothing for a pack without features.dayparts (menuModeEnabled falsy)", async () => {
        renderSettings({ menuModeEnabled: false });
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(screen.queryByText("Menu Mode")).not.toBeInTheDocument();
        expect(screen.queryByRole("radiogroup", { name: /menu mode/i })).not.toBeInTheDocument();
    });
});
