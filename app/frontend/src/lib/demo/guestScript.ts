import { personaAssetUrl } from "@/lib/personaAssets";
import type { DemoGuestScript } from "@/lib/demo/demoRunner";

function isLine(value: unknown): value is DemoGuestScript["lines"][number] {
    if (!value || typeof value !== "object") return false;
    const line = value as Record<string, unknown>;
    return typeof line.id === "string" && typeof line.text === "string" && typeof line.audio === "string";
}

export function isDemoGuestScript(value: unknown): value is DemoGuestScript {
    if (!value || typeof value !== "object") return false;
    const script = value as Record<string, unknown>;
    return (
        script.version === 1 &&
        typeof script.voice === "string" &&
        (script.menuMode === null || script.menuMode === "breakfast" || script.menuMode === "lunch") &&
        typeof script.title === "string" &&
        typeof script.kicker === "string" &&
        Array.isArray(script.lines) &&
        script.lines.length > 0 &&
        script.lines.every(isLine)
    );
}

export async function loadDemoGuestScript(personaId: string, signal?: AbortSignal): Promise<DemoGuestScript | null> {
    if (!personaId) return null;
    try {
        const response = await fetch(personaAssetUrl(personaId, "assets/demo/guestScript.json"), { signal });
        if (!response.ok) return null;
        const body = await response.json();
        return isDemoGuestScript(body) ? body : null;
    } catch {
        return null;
    }
}
