# Summer — PR #290 / #126 cascade regression: final, evidence-backed fix

Supersedes the earlier "echo-cooldown-only" write-up. That earlier fix (only arm echo
suppression when `echoCooldownSeconds > 0`, plus `SessionManager.Detach` on cascade close) is still
correct and still in place (commit `ff35f9b`). But it only made barge-in actually *fire* during TTS.
It did not cause the hang, and it did not fix it.

## Symptom

At `ff35f9b`, `BrowserSocketCancellationTests.RunSessionAsync_BargeInDuringABackpressuredTtsWrite_DoesNotAbortTheBrowserSocket`
timed out in `DrainUntilAsync`. Locally it failed 6 of 7 runs and passed once, slowly (~10 s).
The same test on `origin/dev` failed 4 of 4 here, so the problem predates the #126 work.

## Root cause 1 (product): barge-in deadlocked the browser receive loop

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

## Root cause 2 (test harness): post-handshake buffer shrink caused a TCP RTO crawl

- `OpenLoopbackWebSocketPairAsync` set `SO_RCVBUF`/`SO_SNDBUF = 512` *after* the TCP handshake.
- The window already advertised for the default buffer (tcp_rmem default 131072) can't be retracted.
  - So the receiver dropped data the sender was entitled to send.
  - The sender then crawled through exponential RTO backoff, delivering about 2.3 KB per timeout.
- Evidence:
  - `/proc/net/tcp` for the server socket: tx queue about 27–31 KB, draining about 2304 B every few seconds; rx queue 12855, which is the unread silentChunk2 frame.
  - `/proc/net/netstat` deltas over one run: TCPTimeouts +5, TCPLossFailures +5, TCPZeroWindowDrop +8, RcvPruned +2, TCPRcvQDrop +2, RetransSegs +14.
- The crawl also let silentChunk2 slip into the stale window, which partly hid root cause 1. That explains the rare slow pass.
- Kernel: 6.6.150.1-1.azl3.

## Experiment matrix (the target test)

| Harness | Product barge-in | Result |
|---|---|---|
| old (shrink after handshake) | blocking (await turn inline) | fail ~6/7, 30 s timeout |
| old | non-blocking | fail (TCP RTO crawl) |
| fixed (sizes set before handshake) | blocking | fail 6/6 (write/write deadlock) |
| fixed | non-blocking | **pass**, 1–2 s, with real backpressure observed (chunk#0 send blocked ~73 ms → ~1563 ms, barge-in landed at ~1054 ms) |

Both fixes are required. Either one alone fails.

## Fix

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

## #236 guard check

Mutation: passing `turnCt` into the real `socket.SendAsync`. The target test then fails with Expected `Open` / Actual `Aborted`, so the guard still catches the #236 regression. The mutation was reverted.

## ContextMonitor lifecycle (re-verified)

- Normal close: the teardown `finally` always reaches `_sessionManager?.Detach(...)`, because both preceding awaits swallow exceptions. Grace/idle sweep → `EndSessionLocked` → `_contextMonitors.Remove`.
  - Test: `RunSessionAsync_SocketCloseDetachesCascadeSession_AndGraceSweepRemovesItsContextMonitor`.
- Resume: the provisional session ends via `EndSessionLocked(..., "replaced by resume")`, which removes its monitor immediately. The prior session's monitor is released by detach + sweep.
  - Test: `RunSessionAsync_ResumeAcceptedMidConversation_...`, which asserts both.
- Supersede: the socket is swapped in place on the same record, with no second monitor. A stale socket's later `Detach` is a no-op (socket-identity check).
  - Test: `SessionManagerTests.TryResume_WhenPriorSocketStillAttached_...`.
- Filtered run `SessionManager|ContextMonitor|Cascade`: **112/112 passed**.

## Results (real `dotnet test` output, SDK 11.0.100-rc.1)

- Target test, 5 consecutive runs: **5/5 passed** (1–2 s each). This was repeated on three separate occasions, all green.
- Full `Backend.Tests`: **745 passed, 0 failed, 0 skipped** (Total 745). It was run repeatedly, including after the final edit.
- Cascade conformance (`--filter FullyQualifiedName~Scenarios.Cascade`):
  - **dotnet leg: 18/18 passed**. The CI-scoped `Dotnet=ready&Category!=Browser` subset is 15/15.
  - **python leg: 18/18 passed**.
    - This needed a repo-root `.venv`: Python 3.12 via `uv`, with `app/backend/requirements.txt` and `tests/conformance/requirements-harness.txt` installed.
    - It also needed the frontend built into `app/backend/static` (`npm ci && VITE_AUTH_MODE=Development npm run build`).
    - Both are gitignored local artifacts.
