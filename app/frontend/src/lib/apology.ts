// Pre-recorded "Sorry, give me just a second." clips, played when the server's
// silent rate-limit retry failed too (extension.rate_limited, attempt 1).
// Generated with scripts/generate_apology_clips.py, served from each persona pack's
// assets (issue #80 F7: retired the frontend's own bundled copies under public/audio/).
import { personaAssetUrl } from "@/lib/personaAssets";

export const APOLOGY_LANGUAGES = ["en", "es", "fr", "ja"] as const;
export type ApologyLanguage = (typeof APOLOGY_LANGUAGES)[number];

export const APOLOGY_CLIP_TIMEOUT_MS = 5000;

export function apologyLanguage(language: string | undefined | null): ApologyLanguage {
    const base = (language ?? "").toLowerCase().split(/[-_]/)[0];
    return (APOLOGY_LANGUAGES as readonly string[]).includes(base) ? (base as ApologyLanguage) : "en";
}

/**
 * Builds the persona-pack apology clip URL for `language`, substituting `{lang}` in the pack's
 * `ui.assets.apologyClip` template (e.g. `"assets/audio/apology-{lang}.wav"`,
 * `personas/<id>/persona.json`) and routing it through `personaAssetUrl`. Returns `null` when a
 * persona declares no `apologyClip` at all (the field is optional -- `_UiAssets.apologyClip: str |
 * None` in `persona_loader.py`) so callers can skip playing anything rather than requesting a
 * clip that doesn't exist.
 */
export function apologyClipUrl(personaId: string, template: string | undefined | null, language: string | undefined | null): string | null {
    if (!template) return null;
    const relative = template.replace("{lang}", apologyLanguage(language));
    return personaAssetUrl(personaId, relative);
}

/** Plays the clip; resolves when it ends, fails to play, or after timeoutMs, whichever is first. */
export function playApologyClip(url: string, timeoutMs = APOLOGY_CLIP_TIMEOUT_MS): Promise<void> {
    return new Promise(resolve => {
        let settled = false;
        let timer: ReturnType<typeof setTimeout> | undefined;
        const audio = new Audio(url);
        const finish = () => {
            if (settled) return;
            settled = true;
            if (timer !== undefined) clearTimeout(timer);
            audio.removeEventListener("ended", finish);
            audio.removeEventListener("error", finish);
            resolve();
        };
        audio.addEventListener("ended", finish);
        audio.addEventListener("error", finish);
        timer = setTimeout(() => {
            audio.pause();
            finish();
        }, timeoutMs);
        try {
            const played = audio.play();
            if (played && typeof played.catch === "function") {
                played.catch(error => {
                    console.warn("Apology clip could not play:", error);
                    finish();
                });
            }
        } catch (error) {
            console.warn("Apology clip could not play:", error);
            finish();
        }
    });
}
