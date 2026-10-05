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
import dataclasses
import json
import os
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
BACKEND_DIR = REPO_ROOT / "app" / "backend"
sys.path.insert(0, str(BACKEND_DIR))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import aiohttp  # noqa: E402

import menu_utils  # noqa: E402
import tools  # noqa: E402
from order_state import order_state_singleton  # noqa: E402
from persona_loader import Persona, PersonaCatalog, PersonaValidationError  # noqa: E402

# Reuse smoke_realtime's auth/session/persona-resolution plumbing instead of duplicating it --
# this script's only new logic is the scripted multi-turn order conversation and its report.
import smoke_realtime as sr  # noqa: E402

DEFAULT_SESSIONS_PER_PERSONA = 3
DEFAULT_TIMEOUT = 30.0
# Phrases the final turn's transcript is checked for, to flag an outright-missing read-back
# (an empty or near-empty transcript, or one that never names an item) even though judging
# *correctness* of the read-back's wording is left to the conformance suite, not this script.
_READBACK_HINTS = ("here's your order", "here is your order", "i have", "you have", "total")


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
    error: str | None = None

    @property
    def final_transcript(self) -> str:
        return self.turns[-1].assistant_transcript if self.turns else ""

    @property
    def has_readback(self) -> bool:
        transcript = self.final_transcript.lower()
        return any(hint in transcript for hint in _READBACK_HINTS)

    def to_dict(self) -> dict:
        return {
            "persona": self.persona_id,
            "session_index": self.session_index,
            "scripted_items": list(self.scripted_items),
            "has_readback": self.has_readback if not self.error else None,
            "final_transcript": self.final_transcript,
            "error": self.error,
            "turns": [dataclasses.asdict(t) for t in self.turns],
        }


def pick_scripted_items(persona: Persona) -> tuple[str, str]:
    """Two real menu item names for *persona*: the first is a lexicon- or spokenName-covered
    item when one exists (so the session actually exercises #304's new fields), the second is
    any other real item for the mid-order "change". Falls back to the first two catalog items
    when the persona has no `pronunciations`/`spokenName` entries at all -- the read-back is
    still mandatory and still worth evaluating even without a lexicon hit."""
    catalog = menu_utils.get_catalog_for_persona(persona)
    names = [fields["name"] for fields in catalog.item_fields.values() if fields.get("name")]
    if not names:
        raise EvalVoiceError(f"persona {persona.id!r} has no real menu items to script a session with")
    lexicon_keys = sorted(persona.manifest.pronunciations, key=len, reverse=True)

    def covered(name: str) -> bool:
        return name in catalog.spoken_as or any(key in name for key in lexicon_keys)

    primary = next((n for n in names if covered(n)), names[0])
    secondary = next((n for n in names if n != primary), primary)
    return primary, secondary


async def _run_tool_call(session_id: str, name: str, args: dict) -> str:
    """Execute the real `tools.update_order`/`tools.get_order` for *name* -- the same
    production functions the middle tier calls -- so the read-back text evaluated here is the
    actual `spokenReadBack` field under test, not a stand-in."""
    if name == "update_order":
        result = await tools.update_order(args, session_id)
    elif name == "get_order":
        result = await tools.get_order(args, session_id)
    elif name == "reset_order":
        result = await tools.reset_order(args, session_id)
    else:
        raise EvalVoiceError(f"unexpected tool call {name!r} during a scripted #304 eval session")
    return result.to_text()


async def _script_turn(ws, session_id: str, guest_text: str, timeout: float) -> TurnResult:
    """Send one scripted guest turn (as text -- only the ASSISTANT's audio/transcript is
    under evaluation here, so the guest side doesn't need synthesized audio) and drive the
    tool-call loop (function_call -> real tool execution -> function_call_output ->
    response.create) until the assistant's turn finishes, collecting its spoken transcript."""
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


async def run_one_session(persona: Persona, index: int, rtmt, url: str, headers: dict,
                           timeout: float) -> SessionReport:
    primary, secondary = pick_scripted_items(persona)
    session_id = order_state_singleton.create_session(persona=persona)
    scripted_turns = [
        f"Hi, can I get a {primary}, please?",
        f"Actually, can you also add a {secondary}?",
        "That's everything, I'm done.",
    ]
    report = SessionReport(persona_id=persona.id, session_index=index, scripted_items=(primary, secondary), turns=[])
    try:
        async with aiohttp.ClientSession() as http, http.ws_connect(url, headers=headers) as ws:
            tool_schemas = [tool.schema for tool in rtmt.tools.values()]
            await ws.send_str(rtmt.build_bootstrap_session_update(system_message=rtmt.system_message,
                                                                   tool_schemas=tool_schemas))
            echoed, error = await sr.send_session_update(ws, rtmt.build_bootstrap_session_update(
                system_message=rtmt.system_message, tool_schemas=tool_schemas), timeout)
            if error is not None:
                raise EvalVoiceError(f"session.update rejected: {error}")
            for guest_text in scripted_turns:
                report.turns.append(await _script_turn(ws, session_id, guest_text, timeout))
    except EvalVoiceError as exc:
        report.error = str(exc)
    except (aiohttp.ClientError, OSError, TimeoutError) as exc:
        report.error = f"{type(exc).__name__}: {exc}"
    finally:
        order_state_singleton.end_session(session_id) if hasattr(order_state_singleton, "end_session") else None
    return report


async def evaluate_persona(persona: Persona, n: int, endpoint: str, deployment: str, headers: dict,
                           timeout: float) -> list[SessionReport]:
    rtmt = sr.build_middle_tier(endpoint, deployment, persona=persona)
    url = sr.realtime_url(endpoint, deployment)
    reports = []
    for index in range(n):
        reports.append(await run_one_session(persona, index, rtmt, url, headers, timeout))
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
            reports += await evaluate_persona(persona, args.n, endpoint, deployment, headers, args.timeout)
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
