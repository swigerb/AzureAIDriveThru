"""scripts/smoke_realtime.py: the test audio is the phrase read aloud, the
transcript must match it word for word, the token comes from the resource's
tenant, and the postdeploy hook never fails `azd up`."""

import asyncio
import base64
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import yaml
from aiohttp import web
from aiohttp.test_utils import TestServer

REPO = Path(__file__).resolve().parents[3]
sys.path.append(str(REPO / "app" / "backend"))
sys.path.append(str(REPO / "scripts"))

import smoke_realtime  # noqa: E402

import model_catalog  # noqa: E402
import persona_loader  # noqa: E402


def _fake_model_catalog(deployments=None):
    """A ModelCatalog built from an explicit fixture config/env (#83, P2-14 --model tests),
    never the repo's real config.yaml/os.environ."""
    environ = {"AZURE_AI_MODEL_DEPLOYMENTS": json.dumps(deployments)} if deployments else {}
    return model_catalog.ModelCatalog.load(
        config={"models": {"catalog": [
            {"id": "gpt-realtime-2.1", "pipeline": "realtime", "label": "GPT Realtime 2.1"},
            {"id": "phi-4", "pipeline": "cascade", "label": "Phi-4 (Foundry)"},
        ]}},
        environ=environ,
    )

CLEAN_ENV = {
    "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
    "AZURE_OPENAI_REALTIME_REASONING_EFFORT": "",
    "AZURE_OPENAI_REALTIME_REASONING_MODEL": "",
    "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL": "",
    "AZURE_OPENAI_EASTUS2_API_KEY": "",
}

PHRASE = smoke_realtime._DEFAULT_TRANSCRIPTION_PHRASE
# What gpt-realtime-2.1 produced when handed the phrase as a user turn: it took
# the order instead of reading it. The old check passed this with a note.
ANSWERED = "Sure, I can't place the order for you, but a large cherry limeade and medium tots sounds tasty!"


class EchoingRealtime:
    """Fake /openai/v1/realtime: applies session.updates and echoes the session
    like GA does, "speaks" on response.create, and on input_audio_buffer.commit
    reports `self.transcript` as the guest's transcribed speech.

    #302: also fakes the search tool-call turn (emits a `function_call` item instead of audio
    when the preceding user turn is the search check's own scripted guest text, if
    `self.search_call` is set) and the cascade pipeline's own audio HTTP endpoints
    (`/openai/deployments/{deployment}/audio/transcriptions`, `/openai/v1/audio/speech`) on the SAME fake server,
    since a real cascade deployment reuses the realtime resource for its audio models too."""

    def __init__(self):
        self.reject_keys: set[str] = set()
        self.drop_tools = False
        self.transcript = PHRASE
        self.received: list[dict] = []
        self.auth: list[str | None] = []
        # #302: None means "the model doesn't call any tool" (search check failure mode);
        # a dict {"name": ..., "arguments": ...} is the function_call item to emit instead.
        self.search_call: dict | None = {"name": "search", "arguments": json.dumps({"query": "drinks"})}
        self.cascade_transcript = PHRASE
        self.cascade_tts_bytes = b"\x02\x00" * 1200
        self.cascade_auth: list[str | None] = []

    def app(self):
        app = web.Application()
        app.router.add_get("/openai/v1/realtime", self.handler)
        app.router.add_post("/openai/deployments/{deployment}/audio/transcriptions", self.transcriptions_handler)
        app.router.add_post("/openai/v1/audio/speech", self.speech_handler)
        return app

    async def transcriptions_handler(self, request):
        self.cascade_auth.append(request.headers.get("Authorization"))
        await request.post()  # drain the multipart body
        return web.json_response({"text": self.cascade_transcript})

    async def speech_handler(self, request):
        self.cascade_auth.append(request.headers.get("Authorization"))
        await request.json()
        return web.Response(body=self.cascade_tts_bytes, content_type="application/octet-stream")

    async def handler(self, request):
        self.auth.append(request.headers.get("api-key"))
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        session: dict = {"type": "realtime", "tools": [], "tool_choice": "auto", "instructions": ""}
        pending_search_turn = False
        async for msg in ws:
            event = json.loads(msg.data)
            self.received.append(event)
            kind = event["type"]
            if kind == "conversation.item.create":
                item = event.get("item") or {}
                content = item.get("content") or []
                text = next((c.get("text") for c in content if c.get("type") == "input_text"), None)
                if text == smoke_realtime.SEARCH_TOOL_GUEST_TEXT:
                    pending_search_turn = True
            elif kind == "response.create":
                if pending_search_turn:
                    pending_search_turn = False
                    if self.search_call is not None:
                        await ws.send_json({"type": "response.output_item.done", "item": {
                            "type": "function_call", "name": self.search_call["name"],
                            "arguments": self.search_call["arguments"], "call_id": "call_1"}})
                    await ws.send_json({"type": "response.done"})
                else:
                    await ws.send_json({"type": "response.output_audio.delta",
                                        "delta": base64.b64encode(b"\x01\x00" * 2400).decode()})
                    await ws.send_json({"type": "response.done"})
            elif kind == "input_audio_buffer.commit":
                await ws.send_json({"type": "conversation.item.input_audio_transcription.completed",
                                    "transcript": self.transcript})
            elif kind == "session.update":
                bad = sorted(k for k in event["session"] if k in self.reject_keys)
                if bad:
                    await ws.send_json({"type": "error", "error": {
                        "type": "invalid_request_error", "code": "invalid_value", "param": f"session.{bad[0]}",
                        "event_id": event.get("event_id"), "message": "Unsupported option."}})
                    continue
                session.update(event["session"])
                echoed = dict(session)
                if self.drop_tools:
                    echoed["tools"] = []
                await ws.send_json({"type": "session.updated", "session": echoed})
        return ws


class TranscriptMatchTests(unittest.TestCase):
    """A transcript passes only if it is the phrase, word for word.

    Uses its own fixed test phrase (independent of smoke_realtime's actual default/persona
    phrase, #83/P2-14) since these are pure algorithm tests of transcript_matches/
    transcript_similarity's fuzzy-match tolerance, not of any particular phrase's wording."""

    _TEST_PHRASE = "Hi, can I get a large cherry limeade and a medium tots, please?"

    def test_verbatim_and_formatting_variants_match(self):
        for transcript in (
            self._TEST_PHRASE,
            "hi can i get a large cherry limeade and a medium tots please",
            "  Hi, can I get a large cherry limeade, and a medium tots, please.  ",
            "Hi, can I get a large cherry lime-ade and a medium tots, please?",
            "Hi, can I get a large cherry lime aid and a medium tots, please?",
            # Two slips at once (about 0.95): still the phrase, so still a pass.
            "Hi, can I get a large cherry limeade and a medium tater tots, please?",
            "Hey can I get a large cherry lime aid and a medium tot please",
        ):
            with self.subTest(transcript):
                self.assertTrue(smoke_realtime.transcript_matches(self._TEST_PHRASE, transcript))

    def test_answers_and_paraphrases_do_not_match(self):
        for transcript in (
            ANSWERED,
            "Sure! One large cherry limeade and a medium tots coming right up.",
            "Can I get a cherry limeade and tots?",
            "Hi, can I get a large cherry limeade and a medium tots, please? Anything else for you today?",
            "",
            "   ",
        ):
            with self.subTest(transcript):
                self.assertFalse(smoke_realtime.transcript_matches(self._TEST_PHRASE, transcript))

    def test_normalisation(self):
        self.assertEqual(smoke_realtime.normalise_transcript("  Hi,   THERE!\n"), "hi there")
        self.assertEqual(smoke_realtime.normalise_transcript("申し訳ありません、少々お待ちください。"),
                         "申し訳ありません 少々お待ちください")
        self.assertEqual(smoke_realtime.transcript_similarity(self._TEST_PHRASE, self._TEST_PHRASE.upper()), 1.0)

    def test_threshold_is_between_a_misspelling_and_a_paraphrase(self):
        misspelt = smoke_realtime.transcript_similarity(
            self._TEST_PHRASE, self._TEST_PHRASE.replace("limeade", "lime aid"))
        dropped = smoke_realtime.transcript_similarity(self._TEST_PHRASE, "Can I get a cherry limeade and tots?")
        self.assertGreaterEqual(misspelt, smoke_realtime.TRANSCRIPT_MATCH_THRESHOLD)
        self.assertLess(dropped, smoke_realtime.TRANSCRIPT_MATCH_THRESHOLD)


class RealtimeUrlTests(unittest.TestCase):

    def test_azure_is_wss_and_a_local_fake_is_ws(self):
        self.assertEqual(smoke_realtime.realtime_url("https://r.openai.azure.com/", "gpt-realtime-2.1"),
                         "wss://r.openai.azure.com/openai/v1/realtime?model=gpt-realtime-2.1")
        self.assertEqual(smoke_realtime.realtime_url("http://127.0.0.1:8080", "d"),
                         "ws://127.0.0.1:8080/openai/v1/realtime?model=d")


class CheckSessionTests(unittest.TestCase):

    def _good(self):
        return {"tools": [{"name": n} for n in smoke_realtime.EXPECTED_TOOLS], "tool_choice": "auto",
                "instructions": "x", "reasoning": {"effort": "low"}, "audio": {"output": {"voice": "marin"}}}

    def test_pass(self):
        self.assertEqual(smoke_realtime.check_session("b", self._good(), self._good(), None, {"effort": "low"}), [])

    def test_each_failure_mode_is_reported(self):
        cases = {
            "rejected": (self._good(), None, {"error": {"code": "invalid_value", "param": "session.reasoning"}}),
            "tools": (self._good(), {**self._good(), "tools": [{"name": "search"}]}, None),
            "tool_choice": (self._good(), {**self._good(), "tool_choice": "none"}, None),
            "instructions": (self._good(), {**self._good(), "instructions": ""}, None),
            "reasoning": (self._good(), {**self._good(), "reasoning": {"effort": "none"}}, None),
            "voice": (self._good(), {**self._good(), "audio": {"output": {"voice": "alloy"}}}, None),
        }
        for name, (sent, echoed, error) in cases.items():
            with self.subTest(name):
                failures = smoke_realtime.check_session("b", sent, echoed, error, {"effort": "low"})
                self.assertEqual(len(failures), 1, failures)


class _FakeServerCase(unittest.IsolatedAsyncioTestCase):

    async def asyncSetUp(self):
        self.fake = EchoingRealtime()
        self.server = TestServer(self.fake.app())
        await self.server.start_server()
        self.endpoint = str(self.server.make_url("/"))
        env = patch.dict(os.environ, CLEAN_ENV)
        env.start()
        self.addCleanup(env.stop)

    async def asyncTearDown(self):
        await self.server.close()


class SynthesizeTests(_FakeServerCase):
    """The test audio must be the phrase read aloud, not the model's reply to it."""

    async def test_phrase_is_sent_as_response_instructions_not_a_user_turn(self):
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        pcm = await smoke_realtime._synthesize(url, {}, PHRASE, 5)

        self.assertEqual(pcm, b"\x01\x00" * 2400)
        kinds = [e["type"] for e in self.fake.received]
        self.assertEqual(kinds, ["session.update", "response.create"])
        create = self.fake.received[1]
        self.assertIn(f'"{PHRASE}"', create["response"]["instructions"])
        self.assertIn("word for word", create["response"]["instructions"])
        session = self.fake.received[0]["session"]
        self.assertIsNone(session["audio"]["input"]["turn_detection"])
        self.assertEqual(session["audio"]["output"]["voice"], "alloy")
        self.assertNotIn(PHRASE, session["instructions"])

    async def test_voice_is_configurable(self):
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        await smoke_realtime._synthesize(url, {}, "Hello.", 5, voice="marin")
        self.assertEqual(self.fake.received[0]["session"]["audio"]["output"]["voice"], "marin")


class CheckTranscriptionTests(_FakeServerCase):
    """check_transcription fails when the transcript is not the phrase."""

    async def _check(self):
        rtmt = smoke_realtime.build_middle_tier(self.endpoint, "gpt-realtime-2.1", environ=dict(CLEAN_ENV))
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        return await smoke_realtime.check_transcription(rtmt, url, {}, 5)

    async def test_verbatim_transcript_passes(self):
        failures, report = await self._check()
        self.assertEqual(failures, [])
        self.assertIn("matches the test phrase", report[0])
        commits = [e for e in self.fake.received if e["type"] == "input_audio_buffer.commit"]
        self.assertEqual(len(commits), 1)

    async def test_answered_transcript_fails(self):
        self.fake.transcript = ANSWERED
        failures, report = await self._check()
        self.assertEqual(report, [])
        self.assertEqual(len(failures), 1)
        self.assertIn("does not match the test phrase", failures[0])

    async def test_empty_transcript_fails(self):
        self.fake.transcript = ""
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("empty transcript", failures[0])


class LiveShapeTests(_FakeServerCase):
    """End to end against a fake GA endpoint (no Azure)."""

    async def _run(self, skip_transcription=True, skip_search=True):
        return await smoke_realtime.run(self.endpoint, "gpt-realtime-2.1", voice=None, timeout=5,
                                        skip_transcription=skip_transcription, skip_search=skip_search,
                                        headers={"api-key": "k"})

    async def test_passes_when_the_session_is_accepted(self):
        self.assertEqual(await self._run(), 0)
        updates = [e for e in self.fake.received if e["type"] == "session.update"]
        self.assertEqual(len(updates), 3)
        self.assertEqual(self.fake.auth, ["k"])

    async def test_fails_when_reasoning_is_rejected(self):
        self.fake.reject_keys = {"reasoning"}
        self.assertEqual(await self._run(), 1)

    async def test_fails_when_tools_do_not_register(self):
        self.fake.drop_tools = True
        self.assertEqual(await self._run(), 1)

    async def test_with_transcription_passes_on_a_verbatim_transcript(self):
        self.assertEqual(await self._run(skip_transcription=False), 0)

    async def test_with_transcription_fails_on_an_answered_transcript(self):
        self.fake.transcript = ANSWERED
        self.assertEqual(await self._run(skip_transcription=False), 1)

    async def test_unreachable_endpoint_is_could_not_run(self):
        await self.server.close()
        with self.assertRaises(smoke_realtime.SmokeError):
            await self._run()


class SearchHitCountTests(unittest.TestCase):
    """_count_search_hits (#302): one hit per "[identifier]: ..." block, 0 for the "no
    results" error message (which never starts with "[")."""

    def test_empty_text_is_zero_hits(self):
        self.assertEqual(smoke_realtime._count_search_hits(""), 0)
        self.assertEqual(smoke_realtime._count_search_hits("   "), 0)

    def test_no_results_message_is_zero_hits(self):
        self.assertEqual(smoke_realtime._count_search_hits("No results found for this search."), 0)

    def test_one_block_is_one_hit(self):
        self.assertEqual(smoke_realtime._count_search_hits("[item-1]: Item: Cherry Limeade"), 1)

    def test_multiple_blocks_separated_by_dashes(self):
        text = "[item-1]: Item: Cherry Limeade\n-----\n[item-2]: Item: Tots"
        self.assertEqual(smoke_realtime._count_search_hits(text), 2)


class _FakeCredential:
    """A stand-in TokenCredential (#302): enough shape for SearchClient's credential-policy
    inference (``hasattr(credential, "get_token")``) -- never actually called, since these
    tests patch ``tools.search`` itself rather than letting any real HTTP happen."""

    def get_token(self, *_a, **_k):
        return SimpleNamespace(token="tok", expires_on=0)


class CheckSearchToolCallTests(_FakeServerCase):
    """check_search_tool_call (#302 step 1): a scripted guest turn must produce a well-formed
    `search` function_call, then the SAME query is run for real through tools.search() (faked
    here, no live Azure Search)."""

    async def _check(self, persona=None, tool_result=None):
        rtmt = smoke_realtime.build_middle_tier(self.endpoint, "gpt-realtime-2.1", environ=dict(CLEAN_ENV),
                                                persona=persona)
        url = smoke_realtime.realtime_url(self.endpoint, "gpt-realtime-2.1")
        import rtmt as rtmt_module
        import tools
        result = tool_result or rtmt_module.ToolResult("[item-1]: Item: Cherry Limeade, Category: Drinks",
                                                       rtmt_module.ToolResultDirection.TO_SERVER)

        async def fake_search(*_args, **_kwargs):
            return result

        env = {**CLEAN_ENV, "AZURE_SEARCH_ENDPOINT": "https://search.example.com", "AZURE_SEARCH_INDEX": "menu-index"}
        with patch.dict(os.environ, env), patch.object(tools, "search", fake_search):
            return await smoke_realtime.check_search_tool_call(rtmt, url, {}, 5, persona, _FakeCredential())

    async def test_well_formed_call_with_hits_passes(self):
        failures, report = await self._check()
        self.assertEqual(failures, [])
        self.assertTrue(any("called search" in line for line in report))
        self.assertTrue(any("returned 1 hit" in line for line in report))

    async def test_no_function_call_fails(self):
        self.fake.search_call = None
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("did not emit a function_call", failures[0])

    async def test_wrong_tool_name_fails(self):
        self.fake.search_call = {"name": "add_to_order", "arguments": json.dumps({"query": "drinks"})}
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("called 'add_to_order'", failures[0])

    async def test_malformed_json_arguments_fails(self):
        self.fake.search_call = {"name": "search", "arguments": "{not json"}
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("not valid JSON", failures[0])

    async def test_empty_query_argument_fails(self):
        self.fake.search_call = {"name": "search", "arguments": json.dumps({"query": ""})}
        failures, _ = await self._check()
        self.assertEqual(len(failures), 1)
        self.assertIn("no non-empty 'query'", failures[0])

    async def test_zero_hits_fails(self):
        import rtmt as rtmt_module
        empty_result = rtmt_module.ToolResult("No results found for this search.",
                                              rtmt_module.ToolResultDirection.TO_SERVER)
        failures, _ = await self._check(tool_result=empty_result)
        self.assertEqual(len(failures), 1)
        self.assertIn("returned no hits", failures[0])

    async def test_real_persona_resolves_its_own_index(self):
        persona = smoke_realtime.resolve_persona(persona_loader.PersonaCatalog.load().default_persona_id)
        failures, report = await self._check(persona=persona)
        self.assertEqual(failures, [])
        self.assertTrue(any(persona.id in line for line in report))


class SearchCredentialTests(unittest.TestCase):
    """get_search_credential (#302): API key wins, same tenant-pinned chain otherwise."""

    def test_api_key_env_var_wins(self):
        with patch.dict(os.environ, {"AZURE_SEARCH_API_KEY": "secret-key"}):
            credential = smoke_realtime.get_search_credential()
        self.assertIsInstance(credential, smoke_realtime.AzureKeyCredential)

    def test_no_key_falls_back_to_credential_chain(self):
        class FakeCred:
            def get_token(self, *_a, **_k):
                return SimpleNamespace(token="tok", expires_on=0)

        with patch.dict(os.environ, {"AZURE_SEARCH_API_KEY": ""}), \
                patch.object(smoke_realtime, "_credentials", return_value=[FakeCred()]):
            credential = smoke_realtime.get_search_credential("t", "s")
        self.assertIsInstance(credential, FakeCred)

    def test_every_candidate_failing_is_a_smoke_error(self):
        class FailingCred:
            def get_token(self, *_a, **_k):
                raise RuntimeError("no token")

        with patch.dict(os.environ, {"AZURE_SEARCH_API_KEY": ""}), \
                patch.object(smoke_realtime, "_credentials", return_value=[FailingCred()]):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime.get_search_credential()


class SearchContextForTests(unittest.TestCase):
    """_search_context_for (#302): persona's own search.indexName, or AZURE_SEARCH_INDEX for the
    legacy no-persona invocation; fails closed with no endpoint/index configured at all."""

    def test_no_endpoint_is_a_smoke_error(self):
        with patch.dict(os.environ, {"AZURE_SEARCH_ENDPOINT": ""}):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime._search_context_for(None, _FakeCredential())

    def test_no_persona_and_no_index_env_is_a_smoke_error(self):
        with patch.dict(os.environ, {"AZURE_SEARCH_ENDPOINT": "https://s.example.com", "AZURE_SEARCH_INDEX": ""}):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime._search_context_for(None, _FakeCredential())

    def test_no_persona_uses_azure_search_index_env(self):
        from azure.search.documents import aio as search_aio
        with patch.dict(os.environ, {"AZURE_SEARCH_ENDPOINT": "https://s.example.com",
                                     "AZURE_SEARCH_INDEX": "legacy-index"}), \
                patch.object(search_aio, "SearchClient") as fake_client:
            smoke_realtime._search_context_for(None, _FakeCredential())
        self.assertEqual(fake_client.call_args.args[1], "legacy-index")

    def test_persona_uses_its_own_index_name(self):
        from azure.search.documents import aio as search_aio
        persona = persona_loader.PersonaCatalog.load().get(persona_loader.PersonaCatalog.load().default_persona_id)
        with patch.dict(os.environ, {"AZURE_SEARCH_ENDPOINT": "https://s.example.com", "AZURE_SEARCH_INDEX": ""}), \
                patch.object(search_aio, "SearchClient") as fake_client:
            smoke_realtime._search_context_for(persona, _FakeCredential())
        self.assertEqual(fake_client.call_args.args[1], persona.manifest.search.indexName)


def _fake_cascade_model_catalog(cascade_chat_id, transcription_id="gpt-4o-transcribe", tts_id="gpt-4o-mini-tts",
                                deployments=None):
    """A ModelCatalog fixture with a cascade chat model and models.cascade audio config
    (#302), built from an explicit config/env, never the repo's real config.yaml."""
    deployments = deployments if deployments is not None else {
        cascade_chat_id: "dep-chat", transcription_id: "dep-stt", tts_id: "dep-tts",
    }
    return model_catalog.ModelCatalog.load(
        config={"models": {
            "catalog": [{"id": cascade_chat_id, "pipeline": "cascade", "label": "Fake Cascade Chat",
                        "toolCalling": True}],
            "cascade": {"transcription": transcription_id, "tts": tts_id},
        }},
        environ={"AZURE_AI_MODEL_DEPLOYMENTS": json.dumps(deployments)},
    )


class _FakeChatCompletion:
    """Mimics azure-ai-inference's ChatCompletionsClient.complete() return shape."""

    def __init__(self, tool_calls=(), content=""):
        message = SimpleNamespace(tool_calls=list(tool_calls) or None, content=content)
        self.choices = [SimpleNamespace(message=message)]


class _FakeChatCompletionsClient:
    """Replaces azure.ai.inference.aio.ChatCompletionsClient (#302): no live Foundry call."""

    instances: list["_FakeChatCompletionsClient"] = []
    response: _FakeChatCompletion = _FakeChatCompletion(content="Sure thing!")
    error: Exception | None = None

    def __init__(self, *, endpoint, credential, credential_scopes):
        self.endpoint = endpoint
        self.credential = credential
        self.credential_scopes = credential_scopes
        self.complete_calls: list[dict] = []
        self.closed = False
        type(self).instances.append(self)

    async def complete(self, *, messages, model, tools):
        self.complete_calls.append({"messages": messages, "model": model, "tools": tools})
        if type(self).error is not None:
            raise type(self).error
        return type(self).response

    async def close(self):
        self.closed = True


class CascadeChatCompletionTests(_FakeServerCase):
    """_cascade_chat_completion (#302 step 2): passes on a tool call OR non-empty text."""

    def setUp(self):
        _FakeChatCompletionsClient.instances = []
        _FakeChatCompletionsClient.response = _FakeChatCompletion(content="Sure thing!")
        _FakeChatCompletionsClient.error = None

    async def _call(self, persona):
        import azure.ai.inference.aio as inference_aio
        credential = SimpleNamespace(get_token=lambda *_a, **_k: SimpleNamespace(token="tok", expires_on=0))
        with patch.object(inference_aio, "ChatCompletionsClient", _FakeChatCompletionsClient):
            return await smoke_realtime._cascade_chat_completion(
                "https://foundry.example.com", "dep-chat", persona, [], "What drinks do you have?", credential, 5)

    async def test_text_only_response_passes(self):
        persona = persona_loader.PersonaCatalog.load().get(persona_loader.PersonaCatalog.load().default_persona_id)
        ok, detail = await self._call(persona)
        self.assertTrue(ok)
        self.assertIn("text:", detail)
        self.assertTrue(_FakeChatCompletionsClient.instances[0].closed)

    async def test_tool_call_response_passes(self):
        persona = persona_loader.PersonaCatalog.load().get(persona_loader.PersonaCatalog.load().default_persona_id)
        tool_call = SimpleNamespace(function=SimpleNamespace(name="search"))
        _FakeChatCompletionsClient.response = _FakeChatCompletion(tool_calls=[tool_call])
        ok, detail = await self._call(persona)
        self.assertTrue(ok)
        self.assertIn("tool_call(s): search", detail)

    async def test_neither_tool_call_nor_text_fails(self):
        persona = persona_loader.PersonaCatalog.load().get(persona_loader.PersonaCatalog.load().default_persona_id)
        _FakeChatCompletionsClient.response = _FakeChatCompletion(content="")
        ok, detail = await self._call(persona)
        self.assertFalse(ok)
        self.assertIn("neither a tool call nor any text", detail)


class RunCascadeTests(_FakeServerCase):
    """run_cascade (#302 step 2): STT -> chat completion -> TTS, end to end against fakes (no
    live Azure). The fake realtime server also serves the cascade audio endpoints, since a real
    cascade deployment reuses the realtime resource for its audio models too."""

    def setUp(self):
        _FakeChatCompletionsClient.instances = []
        _FakeChatCompletionsClient.response = _FakeChatCompletion(content="Sure thing!")
        _FakeChatCompletionsClient.error = None

    def _persona(self):
        catalog = persona_loader.PersonaCatalog.load()
        return catalog.get(catalog.default_persona_id)

    async def _run(self, persona=None, catalog=None, transcript=None, cascade_id=None):
        import azure.ai.inference.aio as inference_aio
        persona = persona or self._persona()
        cascade_id = cascade_id or (persona.manifest.models.cascade.default
                                    if persona.manifest.models.cascade is not None else None)
        catalog = catalog or _fake_cascade_model_catalog(cascade_id)
        self.fake.cascade_transcript = (
            transcript if transcript is not None else smoke_realtime._transcription_phrase_for(persona))
        credential = SimpleNamespace(
            get_token=self._async_get_token("tok"))
        with patch.object(smoke_realtime, "get_cascade_credential", return_value=credential), \
                patch.object(inference_aio, "ChatCompletionsClient", _FakeChatCompletionsClient):
            return await smoke_realtime.run_cascade(
                self.endpoint, "gpt-realtime-2.1", audio_endpoint=self.endpoint,
                foundry_endpoint="https://foundry.example.com", persona=persona, model_catalog=catalog,
                timeout=5, headers={}, tenant_id=None, subscription_id=None)

    @staticmethod
    def _async_get_token(token_value):
        async def get_token(*_a, **_k):
            return SimpleNamespace(token=token_value, expires_on=0)
        return get_token

    async def test_passes_end_to_end_against_fakes(self):
        code = await self._run()
        self.assertEqual(code, 0)
        self.assertTrue(any(a and a.startswith("Bearer ") for a in self.fake.cascade_auth))

    async def test_bad_transcript_fails(self):
        code = await self._run(transcript="Sure, I can't place the order for you.")
        self.assertEqual(code, 1)

    async def test_no_cascade_pipeline_on_persona_is_a_smoke_error(self):
        persona = self._persona()
        no_cascade_manifest = persona.manifest.model_copy(update={"models": persona.manifest.models.model_copy(
            update={"cascade": None})})
        no_cascade_persona = persona_loader.Persona(persona.id, persona.pack_dir, no_cascade_manifest)
        with self.assertRaises(smoke_realtime.SmokeError):
            await self._run(persona=no_cascade_persona, catalog=_fake_cascade_model_catalog("gpt-5-mini"))

    async def test_missing_audio_catalog_config_is_a_smoke_error(self):
        persona = self._persona()
        catalog = model_catalog.ModelCatalog.load(
            config={"models": {"catalog": [{"id": persona.manifest.models.cascade.default, "pipeline": "cascade",
                                           "label": "x", "toolCalling": True}]}},
            environ={"AZURE_AI_MODEL_DEPLOYMENTS": json.dumps({persona.manifest.models.cascade.default: "dep"})},
        )
        with self.assertRaises(smoke_realtime.SmokeError):
            await self._run(catalog=catalog)

    async def test_missing_deployment_mapping_is_a_smoke_error(self):
        persona = self._persona()
        catalog = _fake_cascade_model_catalog(persona.manifest.models.cascade.default, deployments={})
        with self.assertRaises(smoke_realtime.SmokeError):
            await self._run(catalog=catalog)

    async def test_no_foundry_endpoint_is_a_smoke_error(self):
        import azure.ai.inference.aio as inference_aio
        persona = self._persona()
        catalog = _fake_cascade_model_catalog(persona.manifest.models.cascade.default)
        credential = SimpleNamespace(get_token=self._async_get_token("tok"))
        with patch.object(smoke_realtime, "get_cascade_credential", return_value=credential), \
                patch.object(inference_aio, "ChatCompletionsClient", _FakeChatCompletionsClient), \
                self.assertRaises(smoke_realtime.SmokeError):
            await smoke_realtime.run_cascade(
                self.endpoint, "gpt-realtime-2.1", audio_endpoint=self.endpoint, foundry_endpoint="",
                persona=persona, model_catalog=catalog, timeout=5, headers={}, tenant_id=None, subscription_id=None)


class CascadeCredentialTests(unittest.IsolatedAsyncioTestCase):
    """get_cascade_credential (#302): always a real Entra ID token, never an API key -- the
    cascade pipeline's own design (section 7.4)."""

    async def test_no_api_key_branch_exists(self):
        """Unlike get_search_credential/get_auth_headers, setting AZURE_SEARCH_API_KEY-style env
        vars must have no effect: the cascade pipeline never accepts an API key."""
        class FakeCred:
            async def get_token(self, *_a, **_k):
                return SimpleNamespace(token="tok", expires_on=0)

        with patch.object(smoke_realtime, "_async_credentials", return_value=[FakeCred()]):
            credential = await smoke_realtime.get_cascade_credential()
        self.assertIsInstance(credential, FakeCred)

    async def test_all_candidates_failing_is_a_smoke_error(self):
        class FailingCred:
            async def get_token(self, *_a, **_k):
                raise RuntimeError("no token")

        with patch.object(smoke_realtime, "_async_credentials", return_value=[FailingCred()]):
            with self.assertRaises(smoke_realtime.SmokeError):
                await smoke_realtime.get_cascade_credential()


class CascadeToolDefinitionsTests(unittest.TestCase):
    """_cascade_tool_definitions (#302): tools.py's flat Realtime-API schemas converted to the
    nested Chat-Completions-style definitions azure-ai-inference expects."""

    def test_converts_name_description_and_parameters(self):
        schemas = [{"name": "search", "description": "Search the menu.",
                   "parameters": {"type": "object", "properties": {"query": {"type": "string"}}}}]
        defs = smoke_realtime._cascade_tool_definitions(schemas)
        self.assertEqual(len(defs), 1)
        self.assertEqual(defs[0].function.name, "search")
        self.assertEqual(defs[0].function.description, "Search the menu.")
        self.assertEqual(defs[0].function.parameters, schemas[0]["parameters"])

    def test_empty_schema_list_is_empty(self):
        self.assertEqual(smoke_realtime._cascade_tool_definitions([]), [])


def _isolated_main(argv, env, azd):
    """Run main() with only `env` set for the names it reads, `azd` as the azd
    env, and a fake run(); return (exit code, run kwargs, env seen by run).

    `seen["kwargs"]` also carries the positional (endpoint, deployment) args
    under "endpoint"/"deployment" keys (#83, P2-14: lets --model's resolved
    deployment be asserted on)."""
    names = ["AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_OPENAI_EASTUS2_ENDPOINT",
             "AZURE_OPENAI_REALTIME_DEPLOYMENT", "AZURE_OPENAI_EASTUS2_API_KEY", *CLEAN_ENV]
    seen = {}

    async def fake_run(endpoint, deployment, **kwargs):
        seen["kwargs"] = {**kwargs, "endpoint": endpoint, "deployment": deployment}
        seen["env"] = {n: os.environ.get(n) for n in CLEAN_ENV}
        return 0
    saved = {n: os.environ.pop(n, None) for n in names}
    try:
        os.environ.update(env)
        with patch.object(smoke_realtime, "_azd_env_values", return_value=azd), \
                patch.object(smoke_realtime, "run", fake_run):
            code = smoke_realtime.main(argv)
    finally:
        for n in names:
            os.environ.pop(n, None)
            if saved[n] is not None:
                os.environ[n] = saved[n]
    return code, seen.get("kwargs"), seen.get("env")


def _isolated_main_cascade(argv, env, azd):
    """Same as _isolated_main (#302), but for `--pipeline cascade`: fakes run_cascade() and
    get_auth_headers() (main() builds headers before dispatching to either pipeline), and
    returns (exit code, run_cascade kwargs)."""
    names = ["AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_OPENAI_EASTUS2_ENDPOINT",
             "AZURE_OPENAI_REALTIME_DEPLOYMENT", "AZURE_OPENAI_EASTUS2_API_KEY",
             "AZURE_AI_FOUNDRY_ENDPOINT", *CLEAN_ENV]
    seen = {}

    async def fake_run_cascade(endpoint, deployment, **kwargs):
        seen["kwargs"] = {**kwargs, "endpoint": endpoint, "deployment": deployment}
        return 0
    saved = {n: os.environ.pop(n, None) for n in names}
    try:
        os.environ.update(env)
        with patch.object(smoke_realtime, "_azd_env_values", return_value=azd), \
                patch.object(smoke_realtime, "get_auth_headers", return_value={"api-key": "k"}), \
                patch.object(smoke_realtime, "run_cascade", fake_run_cascade):
            code = smoke_realtime.main(argv)
    finally:
        for n in names:
            os.environ.pop(n, None)
            if saved[n] is not None:
                os.environ[n] = saved[n]
    return code, seen.get("kwargs")


class MainTests(unittest.TestCase):

    def test_missing_settings_exit_2(self):
        code, _, _ = _isolated_main([], {}, {})
        self.assertEqual(code, 2)

    def test_smoke_error_exit_2(self):
        async def boom(*_a, **_k):
            raise smoke_realtime.SmokeError("no token")
        with patch.object(smoke_realtime, "run", boom), \
                patch.object(smoke_realtime, "_azd_env_values", return_value={}):
            self.assertEqual(smoke_realtime.main(["--endpoint", "https://x", "--deployment", "d"]), 2)

    def test_azd_values_fill_unset_app_settings(self):
        azd = {"AZURE_OPENAI_EASTUS2_ENDPOINT": "https://x", "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
               "AZURE_OPENAI_REALTIME_REASONING_EFFORT": "medium", "AZURE_OPENAI_REALTIME_REASONING_MODEL": "false",
               "AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL": "whisper-1", "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "cedar"}
        code, _, env = _isolated_main([], {}, azd)
        self.assertEqual(code, 0)
        app_settings = [n for n in CLEAN_ENV if n != "AZURE_OPENAI_EASTUS2_API_KEY"]
        self.assertEqual(env, {**{n: azd[n] for n in app_settings}, "AZURE_OPENAI_EASTUS2_API_KEY": None})


class TenantTests(unittest.TestCase):
    """The token must come from the resource's tenant, not the active `az` or `azd` default."""

    AZD = {"AZURE_OPENAI_EASTUS2_ENDPOINT": "https://x", "AZURE_OPENAI_REALTIME_DEPLOYMENT": "d",
           "AZURE_TENANT_ID": "azd-tenant", "AZURE_SUBSCRIPTION_ID": "azd-sub"}
    EXPLICIT = ["--endpoint", "https://x", "--deployment", "d"]

    def _main_identity(self, argv, env, azd):
        code, kwargs, _ = _isolated_main(argv, env, azd)
        self.assertEqual(code, 0)
        return kwargs.get("tenant_id"), kwargs.get("subscription_id")

    def test_azd_env_identity_is_used(self):
        self.assertEqual(self._main_identity([], {}, self.AZD), ("azd-tenant", "azd-sub"))

    def test_azd_env_identity_is_used_even_with_explicit_endpoint_and_deployment(self):
        self.assertEqual(self._main_identity(self.EXPLICIT, {}, self.AZD), ("azd-tenant", "azd-sub"))

    def test_env_then_cli_override_azd(self):
        env = {"AZURE_TENANT_ID": "env-tenant", "AZURE_SUBSCRIPTION_ID": "env-sub"}
        self.assertEqual(self._main_identity([], env, self.AZD), ("env-tenant", "env-sub"))
        self.assertEqual(self._main_identity(["--tenant", "cli-tenant", "--subscription", "cli-sub"], env, self.AZD),
                         ("cli-tenant", "cli-sub"))

    def test_each_value_falls_back_to_azd_independently(self):
        self.assertEqual(self._main_identity(["--tenant", "cli-tenant"], {}, self.AZD), ("cli-tenant", "azd-sub"))
        self.assertEqual(self._main_identity([], {"AZURE_SUBSCRIPTION_ID": "env-sub"}, self.AZD),
                         ("azd-tenant", "env-sub"))

    def test_nothing_anywhere_is_none(self):
        self.assertEqual(self._main_identity(self.EXPLICIT, {}, {}), (None, None))

    def test_api_key_skips_identity(self):
        self.assertEqual(self._main_identity([], {"AZURE_OPENAI_EASTUS2_API_KEY": "k"}, self.AZD), (None, None))

    def test_run_passes_identity_to_auth(self):
        seen = {}

        def fake_auth(*args):
            seen["args"] = args
            raise smoke_realtime.SmokeError("stop here")
        with patch.object(smoke_realtime, "get_auth_headers", fake_auth), \
                self.assertRaises(smoke_realtime.SmokeError):
            asyncio.run(smoke_realtime.run("https://x", "d", voice=None, timeout=1, skip_transcription=True,
                                           tenant_id="t", subscription_id="s"))
        self.assertEqual(seen["args"], ("t", "s"))

    def _auth(self, tenant_id, subscription_id, fail=(), api_key=""):
        """Returns (headers or SmokeError, credentials built, credentials asked for a token)."""
        built, asked = [], []

        class FakeCred:
            def __init__(self, kind, **kwargs):
                self.kind = kind
                built.append((kind, kwargs.get("tenant_id") or kwargs.get("subscription")))

            def get_token(self, *scopes, **_kw):
                asked.append((self.kind, scopes))
                if self.kind in fail:
                    raise RuntimeError(f"{self.kind} said no\nsecond line")
                return SimpleNamespace(token=f"tok-{self.kind}", expires_on=0)

        def az(**k):
            return FakeCred("az-sub" if k.get("subscription") else "az", **k)

        import azure.identity as identity
        with patch.dict(os.environ, {"AZURE_OPENAI_EASTUS2_API_KEY": api_key}), \
                patch.object(identity, "AzureDeveloperCliCredential", lambda **k: FakeCred("azd", **k)), \
                patch.object(identity, "AzureCliCredential", az), \
                patch.object(identity, "DefaultAzureCredential", lambda **k: FakeCred("default", **k)):
            try:
                result = smoke_realtime.get_auth_headers(tenant_id, subscription_id)
            except smoke_realtime.SmokeError as exc:
                result = exc
        return result, built, [kind for kind, _ in asked], [s for _, s in asked]

    def test_subscription_first_then_tenant_pinned_clis(self):
        headers, built, asked, scopes = self._auth("tenant-x", "sub-y")
        self.assertEqual(built, [("az-sub", "sub-y"), ("azd", "tenant-x"), ("az", "tenant-x")])
        self.assertEqual(asked, ["az-sub"])
        self.assertEqual(scopes, [("https://cognitiveservices.azure.com/.default",)])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-az-sub"})

    def test_hard_failure_moves_on_to_the_next_credential(self):
        headers, _, asked, _ = self._auth("tenant-x", "sub-y", fail=("az-sub", "azd"))
        self.assertEqual(asked, ["az-sub", "azd", "az"])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-az"})

    def test_all_failing_reports_every_credential(self):
        err, _, asked, _ = self._auth("tenant-x", "sub-y", fail=("az-sub", "azd", "az"))
        self.assertIsInstance(err, smoke_realtime.SmokeError)
        self.assertEqual(asked, ["az-sub", "azd", "az"])
        self.assertIn("az-sub said no", str(err))
        self.assertIn("azd said no", str(err))
        self.assertNotIn("second line", str(err))

    def test_tenant_only_pins_both_clis(self):
        _, built, _, _ = self._auth("tenant-x", None)
        self.assertEqual(built, [("azd", "tenant-x"), ("az", "tenant-x")])

    def test_subscription_only(self):
        _, built, _, _ = self._auth(None, "sub-y")
        self.assertEqual(built, [("az-sub", "sub-y")])

    def test_nothing_falls_back_to_default_credential(self):
        headers, built, _, _ = self._auth(None, None)
        self.assertEqual(built, [("default", None)])
        self.assertEqual(headers, {"Authorization": "Bearer " + "tok-default"})

    def test_api_key_wins(self):
        headers, built, _, _ = self._auth("tenant-x", "sub-y", api_key="secret")
        self.assertEqual(headers, {"api-key": "secret"})
        self.assertEqual(built, [])

    def test_no_args_still_works_for_the_benchmark(self):
        # scripts/benchmark_reasoning.py calls get_auth_headers() with no arguments.
        headers, built, _, _ = self._auth(None, None)
        self.assertEqual(built, [("default", None)])
        self.assertIn("Authorization", headers)


class PersonaResolutionTests(unittest.TestCase):
    """--persona (#83, P2-14) picks a real pack from the SAME PersonaCatalog app.create_app()
    uses, or fails closed -- never a silent hardcoded-brand fallback."""

    def test_no_persona_is_none(self):
        self.assertIsNone(smoke_realtime.resolve_persona(None))

    def test_every_enabled_pack_resolves_by_id(self):
        catalog = persona_loader.PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = smoke_realtime.resolve_persona(persona_id)
                self.assertEqual(persona.id, persona_id)

    def test_unknown_persona_is_a_smoke_error(self):
        with self.assertRaises(smoke_realtime.SmokeError):
            smoke_realtime.resolve_persona("not-a-real-persona-id")


class TranscriptionPhraseTests(unittest.TestCase):
    """The live-transcription phrase (#83, P2-14) names each persona's own menu, never a
    single hardcoded brand's -- so every pack's smoke check exercises its own vocabulary."""

    def test_no_persona_uses_the_brand_neutral_default(self):
        self.assertEqual(smoke_realtime._transcription_phrase_for(None),
                         smoke_realtime._DEFAULT_TRANSCRIPTION_PHRASE)

    def test_every_pack_builds_a_phrase_from_its_own_menu(self):
        import menu_utils
        catalog = persona_loader.PersonaCatalog.load()
        for persona_id in catalog.ids:
            with self.subTest(persona_id):
                persona = catalog.get(persona_id)
                menu = menu_utils.get_catalog_for_persona(persona)
                names = [fields["name"] for fields in menu.item_fields.values() if fields.get("name")]
                phrase = smoke_realtime._transcription_phrase_for(persona)
                self.assertIn(names[0], phrase)
                self.assertIn(names[1], phrase)


class BuildMiddleTierPersonaTests(unittest.TestCase):
    """persona (#83, P2-14) swaps PromptLoader's pack, so each persona's own prompt/tools load."""

    def test_no_persona_matches_the_legacy_single_brand_default(self):
        default_rtmt = smoke_realtime.build_middle_tier("https://x", "d", environ={})
        catalog = persona_loader.PersonaCatalog.load()
        default_persona = catalog.get(catalog.default_persona_id)
        persona_rtmt = smoke_realtime.build_middle_tier("https://x", "d", environ={}, persona=default_persona)
        self.assertEqual(default_rtmt.system_message, persona_rtmt.system_message)

    def test_each_pack_loads_its_own_system_prompt(self):
        catalog = persona_loader.PersonaCatalog.load()
        messages = {
            persona_id: smoke_realtime.build_middle_tier(
                "https://x", "d", environ={}, persona=catalog.get(persona_id)).system_message
            for persona_id in catalog.ids
        }
        self.assertEqual(len(set(messages.values())), len(messages), "every pack should have a distinct prompt")


class ModelDeploymentResolutionTests(unittest.TestCase):
    """--model (#83, P2-14) resolves via the SAME catalog and persona-allowed rule
    app.create_app() uses (design doc section 7.3: catalog and deployment and persona-allowed)."""

    def test_no_model_is_none(self):
        self.assertIsNone(smoke_realtime.resolve_model_deployment(None, None))

    def test_unknown_catalog_id_is_a_smoke_error(self):
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: _fake_model_catalog())):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime.resolve_model_deployment("not-a-real-model-id", None)

    def test_unmapped_deployment_is_a_smoke_error(self):
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: _fake_model_catalog())):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime.resolve_model_deployment("gpt-realtime-2.1", None)

    def test_mapped_deployment_resolves(self):
        fake = _fake_model_catalog({"gpt-realtime-2.1": "my-realtime-deployment"})
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: fake)):
            deployment = smoke_realtime.resolve_model_deployment("gpt-realtime-2.1", None)
        self.assertEqual(deployment, "my-realtime-deployment")

    def test_model_not_allowed_for_persona_is_a_smoke_error(self):
        catalog = persona_loader.PersonaCatalog.load()
        persona = catalog.get(catalog.default_persona_id)
        fake = _fake_model_catalog({"phi-4": "some-cascade-deployment"})
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: fake)):
            with self.assertRaises(smoke_realtime.SmokeError):
                smoke_realtime.resolve_model_deployment("phi-4", persona)


class CliPersonaAndModelTests(unittest.TestCase):
    """--persona/--model (#83, P2-14) reach run() and override --deployment when given."""

    def test_persona_flag_is_threaded_into_run(self):
        persona_id = persona_loader.PersonaCatalog.load().default_persona_id
        code, kwargs, _ = _isolated_main(["--endpoint", "https://x", "--deployment", "d",
                                          "--persona", persona_id], {}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["persona"].id, persona_id)

    def test_no_persona_flag_leaves_persona_none(self):
        code, kwargs, _ = _isolated_main(["--endpoint", "https://x", "--deployment", "d"], {}, {})
        self.assertEqual(code, 0)
        self.assertIsNone(kwargs["persona"])

    def test_unknown_persona_flag_exits_2(self):
        code, _, _ = _isolated_main(["--endpoint", "https://x", "--deployment", "d",
                                     "--persona", "not-a-real-persona-id"], {}, {})
        self.assertEqual(code, 2)

    def test_model_flag_overrides_deployment(self):
        fake = _fake_model_catalog({"gpt-realtime-2.1": "resolved-deployment"})
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: fake)):
            code, kwargs, _ = _isolated_main(
                ["--endpoint", "https://x", "--deployment", "ignored", "--model", "gpt-realtime-2.1"], {}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["deployment"], "resolved-deployment")

    def test_unresolvable_model_flag_exits_2(self):
        with patch.object(smoke_realtime, "ModelCatalog", SimpleNamespace(load=lambda: _fake_model_catalog())):
            code, _, _ = _isolated_main(
                ["--endpoint", "https://x", "--deployment", "d", "--model", "not-a-real-model-id"], {}, {})
        self.assertEqual(code, 2)


class CascadeCliTests(unittest.TestCase):
    """--pipeline cascade (#302 step 2): dispatches to run_cascade() instead of run(), resolving
    an omitted --persona to the persona catalog's own default (cascade always needs a real
    persona's models.cascade/prompts/tools -- never a single hardcoded brand)."""

    def test_default_pipeline_is_realtime(self):
        code, kwargs, _ = _isolated_main(["--endpoint", "https://x", "--deployment", "d"], {}, {})
        self.assertEqual(code, 0)
        self.assertIsNotNone(kwargs)

    def test_cascade_pipeline_dispatches_to_run_cascade(self):
        code, kwargs = _isolated_main_cascade(
            ["--endpoint", "https://x", "--deployment", "d", "--pipeline", "cascade",
             "--foundry-endpoint", "https://foundry.example.com"], {}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["endpoint"], "https://x")
        self.assertEqual(kwargs["deployment"], "d")
        self.assertEqual(kwargs["foundry_endpoint"], "https://foundry.example.com")
        self.assertEqual(kwargs["audio_endpoint"], "https://x")

    def test_cascade_pipeline_with_no_persona_flag_uses_catalog_default(self):
        default_id = persona_loader.PersonaCatalog.load().default_persona_id
        code, kwargs = _isolated_main_cascade(
            ["--endpoint", "https://x", "--deployment", "d", "--pipeline", "cascade",
             "--foundry-endpoint", "https://foundry.example.com"], {}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["persona"].id, default_id)

    def test_cascade_pipeline_with_explicit_persona_flag(self):
        catalog = persona_loader.PersonaCatalog.load()
        other_id = next(iter(catalog.ids))
        code, kwargs = _isolated_main_cascade(
            ["--endpoint", "https://x", "--deployment", "d", "--pipeline", "cascade", "--persona", other_id,
             "--foundry-endpoint", "https://foundry.example.com"], {}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["persona"].id, other_id)

    def test_foundry_endpoint_falls_back_to_env(self):
        code, kwargs = _isolated_main_cascade(
            ["--endpoint", "https://x", "--deployment", "d", "--pipeline", "cascade"],
            {"AZURE_AI_FOUNDRY_ENDPOINT": "https://from-env.example.com"}, {})
        self.assertEqual(code, 0)
        self.assertEqual(kwargs["foundry_endpoint"], "https://from-env.example.com")

    def test_unknown_persona_flag_with_cascade_pipeline_exits_2(self):
        code, _ = _isolated_main_cascade(
            ["--endpoint", "https://x", "--deployment", "d", "--pipeline", "cascade",
             "--persona", "not-a-real-persona-id"], {}, {})
        self.assertEqual(code, 2)


class SkipSearchCliTests(unittest.TestCase):
    """--skip-search (#302 step 1): threaded into run() as the skip_search kwarg."""

    def test_default_is_false(self):
        code, kwargs, _ = _isolated_main(["--endpoint", "https://x", "--deployment", "d"], {}, {})
        self.assertEqual(code, 0)
        self.assertFalse(kwargs["skip_search"])

    def test_flag_sets_skip_search_true(self):
        code, kwargs, _ = _isolated_main(
            ["--endpoint", "https://x", "--deployment", "d", "--skip-search"], {}, {})
        self.assertEqual(code, 0)
        self.assertTrue(kwargs["skip_search"])


class PostdeployHookTests(unittest.TestCase):
    """An anonymous external `azd up` must never fail because of the smoke check."""

    def test_azure_yaml_postdeploy_is_non_fatal_and_non_interactive(self):
        hooks = yaml.safe_load((REPO / "azure.yaml").read_text(encoding="utf-8"))["hooks"]
        for platform, script in (("windows", "./scripts/smoke_realtime.ps1"), ("posix", "./scripts/smoke_realtime.sh")):
            with self.subTest(platform):
                hook = hooks["postdeploy"][platform]
                self.assertEqual(hook["run"], script)
                self.assertIs(hook["continueOnError"], True)
                self.assertIs(hook["interactive"], False)
                self.assertTrue((REPO / script).is_file())

    def _wrapper_text(self, name):
        return (REPO / "scripts" / name).read_text(encoding="utf-8")

    def test_wrappers_always_exit_0(self):
        for name in ("smoke_realtime.ps1", "smoke_realtime.sh"):
            with self.subTest(name):
                exits = [line.strip() for line in self._wrapper_text(name).splitlines()
                         if line.strip().startswith("exit")]
                self.assertTrue(exits)
                self.assertEqual(set(exits), {"exit 0"})
                self.assertIn("SONIC_SKIP_REALTIME_SMOKE", self._wrapper_text(name))

    def test_sh_wrapper_is_committed_with_lf(self):
        eol = subprocess.run(["git", "ls-files", "--eol", "scripts/smoke_realtime.sh"], cwd=REPO,
                             capture_output=True, text=True).stdout
        if eol:  # tracked
            self.assertIn("i/lf", eol)
            self.assertIn("eol=lf", eol)

    def test_wrappers_loop_over_personas_and_both_pipelines(self):
        """#302: every persona (discovered the same way the app itself does, never a hardcoded
        brand list) gets BOTH a realtime and a cascade pipeline pass."""
        for name in ("smoke_realtime.ps1", "smoke_realtime.sh"):
            with self.subTest(name):
                text = self._wrapper_text(name)
                self.assertIn("PersonaCatalog", text)
                self.assertIn("--pipeline", text)
                self.assertIn("realtime", text)
                self.assertIn("cascade", text)

    def test_wrapper_persona_discovery_matches_the_real_catalog(self):
        """The .sh wrapper's own persona-discovery one-liner must resolve the SAME ids
        PersonaCatalog.load() resolves -- never silently diverge from the app's own discovery."""
        catalog_ids = set(persona_loader.PersonaCatalog.load().ids)
        result = subprocess.run(
            [sys.executable, "-c",
             "import sys; sys.path.insert(0, 'app/backend'); "
             "from persona_loader import PersonaCatalog; print(' '.join(PersonaCatalog.load().ids))"],
            cwd=REPO, capture_output=True, text=True)
        self.assertEqual(set(result.stdout.split()), catalog_ids)


if __name__ == "__main__":
    unittest.main()
