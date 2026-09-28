"""Engine seams the companion's `local_runtime/server.py` talks to.

Mirrors `app/backend/local_runtime.py`'s own `LocalRuntimeClient` `Protocol` pattern on this side
of the wire: `local_runtime/server.py` depends only on these `Protocol`s, never on a concrete
engine class directly, so `local_runtime/tests/` can inject tiny stub engines (no real models, no
heavy ML dependencies) while `local_runtime/__main__.py` wires up the real
`WhisperSttEngine`/`Phi4ChatEngine`/`PiperTtsEngine` for production use.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable


class EngineNotReadyError(RuntimeError):
    """Raised by an engine's `ensure_loaded()` when its model files/optional dependency aren't
    available -- `local_runtime/server.py` turns this into a `503 Service Unavailable` response
    with a clear remediation message instead of a raw traceback."""


@dataclass(frozen=True)
class ChatToolCall:
    """One tool call the chat engine decided to make -- same shape as
    `app/backend/local_runtime.py`'s `LocalToolCall`, duplicated here (not imported) to keep this
    package independent of the main backend, per this task's "never import app/backend from
    local_runtime/, and vice versa" boundary."""

    id: str
    name: str
    arguments: str  # raw JSON text


@dataclass(frozen=True)
class ChatResult:
    content: str | None
    tool_calls: list[ChatToolCall] = field(default_factory=list)


@runtime_checkable
class SttEngine(Protocol):
    """Implemented for real by `local_runtime.engines.stt.WhisperSttEngine`."""

    async def ensure_loaded(self) -> None:
        """Load the model if not already loaded. Raises `EngineNotReadyError` if the model files
        are missing or the optional dependency isn't installed."""
        ...

    async def transcribe(self, pcm16_bytes: bytes, *, sample_rate: int) -> str:
        """*pcm16_bytes* is mono 16-bit PCM at *sample_rate* Hz (the `/v1/transcribe` contract
        fixes this at 16kHz -- see `local_runtime/server.py`)."""
        ...


@runtime_checkable
class ChatEngine(Protocol):
    """Implemented for real by `local_runtime.engines.chat.Phi4ChatEngine`."""

    async def ensure_loaded(self) -> None:
        ...

    async def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> ChatResult:
        """*messages* uses the same `{"role", "content", ["tool_calls"], ["tool_call_id"]}` shape
        `LocalProcessor` builds (`system`/`user`/`assistant`/`tool` roles); *tools* is the flat
        Realtime-API-style schema list (`{"type": "function", "name", "description",
        "parameters"}`) `_tool_definitions()` sends unmodified."""
        ...


@runtime_checkable
class TtsEngine(Protocol):
    """Implemented for real by `local_runtime.engines.tts.PiperTtsEngine`."""

    async def ensure_loaded(self) -> None:
        ...

    def available_voices(self) -> set[str]:
        """Voice ids whose model files are actually present on disk -- used by
        `local_runtime.voices.resolve_voice_id` so `/v1/speak` never tries to load a voice that
        was never downloaded."""
        ...

    async def synthesize(self, text: str, voice: str, *, sample_rate: int) -> bytes:
        """Returns mono 16-bit PCM at *sample_rate* Hz (the `/v1/speak` contract fixes this at
        24kHz -- see `local_runtime/server.py`)."""
        ...
