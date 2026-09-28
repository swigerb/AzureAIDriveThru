"""Phi-4-mini chat engine (ONNX Runtime GenAI), ported from a sibling drive-thru project's own
on-device reference implementation onto this repo's `ChatEngine` protocol, extended for the
multi-round tool-calling loop `app/backend/local_processor.py::_run_chat_tool_loop` actually drives.

Phi-4-mini has no native OpenAI-style function-calling output -- like that sibling reference, tool
definitions are embedded as a JSON block in the prompt with instructions to emit
`<tool_call>{"name": ..., "arguments": {...}}</tool_call>` tags, parsed back out of the generated
text with a regex (`_TOOL_CALL_RE`, the same tag-parsing pattern that reference uses). Unlike that
reference (a single generation per guest turn, tool results spoken directly), this engine also
renders prior `assistant` tool_calls and `tool` result messages back into the prompt so a SECOND
`/v1/chat` call (the multi-round loop `local_processor.py` already implements) can see what each
tool returned and produce a real final answer instead of just echoing the raw tool result.

`onnxruntime-genai` is an optional dependency (`local_runtime/requirements.txt`, NOT
`app/backend/requirements.txt`) -- like `engines/stt.py`, the import is deferred to
`ensure_loaded()` so this module (and `local_runtime.server`) stay importable without it, letting
`local_runtime/tests/` inject a stub `ChatEngine` instead.
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import uuid
from typing import Any

from local_runtime.config import ChatConfig
from local_runtime.engines import ChatResult, ChatToolCall, EngineNotReadyError

logger = logging.getLogger(__name__)

# The sibling project's own `Phi4ModelManager.parse_tool_calls` pattern -- a JSON object wrapped
# in `<tool_call>...</tool_call>` tags, possibly with surrounding prose or multi-line JSON.
_TOOL_CALL_RE = re.compile(r"<tool_call>\s*(\{.*?\})\s*</tool_call>", re.DOTALL)

# Fallback spoken when generation exceeds `inference_timeout_seconds` -- ported from the
# sibling's own `phi4_model.py::process_audio` timeout branch: the guest is never left in
# silence, even though there's no Azure quota to retry against.
_TIMEOUT_FALLBACK = "I'm sorry, could you repeat that?"


class Phi4ChatEngine:
    def __init__(self, config: ChatConfig):
        self._config = config
        self._model: Any = None
        self._tokenizer: Any = None
        self._tokenizer_stream: Any = None
        self._og: Any = None
        self._device_name = "none"
        self._lock = asyncio.Lock()

    async def ensure_loaded(self) -> None:
        if self._model is not None:
            return
        async with self._lock:
            if self._model is not None:
                return
            await asyncio.to_thread(self._load_model)

    def _load_model(self) -> None:
        try:
            og = __import__("onnxruntime_genai")
        except ImportError as exc:
            raise EngineNotReadyError(
                "onnxruntime-genai isn't installed. Run "
                "`pip install -r local_runtime/requirements.txt` (see README 'On-device local mode')."
            ) from exc

        model_path = self._config.model_path
        if not model_path.is_dir() or not (model_path / "genai_config.json").exists():
            raise EngineNotReadyError(
                f"Phi-4-mini ONNX model not found at '{model_path}'. Run "
                "`scripts/download_local_models.py` first to fetch it."
            )

        device = self._config.device
        if device == "auto":
            device = _detect_provider()
        logger.info("Loading Phi-4-mini model from %s (device=%s)", model_path, device)
        self._og = og
        self._model = og.Model(str(model_path))
        self._tokenizer = og.Tokenizer(self._model)
        self._tokenizer_stream = self._tokenizer.create_stream()
        self._device_name = device
        logger.info("Phi-4-mini model loaded (device=%s)", device)

    async def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> ChatResult:
        await self.ensure_loaded()
        prompt = _build_prompt(messages, tools)
        try:
            text = await asyncio.wait_for(
                asyncio.to_thread(self._generate_sync, prompt),
                timeout=self._config.inference_timeout_seconds,
            )
        except TimeoutError:
            logger.warning(
                "Phi-4-mini inference timed out after %.0fs (prompt=%d chars)",
                self._config.inference_timeout_seconds, len(prompt),
            )
            return ChatResult(content=_TIMEOUT_FALLBACK, tool_calls=[])
        return _parse_response(text, max_tool_calls=self._config.max_tool_rounds)

    def _generate_sync(self, prompt: str) -> str:
        og = self._og
        params = og.GeneratorParams(self._model)
        search_options: dict[str, Any] = {"max_length": self._config.max_length}
        if self._config.temperature > 0:
            search_options.update(temperature=self._config.temperature, do_sample=True)
        params.set_search_options(**search_options)

        generator = og.Generator(self._model, params)
        tokens = self._tokenizer.encode(prompt)
        generator.append_tokens(tokens)
        stream = self._tokenizer_stream
        parts: list[str] = []
        while not generator.is_done():
            generator.generate_next_token()
            new_token = generator.get_next_tokens()
            token_text = stream.decode(new_token[0])
            if token_text:
                parts.append(token_text)
        return "".join(parts)


def _detect_provider() -> str:
    """CPU/GPU auto-detection, ported from the sibling's own `phi4_model.py::_load_onnxruntime_genai`
    -- onnxruntime-genai's CPU/CUDA/DirectML variants all import as the same module, so the actual
    provider is detected from onnxruntime's own execution-provider list instead."""
    try:
        import onnxruntime as ort

        providers = ort.get_available_providers()
        if "CUDAExecutionProvider" in providers:
            return "cuda"
        if "DmlExecutionProvider" in providers:
            return "directml"
    except Exception:  # noqa: BLE001 - onnxruntime not importable, or no provider info
        pass
    return "cpu"


def _render_message(message: dict[str, Any]) -> str:
    role = message.get("role")
    content = message.get("content") or ""
    if role == "assistant" and message.get("tool_calls"):
        calls_text = "".join(
            f'<tool_call>{{"name": {json.dumps(call.get("name", ""))}, '
            f'"arguments": {call.get("arguments") or "{}"}}}</tool_call>'
            for call in message["tool_calls"]
        )
        body = f"{content}{calls_text}" if content else calls_text
        return f"<|assistant|>\n{body}\n<|end|>"
    if role == "tool":
        # Phi-4-mini's own chat template has no native tool-result role -- this tag is this
        # engine's own convention (documented here, not a model-defined special token), read
        # back by the SAME engine on the next /v1/chat round so a tool result can inform a
        # real final answer instead of the sibling's "speak the raw tool result" shortcut.
        payload = json.dumps({"tool_call_id": message.get("tool_call_id"), "content": content})
        return f"<|tool|>\n{payload}\n<|end|>"
    tag = "user" if role == "user" else "assistant" if role == "assistant" else "system"
    return f"<|{tag}|>\n{content}\n<|end|>"


def _build_prompt(messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> str:
    """Ported from the sibling's own `Phi4ModelManager._build_prompt`, generalized from a single
    `(system_prompt, user_message, conversation_history)` triple to the full flat OpenAI-chat-style
    `messages` list `/v1/chat`'s wire contract sends (system/user/assistant/tool roles, assistant
    `tool_calls`, tool `tool_call_id`) -- see this module's own docstring."""
    parts: list[str] = []
    system_parts = [m.get("content") or "" for m in messages if m.get("role") == "system"]
    system_content = "\n\n".join(p for p in system_parts if p)
    if tools:
        system_content += (
            "\n\nYou have access to the following tools. To call a tool, output a JSON block "
            'wrapped in <tool_call> tags: <tool_call>{"name": "tool_name", "arguments": '
            "{...}}</tool_call>. You may emit more than one <tool_call> block in a single reply. "
            "A <|tool|> message afterwards carries that call's result -- read it before your next "
            "reply and do not call the same tool again for the same purpose without a reason.\n\n"
            "Available tools:\n" + json.dumps(tools, indent=2)
        )
    if system_content:
        parts.append(f"<|system|>\n{system_content}\n<|end|>")

    for message in messages:
        if message.get("role") == "system":
            continue
        parts.append(_render_message(message))

    parts.append("<|assistant|>")
    return "\n".join(parts)


def _parse_response(text: str, *, max_tool_calls: int) -> ChatResult:
    matches = _TOOL_CALL_RE.findall(text)
    tool_calls: list[ChatToolCall] = []
    for match in matches[:max_tool_calls]:
        try:
            parsed = json.loads(match)
        except json.JSONDecodeError:
            logger.warning("Failed to parse tool call JSON from Phi-4-mini output: %s", match[:200])
            continue
        name = parsed.get("name")
        if not isinstance(name, str) or not name:
            continue
        arguments = parsed.get("arguments", {})
        if not isinstance(arguments, str):
            arguments = json.dumps(arguments)
        tool_calls.append(ChatToolCall(id=f"call_{uuid.uuid4().hex[:24]}", name=name, arguments=arguments))

    if tool_calls:
        return ChatResult(content=None, tool_calls=tool_calls)

    # No tool calls: strip any stray/malformed <tool_call> tags before speaking the answer, same
    # as the sibling's own local_processor.py speech-text cleanup.
    spoken = _TOOL_CALL_RE.sub("", text).strip()
    return ChatResult(content=spoken, tool_calls=[])
