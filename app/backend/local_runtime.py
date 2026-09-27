"""HTTP client for the companion local-runtime process (issue #81, design doc section 7.4).

Design decision (this module + `local_processor.py`, noted in the PR description): rather than
loading Whisper/Phi-4/Piper in-process inside THIS backend the way the sibling repo's local mode
does, the `local` pipeline talks to a small companion process over a plain HTTP/JSON
contract, at the base URL `LOCAL_RUNTIME_ENDPOINT` points at (`model_catalog.py`,
`processors.resolve_local_model`). Rationale:
  1. Keeps this backend's own `requirements.txt` free of ONNX Runtime GenAI / faster-whisper /
     piper-tts -- those heavy, optional dependencies live only in the companion runtime
     (`local_runtime_server/`, ported from the sibling's `phi4_model.py`/`whisper_stt.py`/
     `piper_tts.py`), matching issue #81's "optional dependencies... requirements extra" goal.
  2. Gives conformance/unit tests a small, fully-fakeable HTTP seam
     (`tests/test_local_processor.py`'s `FakeLocalRuntimeServer`, an in-process aiohttp test
     server), the same shape `cascade_processor.py`'s own tests already fake
     `FakeChatCompletionsServer`/the Azure OpenAI audio endpoints with.
  3. Makes "local mode only activates when the local runtime endpoint is configured" (issue
     #81's own acceptance line) a literal, testable fact about one env var, rather than an
     in-process "are the model files present on disk" check.

Wire contract (this module owns both sides of the seam -- the client below, and the reference
server in `local_runtime_server/`):

  POST {endpoint}/v1/transcribe
    body: raw PCM16 mono audio bytes, Content-Type: application/octet-stream, resampled by this
    client to `_STT_SAMPLE_RATE` (16 kHz -- what Whisper expects) from whatever rate the caller
    captured at (this backend's mic input is 24 kHz everywhere else, matching
    `cascade_processor.py`'s own `_AUDIO_SAMPLE_RATE`).
    response: `{"text": "..."}`

  POST {endpoint}/v1/chat
    body: `{"messages": [{"role": ..., "content": ..., ["tool_calls": [...]],
    ["tool_call_id": ...]}, ...], "tools": [{"name": ..., "description": ..., "parameters":
    {...}}, ...]}` -- `messages` in the same flat OpenAI-chat-style shape `local_processor.py`
    accumulates turn-by-turn; `tools` straight from `tools.py`'s own `Tool.schema` (Beth's #77
    scope, unedited -- the SAME flat `{"name", "description", "parameters"}` shape the realtime
    pipeline already sends upstream, no Chat-Completions-style `{"function": {...}}` nesting
    needed since there's no SDK on this side of the wire).
    response: `{"content": "..." | null, "tool_calls": [{"id": ..., "name": ...,
    "arguments": "...json..."}]}`

  POST {endpoint}/v1/speak
    body: `{"text": "...", "voice": "..."}`
    response: raw PCM16 mono audio bytes, Content-Type: application/octet-stream, 24 kHz mono
    (same as every other pipeline's speaker output -- `cascade_processor.py`'s own
    `_AUDIO_SAMPLE_RATE`/`_AUDIO_SAMPLE_WIDTH`; the reference server resamples Piper's native
    rate up/down to this before responding, exactly as the ported `piper_tts.py` already does).

Deliberately a plain HTTP/JSON contract, not the Azure AI Inference SDK `cascade_processor.py`
uses -- the companion process isn't a Foundry deployment, just a small same-box (or LAN) service
with no SDK-specific auth/type surface to match.
"""

from __future__ import annotations

import array
import json
import logging
from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable

import aiohttp

logger = logging.getLogger(__name__)

__all__ = [
    "HttpLocalRuntimeClient",
    "LocalChatResult",
    "LocalRuntimeClient",
    "LocalRuntimeError",
    "LocalToolCall",
]

# Whisper's expected input rate (see the ported local_runtime_server/whisper_stt.py). Every
# other pipeline's mic capture is 24 kHz (cascade_processor.py's own _AUDIO_SAMPLE_RATE) -- this
# client resamples down before uploading, so the wire contract's STT side is always 16 kHz,
# regardless of what any future caller happens to capture at.
_STT_SAMPLE_RATE = 16000


class LocalRuntimeError(Exception):
    """Raised when the companion local-runtime process is unreachable, times out, or returns a
    malformed response. `LocalProcessor` treats this the same as any other turn failure (log and
    end the turn) -- never a silent fallback to a cloud pipeline; issue #81's local mode is a
    distinct, explicit choice, not a cloud/local blend."""


@dataclass(frozen=True)
class LocalToolCall:
    """One tool call parsed out of the local chat model's `/v1/chat` response -- this
    pipeline's equivalent of azure-ai-inference's `ChatCompletionsToolCall`
    (`cascade_processor.py`'s `_execute_tool_call`), a plain dataclass since there is no SDK on
    this side of the wire."""

    id: str
    name: str
    arguments: str  # raw JSON text, exactly like the SDK's own tool_call.function.arguments


@dataclass(frozen=True)
class LocalChatResult:
    """One `/v1/chat` response: either a final answer (`content`, no tool calls) or one or more
    tool calls to execute before the turn continues -- mirrors azure-ai-inference's
    `ChatChoice.message` shape closely enough that `LocalProcessor`'s tool loop reads almost
    identically to `CascadeProcessor._run_chat_tool_loop`."""

    content: str | None
    tool_calls: list[LocalToolCall] = field(default_factory=list)


@runtime_checkable
class LocalRuntimeClient(Protocol):
    """The seam `LocalProcessor` talks to -- implemented for real by `HttpLocalRuntimeClient`
    below, and by `tests/test_local_processor.py`'s `FakeLocalRuntimeServer` (an in-process
    aiohttp test server standing in for the whole companion process, not just this client)."""

    async def transcribe(self, pcm16_bytes: bytes) -> str: ...

    async def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> LocalChatResult: ...

    async def speak(self, text: str, voice: str) -> bytes: ...


def _resample_pcm16(pcm_bytes: bytes, src_rate: int, dst_rate: int) -> bytes:
    """Linear-interpolation PCM16 mono resampler with no numpy/scipy dependency, matching this
    pipeline's "no new heavy deps in the main backend requirements.txt" goal (this module's own
    docstring, point 1). Ported in spirit from the sibling repo's own resampler
    (`piper_tts.py::_resample_pcm`, which uses numpy) -- functionally equivalent, just built on
    the stdlib `array` module `cascade_processor.py`'s `_TurnDetector` already uses. Adequate
    quality for speech-length turns; not intended for anything requiring studio-grade resampling.
    """
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


def _parse_chat_response(payload: Any, url: str) -> LocalChatResult:
    if not isinstance(payload, dict):
        raise LocalRuntimeError(f"Local runtime chat response from {url} must be a JSON object, got {type(payload).__name__}")
    content = payload.get("content")
    if content is not None and not isinstance(content, str):
        raise LocalRuntimeError(f"Local runtime chat response from {url}'s 'content' must be a string or null: {content!r}")
    raw_tool_calls = payload.get("tool_calls") or []
    if not isinstance(raw_tool_calls, list):
        raise LocalRuntimeError(f"Local runtime chat response from {url}'s 'tool_calls' must be a list: {raw_tool_calls!r}")
    tool_calls: list[LocalToolCall] = []
    for index, raw in enumerate(raw_tool_calls):
        if not isinstance(raw, dict):
            raise LocalRuntimeError(
                f"Local runtime chat response from {url}'s tool_calls[{index}] must be an object, got {type(raw).__name__}"
            )
        call_id = raw.get("id")
        name = raw.get("name")
        arguments = raw.get("arguments", "{}")
        if not isinstance(call_id, str) or not call_id:
            raise LocalRuntimeError(f"Local runtime chat response from {url}'s tool_calls[{index}] is missing a string 'id'")
        if not isinstance(name, str) or not name:
            raise LocalRuntimeError(f"Local runtime chat response from {url}'s tool_calls[{index}] is missing a string 'name'")
        if isinstance(arguments, dict | list):
            arguments = json.dumps(arguments)
        if not isinstance(arguments, str):
            raise LocalRuntimeError(
                f"Local runtime chat response from {url}'s tool_calls[{index}]'s 'arguments' must be a string or JSON object: {arguments!r}"
            )
        tool_calls.append(LocalToolCall(id=call_id, name=name, arguments=arguments))
    return LocalChatResult(content=content, tool_calls=tool_calls)


class HttpLocalRuntimeClient:
    """The real `LocalRuntimeClient`: plain HTTP/JSON calls to the companion runtime process at
    *base_url* (`ResolvedModel.deployment` -- see `processors.resolve_local_model`, which stores
    the local runtime's base URL there instead of a Foundry deployment name)."""

    def __init__(self, base_url: str, *, timeout_seconds: float = 30.0, mic_sample_rate: int = 24000):
        self._base_url = base_url.rstrip("/")
        self._timeout = aiohttp.ClientTimeout(total=timeout_seconds)
        self._mic_sample_rate = mic_sample_rate

    async def transcribe(self, pcm16_bytes: bytes) -> str:
        url = f"{self._base_url}/v1/transcribe"
        resampled = _resample_pcm16(pcm16_bytes, self._mic_sample_rate, _STT_SAMPLE_RATE)
        try:
            async with aiohttp.ClientSession(timeout=self._timeout) as http:
                async with http.post(url, data=resampled, headers={"Content-Type": "application/octet-stream"}) as resp:
                    resp.raise_for_status()
                    payload = await resp.json()
        except (aiohttp.ClientError, TimeoutError) as exc:
            raise LocalRuntimeError(f"Local runtime transcription request to {url} failed: {exc}") from exc
        text = payload.get("text") if isinstance(payload, dict) else None
        if not isinstance(text, str):
            raise LocalRuntimeError(f"Local runtime transcription response from {url} is missing a string 'text' field: {payload!r}")
        return text

    async def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> LocalChatResult:
        url = f"{self._base_url}/v1/chat"
        body = {"messages": messages, "tools": tools}
        try:
            async with aiohttp.ClientSession(timeout=self._timeout) as http:
                async with http.post(url, json=body) as resp:
                    resp.raise_for_status()
                    payload = await resp.json()
        except (aiohttp.ClientError, TimeoutError) as exc:
            raise LocalRuntimeError(f"Local runtime chat request to {url} failed: {exc}") from exc
        return _parse_chat_response(payload, url)

    async def speak(self, text: str, voice: str) -> bytes:
        url = f"{self._base_url}/v1/speak"
        body = {"text": text, "voice": voice}
        try:
            async with aiohttp.ClientSession(timeout=self._timeout) as http:
                async with http.post(url, json=body) as resp:
                    resp.raise_for_status()
                    return await resp.read()
        except (aiohttp.ClientError, TimeoutError) as exc:
            raise LocalRuntimeError(f"Local runtime speech synthesis request to {url} failed: {exc}") from exc
