import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Settings from "../settings";
import { DummyDataProvider } from "@/context/dummy-data-context";
import { AzureSpeechProvider } from "@/context/azure-speech-context";
import { DEFAULT_VOICE, VOICE_OPTIONS, resolveVoice } from "@/lib/voices";

// Every voice gpt-realtime-2.1 accepts (the service's own list when it rejects
// anything else, probed live 2026-09-22).
const GA_REALTIME_VOICES = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"];

function renderSettings(voiceChoice: string, roleName?: string, voiceLabelOverride?: string, defaultVoiceId?: string) {
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
                    voiceChoice={voiceChoice}
                    onVoiceChoiceChange={() => {}}
                    roleName={roleName}
                    voiceLabelOverride={voiceLabelOverride}
                    defaultVoiceId={defaultVoiceId}
                />
            </DummyDataProvider>
        </AzureSpeechProvider>
    );
}

describe("Carhop voice picker", () => {
    beforeEach(() => localStorage.clear());

    it("offers every gpt-realtime-2.1 voice and defaults to marin", async () => {
        renderSettings(resolveVoice(localStorage.getItem("voiceChoice")), "carhop");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const picker = (await screen.findByLabelText("Select carhop voice")) as HTMLSelectElement;
        const offered = within(picker)
            .getAllByRole("option")
            .map(o => (o as HTMLOptionElement).value);

        expect([...offered].sort()).toEqual(GA_REALTIME_VOICES);
        expect(new Set(offered).size).toBe(offered.length);
        expect(picker.value).toBe("marin");
    });

    it("marks the OpenAI-recommended voices", async () => {
        renderSettings(DEFAULT_VOICE, "carhop");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));
        const picker = await screen.findByLabelText("Select carhop voice");

        const recommended = within(picker)
            .getAllByRole("option")
            .filter(o => /recommended/i.test(o.textContent ?? ""))
            .map(o => (o as HTMLOptionElement).value);
        expect(recommended.sort()).toEqual(["cedar", "marin"]);
        expect(VOICE_OPTIONS.filter(v => v.recommended).map(v => v.value).sort()).toEqual(["cedar", "marin"]);
    });

    it("keeps a stored valid voice and replaces an unknown one with the default", () => {
        expect(DEFAULT_VOICE).toBe("marin");
        expect(resolveVoice("shimmer")).toBe("shimmer");
        expect(resolveVoice("nova")).toBe("marin");
        expect(resolveVoice(null)).toBe("marin");
        expect(resolveVoice("")).toBe("marin");
    });

    // Issue #119 item 2: the voice label/aria-label must follow the active persona's roleName
    // (persona.schema.json) instead of hard-coding a specific brand's "Carhop", and must fall
    // back to a brand-neutral label when no persona has loaded yet (roleName omitted/empty).
    it("labels the voice picker for a different persona's role name", async () => {
        renderSettings(DEFAULT_VOICE, "barista");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("Barista Voice")).toBeInTheDocument();
        expect(await screen.findByLabelText("Select barista voice")).toBeInTheDocument();
        expect(screen.queryByText("Carhop Voice")).not.toBeInTheDocument();
    });

    it("falls back to a neutral label when no persona roleName is available", async () => {
        renderSettings(DEFAULT_VOICE);
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("Voice")).toBeInTheDocument();
        expect(await screen.findByLabelText("Select voice")).toBeInTheDocument();
        expect(screen.queryByText("Carhop Voice")).not.toBeInTheDocument();
    });

    // Issue #119 item 2 (Rick review round 2): roleName is not restricted to a single lowercase
    // word by the schema (`{ "type": "string", "minLength": 1 }`), so a multi-word role name like
    // a "team member" pack must title-case every word for the visible label ("Team Member
    // Voice"), while the aria-label stays lowercase to match roleName as written.
    it("title-cases every word of a multi-word role name", async () => {
        renderSettings(DEFAULT_VOICE, "team member");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("Team Member Voice")).toBeInTheDocument();
        expect(await screen.findByLabelText("Select team member voice")).toBeInTheDocument();
    });
});

// Issue 164 B8/C5: two of the three original personas override the roleName-derived
// label with their own "AI Voice" copy plus a "Default: Marin" hint; the third original has
// neither, so it keeps the plain roleName-derived label with no hint at all.
describe("voice label override and default hint (issue 164 B8/C5)", () => {
    beforeEach(() => localStorage.clear());

    it("prefers voiceLabelOverride over the roleName-derived label when both are present", async () => {
        renderSettings(DEFAULT_VOICE, "team member", "AI Voice", "marin");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("AI Voice")).toBeInTheDocument();
        expect(screen.queryByText("Team Member Voice")).not.toBeInTheDocument();
    });

    it("still uses roleName for the aria-label even when voiceLabelOverride is set", async () => {
        renderSettings(DEFAULT_VOICE, "team member", "AI Voice", "marin");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByLabelText("Select team member voice")).toBeInTheDocument();
    });

    it("shows a capitalized 'Default: X' hint alongside an override", async () => {
        renderSettings(DEFAULT_VOICE, "team member", "AI Voice", "marin");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("Default: Marin")).toBeInTheDocument();
    });

    it("omits the default hint when there is no voiceLabelOverride, even with a defaultVoiceId", async () => {
        renderSettings(DEFAULT_VOICE, "carhop", undefined, "marin");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("Carhop Voice")).toBeInTheDocument();
        expect(screen.queryByText(/Default:/)).not.toBeInTheDocument();
    });

    it("omits the default hint when there is an override but no defaultVoiceId", async () => {
        renderSettings(DEFAULT_VOICE, "team member", "AI Voice");
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        expect(await screen.findByText("AI Voice")).toBeInTheDocument();
        expect(screen.queryByText(/Default:/)).not.toBeInTheDocument();
    });
});
