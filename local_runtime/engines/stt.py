"""Whisper speech-to-text engine, ported from the McDonald's sibling project's own
`app/backend/whisper_stt.py` onto this repo's `SttEngine` protocol.

`faster-whisper` is an optional dependency (`local_runtime/requirements.txt`, NOT
`app/backend/requirements.txt`) -- this module must stay importable even when it isn't installed,
so `local_runtime/tests/` can import `local_runtime.server` freely and inject a stub `SttEngine`
instead. The import is therefore deferred to `ensure_loaded()`, not module scope.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from local_runtime.config import SttConfig
from local_runtime.engines import EngineNotReadyError

logger = logging.getLogger(__name__)

# Below this many 16kHz PCM16 samples (~0.3s), faster-whisper tends to hallucinate short filler
# words out of near-silence -- same guard the sibling's whisper_stt.py applies.
_MIN_SAMPLES_FOR_TRANSCRIBE = 4800


class WhisperSttEngine:
    def __init__(self, config: SttConfig):
        self._config = config
        self._model: Any = None
        self._lock = asyncio.Lock()

    async def ensure_loaded(self) -> None:
        if self._model is not None:
            return
        async with self._lock:
            if self._model is not None:
                return
            self._model = await asyncio.to_thread(self._load_model)

    def _load_model(self) -> Any:
        try:
            from faster_whisper import WhisperModel
        except ImportError as exc:
            raise EngineNotReadyError(
                "faster-whisper isn't installed. Run "
                "`pip install -r local_runtime/requirements.txt` (see README 'On-device local mode')."
            ) from exc

        device, compute_type = self._config.device, self._config.compute_type
        if device == "auto":
            device, compute_type = _detect_device(compute_type)
        logger.info(
            "Loading faster-whisper model=%s device=%s compute_type=%s",
            self._config.model_size, device, compute_type,
        )
        try:
            return WhisperModel(self._config.model_size, device=device, compute_type=compute_type)
        except Exception as exc:  # first-run download failures, corrupt cache, etc.
            raise EngineNotReadyError(
                f"Failed to load faster-whisper model '{self._config.model_size}': {exc}. Run "
                "`scripts/download_local_models.py` first to pre-cache it."
            ) from exc

    async def transcribe(self, pcm16_bytes: bytes, *, sample_rate: int) -> str:
        await self.ensure_loaded()
        if len(pcm16_bytes) < _MIN_SAMPLES_FOR_TRANSCRIBE * 2:
            return ""
        return await asyncio.to_thread(self._transcribe_sync, pcm16_bytes, sample_rate)

    def _transcribe_sync(self, pcm16_bytes: bytes, sample_rate: int) -> str:
        import numpy as np

        audio = np.frombuffer(pcm16_bytes, dtype="<i2").astype("float32") / 32768.0
        segments, _info = self._model.transcribe(
            audio,
            language="en",
            beam_size=3,
            vad_filter=True,
            # The companion doesn't know the caller's sample rate assumption beyond the contract's
            # fixed 16kHz -- faster-whisper itself always expects 16kHz float32 mono, matching
            # `/v1/transcribe`'s documented input exactly, so *sample_rate* is accepted for the
            # `SttEngine` protocol's sake but only asserted against, never resampled here.
        )
        if sample_rate != 16000:
            logger.warning("WhisperSttEngine received sample_rate=%d, expected 16000", sample_rate)
        return "".join(segment.text for segment in segments).strip()


def _detect_device(requested_compute_type: str) -> tuple[str, str]:
    """CPU/GPU auto-detection, ported from the sibling's own `whisper_stt.py` logic: prefer CUDA
    when `torch.cuda.is_available()`, else CPU with an int8 compute type for speed on modest
    hardware."""
    try:
        import torch

        if torch.cuda.is_available():
            return "cuda", "float16"
    except ImportError:
        pass
    return "cpu", requested_compute_type
