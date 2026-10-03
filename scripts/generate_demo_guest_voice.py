#!/usr/bin/env python3
"""Generate scripted guest voice MP3 clips for persona demo packs."""
from __future__ import annotations

import argparse
import asyncio
import html
import json
import os
from pathlib import Path
from typing import Any

import edge_tts

DEFAULT_VOICE = "en-US-AvaMultilingualNeural"
DEFAULT_RATE = "-1%"


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def load_scripts(personas_dir: Path, persona_filter: set[str] | None) -> list[tuple[str, Path, dict[str, Any]]]:
    scripts: list[tuple[str, Path, dict[str, Any]]] = []
    for pack_dir in sorted(path for path in personas_dir.iterdir() if path.is_dir()):
        if persona_filter is not None and pack_dir.name not in persona_filter:
            continue
        script_path = pack_dir / "assets" / "demo" / "guestScript.json"
        if not script_path.is_file():
            continue
        scripts.append((pack_dir.name, script_path, json.loads(script_path.read_text(encoding="utf-8-sig"))))
    return scripts


async def synthesize_edge(text: str, voice: str, rate: str, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    communicate = edge_tts.Communicate(text=text, voice=voice, rate=rate)
    await communicate.save(str(out_path))


async def synthesize_azure(text: str, voice: str, out_path: Path) -> None:
    import aiohttp

    key = os.environ.get("AZURE_SPEECH_KEY")
    region = os.environ.get("AZURE_SPEECH_REGION")
    if not key or not region:
        raise RuntimeError("AZURE_SPEECH_KEY and AZURE_SPEECH_REGION are required for --engine azure")
    ssml = (
        "<speak version='1.0' xml:lang='en-US'>"
        f"<voice name='{html.escape(voice)}'>{html.escape(text)}</voice>"
        "</speak>"
    )
    url = f"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1"
    headers = {
        "Ocp-Apim-Subscription-Key": key,
        "Content-Type": "application/ssml+xml",
        "X-Microsoft-OutputFormat": "audio-24khz-48kbitrate-mono-mp3",
        "User-Agent": "demo-guest-voice-generator",
    }
    async with aiohttp.ClientSession() as session:
        async with session.post(url, data=ssml.encode("utf-8"), headers=headers) as response:
            if response.status >= 400:
                raise RuntimeError(f"Azure Speech synthesis failed ({response.status}): {await response.text()}")
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_bytes(await response.read())


async def generate(args: argparse.Namespace) -> int:
    personas_dir = repo_root() / "personas"
    persona_filter = set(args.persona) if args.persona else None
    scripts = load_scripts(personas_dir, persona_filter)
    if not scripts:
        print("No guestScript.json files found.")
        return 1

    for persona_id, script_path, script in scripts:
        voice = args.voice or script.get("voice") or DEFAULT_VOICE
        for line in script.get("lines", []):
            rel_audio = str(line["audio"]).replace("/", os.sep)
            out_path = script_path.parent.parent / rel_audio
            text = str(line["text"])
            if args.dry_run:
                print(f"{persona_id} {line['id']}: {out_path.relative_to(repo_root())} <- {text!r}")
                continue
            if args.engine == "azure":
                await synthesize_azure(text, voice, out_path)
            else:
                await synthesize_edge(text, voice, args.rate, out_path)
            print(f"wrote {out_path.relative_to(repo_root())}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--persona", action="append", help="Persona id to generate; repeat to select multiple. Defaults to every pack with a script.")
    parser.add_argument("--engine", choices=["edge", "azure"], default=os.environ.get("DEMO_GUEST_VOICE_ENGINE", "edge"))
    parser.add_argument("--voice", default=None, help=f"Voice name (default: script voice or {DEFAULT_VOICE})")
    parser.add_argument("--rate", default=DEFAULT_RATE, help=f"edge-tts speaking rate (default {DEFAULT_RATE})")
    parser.add_argument("--dry-run", action="store_true")
    return asyncio.run(generate(parser.parse_args()))


if __name__ == "__main__":
    raise SystemExit(main())
