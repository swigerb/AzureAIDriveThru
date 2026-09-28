"""Persona-agnostic voice resolution for the companion's `/v1/speak` endpoint.

`persona.schema.json`'s `voice` block only has one field, `default` (e.g. `"marin"` -- an Azure
OpenAI realtime voice name), and `additionalProperties: false`, so no persona pack declares a
Piper-specific local voice today; part 1's `LocalProcessor` likewise sends a single process-wide
`voice` string to `/v1/speak` (`LOCAL_RUNTIME_VOICE_CHOICE`, or its own default,
`config.yaml`/`.env-sample`), not a persona id. This module is the "config map" that stands in for
a per-persona schema field: it maps whatever `voice` string arrives (a real Piper voice id, a
cloud-pipeline voice name a caller forwarded by mistake, or anything else) to an actually-installed
Piper voice model, falling back to a sensible default rather than failing the turn -- so the
companion works correctly regardless of which persona or pipeline sent the request.
"""

from __future__ import annotations

from dataclasses import dataclass

__all__ = ["VOICE_CATALOG", "VoiceInfo", "resolve_voice_id"]


@dataclass(frozen=True)
class VoiceInfo:
    """One Piper voice model (`rhasspy/piper-voices` on Hugging Face)."""

    id: str
    display_name: str
    locale: str


# The four voices the sibling project's own `download_local_models.py` ships, kept as the
# reference catalog here too -- any of the shipped persona packs can sound like any of these
# (persona-agnostic, no brand-specific voice literal anywhere in this module).
VOICE_CATALOG: dict[str, VoiceInfo] = {
    "en_US-amy-medium": VoiceInfo("en_US-amy-medium", "Amy", "en_US"),
    "en_GB-jenny_dioco-medium": VoiceInfo("en_GB-jenny_dioco-medium", "Jenny", "en_GB"),
    "en_US-lessac-medium": VoiceInfo("en_US-lessac-medium", "Lessac", "en_US"),
    "en_US-kristin-medium": VoiceInfo("en_US-kristin-medium", "Kristin", "en_US"),
}

# Aliases for strings a caller might send that are NOT themselves Piper voice ids -- most notably
# the Azure OpenAI realtime voice names persona packs declare in their (cloud-only) `voice.default`
# field (`persona.schema.json`). None of these are real Piper models, so each maps to the nearest
# still-installed stand-in rather than a 404/500 from the TTS endpoint.
_ALIASES: dict[str, str] = {
    "marin": "en_US-amy-medium",
    "cedar": "en_US-lessac-medium",
}


def resolve_voice_id(requested: str | None, *, default_voice: str, available: set[str] | None = None) -> str:
    """Resolve a requested voice string to a Piper voice id that is actually installed.

    *available*, when given, is the set of voice ids whose model files are present on disk
    (`PiperTtsEngine.available_voices`) -- resolution never returns an id that isn't in it (falling
    back to *default_voice* instead) so `/v1/speak` doesn't try to load a file that was never
    downloaded. When *available* is omitted (e.g. before the engine has scanned the model
    directory), every catalog/alias entry is considered valid.
    """
    candidates: list[str] = []
    if requested:
        requested = requested.strip()
        if requested in VOICE_CATALOG:
            candidates.append(requested)
        elif requested.lower() in _ALIASES:
            candidates.append(_ALIASES[requested.lower()])
        elif requested in (available or ()):
            # An operator-configured voice id not in the reference catalog (e.g. a fifth Piper
            # voice someone downloaded by hand) -- honor it if the file is actually there.
            candidates.append(requested)
    candidates.append(default_voice)
    candidates.append(next(iter(VOICE_CATALOG)))  # last-resort: any catalog voice

    for candidate in candidates:
        if available is None or candidate in available:
            return candidate
    # Nothing on disk matches anything we know about -- return the caller's own default anyway;
    # the TTS engine raises a clear "model file not found" error rather than this function
    # guessing at a path that doesn't exist either.
    return default_voice
