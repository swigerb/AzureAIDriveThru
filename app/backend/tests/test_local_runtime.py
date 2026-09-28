"""Unit tests for local_runtime.py (issue #81, P2-12): the HTTP/JSON client to the companion
local-runtime process.

Covers:
  - `_resample_pcm16`: the pure-Python (no numpy) linear-interpolation resampler bridging the
    24 kHz mic-capture rate every pipeline shares to Whisper's expected 16 kHz input.
  - `_parse_chat_response`: validates and converts a `/v1/chat` JSON payload into
    `LocalChatResult`/`LocalToolCall`, rejecting every malformed shape with `LocalRuntimeError`
    rather than letting a bad response reach `LocalProcessor`'s tool loop.
  - `HttpLocalRuntimeClient`: exercised against a REAL aiohttp server (`aiohttp.test_utils.
    TestServer`) standing in for the companion local-runtime process -- not a mocked
    `ClientSession` -- so these tests prove the actual wire contract (request method/path/body
    shape, response parsing) round-trips correctly over real HTTP, not just that the right
    Python calls were made.
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from aiohttp.test_utils import TestServer

from local_runtime import (
    HttpLocalRuntimeClient,
    LocalRuntimeError,
    _parse_chat_response,
    _resample_pcm16,
)

# ═══════════════════════════════════════════════════════════════════════════════
# _resample_pcm16
# ═══════════════════════════════════════════════════════════════════════════════

def _tone(num_samples: int, amplitude: int = 1000) -> bytes:
    import struct
    return struct.pack(f"<{num_samples}h", *([amplitude] * num_samples))


class ResamplePcm16Tests(unittest.TestCase):
    def test_same_rate_returns_input_unchanged(self):
        pcm = _tone(480)
        self.assertEqual(_resample_pcm16(pcm, 24000, 24000), pcm)

    def test_empty_input_returns_empty_output(self):
        self.assertEqual(_resample_pcm16(b"", 24000, 16000), b"")

    def test_downsampling_produces_the_expected_sample_count(self):
        pcm = _tone(2400)  # 100ms @ 24kHz
        out = _resample_pcm16(pcm, 24000, 16000)
        # 100ms @ 16kHz = 1600 samples = 3200 bytes.
        self.assertEqual(len(out), 1600 * 2)

    def test_upsampling_produces_the_expected_sample_count(self):
        pcm = _tone(1600)  # 100ms @ 16kHz
        out = _resample_pcm16(pcm, 16000, 24000)
        self.assertEqual(len(out), 2400 * 2)

    def test_downsampling_a_constant_tone_preserves_its_amplitude(self):
        # A constant-amplitude signal resampled at any rate must still be (near enough) that
        # same amplitude -- proves the interpolation isn't corrupting values, just resampling
        # the timeline.
        import array
        pcm = _tone(2400, amplitude=1000)
        out = _resample_pcm16(pcm, 24000, 16000)
        samples = array.array("h")
        samples.frombytes(out)
        for s in samples:
            self.assertEqual(s, 1000)

    def test_odd_trailing_byte_is_dropped_not_corrupted(self):
        pcm = _tone(10) + b"\x01"  # one stray trailing byte, not a full sample
        out = _resample_pcm16(pcm, 24000, 24000 * 2)
        self.assertEqual(len(out) % 2, 0)


# ═══════════════════════════════════════════════════════════════════════════════
# _parse_chat_response
# ═══════════════════════════════════════════════════════════════════════════════

class ParseChatResponseTests(unittest.TestCase):
    def test_content_only_response(self):
        result = _parse_chat_response({"content": "Welcome to the drive-thru!"}, "http://x/v1/chat")
        self.assertEqual(result.content, "Welcome to the drive-thru!")
        self.assertEqual(result.tool_calls, [])

    def test_null_content_is_allowed(self):
        result = _parse_chat_response({"content": None, "tool_calls": []}, "http://x/v1/chat")
        self.assertIsNone(result.content)

    def test_tool_calls_are_parsed(self):
        payload = {
            "content": None,
            "tool_calls": [{"id": "call_1", "name": "get_order", "arguments": '{"a": 1}'}],
        }
        result = _parse_chat_response(payload, "http://x/v1/chat")
        self.assertEqual(len(result.tool_calls), 1)
        call = result.tool_calls[0]
        self.assertEqual(call.id, "call_1")
        self.assertEqual(call.name, "get_order")
        self.assertEqual(call.arguments, '{"a": 1}')

    def test_tool_call_arguments_as_a_json_object_is_serialized_to_a_string(self):
        payload = {"tool_calls": [{"id": "call_1", "name": "get_order", "arguments": {"a": 1}}]}
        result = _parse_chat_response(payload, "http://x/v1/chat")
        self.assertEqual(result.tool_calls[0].arguments, '{"a": 1}')

    def test_non_dict_payload_raises(self):
        with self.assertRaises(LocalRuntimeError):
            _parse_chat_response(["not", "a", "dict"], "http://x/v1/chat")

    def test_non_string_content_raises(self):
        with self.assertRaises(LocalRuntimeError):
            _parse_chat_response({"content": 42}, "http://x/v1/chat")

    def test_tool_calls_not_a_list_raises(self):
        with self.assertRaises(LocalRuntimeError):
            _parse_chat_response({"tool_calls": "not a list"}, "http://x/v1/chat")

    def test_tool_call_missing_id_raises(self):
        with self.assertRaises(LocalRuntimeError):
            _parse_chat_response({"tool_calls": [{"name": "get_order"}]}, "http://x/v1/chat")

    def test_tool_call_missing_name_raises(self):
        with self.assertRaises(LocalRuntimeError):
            _parse_chat_response({"tool_calls": [{"id": "call_1"}]}, "http://x/v1/chat")


# ═══════════════════════════════════════════════════════════════════════════════
# HttpLocalRuntimeClient, against a REAL companion-process fake (aiohttp TestServer)
# ═══════════════════════════════════════════════════════════════════════════════

class _FakeCompanionServer:
    """A tiny stand-in for the real companion local-runtime process -- a real aiohttp web
    server (not a mock) exposing the same three routes `HttpLocalRuntimeClient` calls, so these
    tests prove the actual request/response wire contract, not just that the right Python
    method was invoked."""

    def __init__(self):
        self.transcribe_requests: list[bytes] = []
        self.chat_requests: list[dict] = []
        self.speak_requests: list[dict] = []
        self.transcribe_response = {"text": "one shake please"}
        self.chat_response = {"content": "Coming right up!", "tool_calls": []}
        self.speak_response = b"\x01\x02" * 100
        self.transcribe_status = 200
        self.chat_status = 200
        self.speak_status = 200

    async def _handle_transcribe(self, request: web.Request) -> web.Response:
        body = await request.read()
        self.transcribe_requests.append(body)
        return web.json_response(self.transcribe_response, status=self.transcribe_status)

    async def _handle_chat(self, request: web.Request) -> web.Response:
        payload = await request.json()
        self.chat_requests.append(payload)
        return web.json_response(self.chat_response, status=self.chat_status)

    async def _handle_speak(self, request: web.Request) -> web.Response:
        payload = await request.json()
        self.speak_requests.append(payload)
        return web.Response(body=self.speak_response, content_type="application/octet-stream", status=self.speak_status)

    def as_app(self) -> web.Application:
        app = web.Application()
        app.router.add_post("/v1/transcribe", self._handle_transcribe)
        app.router.add_post("/v1/chat", self._handle_chat)
        app.router.add_post("/v1/speak", self._handle_speak)
        return app


class HttpLocalRuntimeClientTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.fake = _FakeCompanionServer()
        self.server = TestServer(self.fake.as_app())
        await self.server.start_server()
        self.client = HttpLocalRuntimeClient(str(self.server.make_url("")), mic_sample_rate=16000)

    async def asyncTearDown(self):
        await self.server.close()

    async def test_transcribe_posts_raw_pcm_and_returns_the_text(self):
        pcm = _tone(1600)  # already 16kHz -- mic_sample_rate matches _STT_SAMPLE_RATE, no resampling
        text = await self.client.transcribe(pcm)
        self.assertEqual(text, "one shake please")
        self.assertEqual(self.fake.transcribe_requests, [pcm])

    async def test_transcribe_resamples_before_uploading(self):
        client_24k = HttpLocalRuntimeClient(str(self.server.make_url("")), mic_sample_rate=24000)
        pcm = _tone(2400)  # 100ms @ 24kHz
        await client_24k.transcribe(pcm)
        uploaded = self.fake.transcribe_requests[0]
        # 100ms @ 16kHz = 1600 samples = 3200 bytes -- proves the client resampled down, not
        # just forwarded the 24kHz bytes verbatim.
        self.assertEqual(len(uploaded), 1600 * 2)

    async def test_transcribe_raises_local_runtime_error_on_malformed_response(self):
        self.fake.transcribe_response = {"unexpected": "shape"}
        with self.assertRaises(LocalRuntimeError):
            await self.client.transcribe(_tone(1600))

    async def test_transcribe_raises_local_runtime_error_on_http_failure(self):
        self.fake.transcribe_status = 500
        with self.assertRaises(LocalRuntimeError):
            await self.client.transcribe(_tone(1600))

    async def test_chat_posts_messages_and_tools_and_returns_content(self):
        messages = [{"role": "user", "content": "one shake please"}]
        tools = [{"name": "get_order", "description": "", "parameters": {}}]
        result = await self.client.chat(messages, tools)
        self.assertEqual(result.content, "Coming right up!")
        self.assertEqual(self.fake.chat_requests, [{"messages": messages, "tools": tools}])

    async def test_chat_returns_tool_calls(self):
        self.fake.chat_response = {
            "content": None,
            "tool_calls": [{"id": "call_1", "name": "get_order", "arguments": "{}"}],
        }
        result = await self.client.chat([], [])
        self.assertEqual(len(result.tool_calls), 1)
        self.assertEqual(result.tool_calls[0].name, "get_order")

    async def test_chat_raises_local_runtime_error_on_http_failure(self):
        self.fake.chat_status = 503
        with self.assertRaises(LocalRuntimeError):
            await self.client.chat([], [])

    async def test_speak_posts_text_and_voice_and_returns_raw_audio_bytes(self):
        audio = await self.client.speak("Coming right up!", "en_US-amy-medium")
        self.assertEqual(audio, self.fake.speak_response)
        self.assertEqual(self.fake.speak_requests, [{"text": "Coming right up!", "voice": "en_US-amy-medium"}])

    async def test_speak_raises_local_runtime_error_on_http_failure(self):
        self.fake.speak_status = 500
        with self.assertRaises(LocalRuntimeError):
            await self.client.speak("hi", "en_US-amy-medium")

    async def test_unreachable_endpoint_raises_local_runtime_error(self):
        dead_client = HttpLocalRuntimeClient("http://127.0.0.1:1", timeout_seconds=1.0)
        with self.assertRaises(LocalRuntimeError):
            await dead_client.transcribe(_tone(160))


if __name__ == "__main__":
    unittest.main()
