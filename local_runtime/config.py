"""Environment-driven configuration for the companion local-runtime process.

Every setting has a sane default so `python -m local_runtime` works out of the box once models are
downloaded to the default `models/` directory (`scripts/download_local_models.py`). All overrides
are plain env vars, `LOCAL_RUNTIME_SERVER_*`/`LOCAL_RUNTIME_STT_*`/`LOCAL_RUNTIME_CHAT_*`/
`LOCAL_RUNTIME_TTS_*` -- deliberately namespaced apart from the main backend's own
`LOCAL_RUNTIME_ENDPOINT`/`LOCAL_RUNTIME_VOICE_CHOICE` (`app/backend/.env-sample`), which configure
the CLIENT side of the same HTTP contract from the other process.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

# This file lives at <repo root>/local_runtime/config.py -- models/ is a repo-root sibling of
# local_runtime/ and app/, gitignored (issue #81 acceptance: "no models committed").
_REPO_ROOT = Path(__file__).resolve().parents[1]
_DEFAULT_MODEL_DIR = _REPO_ROOT / "models"


def _env_str(name: str, default: str) -> str:
    value = os.environ.get(name)
    return value if value else default


def _env_int(name: str, default: int) -> int:
    value = os.environ.get(name)
    try:
        return int(value) if value else default
    except ValueError:
        return default


def _env_float(name: str, default: float) -> float:
    value = os.environ.get(name)
    try:
        return float(value) if value else default
    except ValueError:
        return default


@dataclass(frozen=True)
class SttConfig:
    """faster-whisper settings (ported from the sibling's `whisper_stt.py`)."""

    model_size: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_STT_MODEL", "small"))
    device: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_STT_DEVICE", "auto"))
    compute_type: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_STT_COMPUTE_TYPE", "int8"))


@dataclass(frozen=True)
class ChatConfig:
    """Phi-4-mini ONNX Runtime GenAI settings (ported from the sibling's `phi4_model.py`)."""

    model_dir: Path = field(
        default_factory=lambda: Path(_env_str("LOCAL_RUNTIME_CHAT_MODEL_DIR", str(_DEFAULT_MODEL_DIR / "phi4-mini")))
    )
    variant: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_CHAT_VARIANT", "cpu"))
    device: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_CHAT_DEVICE", "auto"))
    max_length: int = field(default_factory=lambda: _env_int("LOCAL_RUNTIME_CHAT_MAX_LENGTH", 2048))
    temperature: float = field(default_factory=lambda: _env_float("LOCAL_RUNTIME_CHAT_TEMPERATURE", 0.6))
    inference_timeout_seconds: float = field(
        default_factory=lambda: _env_float("LOCAL_RUNTIME_CHAT_TIMEOUT_SECONDS", 30.0)
    )
    max_tool_rounds: int = field(default_factory=lambda: _env_int("LOCAL_RUNTIME_CHAT_MAX_TOOL_HINTS", 8))

    @property
    def model_path(self) -> Path:
        """Directory containing `genai_config.json` for the chosen CPU/GPU variant -- see
        `scripts/download_local_models.py`, which lays out
        `models/phi4-mini/<variant>/<precision-dir>/`."""
        variant_dir = self.model_dir / self.variant
        if not variant_dir.is_dir():
            return variant_dir
        # HF's `microsoft/Phi-4-mini-instruct-onnx` nests one precision directory under each
        # variant (e.g. `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/`, downloaded here as
        # `cpu/cpu-int4-rtn-block-32-acc-level-4/`) -- descend into it if present so callers don't
        # need to know the exact precision folder name.
        children = [p for p in variant_dir.iterdir() if p.is_dir()]
        if len(children) == 1 and (children[0] / "genai_config.json").exists():
            return children[0]
        return variant_dir


@dataclass(frozen=True)
class TtsConfig:
    """Piper settings (ported from the sibling's `piper_tts.py`)."""

    model_dir: Path = field(
        default_factory=lambda: Path(_env_str("LOCAL_RUNTIME_TTS_MODEL_DIR", str(_DEFAULT_MODEL_DIR / "piper")))
    )
    default_voice: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_TTS_DEFAULT_VOICE", "en_US-amy-medium"))
    length_scale: float = field(default_factory=lambda: _env_float("LOCAL_RUNTIME_TTS_LENGTH_SCALE", 0.9))
    sample_rate: int = field(default_factory=lambda: _env_int("LOCAL_RUNTIME_TTS_SAMPLE_RATE", 24000))


@dataclass(frozen=True)
class ServerConfig:
    host: str = field(default_factory=lambda: _env_str("LOCAL_RUNTIME_HOST", "0.0.0.0"))
    port: int = field(default_factory=lambda: _env_int("LOCAL_RUNTIME_PORT", 8100))
    # Guards against a runaway/mis-sent upload on the STT endpoint -- 30s of 16kHz mono PCM16, the
    # main backend's own turn-length ceiling (see local_processor.py's VAD silence window), with
    # generous headroom.
    max_upload_bytes: int = field(default_factory=lambda: _env_int("LOCAL_RUNTIME_MAX_UPLOAD_BYTES", 10 * 1024 * 1024))


@dataclass(frozen=True)
class LocalRuntimeConfig:
    stt: SttConfig = field(default_factory=SttConfig)
    chat: ChatConfig = field(default_factory=ChatConfig)
    tts: TtsConfig = field(default_factory=TtsConfig)
    server: ServerConfig = field(default_factory=ServerConfig)


def load_config() -> LocalRuntimeConfig:
    """Read the companion process's configuration from the environment. Called once at process
    startup (`local_runtime/__main__.py`); tests build `LocalRuntimeConfig()` directly instead."""
    return LocalRuntimeConfig()
