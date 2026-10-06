# Squad Decisions

## PR #50 review, round 4 (Rick: Approve, with should-fix items before merge)

- **`®` removal is now one rule, not two.** `_menu_key()` (the shared normalisation helper) now
  strips `®` itself, as its own explicit step, alongside paren-group removal, whitespace collapse,
  and lowercasing. Previously `®` removal only existed in `order_state.py`'s ad-hoc combo-conversion
  string, and `MENU_CATEGORY_MAP` was still keyed by bare `name.lower()`.
  **Correction (PR #50 review round 5, should-fix 3):** the line above originally claimed this was a
  *live* bug affecting "12 of the 60" `®`-bearing menu items. Re-checked directly against `467494b`
  (the commit before this fix): map construction (`name.lower()`) and lookup
  (`strip_modifiers(item_name).lower()`) were **both** missing `®` removal — symmetric, so
  `®`-bearing names actually resolved fine at that point. The one genuine *live* bug was narrower:
  map construction didn't collapse whitespace the way `strip_modifiers()` does, so only the single
  NBSP-bearing OREO Blast name could ever actually fail to resolve via the map. Consolidating `®`
  removal into `_menu_key()` was still worth doing — it closed a *latent* duplication/drift risk,
  since `order_state.py`'s combo-conversion matching already stripped `®` independently while
  `menu_utils.py` didn't — but it was not fixing a live classification bug for the other 11 `®`
  names. The "reverting the map-key line fails a test for all 12" mutation result is real and worth
  keeping as a regression guard (it proves construction and lookup must stay in sync going forward),
  but it does not mean those 12 were broken in the code as shipped before this fix.
- **Keyword fallbacks now match on word boundaries, not bare substrings.** A plain
  `any(kw in normalized ...)` substring check let `"tea"` match inside `"steak"`, silently
  absorbing an off-menu `"Philly Cheesesteak"`/`"Steak Sandwich"` into a combo's drink slot for
  free and happy-hour-discounting it. Both the fountain-drink and shake/blast/malt keyword lists
  are now compiled `\b...\b` regexes (optional trailing `s` for plurals); Dr Pepper's existing
  regex was already word-boundary and is unchanged. Every on-menu item still resolves via
  `MENU_CATEGORY_MAP` directly (proven by a test that patches both fallback functions to raise) —
  these fallbacks only ever see genuinely off-menu names.
- **Customised sundaes pin Brian's #39 decision under customization too, not just the plain
  case.** `"Hot Fudge Sundae (Extra Fudge)"` in a combo is charged in full (never fills the drink
  slot, kills Rick's Z2) and stays full price at happy hour (kills Z3) — added as two new
  `CustomisedItemMenuLookupTests.cs` Facts, alongside the plain-sundae case already pinned in
  `GoldenMenuComboSlotTheoryTests`/`test_menu_utils.py`.
- **Y4 (an off-menu fountain drink discounted at happy hour) is now pinned at the C# conformance
  level too, not just Python.** Previously only `test_menu_utils.py` covered "an off-menu drink
  like Dr Pepper Zero is still happy-hour discounted"; a new
  `Off_menu_fountain_drink_is_happy_hour_discounted` Fact in `CustomisedItemMenuLookupTests.cs`
  runs the same assertion end to end against the live backend, mirroring the combo-slot version of
  the same case (`Off_menu_fountain_drink_still_absorbs_into_the_combo_drink_slot`) that already
  existed there.
- **README now states the exact `_menu_key()` algorithm as an ordered list** (paren-group removal
  anywhere in the string → whitespace collapse → lowercase → `®` removal) so a C# reimplementation
  has no ambiguity left to diverge on. A new `CustomisedItemMenuLookupTests.cs::
  ParenGroupNormalisationTests` pins the three edge cases Rick's review explicitly asked for: two
  separate `(...)` groups (both strip, still absorbs as a side); a *mid-string* (not just trailing)
  group (strips correctly to a different, non-side real menu item, charges in full); and a
  nested/unbalanced group (fails safe to full price rather than risking a wrong, silent match).
- **Explicitly out of scope (Rick will file separately):** the Python conformance-suite money
  tolerance change, and the `menuItems.json` schema redesign referenced in the correction on entry 38
  above.

## PR #50 review, round 5 (Rick: Approve, with should-fix items before merge)

- **Keyword over-correction fixed: the round-4 word-boundary regexes were too strict and broke
  real spoken off-menu variants that worked at `467494b`.** `\bshake\b` never matches "milkshake"
  at all (no word boundary between "milk" and "shake"), so "Chocolate Milkshake" fell through to
  unclassified; the old `s?` suffix only allowed a single trailing "s", so "Cherry Slushes"
  ("-es") and "Blue Raspberry Slushie" ("-ie") also fell through. Fixed with
  `r"\b(?:slush(?:ie|y)?|limeade|ocean water|drink|tea|lemonade|coke|sprite|root beer)(?:e?s)?\b"`
  (fountain) and `r"(?:\b|milk)(?:shake|blast|malt)(?:e?s)?\b"` (shake/blast/malt) — the latter's
  `(?:\b|milk)` prefix is a narrow, deliberate carve-out for the "milk"+"shake" compound only, not
  a general loosening (a nonsense "Overshake Deluxe" still correctly doesn't match). Both the
  original round-4 fix (steak/tea word-boundary) and this correction are pinned together: a new
  `KeywordOverCorrectionTests` pytest class plus a `Theory` over the three spoken variants in both
  `ComboSlotTests` and `HappyHourDiscountTests` in `CustomisedItemMenuLookupTests.cs`. Mutation
  check: reverting either regex to its round-4 form fails exactly the 6 new conformance rows (3
  combo-slot + 3 happy-hour), the other 16 stay green.
- **README documentation corrections (should-fix 2):** "lowercase" is now stated as
  culture-invariant (C#'s `ToLowerInvariant()`, not the culture-sensitive `ToLower()`); the
  whitespace-collapse step now states the Python-side rule precisely (`str.isspace()`, which
  Python's `str.split()` uses internally) and notes that every `menuItems.json` name's whitespace
  is either an ASCII space or a single NBSP, so C#'s `char.IsWhiteSpace` — which also treats NBSP
  as whitespace — agrees on every real name without special-casing.
- **README history correction (should-fix 3):** the "12 of the 60 items were affected" story has
  been removed from the *rule* statement in the conformance README (the contract only needs to
  state the algorithm, not its discovery history); the corrected story now lives in this file, in
  the round-4 entry above (see the "Correction" paragraph added to the `®` removal bullet under
  "PR #50 review, round 4").
- **`™` and the curly apostrophe `’` are now normalised in `_menu_key()`, exactly like `®`
  (should-fix 4).** `™` is stripped; `’` (U+2019) is replaced with a plain `'` (U+0027). Eight
  `menuItems.json` names contain `™` (the "SONIC Smasher" family, plain and Combo) and one contains
  `’` (the REESE'S Blast); all nine previously missed their own `MENU_CATEGORY_MAP` entry and
  relied on keyword-fallback luck exactly like the OREO Blast's NBSP did before round 4. New
  pytests prove all nine resolve directly (fallbacks patched to raise, mirroring
  `MenuCategoryMapDirectResolutionTests`). A Burger/Sandwich item's map miss is invisible through
  the combo-slot/happy-hour paths (neither keyword list matches "smasher" either way), so the new
  conformance row instead pins the one place the miss *is* end-to-end observable: `update_order`'s
  extras-eligibility check (`ALLOWED_EXTRA_CATEGORIES` includes "burgers & sandwiches", but only if
  the Smasher resolves via the map) — a Smasher spoken without its `™` must still let a follow-up
  "Add Bacon" extra through instead of being wrongly rejected. Mutation check: reverting `_menu_key`
  to the round-4 (®-only) form fails exactly that one new conformance test (23 → 22 passing), the
  other 22 stay green; restored, re-confirmed 23/23.
- **Hyphens are word boundaries in both regex engines by design (should-fix 5, no behaviour
  change).** Documented as a note only in the README's keyword-fallback section — Python's `\b`
  and C#'s `\b` (via `Regex`, whose word-character definition matches .NET's) both treat `-` as a
  non-word character, so a keyword adjacent to a hyphen (e.g. a hyphenated customization like
  `"(Extra-Crispy)"`, or the `"All-American"` prefix on the Smasher family) still gets a correct
  boundary on either side without any special-casing — a future C# port of these keyword regexes
  needs no adjustment for hyphens.

#### 42. Customised Items Must Be Normalised Before Every Menu Lookup (Summer — Backend Dev, PR #50 review round 2)
- **Root cause: customizations live *inside* `item_name`, and lookups didn't account for that.**
  A modifier like `"Tots (Extra Crispy)"` or `"Chili Cheese Tots (Extra Cheese)"` is a single
  string sent by `update_order` — there is no separate "base item" field. `infer_category()`,
  `infer_combo_component()`, and `is_happy_hour_discounted()` were all matching against the raw,
  unstripped, lowercased name. A customised item therefore never hit its real
  `menuItems.json`/allow-list entry and fell through to substring keyword guessing instead —
  which could (and did) disagree with the item's true classification. Rick's concrete repro:
  a Cheeseburger Combo plus `"Chili Cheese Tots (Extra Cheese)"` absorbed the tots for free
  (matched the bare `"tots"` keyword) instead of charging $3.79 in full, because "Chili Cheese
  Tots" is its own priced menu item, not one of the two combo-side-slot items.
- **Fix: one shared `strip_modifiers()`/`_menu_key()` helper, reused everywhere, not duplicated.**
  `menu_utils.strip_modifiers()` removes the trailing `(...)` suffix and collapses whitespace;
  `_menu_key()` lowercases the result. Every classification function (category, combo-slot,
  happy-hour-discount) now normalises through it, and `order_state.py`'s pre-existing ad-hoc
  combo-conversion base-name stripping (`item_name.split("(")[0]`-style) was replaced with a call
  to the same helper rather than kept as a second, independent implementation of the same rule —
  the exact class of bug the Route 44 alias fix (#40, entry 41 above) already taught us to avoid:
  two pieces of code doing raw string matching against the same source of truth, with no shared
  normalisation step, desync the moment the model introduces a transformation (aliases there,
  parenthesized modifiers here).
- **The side-slot keyword fallback is deleted, not fixed (Rick's explicit instruction).** An
  unrecognised/off-menu item — customised or not — never fills the combo side slot; it is always
  charged in full. "A charged item is visible and correctable; a free one is silent revenue
  loss." Only the literal `_COMBO_SIDE_ITEMS` allow-list (tots, groovy fries, post-normalisation)
  can occupy that slot.
- **The drink keyword fallback is split so the happy-hour flag is genuinely the single switch.**
  Fountain-drink keywords (Dr Pepper, Coke, Sprite, root beer, ...) remain unconditionally
  eligible for both the combo drink slot and the happy-hour discount — they're always full-price
  fountain drinks otherwise. Shake/blast/malt keywords are still unconditionally eligible for the
  combo drink slot (that's a menu-composition fact, unrelated to pricing), but the happy-hour
  discount question for them is gated exclusively by
  `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED` — proven by a dedicated test that flips the flag and
  asserts every shake/blast variant (plain, customised, on-menu, off-menu) changes together, while
  combo-slot eligibility stays unaffected.
- **Regression coverage added on both sides:** `test_menu_utils.py::CustomisedItemMenuLookupTests`
  (12 Python unit tests) and a new `CustomisedItemMenuLookupTests.cs` (5 live-backend Facts,
  including the customised Cherry Limeade happy-hour case that kills Rick's Y4).



## 2026-10-06 — #328: deterministic heartbeat/pong transport test

- **Decision:** `test_first_data_frame_after_heartbeat_pong_is_accepted` (`test_ws_transport.py`) no longer patches `_WS_HEARTBEAT_SEC` down to 0.2s to force a PING. That raced aiohttp's own pong-timeout (`heartbeat / 2` = 0.1s) against the test process getting scheduled to receive the PING and write the PONG back — on a loaded CI runner the race lost intermittently (`AssertionError: server killed the socket: None None`, PRs #313/#327), unrelated to the real regression under test (aio-libs/aiohttp#13274, first compressed data frame after a PONG). Fixed by leaving the production heartbeat interval untouched and triggering the PING deterministically via the server-side `WebSocketResponse.ping()` (aiohttp's public one-off-ping API, which never arms a pong-timeout), fetched from `self.rtmt._sessions._session_map` after awaiting session creation.
- **Verification:** 30/30 local runs green (~0.77s each, no timing variance). Mutation check: temporarily forced `compress=True` on the browser-facing `WebSocketResponse` in `rtmt.py` (reintroducing the #13274 regression) — both this test and `test_handshake_does_not_negotiate_permessage_deflate` failed with the real aiohttp error (`Received frame with non-zero reserved bits`, close code 1002); reverted via file copy, `diff` confirmed byte-identical restore.
- **Scope:** Test-only change — no product code touched. The underlying fix (declining permessage-deflate on the browser socket) was already correct; this was a test-harness timing bug, not a real product race.
- **Risk/trade-off:** None — purely a test determinism fix with its regression teeth proven by mutation check.
- **Team impact:** `test_ws_transport.py::BrowserSocketTransportTests` should no longer flake under CI load; future reviewers mutating the compression/heartbeat protection will still get a red test.

## Issue #335: IHttpClientFactory + hosted background services

**Author:** Beth (.NET backend dev)
**Scope:** `app/backend-dotnet/src/Backend`

##### Decisions

1. **PooledConnectionLifetime = 10 minutes.** Applied to all three named HttpClients
   (`search-endpoint`, `cascade-endpoint`, `connectivity-check`) registered via `AddHttpClient` +
   `ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = ... })`
   in `Program.cs`. 10 minutes sits comfortably inside typical Azure DNS TTLs while still giving
   good connection reuse for the normal (no-failover) case. All three clients share the same value
   for simplicity; nothing in the issue called for per-client tuning, and introducing different
   values per client would be an unjustified extra knob.

2. **EntraAuthentication.cs backchannel left as a plain `new HttpClient(...)`, not sourced from
   IHttpClientFactory.** `ConfigureJwtBearer` is registered via the plain
   `AddJwtBearer(Action<JwtBearerOptions>)` overload (no DI access), and is deliberately kept a
   pure, directly-unit-testable static method (every existing unit test calls it directly with no
   running host). The method also constructs `HttpDocumentRetriever`/`ConfigurationManager` using
   `options.Backchannel` synchronously, in the same method body that assigns it — so sourcing the
   client from the factory would require switching to
   `AddOptions<JwtBearerOptions>().Configure<IHttpClientFactory>(...)`, which still has to have the
   factory-created client fully assigned before the rest of the same method runs. That restructuring
   wasn't worth the risk for a backchannel client that's only used for OIDC discovery fetches, not
   the hot path. Left a comment in place at the call site explaining this (per the issue's "document
   why not" allowance).

3. **A third `new HttpClient()` site, not mentioned in the issue body: `Program.cs`'s
   `CheckServiceConnectivityAsync`** (startup-only best-effort connectivity probe). The acceptance
   criteria says "zero `new HttpClient(` in Backend outside tests", so this one needed fixing too —
   given its own named client (`connectivity-check`) from the same factory, with the same 5s
   `Timeout` set on the instance as before (instance-level `Timeout` is unaffected by handler
   pooling, so this is a no-behavior-change swap).

4. **SessionManager's sweep loop → `SessionSweepService : BackgroundService`,** registered via
   `builder.Services.AddHostedService(_ => new SessionSweepService(sessionManager!))`. Registration
   must happen before `builder.Build()` (service registration is build-time), but `sessionManager`
   itself is still constructed later in `Program.cs` (unchanged location — it depends on
   `appConfig`/`sessionsConfig`/`logger`, all only available after Build() per this file's existing
   fail-fast startup-check ordering). The factory closure captures the `sessionManager` local
   variable (declared `SessionManager? sessionManager = null;` up at the registration point); it's
   only ever invoked by the host's DI container when hosted services start inside `app.Run()`, by
   which point the real assignment has already executed. This keeps the diff surgical — no need to
   restructure Program.cs's fail-fast startup sequence to move `appConfig`/`sessionManager`
   construction earlier.

5. **`FireAndForget(this Task, ILogger?, string)` helper** in `Backend.Shared` — observes a
   discarded task and logs any fault at Error level (skips cancellation, which is an expected
   shutdown/supersede path, not a fault). Used at every fire-and-forget call site in
   `src/Backend` (RealtimeProcessor x2, CascadeProcessor x1, EchoSuppressor x2, NudgeScheduler x1,
   RateLimitRecovery x1). Every one of those task bodies already has its own internal try/catch
   except `RealtimeProcessor.AnnounceAfterFirstFrameDecisionAsync` (only catches
   `OperationCanceledException` around its first await) — for that one site, the helper is the first
   thing that will ever log a fault past that point, exactly matching the issue's own description of
   the helper's purpose. `EchoSuppressor` has no `ILogger` field at all (never did), so its two call
   sites pass `logger: null` — the helper still observes the task (preventing an unobserved-task-
   exception) even with no logger to report to.

##### Verification
- `dotnet build src/Backend/Backend.csproj -c Release`: 0 warnings, 0 errors.
- `dotnet test tests/Backend.Tests/Backend.Tests.csproj -c Release`: 824/824 passed (new
  `FireAndForgetTests` included).
- `dotnet test tests/conformance/Conformance.slnx --filter "Dotnet=ready&Category!=Browser"`
  (`CONFORMANCE_BACKEND=dotnet`): 723/723 passed.
- Grep-verified: zero `new HttpClient(` in `src/Backend`; zero un-helpered
  `_ = Task.Run`/`_ = ...Async(` fire-and-forget patterns in `src/Backend`.

## Issue #335 revision: addressing Rick's reject review of commit 210345b

**Revision commit:** `a1922ba` on `dev` (new commit, not an amend of `210345b`)
**Scope:** `app/backend-dotnet/src/Backend`, `app/backend-dotnet/tests/Backend.Tests`
**Reviewer trigger:** Rick's 2026-10-06 🔴 Reject review (`.squad/agents/rick/history.md`)

##### Decisions

1. **Factory-registered HttpClient logging fix.** `AddHttpClient(name)` wires up
   `LoggingHttpMessageHandler`/`LoggingScopeHttpMessageHandler` by default, which log every
   request at Information (full exception on failure) under
   `System.Net.Http.HttpClient.{name}.*`. The old plain `new HttpClient()` instances never did
   this, so it broke the "logs must stay identical" ground rule. Fix: pulled the three named-client
   registrations (`search-endpoint`, `cascade-endpoint`, `connectivity-check`) out of `Program.cs`'s
   top-level statements into a new `Backend.Shared.BackendHttpClients.AddBackendHttpClients`
   extension method, and added `.RemoveAllLoggers()` to each registration inside it.

2. **New regression test: `BackendHttpClientsTests`.** Builds a plain `ServiceCollection`, adds a
   custom `ILoggerProvider` that records every log entry's category, calls
   `AddBackendHttpClients`, overrides the primary handler with a fake one that returns 200
   immediately (no real network I/O), makes one request through `CreateClient`, and asserts no
   `System.Net.Http.HttpClient.*` category entries were captured. Verified by hand: temporarily
   removing `.RemoveAllLoggers()` makes this test fail (it captures exactly the
   `LogicalHandler`/`ClientHandler` request/response log lines Rick found manually); re-adding it
   makes the test pass again.

3. **Comment trims.** Removed issue/PR-narration phrasing Rick flagged by exact quote in
   `Program.cs`, `SessionSweepService.cs`, and `FireAndForget.cs` ("C# review #335 ...", "Issue #15
   follow-up (#335) ...", "matching how they were three independent `new HttpClient()` instances
   before", SessionSweepService's "instead of ... used to use directly", FireAndForget's "(today's
   actual behavior ...)" plus its hard-coded call-site class list). Kept only comments explaining
   *why* a design choice was made.

4. **Non-blocking cleanups.** Trimmed the overstated `EntraAuthentication` backchannel comment to
   the real reason (keeping `ConfigureJwtBearer` pure/directly-unit-testable for a low-frequency
   OIDC-discovery-only client, not "this would be awkward/unsafe"); removed the redundant
   `using Microsoft.Extensions.DependencyInjection;` from `Program.cs` (already implicit via the
   Web SDK); made `FireAndForgetTests.AlreadyFaultedTask_LogsErrorSynchronously` a synchronous
   `void` test (nothing was actually being awaited).

##### Verification
- `dotnet build src/Backend/Backend.csproj -c Release`: 0 warnings, 0 errors.
- `dotnet test tests/Backend.Tests/Backend.Tests.csproj -c Release`: 825/825 passed (824 prior +
  1 new regression test).
- Manually ran the built `Backend.dll` (Development, endpoints pointed at `127.0.0.1:9`): only the
  pre-existing `⚠️ ... unreachable (non-fatal)` warnings appear at startup; no
  `System.Net.Http.HttpClient.*` log lines.
- `dotnet test tests/conformance/Conformance.slnx --filter "Dotnet=ready&Category!=Browser"`
  (`CONFORMANCE_BACKEND=dotnet`): 723/723 passed, matching Beth's prior baseline.
- Grep-verified all four of Rick's exact flagged phrases are gone from `src/Backend`.

##### Note on revision ownership
Per the reviewer-rejection lockout protocol, this revision was done by a different agent than
Beth (the original author of `210345b`), who remains locked out of revising this specific
artifact. Everything Rick confirmed as correct (timeouts, client isolation,
`PooledConnectionLifetime`, `SessionSweepService`'s closure-capture pattern, the `FireAndForget`
helper's logic, the remaining `new HttpClient(` in `EntraAuthentication.cs`) was left untouched.

## Summer — PR #290 / #126 cascade regression: final, evidence-backed fix

Supersedes the earlier "echo-cooldown-only" write-up. That earlier fix (only arm echo
suppression when `echoCooldownSeconds > 0`, plus `SessionManager.Detach` on cascade close) is still
correct and still in place (commit `ff35f9b`). But it only made barge-in actually *fire* during TTS.
It did not cause the hang, and it did not fix it.

##### Symptom

At `ff35f9b`, `BrowserSocketCancellationTests.RunSessionAsync_BargeInDuringABackpressuredTtsWrite_DoesNotAbortTheBrowserSocket`
timed out in `DrainUntilAsync`. Locally it failed 6 of 7 runs and passed once, slowly (~10 s).
The same test on `origin/dev` failed 4 of 4 here, so the problem predates the #126 work.

##### Root cause 1 (product): barge-in deadlocked the browser receive loop

- The `speech_started` branch of `HandleClientMessageAsync` did `await CancelCurrentTurnAsync(...)` inline.
  - The receive loop awaits that handler frame by frame.
  - `CancelCurrentTurnAsync` awaits the cancelled turn task.
- The cancelled turn can only stop once its in-flight browser `SendAsync` finishes.
  - `SendTextAsync` deliberately never passes `turnCt` to the real write: the #236 invariant, because cancelling an in-flight `ManagedWebSocket.SendAsync` aborts the whole socket.
  - So the write only finishes once the browser reads.
- If the browser is meanwhile blocked writing mic audio to the server, both peers are stuck in a write that the other side never reads. That is a write/write deadlock, and it lasts until teardown.

Trace evidence: temporary timestamped probes, since removed. Run with the harness fix (below) and the old blocking code:
- The receive loop entered `CancelCurrentTurnAsync → await task` at the barge-in.
- The turn stayed parked inside its chunk `SendAsync`.
- The guest's `SendAsync(silentChunk2)` never completed.
- The run failed 6 of 6.

##### Root cause 2 (test harness): post-handshake buffer shrink caused a TCP RTO crawl

- `OpenLoopbackWebSocketPairAsync` set `SO_RCVBUF`/`SO_SNDBUF = 512` *after* the TCP handshake.
- The window already advertised for the default buffer (tcp_rmem default 131072) can't be retracted.
  - So the receiver dropped data the sender was entitled to send.
  - The sender then crawled through exponential RTO backoff, delivering about 2.3 KB per timeout.
- Evidence:
  - `/proc/net/tcp` for the server socket: tx queue about 27–31 KB, draining about 2304 B every few seconds; rx queue 12855, which is the unread silentChunk2 frame.
  - `/proc/net/netstat` deltas over one run: TCPTimeouts +5, TCPLossFailures +5, TCPZeroWindowDrop +8, RcvPruned +2, TCPRcvQDrop +2, RetransSegs +14.
- The crawl also let silentChunk2 slip into the stale window, which partly hid root cause 1. That explains the rare slow pass.
- Kernel: 6.6.150.1-1.azl3.

##### Experiment matrix (the target test)

| Harness | Product barge-in | Result |
|---|---|---|
| old (shrink after handshake) | blocking (await turn inline) | fail ~6/7, 30 s timeout |
| old | non-blocking | fail (TCP RTO crawl) |
| fixed (sizes set before handshake) | blocking | fail 6/6 (write/write deadlock) |
| fixed | non-blocking | **pass**, 1–2 s, with real backpressure observed (chunk#0 send blocked ~73 ms → ~1563 ms, barge-in landed at ~1054 ms) |

Both fixes are required. Either one alone fails.

##### Fix

### `CascadeProcessor.RunSessionAsync`

- New `BargeIn()`, which replaces the inline `await CancelCurrentTurnAsync` + `speech_started` send on the receive path:
  - It cancels the current turn's CTS and clears `CurrentTurnTask`/`CurrentTurnCts` immediately.
  - It never awaits on the receive loop.
  - It chains a `BargeInTail` task. The tail waits for the previous tail and for the cancelled turn to really stop, then disposes the CTS, logs, and only then sends `input_audio_buffer.speech_started`. That way no stale audio from the cut-off reply can follow it.
  - The tail never faults: OCE and `WebSocketException` are swallowed, and anything else is logged as a warning.
- A `speech_stopped`-spawned turn first does `await bargeInTail.WaitAsync(turnCt)`.
  - So it never overlaps the cut-off turn's `state.Messages` cleanup.
  - Its frames always follow `speech_started`.
- The nudge's "turn in flight" check also treats a pending `BargeInTail` as in flight.
- Teardown `finally` still awaits everything: `CancelCurrentTurnAsync("connection closing")`, then the barge-in tail, then `CancelNudge`, then `SessionManager.Detach`.
- The #236 invariant is unchanged: `SendTextAsync` still never passes `turnCt` to the real `SendAsync`.

### `BrowserSocketCancellationTests.OpenLoopbackWebSocketPairAsync`

- Set the 512-byte buffers on the listener socket before `Start()` (accepted sockets inherit them) and on the client before `ConnectAsync`, so the advertised window is small from the SYN onward.
- The test's own expectations are unchanged.

##### #236 guard check

Mutation: passing `turnCt` into the real `socket.SendAsync`. The target test then fails with Expected `Open` / Actual `Aborted`, so the guard still catches the #236 regression. The mutation was reverted.

##### ContextMonitor lifecycle (re-verified)

- Normal close: the teardown `finally` always reaches `_sessionManager?.Detach(...)`, because both preceding awaits swallow exceptions. Grace/idle sweep → `EndSessionLocked` → `_contextMonitors.Remove`.
  - Test: `RunSessionAsync_SocketCloseDetachesCascadeSession_AndGraceSweepRemovesItsContextMonitor`.
- Resume: the provisional session ends via `EndSessionLocked(..., "replaced by resume")`, which removes its monitor immediately. The prior session's monitor is released by detach + sweep.
  - Test: `RunSessionAsync_ResumeAcceptedMidConversation_...`, which asserts both.
- Supersede: the socket is swapped in place on the same record, with no second monitor. A stale socket's later `Detach` is a no-op (socket-identity check).
  - Test: `SessionManagerTests.TryResume_WhenPriorSocketStillAttached_...`.
- Filtered run `SessionManager|ContextMonitor|Cascade`: **112/112 passed**.

##### Results (real `dotnet test` output, SDK 11.0.100-rc.1)

- Target test, 5 consecutive runs: **5/5 passed** (1–2 s each). This was repeated on three separate occasions, all green.
- Full `Backend.Tests`: **745 passed, 0 failed, 0 skipped** (Total 745). It was run repeatedly, including after the final edit.
- Cascade conformance (`--filter FullyQualifiedName~Scenarios.Cascade`):
  - **dotnet leg: 18/18 passed**. The CI-scoped `Dotnet=ready&Category!=Browser` subset is 15/15.
  - **python leg: 18/18 passed**.
    - This needed a repo-root `.venv`: Python 3.12 via `uv`, with `app/backend/requirements.txt` and `tests/conformance/requirements-harness.txt` installed.
    - It also needed the frontend built into `app/backend/static` (`npm ci && VITE_AUTH_MODE=Development npm run build`).
    - Both are gitignored local artifacts.

## Decision: PR 290 Rick review fixes (echo semantics, nudge turn, cleanup)

**By:** Summer + Unity. **Addresses:** Rick's REQUEST CHANGES on head `507e2c1`, items 1-5. Item 6 untouched.

##### Echo suppression semantic (identical in Python and C#)
- Drop mic audio only during **estimated playback** (`len(pcm)/(24000*2)` from TTS arrival) **plus an acoustic tail of `min(audio.echo_cooldown_seconds, 0.3s)`** (300ms from decisions.md, 50-200ms speaker-to-mic latency). Default config 1.5 -> 0.3s tail.
- After the deadline, guest audio is accepted immediately (matches realtime #187/#190 observable behaviour).
- **Suppressed frames are dropped, not buffered** (`_TurnDetector.feed` / `TurnDetector.Feed` return before appending), so echo can never reach the next STT upload. Mid-utterance (`speaking`) frames are never dropped.
- **Zero cooldown = suppression disabled entirely** (no playback window, no tail) in BOTH backends. Chosen because it matches C#'s ff35f9b, keeps the existing barge-in tests/headset escape hatch, and is a single clear rule. Python `_speak` now skips arming when the tail is 0 (previously armed unconditionally). Docs no longer say "unconditional".
- Python: `_echo_tail_seconds()`; C#: `CascadeProcessor.EchoTailSeconds()` (ctor applies it).

##### Nudge as a real turn
- C# `ScheduleNudge`: when the nudge fires it hands NudgeTask/NudgeCts to `CurrentTurnTask/CurrentTurnCts` (under a new `turnRegistryLock`), so `BargeIn` and teardown cancel + await it. `speech_started` handling now calls `CancelNudge` *before* `BargeIn` (pending nudge cancelled under the lock; fired nudge is handled by BargeIn). Teardown calls `CancelNudge` first, then cancels/awaits the current turn and tail. 507e2c1's BargeIn/#236/backpressure logic is unchanged apart from taking the lock.
- Python: the firing nudge task sets `state.current_turn_task = asyncio.current_task()` and clears `nudge_task`, so `_cancel_current_turn` (barge-in + teardown) cancels and awaits it.

##### Deleted / changed
- Deleted `SessionManager.CreateContextMonitor/RemoveContextMonitor` and their test; fixed stale docs in SessionManager.cs/ContextMonitor.cs.
- Removed the blanket 1.8s `EchoCooldownClearDelay` from `CascadeScenarioHelpers`; helper now waits only a 400ms `AcousticTailClearDelay` (300ms tail + jitter) by default, with `waitOutAcousticTail:false` for rows probing the window. Replaced the echo row with one that sends the echo *during* playback (1.5s clip) and a row proving a reply ~200ms after playback+tail is answered.
- Docs: persona-architecture.md 7.1 (table, "mirror exactly", "unconditional", zero-cooldown, barge-in ordering) and dotnet_mapping.md updated; config.yaml comment on `echo_cooldown_seconds`.

##### Tests added
Python: buffer-drop, reply-after-tail, tail cap/zero, `_speak` zero-cooldown never arms, nudge registered as current turn. C#: TurnDetector buffer-drop + after-deadline, `EchoTailSeconds` theory, zero-cooldown processor test, barge-in during nudge mid-tool-round (asserts speech_started only after the nudge tool exits, no orphaned tool_calls; verified it fails if the nudge hand-over is removed).

##### Results
- `dotnet test app/backend-dotnet/tests/Backend.Tests`: 752 passed, 0 failed.
- `pytest app/backend/tests`: 1851 passed, 4 failed (pre-existing `test_entra_scripts_runtime.py`, pwsh), 7 skipped.
- Cascade conformance (`--filter Cascade`, 20 rows incl. 2 new): green 3/3 with `CONFORMANCE_BACKEND=dotnet` and 3/3 with `python`. Also full conformance on dotnet minus `Browser` scenarios: 907 passed, 6 skipped, 0 failed (Browser rows need a built frontend/Playwright, unavailable in this sandbox).
- Mutation A (turnCt into socket.SendAsync): FAILS (socket Aborted). Mutation B (inline await CancelCurrentTurn + inline speech_started): FAILS (30s timeout/cancel). Both reverted.

##### For Rick / coordinator
- Behaviour change: barge-in during *estimated playback* is not possible server-side (same as realtime, where mic is dropped while `ai_speaking`); set `echo_cooldown_seconds: 0` to disable. Existing C# barge-in tests use 0.
- The 300ms tail is a hard drop after every turn, so a conformance guest turn sent <300ms after the greeting is swallowed; the helper waits 400ms for that reason.
