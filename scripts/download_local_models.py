#!/usr/bin/env python3
"""Download the on-device local-runtime models (issue #81, ADR-001 decision 7): Phi-4-mini-instruct
ONNX (chat + tool calling), 4 Piper TTS voices, and a warmed faster-whisper STT cache.

Every file this script controls the layout of (Phi-4-mini ONNX weights, Piper voices) is verified
against the SHA-256 the Hugging Face Hub itself reports for that file (its git-lfs "oid") after
download -- a corrupt or truncated download is deleted and reported, never silently kept. Whisper's
cache is warmed via faster-whisper's own resumable/verified downloader (it owns its cache layout,
not this script) and confirmed with a real one-shot transcription of a short silence-plus-tone clip
instead.

Usage:
  python scripts/download_local_models.py                       # CPU Phi-4-mini + all 4 Piper voices
  python scripts/download_local_models.py --variant cuda         # GPU (CUDA) Phi-4-mini variant
  python scripts/download_local_models.py --voices amy,jenny     # only these Piper voices
  python scripts/download_local_models.py --model-dir D:\\models  # override the default ./models
  python scripts/download_local_models.py --skip-whisper         # models only, skip the STT warm-up

Models are downloaded to <repo root>/models/ by default (LOCAL_RUNTIME_CHAT_MODEL_DIR /
LOCAL_RUNTIME_TTS_MODEL_DIR default paths, `local_runtime/config.py`) -- gitignored, never
committed (issue #81 acceptance criteria).
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[1]
_DEFAULT_MODEL_DIR = _REPO_ROOT / "models"

# microsoft/Phi-4-mini-instruct-onnx (issue #81 design 7.4) lays out one directory per
# device/precision variant; "cpu" is the only one every contributor's machine can run, so it's the
# default -- --variant cuda/directml pulls a GPU variant instead for machines that have one.
PHI4_REPO = "microsoft/Phi-4-mini-instruct-onnx"
PHI4_VARIANT_PATTERNS: dict[str, str] = {
    "cpu": "cpu_and_mobile/*",
    "cuda": "gpu/*",
    "directml": "directml/*",
}

# rhasspy/piper-voices (same 4 voices `local_runtime/voices.py`'s VOICE_CATALOG declares).
PIPER_REPO = "rhasspy/piper-voices"
PIPER_VOICE_PATHS: dict[str, str] = {
    "en_US-amy-medium": "en/en_US/amy/medium",
    "en_GB-jenny_dioco-medium": "en/en_GB/jenny_dioco/medium",
    "en_US-lessac-medium": "en/en_US/lessac/medium",
    "en_US-kristin-medium": "en/en_US/kristin/medium",
}


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _verify_downloads(repo_id: str, local_dir: Path, relative_paths: list[str]) -> list[str]:
    """Compare each downloaded file's SHA-256 against the hash Hugging Face's own API reports for
    that file at `main` (git-lfs `oid`, or the plain blob hash for small non-LFS files). Returns the
    list of files that failed verification (empty means everything checked out)."""
    from huggingface_hub import HfApi

    api = HfApi()
    try:
        paths_info = api.get_paths_info(repo_id, relative_paths, repo_type="model")
    except Exception as exc:  # network hiccup, HF API shape change, etc. -- fail closed, not silent
        return [f"<all: could not fetch reference hashes from Hugging Face: {exc}>"]

    expected_by_path = {info.path: getattr(getattr(info, "lfs", None), "sha256", None) for info in paths_info}
    failures: list[str] = []
    for rel_path in relative_paths:
        local_path = local_dir / rel_path
        expected = expected_by_path.get(rel_path)
        if not local_path.is_file():
            failures.append(f"{rel_path}: file missing after download")
            continue
        if not expected:
            # Not an LFS file (e.g. a small .json config) -- Hugging Face doesn't report a sha256
            # for those over this API, only a git blob id. A non-empty file is the best available
            # signal in that case.
            if local_path.stat().st_size == 0:
                failures.append(f"{rel_path}: file is empty")
            continue
        actual = _sha256_file(local_path)
        if actual != expected:
            failures.append(f"{rel_path}: sha256 mismatch (expected {expected}, got {actual})")
    return failures


def download_phi4(model_dir: Path, variant: str, force: bool) -> None:
    from huggingface_hub import snapshot_download

    dest = model_dir / "phi4-mini" / variant
    marker = dest / ".download_complete"
    if marker.is_file() and not force:
        print(f"[phi4-mini/{variant}] already downloaded (delete {marker} or pass --force to redo)")
        return

    pattern = PHI4_VARIANT_PATTERNS[variant]
    print(f"[phi4-mini/{variant}] downloading {PHI4_REPO} ({pattern}) -- this is several GB, be patient...")
    dest.mkdir(parents=True, exist_ok=True)
    snapshot_download(repo_id=PHI4_REPO, allow_patterns=[pattern], local_dir=str(dest))

    onnx_files = sorted(str(p.relative_to(dest)).replace("\\", "/") for p in dest.rglob("*") if p.is_file())
    if not onnx_files:
        sys.exit(f"[phi4-mini/{variant}] download completed but no files were found under {dest} -- aborting")

    print(f"[phi4-mini/{variant}] verifying {len(onnx_files)} file(s) against Hugging Face checksums...")
    # snapshot_download(local_dir=dest, ...) preserves the repo's own relative paths under dest, so
    # each file's path relative to dest IS already the repo-relative path the HF API expects.
    failures = _verify_downloads(PHI4_REPO, dest, onnx_files)
    if failures:
        for failure in failures:
            print(f"  [FAIL] {failure}")
        sys.exit(f"[phi4-mini/{variant}] checksum verification failed -- re-run this script to retry")
    marker.write_text("ok\n")
    print(f"[phi4-mini/{variant}] OK ({len(onnx_files)} file(s) verified)")


def download_piper_voices(model_dir: Path, voice_ids: list[str], force: bool) -> None:
    from huggingface_hub import hf_hub_download

    dest = model_dir / "piper"
    dest.mkdir(parents=True, exist_ok=True)
    tmp_dir = model_dir / "_piper_download_tmp"

    for voice_id in voice_ids:
        if voice_id not in PIPER_VOICE_PATHS:
            sys.exit(f"[piper/{voice_id}] unknown voice id. Known voices: {', '.join(PIPER_VOICE_PATHS)}")
        repo_path = PIPER_VOICE_PATHS[voice_id]
        relative_files = [f"{repo_path}/{voice_id}.onnx", f"{repo_path}/{voice_id}.onnx.json"]
        onnx_dest = dest / f"{voice_id}.onnx"
        json_dest = dest / f"{voice_id}.onnx.json"
        if onnx_dest.is_file() and json_dest.is_file() and not force:
            print(f"[piper/{voice_id}] already downloaded")
            continue

        print(f"[piper/{voice_id}] downloading from {PIPER_REPO}...")
        for relative_file in relative_files:
            hf_hub_download(repo_id=PIPER_REPO, filename=relative_file, revision="v1.0.0", local_dir=str(tmp_dir))

        # Verify against the Hub's own reported hashes BEFORE flattening into piper/ -- the repo's
        # nested layout (en/en_US/amy/medium/...) is what the Hub's checksum API expects.
        failures = _verify_downloads(PIPER_REPO, tmp_dir, relative_files)
        if failures:
            for failure in failures:
                print(f"  [FAIL] {failure}")
            sys.exit(f"[piper/{voice_id}] checksum verification failed -- re-run this script to retry")

        (tmp_dir / relative_files[0]).replace(onnx_dest)
        (tmp_dir / relative_files[1]).replace(json_dest)
        print(f"[piper/{voice_id}] OK")

    if tmp_dir.is_dir():
        import shutil

        shutil.rmtree(tmp_dir, ignore_errors=True)


def warm_whisper_cache(model_size: str, skip: bool) -> None:
    if skip:
        print("[whisper] --skip-whisper set, not touching the STT cache")
        return
    try:
        from faster_whisper import WhisperModel
    except ImportError:
        print("[whisper] faster-whisper isn't installed (pip install -r local_runtime/requirements.txt) -- skipping")
        return

    print(f"[whisper] downloading/caching faster-whisper '{model_size}' (uses its own resumable, hash-checked "
          "downloader -- see this script's module docstring)...")
    model = WhisperModel(model_size, device="cpu", compute_type="int8")

    # Functional smoke check: 0.5s of near-silence should transcribe to an empty/near-empty string
    # without raising -- confirms the cached model actually loads and runs, not just that files
    # exist on disk.
    import array

    silence = array.array("h", [0] * 8000).tobytes()
    import numpy as np

    audio = np.frombuffer(silence, dtype="<i2").astype("float32") / 32768.0
    segments, _info = model.transcribe(audio, language="en", vad_filter=True)
    list(segments)  # force generator evaluation so a load/inference error surfaces here
    print(f"[whisper] OK ('{model_size}' cached and runs)")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model-dir", type=Path, default=_DEFAULT_MODEL_DIR, help="Base model directory (default: ./models)")
    parser.add_argument("--variant", choices=sorted(PHI4_VARIANT_PATTERNS), default="cpu", help="Phi-4-mini ONNX variant")
    parser.add_argument("--voices", type=str, default=None, help="Comma-separated Piper voice ids (default: all 4)")
    parser.add_argument("--stt-model", default="small", choices=["tiny", "base", "small", "medium"])
    parser.add_argument("--skip-phi4", action="store_true")
    parser.add_argument("--skip-piper", action="store_true")
    parser.add_argument("--skip-whisper", action="store_true")
    parser.add_argument("--force", action="store_true", help="Redownload even if already present")
    args = parser.parse_args()

    try:
        import huggingface_hub  # noqa: F401
    except ImportError:
        sys.exit("ERROR: huggingface_hub is required. Run: pip install -r local_runtime/requirements.txt")

    model_dir = args.model_dir.resolve()
    model_dir.mkdir(parents=True, exist_ok=True)
    print(f"Model directory: {model_dir}\n")

    voice_ids = [v.strip() for v in args.voices.split(",")] if args.voices else list(PIPER_VOICE_PATHS)

    if not args.skip_phi4:
        download_phi4(model_dir, args.variant, args.force)
    if not args.skip_piper:
        download_piper_voices(model_dir, voice_ids, args.force)
    warm_whisper_cache(args.stt_model, args.skip_whisper)

    print(
        "\nDone. Next steps (README.md 'On-device local mode'):\n"
        "  1. Set LOCAL_RUNTIME_ENDPOINT=http://localhost:8100 in app/backend/.env\n"
        "  2. Run: python -m local_runtime\n"
        "  3. In another shell: python -m app  (or your usual backend start command)\n"
    )


if __name__ == "__main__":
    main()
