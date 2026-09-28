import { useState, useEffect } from "react";
import { SettingsIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { useDummyDataContext } from "@/context/dummy-data-context";
import ModelPicker from "@/components/ui/model-picker";
import { VOICE_OPTIONS } from "@/lib/voices";
import type { PersonaModels } from "@/types/persona";

/** Capitalizes the first letter of a single word (e.g. "carhop" -> "Carhop"), leaving the rest
 * untouched. */
function capitalize(word: string): string {
    return word.length > 0 ? word[0].toUpperCase() + word.slice(1) : word;
}

/** Title-cases every whitespace-separated word in a persona's `roleName` for display in the
 * voice label (e.g. "carhop" -> "Carhop", "team member" -> "Team Member"). The schema only
 * guarantees `{ "type": "string", "minLength": 1 }` -- roleName is not restricted to a single
 * lowercase word, so a multi-word role name needs every word capitalized, not just the first
 * character of the whole string. */
function titleCase(roleName: string): string {
    return roleName.split(/\s+/).map(capitalize).join(" ");
}

interface SettingsProps {
    isMobile: boolean;
    showSessionTokens: boolean;
    onShowSessionTokensChange: (checked: boolean) => void;
    verboseLogging: boolean;
    onVerboseLoggingChange: (checked: boolean) => void;
    logToFile: boolean;
    onLogToFileChange: (checked: boolean) => void;
    voiceChoice: string;
    onVoiceChoiceChange: (voice: string) => void;
    /** Current persona's top-level role name (persona.schema.json's required `roleName`, e.g.
     * "carhop"), used to build a persona-aware voice label/aria-label instead of hard-coding one
     * brand's role (issue 119). Falls back to a neutral "Voice" label when empty (the
     * brand-neutral placeholder persona-context.tsx renders before any pack loads). */
    roleName?: string;
    /** Current persona's model options (design doc §7), from `/api/personas/{id}` (issue #80 F10,
     * PR 106/#75). Optional purely so `<Settings>` still renders before the persona detail's
     * first fetch resolves -- `App.tsx` always has a real value (`current.models`, even the
     * neutral placeholder's) by the time a guest can reach this dialog. */
    models?: PersonaModels;
    /** The model chosen for the NEXT session (issue #80 F10) -- ignored/hidden entirely if
     * `models` hasn't arrived yet. Optional (defaults below) so callers that never pass `models`
     * (e.g. this component's own tests) don't have to thread through unused wiring. */
    modelId?: string;
    onModelChange?: (id: string) => void;
    /** Locked while a session is active (ADR-001 decision 2), same rule as the persona picker --
     * a model change here only ever takes effect on the next session. */
    modelDisabled?: boolean;
}

export default function Settings({
    isMobile,
    showSessionTokens,
    onShowSessionTokensChange,
    verboseLogging,
    onVerboseLoggingChange,
    logToFile,
    onLogToFileChange,
    voiceChoice,
    onVoiceChoiceChange,
    roleName,
    models,
    modelId = "",
    onModelChange = () => {},
    modelDisabled = false
}: SettingsProps) {
    const [isDarkMode, setIsDarkMode] = useState(() => {
        return localStorage.getItem("isDarkMode") === "true";
    });
    const { useDummyData, setUseDummyData } = useDummyDataContext();
    const voiceLabel = roleName ? `${titleCase(roleName)} Voice` : "Voice";
    const voiceAriaLabel = roleName ? `Select ${roleName} voice` : "Select voice";

    useEffect(() => {
        localStorage.setItem("isDarkMode", isDarkMode.toString());
        if (isDarkMode) {
            document.documentElement.classList.add("dark");
        } else {
            document.documentElement.classList.remove("dark");
        }
    }, [isDarkMode]);


    const handleDarkModeChange = (checked: boolean) => {
        setIsDarkMode(checked);
    };

    const handleDummyDataChange = (checked: boolean) => {
        setUseDummyData(checked);
    };

    const handleSessionTokensChange = (checked: boolean) => {
        onShowSessionTokensChange(checked);
    };

    const handleVerboseLoggingChange = (checked: boolean) => {
        onVerboseLoggingChange(checked);
    };

    const handleLogToFileChange = (checked: boolean) => {
        onLogToFileChange(checked);
    };

    const SettingsContent = () => (
        <div className="space-y-6">
            <div className="flex items-start justify-between">
                <div className="flex-1 space-y-0.5">
                    <Label htmlFor="dark-mode" className="text-gray-900 dark:text-gray-100">
                        Dark Mode
                    </Label>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Toggle between light and dark theme</p>
                </div>
                <div className="ml-4 flex items-center gap-3 shrink-0">
                    <span className="min-w-[5rem] text-right text-xs text-muted-foreground">{isDarkMode ? "Dark Mode" : "Light Mode"}</span>
                    <Switch id="dark-mode" checked={isDarkMode} onCheckedChange={handleDarkModeChange} aria-label="Toggle dark mode" />
                </div>
            </div>
            <div className="flex flex-col gap-2">
                <div className="flex-1 space-y-0.5">
                    <Label htmlFor="voice-choice" className="text-gray-900 dark:text-gray-100">
                        {voiceLabel}
                    </Label>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Choose the drive-thru assistant voice</p>
                </div>
                <div className="flex flex-col">
                    <select
                        id="voice-choice"
                        value={voiceChoice}
                        onChange={(e) => onVoiceChoiceChange(e.target.value)}
                        className="w-full min-w-0 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
                        aria-label={voiceAriaLabel}
                    >
                        {VOICE_OPTIONS.map(voice => (
                            <option key={voice.value} value={voice.value}>
                                {voice.label}
                            </option>
                        ))}
                    </select>
                </div>
            </div>
            {models && (
                <ModelPicker
                    models={models}
                    currentId={modelId}
                    onSelect={onModelChange}
                    disabled={modelDisabled}
                />
            )}
            <div className="flex items-start justify-between">
                <div className="flex-1 space-y-0.5">
                    <Label htmlFor="dummy-data" className="text-gray-900 dark:text-gray-100">
                        Dummy Data
                    </Label>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Toggle between real data and dummy data</p>
                </div>
                <div className="ml-4 flex items-center gap-3 shrink-0">
                    <span className="min-w-[5rem] text-right text-xs text-muted-foreground">{useDummyData ? "Dummy Data" : "Real Data"}</span>
                    <Switch id="dummy-data" checked={useDummyData} onCheckedChange={handleDummyDataChange} aria-label="Toggle dummy data" />
                </div>
            </div>
            <div className="flex items-start justify-between">
                <div className="flex-1 space-y-0.5">
                    <Label htmlFor="session-token-visibility" className="text-gray-900 dark:text-gray-100">
                        Show Session Tokens
                    </Label>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Toggle visibility of session token and round-trip IDs</p>
                </div>
                <div className="ml-4 flex items-center gap-3 shrink-0">
                    <span className="min-w-[5rem] text-right text-xs text-muted-foreground">{showSessionTokens ? "Visible" : "Hidden"}</span>
                    <Switch
                        id="session-token-visibility"
                        checked={showSessionTokens}
                        onCheckedChange={handleSessionTokensChange}
                        aria-label="Toggle session token visibility"
                    />
                </div>
            </div>
            <div className="flex items-start justify-between">
                <div className="flex-1 space-y-0.5">
                    <Label htmlFor="verbose-logging" className="text-gray-900 dark:text-gray-100">
                        Verbose Logging
                    </Label>
                    <p className="text-sm text-gray-600 dark:text-gray-400">
                        Show detailed conversation traces, tool calls, and system messages in the terminal
                    </p>
                </div>
                <div className="ml-4 flex items-center gap-3 shrink-0">
                    <span className="min-w-[5rem] text-right text-xs text-muted-foreground">{verboseLogging ? "Verbose" : "Normal"}</span>
                    <Switch
                        id="verbose-logging"
                        checked={verboseLogging}
                        onCheckedChange={handleVerboseLoggingChange}
                        aria-label="Toggle verbose logging"
                    />
                </div>
            </div>
            {verboseLogging && (
                <div className="flex items-start justify-between pl-4 border-l-2 border-gray-200 dark:border-gray-700">
                    <div className="flex-1 space-y-0.5">
                        <Label htmlFor="log-to-file" className="text-gray-900 dark:text-gray-100">
                            Log to File
                        </Label>
                        <p className="text-sm text-gray-600 dark:text-gray-400">
                            Save verbose logs to a text file on the server for later review
                        </p>
                    </div>
                    <div className="ml-4 flex items-center gap-3 shrink-0">
                        <span className="min-w-[5rem] text-right text-xs text-muted-foreground">{logToFile ? "File logging" : "Off"}</span>
                        <Switch
                            id="log-to-file"
                            checked={logToFile}
                            onCheckedChange={handleLogToFileChange}
                            aria-label="Toggle log to file"
                        />
                    </div>
                </div>
            )}
        </div>
    );

    if (isMobile) {
        return (
            <Sheet>
                <SheetTrigger asChild>
                    <Button variant="outline" size="icon">
                        <SettingsIcon className="h-[1.2rem] w-[1.2rem]" />
                        <span className="sr-only">Open settings</span>
                    </Button>
                </SheetTrigger>
                <SheetContent>
                    <SheetHeader>
                        <SheetTitle>Settings</SheetTitle>
                        <SheetDescription>Adjust your app preferences here.</SheetDescription>
                    </SheetHeader>
                    <SettingsContent />
                </SheetContent>
            </Sheet>
        );
    }

    return (
        <Dialog>
            <DialogTrigger asChild>
                <Button variant="outline" size="icon">
                    <SettingsIcon className="h-[1.2rem] w-[1.2rem]" />
                    <span className="sr-only">Open settings</span>
                </Button>
            </DialogTrigger>
            <DialogContent className="sm:max-w-[425px]">
                <DialogHeader>
                    <DialogTitle>Settings</DialogTitle>
                    <DialogDescription>Adjust your app preferences here.</DialogDescription>
                </DialogHeader>
                <SettingsContent />
            </DialogContent>
        </Dialog>
    );
}
