# Decision: PR 290 Rick review fixes (echo semantics, nudge turn, cleanup)

**By:** Summer + Unity. **Addresses:** Rick's REQUEST CHANGES on head `507e2c1`, items 1-5. Item 6 untouched.

## Echo suppression semantic (identical in Python and C#)
- Drop mic audio only during **estimated playback** (`len(pcm)/(24000*2)` from TTS arrival) **plus an acoustic tail of `min(audio.echo_cooldown_seconds, 0.3s)`** (300ms from decisions.md, 50-200ms speaker-to-mic latency). Default config 1.5 -> 0.3s tail.
- After the deadline, guest audio is accepted immediately (matches realtime #187/#190 observable behaviour).
- **Suppressed frames are dropped, not buffered** (`_TurnDetector.feed` / `TurnDetector.Feed` return before appending), so echo can never reach the next STT upload. Mid-utterance (`speaking`) frames are never dropped.
- **Zero cooldown = suppression disabled entirely** (no playback window, no tail) in BOTH backends. Chosen because it matches C#'s ff35f9b, keeps the existing barge-in tests/headset escape hatch, and is a single clear rule. Python `_speak` now skips arming when the tail is 0 (previously armed unconditionally). Docs no longer say "unconditional".
- Python: `_echo_tail_seconds()`; C#: `CascadeProcessor.EchoTailSeconds()` (ctor applies it).

## Nudge as a real turn
- C# `ScheduleNudge`: when the nudge fires it hands NudgeTask/NudgeCts to `CurrentTurnTask/CurrentTurnCts` (under a new `turnRegistryLock`), so `BargeIn` and teardown cancel + await it. `speech_started` handling now calls `CancelNudge` *before* `BargeIn` (pending nudge cancelled under the lock; fired nudge is handled by BargeIn). Teardown calls `CancelNudge` first, then cancels/awaits the current turn and tail. 507e2c1's BargeIn/#236/backpressure logic is unchanged apart from taking the lock.
- Python: the firing nudge task sets `state.current_turn_task = asyncio.current_task()` and clears `nudge_task`, so `_cancel_current_turn` (barge-in + teardown) cancels and awaits it.

## Deleted / changed
- Deleted `SessionManager.CreateContextMonitor/RemoveContextMonitor` and their test; fixed stale docs in SessionManager.cs/ContextMonitor.cs.
- Removed the blanket 1.8s `EchoCooldownClearDelay` from `CascadeScenarioHelpers`; helper now waits only a 400ms `AcousticTailClearDelay` (300ms tail + jitter) by default, with `waitOutAcousticTail:false` for rows probing the window. Replaced the echo row with one that sends the echo *during* playback (1.5s clip) and a row proving a reply ~200ms after playback+tail is answered.
- Docs: persona-architecture.md 7.1 (table, "mirror exactly", "unconditional", zero-cooldown, barge-in ordering) and dotnet_mapping.md updated; config.yaml comment on `echo_cooldown_seconds`.

## Tests added
Python: buffer-drop, reply-after-tail, tail cap/zero, `_speak` zero-cooldown never arms, nudge registered as current turn. C#: TurnDetector buffer-drop + after-deadline, `EchoTailSeconds` theory, zero-cooldown processor test, barge-in during nudge mid-tool-round (asserts speech_started only after the nudge tool exits, no orphaned tool_calls; verified it fails if the nudge hand-over is removed).

## Results
- `dotnet test app/backend-dotnet/tests/Backend.Tests`: 752 passed, 0 failed.
- `pytest app/backend/tests`: 1851 passed, 4 failed (pre-existing `test_entra_scripts_runtime.py`, pwsh), 7 skipped.
- Cascade conformance (`--filter Cascade`, 20 rows incl. 2 new): green 3/3 with `CONFORMANCE_BACKEND=dotnet` and 3/3 with `python`. Also full conformance on dotnet minus `Browser` scenarios: 907 passed, 6 skipped, 0 failed (Browser rows need a built frontend/Playwright, unavailable in this sandbox).
- Mutation A (turnCt into socket.SendAsync): FAILS (socket Aborted). Mutation B (inline await CancelCurrentTurn + inline speech_started): FAILS (30s timeout/cancel). Both reverted.

## For Rick / coordinator
- Behaviour change: barge-in during *estimated playback* is not possible server-side (same as realtime, where mic is dropped while `ai_speaking`); set `echo_cooldown_seconds: 0` to disable. Existing C# barge-in tests use 0.
- The 300ms tail is a hard drop after every turn, so a conformance guest turn sent <300ms after the greeting is swallowed; the helper waits 400ms for that reason.
