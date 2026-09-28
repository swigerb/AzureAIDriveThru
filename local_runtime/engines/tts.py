"""Piper TTS engine, ported from a sibling drive-thru project's own on-device reference
implementation (`piper_tts.py`) onto this repo's `TtsEngine` protocol.

`piper-tts` is an optional dependency (`local_runtime/requirements.txt`, NOT
`app/backend/requirements.txt`) -- like `engines/stt.py`, the import is deferred to
`ensure_loaded()` so this module stays importable without it, letting `local_runtime/tests/` inject
a stub `TtsEngine` instead. Unlike the sibling's own multi-voice-with-lazy-swap design, this engine
keeps every voice file it finds on disk loaded at once (Piper voices are small, ~60MB each, and the
companion process is long-lived) -- `/v1/speak`'s `voice` field can therefore change per request
with no unload/reload latency, matching `local_runtime.voices.resolve_voice_id`'s own
per-request resolution.
"""

from __future__ import annotations

import asyncio
import logging
import re
from typing import Any

from local_runtime.config import TtsConfig
from local_runtime.engines import EngineNotReadyError
from local_runtime.voices import VOICE_CATALOG

logger = logging.getLogger(__name__)

_SENTENCE_RE = re.compile(r"(?<=[.!?])\s+")


class PiperTtsEngine:
    def __init__(self, config: TtsConfig):
        self._config = config
        self._voices: dict[str, Any] = {}  # voice id -> loaded piper.voice.PiperVoice
        self._native_rates: dict[str, int] = {}
        self._lock = asyncio.Lock()
        self._scanned = False

    async def ensure_loaded(self) -> None:
        """Scans `self._config.model_dir` for installed voice files and loads whichever of
        `VOICE_CATALOG`'s ids are actually present. A pack with zero voices downloaded raises
        `EngineNotReadyError`; a partial set (e.g. only the default voice) is fine -- `available_voices`
        reports exactly what got loaded, and `voices.resolve_voice_id` falls back accordingly."""
        if self._scanned:
            return
        async with self._lock:
            if self._scanned:
                return
            await asyncio.to_thread(self._load_all)
            self._scanned = True

    def _load_all(self) -> None:
        try:
            from piper.voice import PiperVoice
        except ImportError as exc:
            raise EngineNotReadyError(
                "piper-tts isn't installed. Run `pip install -r local_runtime/requirements.txt` "
                "(see README 'On-device local mode')."
            ) from exc

        model_dir = self._config.model_dir
        for voice_id in VOICE_CATALOG:
            onnx_path = model_dir / f"{voice_id}.onnx"
            json_path = model_dir / f"{voice_id}.onnx.json"
            if not onnx_path.exists():
                continue
            try:
                voice = PiperVoice.load(str(onnx_path), config_path=str(json_path) if json_path.exists() else None)
            except Exception:
                logger.exception("Failed to load Piper voice '%s' from %s", voice_id, onnx_path)
                continue
            self._voices[voice_id] = voice
            self._native_rates[voice_id] = getattr(getattr(voice, "config", None), "sample_rate", 22050)
            logger.info("Loaded Piper voice '%s' (native_rate=%d)", voice_id, self._native_rates[voice_id])

        if not self._voices:
            raise EngineNotReadyError(
                f"No Piper voice models found in '{model_dir}'. Run "
                "`scripts/download_local_models.py` first to fetch at least one voice."
            )

    def available_voices(self) -> set[str]:
        return set(self._voices)

    async def synthesize(self, text: str, voice: str, *, sample_rate: int) -> bytes:
        await self.ensure_loaded()
        if voice not in self._voices:
            raise EngineNotReadyError(f"Piper voice '{voice}' is not loaded (available: {sorted(self._voices)})")
        return await asyncio.to_thread(self._synthesize_sync, text, voice, sample_rate)

    def _synthesize_sync(self, text: str, voice_id: str, sample_rate: int) -> bytes:
        voice = self._voices[voice_id]
        native_rate = self._native_rates.get(voice_id, 22050)
        syn_config = None
        try:
            from piper.config import SynthesisConfig

            syn_config = SynthesisConfig(length_scale=self._config.length_scale)
        except ImportError:
            pass

        pcm_parts: list[bytes] = []
        for sentence in _split_sentences(text):
            try:
                chunks = voice.synthesize(sentence, syn_config=syn_config)
            except TypeError:
                # Older piper-tts releases don't accept syn_config -- fall back to defaults.
                chunks = voice.synthesize(sentence)
            for chunk in chunks:
                pcm_parts.append(chunk.audio_int16_bytes)
        raw_pcm = b"".join(pcm_parts)
        if not raw_pcm:
            return b""
        if native_rate != sample_rate:
            raw_pcm = _resample_pcm16(raw_pcm, native_rate, sample_rate)
        return raw_pcm


def _split_sentences(text: str) -> list[str]:
    parts = _SENTENCE_RE.split(text.strip())
    return [p for p in parts if p.strip()] or [text.strip()]


def _resample_pcm16(pcm_bytes: bytes, src_rate: int, dst_rate: int) -> bytes:
    """Linear-interpolation PCM16 mono resampler -- same algorithm as
    `app/backend/local_runtime.py::_resample_pcm16`, duplicated (not imported) to keep
    `local_runtime/` independent of `app/backend/` per this task's package boundary; no numpy
    dependency needed for this one arithmetic helper."""
    import array

    if src_rate == dst_rate or not pcm_bytes:
        return pcm_bytes
    usable_len = len(pcm_bytes) - (len(pcm_bytes) % 2)
    src_samples = array.array("h")
    src_samples.frombytes(pcm_bytes[:usable_len])
    src_count = len(src_samples)
    if src_count == 0:
        return b""
    dst_count = max(1, round(src_count * dst_rate / src_rate))
    ratio = src_count / dst_count
    dst_samples = array.array("h", bytes(2 * dst_count))
    for i in range(dst_count):
        pos = i * ratio
        idx = int(pos)
        frac = pos - idx
        a = src_samples[idx]
        b = src_samples[idx + 1] if idx + 1 < src_count else a
        dst_samples[i] = int(a + (b - a) * frac)
    return dst_samples.tobytes()


__all__ = ["PiperTtsEngine"]
