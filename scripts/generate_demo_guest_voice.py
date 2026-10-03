#!/usr/bin/env python3
"""Generate scripted guest voice MP3 clips for persona demo packs."""
from __future__ import annotations

import argparse
import array
import asyncio
import html
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any

DEFAULT_VOICE = "en-US-AvaMultilingualNeural"
DEFAULT_RATE = "-1%"
SILENCE_THRESHOLD_DBFS = -40.0
MAX_INTERNAL_SILENCE_MS = 120
EDGE_SILENCE_MS = 50
CROSSFADE_MS = 8


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
        scripts.append(
            (pack_dir.name, script_path, json.loads(script_path.read_text(encoding="utf-8-sig")))
        )
    return scripts


async def synthesize_edge(text: str, voice: str, rate: str, out_path: Path) -> None:
    try:
        import edge_tts
    except ImportError as exc:
        raise RuntimeError("edge-tts is required for --engine edge; install it or use --engine azure") from exc
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


def _run_ffmpeg(command: list[str], *, input_bytes: bytes | None = None) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(command, input=input_bytes, capture_output=True, check=True)


def _probe_audio(path: Path) -> tuple[int, int, int | None]:
    result = _run_ffmpeg(
        [
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=sample_rate,channels,bit_rate:format=bit_rate",
            "-of",
            "json",
            str(path),
        ]
    )
    metadata = json.loads(result.stdout.decode("utf-8"))
    stream = metadata["streams"][0]
    sample_rate = int(stream["sample_rate"])
    channels = int(stream.get("channels") or 1)
    bit_rate_value = stream.get("bit_rate") or metadata.get("format", {}).get("bit_rate")
    bit_rate = int(bit_rate_value) if bit_rate_value else None
    return sample_rate, channels, bit_rate


def _decode_pcm(path: Path, sample_rate: int, channels: int) -> array.array:
    result = _run_ffmpeg(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(path),
            "-f",
            "s16le",
            "-acodec",
            "pcm_s16le",
            "-ar",
            str(sample_rate),
            "-ac",
            str(channels),
            "pipe:1",
        ]
    )
    samples = array.array("h")
    samples.frombytes(result.stdout)
    if samples.itemsize != 2:
        raise RuntimeError("Unexpected PCM sample width from ffmpeg")
    if sys.byteorder != "little":
        samples.byteswap()
    return samples


def _silence_flags(samples: array.array, channels: int) -> tuple[list[bool], int | None, int | None]:
    threshold = int(round((10 ** (SILENCE_THRESHOLD_DBFS / 20.0)) * 32768))
    frame_count = len(samples) // channels
    flags: list[bool] = []
    first_speech: int | None = None
    last_speech: int | None = None
    for frame in range(frame_count):
        offset = frame * channels
        peak = max(abs(samples[offset + channel]) for channel in range(channels))
        is_silent = peak < threshold
        flags.append(is_silent)
        if not is_silent:
            if first_speech is None:
                first_speech = frame
            last_speech = frame
    return flags, first_speech, last_speech


def _append_frames(
    output: array.array,
    samples: array.array,
    channels: int,
    start_frame: int,
    end_frame: int,
    *,
    crossfade_frames: int = 0,
) -> None:
    if end_frame <= start_frame:
        return
    overlap = min(crossfade_frames, len(output) // channels, end_frame - start_frame)
    if overlap > 0:
        output_start = len(output) - (overlap * channels)
        for frame in range(overlap):
            ratio = (frame + 1) / (overlap + 1)
            source_offset = (start_frame + frame) * channels
            output_offset = output_start + (frame * channels)
            for channel in range(channels):
                mixed = (output[output_offset + channel] * (1.0 - ratio)) + (
                    samples[source_offset + channel] * ratio
                )
                output[output_offset + channel] = max(-32768, min(32767, int(round(mixed))))
        start_frame += overlap
    output.extend(samples[start_frame * channels : end_frame * channels])


def _max_silence_seconds(flags: list[bool], sample_rate: int, start_frame: int, end_frame: int) -> float:
    max_run = 0
    current_run = 0
    for frame in range(start_frame, end_frame):
        if flags[frame]:
            current_run += 1
            max_run = max(max_run, current_run)
        else:
            current_run = 0
    return max_run / sample_rate


def _bitrate_arg(bit_rate: int | None) -> str | None:
    if bit_rate is None:
        return None
    if bit_rate % 1000 == 0:
        return f"{bit_rate // 1000}k"
    return str(bit_rate)


def cap_internal_silences(path: Path) -> dict[str, float | int]:
    sample_rate, channels, bit_rate = _probe_audio(path)
    samples = _decode_pcm(path, sample_rate, channels)
    flags, first_speech, last_speech = _silence_flags(samples, channels)
    if first_speech is None or last_speech is None:
        return {"shortened": 0, "max_before": 0.0, "max_after": 0.0}

    edge_frames = round(sample_rate * EDGE_SILENCE_MS / 1000)
    max_silence_frames = round(sample_rate * MAX_INTERNAL_SILENCE_MS / 1000)
    crossfade_frames = round(sample_rate * CROSSFADE_MS / 1000)
    start_frame = max(0, first_speech - edge_frames)
    end_frame = min(len(flags), last_speech + 1 + edge_frames)
    max_before = _max_silence_seconds(flags, sample_rate, start_frame, end_frame)

    output = array.array("h")
    position = start_frame
    shortened = 0
    frame = start_frame
    while frame < end_frame:
        if not flags[frame]:
            frame += 1
            continue
        run_start = frame
        while frame < end_frame and flags[frame]:
            frame += 1
        run_end = frame
        run_length = run_end - run_start
        is_internal = run_start > start_frame and run_end < end_frame
        if is_internal and run_length > max_silence_frames:
            left_keep = max_silence_frames // 2
            right_keep = max_silence_frames - left_keep
            _append_frames(output, samples, channels, position, run_start + left_keep)
            _append_frames(
                output,
                samples,
                channels,
                run_end - right_keep,
                run_end,
                crossfade_frames=crossfade_frames,
            )
            position = run_end
            shortened += 1
    _append_frames(output, samples, channels, position, end_frame)

    processed_flags, processed_first, processed_last = _silence_flags(output, channels)
    if processed_first is None or processed_last is None:
        max_after = 0.0
    else:
        max_after = _max_silence_seconds(processed_flags, sample_rate, processed_first, processed_last + 1)

    tmp_path = path.with_name(f".{path.stem}.vadcap{path.suffix}")
    encode_command = [
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "s16le",
        "-ar",
        str(sample_rate),
        "-ac",
        str(channels),
        "-i",
        "pipe:0",
        "-ar",
        str(sample_rate),
        "-ac",
        str(channels),
        "-codec:a",
        "libmp3lame",
    ]
    bit_rate_arg = _bitrate_arg(bit_rate)
    if bit_rate_arg:
        encode_command.extend(["-b:a", bit_rate_arg])
    encode_command.append(str(tmp_path))
    output_bytes = output.tobytes()
    if sys.byteorder != "little":
        little_endian_output = array.array("h", output)
        little_endian_output.byteswap()
        output_bytes = little_endian_output.tobytes()
    try:
        _run_ffmpeg(encode_command, input_bytes=output_bytes)
        tmp_path.replace(path)
    finally:
        if tmp_path.exists():
            tmp_path.unlink()

    return {"shortened": shortened, "max_before": max_before, "max_after": max_after}


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
            stats = cap_internal_silences(out_path)
            print(
                f"wrote {out_path.relative_to(repo_root())} "
                f"(internal silence {stats['max_before']:.3f}s -> {stats['max_after']:.3f}s; "
                f"shortened {stats['shortened']})"
            )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--persona",
        action="append",
        help="Persona id to generate; repeat to select multiple. Defaults to every pack with a script.",
    )
    parser.add_argument(
        "--engine",
        choices=["edge", "azure"],
        default=os.environ.get("DEMO_GUEST_VOICE_ENGINE", "edge"),
    )
    parser.add_argument("--voice", default=None, help=f"Voice name (default: script voice or {DEFAULT_VOICE})")
    parser.add_argument("--rate", default=DEFAULT_RATE, help=f"edge-tts speaking rate (default {DEFAULT_RATE})")
    parser.add_argument("--dry-run", action="store_true")
    return asyncio.run(generate(parser.parse_args()))


if __name__ == "__main__":
    raise SystemExit(main())
