# ---------------------------------------------------------------------------
# Companion local-runtime image (issue #81 part 2): Whisper STT + Phi-4-mini
# ONNX chat + Piper TTS behind part 1's HTTP contract (/v1/transcribe,
# /v1/chat, /v1/speak). Kept as its own image, never merged into app/Dockerfile
# -- the main backend image must not grow these heavy, optional dependencies.
# ---------------------------------------------------------------------------
FROM python:3.12-slim

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# onnxruntime-genai / piper-tts / faster-whisper all ship prebuilt CPU wheels for
# linux/amd64, but faster-whisper's ctranslate2 dependency needs libgomp1 at
# runtime, and piper-tts needs libsndfile1 to read/write wav data.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

COPY ./local_runtime/requirements.txt /app/requirements.txt
RUN python -m pip install --no-cache-dir -r requirements.txt

COPY ./local_runtime /app/local_runtime

EXPOSE 8100

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8100/health')" || exit 1

# Single worker, plain aiohttp runner (no gunicorn) -- this is a single-tenant
# companion process for one backend replica on the same machine, not a
# horizontally-scaled service; ensure_loaded()'s per-engine asyncio.Lock
# assumes exactly one process holds the model weights in memory.
CMD ["python3", "-m", "local_runtime"]
