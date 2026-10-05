"""Live voice evaluation for #304: pronunciation lexicon + mandatory server-composed read-back.

For each persona pack (or just the ones given via --persona), opens N live Azure OpenAI
Realtime sessions (default N=3) against a real deployment, scripts a guest order -- one item
covered by that persona's own `pronunciations` lexicon or `spokenName` override when one
exists (falling back to any two real menu items otherwise), adds a second item as a
mid-order change, tells the carhop it is done, and for the assistant's final turn reports:

  - **Read-back presence**: whether the assistant's spoken response actually says something
    (via the realtime API's own `response.audio_transcript.done` transcription of its
    synthesized audio), after `get_order` fires per the persona's `ORDER_READBACK` prompt
    rule, rather than staying silent or only emitting a total.
  - **Lexicon spelling**: what the transcript spells any lexicon-covered or spokenName-
    overridden item as. Speech-to-text normalizes spoken audio back to conventional
    spelling, so a transcript reading "Munchkins" does not by itself prove mispronunciation
    -- this script reports the transcript text next to the item's `spokenReadBack`-composed
    expected wording so a human reviewer can judge the actual audio (saved client-side, see
    --save-audio) against it. It does not itself assert pass/fail on pronunciation quality.

This is a REPORTING tool, not a pass/fail gate: it prints (and, with --output, writes a JSON
report of) what each session's assistant actually said, for the coordinator to review against
the #304 brief. It never asserts order or lexicon "correctness" the way the conformance suite
does -- that's `SpokenReadBackConformanceTests` / `CascadePronunciationLexiconConformanceTests`'s
job (those run against a scripted fake model and assert exact text; this runs against the real
realtime model and reports what it actually said, which only a human can fully judge for
*pronunciation*).

Shares its live-connection plumbing (auth, session.update construction, persona/model
resolution) with `scripts/smoke_realtime.py` -- see that script's own docstring for the full
auth precedence and env vars. In short: AZURE_OPENAI_EASTUS2_ENDPOINT /
AZURE_OPENAI_REALTIME_DEPLOYMENT (or --endpoint/--deployment), AZURE_OPENAI_EASTUS2_API_KEY if
set, else an Entra ID token.

Tool calls (`update_order`, `get_order`) are executed for real against this process's own
`tools.update_order` / `tools.get_order` -- the SAME functions the production middle tier
calls -- bound to a fresh `order_state_singleton` session per live realtime session, so the
`spokenReadBack` text spoken back is the real, server-composed field under test, not a stub.

Usage (from the repo root, with `az login` / `azd auth login` done):

    python scripts/eval_voice.py                                   # every enabled persona, N=3
    python scripts/eval_voice.py --persona <persona-id> --n 5
    python scripts/eval_voice.py --endpoint https://<aoai>.openai.azure.com/ --deployment gpt-realtime-2.1
    python scripts/eval_voice.py --output eval_voice_report.json

NOT part of the automated test suite and NOT run by this session -- it needs a live,
deployed Azure OpenAI Realtime endpoint and real spend. The coordinator runs it, by hand,
against a real deployment, and reviews the report (and, with --save-audio, the saved clips).

Exit codes: 0 = every scripted session completed and produced a report (read-back presence
and lexicon spelling are reported, not gated); 1 = a session errored before completion
(connection, auth, or an unexpected event); 2 = could not run at all (missing
endpoint/deployment or no personas to evaluate).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import dataclasses
import json
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
BACKEND_DIR = REPO_ROOT / "app" / "backend"
sys.path.insert(0, str(BACKEND_DIR))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import aiohttp  # noqa: E402

# Reuse smoke_realtime's auth/session/persona-resolution plumbing instead of duplicating it --
# this script's only new logic is the scripted multi-turn order conversation and its report.
import smoke_realtime as sr  # noqa: E402

import menu_utils  # noqa: E402
import tools  # noqa: E402
from order_state import order_state_singleton  # noqa: E402
from persona_loader import Persona, PersonaCatalog, PersonaValidationError  # noqa: E402

DEFAULT_SESSIONS_PER_PERSONA = 3
DEFAULT_TIMEOUT = 30.0
# Phrases the final turn's transcript is checked for, to flag an outright-missing read-back
# (an empty or near-empty transcript, or one that never names an item) even though judging
# *correctness* of the read-back's wording is left to the conformance suite, not this script.
# #313 (Rick's review, eval_voice.py defect list): split into two buckets instead of one flat
# list -- a reply that says only "Your total is $4.31" used to satisfy `has_readback` on the
# "total" hint alone, which is EXACTLY the total-only regression #304/#313 are about. A real
# read-back must name the item(s) (one of the `_ITEM_HINTS`) *and* state the total (one of the
# `_TOTAL_HINTS`).
_ITEM_HINTS = ("here's your order", "here is your order", "i have", "you have")
_TOTAL_HINTS = ("total",)


class EvalVoiceError(Exception):
    """Could not run at all (see module docstring exit code 2)."""


@dataclasses.dataclass
class TurnResult:
    guest_text: str
    assistant_transcript: str
    tool_calls: list[str]


@dataclasses.dataclass
class SessionReport:
    persona_id: str
    session_index: int
    scripted_items: tuple[str, str]
    turns: list[TurnResult]
    expected_readback: str = ""
    error: str | None = None

    @property
    def final_transcript(self) -> str:
        return self.turns[-1].assistant_transcript if self.turns else ""

    @property
    def has_readback(self) -> bool:
        transcript = self.final_transcript.lower()
        mentions_items = any(hint in transcript for hint in _ITEM_HINTS)
        mentions_total = any(hint in transcript for hint in _TOTAL_HINTS)
        return mentions_items and mentions_total

    def to_dict(self) -> dict:
        return {
            "persona": self.persona_id,
            "session_index": self.session_index,
            "scripted_items": list(self.scripted_items),
            "has_readback": self.has_readback if not self.error else None,
            "final_transcript": self.final_transcript,
            # #313: the docstring promises the transcript is reported "next to the item's
            # spokenReadBack-composed expected wording" -- this was never actually populated.
            "expected_readback": self.expected_readback,
            "error": self.error,
            "turns": [dataclasses.asdict(t) for t in self.turns],
        }


@dataclasses.dataclass
class ScriptedOrder:
    """The guest-order script for one session: *primary*/*primary_size* is ordered first (a
    lexicon- or spokenName-covered item when one exists, at an explicit size so the model never
    has to ask), then resized to *resize_size* (Brian's #304 bug: order 25 Munchkins, then
    change to 10 -- the read-back after a `modify`, not just an `add`, is what's under test),
    then *secondary* is added as a second line before the guest finishes."""
    primary: str
    primary_size: str
    resize_size: str
    secondary: str
    secondary_size: str


def pick_scripted_items(persona: Persona) -> ScriptedOrder:
    """Build *persona*'s scripted order (#313, Rick's review: the old script never stated a
    size, so the model had to ask and turns 2/3 ran against an incomplete order, and it never
    exercised Brian's actual bug -- order one size, then change it, then confirm the read-back).
    *primary* prefers a lexicon- or spokenName-covered item (so the session exercises #304's
    pronunciation fields) that also has 2+ real sizes (so a genuine resize can be scripted);
    falls back to the first covered/real item and its sole size (or "") otherwise."""
    catalog = menu_utils.get_catalog_for_persona(persona)
    entries = [(fields["name"], fields) for fields in catalog.item_fields.values() if fields.get("name")]
    if not entries:
        raise EvalVoiceError(f"persona {persona.id!r} has no real menu items to script a session with")
    lexicon_keys = sorted(persona.manifest.pronunciations, key=len, reverse=True)

    def covered(name: str) -> bool:
        return name in catalog.spoken_as or any(key in name for key in lexicon_keys)

    def sizes_for(fields: dict) -> list[str]:
        return [s for s in (fields.get("sizes") or ()) if s]

    # Prefer a covered item with 2+ sizes (lets the script resize it, Brian's exact flow);
    # fall back to any covered item, then to the catalog's first item.
    primary_name, primary_fields = next(
        ((n, f) for n, f in entries if covered(n) and len(sizes_for(f)) >= 2),
        next(((n, f) for n, f in entries if covered(n)), entries[0]),
    )
    secondary_name, secondary_fields = next(((n, f) for n, f in entries if n != primary_name),
                                            (primary_name, primary_fields))

    primary_sizes = sizes_for(primary_fields)

    def _as_count(size: str) -> int | None:
        head = size.split()[0] if size else ""
        return int(head) if head.isdigit() else None

    counts = [(c, s) for s in primary_sizes if (c := _as_count(s)) is not None]
    if len(counts) >= 2:
        counts.sort(key=lambda pair: pair[0])
        primary_size, resize_size = counts[-1][1], counts[0][1]
    elif len(primary_sizes) >= 2:
        primary_size, resize_size = primary_sizes[0], primary_sizes[1]
    else:
        primary_size = primary_sizes[0] if primary_sizes else ""
        resize_size = primary_size

    secondary_sizes = sizes_for(secondary_fields)
    secondary_size = secondary_sizes[0] if secondary_sizes else ""
    return ScriptedOrder(primary_name, primary_size, resize_size, secondary_name, secondary_size)


async def _run_tool_call(session_id: str, name: str, args: dict) -> str:
    """Execute the real `tools.update_order`/`tools.get_order`/`tools._search_dispatch` for
    *name* -- the same production functions the middle tier calls -- so the read-back text
    evaluated here is the actual `spokenReadBack` field under test, not a stand-in.

    #313 (Rick's review): every persona's system prompt makes `search` MANDATORY before
    `update_order` ("ALWAYS call search BEFORE adding any item"), so a live model calls it on
    effectively every turn -- this used to raise `EvalVoiceError` on that very first call,
    aborting the whole session before the scripted order could run at all."""
    if name == "update_order":
        result = await tools.update_order(args, session_id)
    elif name == "get_order":
        result = await tools.get_order(args, session_id)
    elif name == "reset_order":
        result = await tools.reset_order(args, session_id)
    elif name == "search":
        result = await tools._search_dispatch(args, session_id)
    else:
        raise EvalVoiceError(f"unexpected tool call {name!r} during a scripted #304 eval session")
    return result.to_text()


async def _script_turn(ws, session_id: str, guest_text: str, timeout: float,
                       audio_chunks: list[bytes] | None = None) -> TurnResult:
    """Send one scripted guest turn (as text -- only the ASSISTANT's audio/transcript is
    under evaluation here, so the guest side doesn't need synthesized audio) and drive the
    tool-call loop (function_call -> real tool execution -> function_call_output ->
    response.create) until the assistant's turn finishes, collecting its spoken transcript.
    *audio_chunks*, if given (``--save-audio``), accumulates every ``response.audio.delta``'s
    base64-decoded PCM bytes so the caller can write them out for a human to actually listen to
    the pronunciation under test (the transcript alone only proves what speech-to-text heard,
    not how it sounded -- see the module docstring's "Lexicon spelling" caveat)."""
    await ws.send_str(json.dumps({
        "type": "conversation.item.create",
        "item": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": guest_text}]},
    }))
    await ws.send_str(json.dumps({"type": "response.create"}))
    transcript_parts: list[str] = []
    tool_calls: list[str] = []
    deadline = time.monotonic() + timeout
    while (remaining := deadline - time.monotonic()) > 0:
        event = await sr._next_event(ws, remaining)
        if event is None:
            continue
        etype = event.get("type")
        if etype == "response.audio_transcript.done":
            transcript_parts.append(event.get("transcript") or "")
        elif etype == "response.audio.delta" and audio_chunks is not None:
            delta = event.get("delta")
            if delta:
                audio_chunks.append(base64.b64decode(delta))
        elif etype == "response.function_call_arguments.done":
            call_id = event["call_id"]
            tool_name = event["name"]
            args = json.loads(event.get("arguments") or "{}")
            tool_calls.append(tool_name)
            output = await _run_tool_call(session_id, tool_name, args)
            await ws.send_str(json.dumps({
                "type": "conversation.item.create",
                "item": {"type": "function_call_output", "call_id": call_id, "output": output},
            }))
            await ws.send_str(json.dumps({"type": "response.create"}))
            deadline = time.monotonic() + timeout  # a tool round trip gets its own fresh timeout
        elif etype == "response.done":
            break
        elif etype == "error":
            err = (event.get("error") or {})
            raise EvalVoiceError(f"realtime error during scripted turn {guest_text!r}: {err.get('message')}")
    return TurnResult(guest_text=guest_text, assistant_transcript=" ".join(p for p in transcript_parts if p),
                      tool_calls=tool_calls)


def _write_audio_clip(output_dir: Path, persona_id: str, session_index: int, turn_index: int,
                      pcm: bytes) -> None:
    """Write raw 24kHz mono 16-bit PCM (the realtime API's `pcm16` output format) as a WAV file
    under *output_dir* -- #313 (Rick's review): `--save-audio` was documented but not
    implemented at all; this is the minimal, dependency-free WAV container so a human can play
    the clip back in any media player without needing ffmpeg/pydub installed."""
    import wave
    output_dir.mkdir(parents=True, exist_ok=True)
    path = output_dir / f"{persona_id}-{session_index}-turn{turn_index}.wav"
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)  # 16-bit
        wav_file.setframerate(24000)
        wav_file.writeframes(pcm)


async def run_one_session(persona: Persona, index: int, rtmt, url: str, headers: dict,
                          timeout: float, save_audio_dir: Path | None = None) -> SessionReport:
    scripted = pick_scripted_items(persona)
    session_id = order_state_singleton.create_session(persona=persona)
    size_phrase = f" {scripted.primary_size.title()}" if scripted.primary_size else ""
    resize_phrase = scripted.resize_size.title() if scripted.resize_size else scripted.primary_size.title()
    secondary_size_phrase = f" {scripted.secondary_size.title()}" if scripted.secondary_size else ""
    # #313 (Rick's review): the old script never stated a size (the model had to ask, so turns
    # 2/3 ran against an incomplete/empty order) and never exercised Brian's actual bug (a
    # `modify` resize, not just an `add`). This scripts that flow explicitly: order the primary
    # item at an explicit size, add a second item, then RESIZE the primary (Brian: 25 -> 10
    # count) and confirm the guest hears the full, correct read-back afterward.
    scripted_turns = [
        f"Hi, can I get a{size_phrase} {scripted.primary}, please?",
        f"Actually, can you also add a{secondary_size_phrase} {scripted.secondary}?",
        f"Can you change that {scripted.primary} to {resize_phrase}?",
        "That's everything, I'm done.",
    ]
    report = SessionReport(persona_id=persona.id, session_index=index,
                           scripted_items=(scripted.primary, scripted.secondary), turns=[])
    audio_chunks: list[bytes] = [] if save_audio_dir is not None else None
    try:
        async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
            tool_schemas = [tool.schema for tool in rtmt.tools.values()]
            # #313 (Rick's review): `session.update` was sent TWICE -- once raw, once more via
            # `sr.send_session_update` (which also waits for the `session.updated`/error echo).
            # Only the second call's confirmation was ever even checked. Send it exactly once.
            echoed, error = await sr.send_session_update(ws, rtmt.build_bootstrap_session_update(
                system_message=rtmt.system_message, tool_schemas=tool_schemas), timeout)
            if error is not None:
                raise EvalVoiceError(f"session.update rejected: {error}")
            for turn_index, guest_text in enumerate(scripted_turns):
                turn_audio: list[bytes] = [] if audio_chunks is not None else None
                result = await _script_turn(ws, session_id, guest_text, timeout, turn_audio)
                report.turns.append(result)
                if turn_audio and save_audio_dir is not None:
                    _write_audio_clip(save_audio_dir, persona.id, index, turn_index, b"".join(turn_audio))
        # The expected wording a human reviewer compares the transcript against -- the real,
        # server-composed `spokenReadBack` for this session's final order state (#313: the
        # docstring promised this but it was never populated).
        report.expected_readback = order_state_singleton.get_grouped_order_for_readback(session_id)
    except EvalVoiceError as exc:
        report.error = str(exc)
    # #313 (Rick's review): `sr._next_event` raises `sr.SmokeError` on a closed/errored socket,
    # but this previously caught only `EvalVoiceError`/`aiohttp.ClientError`/`OSError`/
    # `TimeoutError` -- a dropped connection produced an unhandled traceback instead of a
    # reported, per-session error.
    except (aiohttp.ClientError, sr.SmokeError, OSError, TimeoutError) as exc:
        report.error = f"{type(exc).__name__}: {exc}"
    finally:
        order_state_singleton.end_session(session_id) if hasattr(order_state_singleton, "end_session") else None
    return report


async def evaluate_persona(persona: Persona, n: int, endpoint: str, deployment: str, headers: dict,
                           timeout: float, save_audio_dir: Path | None = None) -> list[SessionReport]:
    rtmt = sr.build_middle_tier(endpoint, deployment, persona=persona)
    url = sr.realtime_url(endpoint, deployment)
    reports = []
    for index in range(n):
        reports.append(await run_one_session(persona, index, rtmt, url, headers, timeout, save_audio_dir))
    return reports


def _personas_to_evaluate(persona_ids: list[str] | None) -> list[Persona]:
    try:
        catalog = PersonaCatalog.load()
    except PersonaValidationError as exc:
        raise EvalVoiceError(f"could not load persona catalog: {exc}") from exc
    if persona_ids:
        try:
            return [catalog.get(pid) for pid in persona_ids]
        except KeyError as exc:
            raise EvalVoiceError(str(exc)) from exc
    # Every enabled persona, never a single hardcoded brand -- same convention as
    # smoke_realtime.py's own --persona resolution and CascadePersonaParityConformanceTests.
    all_personas = [catalog.get(pid) for pid in catalog.ids]
    if not all_personas:
        raise EvalVoiceError("persona catalog has no personas to evaluate")
    return all_personas


def print_report(reports: list[SessionReport]) -> None:
    for report in reports:
        label = f"{report.persona_id} #{report.session_index}"
        if report.error:
            print(f"ERROR {label}: {report.error}")
            continue
        print(f"{label}: items={report.scripted_items} readback_present={report.has_readback}")
        print(f"  final transcript: {report.final_transcript!r}")
        # #313 (Rick's review): the docstring promises the transcript is reported "next to the
        # item's spokenReadBack-composed expected wording" -- print it so a human reviewer can
        # actually compare them side by side.
        print(f"  expected readback: {report.expected_readback!r}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--persona", action="append", dest="personas",
                       help="Persona id to evaluate (repeatable). Default: every enabled persona.")
    parser.add_argument("--n", type=int, default=DEFAULT_SESSIONS_PER_PERSONA,
                       help=f"Live sessions per persona (default {DEFAULT_SESSIONS_PER_PERSONA}).")
    parser.add_argument("--endpoint", help="Azure OpenAI endpoint. Default: AZURE_OPENAI_EASTUS2_ENDPOINT / azd env.")
    parser.add_argument("--deployment", help="Realtime deployment name. Default: AZURE_OPENAI_REALTIME_DEPLOYMENT / azd env.")
    parser.add_argument("--model", help="model_catalog.py catalog id, resolved to a deployment (overrides --deployment).")
    parser.add_argument("--tenant", help="Azure tenant id for Entra ID auth.")
    parser.add_argument("--subscription", help="Azure subscription id for Entra ID auth.")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT, help=f"Per-turn timeout in seconds (default {DEFAULT_TIMEOUT}).")
    parser.add_argument("--output", type=Path, help="Write the full JSON report to this path.")
    parser.add_argument("--save-audio", type=Path, metavar="DIR",
                       help="Save each turn's synthesized assistant audio as a WAV file under DIR "
                            "(one per persona/session/turn) so a human can judge pronunciation "
                            "from the actual audio, not just its transcript.")
    args = parser.parse_args(argv)

    try:
        personas = _personas_to_evaluate(args.personas)
        azd_values = sr._azd_env_values()
        endpoint = sr.resolve_setting("AZURE_OPENAI_EASTUS2_ENDPOINT", args.endpoint, azd_values)
        deployment = (sr.resolve_model_deployment(args.model, personas[0] if personas else None)
                     or sr.resolve_setting("AZURE_OPENAI_REALTIME_DEPLOYMENT", args.deployment, azd_values))
        if not endpoint or not deployment:
            raise EvalVoiceError("no endpoint/deployment -- pass --endpoint/--deployment or set "
                                "AZURE_OPENAI_EASTUS2_ENDPOINT/AZURE_OPENAI_REALTIME_DEPLOYMENT (or an azd env)")
        tenant_id, subscription_id = sr.resolve_identity(args.tenant, args.subscription, azd_values)
        headers = sr.get_auth_headers(tenant_id, subscription_id)
    except (EvalVoiceError, sr.SmokeError) as exc:
        print(f"eval_voice: cannot run: {exc}", file=sys.stderr)
        return 2

    async def _run_all() -> list[SessionReport]:
        reports: list[SessionReport] = []
        for persona in personas:
            reports += await evaluate_persona(persona, args.n, endpoint, deployment, headers,
                                              args.timeout, args.save_audio)
        return reports

    try:
        reports = asyncio.run(_run_all())
    except (aiohttp.ClientError, OSError) as exc:
        print(f"eval_voice: could not reach {endpoint}: {exc}", file=sys.stderr)
        return 1

    print_report(reports)
    if args.output:
        args.output.write_text(json.dumps([r.to_dict() for r in reports], indent=2))
        print(f"\nFull report written to {args.output}")
    return 1 if any(r.error for r in reports) else 0


if __name__ == "__main__":
    raise SystemExit(main())
