# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Core Context

**Rebrand & Setup (2026-03-19)**: Completed Sonic rebrand from Dunkin across all backend systems. Key learning: `_infer_category()` in tools.py couples frontend menuItems.json to backend category inference. Team coordinated: Rick scope, Morty frontend, Summer backend (69 tests), Birdperson verification (12 tests).

**Performance Hardening (2026-03-19)**: Major backend perf pass: (1) `_tools_pending` moved from shared to per-connection (race condition). (2) `_PASSTHROUGH_TYPES` frozenset for O(1) hot-path bypass. (3) `__slots__` on classes, module-level search cache, compression middleware, gzip on HTTP. All 100 tests pass.

**Menu Integration (2026-03-19)**: Created Sonic menu ingestion notebook (recursive category traversal, size variant resolution, 172 of 1334 products). Key lesson: `.ipynb` files require programmatic Python JSON editing.

**Critical Ordering Bugs (2026-03-19)**: Fixed three issues: (1) `.env` pointed to wrong search index. (2) System prompt didn't extract prices from `sizes` JSON. (3) `max_tokens=150` truncated closing phrases → raised to 250. All 100 tests pass.

## Archive (2026-03-20 through 2026-03-22)

Series of debugging and feature work across demo readiness, debugging sprint, and architecture review:
- **Echo Suppression (2026-03-20):** rtmt.py audio gating with ai_speaking flag, 300ms→500ms cooldown, buffer clear. Refined 2026-03-21 with fast substring detection.
- **System Prompt (2026-03-21):** Converted to bulleted format with ALL CAPS emphasis. Explicit anti-hallucination grounding.
- **Order Routing (2026-03-21):** Changed `update_order` from TO_CLIENT to TO_BOTH, fixing dead silence on valid orders.
- **Tools Hardening (2026-03-21):** Price validation ($0 rejection), combo detection, human-readable size formatting, upsell hints.
- **Demo Polish (2026-03-21):** Temperature 0.6→0.5, added `get_grouped_order_for_readback()`. All 118 tests passing.
- **Menu Data (2026-03-22):** Audited menuItems.json, synced 50 Sonic items, dynamic MENU_CATEGORY_MAP inference.
- **Tool-Call Fix (2026-03-22):** Reordered WebSocket messages so session.update (with tools) arrives before greeting.
- **Greeting Fix (2026-03-22):** Rewritten imperative greeting prompt. Pre-set ai_speaking before response.create. Increased cooldown 500ms→1.5s.
- **Happy Hour (2026-03-22):** Drinks/slushes half-price 2:00–4:00 PM via STORE_TIMEZONE (fixed UTC bug).
- **OOS Machine (2026-03-22):** Ice cream [OOS] tag in search results. Non-blocking steering.
- **reset_order Tool (2026-03-22):** Big Red Button with TO_CLIENT routing.
- **Verbose Logging (2026-03-22):** Dedicated `sonic-verbose` logger, per-session toggles, message lifecycle logging.
- **Combo Pivot (2026-03-22):** Fixed state overwriting on combo + sides/drinks combo absorption with session counters.
- **Item Customization (2026-03-22):** End-to-end mods ("no lettuce", "extra ketchup") with natural voice readback.
- **Prompt Externalization (2026-03-25):** Full YAML-driven infrastructure (prompt_loader.py, config_loader.py, config.yaml). Wired into app.py, rtmt.py, tools.py with backward-compatible fallbacks. All 125 tests pass.
- **Architecture Review (2026-03-25):** Fixed 4 bugs: happy hour timezone, `_sent_greeting` memory leak, dead code, size/category deduplication in menu_utils.py.



<!-- Append new learnings below. Each entry is something lasting about the project. -->

**PR #72 (P2-3) — completing the Sonic menu from its source data, incl. floats (2026-09-27):**
The production export (`app/frontend/src/data/sonic-menu-items.json`) has a previously-unmined
top-level `modifiers` collection (242 entries), completely separate from the `products`
collection `scripts/extract_production_items.py` walks. `products` holds $0.00 recipe/ingredient
placeholders (Bacon, Burger Patty, Flavor syrups, Whip Topping — `extract_production_items.py`
explicitly skips `isRecipe: true` entries), but `modifiers` carries REAL non-zero prices for the
same concepts under "Extra `<X>`" names (Extra Bacon $2.00, ~17 "Extra `<Flavor>`" syrup shots
mostly at $0.60, a separate ~13-item group of dessert mix-ins at $1.00 — a different concept from
drink-flavor shots, don't conflate them). No priced patty modifier exists anywhere in the export;
that gap has to be resolved by falling back to whatever price is already trusted elsewhere (here,
`system_prompt.yaml`'s pre-existing $1.50), documented transparently in the item's `origin`
field rather than silently invented. **Key process lesson:** when an issue asks you to "add
priced add-ons from the source data," always check for a modifiers/ingredients collection
distinct from the main products walk before concluding a price doesn't exist — the extraction
script's own scope (skip recipe items) does not mean the raw export has no real price for that
concept elsewhere. Also: a stale hardcoded row-count assertion
(`Assert.Equal(60, golden.Items.Count)` in a C# Theory test, `GoldenMenuComboSlotTheoryTests.cs`)
is a 1:1, mechanical consequence of adding golden-table rows — safe and necessary to update
alongside the golden JSON itself, distinct from actual scenario-logic edits reserved for whoever
owns harness/scenario work in parallel (Birdperson's #76 here). Fresh worktrees need their own
`.venv` (`requirements.txt` + pinned `ruff`/`pytest`/`pytest-asyncio` versions) and their own
built `app/backend/static` (`npm ci && npm run build`) before either `pytest` or the C#
conformance harness's `PythonBackendLauncher` will run cleanly — `git worktree add` does not
carry over build artifacts, confirming the same lesson already recorded below from PR #66.

**PR #73 (P2-4) — No off-menu ordering, ADR-001 decision 4 (2026-09-27):** Made `resolve_menu_item()`
(exact normalized name or exact-match alias against the real pack, nothing else) the single on-menu
gate `update_order`'s `add` path calls before any size/price/customization/extras/quantity check;
anything that doesn't resolve is rejected `TO_SERVER`-only with `error_messages.yaml`'s
`item_not_on_menu` (or `size_not_available` if the name resolves but the size doesn't), same plain-
text rejection shape every other add-time guard in `tools.py` already used — no new wire schema was
needed, and inventing one would have meant touching `rtmt.py`/`app.py` session code, explicitly out
of scope here (#74 next). Fully deleted (not just made unreachable) three keyword-fallback regex
functions in `menu_utils.py` (`_FOUNTAIN_DRINK_KEYWORD_RE`, `_SHAKE_BLAST_KEYWORD_RE`,
`_DR_PEPPER_RE`) plus `infer_category`'s substring rules and `bundle_slots`' `"combo" in name`
escape hatch (the "until #73" fallback called out in the wave plan); off-menu names now get safe
classification defaults (`""`/`False`/`()`/`None`) instead of guessing. Replaced
`_ICE_CREAM_MACHINE_KEYWORDS` substring matching in the OOS annotation with a new
`requires_machine(item_name)` reading the pack's real `requiresMachine` field, and replaced
`EXTRAS_KEYWORDS` name matching in the extras guard with a new `is_extra_item(item_name)` reading
the real `isExtra` field — both resolve aliases first through the same `_resolve_alias` pipeline
every other classifier uses, which is why e.g. "Whipped Cream" (an alias of "Whipped Topping")
still correctly reports `isExtra=True`. **Data-loss near-miss and recovery:** a `git checkout --
menu_utils.py` run to "clean up a scratch experiment" silently wiped the entire uncommitted #73
rewrite of that file back to the pre-#73 baseline, undetected until a later `pytest` `ImportError`
surfaced it. Reconstructed the file from scratch using the (intact) test files as the spec
(`test_menu_utils.py`'s class/test names, `tools.py`'s import contract, real `menuItems.json`
field values) and re-verified byte-for-byte against every consumer before trusting it again.
**Key lesson, now load-bearing for how I work in a worktree:** `git checkout -- <file>` on an
uncommitted file has zero recovery path — never run it against a file with pending work, and more
importantly, commit a fully-verified milestone (full test suite green) *before* starting any
follow-on work (mutation testing, docs) that involves further `git` commands, so an accidental
revert only costs the uncommitted increment, not the whole feature. Applied this immediately after:
committed the reconstruction first, then ran all three issue-mandated mutation checks (keyword
fallback re-added → off-menu test fails; a shake's `requiresMachine` blanked → machine-down test
fails; an extra's `isExtra` flipped false → extras-guard test fails) against that safe restore
point, using `git checkout --` only for files with zero uncommitted #73 diff.

**PR #66 round 3 — conformance-flake attribution R1/R2 (2026-09-25):** Fixed the last two items from Rick's round-2 re-review on the C#/xUnit conformance harness (`tests/conformance`) without touching Beth's overall design. R1: a single late backend error was cascading into failing every remaining scenario because (a) the charge to the previous scenario never consumed/advanced the count it charged, so the same line got re-attributed to every subsequent scenario, and (b) `Assert.Fail` for that charge ran *before* the `try`/`finally` in `ConformanceFixture.RunAsync`, so the failing scenario itself was silently dropped from the report instead of being recorded — moving the charge+fail inside `try`/`finally` (with a `postBodyRecorded` guard) fixed both at once; also seeded a `StartupScenarioName` pseudo-scenario so pre-scenario startup errors fail once, by name, instead of surfacing as `<unknown scenario>` and cascading. R2: the unhandled-error count was read twice per checkpoint (once for the charge/check, again for the baseline/record) — collapsed to single-read `BeginScenario`/`EndScenario` methods so a line landing between reads can't be lost. Key lessons: (1) `git worktree add` does not carry over frontend build artifacts — `app/backend/static/` needs `robocopy`-mirroring from the main checkout same as the Python venv, or the full conformance suite fails ~330/456 for reasons that have nothing to do with your change; (2) one CI-only failure in a real-process, wall-clock-timing-sensitive test (ironic, given the PR's whole purpose) was a genuine one-off scheduling flake, confirmed by rerunning just that job rather than assuming a regression; (3) squad personas share one GitHub account — there's no `@handle` per persona, so "@-mention Rick" means naming him in plain text in the comment body, not fabricating a GitHub username (which risks pinging an unrelated real account).

## Learnings — Current (Phase 7)

### 2026-08-06: Ruff Lint Cleanup — 70 Errors → 0
- **Baseline:** 70 ruff errors across backend + scripts (F401×29, I001×13, F541×6, UP015×6, UP006×5, F841×5, UP017×2, UP035×2, E402×1, UP041×1). 354 tests passing.
- **Auto-fixed (66):** `ruff check . --fix` handled all F401 (unused imports), I001 (import sorting), F541 (empty f-strings), UP006 (Dict→dict annotations), UP015 (redundant open modes), UP017 (timezone.utc→UTC alias), UP035 (typing.Dict→dict), UP041 (TimeoutError alias). Zero test regressions.
- **Hand-fixed (4):** F841×5 — three `sid = create_session()` in test_rtmt.py were side-effect calls (removed assignment); two `response = upload_documents()` in notebooks were side-effect calls (renamed to `_`). None were latent bugs. E402×1 — `from collections import Counter` mid-cell in notebook, suppressed with `# noqa: E402, I001` (legitimate placement near usage in 150-line data-processing cell).
- **No global ignores added.** All suppressions are targeted per-line noqa with justification.
- **Key Insight:** The test_rebrand_verification.py branding guard was safe — its f-strings without placeholders were just `f"message"` strings that ruff correctly converted to plain strings; the test's scanning constants were not affected.
- **Test Results:** 354/354 passing after all changes. Zero regressions.

### 2026-08-06: Dependency Update Sprint — Security + Version Bumps
- **Security-Critical Upgrades:** aiohttp 3.10.11→3.14.3 (33+ CVEs including PYSEC-2026-2102, PYSEC-2026-3545, PYSEC-2026-237), python-dotenv 1.0.1→1.2.2 (PYSEC-2026-2270), cryptography ≥50.0.0 added as floor pin (9 CVEs in transitive 46.0.0). pip-audit now reports zero known vulnerabilities.
- **Major Bumps Taken:** azure-search-documents 11.6.0→12.0.0 (verified: breaking changes only affect beta API users, not our `SearchClient`/`VectorizableTextQuery` surface in tools.py), azure-identity 1.19.0→1.25.3, azure-storage-blob 12.24.0→12.30.0, cffi 1.17.1→2.1.1 (required by cryptography ≥50), rich 13.9.4→15.0.0 (only used in setup_intvect.py).
- **Deliberately Skipped:** openai stays at 1.109.1 (latest 1.x) — not imported anywhere in backend code (app uses raw aiohttp WebSockets to Azure OpenAI Realtime), but scripts/notebooks in `scripts/` use `from openai import AzureOpenAI` and would break on 2.x. gunicorn stays at 23.0.0 — no CVEs, UNIX-only server, 26.0.0 is a 3-major jump with insufficient changelog visibility.
- **pyproject.toml Fix:** `target-version` corrected from `"py311"` to `"py312"` — Dockerfile uses `python:3.12-slim`, devcontainer uses `python:1-3.12-bookworm`.
- **Key Insight:** cffi is pinned in requirements.txt but is purely a transitive dependency (not imported in any backend source). cryptography ≥50.0.0 requires cffi ≥2.0, so the two pins must move together. msal constrains cryptography to <51.
- **Test Results:** 354/354 passing before AND after changes. Zero regressions.

## Learnings — Current (Phase 6)

### 2026-03-26: Combo Conversion Sprint — Mod-Stripping + Size Prompting + Regression Tests
- **Part 1 — Mod-Stripping Bug Fix:** When AI sends a combo with mods already embedded (e.g., `SuperSONIC® Bacon Double Cheeseburger Combo (Pickles Only)`), the `combo_base` retained the parenthesized mods while `existing_base` stripped them via `.split("(")[0]`. This mismatch prevented standalone-to-combo removal, causing both items to appear on the ticket. Fix: Added `combo_base = combo_base[:combo_base.find("(")].strip()` after the initial normalization, mirroring how `existing_base` already strips mods. Two-line change in `order_state.py` line 128-129. Key Insight: The combo conversion comparison must be symmetric — both sides need identical normalization. The AI may send combo names with or without mods depending on context, so the backend must handle both.
- **Part 2 — Combo Size Prompting (Unity):** Scoped MENU_AND_PRICING "default to MEDIUM" rule to standalone items only. Added explicit COMBO SIZE PROMPTING block requiring AI to ask for side and drink sizes during combo completion. Updated SUGGESTIVE_SELLING upsell instructions. Three surgical edits to system_prompt.yaml — all 337 tests pass.
- **Part 3 — Regression Tests (Birdperson):** Wrote 7 new tests to `test_order_state.py` covering: (1) combo with mods, standalone without → removed, (2) no-mods regression, (3) different mods on standalone vs combo → stripped, removed, (4) mod carry-forward, (5) multiple standalones selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. All 354 tests passing.

### 2026-03-25: Phase 6 — Critical Combo Conversion Bug Fix
- **Combo Conversion Bug (Demo Blocker):** Fixed double-charging bug where converting a standalone burger to a combo resulted in BOTH the standalone ($6.59) AND the combo ($10.19) in the order, plus separate side ($2.79) and drink ($2.49). Root cause was two missing behaviors in `handle_order_update()`: (1) No auto-removal of the standalone entree when the combo version was added. (2) No post-combo absorption — sides/drinks added AFTER a combo were not recognized as combo components. Fix: `order_state.py` now auto-removes matching standalone entrees on combo add (with mod carry-over, e.g., "(Pickles Only)" transfers to combo), and absorbs post-combo sides/drinks into unfilled combo slots. `tools.py` updated to use return value from `handle_order_update()` for accurate delta text ("included with your combo" / "Upgraded to combo"). System prompt COMBO_PIVOT_RULES updated to reflect automatic backend handling. 9 new tests added, 1 existing test updated. All 346 tests pass.
- **Key Insight:** The combo absorption code only ran when a combo was added (absorbing pre-existing sides/drinks). The common real-world flow is: burger first → combo conversion → side → drink. This requires BOTH entree replacement AND reverse absorption (sides/drinks added after the combo). The code approach is more reliable than prompt instructions — the AI might forget to call remove, but the backend always catches it.

## Learnings — Current (Phase 5)

### 2026-03-25: Phase 5 — RTMT + Tool Calling Test Coverage
- **Test Coverage Sprint (Birdperson):** Two new test files (141 tests) covering the two largest zero-coverage gaps: (1) `test_rtmt.py` (75 tests) — WebSocket lifecycle, SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex, pre-serialized messages, ToolResult/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). (2) `test_tool_calling.py` (66 tests) — search pipeline (formatting, sizes, empty results, errors, caching with TTL/eviction/clear/case-insensitive), OOS annotations, order CRUD (add/remove/quantity limits per-item 10/total 25, incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). Total suite: 337/337 passing (196 pre-existing + 141 new). All Azure/OpenAI calls mocked at boundaries.
- **Flagged Issue (Pre-existing):** `INVALID_MODS` referenced in `tools.py:112` but never defined — would raise `NameError` if any item with parenthesized customizations hit `validate_customization()`. Noted for future review.
- **EchoSuppressor Async:** `on_audio_done()` uses `asyncio.ensure_future()` internally — tests wrapped with `asyncio.run()` for proper event loop context. No issues.

## Learnings — Current (Phase 4 — Archived)

### 2026-03-25: Phase 4 — Demo-Safe Security
- **Token Provider Async Refresh (Task 1):** Replaced blocking `self._token_provider()` call in `_forward_messages` with background refresh loop (`_refresh_token_loop`) that proactively refreshes the Azure AD token every 5 minutes via `run_in_executor`. Cached token served to new connections. Startup warm-up still synchronous (fine for one-time init). Background task starts on app startup, cancels on shutdown.
- **Session/Connection Limits (Task 2):** Added `active_session_count`, `can_accept_session()`, and idle-timeout tracking to `SessionManager`. Max 10 concurrent sessions, 5-min idle timeout — both configurable in `config.yaml` under `security:`. Over-limit connections get a friendly JSON error (`"Server is busy"`) and clean close. Idle checker runs every 60s as a background task.
- **Origin Validation (Task 3):** Added origin check in `_websocket_handler` before WebSocket accept. Same-origin always allowed (no Origin header or matching Host). Cross-origin allowed via `security.allowed_origins` list. Rejected origins logged at WARNING. Default: same-origin only.
- **HMAC Session Token (Task 4):** `create_hmac_token()` and `validate_hmac_token()` utilities in rtmt.py. `GET /api/auth/session` endpoint in app.py returns 15-min tokens. `os.urandom(32)` secret generated per app startup. Auth disabled by default (`require_session_token: false`) — zero demo impact until explicitly enabled.
- **Frontend Token Wiring (Task 5):** `useRealtime.tsx` fetches `/api/auth/session` on mount, appends `?token=...` to WebSocket URL. Graceful fallback if endpoint unavailable (null token = no param). Token refresh on 401 close. No behavior change when backend doesn't require tokens.
- **All 195 tests pass.** One pre-existing flaky perf test (`test_search_formatting_empty_results`, 18ms > 10ms threshold) occasionally fails due to timing sensitivity — unrelated to security changes.

## Learnings — Archive (2026-03-19 through 2026-03-25)

Detailed technical learnings from demo readiness, debugging, and prompt externalization sprints archived here for reference.


- **max_response_output_tokens budgets tool calls AND audio**: In the OpenAI Realtime API, `max_response_output_tokens` is shared between audio/text output AND tool call arguments in the same response. With 250 tokens, the model generates audio first (streaming left-to-right), consuming most of the budget, then has insufficient tokens for tool call JSON — so it silently skips the tool call. Set to 4096 to eliminate this constraint; the system prompt already controls verbosity ("ONE or TWO short sentences max").
- **Silent API error passthrough was a critical diagnostic gap**: `"error"` was in `_PASSTHROUGH_SERVER_TYPES`, meaning OpenAI errors (e.g., rejected session.update with malformed tool schemas) were forwarded to the client without any backend logging. Moved error handling into `_process_message_to_client` match/case with `logger.error()`. Always ensure error-class messages go through the full processing path.
- **response.done diagnostic logging**: Added INFO-level logging in `response.done` to report whether each response contained tool calls or only audio/text. Without this, there's no way to distinguish "model didn't try to call tools" from "tool call was silently dropped." Critical for diagnosing tool-calling regressions.
- **Echo cooldown 0.5s too short for mid-conversation**: After AI finishes speaking, speakers still resonate and mic picks up tail-end echo. 0.5s cooldown wasn't enough — OpenAI's VAD interprets the residual audio as a new speech turn, triggering a self-talk loop. 1.5s cooldown (3.0s for greeting) provides adequate margin. The delayed second buffer flush (`loop.call_later`) catches echo audio that accumulates DURING the cooldown window — the immediate flush at `response.audio.done` only clears what's already there.
- **System prompt patience instructions cause self-talk**: "If the guest pauses, give them space" was interpreted by the model as permission to generate unsolicited patience responses ("No rush!", "Take your time!"). In a voice system with echo, silence-after-echo looks like a pause → model fills it → echo repeats → infinite loop. Replace with explicit "NEVER speak unless the guest has spoken first" to break the cycle.
- **Delayed buffer flush pattern**: `loop.call_later()` can't call coroutines directly. Use `asyncio.ensure_future(coro)` inside a regular callback. Default arg `tws=target_ws` captures the websocket reference at definition time, preventing closure issues if the outer variable changes.
- **Verbose logging architecture**: Dedicated `sonic-verbose` logger (separate from `sonic-drive-in`) with per-session toggle via `extension.set_verbose_logging` WebSocket message + `VERBOSE_LOGGING` env var for always-on. Extension messages intercepted in `from_client_to_server()` and NOT forwarded to OpenAI. The `_vlog()` helper checks per-connection `verbose` flag OR global `_VERBOSE_GLOBAL`. Audio data never logged (floods terminal) — only frame counts and message types. Tool calls get full lifecycle logging with args, result (truncated to 500 chars), direction, and execution time.
- **Per-connection file handler lifecycle**: `session_file_handler` is a per-connection `logging.FileHandler` tracked via `nonlocal` in `from_client_to_server()`. Attached to `vlogger` when enabled, removed+closed in the `finally` block on disconnect. Uses `line_buffering=True` via `stream.reconfigure()` for immediate flush. The `_LOGS_DIR` is `pathlib.Path(__file__).parent / "logs"` — relative to rtmt.py, not CWD. `_VERBOSE_LOG_FILE_GLOBAL` env var attaches a module-level handler at import time (separate from per-connection handlers).
- **Search index ingestion architecture**: Three notebooks in `scripts/`: (1) `sonic_menu_ingestion_search.ipynb` — reads from `sonic-menu-items.json` (1334 products, nested Sonic API format), the original production ingestion. (2) `menu_ingestion_search_json.ipynb` — reads from `menuItems.json` (flat 50-item format), simpler and correct for current use. (3) `menu_ingestion_search_pdf.ipynb` — PDF-based ingestion (not relevant). The JSON notebook is the one to use going forward — it reads `menuItems.json`, generates 3072-dim embeddings, and uploads in batches of 15 to the index named in `AZURE_SEARCH_INDEX` env var. The `structured_menu_items` file at repo root is a reference snapshot — the notebook reads directly from `menuItems.json`, not from it.
- **rtmt.py Code Organization (Phase 3)**: Broke 766-line god file into 3 focused modules: `session_manager.py` (154 lines — SessionManager class for session lifecycle, greeting state, ContextMonitor for token tracking), `audio_pipeline.py` (199 lines — EchoSuppressor class, verbose logging infrastructure, audio constants/markers), and `rtmt.py` (586 lines — thin orchestrator, RTMiddleTier, WebSocket routing, message processing). Public API unchanged — `from rtmt import RTMiddleTier, ToolResult, ToolResultDirection, Tool, RTToolCall` still works. No circular imports. EchoSuppressor encapsulates the ai_speaking/cooldown_end/greeting_in_progress state machine with clean methods (should_suppress_audio, on_audio_delta, on_audio_done, on_speech_started, on_barge_in). SessionManager owns _session_map, _sent_greeting, and _context_monitors dicts — single point of cleanup in cleanup_session().
- **Context Window Monitoring**: Added ContextMonitor class in session_manager.py. Tracks estimated token usage per session using ~4 chars/token heuristic. Logs WARNING at 80% and CRITICAL at 95% of configurable max_tokens (128K default). Tracks: system message, tool schemas, tool call args/results, AI response content, user transcriptions, greeting text. Config in config.yaml under `context:` key. No truncation — monitoring only. Warns once per threshold per session (no spam).

- **WS transport: "Received frame with non-zero reserved bits" root-caused and fixed (2026-09-22, `fix/ws-transport`)**
  - **Which path fired:** the aiohttp reader check `elif rsv1:`, i.e. RSV1 on what it thinks is a continuation frame. In 3.14.2/3.14.3 (`_websocket/reader_py.py` ~402), control frames update `_compressed` but not `_frame_fin`. On a fresh socket whose *first client frame is a PONG*, `_compressed` gets pinned to FALSE, so the next deflated data frame is rejected with 1002.
    - Upstream: aio-libs/aiohttp#13274, introduced by #12988, fixed by #13302. The fix is merged but **unreleased**; 3.14.3 is the latest on GitHub and on the proxy feed.
  - **Reproduction:**
    - The real `RTMiddleTier` plus FakeGARealtime, with a client that offers deflate, idles past one heartbeat (so it autopongs), then sends `session.update` → close 1002. The same thing happens in real Chromium (Edge 153).
    - No failure with `compress=False`, or when the client sends data before the first heartbeat.
    - Production matches: socket 09ccb306 sat ~40s after the idle-close auto-reconnect, so its first client frames were PONGs, and the mic press then died.
  - **Fix:**
    - `web.WebSocketResponse(..., compress=_WS_COMPRESS)`, with `connection.ws_compression: false` in config.yaml.
    - Upstream `ws_connect(..., compress=0)`. Azure OpenAI already declines deflate, so this just makes it explicit.
  - **Trade-off measured:** real aiohttp on loopback, 60s of 24kHz PCM16 as base64 JSON at 10 msg/s.
    - Deflate saves ~33% of bytes (63→42 KiB/s up, 86→58 KiB/s down).
    - It costs ~5× the socket CPU (62ms → ~300ms per minute) and adds ~0.7ms p50 / 1.9ms p95 echo latency.
    - Not worth it for the demo.
  - **Idle close:** `session_manager.IDLE_CLOSE_CODE = 4000`, `IDLE_CLOSE_REASON = "idle_timeout"`. It is application-range, so the browser can tell it apart from 1002/1006/1011.
  - **Tests:** `tests/test_ws_transport.py` (5), all mutation-checked. Reverting `compress` reproduces the production 1002 in the test.
  - **Heads-up:** gunicorn runs `--workers 2` with per-process `order_state` and `app_secret`. That matters for order resume (Part B plan) and for `require_session_token`.
### 2026-09-22 — Realtime model config plumbing (with Unity)
- `configure_realtime_model(rtmt, model_cfg, environ)` in `rtmt.py` is now the single place that applies the `config.yaml` `model:` settings. app.py and `scripts/smoke_realtime.py` both call it, so the smoke check sends exactly what the app sends. It applies:
  - temperature and max_tokens;
  - `transcription_model`, with env `AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL`;
  - `reasoning_effort`, with env `AZURE_OPENAI_REALTIME_REASONING_EFFORT`;
  - `parallel_tool_calls`.
- Env precedence: an empty env value falls back to `config.yaml`, and `off` disables. Watch out: YAML parses an unquoted `off` as `False`, and `normalize_reasoning_effort` treats that as disabled.
- The `infra/main.bicep` container env gains optional `AZURE_OPENAI_REALTIME_REASONING_EFFORT` / `AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL`:
  - they are added via `union()` only when set, so the default deploy is unchanged;
  - `main.parameters.json` maps them from azd env;
  - the voice default is now `${AZURE_OPENAI_REALTIME_VOICE_CHOICE=marin}`.
- **Gotcha:** an azd env value beats the parameters-file default. The `sonic-demo` env had `shimmer` pinned and was updated with `azd env set AZURE_OPENAI_REALTIME_VOICE_CHOICE marin`. That file is gitignored, so other existing environments need the same command.
- azure.yaml has a new **non-fatal** `postdeploy` hook, `scripts/smoke_realtime.ps1` / `.sh`:
  - `continueOnError: true` and `interactive: false`;
  - the wrapper always exits 0 and prints a loud warning on failure or when the check could not run;
  - it skips if there is no venv, or if `SONIC_SKIP_REALTIME_SMOKE=true`.

## 2026-09-22 — feat/voice-reasoning: explicit reasoning_model switch

- Added `model.reasoning_model` (`auto` | `true` | `false`), overridable by env `AZURE_OPENAI_REALTIME_REASONING_MODEL`. It is plumbed through `main.bicep`, `main.parameters.json` and the `azure.yaml` pipeline vars.
  - Precedence: runtime rejection > explicit switch > deployment-name check (the safe default for 1.5 and older).
- The fallback in `rtmt.py` was audited and is correct:
  - Every `session.update` has an `event_id`.
  - A correlated error triggers exactly one minimal resend (type, instructions, tools, tool_choice).
  - There is no loop, unrelated errors are ignored, and the error reaches the browser only if the fallback is also rejected.
  - 1.5's reasoning rejection has no `event_id`, so the in-flight heuristic is required.
- `azure.yaml` adds a non-fatal postdeploy smoke hook. The service is still named `backend`.
- `config.yaml`: `reasoning_effort: low` is now validated by the benchmark. `parallel_tool_calls: null` is kept.

## 2026-09-22 — feat/order-resume Stage 1 (backend)

- `session_manager.py`: attached/detached/ended lifecycle with an injectable clock.
  - Grace hold capped by the remaining idle budget; idle close ends the session before closing 4000.
  - LRU cap on detached sessions and a 15s sweep. The concurrency cap counts attached sessions only.
  - The mic append stream is no longer guest activity; speech_started and transcription are.
- Resume credential: `token_urlsafe(32)`. Only its sha256 is stored, the compare is constant-time, and it is single-use and rotated on every success. Logs show sha256[:8] only.
- `rtmt.py` first-frame handshake:
  - Metadata is deferred until the decision.
  - A late resume gets `not_first_frame` and a re-announce.
  - A stale socket is closed 4002. `extension.end_session` closes 1000.
  - The handler's `finally` detaches, which fixes a mapping leak when the upstream connect fails.
- Rehydration:
  - A transcript ring buffer per session (`history_turns` / `history_chars`).
  - One system `conversation.item.create` (order JSON plus recent turns) after the bootstrap session.update, with the greeting suppressed.
  - A never-greeted resume greets normally.
- Nudge: `resume.nudge_after_seconds` (30), once, gated on `session_configured`. Cancelled by speech_started, a transcript, or a guest `response.create`. It is not guest activity.
- `app.py load_app_secret`: `APP_SESSION_SECRET` env, falling back to urandom for local dev.

## 2026-09-23 — feat/round3

- Reviewed R1's backend integration with the order-resume session layer:
  - Retries go straight to the upstream socket, so `touch()`/idle clock is never refreshed by a retry (not guest activity).
  - The 30 s nudge checks `RateLimitRecovery.busy` before firing; the handler's `finally` detach cancels a pending retry, so nothing fires into a held session.
  - The session.update fallback keeps its correlated-error path; `scripts/benchmark_reasoning.py` unaffected.
- `resilience.rate_limit` block added to `config.yaml`; env override `RATE_LIMIT_RECOVERY_ENABLED` (empty keeps config).

## 2026-09-23 — feat/conformance-harness (#7)

- Built `app/backend/test_hooks.py`: gated by `CONFORMANCE_TEST_HOOKS=1` (literal `"1"` only), `HOOKS_ENABLED` read once at import time, `now(tz)` (fixed instant from `CONFORMANCE_FIXED_NOW`, requires an explicit offset/zone, converts into caller's `tz`), `seconds(env_var, default)` (parsed override or silent fallback to `default`).
- Wired into 4 sites / 7 env vars: `order_state.is_happy_hour()` (`CONFORMANCE_FIXED_NOW`), `session_manager.py`'s idle/grace/nudge/first-frame-timeout constants, `rtmt.py`'s `_SESSION_CONFIGURED_TIMEOUT_SEC` (greeting timeout), `rate_limit.py`'s `RateLimitSettings.from_config()` retry delays. Never touched bicep or the Dockerfile.
- `tests/test_test_hooks.py`: 18 new tests, mutation-checked (hardcoding `HOOKS_ENABLED = True` fails 8/18; restoring is green).
- Found and fixed a genuine pre-existing flaky test directly in the domain the hooks address: `test_combo_orders.py::test_combo_plus_standalone_drink_at_full_price` never patched `is_happy_hour()` unlike its siblings, so it failed whenever run during the real happy-hour window.
- `pytest app/backend/tests -q`: 586 passed, 61 subtests (568 baseline + 18 new). `ruff check .` clean.
- Decision logged: `.squad/decisions/inbox/summer-test-hooks.md`.

## 2026-09-24 — feat/conformance-harness Stage C item N8 (#7)

- Rick's N8 (Python half): `conformance_hooks.py`'s `seconds()` silently swallowed an unparseable override and fell back to `default` — the same "silent ignore" shape item 14 already fixed for `CONFORMANCE_FIXED_NOW`, just left over for the timer overrides. Also fixed a wrong docstring claim that a trailing "IANA zone" is accepted by `_parse_fixed_now` — `datetime.fromisoformat` only accepts a numeric UTC offset (or `Z`), confirmed empirically (`fromisoformat("...America/Chicago")` raises).
- `seconds(env_var, default)` now raises `ValueError` immediately when hooks are enabled and the override is present but unparseable, NaN, +/-infinity, zero, or negative; unset/empty still falls back to `default` untouched. Every call site (`session_manager.py`, `rate_limit.py`, `rtmt.py`) assigns the result to a module-level constant at its own import time, so this makes a bad override fail the backend's startup immediately (non-zero exit) instead of silently running an entire test/production run with a wrong timer value.
- `test_conformance_hooks.py`: removed `test_seconds_falls_back_on_unparseable_override` (it locked in the old silent-ignore behaviour, which is exactly what this item removes); added `TestSecondsFailsFastOnInvalidOverride` — 6 tests: unparseable/NaN(`"nan"`,`"NaN"`)/+-inf(`"inf"`,`"-inf"`,`"Infinity"`)/zero/negative all must raise `ValueError` naming the env var; unset-override and hooks-disabled-with-garbage-value must not raise.
- Mutation-checked: reverted `seconds()` to the old catch-and-fall-back body → all 10 parametrized/direct cases in the new test class failed with "DID NOT RAISE ValueError" → restored → 36/36 green in `test_conformance_hooks.py`.
- `pytest app/backend/tests -q`: 604 passed, 61 subtests passed (up from the 592 baseline reported after Stage B, plus this stage's incremental additions). `ruff check .` clean.
- Beth logged the C#/README/workflow half of this item (`BackendEnvironment.cs` reclassification, the stale CI comment, the "Test hooks" README section) — commit `94f2432` covers both halves in one changeset since they landed together.

## 2026-09-24 — feat/conformance-s1-3 issue #9 (with Birdperson)

- Owned the backend-domain half of the black-box ordering conformance suite: mined cent-accurate golden pricing/business-rule cases out of `order_state.py`/`tools.py`/`menu_utils.py`'s own test suites (`test_order_state*.py`, `test_tools*.py`, `test_order_logic.py`, `test_combo_orders.py`) and `menuItems.json`/`config.yaml`, covering every combo item (all 10), the Route 44 size aliasing (`SIZE_MAP` in `menu_utils.py` — `rt44`/`rt 44`/`route 44`/`44`/`44oz` all normalize to the same "Route 44" display size), the whole-order and per-item quantity caps, the zero/negative-price rejection guard, and the happy-hour 50%-off-drinks window (14:00-16:00 store time, `is_happy_hour()` in `order_state.py`) at all 4 boundary instants. Assembled it into one shared `golden-order-pricing.json` (+ C# loader) so the numbers are asserted identically here and reusable by S4's future backend.
- Confirmed via mutation-check (scratch-edit/revert cycles in `app/backend`, never committed) that the scenarios actually exercise the intended logic rather than passing vacuously — e.g. inverting the tax-rate multiplication, the combo-absorption slot-count math, and the happy-hour boundary comparison operator each broke exactly the expected golden cases and came back green on restore.
- Diagnosed (with Birdperson) two real bugs in the tool-calling path rather than in pricing itself: `tools.py::search`'s try/except doesn't actually cover the code path that raises (the exception happens during result iteration, not the initial call), and `rtmt.py` has no per-tool-call exception boundary at all. Both are pre-existing production risks (an ordinary upstream network hiccup during a search, or any tool bug, currently kills the whole voice session) — flagged for the coordinator via `[Fact(Skip = "Known Python bug: ...")]` rather than patched, since Python changes are out of scope for this stream.
- `app/backend` left byte-for-byte untouched: `pytest app/backend/tests -q` 604 passed/61 subtests matches baseline exactly; `ruff check .` clean.

## 2026-09-25 — PR #66 round 3 conformance flake fix (R1/R2 fixes)

- Revised PR #66 (round 3) after Beth's R2 revisions, addressing Rick's R2 feedback. Reviewer lockout applied — did not consult Birdperson or Beth, revised solo.
- **R1 fix:** One late error cascading into every remaining scenario. Three compounding bugs in `ScenarioErrorAttribution` / `ConformanceFixture.RunAsync`: (1) charge to previous scenario didn't advance watermark/consumed count, so same line re-attributed to B, C, D...; (2) `Assert.Fail` for charge ran before `try`/`finally`, so current scenario disappeared from report entirely; (3) startup errors had no scenario to charge, surfacing as `<unknown scenario>` and cascading via bug 1. Fix: make charge always consume; move charge+`Assert.Fail` inside `try`/`finally` with `postBodyRecorded` flag; seed `StartupScenarioName` pseudo-scenario.
- **R2 fix:** Unhandled-error count read twice per checkpoint (start and end), creating window where a line landing between reads was silently lost. Fix: collapsed to `BeginScenario`/`EndScenario`, each taking single snapshot and deriving both charge/check and baseline/record from that one read.
- **Testing:** 7 new unit tests in `ScenarioErrorAttributionTests.cs` (cascade regression, idempotency, startup, single-read proofs). Updated existing real-process integration test to route through `BeginScenario`/`EndScenario`. Mutation-checked all 4 logic changes; each caught by 1-3 tests. Disclosed gap: `try`/`finally` reordering itself needs live fixture+real backend, validated via code review + full-suite regression (456/456 × 5 local runs).
- **Validation:** 456/456 × 5 consecutive local runs, 33 targeted harness tests 10/10, `pytest app/backend/tests -q` 875/125, `ruff check .` clean, CI green on 0ad194e with one unrelated timing flake (rerun passed clean).
- **Learnings for team:** Worktree frontend/static assets require robocopy mirror from main checkout (git worktree add doesn't copy build artifacts). A single CI flake in timing-sensitive real-process test is expected; rerun specific job (not whole suite) to confirm environmental vs regression. Persona mentions in GitHub PR comments post under shared account, identify in body text, don't fabricate @handles.
- Rick requested R3 changes (timing-flake in the de-flaked test itself); Summer locked out per reviewer-lockout rule.
- **PR #66 merged as 7ca056d (2026-09-25):** Participated in round 3 revision of conformance flake fixes covering R1 cascade and R2 double-read. Lesson: event-driven waits preferred over wall-clock margins; attribute errors to the scenario that caused them; never rely on timing windows in tests under parallel load.

## 2026-09-26 — PR #92 issue #70 (P2-1) Persona pack skeleton and loader, Sonic only

- Built `personas/` per ADR-001/persona-architecture.md: `persona.schema.json` + `menu.schema.json` (JSON Schema draft 2020-12, `additionalProperties: false`), `personas/sonic/{persona.json,prompts/*.yaml,menu/menuItems.json,assets/**}`. `persona.json` uses real hardcoded values snapshotted straight out of `tools.py`/`order_state.py`/`config.yaml` (SIZE_MAP, INVALID_MODS, happy-hour window, extras allow/block sets, MOCK_MACHINE_STATUS) rather than the design doc's abridged example — the doc is illustrative, the runtime constants are the actual contract to preserve for "no behavior change."
- New `app/backend/persona_loader.py`: `PersonaCatalog.load()` dual-validates every enabled pack (raw `jsonschema.validate()` first, then Pydantic `extra="forbid"` models) and raises `PersonaValidationError` naming the persona id + file + field on any violation. Wired into `create_app()` as a startup gate before prompt loading — an invalid pack now `sys.exit(1)`s with a clear message instead of the backend silently limping along on partial config. 13 dedicated mutation tests (missing/extra/wrong-type fields, id/folder mismatch, bad enum values, malformed JSON) each confirm the loader actually rejects what it's supposed to, not just accepts what it's supposed to.
- **Deleted `manifest.yaml` as dead data** after confirming nothing reads its `model_config` block (app.py/rtmt.py get those values from config.yaml/env vars) — its only live purpose, filename mapping, is replaced by a fixed-filename convention (system_prompt.yaml, greeting.yaml, tool_schemas.yaml, error_messages.yaml, hints.yaml). Worth checking for this pattern elsewhere: config files that look load-bearing but are actually only read by their own now-removed code path.
- **Container path-depth gotcha**: `app/backend/*.py`'s repo-root-relative fallback (`Path(__file__).resolve().parents[2]`) is correct in the dev checkout but silently wrong once Docker flattens `backend/` onto `/app` (only reaches one level up, not the real repo root) — the fix has to be an explicit `ENV PERSONAS_DIR=/app/personas` in the Dockerfile, not a cleverer relative-path formula. Any future module doing repo-root-relative path resolution needs the same explicit env-var escape hatch for the container layout, not just for dev.
- Copied (not moved) `menuItems.json`/assets into the pack rather than deleting the frontend's copies, since `app/frontend/src/**` was explicitly out of scope this wave (owned by Morty/#80) — the pack copy is the new backend source of truth; the frontend's own copy is unwired legacy duplication to be cleaned up in a later wave once frontend ownership allows it.
- Validation: `pytest app/backend/tests -q` 899 passed/125 subtests (baseline 875 − 1 removed manifest-loader test + 25 new persona-loader tests = exact match), `ruff check .` clean, full conformance suite 458/458 (baseline 458, zero regressions) after repointing `RepoPaths.MenuItemsJsonPath` to the pack.
- PR #92 opened into dev (head `b0b4375`), not merged. Repo renamed to swigerb/AzureAIDriveThru mid-task as flagged — old URLs still redirected fine, no action needed.

## 2026-09-27 — PR #94 issue #71 (P2-2) Per-item menu fields and data-driven classification, Sonic

- Rick's #92 review required a FIRST commit (before any #71 change) adding a drift guard: the frontend's legacy `menuItems.json`/asset copies (still live pending #80) may lag the persona pack as a strict subset but must never contradict it on shared items (sizes/prices/description), and five duplicated asset groups (logo, favicon, 4 apology clips, 2 demo JSON files) must stay byte-identical. Landed as its own commit (`d598eae`) with an explicit docstring note that #80 (F3/F4/F7) retires both the frontend copies and this guard — whoever picks up #80 should delete the test file, not maintain it.
- Added the #51-designed per-item fields to all 60 Sonic menu items: `comboSlot`, `happyHourDiscounted`, `aliases`, `isExtra` on every item; `bundle` on the 10 Combos items; `requiresMachine: "ice_cream_machine"` on the 10 Shakes & Ice Cream items (value sourced from `persona.json`'s own `machines` block, not invented). Deliberately left #72's float fields and #73's keyword-fallback removal untouched — scope creep across sibling issues is exactly what a "reads the design but keys off #71's specific acceptance criteria" pass is supposed to prevent.
- Rewrote `menu_utils.py`'s classification core to be data-driven: one `_load_menu_data()` pass builds `_MENU_ITEM_FIELDS`/`_MENU_ALIAS_MAP`; `infer_category`/`infer_combo_component`/`is_happy_hour_discounted` all resolve aliases first, then look up the data, falling through to the (unchanged) keyword fallback only for genuinely off-menu items. Removed five now-redundant name-keyed Python tables (`_COMBO_SIDE_ITEMS`, `_TOTS_ALIASES`, `_COMBO_DRINK_CATEGORIES`, `_SUNDAES`, `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED`) plus the dead `_resolve_combo_side_alias` helper.
- **Golden table checked against the data, not generated from it** — added `GoldenTableCheckedAgainstPackDataTests`, which reads `menuItems.json`'s raw JSON fields directly (bypassing `menu_utils.py` entirely) and diffs against `golden-menu-categories.json`, sitting alongside the pre-existing check of the classification functions against the same table. Two independent readings of one oracle, neither derived from the other — satisfies the letter of "checked against, not generated from" literally.
- One deliberate, sanctioned behavior change promoting alias resolution to category lookups too (previously only combo-slot resolved aliases): `infer_category("Tater Tot")` now returns `"hot dogs & tots"` instead of the old keyword-fallback guess of `"sides"`. Pinned explicitly with `AliasResolvesIdenticallyOnEveryLookupTests` (asserts every one of Tots' 16 alias forms is identical to the canonical item on category/combo-slot/happy-hour-discount) rather than left as an unremarked side effect a future reviewer would have to reverse-engineer from a diff.
- **Tooling gotcha for the team:** inside a single `powershell` tool call, `cd`/`Set-Location`/`[Environment]::CurrentDirectory` does not reliably propagate to (a) subsequent `[System.IO.File]` static calls using relative paths, or (b) a child `python.exe` process launched later in the same script — its CWD does not inherit the parent session's directory change. Fix that actually works: use fully absolute paths everywhere (inside both PowerShell and any Python script it launches) rather than relying on any directory-change call persisting across process boundaries.
- Mutation-checked all three axes: frontend price flip and asset byte flip each correctly failed the drift guard (and only the drift guard) and were restored byte-identically; a data flip (`Tots.comboSlot: "sides" -> "none"`) correctly failed both golden-check test classes *and* the `GoldenMenuComboSlotTheoryTests` conformance scenario (59/60 rows, exactly the mutated row), then was restored byte-identically with a clean `git diff` and full green re-run.
- Validation: `pytest app/backend/tests -q` 908 passed/141 subtests (baseline 903/125; net math: +4 drift guard, +1 golden-vs-data test, +1 alias-equivalence class with +16 subtests from its `subTest` loop over 16 alias forms, −1 redundant flag-patch test folded into its sibling). `ruff check .` clean. Full conformance suite 458/458 (baseline 458), 1 pre-existing timing-sensitive test (`CapturedProcessOutputWaitTests`, unrelated to menu/persona code) confirmed flaky via isolated rerun (15/15 alone).
- PR #94 opened into dev (head `11037d3`, two commits: `d598eae` drift guard, `11037d3` #71 implementation), not merged. Decision note: `.squad/decisions/inbox/summer-71.md`.

## 2026-09-27 — PR TBD issue #72 Part 2 (P2-3) Full Sonic menu export import

- Followed Rick's #72 Part 2 rules comment + PR #98 review verbatim: imported all 106 export products missing from `personas/sonic/menu/menuItems.json` (172-product export via `scripts/extract_production_items.py`, 66 already overlapping pre-existing items) — 74 -> 180 items, 6 -> 12 categories. Zero exclusions: no `isAvailable:false` or zero-price product exists anywhere in the export.
- Built the 106 new items programmatically from a field-table rule engine (combo/drink slot + happy-hour discount by category, `requiresMachine` from description text for ice-cream/slush items, `bundle:{slots:[sides,drinks]}` only when the export's own `ingredientRefs` show real swappable side+drink component groups — never guessed from "Combo"/"Dinner"/"Meal" appearing in the name), then hand-verified every decision via a full per-item audit report before merging into the real menu file.
- Removed two pre-existing items confirmed genuinely absent from the export (not naming mismatches): `"Jr Double Cheeseburger Combo"`, `"All-American SONIC Smasher™ Combo"` (ADR-001 decision 4). This broke `test_persona_pack_drift_guard.py` because the untouchable `app/frontend/**` copy still names both — fixed with a narrow, named, commented test exception (`_KNOWN_FRONTEND_ONLY_ITEMS_PENDING_ISSUE_80`) rather than weakening the guard generally; it'll be deleted once #80 retires the frontend's duplicated menu copy.
- Added 2 new `isExtra` items from the export's Regular-tier priced modifiers (Sweet Cream $0.50, Jalapeños $0.80) plus the "whip topping" / "half and half tea" aliases Rick asked for, and one alias fix of my own (`"jalapenos"`, ASCII) since `_menu_key()` strips ®/™/curly-apostrophes but never diacritics — a real normalization gap, not decoration. Updated the system prompt's `Extras:` line (the only prompt text naming priced items now contradicted by the data) and its pinned-count test 3 -> 5.
- Regenerated `tests/conformance/testdata/golden-menu-categories.json` (74 -> 180 rows) hand-written from the SAME field-table rules used to build the items — not generated from `menuItems.json` — preserving issue #71's "independent oracle" convention. Updated the two C# hardcoded-count spots (`GoldenMenuComboSlotTheoryTests.cs`, `GoldenMenuCategoryData.cs`) to match.
- **Mutation-check turned up a real, honest coverage gap worth flagging to the team:** flipping a limeade's `happyHourDiscounted` breaks 6 tests (strong coverage), but dropping a slush's `requiresMachine` breaks zero — that field has no backend consumer or golden-table column yet, it's schema-only. Didn't invent a test to paper over this since wiring it up is new backend logic, out of scope for a data-only import; documented it plainly in the decision note instead of silently declaring victory.
- **Worktree gotcha (same shape as the #71 note, now confirmed for two Part-2-in-a-row runs):** `git worktree add` never copies `app/backend/static` (frontend build output, gitignored) — a fresh worktree fails `test_app.py`/`test_performance.py` (7 tests) with `'...\\static' does not exist` until you run `npm ci && npm run build` in `app/frontend` inside that worktree. Confirmed these 7 failures are 100% environmental (present before any of my menu changes, gone after building), not a menu-data regression — worth a permanent note for any future worktree-based task.
- Validation: `pytest app/backend/tests -q` 916 passed/149 subtests (matches the task's stated baseline exactly). `ruff check .` clean. C# conformance suite (`dotnet test Conformance.slnx --filter "Category!=Browser"`, `CONFORMANCE_BACKEND=python`, .NET 11 RC1) 578/578 passed, golden-row Theory grew from 74 to 180 cases with the golden table.
- PR opened into dev as draft (flagged items above remain for Rick's call: French Toast Sticks Combo bundle anomaly, Strawberry Cheesecake Cream Cooler machine ambiguity, 7 pre-existing combo price/size mismatches, the requiresMachine coverage gap). Decision note: `.squad/decisions/inbox/summer-72b.md`.

## 2026-09-27 — PR #101 (squad/76-personas-v2) round 3: closing the regen-script laundering gap

- Took over a rejected-revision round I didn't author: Birdperson opened #101, Beth revised it per Rick's round-2 review, Rick rejected Beth's revision for exactly one remaining item (both were then locked out). Read only `gh pr view 101 --json reviews,comments` plus the four named files — never touched the rest of the PR's round-1/round-2 diff.
- **The gap:** `regenerate_rebrand_baseline.py` rewrote every existing entry's `max` to today's real count unconditionally. So "add a Sonic word to shared code, the guard test fails, run the regen script, it passes" laundered a real brand-word increase through what looked like a routine refresh — the guard test couldn't tell, since it was comparing against the file the script had just rewritten to match the new (higher) reality.
- **Fix, default (no flags):** the script now computes a plan against the checked-in baseline before writing anything — a count strictly below its `max` lowers it; a `(file, brand)` pair with zero remaining hits is dropped entirely; but any RAISE or any brand-new `(file, brand)` entry is refused: printed to stdout, exit 1, and `_dump_baseline` is never called — the file on disk is left byte-for-byte untouched (verified in tests via `read_bytes()` before/after).
- **Fix, explicit override:** `--allow-increase --increase-reason '#123'` is required together — `--allow-increase` alone (no reason, or a malformed one that doesn't `fullmatch(r"#\d+")`) still refuses and still writes nothing. With both, the raised/new entries get `increase_reason` stamped (carrying over the entry's pre-existing `issue`/`reason` unchanged); a genuinely new entry still gets `issue: "#TODO"` and the script still exits 1 to force a human to fill in a real issue before committing — `--allow-increase` only unblocks the count-raise refusal, not the pre-existing "new entries need a real issue" requirement, which I kept as-is on purpose.
- Added `increase_reason` as a new optional `BaselineEntry` field (`rebrand_scan.py`, empty string when absent) and a matching test in `test_rebrand_verification.py` (`test_baseline_entries_with_an_increase_reason_have_a_valid_format`) validating it the same way `issue` is validated, but only when non-empty — so 69 of the 70 real baseline entries (no genuine raises exist yet) carry a blank `increase_reason` with no guard tripping.
- Refactored the script's core into a pure `_plan(counts, previous, *, allow_increase, increase_reason) -> (entries, blocked)` function specifically so it's unit-testable without scanning the real repo or touching the real committed `rebrand_baseline.yaml` — tests patch `_count_brand_occurrences`/`_load_baseline` with synthetic data and point `BASELINE_PATH` at a throwaway temp file. New `test_regenerate_rebrand_baseline.py` (10 tests): default-lowers-only, default-drops-zeroed-entry, refuses-raise (file unchanged), refuses-new-entry (file unchanged), allow-increase-without-reason still fails, allow-increase-with-malformed-reason still fails, allow-increase-with-valid-reason succeeds and stamps the field, a brand-new entry via `--allow-increase` still flags `#TODO` and exits 1, an unchanged count preserves its prior `increase_reason`, and a genuine lower clears a stale one.
- **Gotcha caught and fixed before it became a false failure:** the new test file itself uses "sonic" as fixture/example data ~33 times for `BaselineEntry` objects, which the brand-word guard's own scan would otherwise flag as an unbaselined leftover. Added it to `rebrand_scan.py`'s `BRAND_EXCLUDED_FILES` alongside the pre-existing `test_rebrand_verification.py`/`rebrand_scan.py`/`regenerate_rebrand_baseline.py` self-exclusions — same rationale, not a new pattern.
- **Mutation-checked the refusal path specifically** (the thing this whole round exists to fix): temporarily replaced the `if blocked:` refusal branch with an unconditional `_plan(..., allow_increase=True, ...)` call (bypassing every check). Reran `test_regenerate_rebrand_baseline.py` — exactly the 4 tests that assert refusal behavior failed (`test_refuses_raise`, `test_refuses_new_entry`, `test_allow_increase_without_reason_fails`, `test_allow_increase_with_malformed_reason_fails`), the other 6 (lower/drop/success paths) stayed green as expected. Restored the original file from a `.bak` copy; reran clean (10/10).
- **Sanity-ran the regenerated script against the real, live baseline** (not just synthetic test data): default run against `app/backend/tests/rebrand_baseline.yaml` as it exists in the repo today exits 0 and changes nothing except adding the new blank `increase_reason: ''` field to all 70 entries plus the header-comment update describing the new flags — zero `max` changes, confirming no hidden real-world raise was lurking and that the lower-only path is a true no-op on unchanged data.
- Validation: `python -m pytest app/backend/tests -q` 970 passed/154 subtests (baseline 954 stated in the task /+16 net: +10 regen-script tests, +1 increase_reason-format test, +5 for the optional CI ratchet-check's own script/tests) after `npm ci && npm run build` in `app/frontend` (same worktree-static gotcha as my #71/#72 rounds — `app/backend/static` doesn't exist in a fresh `git worktree add` until the frontend is built there). `ruff check .` clean repo-wide.
- **Optional item done too:** added `check_rebrand_baseline_against_base.py` — a small CI-only script that loads the checked-in `rebrand_baseline.yaml` (head) and a base-branch copy, and fails on any `max` increase or brand-new entry that lacks a valid `increase_reason`, closing the gap that a hand-edit of the YAML (skipping the regen script entirely) would otherwise slip past. Wired it in as a new step in `.github/workflows/conformance.yml`'s existing `python-tests` job (not a new job, so `conformance-gate`'s `needs:` array didn't need touching) — `git fetch origin "${{ github.base_ref }}" --depth=1` then `git show origin/$BASE:app/backend/tests/rebrand_baseline.yaml`, guarded to `pull_request` events only (`github.base_ref` is only set then) and tolerant (`|| true`) of the base branch predating the file, matching the script's own graceful skip. Validated the comparison logic locally with three synthetic scenarios (identical files pass; a lowered max in a hand-built base file passes; a raised max without `increase_reason` fails with the exact offending entry printed) before wiring it into the workflow, plus 5 new unit tests (`test_check_rebrand_baseline_against_base.py`) covering missing-base-file/raise-without-reason/raise-with-reason/new-entry-without-reason/lower-never-needs-reason.
- Commented on PR #101 addressed to Rick describing the change; did not merge. Head SHA and CI status recorded in the PR comment itself.

## 2026-09-27 — #74 (P2-5) persona binding (misfiled to .squad/history.md; moved here per Rick's PR #102 review item 6)

- A fresh `git worktree add` has neither a Python `.venv` nor a built frontend
  (`app/backend/static/`) -- **both** are required before `dotnet test Conformance.slnx` can even
  launch the Python backend subprocess, and the failure symptoms don't say so directly: a missing
  `.venv` shows up as `System.ComponentModel.Win32Exception` trying to spawn
  `.venv\Scripts\python.exe` (~474/589 fail), a missing `static/index.html` shows up as
  `System.InvalidOperationException` from `PythonBackendLauncher` (~463/589 fail). Fix each fresh
  worktree once with `python -m venv .venv` + `pip install -r app/backend/requirements.txt`
  (real venv, never a junction/symlink) and `npm install && npm run build` in `app/frontend`.
  Neither step touches tracked files (both outputs are gitignored) -- pure one-time local setup,
  not a code change.
- The "default context" delegation pattern (every persona-specific lookup falls back exactly to
  the pre-existing module-level/env-driven behavior when no persona catalog is configured) let
  the whole persona-binding feature (#74) land with zero changes to the existing test suite's
  pass/fail set -- the entire risk of a large cross-cutting change (order_state/menu_utils/tools/
  rtmt/app.py) can be pushed into new, additive code paths only exercised when a persona catalog
  is actually configured, rather than forking existing logic. (Rick rejected this exact pattern
  in PR #102 round 1 -- see Squanchy's round-2 revision for why the catalog needed to become
  mandatory instead.)
- `BackendUnderTest.IsPython` exists in the C# conformance harness (`BackendUnderTest.cs`) as a
  gating property for backend-specific scenarios, but has zero actual test-file usages anywhere
  in the suite -- only a hypothetical pattern documented in the README. Before relying on it for a
  Python-only feature's conformance rows, expect to have to build the first real usage from
  scratch, not just follow an existing example.
## 2026-09-27 — #75 (P2-6) model catalog, processor interface, per-session realtime model selection

- New worktree `SonicAIDriveThru-wt\p2-75` off `origin/dev` (71daa9f, which already carries #74's
  mandatory persona catalog + per-session binding). Branch `squad/75-model-flexibility`, draft PR
  #106 pushed after the first landing increment, four commits total, marked ready once validation
  was green.
- **Catalog is deliberately environment-agnostic**: `config.yaml`'s `models.catalog` entries carry
  no deployment name at all -- just `id`, `pipeline` (realtime|cascade|local), `label`, and
  capability flags (`reasoning`, `toolCalling`, `runtime`). Deployment names live entirely in a new
  env var, `AZURE_AI_MODEL_DEPLOYMENTS` (JSON map of catalog id -> Foundry deployment name), parsed
  by `ModelCatalog.load()`. This mirrors the persona catalog's own env-var-for-environment-specifics
  pattern and kept #75 from ever needing to touch `infra/**` (Unity's #93 lane) -- I only needed to
  *name* the env var and post it as a PR/issue comment, never define the Bicep side of it.
- **The processor interface question turned out to already be answered**: `processors.py`'s
  `ProcessorRegistry` class existed from the very first commit of this feature (written before I
  picked back up after a compaction) -- #75's actual remaining processor-interface work was just
  wiring `rtmt.model_catalog` / `rtmt.processor_registry = ProcessorRegistry([rtmt])` into
  `app.py`, not building a new registry class. Don't assume "processor interface" work means new
  scaffolding; check what's already there before designing more.
- **`_startup_checks` gotcha**: adding a new key to the module-level `_startup_checks` dict for the
  model catalog (even defaulted `False`) broke 3 existing `/health` tests in `test_app.py` that
  manually call `_startup_checks.update(personas_loaded=True, prompts_loaded=True,
  config_loaded=True, env_vars=True)` with an exact 4-key set and then assert `all(...)` -- a 5th
  key never set `True` by that literal call makes `all()` fail. Resolution: don't add a
  `model_catalog_loaded` key at all; the catalog load is still fail-fast (`sys.exit(1)` on
  `ModelValidationError`, same pattern as the persona catalog), just not reflected in `/health`.
  Documented the omission in a code comment rather than silently skipping it, so the next reader
  doesn't "fix" it by adding the key back and re-breaking those tests.
- **The omitted-`?model=` path never touches the catalog at all, by design** (#75 acceptance:
  "keep behavior identical when no model param is given"): `resolve_realtime_model`'s
  default/persona-default branch resolves straight to the caller's `default_deployment` argument
  and always sets `reasoning=None` (meaning "use the old deployment-name heuristic"), unconditionally,
  before the catalog or `AZURE_AI_MODEL_DEPLOYMENTS` is even consulted. I initially wrote a test
  asserting `get_model_deployment(sid) is None` for this path, which is simply wrong -- the
  deployment is always the concrete configured default, never `None`. Only `reasoning` is `None`
  here.
- **Reasoning override has two independent gates, not one**: `reasoning_enabled()` requires BOTH
  `normalize_reasoning_effort(self.reasoning_effort)` to be non-`None` (i.e. `reasoning_effort` is
  actually configured, e.g. from `model.reasoning_effort` in a real deployment's config) AND
  `_reasoning_model(reasoning_override)` to be true. A test that sets `reasoning_override=True`
  (via selecting a catalog `reasoning: true` model) but never configures `rtmt.reasoning_effort`
  will observe no `reasoning` field at all and look like the override is broken -- it isn't; the
  effort gate is just unmet. Any reasoning-override test needs `self.rtmt.reasoning_effort` set to
  a valid value first.
- **`FakeGARealtime` shared mutable state across sequential `ws_connect()` calls in one test**:
  `_RealtimeHarness.asyncSetUp()` creates exactly one `self.fake` per test *method*. Two sequential
  `ws_connect()` calls in the same test method share that fake's `session["voice"]`/
  `assistant_audio` state -- a second connection requesting a different bound voice than the first
  (after the first produced assistant audio) triggers a genuine `cannot_update_voice` rejection
  from the fake, which cascades into rtmt.py's real fallback-recovery logic and silently strips
  unrelated fields (like `reasoning`) from the observed bootstrap update, producing a misleading
  failure that looks like the feature under test is broken. Fix: one independent test *method* per
  scenario that needs its own fresh upstream session, not two `ws_connect()` calls in one body.
- **Session-creation-vs-upstream-connect race in tests**: `self.rtmt._sessions.active_session_count
  >= 1` goes true inside `_websocket_handler`, *before* `_forward_messages()` is even awaited (and
  therefore before the upstream `ws_connect` happens). A test asserting something about the
  upstream connection itself (e.g. which `?model=` deployment name was dialed) must gate on a
  signal from the fake's own `handler()` (I added `FakeGARealtime.connect_model_params`), not on
  `active_session_count`.
- **Conformance baseline (634) splits across two `dotnet test` filter invocations, matching CI
  exactly**: `--filter "Category!=Browser"` (629 tests) + a separate `--filter "Category=Browser"`
  run (5 tests) = 634. Running only the first filter looks like a passing-but-short run (629/629,
  100% green) that actually silently skips 5 baseline tests -- always run both filters (or drop the
  filter entirely, which the python leg's own `dotnet test Conformance.slnx --no-restore` without
  `--filter` also covers) before reporting a baseline-matching total.
- Mutation checks all done by temporarily editing the exact line the acceptance criteria named
  (`resolve_realtime_model`'s `is_selectable` call for #1, its default-branch deployment expression
  for #2, `ModelCatalog.is_selectable`'s `entry.pipeline == pipeline` clause for #3), confirming the
  expected tests failed, then reverting and re-confirming `git diff` was empty before moving on --
  never left a mutation in place across a tool-call boundary.
- No conformance rows added for model selection itself: the task allowed this ("untagged unless the
  C# skeleton supports it") and the C# skeleton's `[Trait("Dotnet", "ready")]` set doesn't cover
  model selection yet (it's Python-only, `/realtime?model=` isn't in the dotnet-ready scenario list).
- PR #106 marked ready for review (not merged). Posted `AZURE_AI_MODEL_DEPLOYMENTS`'s exact shape as
  a comment on both #93 (Unity's infra PR) and #85, per the task's explicit instruction to comment
  on both rather than just one.

**PR #110 revision (round 2), Rick's review, issue #80 (2026-09-28)**: Revised a REJECTED PR
(original author Morty, locked out) covering ADR-001's runtime persona theming end to end -- all
6 required items + 2 non-blocking items from a single review pass, without consulting the
original author.
- The persona-context fallback fix (delete the hardcoded Sonic `FALLBACK_ID`/`FALLBACK_SUMMARY`/
  `FALLBACK_DETAIL`, use NEUTRAL shared defaults) and moving a persona pack's own copy
  (name/title/ticket/status/hero/disclaimer + a new `ticket.emptyHint` key) into its own
  `persona.json` are two separate, sequenceable slices -- doing the fallback-neutralization first
  and the copy-migration second (rather than together) kept each commit's diff reviewable and let
  the locale-file neutralization build on an already-neutral context file instead of touching both
  at once.
- `rebrand_scan.py`'s per-file ratchet is per-LINE not per-occurrence (`\bsonic\b` word-boundary,
  case-insensitive, one hit per matching source line regardless of how many times it appears on
  that line) -- consolidating multiple same-line-worthy mentions onto fewer lines, or rewording
  comments/test-titles to drop a standalone brand word while keeping compound identifiers
  (`SonicApp`, `sonicPersona` -- no trailing word boundary, so `\bsonic\b` never matches them), are
  both valid, meaning-preserving ways to bring a file back under its baseline `max` without losing
  test coverage.
- **New lesson, cost real CI time**: `regenerate_rebrand_baseline.py`'s lower-only path clears an
  entry's `increase_reason` whenever its count decreases vs. the *local* checked-in baseline
  (`"a genuine lower supersedes whatever justified the old max"`) -- correct for the local
  self-consistency ratchet test, but blind to the separate CI-only
  `check_rebrand_baseline_against_base.py`, which requires `increase_reason` on ANY (file, brand)
  entry that has no counterpart at all on the PR's base branch, independent of whether the count
  went up, down, or is brand new on this branch. A feature branch's own new files/mentions are
  always "new vs base" no matter how many times their local baseline gets regenerated downward
  afterward -- regenerating lower-only after neutralizing a file does NOT mean its baseline entry
  stops needing `increase_reason` if that file never existed on the base branch. Before trusting a
  lower-only regen to be CI-clean, diff the regenerated baseline against `origin/<base>`'s copy
  with `check_rebrand_baseline_against_base.py` directly (not just the local
  `test_rebrand_verification.py` suite) -- the two checks enforce different, only-partially
  overlapping invariants, and only one of them is visible from a clean local pytest run using the
  same working tree's own history.
- A baseline entry's free-text `reason` field is never re-validated by any test beyond format
  checks on `issue`/`increase_reason` -- `regenerate_rebrand_baseline.py` carries it over verbatim
  from the prior entry on every path, so it silently goes stale (still describing deleted code)
  the moment the underlying source changes without a matching manual edit to the YAML. Worth a
  manual sanity pass on `reason` text for any entry whose source lines actually changed, not just
  its `max` count.
- Assembling a scratch `PERSONAS_DIR` (fixture packs' `test-alpha`/`test-beta` + a hard copy of the
  real `personas/sonic/`, no symlinks) plus dummy (unreachable but well-formed) Azure env var
  values is enough to boot the real aiohttp backend locally for UX screenshots -- `app.py`'s only
  hard-fail startup gate is presence of the 4 `_REQUIRED_ENV_VARS` strings; `_check_service_
  connectivity()` is a 5s-timeout, non-fatal, logged-warning-only best-effort probe, and Azure
  credential construction (`DefaultAzureCredential()`) doesn't block startup even with no real
  auth configured, since nothing in this app's static-asset + `/api/personas*` surface actually
  needs a live token until a realtime session starts.
- Playwright's own screenshot `path` writes are sandboxed to its own `browser-output` dir (or the
  MCP host's cwd) regardless of what absolute path is requested -- for any screenshot that needs to
  land in an arbitrary caller-specified directory, capture with a bare relative filename first,
  then move the file with a normal shell command afterward.

## 2026-09-28 — #95/#103/#68 PR #123 revision after Rick's rejection

- Picked up PR #123 (branch `squad/flakes-95-103-68`, head `f1e7bff`) after Rick rejected it and
  Birdperson (the author) was locked out. Worked in a fresh worktree
  (`SonicAIDriveThru-wt\flakes-r2`) off the existing branch -- no new branch, no rebase, just
  additional commits on top.
- **#68's real bug was a same-signal-different-meaning confusion.** The rejected PR added
  `on_guest_audio_forwarded()` cancellation because the flaky test raced on `speech_started`
  timing. But the browser forwards *every* mic buffer to the backend, including pure silence --
  there is no VAD gate on the client side. So "audio forwarded" fires on frame 1 of every guest
  turn, silence or not, and would cancel every pending rate-limit retry the instant a turn starts,
  including the greeting retry (#48). The correct fix was to leave the product code alone (revert
  to `speech_started`-based cancellation, which only fires on genuine detected speech) and instead
  fix the *test*: swap to the long second-retry delay profile (3.6s) so there's real margin, and
  replace the polling assertion with an event-driven wait on the backend's own
  `"Rate-limit retry cancelled: guest started speaking"` diagnostic line via the harness's
  `WaitForDiagnosticsAsync`. Added a dedicated regression test that keeps streaming audio frames
  from the fake browser without ever emitting `speech_started`, asserting the retry still fires --
  this is the row that would have caught the rejected PR's regression directly. Mutation-proof:
  temporarily restoring the old `on_guest_audio_forwarded()` code makes this exact new test fail,
  with the log showing the retry cancelled on the very first forwarded frame.
- **#103's "zero margin" flake wasn't a logic bug, it was a clock-vs-timer mismatch.**
  `WaitForOutputQuiescenceAsync` computed `idleNeeded` once, awaited `Task.Delay(idleNeeded)`, and
  trusted its completion as proof `idleWindow` of wall-clock silence had passed. But
  `Task.Delay`'s underlying timer has coarse resolution (~1ms Linux, ~15.6ms Windows) and can
  complete slightly before that much time has actually elapsed versus `DateTimeOffset.UtcNow`,
  so `completedUtc - lastAppendUtc` can land a hair under `idleWindow` -- a genuine race, not a
  fixable-by-adding-margin problem (Rick explicitly wanted zero margin kept). Fix: removed the
  `firstIteration` special case and made every loop iteration re-derive `quietFor` fresh from
  `_lastAppendUtc` under the lock, so a timer that fired early just causes one more short
  iteration instead of a premature `true`. This one design (no special-casing "why did the loop
  wake up") handles re-arm-via-new-line, timer-shortfall, and deadline-cap-hit uniformly.
  Mutation-proof: since there's no injectable clock/timer seam in production code, I proved the
  fix's necessity two ways -- (1) reverting to the old code and forcing `Task.Delay` to fire 20ms
  early via a scratch-only subtraction (Rick's own suggested mutation) reliably reproduced the
  under-shoot assertion failure; (2) applying the identical forced-20ms-early injection on top of
  the *fixed* code passes cleanly, because the re-check loop absorbs the shortfall. Both scratch
  edits were discarded before committing -- never land a clock-seam hack in reviewed code just to
  make a mutation test easier.
- **A tiny `idleWindow` isn't automatically a good amplifier for timer jitter.** My first mutation
  attempt shrank `idleWindow` to 2ms hoping to make the ~1-15.6ms resolution noise dominate --
  it didn't reproduce the bug at all, because Windows' coarse timer granularity means a 2ms
  `Task.Delay` request almost always *overshoots* to the next tick boundary (rounds up), it
  essentially never fires early when the requested duration is already smaller than the
  resolution period. The early-fire behavior Rick described only shows up when the requested
  delay is large enough that the timer's rounding can land on either side of the target --
  forcing the shortfall explicitly (subtracting a fixed offset from the requested delay) was far
  more reliable than trying to provoke it by making the window small.
- **`app/backend/static` is a hard pytest dependency that isn't checked in.** A fresh worktree
  produced 7-9 unrelated-looking `pytest` failures (`ValueError: static dir does not exist`) until
  `npm ci && npm run build` was run in `app/frontend` -- CI always does this before the Python test
  step; any new worktree/venv setup needs to replicate it or the whole suite looks broken for
  reasons that have nothing to do with the change under review.
- **Before trusting a scary-looking pytest/conformance failure that doesn't match the assigned
  issue, reproduce it on a clean `origin/dev` worktree first.** Two Python failures and one C#
  mirror survived every fix in this PR (`test_resize_wrong_size_price_carryover_...` and
  `test_item_without_bundle_absorbs_nothing`, both pricing/menu bugs, tracked as #121) -- a
  side-by-side scratch worktree pinned to plain `origin/dev` with zero code differences in the
  relevant files reproduced the identical failures, confirming they're pre-existing and unrelated
  rather than something this branch introduced. Cheaper and more convincing than trying to reason
  about it from a diff alone.