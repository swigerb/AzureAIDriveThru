"""The companion local-runtime process (issue #81 part 2, split from #136/part 1's client).

This package IS "the companion local-runtime" that `app/backend/local_runtime.py`'s
`HttpLocalRuntimeClient` talks to over HTTP (`docs/persona-architecture.md` section 7.6). It is a
separate, standalone process -- never imported by the main backend -- so the backend's own
`requirements.txt` stays free of the heavy, optional ML dependencies (ONNX Runtime GenAI,
faster-whisper, Piper) declared in `local_runtime/requirements.txt` instead (issue #81's own
"optional dependencies... requirements extra" acceptance line).

Ported from the McDonald's sibling project's own local mode (`phi4_model.py`, `whisper_stt.py`,
`piper_tts.py`), reshaped to serve the exact three-endpoint contract part 1 already documented and
shipped a client for, instead of the sibling's in-process wiring:

    POST /v1/transcribe  raw 16kHz mono PCM16              -> {"text": "..."}
    POST /v1/chat        {"messages": [...], "tools": [...]} -> {"content": ..., "tool_calls": [...]}
    POST /v1/speak       {"text": "...", "voice": "..."}    -> raw 24kHz mono PCM16

Run it with `python -m local_runtime` (see `local_runtime/__main__.py`), or via
`docker-compose.local.yml` alongside the main backend. See the repo README's "On-device local
mode" section for end-to-end setup.
"""

from __future__ import annotations

__all__: list[str] = []
