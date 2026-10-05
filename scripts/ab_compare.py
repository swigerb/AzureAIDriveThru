"""Live A/B comparison harness for the Python and C# backends (issue #18).

Drives the SAME scripted guest orders -- one persona's own Demo Mode script
(``personas/<id>/assets/demo/guestScript.json``) and its recorded guest audio
clips -- against both backends' live ``/realtime`` WebSocket, **exactly the
way the browser does**: the frontend's own ``session.update`` (server VAD),
then each recorded clip streamed as ``input_audio_buffer.append`` (PCM16
24 kHz) followed by trailing silence so server VAD ends the turn. Production
rejects client text turns (``conversation.item.create``/``response.create``
are not in ``rtmt.py``'s ``_CLIENT_ALLOWED_TYPES``), so audio is the only
faithful path -- this never sends a text turn. This never guesses the auth or
wire contract: see ``docs/persona-architecture.md`` section 18 (ADR-002) and
``src/`` (the frontend's own ``useRealtime.tsx`` / ``src/auth``) for the exact
shapes this mirrors.

**Auth.** A delegated Entra token comes from

    az account get-access-token --scope api://<ENTRA_CLIENT_ID>/access_as_user

the same headless path ``Setup-EntraAuth.ps1`` and the conformance suite use
(persona-architecture.md 18.1, 18.11). That token is exchanged for the app's
layered HMAC session token at ``GET /api/auth/session`` (18.3), and both are
attached to the ``/realtime`` URL exactly as the frontend does
(``?token=<hmac>&access_token=<entra>``, ``useRealtime.tsx`` ``getSocketUrl``).

**Per-turn measurements:** first-audio latency (end of the guest's streamed
speech to the first ``response.output_audio.delta``/``response.audio.delta``
frame -- frames are read concurrently with the trailing silence being sent,
so an audio delta that arrives during that silence is timestamped the moment
it is read, not after the silence finishes sending) and total turn time (same
start, through a ``response.done`` followed by ``_TURN_SETTLE_S`` with no new
``response.created`` -- this includes the trailing-silence send and the
settle tail, so every turn number is inflated by roughly that fixed amount).
A turn that never sees a ``response.done`` before the per-order timeout is
flagged (``TurnResult.timed_out``) and the whole order is recorded as an
error, even if an earlier turn in the same order produced a ticket.

**Per-order measurements:** tool correctness -- a run "produced a priced
ticket" when the final ``extension.middle_tier_tool_response`` frame the
backend already sends the browser (``rtmt.py``) parses into a non-empty item
list and total with no error; this says nothing about whether that ticket
matches the spoken order -- the real accuracy signal is cross-backend parity
(below). Session ready -- WebSocket connect to the first
``extension.session_metadata`` frame -- is on an already-warm container, not
a container cold start.

**Cross-backend parity:** for a given persona x model, "identical ticket"
requires every repetition on BOTH backends to produce the exact same single
ticket (items, sizes, quantities, total) -- a single differing repetition
fails parity, even if most reps agree.

**Per-app measurements:** CPU (``UsageNanoCores``) and memory
(``WorkingSetBytes``) over the actual wall-clock window the run took (start
to end timestamps recorded around the sweep), via ``az monitor metrics list``
against each backend's Container App. Backends are exercised one after
another (never concurrently), so that window necessarily includes both.

This module intentionally never hardcodes a persona or brand name: every
persona id and display name comes from ``personas/<id>/`` on disk, read at
run time. Shared code stays brand-neutral everywhere else.

**This script makes live network calls (WebSocket, REST, Azure CLI) when
run.** It is never invoked against live Azure as part of building or testing
this harness -- see ``scripts/tests/test_ab_compare.py`` for the fake-backed
unit tests that exercise its logic without any network or ``az`` access.

Usage (from the repo root, with ``az login`` done, both backends deployed
and reachable, and ``ffmpeg`` on PATH to decode the recorded guest clips)::

    python scripts/ab_compare.py \\
        --python-uri https://<python-fqdn> --dotnet-uri https://<dotnet-fqdn> \\
        --entra-client-id <client-id> \\
        --reps 3 --personas <persona-id>,<persona-id> --models gpt-realtime-2.1-mini,gpt-realtime-2.1 \\
        --out docs/ab-report

``--python-uri``/``--dotnet-uri`` default to the ``BACKEND_URI``/
``BACKEND_DOTNET_URI`` env vars (the same names the frontend's two-hostname
setup and ``infra/main.bicep`` use, persona-architecture.md 18.7, #311).
``--entra-client-id`` defaults to ``ENTRA_CLIENT_ID``. Pass ``--skip-metrics``
to omit the ``az monitor metrics list`` calls (no Container Apps access, or a
non-Azure dry run against local dev servers).

Exit codes: 0 = every order produced a priced ticket on both backends with no
errors, and every persona x model combination had identical cross-backend
tickets, 1 = at least one mismatch, timed-out turn, or run error, 2 = could
not run (missing URIs/token/az CLI).
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import shutil
import statistics
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[1]
PERSONAS_DIR = REPO_ROOT / "personas"

# Both event names observed in the wild across backend/model versions for an assistant audio
# chunk -- accept either so a first-audio timestamp is never missed over a naming difference.
_AUDIO_DELTA_TYPES = frozenset({"response.output_audio.delta", "response.audio.delta"})
_RESPONSE_DONE_TYPE = "response.done"
_TOOL_RESPONSE_TYPE = "extension.middle_tier_tool_response"
_METADATA_TYPE = "extension.session_metadata"

DEFAULT_MODELS = ("gpt-realtime-2.1-mini", "gpt-realtime-2.1")
DEFAULT_TIMEOUT_S = 30.0
_SAMPLE_RATE = 24000
_CHUNK_SECONDS = 0.1
_CHUNK_BYTES = int(_SAMPLE_RATE * _CHUNK_SECONDS) * 2
_TRAILING_SILENCE_S = 1.2
_TURN_SETTLE_S = float(os.environ.get("AB_COMPARE_TURN_SETTLE_S", "2.5"))
# Mirrors useRealtime.tsx's session.update (server VAD settings).
_FRONTEND_SESSION_UPDATE = {
    "type": "session.update",
    "session": {"turn_detection": {"type": "server_vad", "threshold": 0.7,
                                   "prefix_padding_ms": 300, "silence_duration_ms": 500}},
}


# ── Errors ───────────────────────────────────────────────────────────────

class ABCompareError(RuntimeError):
    """Raised for configuration or environment problems the harness can't recover from."""


# ── Persona data (read from disk -- never hardcoded, never brand-specific) ──

def discover_personas() -> list[str]:
    """Every persona id with a Demo Mode script, sorted for stable output."""
    ids = []
    if not PERSONAS_DIR.is_dir():
        return ids
    for child in sorted(PERSONAS_DIR.iterdir()):
        if (child / "assets" / "demo" / "guestScript.json").is_file():
            ids.append(child.name)
    return ids


def _guest_script(persona_id: str) -> dict:
    path = PERSONAS_DIR / persona_id / "assets" / "demo" / "guestScript.json"
    return json.loads(path.read_text(encoding="utf-8"))


def load_guest_clip_paths(persona_id: str) -> list[tuple[str, Path]]:
    """(text, audio path) for every scripted guest line that has a recorded clip."""
    assets = PERSONAS_DIR / persona_id / "assets"
    return [(line["text"], assets / line["audio"]) for line in _guest_script(persona_id).get("lines", [])
            if line.get("text") and line.get("audio")]


def load_menu_mode(persona_id: str) -> str | None:
    """The Demo Mode script's own menu mode (e.g. a daypart), sent as ``?mode=`` like the browser."""
    return _guest_script(persona_id).get("menuMode")


def load_guest_clip_pcm(path: Path) -> bytes:
    """Decodes a recorded guest clip to PCM16 mono 24 kHz (the realtime input format) with ffmpeg."""
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise ABCompareError("ffmpeg is required to stream the recorded guest clips")
    out = subprocess.run([ffmpeg, "-v", "error", "-i", str(path), "-f", "s16le", "-ac", "1",
                          "-ar", str(_SAMPLE_RATE), "-"], capture_output=True, check=True)
    return out.stdout


@dataclass(frozen=True)
class ExpectedItem:
    item: str
    size: str
    quantity: int


# ── Auth (az CLI, headless MSAL -- Setup-EntraAuth.ps1's own path) ──────────

def _az() -> str:
    """Absolute path to the az CLI. On Windows it is `az.cmd`, which subprocess can't run by
    bare name without a shell, so resolve it explicitly (same issue smoke_realtime.py handles)."""
    return shutil.which("az") or "az"


def get_entra_token(client_id: str, *, runner=subprocess.run) -> str:
    """A delegated Entra access token for ``api://<client_id>/access_as_user``, from
    ``az account get-access-token`` (the pre-authorized Azure CLI client, 18.1/18.11).
    ``runner`` is injectable so tests never shell out."""
    scope = f"api://{client_id}/access_as_user"
    try:
        result = runner(
            [_az(), "account", "get-access-token", "--scope", scope, "-o", "json"],
            capture_output=True, text=True, check=True,
        )
    except FileNotFoundError as exc:
        raise ABCompareError("the az CLI was not found on PATH; run `az login` first") from exc
    except subprocess.CalledProcessError as exc:
        raise ABCompareError(f"az account get-access-token failed: {exc.stderr or exc.stdout}") from exc
    payload = json.loads(result.stdout)
    token = payload.get("accessToken")
    if not token:
        raise ABCompareError("az account get-access-token returned no accessToken")
    return token


async def fetch_session_token(http, base_url: str, access_token: str) -> str:
    """The layered HMAC session token from ``GET /api/auth/session`` (18.3), bearer-authed
    exactly like ``authorizedFetch`` on every other protected REST call."""
    headers = {"Authorization": f"Bearer {access_token}"}
    async with http.get(f"{base_url}/api/auth/session", headers=headers) as resp:
        resp.raise_for_status()
        payload = await resp.json()
    token = payload.get("token")
    if not token:
        raise ABCompareError(f"{base_url}/api/auth/session returned no token")
    return token


def realtime_url(base_url: str, *, persona_id: str, model_id: str, session_token: str, access_token: str,
                 menu_mode: str | None = None) -> str:
    """Mirrors ``useRealtime.tsx``'s ``getSocketUrl``: the same four query params, in Entra mode."""
    ws_base = base_url.replace("https://", "wss://").replace("http://", "ws://")
    return (
        f"{ws_base}/realtime?persona={persona_id}&model={model_id}"
        f"&token={session_token}&access_token={access_token}"
        + (f"&mode={menu_mode}" if menu_mode else "")
    )


# ── Per-turn / per-order results ────────────────────────────────────────────

@dataclass
class TurnResult:
    guest_text: str
    first_audio_latency_s: float | None
    total_turn_time_s: float
    # True when this turn never saw a `response.done` before the per-order timeout -- the
    # turn's numbers (if any) are incomplete, and the whole order is recorded as an error even
    # if an earlier turn in the same order produced a ticket (Rick's PR #321 review, non-blocking).
    timed_out: bool = False


@dataclass
class OrderRunResult:
    backend: str
    persona_id: str
    model_id: str
    rep: int
    session_ready_s: float | None
    turns: list[TurnResult] = field(default_factory=list)
    ticket_items: list[ExpectedItem] = field(default_factory=list)
    ticket_total: float | None = None
    error: str | None = None

    @property
    def tool_correct(self) -> bool:
        """The run produced a priced ticket with no error. This is NOT an accuracy claim -- it
        says nothing about whether the ticket matches the spoken order. Cross-backend agreement
        (the real A/B correctness bar) is computed in `build_report` by comparing Python vs C#
        tickets."""
        return self.error is None and self.ticket_total is not None and bool(self.ticket_items)

    def ticket_key(self) -> tuple | None:
        if self.ticket_total is None:
            return None
        return (tuple(sorted((i.item, i.size, i.quantity) for i in self.ticket_items)), round(self.ticket_total, 2))


def _parse_ticket(tool_result: Any) -> tuple[list[ExpectedItem], float] | None:
    """Parses an ``extension.middle_tier_tool_response`` ``tool_result`` (an ``OrderSummary``
    JSON string, see ``order_state.get_order_summary_json``) into item list + final total."""
    if isinstance(tool_result, str):
        try:
            payload = json.loads(tool_result)
        except json.JSONDecodeError:
            return None
    elif isinstance(tool_result, dict):
        payload = tool_result
    else:
        return None
    items_raw = payload.get("items")
    final_total = payload.get("finalTotal")
    if items_raw is None or final_total is None:
        return None
    items = [
        ExpectedItem(item=row["item"], size=row["size"], quantity=int(row["quantity"]))
        for row in items_raw
    ]
    return items, float(final_total)


async def _wait_for(ws, predicate, timeout_s: float):
    """Reads frames from *ws* until *predicate(frame)* is true or *timeout_s* elapses.
    Returns the matching frame, or ``None`` on timeout."""
    import aiohttp

    deadline = time.monotonic() + timeout_s
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        try:
            msg = await asyncio.wait_for(ws.receive(), timeout=remaining)
        except TimeoutError:
            return None
        if msg.type != aiohttp.WSMsgType.TEXT:
            if msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                return None
            continue
        frame = json.loads(msg.data)
        if predicate(frame):
            return frame


async def run_order(
    http,
    ws_connect,
    *,
    backend: str,
    base_url: str,
    persona_id: str,
    model_id: str,
    rep: int,
    access_token: str,
    timeout_s: float = DEFAULT_TIMEOUT_S,
    clip_loader=None,
) -> OrderRunResult:
    """Runs *this persona*'s Demo Mode order once against *base_url*, exactly the way a browser
    does: the frontend's own ``session.update`` (server VAD), wait for the greeting, then stream
    each recorded guest clip as ``input_audio_buffer.append`` (PCM16 24 kHz) followed by
    trailing silence so server VAD ends the turn. Production rejects client text turns
    (``conversation.item.create`` is not in rtmt.py's ``_CLIENT_ALLOWED_TYPES``), so audio is the
    only faithful path. First-audio latency is measured from the end of the guest's speech (last
    speech chunk sent) to the first assistant audio delta: the trailing silence is sent by a
    concurrent task while frames are read, so a delta arriving during that silence is timestamped
    the moment it is read rather than only once the silence finishes sending (Rick's PR #321
    review, B2 -- the old sequential send-then-read order gave every first-audio value a floor of
    about ``_TRAILING_SILENCE_S``). A turn ends once a ``response.done`` is followed by
    ``_TURN_SETTLE_S`` with no new ``response.created`` (tool-call rounds chain several responses
    per guest turn); a turn that never reaches that point before *timeout_s* is flagged
    (``TurnResult.timed_out``) and the whole order is recorded as an error, even if an earlier
    turn already produced a ticket. ``ws_connect``/``clip_loader`` are injectable for tests."""
    result = OrderRunResult(backend=backend, persona_id=persona_id, model_id=model_id, rep=rep, session_ready_s=None)
    loader = clip_loader or load_guest_clip_pcm
    try:
        clips = load_guest_clip_paths(persona_id)
        menu_mode = load_menu_mode(persona_id)
        session_token = await fetch_session_token(http, base_url, access_token)
        url = realtime_url(
            base_url, persona_id=persona_id, model_id=model_id,
            session_token=session_token, access_token=access_token, menu_mode=menu_mode,
        )
        connect_start = time.monotonic()
        async with ws_connect(url) as ws:
            ready = await _wait_for(ws, lambda f: f.get("type") == _METADATA_TYPE, timeout_s)
            result.session_ready_s = time.monotonic() - connect_start
            if ready is None:
                raise ABCompareError(f"{backend}: no {_METADATA_TYPE} frame before timeout")
            await ws.send_json(_FRONTEND_SESSION_UPDATE)

            last_ticket: tuple[list[ExpectedItem], float] | None = None

            async def settle(turn_start: float | None) -> tuple[float | None, bool]:
                nonlocal last_ticket
                first_audio: float | None = None
                saw_done = False
                deadline = time.monotonic() + timeout_s
                while time.monotonic() < deadline:
                    wait = _TURN_SETTLE_S if saw_done else deadline - time.monotonic()
                    try:
                        msg = await asyncio.wait_for(ws.receive(), timeout=max(wait, 0.05))
                    except TimeoutError:
                        if saw_done:
                            return first_audio, True
                        break
                    import aiohttp
                    if msg.type != aiohttp.WSMsgType.TEXT:
                        if msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                            break
                        continue
                    frame = json.loads(msg.data)
                    frame_type = frame.get("type")
                    if frame_type in _AUDIO_DELTA_TYPES and first_audio is None and turn_start is not None:
                        first_audio = time.monotonic() - turn_start
                    elif frame_type == _TOOL_RESPONSE_TYPE:
                        parsed = _parse_ticket(frame.get("tool_result"))
                        if parsed is not None:
                            last_ticket = parsed
                    elif frame_type == _RESPONSE_DONE_TYPE:
                        saw_done = True
                    elif frame_type == "response.created":
                        saw_done = False
                return first_audio, saw_done

            async def send_trailing_silence() -> None:
                """Sends `_TRAILING_SILENCE_S` worth of silence so server VAD ends the turn.
                Run concurrently with `settle()` (via `asyncio.gather`, below) so a frame that
                arrives mid-silence is read -- and timestamped -- immediately instead of only
                once every silence chunk has been sent (B2)."""
                silence = b"\x00" * _CHUNK_BYTES
                for _ in range(int(_TRAILING_SILENCE_S / _CHUNK_SECONDS)):
                    await ws.send_json({"type": "input_audio_buffer.append",
                                       "audio": base64.b64encode(silence).decode("ascii")})
                    await asyncio.sleep(_CHUNK_SECONDS)

            _, greeted = await settle(None)
            if not greeted:
                raise ABCompareError(f"{backend}: greeting never completed")

            for text, clip in clips:
                pcm = loader(clip)
                for offset in range(0, len(pcm), _CHUNK_BYTES):
                    await ws.send_json({"type": "input_audio_buffer.append",
                                       "audio": base64.b64encode(pcm[offset:offset + _CHUNK_BYTES]).decode("ascii")})
                    await asyncio.sleep(_CHUNK_SECONDS)
                turn_start = time.monotonic()
                (first_audio_s, saw_done), _ = await asyncio.gather(
                    settle(turn_start), send_trailing_silence(),
                )
                result.turns.append(TurnResult(
                    guest_text=text, first_audio_latency_s=first_audio_s,
                    total_turn_time_s=time.monotonic() - turn_start,
                    timed_out=not saw_done,
                ))
                if not saw_done and result.error is None:
                    result.error = f"{backend}: turn timed out before response.done: {text!r}"

            if last_ticket is not None:
                result.ticket_items, result.ticket_total = last_ticket
    except Exception as exc:  # noqa: BLE001 -- recorded per-run, never crashes the whole sweep
        result.error = f"{type(exc).__name__}: {exc}"
    return result

# ── Azure Container App CPU/memory (az monitor metrics list) ────────────────

@dataclass
class ResourceMetrics:
    avg_cpu_nanocores: float | None
    avg_memory_bytes: float | None


def fetch_container_app_metrics(
    resource_id: str, start_iso: str, end_iso: str, *, runner=subprocess.run,
) -> ResourceMetrics:
    """``az monitor metrics list`` for ``UsageNanoCores``/``WorkingSetBytes`` averaged over the
    run window, for one backend's Container App."""
    try:
        result = runner(
            [
                _az(), "monitor", "metrics", "list", "--resource", resource_id,
                "--metric", "UsageNanoCores,WorkingSetBytes",
                "--aggregation", "Average",
                "--start-time", start_iso, "--end-time", end_iso,
                "-o", "json",
            ],
            capture_output=True, text=True, check=True,
        )
    except FileNotFoundError as exc:
        raise ABCompareError("the az CLI was not found on PATH; run `az login` first") from exc
    except subprocess.CalledProcessError as exc:
        raise ABCompareError(f"az monitor metrics list failed: {exc.stderr or exc.stdout}") from exc
    payload = json.loads(result.stdout)
    return _parse_metrics_payload(payload)


def _parse_metrics_payload(payload: dict) -> ResourceMetrics:
    averages: dict[str, list[float]] = {"UsageNanoCores": [], "WorkingSetBytes": []}
    for metric in payload.get("value", []):
        name = metric.get("name", {}).get("value")
        if name not in averages:
            continue
        for ts in metric.get("timeseries", []):
            for point in ts.get("data", []):
                avg = point.get("average")
                if avg is not None:
                    averages[name].append(float(avg))
    cpu = statistics.fmean(averages["UsageNanoCores"]) if averages["UsageNanoCores"] else None
    mem = statistics.fmean(averages["WorkingSetBytes"]) if averages["WorkingSetBytes"] else None
    return ResourceMetrics(avg_cpu_nanocores=cpu, avg_memory_bytes=mem)


# ── Reporting ────────────────────────────────────────────────────────────

def _percentile(values: list[float], pct: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    k = (len(ordered) - 1) * (pct / 100)
    f, c = int(k), min(int(k) + 1, len(ordered) - 1)
    if f == c:
        return ordered[f]
    return ordered[f] + (ordered[c] - ordered[f]) * (k - f)


def _latency_stats(results: list[OrderRunResult]) -> dict[str, float | None]:
    first_audio = [t.first_audio_latency_s for r in results for t in r.turns if t.first_audio_latency_s is not None]
    total_turn = [t.total_turn_time_s for r in results for t in r.turns]
    return {
        "first_audio_p50": _percentile(first_audio, 50),
        "first_audio_p90": _percentile(first_audio, 90),
        "total_turn_p50": _percentile(total_turn, 50),
        "total_turn_p90": _percentile(total_turn, 90),
    }


def build_report(
    results: list[OrderRunResult],
    metrics: dict[str, ResourceMetrics] | None = None,
    run_window: tuple[str, str] | None = None,
) -> dict:
    """A JSON-serializable report: p50/p90 latency tables per persona x backend x model, a
    correctness matrix ("produced a priced ticket", not a spoken-order accuracy check), a
    cross-backend ticket-parity table, session-ready timing, and (when supplied) per-app
    CPU/memory averages plus the wall-clock *run_window* (start, end ISO timestamps) those
    averages were queried over -- backends are exercised one after another, never
    concurrently, so that window necessarily covers both (B3, Rick's PR #321 review)."""
    groups: dict[tuple[str, str, str], list[OrderRunResult]] = {}
    for r in results:
        groups.setdefault((r.persona_id, r.backend, r.model_id), []).append(r)

    latency_table = []
    correctness_matrix = []
    for (persona_id, backend, model_id), group in sorted(groups.items()):
        stats = _latency_stats(group)
        latency_table.append({"persona": persona_id, "backend": backend, "model": model_id, **stats})
        session_ready_times = [r.session_ready_s for r in group if r.session_ready_s is not None]
        correctness_matrix.append({
            "persona": persona_id, "backend": backend, "model": model_id,
            "reps": len(group),
            "correct": sum(1 for r in group if r.tool_correct),
            "errors": sum(1 for r in group if r.error is not None),
            "error_messages": sorted({r.error for r in group if r.error is not None}),
            "session_ready_p50_s": _percentile(session_ready_times, 50),
        })

    parity = []
    for persona_id, model_id in sorted({(r.persona_id, r.model_id) for r in results}):
        group = [r for r in results if r.persona_id == persona_id and r.model_id == model_id]
        by_backend = {b: [r for r in group if r.backend == b] for b in ("python", "dotnet")}
        keys = {b: {r.ticket_key() for r in rs if r.tool_correct} for b, rs in by_backend.items()}
        # "Identical ticket" requires EVERY rep on BOTH backends to produce the exact same
        # single ticket -- a single differing (or missing/erroring) rep fails parity, even if
        # most reps agree (B4, Rick's PR #321 review: the old rule only checked that the two
        # backends' ticket SETS intersected, so a partial overlap still said "yes").
        all_match = (
            bool(by_backend["python"]) and bool(by_backend["dotnet"])
            and all(r.tool_correct for r in by_backend["python"])
            and all(r.tool_correct for r in by_backend["dotnet"])
            and len(keys["python"]) == 1
            and len(keys["dotnet"]) == 1
            and keys["python"] == keys["dotnet"]
        )
        common = keys["python"] & keys["dotnet"]
        sample = next(iter(common), None)
        parity.append({"persona": persona_id, "model": model_id, "match": all_match,
                       "ticket_total": sample[1] if sample else None,
                       "python_tickets": len(keys["python"]), "dotnet_tickets": len(keys["dotnet"])})

    report: dict[str, Any] = {
        "parity": parity,
        "latency": latency_table,
        "correctness": correctness_matrix,
    }
    if metrics:
        report["resource_usage"] = {
            backend: {
                "avg_cpu_nanocores": m.avg_cpu_nanocores,
                "avg_memory_bytes": m.avg_memory_bytes,
            }
            for backend, m in metrics.items()
        }
        if run_window:
            report["resource_usage_window"] = {"start": run_window[0], "end": run_window[1]}
    return report


def render_markdown(report: dict) -> str:
    lines = ["# A/B comparison report", ""]

    lines.append("## Latency (p50 / p90, seconds)")
    lines.append("")
    lines.append("| Persona | Backend | Model | First audio p50 | First audio p90 | Turn p50 | Turn p90 |")
    lines.append("| --- | --- | --- | --- | --- | --- | --- |")
    for row in report.get("latency", []):
        def fmt(v):
            return f"{v:.3f}" if v is not None else "n/a"
        lines.append(
            f"| {row['persona']} | {row['backend']} | {row['model']} "
            f"| {fmt(row['first_audio_p50'])} | {fmt(row['first_audio_p90'])} "
            f"| {fmt(row['total_turn_p50'])} | {fmt(row['total_turn_p90'])} |"
        )
    lines.append("")

    lines.append("## Python vs C# ticket parity (same spoken order)")
    lines.append("")
    lines.append("| Persona | Model | Identical ticket | Python tickets | Dotnet tickets | Total |")
    lines.append("| --- | --- | --- | --- | --- | --- |")
    for row in report.get("parity", []):
        total = f"${row['ticket_total']:.2f}" if row["ticket_total"] is not None else "n/a"
        lines.append(
            f"| {row['persona']} | {row['model']} | {'yes' if row['match'] else 'NO'} "
            f"| {row['python_tickets']} | {row['dotnet_tickets']} | {total} |"
        )
    lines.append("")

    lines.append("## Correctness matrix")
    lines.append("")
    lines.append(
        "| Persona | Backend | Model | Reps | Produced ticket | Errors | Session ready p50 (s) |"
    )
    lines.append("| --- | --- | --- | --- | --- | --- | --- |")
    for row in report.get("correctness", []):
        ready = row["session_ready_p50_s"]
        ready_str = f"{ready:.3f}" if ready is not None else "n/a"
        lines.append(
            f"| {row['persona']} | {row['backend']} | {row['model']} "
            f"| {row['reps']} | {row['correct']}/{row['reps']} | {row['errors']} | {ready_str} |"
        )
    lines.append("")

    if "resource_usage" in report:
        lines.append("## Resource usage (run average)")
        lines.append("")
        window = report.get("resource_usage_window")
        if window:
            lines.append(
                f"Measured over the actual run window ({window['start']} to {window['end']}). "
                "Backends are exercised one after another, never concurrently, so this window "
                "covers both."
            )
            lines.append("")
        lines.append("| Backend | Avg CPU (nanocores) | Avg memory (bytes) |")
        lines.append("| --- | --- | --- |")
        for backend, usage in sorted(report["resource_usage"].items()):
            cpu = usage["avg_cpu_nanocores"]
            mem = usage["avg_memory_bytes"]
            cpu_str = f"{cpu:,.0f}" if cpu is not None else "n/a"
            mem_str = f"{mem:,.0f}" if mem is not None else "n/a"
            lines.append(f"| {backend} | {cpu_str} | {mem_str} |")
        lines.append("")

    return "\n".join(lines)


# ── CLI / orchestration ──────────────────────────────────────────────────

def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--python-uri", default=os.environ.get("BACKEND_URI"),
                         help="Python backend base URL (default: $BACKEND_URI)")
    parser.add_argument("--dotnet-uri", default=os.environ.get("BACKEND_DOTNET_URI"),
                         help="C# backend base URL (default: $BACKEND_DOTNET_URI)")
    parser.add_argument("--entra-client-id", default=os.environ.get("ENTRA_CLIENT_ID"),
                         help="Entra app registration client id (default: $ENTRA_CLIENT_ID)")
    parser.add_argument("--reps", type=int, default=1, help="Repetitions per persona x model x backend")
    parser.add_argument("--personas", default=None, help="Comma-separated persona ids (default: all discovered)")
    parser.add_argument("--models", default=",".join(DEFAULT_MODELS), help="Comma-separated realtime model ids")
    parser.add_argument("--out", default="report", help="Output path prefix (writes <out>.md and <out>.json)")
    parser.add_argument("--timeout-s", type=float, default=DEFAULT_TIMEOUT_S)
    parser.add_argument("--skip-metrics", action="store_true", help="Skip az monitor metrics list calls")
    parser.add_argument("--python-resource-id", default=os.environ.get("AB_COMPARE_PYTHON_RESOURCE_ID"),
                         help="Container App resource id for the Python backend's metrics")
    parser.add_argument("--dotnet-resource-id", default=os.environ.get("AB_COMPARE_DOTNET_RESOURCE_ID"),
                         help="Container App resource id for the C# backend's metrics")
    return parser.parse_args(argv)


async def _run_all(args: argparse.Namespace) -> list[OrderRunResult]:
    import aiohttp

    backends = []
    if args.python_uri:
        backends.append(("python", args.python_uri))
    if args.dotnet_uri:
        backends.append(("dotnet", args.dotnet_uri))
    if not backends:
        raise ABCompareError("at least one of --python-uri/--dotnet-uri ($BACKEND_URI/$BACKEND_DOTNET_URI) is required")
    if not args.entra_client_id:
        raise ABCompareError("--entra-client-id ($ENTRA_CLIENT_ID) is required")

    personas = args.personas.split(",") if args.personas else discover_personas()
    models = [m.strip() for m in args.models.split(",") if m.strip()]
    access_token = get_entra_token(args.entra_client_id)

    results: list[OrderRunResult] = []
    async with aiohttp.ClientSession() as http:
        for persona_id in personas:
            for model_id in models:
                for backend, base_url in backends:
                    for rep in range(args.reps):
                        result = await run_order(
                            http, http.ws_connect,
                            backend=backend, base_url=base_url, persona_id=persona_id,
                            model_id=model_id, rep=rep, access_token=access_token,
                            timeout_s=args.timeout_s,
                        )
                        results.append(result)
    return results


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    # Real wall-clock start/end around the sweep, not a fixed trailing hour taken afterwards
    # (B3, Rick's PR #321 review: a fixed `now() - 3600` window can include idle time before
    # the run, miss the start of a run longer than an hour, and always covers the period when
    # the OTHER backend was being exercised, since backends run one after another below).
    run_start = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    try:
        results = asyncio.run(_run_all(args))
    except ABCompareError as exc:
        print(f"ab_compare: {exc}", file=sys.stderr)
        return 2
    run_end = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    metrics = None
    if not args.skip_metrics and (args.python_resource_id or args.dotnet_resource_id):
        metrics = {}
        try:
            if args.python_resource_id:
                metrics["python"] = fetch_container_app_metrics(args.python_resource_id, run_start, run_end)
            if args.dotnet_resource_id:
                metrics["dotnet"] = fetch_container_app_metrics(args.dotnet_resource_id, run_start, run_end)
        except ABCompareError as exc:
            print(f"ab_compare: metrics skipped: {exc}", file=sys.stderr)
            metrics = None

    report = build_report(results, metrics, run_window=(run_start, run_end))
    out_path = Path(args.out)
    out_path.with_suffix(".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    out_path.with_suffix(".md").write_text(render_markdown(report), encoding="utf-8")

    any_bad = any(r.error is not None or not r.tool_correct for r in results) or any(
        not row["match"] for row in report.get("parity", []))
    return 1 if any_bad else 0


if __name__ == "__main__":
    sys.exit(main())
