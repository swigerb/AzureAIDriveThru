"""Contract tests for `local_runtime/server.py` -- exercises the real aiohttp route handlers
against tiny stub engines implementing the `SttEngine`/`ChatEngine`/`TtsEngine` protocols directly
(`local_runtime/engines/__init__.py`), with NO real model weights and NO heavy ML dependencies, so
this file runs in CI exactly like every other unit test.

Uses `aiohttp.test_utils.TestServer`/`TestClient` against a real HTTP server and
`unittest.IsolatedAsyncioTestCase`, matching the pattern `app/backend/tests/test_local_runtime.py`
already uses for the CLIENT side, so these tests prove the actual wire contract round-trips
correctly over real HTTP, not just that the right Python calls were made.
"""

from __future__ import annotations

import unittest

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer
from local_runtime.engines import ChatResult, ChatToolCall, EngineNotReadyError
from local_runtime.server import create_app


class StubSttEngine:
    def __init__(self, *, text: str = "one cheeseburger please", ready: bool = True):
        self.text = text
        self.ready = ready
        self.calls: list[tuple[bytes, int]] = []

    async def ensure_loaded(self) -> None:
        if not self.ready:
            raise EngineNotReadyError("stub stt not ready")

    async def transcribe(self, pcm16_bytes: bytes, *, sample_rate: int) -> str:
        await self.ensure_loaded()
        self.calls.append((pcm16_bytes, sample_rate))
        return self.text


class StubChatEngine:
    def __init__(self, *, result: ChatResult | None = None, ready: bool = True):
        self.result = result or ChatResult(content="sure thing", tool_calls=[])
        self.ready = ready
        self.calls: list[tuple[list[dict], list[dict]]] = []

    async def ensure_loaded(self) -> None:
        if not self.ready:
            raise EngineNotReadyError("stub chat not ready")

    async def chat(self, messages: list[dict], tools: list[dict]) -> ChatResult:
        await self.ensure_loaded()
        self.calls.append((messages, tools))
        return self.result


class StubTtsEngine:
    def __init__(self, *, pcm: bytes = b"\x01\x02\x03\x04", ready: bool = True, voices: set[str] | None = None):
        self.pcm = pcm
        self.ready = ready
        self._voices = voices if voices is not None else {"en_US-amy-medium"}
        self.calls: list[tuple[str, str, int]] = []

    async def ensure_loaded(self) -> None:
        if not self.ready:
            raise EngineNotReadyError("stub tts not ready")

    def available_voices(self) -> set[str]:
        return self._voices

    async def synthesize(self, text: str, voice: str, *, sample_rate: int) -> bytes:
        await self.ensure_loaded()
        self.calls.append((text, voice, sample_rate))
        return self.pcm


def _make_app(
    *, stt: StubSttEngine | None = None, chat: StubChatEngine | None = None, tts: StubTtsEngine | None = None,
    max_upload_bytes: int = 10 * 1024 * 1024,
) -> web.Application:
    return create_app(
        stt=stt or StubSttEngine(), chat=chat or StubChatEngine(), tts=tts or StubTtsEngine(),
        max_upload_bytes=max_upload_bytes,
    )


class TranscribeEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_returns_text_from_the_stt_engine(self):
        stt = StubSttEngine()
        app = _make_app(stt=stt)
        async with TestClient(TestServer(app)) as client:
            pcm = b"\x00\x01" * 100
            resp = await client.post("/v1/transcribe", data=pcm, headers={"Content-Type": "application/octet-stream"})
            self.assertEqual(resp.status, 200)
            self.assertEqual(await resp.json(), {"text": "one cheeseburger please"})
        self.assertEqual(stt.calls, [(pcm, 16000)])

    async def test_rejects_uploads_over_the_configured_limit(self):
        app = _make_app(max_upload_bytes=10)
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/transcribe", data=b"x" * 11)
            self.assertEqual(resp.status, 413)

    async def test_returns_503_when_engine_not_ready(self):
        app = _make_app(stt=StubSttEngine(ready=False))
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/transcribe", data=b"\x00\x01" * 100)
            self.assertEqual(resp.status, 503)
            self.assertIn("error", await resp.json())


class ChatEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_posts_messages_and_tools_and_returns_content(self):
        chat = StubChatEngine()
        app = _make_app(chat=chat)
        messages = [
            {"role": "system", "content": "you are a drive-thru order taker"},
            {"role": "user", "content": "hi"},
        ]
        tools = [{"type": "function", "name": "get_order", "description": "d", "parameters": {}}]
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", json={"messages": messages, "tools": tools})
            self.assertEqual(resp.status, 200)
            self.assertEqual(await resp.json(), {"content": "sure thing", "tool_calls": []})
        self.assertEqual(chat.calls, [(messages, tools)])

    async def test_returns_tool_calls_shape_matching_the_wire_contract(self):
        result = ChatResult(content=None, tool_calls=[ChatToolCall(id="call_1", name="get_order", arguments="{}")])
        app = _make_app(chat=StubChatEngine(result=result))
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", json={"messages": [], "tools": []})
            self.assertEqual(
                await resp.json(),
                {"content": None, "tool_calls": [{"id": "call_1", "name": "get_order", "arguments": "{}"}]},
            )

    async def test_defaults_tools_to_empty_list_when_omitted(self):
        chat = StubChatEngine()
        app = _make_app(chat=chat)
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", json={"messages": [{"role": "user", "content": "hi"}]})
            self.assertEqual(resp.status, 200)
        self.assertEqual(chat.calls[0][1], [])

    async def test_rejects_a_non_list_messages_field(self):
        app = _make_app()
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", json={"messages": "not-a-list"})
            self.assertEqual(resp.status, 400)

    async def test_rejects_malformed_json_body(self):
        app = _make_app()
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", data=b"not json", headers={"Content-Type": "application/json"})
            self.assertEqual(resp.status, 400)

    async def test_returns_503_when_engine_not_ready(self):
        app = _make_app(chat=StubChatEngine(ready=False))
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/chat", json={"messages": []})
            self.assertEqual(resp.status, 503)


class SpeakEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_posts_text_and_voice_and_returns_raw_pcm_bytes(self):
        tts = StubTtsEngine()
        app = _make_app(tts=tts)
        async with TestClient(TestServer(app)) as client:
            resp = await client.post(
                "/v1/speak", json={"text": "your total is five dollars", "voice": "en_US-amy-medium"},
            )
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers["Content-Type"], "application/octet-stream")
            self.assertEqual(await resp.read(), b"\x01\x02\x03\x04")
        self.assertEqual(tts.calls, [("your total is five dollars", "en_US-amy-medium", 24000)])

    async def test_resolves_an_unrecognized_voice_to_the_configured_default(self):
        tts = StubTtsEngine(voices={"en_US-amy-medium"})
        app = _make_app(tts=tts)
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/speak", json={"text": "hi", "voice": "not-a-real-voice"})
            self.assertEqual(resp.status, 200)
        self.assertEqual(tts.calls[0][1], "en_US-amy-medium")

    async def test_omitted_voice_falls_back_to_the_default(self):
        tts = StubTtsEngine()
        app = _make_app(tts=tts)
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/speak", json={"text": "hi"})
            self.assertEqual(resp.status, 200)
        self.assertEqual(tts.calls[0][1], "en_US-amy-medium")

    async def test_returns_503_when_engine_not_ready(self):
        app = _make_app(tts=StubTtsEngine(ready=False))
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/speak", json={"text": "hi"})
            self.assertEqual(resp.status, 503)

    async def test_rejects_a_non_string_text_field(self):
        app = _make_app()
        async with TestClient(TestServer(app)) as client:
            resp = await client.post("/v1/speak", json={"text": 123})
            self.assertEqual(resp.status, 400)


class HealthEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_reports_ready_true_when_every_engine_loads(self):
        app = _make_app()
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/health")
            self.assertEqual(resp.status, 200)
            self.assertEqual(await resp.json(), {"ready": True, "engines": {"stt": True, "chat": True, "tts": True}})

    async def test_reports_503_and_per_engine_status_when_one_engine_is_not_ready(self):
        app = _make_app(tts=StubTtsEngine(ready=False))
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/health")
            self.assertEqual(resp.status, 503)
            self.assertEqual(
                await resp.json(), {"ready": False, "engines": {"stt": True, "chat": True, "tts": False}},
            )


if __name__ == "__main__":
    unittest.main()
