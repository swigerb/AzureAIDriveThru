# Unity — History

## Project Context

- **Project:** Sonic AI Drive-Thru Voice Assistant — a voice-driven drive-thru ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps.
- **Owner:** Brian Swiger
- **Stack:** Python (aiohttp, WebSockets), React/TypeScript, Azure OpenAI GPT-4o Realtime API, Azure AI Search, Azure Speech SDK
- **Key files:** `app/backend/rtmt.py` (realtime middle tier), `app/backend/app.py`, `app/frontend/src/hooks/useRealtime.tsx`
- **Joined:** 2026-03-21

## Learnings

### 2026-03-26: Combo Size Prompting Fix Sprint (Unity's Part)
- **Problem:** When guest accepted a combo upsell, the AI defaulted combo side/drink to Medium without asking what size the guest wanted.
- **Root Cause:** Blanket "default to MEDIUM" rule in MENU_AND_PRICING overriding contextual combo-completion behavior.
- **Solution:** Scoped "default to MEDIUM" rule to standalone items only. Added explicit COMBO SIZE PROMPTING section requiring AI to ask for side and drink sizes when guest accepts combo. Updated SUGGESTIVE_SELLING with explicit size-ask instructions.
- **Changes:** Three surgical edits to `app/backend/prompts/sonic/system_prompt.yaml` (MENU_AND_PRICING, COMBO_LOGIC, SUGGESTIVE_SELLING sections).
- **Trade-off:** Adds one extra conversational turn when guest doesn't specify sizes (acceptable — better than wrong sizes).
- **Impact:** Eliminates silent Medium defaulting on combo components. Guests now get asked what size they want, improving order accuracy and UX.

### 2026-03-21 through 2026-03-22: Demo Readiness & System Prompt Optimization (Consolidated)

**System Prompt Best Practices (gpt-realtime-1.5 Patterns):**
- Bullets > paragraphs for instruction-following. ALL CAPS for emphasis. Explicit negative instructions ("NEVER say X WITHOUT calling Y FIRST") + consequence statements ("item WILL NOT appear") required for tool-calling mandates. Dense paragraphs cause failures.
- Section positioning matters heavily — TOOL-CALLING RULES must be early (section #2, right after VOICE STYLE). gpt-realtime-1.5 prioritizes top-of-prompt instructions.
- COMBO LOGIC — DETERMINISTIC: Strict priority (Item Selection → Combo Completion → Upsell → Treat Suggestion) prevents jumping to desserts before combo sides.
- QUANTITY LIMITS: Conversational tone ("suggest capping"), never refuse service. Complements backend enforcement.
- TOOL HINTS: `[SYSTEM HINT]` patterns from backend — AI acts on them immediately, NEVER reads aloud.

**VAD & Latency Optimization:**
- VAD threshold: 0.8 for noisy/echo environments; 0.7 for clean demo settings. Always retune after echo suppression.
- Prefix padding: Minimum 300ms for reliable speech capture (avoids plosive clipping).
- No filler words at response start (Okay, So, Well) — reduces perceived latency.
- Temperature: 0.5 for fast TTFT.

**Prompt Token Budgeting:**
- 250 max_tokens was insufficient once ordering flows grew (combo hints, upsell suggestions, multi-item readbacks). Raised to 1024.
- Token limits must be re-evaluated whenever prompt complexity increases.
- Tool calls share token budget with verbal output — must reserve headroom.

**Coordination Patterns:**
- Backend message reordering (Summer) + system prompt tool-calling mandate (Unity) both required for reliable tool execution.
- Backend `[SYSTEM HINT]` injection + Unity's TOOL HINTS section = defense-in-depth backend decides *when* to hint, AI knows *how* to act.
- Backend enforcement + AI conversational guardrails = defense-in-depth.

### 2026-03-25: Prompt YAML Content Extraction

**Files Created:**
- `system_prompt.yaml` — 22 sections extracted verbatim, priority-ordered for gpt-realtime-1.5 compliance
- `greeting.yaml`, `tool_schemas.yaml`, `error_messages.yaml`, `hints.yaml`, `manifest.yaml`
- Total: ~8.5 KB of brand-portable prompt content

**Key Decisions:**
- TOOL-CALLING RULES moved to section #2 (confirmed gpt-realtime-1.5 best practice)
- System prompt trimmed ~33% via section merging, verbose example removal
- max_response_output_tokens increased to 1024 (tool call + verbal budget)
- Tool descriptions branded (not generic) for future brand portability
- Error messages use Jinja2 StrictUndefined for early validation

**Coordination:** Summer's `prompt_loader.py` reads manifest-driven YAML at startup. All 125 tests pass.

### 2026-03-26: Same-Utterance Combo Fix (Critical Demo Bug)

**Problem:** When a customer specified a combo entree, side, AND drink in one sentence (e.g., "bacon double cheeseburger combo with medium tots and a large diet Coke"), the AI ignored the side and drink, then re-asked for them — causing multiple wasted turns.

**Root Cause:** `update_order` is single-item. After the first call (combo entree), the backend's `get_combo_requirements()` returns a `[SYSTEM HINT]` saying "ask for side and drink." The AI blindly followed the hint instead of processing the remaining items the customer already specified.

**Fix (prompt-only, 3 sections):**
1. **COMBO_LOGIC** — Added "SAME-UTTERANCE COMBO RULE" block: parse ALL components from the sentence first, call update_order back-to-back for each, ignore [SYSTEM HINT] if items already mentioned, only ask about truly missing components.
2. **COMBO_PIVOT_RULES** — Added: hints reflect state after each individual call; if unprocessed items remain from utterance, add them before responding to the hint.
3. **TOOL_CALLING_RULES** — Added "MULTI-ITEM UTTERANCES" rule: process all mentioned items before responding verbally.

**Validation:** YAML valid, 337 tests pass, no code changes.

### 2026-03-27: Combo Size Prompting Fix (Critical Demo Bug)

**Problem:** When a guest accepted a combo upsell (e.g., "Yeah, I'll take Tots and a drink"), the AI defaulted the side to Medium without asking the guest what size they wanted. Drink size was also not asked.

**Root Cause:** Prompt priority conflict — the blanket "default to MEDIUM" rule in MENU_AND_PRICING (priority 4) overrode the vague "ask for missing details" in SUGGESTIVE_SELLING (priority 12). gpt-realtime-1.5 prioritizes higher-ranked sections.

**Fix (3 surgical edits to system_prompt.yaml):**
1. **MENU_AND_PRICING** — Scoped "default to MEDIUM" to STANDALONE items only. Added explicit callout that combo side/drink slots require asking the guest.
2. **COMBO_LOGIC** — Added new "COMBO SIZE PROMPTING — CRITICAL" block: MUST ask what size for combo components, ask side size first then drink, skip asking only if guest already specified sizes.
3. **SUGGESTIVE_SELLING** — Changed vague "ask for missing details" to specific: "ask what SIZE side and what SIZE drink they want" with example phrasing.

**Pattern:** When a blanket default rule conflicts with a contextual behavior rule, scope the default explicitly. Use ⚠️ CRITICAL markers and ALL CAPS for override rules — gpt-realtime-1.5 respects these formatting cues for instruction priority.

**Validation:** YAML valid, 347 tests pass (1 pre-existing async failure unrelated).

### 2026-07-09: Model Migration — gpt-4o-realtime-preview → gpt-realtime-1.5 (GA)

**Problem:** The demo was pinned to `gpt-4o-realtime-preview` (version `2024-10-01`), which has been retired from Azure and can no longer be deployed. `azd up` would fail at provisioning.

**Target:** `gpt-realtime-1.5` version `2026-02-23` (GA, `GlobalStandard` SKU, retirement 2027-08-24 — longest runway of any realtime model).

**GA API Surface Changes (verified against official docs):**
- **WebSocket URL:** `/openai/realtime?api-version=X&deployment=Y` → `/openai/v1/realtime?model=Y` (no api-version param)
- **Event names (server→client):** `response.audio.delta` → `response.output_audio.delta`, `response.audio.done` → `response.output_audio.done`, `response.audio_transcript.delta` → `response.output_audio_transcript.delta`, `response.audio_transcript.done` → `response.output_audio_transcript.done`, `response.text.delta` → `response.output_text.delta`, `response.text.done` → `response.output_text.done`, `conversation.item.created` → `conversation.item.added`
- **Session config:** Voice moved from `session.voice` to `session.audio.output.voice`; audio format to nested `audio.input/output`; turn detection to `audio.input.turn_detection`
- **Auth:** Kept `cognitiveservices.azure.com/.default` scope (matches Brian's `Microsoft.CognitiveServices/account` resource type)
- **Doc sources:** `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets`, `https://developers.openai.com/api/reference/resources/realtime`

**Key Design Decision — Frontend Compatibility:**
Since `app/frontend/` is off-limits, the middleware (`rtmt.py` + `audio_pipeline.py`) translates GA event names back to legacy names before forwarding to the client. Both GA and legacy names are in `_PASSTHROUGH_SERVER_TYPES`. A `_GA_TO_LEGACY_EVENTS` mapping dict handles the translation.

**Voice:** Unified on `coral` (warm, friendly, clear — fits carhop persona). Fixed inconsistency where `config.yaml` had `coral` but `main.parameters.json` defaulted to `alloy`.

**Files Changed (16):**
- `infra/main.bicep` — deployment name/model/version updated
- `infra/main.parameters.json` — voice default `alloy` → `coral`
- `app/backend/rtmt.py` — removed `api_version`, changed WS URL/params, added GA event translation, dual voice injection, `conversation.item.added` handling, echo suppression for both marker sets
- `app/backend/audio_pipeline.py` — expanded passthrough sets, added `_GA_TO_LEGACY_EVENTS` mapping, updated markers
- `app/backend/config.yaml` — removed `api_version` line
- `app/backend/app.py` — removed `api_version` assignment
- `app/backend/.env-sample` — updated deployment name, removed version env var
- `app/backend/prompts/sonic/system_prompt.yaml`, `manifest.yaml` — model name updated
- `docs/existing_services.md`, `docs/manual_setup.md`, `voice_rag_README.md` — model references updated
- `app/backend/tests/test_rtmt.py` — 5 new tests for GA event translation and passthrough coverage
- `app/backend/tests/test_app.py`, `test_performance.py` — deployment name in mock env vars

**Validation:** Bicep builds (pre-existing BCP420 error in `container-apps.bicep` unrelated). 365 tests passed (360 original + 5 new). Ruff clean.

**Needs Live Verification:**
- Does `cognitiveservices.azure.com/.default` auth scope work with `/openai/v1` endpoint?
- Does the GA server accept flat `session.update` format alongside nested?
- Which exact event names appear on the wire from `gpt-realtime-1.5`?
- Which voices does `gpt-realtime-1.5` actually support? (docs don't enumerate per-model)

### 2026-09-22: $0.00 Carhop Ticket — Unconfigured Upstream Session + GA Voice Lock; gpt-realtime-2.1

**Problem:** On the deployed demo the Carhop Ticket stayed at $0.00 for the whole conversation. The assistant sounded in character but never called a tool. Its first reply was generic ("Hey there! Sounds like you're just warming up..."). No application code had changed since the verified August deploy.

**Root Cause (confirmed in prod Log Analytics + a local repro against the real endpoint):**
- The browser sends `session.update` only from `startSession()`, i.e. when the mic is pressed. It does not send one when react-use-websocket auto-reconnects.
- Prod sequence:
  - 21:02:30 — the idle checker closed the session.
  - 21:03:15 — `Received frame with non-zero reserved bits` killed the new socket. It auto-reconnected with the mic live.
  - The fresh upstream session ran on service defaults: no tools, generic instructions, voice alloy, server VAD auto-responding.
  - 21:03:24 — the model answered the mic audio; that was the generic line.
  - 21:03:30 — the browser's `session.update` (4 tools, `tool_choice=auto`, voice shimmer) was **rejected** with `invalid_request_error` / `cannot_update_voice`.
- GA rejects the *whole* event, so tools and instructions were never applied. The "persona" came only from the greeting item text.
- Two premises were wrong. There *was* an error event, and the instructions were *not* reaching the model.
- `tool_choice` was never "none": `self.tools` is populated synchronously before `attach_to_app`.
- Voice lock, verified live on gpt-realtime-1.5:
  - Sending the same voice after audio is accepted.
  - Sending a different voice after audio rejects the whole event.
  - Omitting the voice is accepted.

**Fix (`rtmt.py`):**
- A server-authoritative bootstrap `session.update` is now the first upstream frame after `ws_connect`. It carries tools, instructions, voice, and the browser's VAD/transcription values (`_BOOTSTRAP_CLIENT_SESSION`).
- A per-connection `assistant_audio_seen` flag tracks when the model has spoken.
  - Once it is set, `_build_session(voice_locked=True)` strips `audio.output.voice`.
  - `extension.set_voice` is deferred once it is set. The picker's voice is process-wide, so another tab can change it mid-call.
- The greeting waits for `session.updated` (5 s timeout). This is the same fix as the sibling brand repo's `ba8c94d`.
- The greeting is triggered by the client `session.update`, not the bootstrap, so the page never greets unprompted.
- The server-override logic moved into `_build_session()`, so the bootstrap and the client update share one path.

**Validation:**
- Local repro (reconnect scenario, real AOAI):
  - Before the fix: `cannot_update_voice`, `update_order_calls=0`, total $0.00.
  - After the fix: no error, `update_order_calls=2`, total $10.13.
- `tests/test_session_bootstrap.py` has 10 tests. Mutation-checked:
  - reverting rtmt.py fails 6;
  - removing the bootstrap fails 5;
  - removing the voice strip fails 3;
  - an unconditional picker send fails 1.
- 412 backend tests pass; ruff is clean.

**gpt-realtime-2.1 (GA 2026-07-07, retires 2027-07-31):**
- `infra/main.bicep` → `gpt-realtime-2.1` / `2026-07-07` / `GlobalStandard`.
  - The deployment name changes, so ARM's incremental mode leaves the old 1.5 deployment in place for rollback.
- The GA surface is unchanged vs 1.5: same URL, session shape, event names, and 10 voices.
- The only additions are `reasoning.effort` and `parallel_tool_calls`, for reasoning models only.
  - `_to_ga_session` allows both through, but nothing sends them by default.
  - On a non-reasoning model an unsupported field would reject the update, and the tools with it.
- Learn still labels 2.1 "preview"; the resource model catalog says GenerallyAvailable.
- Voices: OpenAI recommends marin/cedar for best quality.
  - Recommended carhop default: **marin**.
  - `shimmer` is left in place pending Brian's ear test, because the voice set did not change.

**Needs Live Verification:**
- Whether 2.1 accepts the bootstrap payload, including `input_audio_transcription.model=whisper-1`. Learn notes an Azure deviation that requires a deployment name in that field.
- `reasoning.effort` latency tuning.
- The cause of the reserved-bits websocket error.

<!-- Older detailed sections archived above for space. Current learnings focused on Phase 3 integration. -->


## 2026-09-22 — feat/voice-reasoning finalize (reasoning effort benchmark)

- Live probes, 2.1:
  - Accepts `reasoning.effort` none, minimal, low, medium, high and xhigh.
  - Accepts `parallel_tool_calls` true and false, but does not echo it.
  - Accepts exactly 10 voices (fable, onyx and nova are rejected).
- Live probes, 1.5:
  - Rejects `reasoning` at every level (`invalid_value`, with NO `error.event_id`).
  - Rejects `parallel_tool_calls: true` and accepts `false`.
- Transcription: `whisper-1` is the only model that works without an extra deployment. `gpt-4o-(mini-)transcribe` pass `session.update`, but every turn then fails with `DeploymentNotFound`.
- Benchmark (2.1, real prompt and tools, text in, audio out; 18–30 trials per effort):
  - All efforts from `none` to `xhigh` have a TTFA median of 0.87–1.01 s (jitter).
  - `none` and `minimal` call tools before speaking (7/30 and 11/18 trials), so their p90 is a silent gap of 2.1 s / 5.3 s.
  - **Chose `low`**: 30/30 correct, TTFA p90 1.57 s, first tool call at 2.04 s.
  - `parallel_tool_calls` false serialises search→add, is about 1.3 s slower and uses about 2× the tokens. Keep `null`.
- Results table: `docs/customizing_deploy.md`.
- Raw JSONL: in the session `bench/` directory.

## 2026-09-23 — feat/round3

- **R1 backend (rate-limit recovery):** `app/backend/rate_limit.py` (`RateLimitRecovery`), wired in `rtmt.py`.
  - Detection: failed `response.done` whose `status_details.error` code/type contains `rate_limit`, or an uncorrelated `rate_limit` `error` event. Correlated session.update errors still go to the minimal-update fallback.
  - Ladder per failed response: silent retry after 1.5 s → `extension.rate_limited {attempt:1}` + retry after 4 s → `{attempt:2, final:true}`. A "try again in X s/ms" hint is clamped to [0.5, 5] / [2, 8] s.
  - Cancelled by speech_started, a foreign `response.created`, a browser `response.create`, or detach.
  - Composes with resume: a retry never touches the idle clock; the nudge is gated on `recovery.busy`; an error during the nudge's response doesn't stack a retry; detach cancels.
  - Config `resilience.rate_limit.*`; `RATE_LIMIT_RECOVERY_ENABLED` overrides (same name as the sibling demos).
- **Apology clips:** `scripts/generate_apology_clips.py` recorded en/es/fr/ja on `gpt-realtime-2.1` / marin (phrase in `response.instructions`), each whisper-verified word for word (2.1–3.2 s, 100–152 KB).
- **R2 (smoke check port):** `scripts/smoke_realtime.py` now fails when the transcript doesn't match the synthesised phrase (similarity ≥ 0.85), puts the phrase in `response.instructions`, and authenticates against the resource's tenant (`--tenant` / `--subscription`, env, azd; credentials tried in turn).
  - Live probe: user-turn synthesis was verbatim 1/6 (the model answered the order); `response.instructions` 6/6.
  - Live smoke passed on `gpt-realtime-2.1` (0.98) and `gpt-realtime-2.1-dz` (1.00).
- **dz:** `gpt-realtime-2.1-dz` pinned as a reasoning deployment (test + docs note).

## 2026-09-24 — feat/conformance-s1-2 Stage S1.2 (#8)

- Paired with Birdperson on the #8 conformance port in worktree `SonicAIDriveThru-wt-S1-2`, contributing the realtime-protocol/GA-shape judgment calls: confirmed the bootstrap `session.update`'s GA shape (`audio.input.transcription` rename, `voice`, `tool_choice=auto`) against my earlier live-probe notes, confirmed `deployment_supports_reasoning`'s name-based classification (2.1/2.1-dz reasoning-capable, 1.5 not — matches the rate-limit-recovery/reasoning-deployment probes from R3) so the new `Deployment` fixture override could vary it additively per-collection, and confirmed the voice-lock (`_strip_output_voice`) and reasoning-rejection-fallback semantics against `rtmt.py`'s actual GA session-update rules rather than assumption.
- Flagged and helped root-cause the wire-order subtlety in `rtmt.py`'s `_process_message_to_client`: `extension.round_trip_token` is emitted before the caller relays `response.done`, so a scenario chaining sequence-bounds between the two must account for the token arriving first on the wire — this fixed a flaky assertion in the new `ResponseCancelRelayTests.cs`.
- Reviewed all 16 mutation-test targets across the reasoning/voice-lock/session-update-fallback/close-code/barge-in scenarios for realtime-protocol plausibility (e.g. confirmed `_SessionUpdateGuard.correlate`'s order-based fallback path is only reachable because GA's 1.5 rejection omits `error.event_id` — a genuine wire quirk, not a test artifact) before Birdperson executed each isolated scratch-mutation test run.
- Final state: `dotnet test tests/conformance` green 3x (109 passed/2 skipped/0 failed), `pytest app/backend/tests -q` 604 passed/61 subtests unchanged, `ruff check .` clean. 5 commits, each referencing #8.
  ## 2026-09-24 — fix/session-scrub (S1 fan-out: #27, #29, #25)

  Sole stream allowed to touch `app/backend/`. Worktree `SonicAIDriveThru-wt-leak`.
  Three commits (`507ec94`, `00e2646`+`cd2dca3`, `edd60ba`), each with a Python fix,
  mutation-checked Python unit tests, and a mutation-checked black-box conformance
  scenario under `tests/conformance/tests/Conformance.Tests/Scenarios/Security/`:

  - **#27** — `session.updated` relayed unscrubbed (instructions/tools leaked to the
    browser). One `_scrub_session_for_client` helper now covers both
    `session.created` and `session.updated`. Un-skipped
    `SessionUpdatedClientVisibilityTests.Browser_never_receives_instructions_or_tools_in_session_updated`.
  - **#29** — scrub hardening: (a) GA echo carries `max_output_tokens` alongside the
    legacy `max_response_output_tokens` we already hid — now pop both, plus drop
    `model`, `audio.input.transcription.model`, `reasoning`, `parallel_tool_calls`.
    (b) Server-authored `role: "system"` items (resume rehydration, the nudge) were
    echoing back to the browser via `conversation.item.created`/`.added` — now
    dropped (role=system only; user/assistant items the transcript UI needs are
    untouched). New scenarios: `ScrubHardeningTests.cs`,
    `ResumeRehydrationClientVisibilityTests.cs`.
  - **#25** — Origin check was `origin.endswith(host)`, accepting lookalike domains
    (`https://evil-<host>`). Replaced with `_origin_matches_host`: exact,
    case-insensitive match of the parsed Origin's netloc against `Host`. Missing
    Origin is unchanged (still accepted) — documented, not a regression. New
    scenario file `OriginValidationTests.cs` (exact accepted, lookalike-suffix
    403, missing-origin unchanged).
  - Also added the **N14** backend-contract note (#28) to
    `tests/conformance/README.md`: a backend must close its upstream socket when
    the browser disconnects; `ConformanceFixture` already waits for this.
  - Found (not a bug): `app/backend/tests/test_security.py`'s `_validate_origin`
    is a standalone reimplementation that never called into `rtmt.py` — already
    doing correct exact-match logic, so it never would have caught the real
    `.endswith` bug. New #25 coverage exercises the real function instead
    (`test_rtmt.py`), left `test_security.py` untouched.
  - Final validation: pytest 624 passed (was 604 baseline, +20 across the three
    issues); `dotnet test tests\conformance` 96 passed, green ×3; ruff clean;
    `git status` clean; frontend `npm test` unaffected, still 116 passed.
  - Not covered black-box: `reasoning`/`parallel_tool_calls` scrub (#29b) — only
    sent by a reasoning-model deployment, not worth a dedicated `BackendProfile`;
    covered at the Python unit level only.

  ## 2026-09-27 — PR #93 revision 2 (issue #85, new environment infra)

  Under strict reviewer lockout: Squanchy authored PR #93, Rick rejected it, Squanchy is
  locked out, I own the revision. Worktree `SonicAIDriveThru-wt\p2-85-r2`, merged
  `origin/dev` in first (no rebase, no force-push).

  - **AVM module `endpoint` output is not trustworthy for AIServices-kind accounts.**
    `br/public:avm/res/cognitive-services/account` outputs `endpoint` as literally
    `cognitiveService.properties.endpoint` — the generic multi-service Foundry endpoint.
    For a plain `OpenAI`-kind account this happens to already be the
    `.openai.azure.com` host, which is presumably why nobody had hit this before; for an
    `AIServices`-kind account (this PR's item 1: `kind` changed from `OpenAI` to
    `AIServices`, immutable after creation) it is not guaranteed to be. **Lesson:** when
    a module's kind changes, re-check every output that used to be "obviously" the right
    value — build the realtime host explicitly from `customSubDomainName` instead of
    trusting the module, and confirm the format against the actual product docs
    (Foundry realtime docs show `https://{your-resource}.openai.azure.com` works
    regardless of kind).
  - **`azd provision --preview`'s console summary doesn't show property values for
    brand-new `Create` resources** — only name/type/operation, no SKU/capacity/kind
    diff (ARM `whatIf` only computes property-level deltas against something that
    already exists). To actually *prove* a tracked default like "Search SKU is basic"
    or "realtime capacity is 10" took effect from a clean env state, re-run the
    equivalent `az deployment sub what-if` directly with the same resolved parameter
    values and read the full JSON `changes[].after` properties — much stronger evidence
    than the azd summary alone, and worth doing whenever a review specifically asks for
    proof of a default, not just "it deploys".
  - **A code comment can trip a brand-name guard just as easily as real logic.** Wrote
    an inline Bicep comment for the empty-PERSONAS default that named a specific brand
    as an example (to illustrate "a new pack landing") and it failed
    `test_rebrand_verification.py`'s pre-persona-pack brand guard exactly like a real
    reference would have. Reworded to describe the situation generically. Comments are
    still source text to these guards — don't assume "it's just a comment" is safe.
  - Final validation: `az bicep build` clean (13 warnings, same classes as `origin/dev`'s
    14, no new types); `azd provision --preview` from a clean, newly-created local azd
    env succeeded (real ARM whatIf, no overrides for SKU/capacity/personas); direct
    `az deployment sub what-if` on the same clean-env values confirmed Search SKU
    `basic`, realtime capacity `10`, account `kind: AIServices`, per-model `format`,
    the explicit `.openai.azure.com` endpoint, and `PERSONAS`/`AZURE_SEARCH_INDEXES`
    both absent from the container app env when personas is empty; pytest 903
    passed/1 pre-existing unrelated failure; ruff clean. One commit (`ac7e7da`),
    pushed, no force-push. PR #93 commented (mapped items 1-5 to Rick), issue #87
    got an explicit AIServices realtime smoke-check item. Did not merge.

### 2026-09-27: PR #93 revision 3 (issue #85) — post-merge brand-guard regression, fixed by design not by baseline

  Coordinator flagged the PR as "dirty" against `dev` after a burst of merges (#92
  persona loader — already accounted for, #96 C# skeleton + dotnet CI, #99-#101
  menu/conformance/brand baseline). New worktree `p2-85-r3`, merged `origin/dev` in
  (no rebase, no force-push): **zero textual conflicts** — git's `ort` strategy
  resolved everything automatically. GitHub's `mergeable: CONFLICTING` flag had
  simply gone stale relative to how far `dev` had moved; it cleared to `MERGEABLE`
  once the merge commit was pushed. **Lesson:** a coordinator-reported "dirty"/
  conflicting PR does not always mean an actual line-level conflict — always try
  the real `git merge` first before assuming manual conflict resolution is needed;
  it may just be a stale mergeability cache that a normal merge-and-push clears.

  - **A dependency-baseline test can regress you retroactively, through no line
    you touched in the merge.** Merging in #101's new ratcheting brand-guard test
    (`test_rebrand_verification.py`) exposed that revision 2's own
    `defaultPersona string = 'sonic'` bicep default (added days earlier, before the
    guard existed) had pushed 3 (file, brand) pairs above baseline —
    `infra/main.bicep` (no entry at all), `infra/main.parameters.json` (max 1, now
    2), `DEPLOY.md` (max 5, now 6). The failure only surfaced once the *guard*
    landed via merge, not when the *offending code* landed — a reminder to
    re-run the full test suite after every merge from a fast-moving base branch,
    even when your own tracked files are byte-for-byte unchanged by the merge.
  - **When a brand-guard baseline blocks you, prefer fixing the design over
    padding the baseline — especially when told the regenerate tool is
    lower-only.** Rather than adding a baseline entry to permit `'sonic'` as
    `defaultPersona`'s hardcoded default, applied the same "omit env var when
    empty" pattern already used for `personas` (revision 2, item 5):
    `defaultPersona` now defaults to `''`, and
    `empty(defaultPersona) ? {} : { DEFAULT_PERSONA: defaultPersona }` in both
    container apps. Checked `app/backend/persona_loader.py` first to confirm an
    absent `DEFAULT_PERSONA` already falls back sanely (first-party pack if
    enabled, else first enabled id alphabetically) — so this was a genuine
    brand-neutral improvement, not just a guard-dodge, and it matches the
    existing `personas`/search-index philosophy instead of adding a one-off
    exception to it.
  - Re-validation after merge + fix: `az bicep build` clean (same 13 warning
    classes); `azd provision --preview` from a fresh clean env (new worktree, no
    carried-over `.azure` state) reproduced the same 10-resource plan; a direct
    `az deployment sub what-if` re-confirmed all 5 of Rick's original items
    (Search SKU `basic`, account `kind: AIServices`, realtime capacity `10`,
    per-model `format: OpenAI`, `.openai.azure.com` endpoint), plus a *second*
    what-if run specifically to prove `DEFAULT_PERSONA` is now also omitted when
    empty; `pytest -q` 970 passed/154 subtests/0 failures (the revision-2
    `pytest-asyncio` environment gap was already resolved here); `ruff` clean;
    all 8 CI checks green including `conformance-gate`. One fix commit
    (`1aea7c0`) on top of the merge commit (`64111de`), pushed, no force-push.
    PR #93 commented again (mapped the merge + the brand-guard fix), did not
    merge.

### 2026-09-27: Dunkin' persona pack, issue #79 (PR #111, draft) — data-only pack ported from the sibling repo, three "second real pack" collisions found and worked around without touching out-of-scope test/harness code

  Worktree `p2-79` off `origin/dev` (`76c0dfa`), branch `squad/79-dunkin-pack`. Task
  was explicitly DATA ONLY: `personas/dunkin/**` and
  `tests/conformance/testdata/personas/dunkin/**`, ported verbatim from the
  read-only sibling `dunkin-chat-voice-assistant`, following `personas/sonic/**`'s
  contract exactly (persona.json, menu/menuItems.json, prompts/*.yaml, assets).
  16 source menu items -> 16 ported, 0 exclusions. Happy hour: Brian's decision
  applied (announce it, like Sonic, instead of the sibling's silent 25% off) —
  `startHour:14/endHour:17/priceMultiplier:"0.75"/announce:true`, eligible only for
  Signature Lattes + Cold Beverages (6/16 items). Extras (`isExtra` items: flavor
  swirls, shots, milk options) share the same two eligible categories as happy
  hour, aliases pulled from the sibling's real `EXTRA_MENU_ITEMS` keyword logic,
  not invented. No bundles/machines — the sibling has neither.

  - **Schema-unsupported source fields get dropped and reported, never
    schema-edited, never fabricated.** `caffeineContent`/`brewingMethod`/
    `availability`/`calories` existed in the sibling's data but have no home in
    `menu.schema.json`; the task explicitly forbade touching the schema, so they
    were cut from `menuItems.json` and listed as a schema gap in the PR body
    instead — the least-surprising place for a future schema owner to find them.
  - **A second real persona-pack folder trips at least three *different*
    pre-existing, out-of-scope test/harness assumptions, all in the same "only
    one real pack has ever existed" shape, but with three different correct
    responses.** (1) `test_persona_loader.py`'s `test_real_sonic_pack_loads`/
    `test_valid_copy_loads_identically` hardcode `catalog.ids == ["sonic"]`
    against the real repo `personas/` dir with no override — genuinely broken by
    *any* second pack, unfixable from a data-only PR, left as a flagged,
    documented, known failure for a coordinator-level test edit. (2)
    `test_rebrand_verification.py`'s `FORBIDDEN_PATTERNS` forbids the literal
    phrase "crew member" repo-wide (a leftover McDonald's-rebrand guard with no
    persona-pack carve-out) — but "crew member" is genuine, sourced Dunkin
    terminology; since the test itself is out of scope, the *content* was
    reworded instead ("crew associate"/"Dunkin crew"/"Voice Crew assistant") —
    when a shared guard is unfixable, look for a content-side rewording that
    preserves meaning before assuming you must touch the guard. This will hit
    McDonald's #78 too, probably harder, since "crew member" is McDonald's's own
    real terminology. (3) `rebrand_scan.py`'s `DIRECTORY_EXCEPTIONS` for
    `tests/conformance/testdata/` only rescues brand `"sonic"` **by deliberate
    design** ("can't be snuck in without a code review of this literal list") —
    so any new persona's golden-testdata file that so much as *mentions* its own
    brand name in a comment trips the brand-word baseline guard. Fixed by
    wording the new golden files' prose entirely brand-name-free (verified via
    the exact same `\bword\b` regex the guard uses) rather than editing the
    guard — a zero-risk workaround when the constraint is "don't say the word,"
    not "don't have the data." **Same three collisions will recur verbatim for
    #78 (McDonald's)** — flagged prominently in the PR for whichever lands
    second, and for #77/coordinator to bundle a batch fix.
  - **The C# side of the same problem was already solved correctly, unlike the
    Python side** — `ConformancePersonas`'s own unit tests use scratch temp
    directories via a testable `DiscoverFromDisk(string)` overload instead of
    asserting against the real repo `personas/` folder, so adding a real second
    pack causes zero C# test breakage (18/18 `ConformancePersonasTests` still
    pass; confirmed with a `dotnet build` + targeted `dotnet test --filter` using
    the .NET 11 RC1 SDK). Worth pointing future Python test authors at this
    file as the pattern to copy when writing pack-count-sensitive tests.
  - Golden conformance testdata created per Rick's #79 review note even though
    no C# fixture consumes `tests/conformance/testdata/personas/<id>/` yet
    (`ConformancePersonas.cs`'s own doc comment says so) — cross-checked
    programmatically against the real `menuItems.json` (names/categories/
    prices/happy-hour eligibility) and tax arithmetic verified with Python
    `Decimal` before committing, rather than hand-typing numbers and hoping.
  - Final validation: real `PersonaCatalog.load()`/`PromptLoader` (production
    code, not a mock) load/render the pack cleanly; `ruff` clean; `pytest -q`
    with `PERSONAS=sonic,dunkin` — 1030 passed/165 subtests, only the 2 flagged
    `test_persona_loader.py` collisions failing; `test_rebrand_verification.py`
    26/5 subtests all green (no baseline regen needed — the reworks removed the
    offending references rather than needing new baseline entries). Three
    commits (`03d54a9`, `5225f88`, `a8c848a`), pushed, PR #111 opened as
    **draft** (merge gate: #77 + #76 part 2 must land first, coordinator
    decides). Cleaned up a stray scratch venv in the main checkout and leftover
    issue-research `.txt` dumps under `C:\src\repos\` left over from an earlier
    phase of this same session.


### 2026-09-27: McDonald's persona pack, issue #78 (PR TBD, draft) — the predicted "crew member" collision from #79's history entry hit exactly as forecast, plus a new bidirectional brand-guard catch

  Worktree `p2-78` off `origin/dev`, branch `squad/78-mcdonalds-pack`. Task was
  explicitly DATA ONLY: `personas/mcdonalds/**`, adapted (not copied) from the
  read-only sibling `McDonalds_AI_DriveThru`, mirroring `personas/sonic/**`'s
  contract. 134 source products -> 134 menu items, 0 exclusions. Full detail in
  the decision note (`.squad/decisions/inbox/unity-78.md`).

  - **The #79 (Dunkin) history entry's prediction came true, harder, as
    forecast.** That entry flagged the repo-wide "crew member" ban in
    `test_rebrand_verification.py` would hit McDonald's "probably harder, since
    'crew member' is McDonald's's own real terminology" — exactly right: the
    sibling's real role name and 5 prose occurrences all used "crew member".
    Same fix pattern as #79: reword content ("team member"), never touch the
    guard. **Lesson reinforced**: when a sibling agent's history entry predicts
    a specific collision for your own upcoming issue, that prediction is worth
    checking literally before you even start authoring prose — would have saved
    a full pytest cycle if I'd grepped the sibling's role name against the
    banned-phrase list up front instead of discovering it via test failure.
  - **New catch this pack surfaced that #79's didn't call out explicitly: the
    brand-guard baseline test is bidirectional**, and it caught my *own*
    explanatory comment ("...the way Sonic's Flavor Add-In / Add Bacon are...")
    inside `personas/mcdonalds/prompts/error_messages.yaml` — a persona pack
    may not name another persona's brand even in a code comment aimed at a
    future reader. Reworded to drop the brand name entirely; worth calling out
    since it's an easy thing to write innocently (comparing to the reference
    pack by name) and only surfaces at test time, not at review time.
  - **Same #79-flagged `test_persona_loader.py` pair broke again** (`catalog.ids
    == ["sonic"]` hardcoded against the real repo `personas/` dir) — confirmed,
    as #79 predicted, this recurs for every second-and-later real pack, still
    unfixable from a data-only PR. **New finding this pack added**: the C#
    side isn't fully immune after all -- while `ConformancePersonasTests`
    (harness-internal) uses scratch temp dirs and stayed green as #79 found,
    `app/backend-dotnet`'s own `Backend.Tests/Personas/PersonaCatalogTests.cs`
    has the *same* real-repo-`personas/`-dir hardcoding as the Python test
    (`EmptyPersonasDirectory_ThrowsNoPersonasEnabled`), plus a second, sharper
    one: `DefaultPersonaNotEnabled_Throws` literally uses the *string*
    `"mcdonalds"` as its placeholder for "a persona id that doesn't exist" --
    written before #78 existed, now ironically broken by #78 landing for real.
    Flagged all of these for a coordinator-level batch fix rather than touching
    backend/test code from a data-only PR.
  - **CI parity required actually building the frontend once locally**:
    `app/backend/static/` is gitignored, generated-only content
    (`npm run build` in `app/frontend`); its absence in a fresh worktree cascades
    into ~7 unrelated `create_app()`/`Program.cs` startup test failures. Checked
    `conformance.yml` first and confirmed CI always builds the frontend before
    either backend leg runs — so ran `npm run build` locally too rather than
    faking a placeholder `index.html`, to get an honest, CI-equivalent
    `pytest -q` result rather than a partially-blocked one.
  - Full validation: `python -m pytest -q` 1031 passed / 2 failed (both the
    pre-existing single-pack assumption pair, documented above); `ruff check`
    clean; `regenerate_rebrand_baseline.py` zero diff (own-brand-pack exemption
    already covers it, never used `--allow-increase`); .NET 11 RC1
    `Backend.Tests` 79 passed / 2 failed (same pair, C# side); full conformance
    suite with both personas auto-discovered from disk (CI's own convention,
    `CONFORMANCE_PERSONAS` left unset) -- python backend leg 629 passed / 0
    failed, dotnet backend leg (`Dotnet=ready` subset) 11 passed / 0 failed.
    Reverted an incidental `app/frontend/package-lock.json` one-line diff from
    `npm install` before committing (unrelated to this task's scope). 4 small
    commits (persona.json; prompts; menu; assets), pushed, PR opened as
    **draft** (merge gate: #77 + #76 part 2 must land first, coordinator
    decides). Cleaned up scratch pytest/conformance output captures before
    committing; `.venv`/`node_modules`/`static/` all confirmed gitignored.

- **#78 PR #112 follow-up round (dev merge + #107 price-wording fix)**: after
  coordinator confirmed #114 (pack-agnostic shared tests, `d475c77`) landed on
  `dev`, merged `origin/dev` into `squad/78-mcdonalds-pack` with a plain
  `git merge` (no rebase, no force-push, per the strict git rule) -- clean,
  13 files touched, zero conflicts. Applied Rick's #107 review wording to my
  own pack's `price` tool-schema param (`"Ignored; the server prices from the
  menu."`) *ahead* of #107 itself landing on dev -- confirmed via
  `git show origin/dev:personas/sonic/prompts/tool_schemas.yaml` that Sonic's
  own pack still had the old wording, so this is intentional get-ahead-of-it
  asymmetry per the coordinator's explicit instruction, not a mistake; it
  self-resolves to byte-identical text on the next `dev` merge once #107 lands.
  Also swept the system prompt for any language telling the model to
  extract/send a price to `update_order` (3 spots) while preserving legitimate
  verbal price-quoting-to-the-guest guidance -- worth remembering as a pattern:
  a tool-schema wording fix is rarely complete without checking the prompt
  prose that references the same tool/field.
  Re-ran full validation post-merge and confirmed the fix worked as advertised:
  pytest 1064 passed / 0 failed (both previously-failing single-pack-assumption
  tests now pass), ruff clean, .NET Backend.Tests 82 passed / 0 failed,
  conformance python leg 634/634, dotnet leg 11/11. Checked #108's status before
  attempting the smoke.json testdata step -- still open/unmerged, so left that
  step explicitly pending rather than guessing at a template that doesn't exist
  yet. Pushed, watched CI to completion (all 8 checks green), reported back to
  the coordinator with head SHA on the PR itself.

### 2026-09-28: PR #110 (#80, `squad/80-picker-assets`) round 3 — Rick's review
- **Item 1 (logo):** test-alpha's fixture `logo.svg` was a size-less 1px circle
  (no `viewBox`/`width`/`height`), so it loaded 200, `onError` never fired, and
  the browser reserved a `300x150` intrinsic box that offset the hero badge.
  Replaced it in place with a real 160x48 wordmark SVG (rounded rect + "ALPHA"
  text) and hardened `BrandHero`'s `<img>` with `max-w-[14rem] object-contain`
  so a future size-less logo can't blow out the layout again — belt-and-braces,
  not just a fixture swap. Verified visually via Playwright: the badge sits
  flush next to "VOICE ORDERING DEMO" with no offset. Added
  `App.brandHero.test.tsx` (test-alpha image + hardening classes; test-beta
  text fallback).
- **Item 2 (baseline):** the two "raise" root causes were both hardcoded
  `personas/sonic/...` paths (`generate_apology_clips.py`,
  `test_rate_limit.py`) — fixed by deriving the path from
  `default_persona.get_default_persona().assets_dir` instead, which reverted
  both counts to dev's baseline (no raise needed at all). The three "new
  entries" were legitimate persona-context/test files already in scope for
  #80 — reworded their comments to drop brand-name prose rather than lower
  the bar for the guard. **Gotcha:** `regenerate_rebrand_baseline.py` only
  recomputes numeric `max`; it does NOT revert `issue`/`reason` text once a
  count returns to baseline — that has to be hand-restored to dev's exact
  wording (`git show origin/dev:...`) or the diff shows a spurious text-only
  change even with the right number. Confirmed clean via both
  `git diff origin/dev -- rebrand_baseline.yaml` (decreases/removals only) and
  the CI script itself,
  `check_rebrand_baseline_against_base.py <base-baseline>` (exit 0).
- **Item 3 (loading shell):** spinner recolored to `border-muted-foreground`
  (was `border-primary`); a `data-persona-loading` attribute on
  `<html>` (toggled by a `useEffect` keyed off `!personaReady`) drives new
  `index.css` rules that hide the body's gradient background and the
  `::before`/`::after` decorative blobs until a persona resolves. Verified via
  a Playwright `page.route` delay on `/api/personas` to reliably freeze the
  loading state long enough to screenshot it (a local backend resolves too
  fast to catch by chance).
- **Item 4 (PR #106 coordination):** #106 (Birdperson round 3, issue #75)
  merged to dev *during* this session — merged `origin/dev` again
  post-implementation (clean, no conflicts) and re-validated everything
  against the real shape rather than trusting the pre-merge assumption. The
  frontend types (`PersonaModelOption {id,label,reasoning}`,
  `PersonaModelPipeline {default, models?: PersonaModelOption[]}`) already
  matched exactly what `app.py`'s `_model_pipelines_body`/`_selectable_models`
  emit post-merge — built this against the PR description ahead of the merge
  landing, and it held up byte-for-byte once verified against dev's real
  `app.py`. **Gotcha:** `brandColorTokens.test.ts`'s hex-literal guard
  (`/#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3})(?![0-9a-f])/gi`)
  flags bare `#106`/`#110`-style refs in source comments as false-positive hex
  colors, because 3+ pure-digit numbers are also valid hex — write "PR 106" /
  "issue 80" without the `#` in `app/frontend/src` comments instead (`#75`/
  `#80` are safe only because they're 2 digits, below the regex's 3-digit
  floor).
- **Validation:** `npx vitest run` 230/230; `npm run build` clean;
  `dotnet test --filter Category=Browser` 5/5;
  `dotnet test --filter Category!=Browser` (python backend) 647/647 (up from
  637 pre-#106-merge); `pytest app/backend/tests` 1147 passed/168 subtests (up
  from 1073); `ruff check .` clean. Two separate Python venvs were needed in
  this worktree: a repo-root one (`<worktree>/.venv`) that
  `Conformance.Harness.PythonBackendLauncher` hardcodes for the dotnet test
  harness, and `app/backend/.venv` only for the one-off baseline regeneration
  script — conflating them wastes time chasing a launcher failure that's
  actually just the wrong venv location.
- **Screenshots** (Playwright, real backend serving a scratch `PERSONAS_DIR`
  combining `personas/` + the two fixture packs, never committed): test-alpha
  light (logo aligned), test-beta light (text fallback), first-load neutral
  shell (delayed `/api/personas` route to freeze the loading state), Sonic
  light + dark (regression, unchanged).
- 6 commits on top of the two `origin/dev` merges (`ff3aee4` pre-work,
  `73826c0` post-#106), pushed, no force-push, no rebase. PR #110 commented
  addressed to Rick mapping all 4 items with the baseline diff pasted inline;
  did not merge.

## #78 McDonald's pack — round 4 (2026-09-27): #108/#110 merge, smoke.json, UX check
- Real `smoke.json` schema (from reading `PersonaSmokeTests.cs` directly, not
  guessing) is exactly 5 fields: greetingSubstring, searchableOwnItem,
  orderableItemName, orderableItemSize, orderableItemPrice. No off-menu-item
  field, no happy-hour-banner field exist in the actual harness — those two
  proofs use hardcoded literals / fixture-pack-only fixtures respectively.
  Lesson: when a coordinator relays "add X if the format supports it," read the
  actual test/schema source before assuming the ask maps to a real field.
- Brand guard scope silently expanded (via #108/#110/#114) to also scan
  per-persona conformance testdata, not just personas/<id>/**. A stray
  cross-pack brand mention in a testdata description file tripped it. Lesson:
  the brand guard's scope can grow underneath you across dev merges — rerun
  `test_rebrand_verification.py` after every merge, don't assume prior-round
  scope still holds.
- #110 restructured frontend asset serving (deleted old public/ assets in
  favor of per-pack /personas/<id>/assets/** served directly). A
  well-structured persona pack built ahead of that change needed zero
  modification — following the schema/reference-pack conventions closely paid
  off across an unrelated infra refactor landing later.
- Playwright UX check on a native <select> persona picker: element refs from
  browser_snapshot invalidate quickly under live theme/context re-renders; use
  browser_evaluate + document.querySelector/CSS selectors for anything that
  needs to survive across multiple interaction steps, not snapshot refs.
- Found a real (pre-existing, shared-frontend, out-of-scope-to-fix-here) bug:
  Settings dialog hardcodes "Carhop Voice" regardless of active persona.
  Reported via PR comment + screenshot rather than fixed, since #78 is data-only.
