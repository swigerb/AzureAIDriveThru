# Project Context

- **Owner:** Brian Swiger
- **Project:** Sonic AI Drive-Thru Voice Assistant — AI-powered voice ordering experience using Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Created:** 2026-03-19

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- **Phase 5 Test Coverage (2026-03-25):** Wrote 141 new tests covering RTMT and tool calling pipelines. `test_rtmt.py` (75 tests): SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex validation, pre-serialized messages, ToolResult/ToolResultDirection/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). `test_tool_calling.py` (66 tests): search query formatting/sizes/empty/errors/cache (TTL/eviction/clear/case-insensitive), OOS annotations, update_order add/remove/quantity limits (per-item max 10/total max 25/incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation hints, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). Total suite: 337 tests (196 pre-existing + 141 new). All Azure calls mocked. Commit: ac27ba9.
- **EchoSuppressor Async Requirement:** `EchoSuppressor.on_audio_done()` uses `asyncio.ensure_future()` internally, so tests must run inside async context (`asyncio.run()`) — plain sync unittest won't work. Document for future maintainers.
- **Pre-existing INVALID_MODS Dead Code:** Referenced in `tools.py:112` but never defined. Would raise `NameError` if any item with parenthesized mods hit `validate_customization()`. Flagged for Summer to review in future sprint.
- **Test Formatting Detail:** `get_grouped_order_for_readback()` format uses plain decimals ("6.46"), not "$6.46" — tests must match actual output.
- **Phase 4 security tests added** (`test_security.py`): 31 tests covering session limits (max concurrent acceptance/rejection, close-and-reopen, idle timeout cleanup, active-session survival), origin validation (same-origin, missing, foreign, allowed_origins list, trailing slash normalization), HMAC session tokens (valid, expired at 15min TTL, malformed, empty, require_session_token flag, signature tampering, payload tampering, wrong secret, exact-expiry boundary), and config.yaml security section verification. Uses lightweight stub classes for session limiter, origin validator, and HMAC token gen/verify to decouple from Summer's in-progress implementation — stubs match expected interface contracts so tests will validate the real code once merged.
- **Prompt loader tests added** (`test_prompt_loading.py`): 37 tests covering PromptLoader YAML loading (valid files, system prompt assembly, greeting, tool schemas, error messages, hints), missing file error paths (brand dir, manifest, system_prompt, greeting, tool_schemas), validation errors (empty sections, missing keys, malformed YAML, non-dict YAML), Jinja2 template rendering (variables, numeric, unknown key fallback, delta templates), and production smoke tests against real `prompts/sonic/` files. Uses pytest fixtures with `tmp_path` for isolated temp brand directories and `patch("prompt_loader._PROMPTS_DIR")` for clean redirection. Total test count now 196 (68 new).
- **Pre-existing flake**: `test_search_formatting_empty_results` in `test_performance.py` intermittently fails with 18ms > 10ms threshold — timing-sensitive, not a real regression.
- **Rebrand verification tests added**(`test_rebrand_verification.py`): 12 tests scan every source file for forbidden terms ("dunkin", "crew member", "coffee-chat"). Excludes `.squad/`, `.git/`, `node_modules/`, `__pycache__/`, `voice_rag_README.md` (attribution), and itself. Targeted checks verify README title, index.html `<title>`, and backend system prompt. Pre-rebrand run: 5 pass, 7 fail — exactly right. Tests report file + line number for every violation.
- Existing test files (`test_app.py`, `test_models.py`, `test_order_state.py`, `test_extras_rules.py`, `test_tools_search.py`) contain zero Dunkin/crew-member/coffee-chat references — no updates needed there.
- The backend system prompt in `app.py` was already rebranded to Sonic before these tests ran, so those 3 targeted prompt tests pass immediately.
- **Team Orchestration (2026-03-19T04-06)**: Rick provided scope analysis, Morty completed frontend rebrand (13 tests pass), Summer completed backend rebrand (69 tests pass), Birdperson created verification tests (12 tests pass).
- **Performance test harness added** (`test_performance.py`): 28 tests covering latency benchmarks (order_state <5ms, search formatting <10ms, JSON serialization <2ms), memory efficiency (add/remove cycle <1MB delta, 100-item order <2MB peak), thread safety (10 concurrent writers, 8 concurrent session lifecycles), session isolation, production readiness (app startup, static files, health endpoint, CORS wildcard check), and Pydantic model serialization speed. All tests use mocks — zero real Azure calls. Full suite now at 100 tests.
- OrderState is a singleton with no locking; thread safety tests pass because GIL serializes Python bytecode, but under true parallelism (e.g., multi-process) this would need a lock. Worth noting for future scaling.
- `aiohttp.test_utils.TestClient/TestServer` is the canonical way to test aiohttp endpoints without starting a real server — used for health and CORS checks.
- **Performance Audit Orchestration (2026-03-19T13-21)**: Team completed full-stack performance sprint with 5 agents. Rick lead: 8 fixes across JSON parsing, token cap, search params, system prompt, JSON caching, VAD timing, and response filtering. Summer: 10 fixes for race conditions, hot-path fast-returns, search caching, compression, gzip, logging, memory. Morty: 9 fixes for AudioContext reuse, zero-alloc buffers, memoization, lazy loading, vendor chunking. Squanchy: 6 infrastructure fixes for Gunicorn async, health probes, auto-scaling, Docker caching. Birdperson: 28 performance tests validating latency, memory, thread safety, production readiness. All decisions documented in decisions.md. Orchestration logs written per-agent.
- **Phase 5 RTMT + Tool Calling tests added** (`test_rtmt.py` + `test_tool_calling.py`): 141 new tests. `test_rtmt.py` (75 tests): SessionManager creation/cleanup/concurrency/greeting/idle-timeout, ContextMonitor thresholds, EchoSuppressor state machine (audio delta/done/cooldown/barge-in/greeting suppression), TYPE_RE regex validation, pre-serialized messages, ToolResult/ToolResultDirection/Tool/RTToolCall value objects, HMAC token create/validate, RTMiddleTier init/attach/config, message processing (session.update injection, passthrough audio, session.created stripping, tool execution with TO_BOTH routing, error logging, malformed JSON), WebSocket handler (origin rejection, token rejection). `test_tool_calling.py` (66 tests): search query formatting/sizes/empty/errors/cache (TTL/eviction/clear/case-insensitive), OOS annotations, update_order add/remove/quantity limits (per-item/total/incremental), get_order/reset_order, tax calculation, upsell hints (burger→combo, drink→addon, side→drink, combo→upgrade), combo validation hints, menu_utils normalize_size/infer_category, extras validation, edge cases (special chars, duplicate items, empty cart). All Azure calls mocked. Total suite: 337 tests (196 pre-existing + 141 new).
- `EchoSuppressor.on_audio_done()` uses `asyncio.ensure_future()` internally, so tests that call it must run inside an async context (`asyncio.run()`) — plain sync unittest won't work.
- `INVALID_MODS` is referenced in `tools.py:112` but never defined — pre-existing dead code. `validate_customization()` would raise `NameError` if any item with parenthesized mods hit that path. Noted but not fixed (not in scope).
- The `get_grouped_order_for_readback()` format doesn't include `$` prefix for totals — it uses plain decimal like "6.46". Tests should match actual format, not assume `$`.
- **Combo conversion mods regression tests (2026-03-26):** Added 7 tests to `test_order_state.py` covering the mod-in-combo-name bug and ensuring symmetric normalization. Key insight: `combo_base` must strip parenthesized mods before comparison with `existing_base` (which already strips via `.split("(")[0]`). Tests cover: (1) combo arrives with mods in name, (2) no-mods regression, (3) different mods on standalone vs combo, (4) mod carry-forward from standalone, (5) multiple standalones with selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. Summer's fix (stripping parens from combo_base before comparison) was already applied — all 7 pass. Total suite: 354 tests.
- **Combo conversion mods regression tests (2026-03-25):** Added 7 tests to `test_order_state.py` covering the mod-in-combo-name bug. Key insight: `combo_base` must strip parenthesized mods before comparison with `existing_base` (which already strips via `.split("(")[0]`). Tests cover: (1) combo arrives with mods in name, (2) no-mods regression, (3) different mods on standalone vs combo, (4) mod carry-forward from standalone, (5) multiple standalones with selective removal, (6) quantity>1 decrement, (7) ® symbol normalization. Summer's fix (stripping parens from combo_base before comparison) was already applied — all 7 pass. Total suite: 354 tests.
- **Flaky time-dependent test fix (2026-08-06):** `test_extras_rules.py::test_allow_extra_when_slush_present` and `test_tool_calling.py::test_tax_on_multiple_items` failed daily 14:00–16:00 CDT because `is_happy_hour()` applied a 50% drink discount during that window, making hardcoded total assertions wrong. Fix: patched `order_state.is_happy_hour` to `False` in `setUp`/`tearDown` for classes with price assertions involving drink items (`ExtrasRuleTests`, `TaxCalculationTests`). Added explicit `HappyHourPricingTests` class (5 tests) and `test_tax_on_multiple_items_during_happy_hour` for positive coverage of the discount path — both discounted and non-discounted totals are now deterministically tested. Verified patch bites by confirming discounted vs full-price totals under each state. Full audit: no other tests in the suite depend on wall-clock time for pricing (combo tests use non-drink items or absorbed items with $0 contribution; `test_performance.py` uses `time.perf_counter()` for benchmarks only; `test_security.py` uses `time.time()` for token expiry logic, not pricing). Suite: 360 passed (354 + 6 new), ruff clean, deterministic under both HH=True and HH=False global patches.
- **Guard test false-negative fix (2026-08-06):** Found and fixed a defect in my own `test_rebrand_verification.py`. Root cause: `EXCLUDED_DIRS` was over-broad — it excluded `.devcontainer`, `.github`, `.vscode`, and `.copilot` from scanning, which meant `.devcontainer/devcontainer.json` containing `"name": "Coffee Chat"` (the old pre-rebrand repo name) survived undetected while the test reported green. This is the exact failure mode the guard test exists to prevent. Fix: (1) narrowed `EXCLUDED_DIRS` to only `.git`, `node_modules`, `__pycache__`, `.venv`/`venv`/`env`, and `.squad` (the latter kept deliberately since it holds legitimate historical rebrand records — added a comment explaining why); (2) removed `.devcontainer`, `.github`, `.vscode`, `.copilot` from exclusions so config/CI dirs are now scanned; (3) fixed the `devcontainer.json` violation (`"Coffee Chat"` → `"Sonic AI Drive-Thru"`, Node `"20"` → `"22"` to match Dockerfile and CI); (4) added `.sh` to `SCAN_EXTENSIONS` and `SCAN_FILENAMES = {"Dockerfile"}` for extensionless file coverage — this surfaced real violations in `deploy.sh` and `docker-build.sh` (`coffee-chat-app`/`coffee-chat-assistant` references), which were also fixed; (5) verified `.ipynb` not worth scanning (too noisy — outputs are generated artifacts, not authored code; team handles notebook cleanup separately). Proof-of-failure test: reintroduced `"Coffee Chat"` into `devcontainer.json`, confirmed test now fails with `[coffee-chat (old repo name)] .devcontainer\devcontainer.json:4`, then restored the fix and confirmed 12/12 pass. Lesson: guard tests are only as good as their scan scope — over-excluding directories defeats the purpose. Full verification: backend 354 pass, frontend build + 13/13 tests pass, `squad doctor` 11/0. Docker build blocked by corporate SSL proxy (environment issue, not code — `node:22-slim` stage started successfully confirming Node upgrade works).
- **Silent tool-call regression test (2026-09-22):** Added `tests/test_session_bootstrap.py` for the $0.00 Carhop Ticket incident.
  - It drives the real middle tier end to end against a fake GA realtime server. The fake enforces the two service rules involved:
    - server VAD auto-responds to mic audio;
    - a `session.update` with a different voice after assistant audio is rejected *wholesale* (`cannot_update_voice`).
  - Key insight: the earlier suite only unit-tested payload shape, and the payload was valid. The bug was *ordering*: browser audio reached an unconfigured session. Only an end-to-end fake with real service semantics can catch that.
  - Mutation-checked:
    - reverting rtmt.py fails 6;
    - removing the bootstrap fails 5;
    - removing the voice strip fails 3;
    - an unconditional picker send fails 1.
  - Also added `GARealtime21SurfaceTests`: reasoning fields pass through but are never sent by default, and the picker offers exactly the 10 documented GA voices (allow-list mutation fails 1).
  - Watch-out: `test_rebrand_verification` flags the sibling brand's name in source comments, so refer to that repo generically.

- **WS transport regression tests (2026-09-22, `fix/ws-transport`)**
  - Backend `tests/test_ws_transport.py`:
    - The handshake does not negotiate permessage-deflate.
    - The exact production framing (server PING → client PONG → deflated data frame) keeps the session alive and the `session.update` reaches the upstream.
    - Idle close delivers 4000/"idle_timeout" and deletes the order session.
    - The config default is off.
    - The upstream `ws_connect` passes `compress=0`.
  - Frontend `hooks/__tests__/useRealtime.test.tsx` (6) mocks `react-use-websocket` and captures the url/options/connect arguments; `status-message` gained 2 tests.
  - Mutations, each of which fails at least one test:
    - Drop `compress=` → 2 fail, with the real 1002.
    - Drop upstream `compress=0` → 1 fails.
    - Idle `ws.close()` default → `1000 != 4000`.
    - Config set to true → 3 fail.
    - `shouldReconnect: () => true` → 1 fails.
    - No `setShouldConnect(false)` on idle → 2 fail.
    - Keep=true on audio → 1 fails.
    - No token gating → 1 fails.
    - No `onReconnectStop` → 1 fails.
    - Stale token on reconnect → 1 fails.
    - `StatusMessage` ignores the notice → 2 fail.
  - The heartbeat test patched to 0.2s passed 10/10 in a flake loop.
### 2026-09-22 — session.update self-healing tests + mutation check
- `tests/test_session_bootstrap.py`: `FakeGARealtime` can now reject updates in configurable ways:
  - `reject_keys`: reject any update carrying the given GA session keys;
  - `reject_every_update`;
  - `echo_event_id=False`, which mimics gpt-realtime-1.5 rejecting `reasoning` with `event_id=None` and `param=None`;
  - it also rejects two unrelated client events: `conversation.item.delete` (echoes the event_id, `param=item_id`) and `input_audio_buffer.commit` (no event_id).
- New `SessionUpdateFallbackTests`, run end to end through the real middle tier:
  - every session.update carries a unique event_id;
  - (a) a rejected bootstrap gets exactly ONE fallback whose session keys are exactly {type, instructions, tools, tool_choice}. Tools are registered, the browser never sees the error, and the next VAD response has the tools;
  - the no-event_id variant of (a) also sets `_reasoning_rejected`, so later updates omit `reasoning`;
  - (b) the fallback being rejected too causes no loop, with and without an echoed event_id. There are exactly 2 updates, and the fallback's error reaches the browser once. A new original gets its own single fallback;
  - (c) the unrelated errors trigger no fallback and are forwarded.
- New `SessionUpdateGuardTests` cover the correlation rules and one-fallback-per-original.
- New `ReasoningAndTranscriptionConfigTests` cover:
  - reasoning is sent only when configured and only on reasoning deployments, and never on 1.5, gpt-realtime, the dated snapshot, mini, or 4o;
  - a client cannot inject `reasoning`;
  - the env/config precedence matrix;
  - the shipped `config.yaml` is rollback-safe.
- **Mutation check:**
  - `_recover_rejected_session_update` returning False fails 4 tests. They are (a) ×2 and (b) ×2, and all time out waiting for a fallback.
  - Removing the one-fallback loop guard fails both (b) tests with `187 != 2` / `180 != 2` updates, i.e. a runaway loop.
  - (c) is the negative control and correctly still passes.
- Backend: 431 passed (baseline 412).

## 2026-09-22 — feat/voice-reasoning verification

- Fallback mutation checks, run on `test_session_bootstrap.py`:
  - Fallback disabled: 4 failed (bootstrap minimal fallback, no-loop, no-loop without event_id, no-event_id recovery).
  - Loop guard removed: 2 failed.
  - Foreign event_id correlated: 2 failed.
  - Reasoning switch ignored: 6 failed.
  - Everything restored: all pass.
- `smoke_realtime.py`: 1.5 answered the TTS phrase instead of reading it aloud. The instructions are now firmer and an empty transcript fails. PASS on 2.1 and 1.5.
- `benchmark_reasoning.py`:
  - Repaired: stub search with the real result format, real order tools, stricter add counts, and realistic size-change history.
  - New: median/p90 output, a `--summarize` mode, and `--resume` for chunked runs.
- Final gate: pytest 434 passed; ruff clean; frontend build OK with 16/16 tests; `az bicep build` 0 errors.

- **Order resume Stage 1 tests (2026-09-22, `feat/order-resume`)**
  - `tests/test_order_resume.py` (42 tests) and `tests/test_infra_resume.py`.
  - They use a FakeClock plus a per-connection fake GA upstream, reusing the `_RealtimeHarness` `fake_class` hook, with no real sleeps over 1s.
  - Coverage:
    - Grace-then-delete; idle deletes immediately and a later resume is rejected; grace capped by the idle budget.
    - LRU cap; the concurrency cap ignores detached sessions.
    - Valid resume keeps the same sid and order; wrong, expired, reused and malformed ids are rejected, and the guest gets a fresh session.
    - Resume is honoured as the first frame only; 4002 goes to the stale socket.
    - Upstream order: bootstrap → rehydration (with the order) → no greeting.
    - The nudge fires once and only after session.updated; it is cancelled by speech, a transcript, or a guest response; 0 disables it.
    - The resume id never appears in logs (caplog at DEBUG plus verbose logging).
  - Mutation checks: 51 mutations (steps 0–3), all killed. Three step-2 survivors were killed after adding tests.
  - Final: pytest 496 passed (baseline 442), ruff clean.

- **Order resume — Stage 2 tests (2026-09-22)**
  - vitest suites:
    - `useRealtime.test.tsx`, 25 tests: resume first frame and never queued, id storage and rotation, each close code's reconnect/clear semantics, end_session plus the fresh socket.
    - `App.resume.test.tsx`, 12 tests: resumed → ticket, mic restart, no reset; gesture fallback; rejected; idle; superseded; gave-up; New order; a fast tap after New order; a tap on a resumed session never waits for a greeting.
    - `recorder.test.ts`, 3 tests, and the StatusMessage notices.
  - Mutations: 16 on the hook, 31 on the app/recorder/notices/ending, and 8 on the e2e. All killed, except two equivalents (`shouldReconnect` duplicated by `setShouldConnect`; an `ended` branch unreachable after the refactor). Survivors A14, A18 and X3 were killed after adding tests; A18 also needed a fix.
  - `scripts/e2e_order_resume.py`:
    - Setup: headless Edge, built frontend, the real RTMiddleTier and real order tools, and a fake GA upstream. 42/42 checks in ~37s.
    - Scenarios: 1011 drop → same ticket and sid, bootstrap → rehydration → no greeting, auto mic, one nudge after the shortened 4s. Also the gesture fallback, a tap while reconnecting, a reload, idle 4000, strict autoplay, and resume ids absent from URLs and logs.

## 2026-09-23 — feat/round3

- Mutation checks (all scripts kept outside the repo):

  | item | mutants | killed |
  |---|---|---|
  | R1 backend | 20 + 4 follow-ups | all; M10 killed after adding a `silent` fake mode; M11/M21/M22 were dead code and removed; M17 killed via `RecoveryUnitTests` |
  | R1 frontend | 15 | 15 |
  | R1 clips | 11 | 11 |
  | R2 smoke | 27 | 27 (a 0.97 threshold first survived; added ~0.95 cases) |
  | R3 locales | 17 | 17 |
  | dz | 5 | 5 |

- Counts: backend 496 → 568 (+61 subtests); vitest 65 → 116.
- `ResumeInteractionTests` covers retry vs nudge (no double response either way), retry not refreshing idle, and detach cancelling a pending retry.

## 2026-09-23 — feat/conformance-harness (#7)

- Designed and stood up `tests/conformance/`: a black-box, language-neutral .NET 11 xUnit v3 suite (`Conformance.slnx`, hand-authored — `dotnet sln add` has a CLI bug on `.slnx` this SDK build) talking to the backend only over HTTP/WebSocket. Three projects: `Conformance.Fakes` (FakeRealtimeUpstreamServer with GaSessionValidator/FrameLog/RealtimeScript; FakeSearchServer answering from `menuItems.json`), `Conformance.Harness` (PythonBackendLauncher, BackendEnvironment, BackendLauncherFactory, RealtimeBrowserClient, RepoPaths, NetworkUtils), `Conformance.Tests` (5 test classes, 8 tests).
- Scenarios: smoke (bootstrap `session.update` first frame, 4 tools + `tool_choice=auto`, greeting waits for `session.updated`), `/health` 200 (JSON-parsed, not substring), `/api/auth/session` token, deflate not negotiated, plus 3 fake-only scripting tests (audio deltas, function calls, rate-limited `response.done`).
- Mutation-checked the smoke scenario against `rtmt.py`: removing the bootstrap send fails both smoke tests with clear timeout messages ("Bootstrap session.update never arrived"); restoring is green again (8/8).
- Green 3× in a row at three points in the work (initial, after Summer's test-hook changes, after Beth's cross-platform fix) — deterministic, no sleeps, `FrameLog.WaitForAsync`-style timeout waits throughout.
- `CONFORMANCE_BACKEND=dotnet` (S2 placeholder) skips cleanly: 5 skip, 3 fake-only pass, 0 fail.
- Decision logged: `.squad/decisions/inbox/birdperson-conformance-suite-design.md`.

## 2026-09-24 — feat/conformance-harness Stage A (Rick's PR #22 review, #7/#11)

- Rick reviewed PR #22 and requested changes; CI was confirmed red because `app/backend/static` (gitignored, built by the frontend) doesn't exist on a clean runner — the coordinator's local "8/8" only passed because that machine had a stale built frontend. Lesson applied: re-validated everything this round from a genuinely clean state (moved `static` aside, confirmed the suite fails with the intended clear message, restored it, then re-ran green).
- Owned/drove the scenario design for items 2, 3, 4, 5:
  - **Item 5** — replaced the shared server-level frame log with a per-connection `FakeRealtimeConnection` (id, own `ReceivedFrames`, api-key, model) plus a `ConnectionRegistry` (`WaitForNextConnectionAsync`, `WaitForNoOpenConnectionsAsync`). Rewrote `SmokeSessionBootstrapTests` to assert only on its own captured connection and to fail if a connection is already open at test start.
  - **Item 4** — new scenario `Voice_cannot_be_changed_after_assistant_audio_has_been_sent`: sets voice pre-audio (accepted), drives a full scripted response through, then asserts a second voice change is rejected with `cannot_update_voice` echoing the request's `event_id`.
  - **Item 2** — new `ResponseDoneRoundTripTests`: waits *past* the greeting for `extension.round_trip_token` on the browser and asserts no `"Traceback"` in captured backend stderr. Mutation-checked by removing `output`/`usage` from the fake's `response.done` body — reproduced the exact `rtmt.py` `KeyError: 'output'` traceback that silently kills the connection (root cause of the missing round-trip token); restoring is green again.
  - **Item 3** — new `UpdateOrderToolCallTests`: scripts a real `update_order` function call through the full GA item lifecycle, sends a raw client `response.create`, asserts the resulting `function_call_output` reaches the upstream fake for the exact `call_id` and `extension.middle_tier_tool_response` reaches the browser, with no backend traceback.
- Full suite: 8 → 14 tests across the 9 Stage-A commits. Green 3× in a row from the final clean state (14/14 each run, ~1s).
- Final clean-state validation: `dotnet test` 3×green (14/14), `pytest app/backend/tests -q` 586 passed/61 subtests, `ruff check .` clean, `npm test` in `app/frontend` 116 passed, `git status` clean, no `bin/`/`obj/` tracked (`tests/conformance/.gitignore` confirmed via `git check-ignore -v`).
- Stopped after Stage A per instruction — Stage B (items 8–17) waits for the coordinator to push and confirm CI green.
- Decision logged: `.squad/decisions/inbox/birdperson-stage-a-review-response.md`.

## 2026-09-24/25 — feat/conformance-s1-4 (#10, #26)

- Closed out issues #10 and #26 in worktree `SonicAIDriveThru-wt-S1-4`: 25 new black-box `[Fact]` scenarios plus a real-browser (Playwright for .NET) tier, against the `ShortTimers` profile (and a new `ResumeTimers` profile carved out for the resume-handshake files — see below).
  - `IdleTimeoutTests.cs` (3): attached-4000 idle close, resume-after-idle rejected with fresh metadata, drop-near-idle-boundary — idle clock keeps running while detached.
  - `ResumeHandshakeTests.cs` (7): valid/wrong/expired/reused/malformed resume id handling, `extension.resume` honoured only as the literal first frame, 4002 steal from a still-attached socket, resume id never appearing in any URL.
  - `ResumeRehydrationAndNudgeTests.cs` (4): order restored via bootstrap `session.update` + system rehydration `conversation.item.create` with no greeting `response.create`, the nudge firing exactly once only after `session.updated` and cancelled by guest speech, `extension.end_session` → 1000 + order gone.
  - `RateLimitRecoveryTests.cs` (5) + `RateLimitGuestSpeechCancellationTests.cs` (1): silent-then-notify ladder (`attempt:1` then `attempt:2, final:true`), cancellation by guest speech and by detach, idle-clock exemption for retries, no tool re-run on a retried follow-up, service retry-hint parsing/clamping to the hardcoded production bounds.
  - `OrderResumeBrowserTests.cs` (5, `Category=Browser`): ported all 42 checks from `scripts/e2e_order_resume.py` — 1011 drop → auto-reconnect → same ticket/session id/no greeting/mic auto-restart; nudge once; idle 4000 → no reconnect, fresh tap; reload → tap to continue; strict autoplay policy; tap-while-reconnecting.
  - Harness: `BrowserChannelPolicy.cs` (msedge-channel detection, skips with a clear reason — never silently — when no supported channel is installed, dotnet-placeholder-style), `BrowserConformanceFixture.cs`, `BrowserTimers`/`RateLimitTimers`/`ResumeTimers` backend profiles, fake-mic Chromium flags. CI: additive `Category=Browser` job in `conformance.yml` using the runner's Edge/Chrome channel (`ci:` commit).
- Mutation-check summary (all `app/backend/*.py` scratch edits, reverted, `git diff --stat app/backend` confirmed clean after every cycle):

  | scenario file | mutants | killed | notes |
  |---|---|---|---|
  | IdleTimeoutTests | 3 | 3 | idle_timeout removed/doubled |
  | ResumeHandshakeTests | 7 | 7 | resume validation short-circuited, first-frame-only window removed, 4002 supersede disabled |
  | ResumeRehydrationAndNudgeTests | 4 | 4 | rehydration `conversation.item.create` dropped, nudge-after removed, end-session order-scrub skipped |
  | RateLimitRecoveryTests | 5 | 5 | ladder attempt-count/final flag flipped, `FIRST_RETRY_BOUNDS` floor zeroed (both original coverage and this session's fix, see below) |
  | RateLimitGuestSpeechCancellationTests | 1 | 1 | guest-speech cancellation removed |
  | OrderResumeBrowserTests | 5 | 5 | verified against the real frontend/backend, no fake-only shortcuts |
  - 3 documented defense-in-depth cases (assertion still holds even when a *different* mutation than the one targeted is applied): A5 (resume id single-use), B6 (nudge-cancel), B7 (retry-cancel-by-detach) — noted, not treated as failures.
- **Two genuine, load-dependent determinism bugs found and fixed** during a post-commit full-suite re-verification pass (surfaced only under heavy consecutive full-suite runs, never in isolation) — both are property of *my own authored test code*, not the backend, and both are now fixed, mutation-re-verified, and folded into their original commits:
  1. **Idle-margin race** (`ResumeHandshakeTests.cs`/`ResumeRehydrationAndNudgeTests.cs`): these files' multi-round-trip resume scenarios could genuinely approach/exceed `ShortTimers`' aggressive 1s idle budget under heavy concurrent suite load (no intermediate frame resets the idle clock). Fix: new `ResumeTimers` backend profile (8s idle/grace, otherwise identical to `ShortTimers`) scoped to just these two files. Folded into `5d8f332` (harness), `03fa4a0`/`d9ba9f5` (the two test files).
  2. **`Task.WhenAny`/`ContinueWith` false-positive race** (`ResumeHandshakeTests.cs`, `A_late_resume_attempt_is_rejected_and_the_session_continues`): a "must not close" check raced `WaitForCloseAsync(500ms).ContinueWith(_ => true)` against `Task.Delay(500ms).ContinueWith(_ => false)` — `ContinueWith` with no continuation-option guard runs on fault too, so `WaitForCloseAsync`'s *expected* `TimeoutException` still produced `true`, turning the assertion into a coin-flip between two independent clocks under scheduler contention. Fix: replaced with `try { await WaitForCloseAsync(500ms) } catch (TimeoutException) { closedQuickly = false; }`. Mutation-checked (added an early `ws.close(1011)` right after `rtmt.py`'s `reject_late_resume`, confirmed the fixed assertion fails fast at 77ms; reverted, confirmed pass). Folded into `03fa4a0`.
  3. **Rate-limit-hint client-side timer race** (`RateLimitRecoveryTests.cs`, `A_service_retry_hint_is_parsed_and_clamped_to_the_production_bounds`): a redundant "must not fire within 0.3s" `FrameLog.WaitForAsync` check raced its own internal `Task.Delay(0.3s)` (a shared-thread-pool timer) against the real ~0.5s-floor retry's arrival signal on the *same* wait — under full-suite CPU contention the timeout's own callback could be scheduled late enough that the genuinely-on-time retry had already landed and released the wait first, producing a spurious "too early" match. Reproduced ~2 times in ~7 full-suite runs pre-fix; rock solid 12/12 in isolation (confirming it's a load-dependent scheduler race, not a logic bug). Fix: removed the redundant racy check; kept the (already-present) direct assertion on the retry frame's own recorded `ReceivedAt` timestamp (`elapsed >= 0.35s`), which proves the identical property (hint recognised, clamped to the floor, not the raw hint or the unhinted default) without racing an independent client-side clock. Mutation-checked (`FIRST_RETRY_BOUNDS` floor zeroed to 0.0, confirmed fail at 88ms/15ms observed; reverted, confirmed pass). Folded into `bda36d9`'s successor via `git commit --fixup` + `--autosquash`, then a single unconditional reword rebase to document the fix (verified immediately via `git show --stat`).
  - Re-verification after both fixes: 15+ consecutive full-suite `dotnet test` runs (this pass) with zero recurrence of either bug; pytest 604 passed/61 subtests (baseline match); ruff clean; `git status --porcelain` clean throughout.
- **Git mechanics lesson reinforced**: `git commit --fixup=<hash>` + `git -c sequence.editor=true rebase -i --autosquash <base>` is the reliable way to fold working-tree fixes into non-HEAD historical commits non-interactively. For rewording a folded commit's message afterward, do **one single-commit, unconditional reword rebase per commit** (a `GIT_EDITOR` script that just copies a prepared message file, no content-matching branches) and verify immediately via `git show --stat` — a multi-commit content-matching reword script mixed up two commits' messages earlier this session; the single-commit approach used for this session's final fix had no such issue.
- **Investigated but did not chase further (out of scope, already-documented, not authored-scenario bugs):**
  - Windows-only `asyncio.ProactorEventLoop` `ConnectionResetError [WinError 10054]` — reproduced several times *only* during this session's unusually heavy volume of back-to-back full-suite runs (and once right after force-killing ~37 leftover `msedge.exe` processes via individual `Stop-Process -Id`), manifesting as the fixture's own `Assert.Equal(baselineUnhandledErrors, Backend.UnhandledErrorCount())` guard correctly catching a genuine backend-side exception. Confirmed transient: a 20s cooldown + fresh runs returned to 3/3 clean. This is local-Windows-dev-machine loopback socket/port churn under sustained parallel load, already flagged in a prior investigation as non-reproducible on CI (ubuntu-latest) — the guard itself is working as designed (real bugs would legitimately trip it), not something to weaken.
  - `Strict_autoplay_policy_still_auto_restarts_the_mic_after_a_drop_but_not_after_a_reload`: failed twice this session (both instances correlated with the WinError 10054 artifact above, confirmed by log inspection), passed reliably otherwise (5/5 in a dedicated isolation check). Not a logic bug in the scenario.
  - `GreetingTimeoutFallbackTests` (pre-existing, not authored this task): not re-observed this session despite the added stress runs.
  - `Tapping_the_mic_while_reconnecting_still_sends_resume_as_the_literal_first_frame`: one occurrence, same WinError 10054/`UnhandledErrorCount` signature as above, not a logic bug.
- Final commit log (9 commits, all local/unpushed on `feat/conformance-s1-4`): `5d8f332` harness profiles, `d733c0d` harness Playwright+channel policy, `74e25bd` idle-close, `03fa4a0` resume-handshake, `d9ba9f5` resume-rehydration/nudge, `29e6a50` rate-limit recovery, `e34bfb0` real-browser order-resume, `5a5d5ce` `ci:` browser job.
- Final validation: `dotnet build` clean (0/0), `dotnet test` 15+ consecutive full-suite green post-fixes (116 tests, 1 skip for no-browser-channel), `pytest app/backend/tests -q` 604 passed/61 subtests, `ruff check app/backend` clean, `git status --porcelain` clean.
## 2026-09-24 — feat/conformance-s1-3 issue #9 (with Summer)

- Black-box ordering scenarios in `tests/conformance/tests/Conformance.Tests/Scenarios/Ordering/` (new sub-folder per the S1 fan-out rules): `update_order` add/remove/modify incl. merge-by-quantity, partial vs full removal, per-item and whole-order quantity caps, zero/negative-price rejection, and all five Route 44 size aliases (`rt44`/`rt 44`/`route 44`/`44`/`44oz`) displaying as "Route 44 <item>"; combo scenarios incl. component absorption for all 9 golden `test_combo_orders.py` cases plus all 10 real combo menu items from `menuItems.json`; tax/totals to the cent at the four happy-hour boundary instants (13:59:59/14:00:00/15:59:59/16:00:00 store-local) via dedicated `FixedClock` fixtures, plus a non-drink item proven unaffected; `search` against `FakeSearch` incl. the field-name-fallback 400 path; `get_order`/`reset_order`; and the "tool errors, session survives" scenario (both the graceful application-level case and the genuinely-unhandled-exception case).
- Golden data ported from `test_order_state*.py`, `test_tool_calling.py`, `test_combo_orders.py`, `menuItems.json`, and `config.yaml` into one shared `tests/conformance/testdata/golden-order-pricing.json` + `GoldenOrderPricingData.cs` loader, so S4's future C# backend can assert the same cent-accurate cases.
- Two additive harness changes (own `harness:` commits): `FakeSearchServer.RejectSelectFieldOnce` (one-shot select-field-mismatch 400 simulation, harness follow-up #23) and `RepoPaths.GoldenOrderPricingJsonPath`.
- Found and fixed a harness bug while triage-ing the first run (18/55 failing): `OrderScenarioHelpers.CallToolAsync`'s browser-frame wait only filtered by `tool_name`, and `FrameLog.WaitForAsync` always returns the *first* matching frame in its history (no per-caller cursor) rather than the newest — so a second call to the same tool silently re-matched the first call's stale frame. Fixed with a browser-frame-count watermark captured before each call. This alone fixed 14 of the 18 failures.
- Found two genuine Python bugs, both empirically confirmed by running the scenario unskipped against the live backend (not just by reading source), both `[Fact(Skip = "Known Python bug: ...")]` with full tracebacks — `app/backend` is untouched:
  1. `rtmt.py`'s `response.output_item.done` handler has no try/except around `await tool.target(...)`; an unhandled exception in a tool (e.g. a scripted call omitting a required arg, raising `KeyError`) propagates to `_forward_messages`'s connection-wide catch-all, which tears the whole WebSocket down instead of returning a model-visible error. Confirmed: `KeyError: 'item_name'` at `tools.py:325` → `rtmt.py:817` → `rtmt.py:1344` → `rtmt.py:1357` → `Session ... detached (client close code=None)`, no `function_call_output` ever sent.
  2. `tools.py::search`'s try/except (206-248) only wraps the initial *lazy* `search_client.search(...)` call; the actual HTTP fetch (and any `HttpResponseError`) happens later in `async for record in search_results:` (line 251), outside the try/except — making the "Could not find a property named" fallback-retry branch (218-229) dead code. Confirmed: the harness's injected 400 was received and parsed correctly, but the retry was never sent and the connection died via the same rtmt.py mechanism as bug 1.
- Fixed 4 test-design flakes of my own making (not Python bugs): several new tests used a drink item under the ambient `ConformanceCollection` (real wall-clock), which happened to fall inside the real 14:00-16:00 happy-hour window during this run. Swapped one drink item for a non-drink one (matching the existing `UpdateOrderToolCallTests` precedent) and moved two test classes/a combo scenario onto explicit off/on-happy-hour `FixedClock` collections, filtering by the golden data's `happyHour` flag.
- Mutation-checked 13 representative behaviors across every scenario category directly in `app/backend/*.py` (scratch edits, never committed, `git checkout --` restore verified after each): quantity merge, partial/full removal, Route 44 size mapping, whole-order quantity cap, combo absorption slot math, combo-conversion mods carried, happy-hour boundary comparison, tax-rate application, `get_order`/`search` TO_BOTH vs TO_SERVER routing, `reset_order` clearing, and the graceful price-rejection guard — all 13 broke their scenario when mutated and passed again once reverted; `git status -- app/backend` confirmed clean after every cycle.
- Validation: `dotnet test tests\conformance` green 3× in a row (146 total, 142 passed, 4 skipped — the 2 new Python-bug skips plus 2 pre-existing), `pytest app/backend/tests -q` 604 passed/61 subtests (baseline unchanged, `app/backend` untouched), `ruff check .` clean, `git status` clean, no `bin/`/`obj/`/`TestResults/` tracked.
- 10 commits: 2 `harness:`-prefixed (FakeSearchServer, RepoPaths) + 8 scenario/data commits in logical groups (golden data+loader, shared helpers+fixtures, update_order, combos, happy-hour+tax, search, get_order/reset_order, tool-error-survives).
## 2026-09-24 — feat/conformance-s1-2 Stage S1.2 (#8)

- Ported 11 new scenario files (12 with the reasoning-deployment fixture helper) under `Scenarios/{Http,Sessions,Transport,BargeIn}/` in worktree `SonicAIDriveThru-wt-S1-2`, extending (not duplicating) the harness PR's smoke bootstrap/health/auth-token/deflate/voice-lock-self-test coverage: `/health` version+checks, static `index.html` cache-control, auth-token HMAC format+distinctness, origin exact-host-accept/foreign-reject (lookalike-suffix case written correct-and-Skipped, referencing #25 — owned by the security stream), GA-shape bootstrap `session.update` (tools/tool_choice=auto/instructions/voice/transcription), voice lock (strip on session.update + picker defer), session.update-rejection-to-exactly-one-minimal-fallback with unrelated-errors-ignored, `reasoning` sent for gpt-realtime-2.1/-2.1-dz never for -1.5 (varied via a new additive `Deployment` fixture override), the three application close codes (4000/4002/1000, idle-timing itself deferred to #10), heartbeat PONG-then-data survival, and barge-in `response.cancel` relay + `response.done status:"cancelled"` handling.
- One additive harness change (`ConformanceFixture.cs`, own `harness:`-prefixed commit): a `RunAsync` overload taking an `int expectedNewBackendErrorCount` for scenarios whose subject is a deterministic backend-logged error path (fallback recovery, unrelated-error-ignored), plus a `protected virtual string? Deployment` extension point mirroring the existing `Profile` pattern. Zero-arg `RunAsync` behaviour is unchanged (forwards a zero count).
- Found and fixed two bugs in my own new `ResponseCancelRelayTests.cs` while porting (not Python bugs): `FrameLog.WaitForAsync` matches already-recorded history, not just future frames, so `firstDelta`'s predicate needed a sequence lower-bound to avoid matching the greeting turn's own earlier audio delta; and discovered `rtmt.py`'s `_process_message_to_client` sends `extension.round_trip_token` before returning `response.done` to its caller, so on the wire the token genuinely precedes `response.done` for the same turn — both bound independently off `firstDelta.Sequence` rather than chained.
- Mutation-checked every new scenario fact (16 facts total) against Python via isolated, scratch, git-checkout-reverted edits to `app.py`/`rtmt.py`/`session_manager.py`, one mutation per filtered `dotnet test` run — all 16 confirmed fail-then-restore-green cleanly. (An initial batched run of 8 mutations together produced roughly 16 unexplained collateral failures in unrelated tests; abandoned that approach in favour of one-mutation-per-isolated-run for clean attribution — `ConformanceFixture.RunAsync` was confirmed to baseline its unhandled-error count per-invocation, not as a global monotonic counter, so the batched run's collateral failures were most likely genuine cross-mutation behavioural interaction rather than counter pollution.)
- Validation: `dotnet test tests/conformance` green 3x in a row both before and after mutation testing (109 passed / 2 skipped / 0 failed, Total 111 each run); `pytest app/backend/tests -q` 604 passed/61 subtests (baseline unchanged); `ruff check .` clean; `git status` clean, no bin/obj/TestResults committed.
- Committed in 5 logical steps (harness, Http, Sessions, Transport, BargeIn scenarios, plus this history log), each referencing #8 with mutation break/fail/restore evidence tables in the commit messages.

## 2026-09-25 — squad/55-62-conformance-flakes Fix conformance flakes #55 and #62 (PR #66)

- Same class as #52/#54: a wall-clock-adjacent check reads captured backend stderr *synchronously*, racing the async `ErrorDataReceived` callback that fills it under load.
- **#55 (`GreetingTimeoutFallbackTests`)**: root cause was `Backend.DumpDiagnostics()` — an instantaneous snapshot — called right after a frame arrived on a different channel, with no synchronization to the async capture. Fix: `CapturedProcessOutput.WaitForDiagnosticsAsync(predicate, timeout, ct)`, an event-driven wait using the same `TaskCompletionSource`-swap-on-append idiom `FrameLog` already uses for frames, exposed as a default-interface method on `IBackendUnderTest` (default falls back to the old instantaneous check) and implemented in `PythonBackendLauncher.ProcessBackend`.
- **#62 (`UpdateOrderToolCallTests.Scripted_update_order_call_executes_and_notifies_the_browser`)**: root cause was `ConformanceFixture.RunAsync` capturing `baselineUnhandledErrors` instantaneously at scenario start, while a *previous* scenario's own legitimate stderr line could still be in flight through the async capture callback — landing after the new baseline snapshot and getting misattributed as a new error under the strict zero-tolerance `RunAsync(body)` overload. Fix: `CapturedProcessOutput.WaitForQuiescenceAsync(idleWindow, maxWait, ct)`, an idle-window/debounce wait (250ms idle window, capped at the existing 30s `ScenarioTeardownTimeout`), called before capturing the baseline.
- No Python backend behaviour changed — purely a test-harness fix, per the task mandate.
- Mutation-checked both: #55's predicate mutated to an unmatchable string → test correctly failed after timeout. #62's mutation target: `ToolMalformedArgumentsTests` (deliberately triggers rtmt.py's layer-1 `except Exception` via malformed tool-call JSON, one genuine new backend error) — `allowedNewBackendErrors` tightened from 1→0 → test correctly failed with "Expected at most 0 new backend error(s) above the baseline of 0, but observed 1." Both reverted before final commit. (First mutation-check attempt used `ToolErrorSessionSurvivesTests` and unexpectedly *passed* — investigated and found that scenario doesn't reliably produce a countable new-error delta in isolation the way `ToolMalformedArgumentsTests` does; switched targets rather than concluding the fix was unsound.)
- Validation with both fixes applied, no mutations: 12/12 full-suite runs green (430/430 each, no load); 20/20 full-suite runs green (430/430 each) under 20 concurrent CPU-busy jobs — the calibrated load level that reproduces this flake class without tripping unrelated 30s-teardown-timeout breakage across the suite (40 jobs was confirmed too heavy: causes wholesale unrelated failures from ThreadPool/CPU starvation, not representative of the specific tight races here). Both target tests explicitly confirmed passing in all 20 loaded runs via per-run log grep. `pytest`: 875 passed, 125 subtests passed, 1 pre-existing unrelated failure (`test_combo_logic`, a pytest-asyncio plugin-registration issue that reproduces identically on `dev`'s own main checkout — not touched by this change). `ruff check .`: clean.
- Genuine wall-clock reproduction of #55/#62 specifically proved elusive this session even with CPU load (they're rare even under the calibrated 20-job load); confidence instead comes from architectural root-cause analysis (matching the established #52/#54 pattern) plus mutation-check evidence that the fix mechanism is real and non-vacuous, plus a clean regression bar (32 consecutive green full-suite runs total across loaded/unloaded).
- PowerShell gotcha re-confirmed: a typed `[int]$Param` and a differently-cased local `$param` collide (case-insensitive variable names) — caused a `repro-loop.ps1` bug this session; fixed by renaming the local.
- Commit `35a19ad` on branch `squad/55-62-conformance-flakes`, pushed to `swigerb/SonicAIDriveThru`, PR #66 into `dev`, requesting Rick's review. Not merged.
- **PR #66 merged as 7ca056d (2026-09-25):** Participated in round 1 review of conformance flake fixes covering late-error attribution, mutation-tested unit tests, and honest PR text. Rick requested R1 changes; Birdperson locked out per reviewer-lockout rule. Lesson: event-driven waits preferred over wall-clock margins; attribute errors to the scenario that caused them; never rely on timing windows in tests under parallel load.

## 2026-09-27 — squad/80-theming PR #91 round 2 (issue #80), worktree `p2-80-r2`

- Reviewer-lockout revision of Morty's PR #91 after Rick's rejection (3 required changes): renamed
  ~70 Sonic-color-named CSS/Tailwind tokens (`--brand-red/blue/yellow`, `--brand-light/dark`) to
  role names (`--brand-primary/secondary/accent`, `--brand-background/foreground`) across
  `index.css`, `lib/personaTheme.ts`, and the four component/style files the PR touched; converted
  every `--brand-*-veil-*` from hard-coded `rgba()` to `color-mix(in srgb, var(--brand-*-hex) N%,
  transparent)` so a future `applyTheme()` call re-tints veils along with the rest of the palette;
  rewrote the `brandColorTokens.test.ts` guard to ban every color literal (any 3/4/6/8-digit hex,
  any `rgb()`/`rgba()`/`hsl()`/`hsla()` with a literal numeric argument including Tailwind arbitrary
  values) instead of just Sonic's 15 hardcoded hexes, with a 2-entry black/white allowlist and a
  battery of self-tests plus a live mutation check.
- **Found and fixed a real regex bug in my own first draft, before it ever left the worktree**: `\b`
  does not fire between two word characters, and Tailwind's arbitrary-value syntax glues the
  literal directly onto the preceding token with an underscore (`shadow-[0_4px_8px_rgba(0,0,0,.2)]`
  — `_` and `r` are both `\w`), so `/\b(?:rgba?|hsla?)\(/` silently missed exactly the shape the
  guard exists to catch. Caught it by reasoning through the regex against the two real allowlisted
  literals already in the codebase, not by a failing test (the self-test fixtures I'd written
  happened to use `[rgb(...)]`/space-preceded forms that don't trigger the bug) — added a dedicated
  underscore-glued self-test afterward so the regression can't come back silently. Fixed with a
  negative lookbehind for a preceding letter instead of `\b`. Lesson: when a guard's job is to catch
  an adversarial pattern, write the regex test cases from the exact real-world shapes already in
  the codebase first, not just clean textbook fixtures — the codebase's own edge cases are the ones
  most likely to expose a boundary-assertion bug.
- Also caught mid-task: a `locales.test.ts` guard (`TEMPLATE_LEFTOVERS`, pre-existing, unrelated to
  my edit) bans competitor-brand mentions (`\bdunkin\b`) in any `.ts`/`.tsx` under `src` — tripped
  by my own explanatory comment in `personaTheme.ts` naming Dunkin/McDonald's as illustrative
  examples of "another persona's colors." Reworded to describe the *shape* of the problem generically
  instead of naming real competitor brands, even in code comments outside the two exempt token files.
- Live mutation check on a real PR-touched file (not just fixtures): temporarily reintroduced
  `#DA291C` into `App.tsx`, confirmed `brandColorTokens.test.ts` failed with exactly that hex in the
  reported hits array, reverted, re-confirmed 48/48 green and no `MUTATION_TEST` marker left behind.
- Proved the `color-mix()` veil derivation is genuinely live (not just mathematically equivalent on
  paper) via a throwaway browser-console harness against the running dev server: swapped
  `--brand-primary-hex` to a synthetic orange, read a probe element's computed
  `var(--brand-primary-veil-10)` background before/during/after, confirmed it tracked the swap and
  reverted to Sonic's exact original `color(srgb ...)` value on removal. Nothing from this harness
  was committed; no other persona's real palette exists anywhere in the diff.
- Playwright screenshot diff: full-page light/dark screenshots vs the pre-PR baseline differ by
  ≤1 RGB unit on ~1.45% of pixels (AA/rounding noise, same order of magnitude as Morty's own
  round-1 measurement) — strong evidence the rename + color-mix conversion render pixel-identical
  for Sonic. The fixed-viewport screenshot showed a much larger raw diff %, but direct pixel
  sampling at matching content landmarks (mic button `#E40046`, page background `#F0F7F9`) came back
  byte-identical before/after; the diff was a scrollbar-presence layout shift in that one capture,
  not a color regression. Lesson: a large aggregate pixel-diff percentage on a fixed-viewport capture
  can be dominated by an unrelated layout/scrollbar artifact — sample actual colors at known content
  landmarks before concluding a visual regression exists.
- `npm test`: 181 green (171 baseline + 10 new guard self-tests). `npm run build` clean.
  `Category=Browser` conformance: 5/5 green locally against .NET 11 RC1 (`DOTNET_ROOT`/`PATH`
  scoped to the invoking process only, not the global environment).
- Posted the final `PersonaTheme` key list on issue #70 for Summer's `persona.schema.json` work.
  Left `applyTheme()` ignoring `theme.dark` and the static dark-mode CSS block for F1, per the
  task's explicit scope boundary.

## 2026-09-27 — squad/76-personas-v2 PR #101 (#76 P2-7 groundwork)

- The conformance harness's persona
  dimension (`ConformancePersonas`, mirroring `persona_loader.py`'s exact
  `PERSONAS`/`DEFAULT_PERSONA`/disk-discovery algorithm) and the CI `persona × backend`
  matrix are designed so adding a persona is a data change, not a code change: extend
  `.github/workflows/conformance.yml`'s `persona: [sonic]` list once #78/#79 land packs, and
  `ConformancePersonas.DiscoverFromDisk` already finds them without a harness edit.
- Inverting a "forbidden brand word" guard for a multi-persona repo needs three concerns
  kept separate, or the design gets muddled fast: (1) a persona pack may say its own brand
  but not another's (cross-brand leak inside `personas/<id>/**`), (2) a small number of docs
  are explicitly cross-brand by design (ADR-001, `docs/persona-architecture.md`) and should
  be classified-allowed, not just scan-excluded, so the rule is actually exercised/testable,
  and (3) everywhere else needs a per-location allowlist where every entry is forced to carry
  a tracking issue ref (a dedicated test fails on a missing/malformed one) so "temporarily
  allowed" can't quietly become "permanently forgotten". Today's real repo state needed ~13
  allowlist entries (mostly directory-prefix, longest-prefix-wins) to cover the ~72 files that
  still say "Sonic" pending #74/#78/#79/#80/#86 migration work -- that volume is expected and
  healthy for a repo mid-multi-persona-migration, not a sign the guard is too loose.
- A CI workflow's own comments are source text the brand guard scans too: mentioning a
  not-yet-existing persona ("McDonald's"/"Dunkin") in a `.yml` comment tripped the guard
  before it was allowlisted -- worth remembering before adding forward-looking comments to
  any scanned file.

## 2026-09-27 — squad/76-part2-multi-index PR #108 (#76 part 2, harness only)

- The fake search server previously served ONE fixed catalog regardless of which index name
  the backend's tool actually queried, which meant the four pytest-only per-persona rules
  (per Rick's note on #20) had no C#-side conformance equivalent -- multi-index routing was
  the actual unblocker, not a smoke-test writing exercise. Once `FakeSearchServer` loads and
  serves each persona's own menu data keyed by index name (mirroring the backend's real
  resolution), a same-shaped isolation row (session A never sees session B's items) becomes
  trivial to write and is the highest-signal single test for this whole stream.
- When a fixture pack's own data doesn't actually exercise the property you'd naively want to
  test (here: neither test-alpha's nor test-beta's menu items opt into `happyHourDiscounted`,
  so "assert the item gets discounted" would be vacuously testing nothing), look for the
  complementary property that the fixture DOES exercise honestly instead of reaching for an
  out-of-scope fixture-data edit. Same item + same persona at two clock instants (inside vs.
  just outside the happy-hour window) asserting EQUAL totals proves "the persona-level flag
  doesn't blanket-discount a non-opted item" and "a `happyHour: null` persona is
  clock-invariant" -- both real, useful conformance properties -- without touching shared
  Python-owned fixture JSON.
- A wildcard (`"*"`) search query only reliably round-trips through the fake's default
  `top_results=3` cap when the whole catalog has ≤3 items. A smoke Theory meant to run
  uniformly across both small fixture packs (3 items) and a persona's real, much larger
  catalog (sonic) needs to search by the expected item's OWN NAME, not a wildcard, or the
  large-catalog row will silently get the wrong (truncated) result set and fail for a reason
  that has nothing to do with the property under test.
- Deliberately failing loud (`Assert.True` with a message naming the pack) rather than
  quietly falling through is worth doing in TWO places for this kind of "runs once per
  discovered pack" design: (1) inside the expectations lookup, so a newly discovered pack
  with no registered expectations breaks the *build/first-run* obviously, and (2) in a
  SEPARATE coverage test that independently re-discovers packs from disk and cross-checks
  against the Theory's own live `[MemberData]` method via reflection (not a hand-copied
  list) -- because a mutation that shrinks the Theory's own data-source method to quietly
  drop a persona leaves the Theory itself green (fewer rows, all still passing) and only the
  independent coverage test catches it. Confirmed by mutation: shrinking
  `FixturePersonaIds()` to drop `test-beta` left the smoke Theory green but failed the
  coverage test with a clear message.
- xUnit v3 in this repo (`xunit.v3.mtp-off` 4.0.1) changed `TheoryData<T>` to implement
  `IEnumerable<TheoryDataRow<T>>` (not v2's plain `IEnumerable<object[]>`) -- each row's
  actual value is on `.Data`. When a project doesn't have a `Directory.Packages.props` at the
  repo root, check nested folders (`tests/conformance/Directory.Packages.props` here) before
  assuming central package management isn't in use; when even that's ambiguous, loading the
  already-built test assembly's own `xunit.v3.core.dll` via reflection and inspecting the
  live type is faster and more reliable than guessing from version-number docs.
- **PR #107 revision, Rick's #104 review round 2 (2026-09-28):** Took over PR #107
  (squad/104-menu-price, issue #104: price from the menu, not the tool call) after Rick
  rejected it and Beth was locked out; worked in a fresh worktree
  (SonicAIDriveThru-wt\p2-104-r2), merged origin/dev first (no rebase/force-push, #109
  already in). Addressed all 7 required changes: (1) crash-safety guard for
  null/non-numeric/omitted tool price (never compared, never crashes) plus moving
  ToolFailureCapAndTicketRefreshTests's crash trigger off the now-harmless tool price onto
  a string quantity -- confirmed empirically the exception actually fires at the per-item
  quantity-limit check, not the whole-order sum Rick's review text guessed, since
  	ools.py::update_order has no internal try/except and only an unrelated earlier
  search-like function does; (2) wrong-size carry-over (.39) and pre-discounted happy-hour
  (.445, never .725) acceptance rows in both pytest and C# conformance; (3) reverted the
  brand-baseline raise, generic-izing the one new README path mention to
  personas/<id>/menu/menuItems.json -- and caught a second, self-inflicted baseline
  violation from my own new test's docstring mentioning the brand by name, fixed the same
  way; (4) deleted the dead price_validation_failed error key after grepping pp/ to
  confirm nothing reads it; (5) unified the schema description in both
  	ool_schemas.yaml and 	ools.py's hardcoded copy; (6) shrunk
  docs/persona-architecture.md section 6's #104 bullet from ~29 lines to one paragraph,
  removed em dashes this PR had introduced (careful to leave every pre-existing em dash in
  the same files alone -- git diff origin/dev -- <file> | grep '^+.*—' is the precise way
  to isolate exactly the lines a branch actually added, not a whole-file style sweep), and
  added a new data test (	est_menu_data_completeness.py) asserting every item size in
  every currently-loaded persona pack (real personas/ plus the 	est-alpha/	est-beta
  fixtures) has a matching menu price; (7) rewrote the stale PR body and posted a correction
  to Unity, since PR comments can't be edited by another author and git history
  (git log --oneline -- <file>) is the reliable way to verify an authorship claim rather
  than trusting the comment text.
  - **Real bug found via the docs work, not the code review:** while documenting the
    "direct-caller fallback" (menu.price_for returns None -> falls back to the
    caller-supplied price) for item 6, noticed its logger.warning used %.2f on a price
    that isn't guaranteed numeric in that branch specifically (the null/non-numeric guard
    added for item 1 only runs inside the sibling if menu_price is not None: branch) --
    would have crashed on a non-numeric price reaching that path. Fixed with %r. Writing
    the doc for a fallback path is a good forcing function for actually reading it closely.
  - **Mutation testing is worth doing twice, on purpose, per change:** re-trusting the tool
    price (price = menu_price commented out) failed 8 pytest cases and 7+ C# conformance
    tests -- including a pydantic_core.ValidationError crash on the null/omitted-price
    cases, since the schema no longer requires the model to send a numeric price at all, so
    removing the server-side guard doesn't just mis-price, it can crash the tool call
    outright. Removing the null/non-numeric isinstance guard crashed both null- and
    non-numeric-price pytest cases with decimal.InvalidOperation from 	o_decimal(). Both
    reverted cleanly and the suite verified green again after each -- confirming the tests
    actually exercise the code they claim to, not just pass vacuously.
  - Final validation: pytest 1064 passed / 165 subtests, ruff clean; full .NET conformance
    (RC1 SDK, dotnet test) 640 passed / 0 failed; all 8 GitHub Actions checks green on the
    pushed branch. Head 488bc5b. Did not merge -- left for the team.

- **PR #106 round 3 (issue #75, Rick's review, 2026-09-28):** Mapped Rick's 4 required items to commits: (1) reasoning precedence -- `_reasoning_model()` in `rtmt.py` had the explicit `AZURE_OPENAI_REALTIME_REASONING_MODEL` switch checked AFTER the catalog override in round 2; swapped back to latch -> switch -> catalog -> name-heuristic (`05cb9b2`). Restored the retired `Gpt15` conformance test (renamed `ReasoningSwitchFalseKeepsReasoningOffA15DeploymentTests`) and inverted `ReasoningSwitchOffIsOverriddenByTheBoundModelsCatalogEntryTests` -> `...OverridesTheBoundModelsCatalogEntryTests` (now asserts reasoning NOT sent when switch=false, even though the bound model's catalog entry says `reasoning: true`). (2) Set `AZURE_OPENAI_REALTIME_REASONING_MODEL=false` on `Gpt15ConformanceFixture` (modeling how an operator actually disables reasoning on a 1.5 deployment) and reverted `SecondSessionUpdateRejectionLoopGuardTests`'s `allowedNewBackendErrors` 4->3, since bootstrap no longer carries `reasoning` so the scripted rejection can't also trip the latch as a side effect -- both in `05cb9b2`. (3) Renamed the `/api/personas` per-pipeline `allowed` key to `models` (`c9751e2`) across `app.py`, `test_model_selection.py`, and `ModelSelectionConformanceTests.cs` -- the persona pack's own raw `models.<pipeline>.allowed` config field is a DIFFERENT thing (unfiltered persona-author allow-list) and correctly keeps its old name; only the API's already-filtered catalog-intersect-deployment-intersect-persona-allowed response list got renamed, since design doc section 5.2 already called it "the selectable `models`" in prose. (4) PR #108 (test-gamma vs `FixturePackPersonaSmokeTests`) was still OPEN when I finished -- left a note there per Rick's merge-order guidance rather than touching a test class that doesn't exist yet on this branch.
- **Brand-baseline regen is genuinely lower-only and will bite you on doc prose, not just code:** writing "sonic's own realtime default" in a new README paragraph raised the `(tests/conformance/README.md, sonic)` baseline entry from the checked-in max of 14 matching lines to 15 -- `regenerate_rebrand_baseline.py` (no flags) correctly refused to write, printing the exact RAISE entry. Reworded to "the default persona's own realtime default model" (says the same thing without the brand word) and reran the regen: 0 diff, confirming it's back at the checked-in max. Do this check BEFORE committing doc changes that mention a brand name, not after -- it's a one-line PowerShell count (Select-String -Pattern with a word-boundary brand regex) against the file you just edited vs. the prior commit's version of the same file.
- **Mutation testing evidence for a precedence swap is a 2-line diff + one filtered test run, not a rebuild of the whole matrix:** to prove round 3's fix actually matters, temporarily swapped the switch/catalog check order back in `_reasoning_model()` (reintroducing round 2's bug), ran the switch-off conformance test filtered by name -- failed exactly as expected (catalog `reasoning: true` won over the explicit `false`) -- then discarded the change to revert cleanly and reran the full suite (652/652) to confirm no residual damage.
- Final validation for this round: pytest 1151 passed / 168 subtests, ruff clean; full .NET conformance (RC1 SDK, dotnet test -c Release) 652 passed / 0 failed; brand-baseline regen confirmed zero drift (lower-only, no --allow-increase used). Commits on top of the origin/dev merge: reasoning precedence fix, personas key rename, README precedence-table update, and the brand-word wording fix. Did not merge PR #106 -- posted a mapped comment addressed to Rick and left the branch open for the team.

- **Three conformance flakes, one branch (#95/#103/#68, squad/flakes-95-103-68, 2026-09-28):**
  - **#95** -- `response.cancel` sent after a response already finished gets
    `response_cancel_not_active` back from the fake upstream; `rtmt.py` was logging that at
    ERROR, which strict scenarios (`OrderResumeBrowserTests`) count. Fix: a small benign-path
    branch in `rtmt.py` that logs at INFO and still relays to the browser -- upstream error
    codes that mean "already resolved, nothing to do" are not backend bugs. Python unit test +
    a new `ResponseCancelRelayTests` conformance row, both red without the fix.
  - **#103** -- `CapturedProcessOutputWaitTests`'s rearm test compared a test-side
    `Stopwatch.Elapsed` read against production's own internal `DateTimeOffset.UtcNow`-based
    state via a 30ms margin that load blows through. **The real lesson: when a flaky test
    compares two independently-read clocks with a margin, look for whether the production
    class already has (or can cheaply expose) its own internal timestamp, and compare directly
    against THAT instead of re-deriving a second reading** -- eliminates the margin entirely
    rather than widening it. Exposed `internal DateTimeOffset LastAppendUtc` on
    `CapturedProcessOutput` for exactly this. C#'s `CS0162` (unreachable code) is a build
    ERROR, not a warning, in this project -- mutation-testing techniques that insert
    unconditional early returns won't build; gate the mutation behind an environment-variable
    check instead (`Environment.GetEnvironmentVariable("MUTATE_X") == "1"`) so the file always
    has a reachable path regardless of whether the mutation is "active." Two mutations proved
    the fix: short-circuiting the re-arm loop, and halving the effective idle window on re-arm.
  - **#68** -- `RateLimitGuestSpeechCancellationTests`: cancelling a pending rate-limit retry in
    response to guest speech was wired ONLY to the upstream model's own `speech_started`
    acknowledgment round-tripping back through `from_server_to_client` -- a real network round
    trip racing the retry's own fixed local timer (as low as 0.4s in some conformance
    profiles). Traced and ruled out a same-process reordering theory first (confirmed
    `from_server_to_client`'s `async for msg in target_ws` loop is strictly sequential -- a
    later message literally can't be processed until an earlier handler fully returns) before
    accepting the round-trip-vs-timer race as the real shape. Fix: a new, minimal
    `RateLimitRecovery.on_guest_audio_forwarded()` fired the instant non-echo-suppressed mic
    audio is forwarded upstream in `from_client_to_server` -- the earliest purely local,
    deterministic signal available, no round trip, no margin. Deliberately does NOT call
    `_reset()` (unlike `on_guest_speech()`), since it fires on every forwarded frame and must
    not disturb ladder state that only the VAD-confirmed path should own.
  - **Reproduction diligence vs. a genuinely rare race:** issue #68 itself was filed from an
    unreproduced CI observation ("not reproduced locally as part of this investigation") --
    same as #55/#62 before it (see PR #66's own text). Spent real effort trying anyway: the
    isolated target test 20x with no load, 20x under a busy-loop-per-core (24 cores), then 50x
    at 2x core oversubscription (48 busy processes) -- 0 failures in all ~90 runs. Also looped
    the FULL suite 4x under 24-core load specifically because #55/#62's own filed text says
    they only manifested in full-suite runs, never isolated ones -- still 0 hits on the target
    assertion (though 2/4 runs picked up unrelated backend-health-timeout failures, a pure
    CPU-starvation artifact of 100% synthetic load on Python process startup, not a logic
    race). **Lesson: a race this rare is legitimately allowed to resist reproduction within a
    reasonable budget -- the acceptance bar has to become "closed structurally, verified safe
    by static analysis + a deterministic unit test + no regression," not "reproduced red then
    fixed," when the issue itself says it was never reproduced either.** Mutation-tested by
    neutralizing `on_guest_audio_forwarded`'s cancellation (`if self.pending and False`) --
    the new unit test went red exactly as expected, reverted cleanly.
  - **"Merge origin/dev first" surfaced a genuine, unrelated regression, not a merge
    conflict:** after merging (clean, no conflicts -- P2-7 part 2 / #108 and P2-11 / #110),
    the full suite showed `UpdateOrderAddRemoveModifyTests` failing intermittently (3/10 clean,
    7/10 with exactly that one failure). Rather than assume it was something I'd disturbed,
    spun up a throwaway `git worktree add $env:TEMP\... origin/dev` at the exact pre-merge dev
    tip, built its own venv + C# solution from scratch, and reproduced the SAME failure 6/6 in
    isolation there -- proving it predates and is fully independent of this branch's three
    fixes. Filed it as a new tracked issue (#121, referencing the closed #104 it looks like a
    regression of) instead of silently working around it or trying to fix out-of-scope
    menu/persona code. **Lesson: when a required merge step surfaces a new failure, don't
    assume it's yours -- a disposable worktree at the pre-merge tip is a cheap, conclusive way
    to attribute it before you touch anything.**
  - **Rebrand-baseline bump is sometimes the correct fix, not a workaround:** the already-
    committed #95 test's `assertNoLogs("sonic-drive-in", ...)` pushed
    `test_session_bootstrap.py` to a legitimate 5th "sonic" occurrence, one over the checked-in
    baseline max of 4 -- failing `test_rebrand_verification.py`. Used the sanctioned
    `regenerate_rebrand_baseline.py --allow-increase --increase-reason '#95'` (per PR #101's
    ratchet-safe design) rather than hand-editing the YAML; produced a minimal 2-line diff
    (`max: 4 -> 5`, `increase_reason: '' -> '#95'`).
  - Final validation: pytest 1150 passed / 168 subtests (post-merge), ruff clean; full .NET
    conformance suite 662/663 consistently across repeated runs, the sole failure being #121
    (confirmed pre-existing on origin/dev, unrelated); all three targets 20/20 clean under
    24-core busy-loop load post-merge. Head `773561f` (merge commit) on top of `3908cb5`
    (#68) / `3c6a799` (#103) / `126398b` (#95). Did not merge -- PR opened for the team, "Refs
    #95, #103, #68" (nothing closed).

## 2026-09-28 — squad/127-pack-happy-hour PR #133 (#127, per-pack happy-hour conformance)

- **Real-pack Theory rows need per-row backend instances, not one shared fixture:** every
  prior happy-hour test (`PersonaHappyHourConformanceTests`, `HappyHourBoundaryTests`) pins a
  SINGLE shared clock/persona pair via a static `[CollectionDefinition]`/`ICollectionFixture`,
  because the persona id and instant are compile-time constants there. #127 needed a Theory
  over `ConformancePersonas.DiscoverFromDisk()` where each real pack has its own timezone and
  window, so the pinned "inside" and "outside" instants differ per pack. Solved by making
  `RealPackHappyHourFixture` a normal (non-static) `ConformanceFixture` subclass with a primary
  constructor `(string personaId, DateTimeOffset instant)`, instantiated directly inside the
  Theory body via `await using` + explicit `InitializeAsync()`/`RunAsync(...)` calls -- legal
  because `ConformanceFixture` is `IAsyncLifetime`, which in xunit.v3 is just
  `IAsyncDisposable` plus an `InitializeAsync()` method, not a magic xunit-only lifecycle hook.
  New pattern for this codebase; documented inline for the next brand pack (#112) to copy.
- **IANA timezone IDs resolve natively on this Windows box under .NET 11** --
  `TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")` etc. just works (ICU-backed), no
  Windows-ID mapping table needed. Verified with a disposable scratch console project before
  trusting it in the real Theory, then deleted the scratch project.
- **"Just outside the window" is safest computed, never hand-written:** `outsideInstant =
  insideInstant.AddSeconds(-1)`, derived from the SAME resolved UTC instant as "inside", makes
  the boundary DST/timezone-safe by construction -- no manual wall-clock arithmetic that could
  silently drift a pack's own DST rules.
- **`file`-scoped classes don't cross files, even same namespace:** the existing
  `PersonaHappyHourTestSupport` helper is `file static class` in a different .cs file, so it's
  invisible to the new Theory's file -- had to duplicate it locally as
  `RealPackHappyHourTestSupport`. A small, deliberate duplication rather than widening the
  original's visibility for one caller.
- **Coverage-test parity is worth adding even when it's currently a no-op:** mirrored
  `PersonaSmokeCoverageTests.cs`'s exact shape (`RealPackHappyHourCoverageTests`,
  independently re-discovering packs from disk and reflecting into the Theory's own
  `[MemberData]` source) so a future accidental shrink of `DiscoveredPersonaIds()` fails loudly
  even though today it's a 1:1 passthrough to `DiscoverFromDisk()`.
- **Golden-file decision: DELETED, not wired in.** `testdata/personas/dunkin/golden-menu-
  categories.json` / `golden-order-pricing.json` were never consumed by any C# fixture; their
  `businessRules` block only duplicated `persona.json`'s own fields (a second, driftable source
  of truth for exactly what this Theory now reads directly); their DST-boundary-matrix richness
  exceeds #127's one-inside/one-outside-instant scope and remains scoped to a single other
  pack's own dedicated boundary tests, which Dunkin never had an equivalent of; and their
  tax/size-display/extras sections are a materially larger, out-of-scope conformance surface.
  Rationale recorded inline in Dunkin's own `smoke.json` description.
- **Rebrand guard (`test_rebrand_verification.py`) polices per-pack *testdata* prose too, not
  just code:** my first draft of Dunkin's `smoke.json` description explained the golden-file
  deletion by naming the OTHER real pack by brand for contrast ("remains Sonic-specific...")
  -- failed `test_brand_word_counts_match_the_checked_in_baseline` because that literal
  brand word appeared inside `personas/dunkin/**`. Fixed by rephrasing to "a single other,
  unrelated pack's own dedicated test files" with no brand literal at all. Good reminder that
  the no-brand-literal rule for #127 (shared C#) has a testdata-side twin already enforced by
  CI, and it applies even to *my own* documentation prose, not just executable code.
- **Mutation testing, exactly per the two required cases:** committed first (`08e9593`), then
  (1) hardcoded `_happy_hour_discount` to `to_decimal("0.5")` in `order_state.py` regardless of
  pack -- Sonic's row still passed (its own multiplier IS 0.5, a coincidence), Dunkin's row
  failed with the exact loud message naming its own real multiplier (0.75); (2) hardcoded
  `_happy_hour_announce` to `False` -- BOTH real packs' rows failed on the banner assertion,
  since both currently have `announce:true`. Reverted both mutations (`git checkout --`),
  re-verified a clean working tree and the full 677/677 conformance suite green again before
  pushing.
- Final validation: new Theory 2/2 rows pass (sonic, dunkin) + 1 coverage test; full
  HappyHour+Smoke filter 61/61; full `.NET` conformance suite 677/677; pytest 1174 passed / 168
  subtests; ruff clean. Head `08e959340371239a5acbe9fc3e368204cdd11cd1` on top of `06759fd`
  (origin/dev). PR #133, "Refs #127" (nothing closed -- #112/McDonald's still open, so the
  disabled-pack code path is correct but unexercised by a real pack until it lands). Did not
  merge.
