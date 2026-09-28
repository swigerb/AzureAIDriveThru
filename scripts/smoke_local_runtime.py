#!/usr/bin/env python3
"""One scripted local-runtime session against a running companion process (issue #81 part 2).

`scripts/smoke_realtime.py` (part of azd's postdeploy hook) only applies to the realtime/cascade
pipelines -- it opens a live Azure OpenAI Realtime WebSocket, which the local pipeline has no
equivalent of at all (LocalProcessor talks to the companion over the plain HTTP contract this
script itself exercises). This script is the local-pipeline substitute the README's "On-device
local mode" section points to for a manual end-to-end check: it drives the companion's three
endpoints directly (`/v1/transcribe`, `/v1/chat`, `/v1/speak`) using a persona's own prompt/tool
config, WITHOUT needing a live microphone:

  1. /v1/speak     a scripted guest line -> PCM16 audio (proves TTS + the voice catalog work)
  2. /v1/transcribe   that same PCM16 fed back in -> text (proves STT works, TTS->STT round trip
                      substitutes for a live mic since Piper's own voice is intelligible to Whisper)
  3. /v1/chat      the persona's local_system_prompt (or system_prompt) + the transcribed guest
                    line + that persona's real tool schemas -> assistant reply / tool_calls
  4. /v1/speak     the assistant's reply text -> PCM16 audio, saved to a .wav file for a human to
                    actually listen to

Requires app/backend on sys.path to reuse PromptLoader/persona_loader exactly as the real backend
does (same code, not a re-implementation) -- added explicitly below rather than relying on the
ambient path tricks other repo scripts use, since this script can be run from any directory.

Usage:
  python scripts/smoke_local_runtime.py --persona <persona-id> --model phi-4-mini-local
  python scripts/smoke_local_runtime.py --persona <persona-id> --endpoint http://localhost:8100 \
      --text "a medium drink with no ice, please"

`--persona` accepts any pack directory name under `personas/` (discovered at runtime, not
hardcoded here, so this script needs no brand-specific edits as packs are added or renamed).
"""

from __future__ import annotations

import argparse
import sys
import time
import urllib.error
import urllib.request
import wave
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[1]
_APP_BACKEND = _REPO_ROOT / "app" / "backend"
if str(_APP_BACKEND) not in sys.path:
    sys.path.insert(0, str(_APP_BACKEND))

# Generic guest line, brand-agnostic on purpose: this script is shared code, so it must not
# hardcode any one persona's menu wording (see personas/<id>/prompts/local_system_prompt.yaml for
# real brand-specific menu items). It's simple enough to exercise a search + update_order round
# trip against any persona's real menu ("drink"/"medium" are near-universal quick-service terms).
DEFAULT_GUEST_LINE = "I'll have a medium drink, please."


def _discover_personas(repo_root: Path) -> list[str]:
    """Persona ids are directory names -- discovered at runtime so this script's --persona
    choices never need brand-specific edits (see module docstring)."""
    personas_dir = repo_root / "personas"
    if not personas_dir.is_dir():
        return []
    return sorted(p.name for p in personas_dir.iterdir() if p.is_dir() and (p / "persona.json").exists())

_SAMPLE_RATE_STT = 16000
_SAMPLE_RATE_TTS = 24000


def _post_json(base_url: str, path: str, payload: dict, timeout: float) -> dict:
    import json

    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}{path}", data=body, method="POST", headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _speak(base_url: str, text: str, voice: str, timeout: float) -> bytes:
    """POST /v1/speak's JSON `{"text", "voice"}` request and return the raw PCM16 response body."""
    body = _json_bytes({"text": text, "voice": voice})
    req = urllib.request.Request(
        f"{base_url}/v1/speak", data=body, method="POST", headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def _write_wav(path: Path, pcm16_bytes: bytes, sample_rate: int) -> None:
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(pcm16_bytes)


def run_session(*, endpoint: str, persona: str, model: str, text: str, timeout: float, out_dir: Path) -> int:
    from persona_loader import resolve_personas_dir
    from prompt_loader import PromptLoader

    personas_dir = resolve_personas_dir(Path(__file__))
    pack_dir = personas_dir / persona
    if not pack_dir.is_dir():
        print(f"ERROR: no persona pack at {pack_dir}", file=sys.stderr)
        return 2

    loader = PromptLoader(brand=persona, prompts_dir=pack_dir / "prompts")
    system_prompt = loader.get_local_system_prompt()
    tools = loader.get_tool_schemas()
    print(f"[{persona}] loaded local_system_prompt ({len(system_prompt)} chars), {len(tools)} tool(s)")

    out_dir.mkdir(parents=True, exist_ok=True)

    # 1. Speak the scripted guest line so we have real, intelligible Piper audio to transcribe --
    # a stand-in for a live microphone recording.
    print(f"[{persona}] /v1/speak (guest line): {text!r}")
    guest_pcm = _speak(endpoint, text, "", timeout)
    if not guest_pcm:
        print(f"[{persona}] FAIL: /v1/speak returned no audio for the guest line", file=sys.stderr)
        return 1
    _write_wav(out_dir / f"{persona}_guest_line.wav", guest_pcm, _SAMPLE_RATE_TTS)
    print(f"[{persona}]   -> {len(guest_pcm)} bytes @ {_SAMPLE_RATE_TTS}Hz -> {out_dir / f'{persona}_guest_line.wav'}")

    # /v1/transcribe expects 16kHz; the companion's own resampler runs client-side in production
    # (LocalProcessor), so this script does its own tiny resample here rather than sending TTS's
    # 24kHz output straight to STT.
    guest_pcm_16k = _resample(guest_pcm, _SAMPLE_RATE_TTS, _SAMPLE_RATE_STT)
    print(f"[{persona}] /v1/transcribe (TTS output fed back in)...")
    transcribe_result = _post_json_binary(endpoint, "/v1/transcribe", guest_pcm_16k, timeout)
    transcribed_text = transcribe_result.get("text", "")
    print(f"[{persona}]   -> {transcribed_text!r}")
    if not transcribed_text.strip():
        print(f"[{persona}] WARNING: transcription came back empty -- STT engine may not be loaded/working")

    # 2. Chat: persona's own local system prompt + the (transcribed) guest line + real tool schemas.
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": transcribed_text or text},
    ]
    print(f"[{persona}] /v1/chat (model={model})...")
    chat_result = _post_json(endpoint, "/v1/chat", {"messages": messages, "tools": tools}, timeout)
    content = chat_result.get("content")
    tool_calls = chat_result.get("tool_calls", [])
    print(f"[{persona}]   content={content!r}")
    for call in tool_calls:
        print(f"[{persona}]   tool_call: {call.get('name')}({call.get('arguments')})")

    reply_text = content or (f"Calling {tool_calls[0]['name']}..." if tool_calls else "(no reply)")

    # 3. Speak the assistant's reply for a human to actually listen to.
    print(f"[{persona}] /v1/speak (assistant reply): {reply_text!r}")
    reply_pcm = _speak(endpoint, reply_text, "", timeout)
    _write_wav(out_dir / f"{persona}_assistant_reply.wav", reply_pcm, _SAMPLE_RATE_TTS)
    print(f"[{persona}]   -> {len(reply_pcm)} bytes -> {out_dir / f'{persona}_assistant_reply.wav'}")

    print(f"[{persona}] OK")
    return 0


def _json_bytes(payload: dict) -> bytes:
    import json

    return json.dumps(payload).encode("utf-8")


def _post_json_binary(base_url: str, path: str, pcm16_bytes: bytes, timeout: float) -> dict:
    import json

    req = urllib.request.Request(
        f"{base_url}{path}", data=pcm16_bytes, method="POST", headers={"Content-Type": "application/octet-stream"}
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _resample(pcm_bytes: bytes, src_rate: int, dst_rate: int) -> bytes:
    """Reuses the exact same dependency-free linear-interpolation resampler the companion's own
    TTS engine uses (local_runtime/engines/tts.py::_resample_pcm16), imported directly rather than
    re-implemented here, so this script's own resampling can never silently drift from the
    production code path it's meant to be smoke-testing."""
    sys.path.insert(0, str(_REPO_ROOT))
    from local_runtime.engines.tts import _resample_pcm16

    return _resample_pcm16(pcm_bytes, src_rate, dst_rate)


def wait_for_health(endpoint: str, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(f"{endpoint}/health", timeout=5) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    available_personas = _discover_personas(_REPO_ROOT)
    parser.add_argument(
        "--persona", required=True, choices=available_personas or None,
        help=f"Persona pack id (discovered under personas/): {available_personas or 'none found'}",
    )
    parser.add_argument("--model", default="phi-4-mini-local", help="Label only -- the companion serves whatever it loaded")
    parser.add_argument("--endpoint", default="http://localhost:8100")
    parser.add_argument("--text", default=None, help="Guest line to speak/transcribe (default: a generic sample line)")
    parser.add_argument("--timeout", type=float, default=120.0, help="Per-request timeout in seconds (model load can be slow on CPU)")
    parser.add_argument("--out-dir", type=Path, default=_REPO_ROOT / "scripts" / "_smoke_local_runtime_out")
    parser.add_argument("--wait-healthy", type=float, default=0.0, help="Seconds to wait for GET /health before starting")
    args = parser.parse_args()

    if args.wait_healthy > 0 and not wait_for_health(args.endpoint, args.wait_healthy):
        print(f"ERROR: {args.endpoint}/health did not return 200 within {args.wait_healthy}s", file=sys.stderr)
        return 1

    text = args.text or DEFAULT_GUEST_LINE
    try:
        return run_session(
            endpoint=args.endpoint, persona=args.persona, model=args.model, text=text,
            timeout=args.timeout, out_dir=args.out_dir,
        )
    except urllib.error.URLError as exc:
        print(f"ERROR: could not reach the companion at {args.endpoint}: {exc}", file=sys.stderr)
        print("Is it running?  python -m local_runtime", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
