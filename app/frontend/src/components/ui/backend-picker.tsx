import { useTranslation } from "react-i18next";

import { Label } from "@/components/ui/label";
import { Tooltip } from "@/components/ui/tooltip";
import { backendTargetUrl, currentBackendId } from "@/lib/backends";
import type { PersonaBackendEntry } from "@/types/persona";

export interface BackendPickerProps {
    backends: PersonaBackendEntry[];
    personaId: string;
    modelId: string;
    /** Same "locked while a session is active" rule as `persona-picker.tsx`/`model-picker.tsx`
     * (ADR-001 decision 2) -- switching backends means navigating to a different hostname
     * entirely, so it can't happen out from under an in-progress order either. */
    disabled: boolean;
}

const BACKEND_LABELS: Record<string, string> = {
    python: "Python",
    dotnet: "C# (.NET)"
};

const LOCK_HINT_ID = "backend-picker-lock-hint";

/**
 * Python / C# (.NET) backend switch (issue #80 F11, design doc §9 row F11 and §10.1's "Option A:
 * two container apps, header switch navigates between hostnames, no proxy"). Hidden entirely
 * whenever `/api/personas`' `backends[]` (design doc §5.2) lists fewer than two entries -- a
 * single-backend deployment has nothing to switch between (`app/backend/app.py`'s
 * `_backend_entries` only returns the second, `dotnet`, entry once `BACKEND_DOTNET_URI` is set).
 *
 * Switching backends is a real navigation (`window.location.assign`), not client-side state --
 * there is no proxy, per §10.1. Because the browser's own address bar doesn't always carry
 * `?persona=`/`?model=` (persona-context.tsx/App.tsx can resolve either purely from localStorage),
 * the target URL is built explicitly from the caller's current `personaId`/`modelId` props (see
 * `lib/backends.ts`'s `backendTargetUrl`) rather than by copying `window.location.search`, so both
 * choices survive the hop to the other backend's hostname.
 *
 * Rick's PR 134 review nit: `backendTargetUrl` refuses any scheme other than `http:`/`https:`
 * (throws) -- `backend.url` is server config, not guest input, but it's cheap defense against a
 * misconfigured entry turning a click into a `javascript:`/`file:` navigation. The throw is caught
 * here so a bad entry silently declines to navigate rather than surfacing as an uncaught error.
 */
export default function BackendPicker({ backends, personaId, modelId, disabled }: BackendPickerProps) {
    const { t } = useTranslation();
    if (backends.length < 2) return null;

    const currentId = currentBackendId(backends);

    const handleChange = (id: string) => {
        if (id === currentId) return;
        const target = backends.find(backend => backend.id === id);
        if (!target) return;
        try {
            window.location.assign(backendTargetUrl(target, personaId, modelId));
        } catch (error) {
            console.error("Refusing to navigate to backend:", error);
        }
    };

    const select = (
        <select
            id="backend-picker"
            value={currentId}
            disabled={disabled}
            onChange={event => handleChange(event.target.value)}
            aria-label="Select backend"
            aria-describedby={disabled ? LOCK_HINT_ID : undefined}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-primary disabled:cursor-not-allowed disabled:opacity-60 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
        >
            {backends.map(backend => (
                <option key={backend.id} value={backend.id}>
                    {BACKEND_LABELS[backend.id] ?? backend.id}
                </option>
            ))}
        </select>
    );

    return (
        <div className="flex items-center gap-2">
            <Label htmlFor="backend-picker" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                {t("picker.backendLabel")}
            </Label>
            {disabled ? (
                <Tooltip content={t("picker.lockedHint")}>
                    <div>{select}</div>
                </Tooltip>
            ) : (
                select
            )}
            {disabled && (
                <span id={LOCK_HINT_ID} className="sr-only">
                    {t("picker.lockedHint")}
                </span>
            )}
        </div>
    );
}
