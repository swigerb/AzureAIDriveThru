"""Entrypoint for the companion local-runtime process: `python -m local_runtime`.

Wires the real `WhisperSttEngine`/`Phi4ChatEngine`/`PiperTtsEngine` implementations onto
`local_runtime.server.create_app` and runs the aiohttp app on `ServerConfig.host`/`port`
(defaults `0.0.0.0:8100`, matching `docker-compose.local.yml`'s `LOCAL_RUNTIME_ENDPOINT` wiring and
`app/backend/.env-sample`'s documented default). This module is intentionally the ONLY place that
imports concrete engine classes -- `local_runtime/server.py` and `local_runtime/tests/` only ever
see the `SttEngine`/`ChatEngine`/`TtsEngine` protocols.
"""

from __future__ import annotations

import logging

from aiohttp import web

from local_runtime.config import load_config
from local_runtime.engines.chat import Phi4ChatEngine
from local_runtime.engines.stt import WhisperSttEngine
from local_runtime.engines.tts import PiperTtsEngine
from local_runtime.server import create_app


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    config = load_config()

    app = create_app(
        stt=WhisperSttEngine(config.stt),
        chat=Phi4ChatEngine(config.chat),
        tts=PiperTtsEngine(config.tts),
        default_voice=config.tts.default_voice,
        max_upload_bytes=config.server.max_upload_bytes,
    )
    logging.getLogger(__name__).info(
        "Starting local-runtime companion service on %s:%d", config.server.host, config.server.port,
    )
    web.run_app(app, host=config.server.host, port=config.server.port, print=None)


if __name__ == "__main__":
    main()
