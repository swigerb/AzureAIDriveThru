# Summer — PR #290 cascade regression follow-up

## Root cause

Two .NET cascade regressions were confirmed in the merged `SessionManager.CreateSession` path work:

1. **Zero echo-cooldown did not actually disable cascade echo suppression.**
   `CascadeProcessor.SpeakAsync()` always armed `TurnDetector.StartEchoCooldown()` for
   `tts_duration + echoCooldownSeconds`. That meant an explicit `echoCooldownSeconds: 0` still
   suppressed barge-in for the entire TTS playback window instead of restoring the pre-#126
   behavior the existing cancellation test expects.

2. **Cascade sessions were registered in `SessionManager`, but never detached on socket close.**
   After the merge resolution moved cascade onto `SessionManager.CreateSession()`, the session got
   a `ContextMonitor`, resume metadata, and sweep participation — but `RunSessionAsync()` still
   ended with only turn/nudge cleanup. Without a matching `SessionManager.Detach(...)` in `finally`,
   closed cascade sockets could leave the session record attached indefinitely, so the
   `ContextMonitor` would not be released by the normal grace/idle end-of-session path.

## Fix applied

- In `app/backend-dotnet/src/Backend/Sessions/CascadeProcessor.cs`:
  - only arm cascade echo suppression when `_echoCooldownSeconds > 0`;
  - detach the cascade session from `SessionManager` in `RunSessionAsync()`'s `finally` block.
- In tests:
  - added coverage that a resumed cascade session removes the provisional connection's monitor and
    that the resumed/original monitor is released by the grace sweep;
  - added coverage that an ordinary cascade socket close now detaches and is cleaned up by
    `SweepDetached()`;
  - updated stale standalone-ContextMonitor comments to match the new post-merge reality.

## ContextMonitor lifecycle verification

Verified from code and tests:

- **Normal cascade close:** `CreateSession()` creates the monitor; `RunSessionAsync(...finally)` now
  calls `SessionManager.Detach(...)`; later `SweepDetached()`/`EndSessionLocked()` removes the
  monitor.
- **Resume:** `TryResume(...)` ends the provisional session via
  `EndSessionLocked(provisionalSessionId, "replaced by resume")`, which removes the provisional
  monitor immediately. The original session record/monitor is retained and later removed by the same
  detach + sweep path.
- **Supersede:** `TryResume(...)` swaps the attached socket in-place on the original session record;
  it does not create a second monitor for that original session. The stale socket's later close is a
  no-op because `Detach(...)` checks socket identity.
- **Idle/grace sweep:** `SessionManager.CloseIdleSessionsAsync()` and `SweepDetached()` both route
  through `EndSessionLocked()`, which removes `_contextMonitors[sessionId]`.

## Test results

### Passing runs

- Focused .NET cascade/session coverage:
  - `Backend.Tests.Cascade.CascadeProcessorTests`
  - `Backend.Tests.Cascade.TurnDetectorTests`
  - `Backend.Tests.Sessions.SessionManagerTests`
  - **68/68 passed**
- Cascade conformance, **dotnet leg** (`CONFORMANCE_BACKEND=dotnet`, filter `Scenarios.Cascade`):
  - **18/18 passed**

### Environment blockers / remaining failures observed in this workspace

- Full `Backend.Tests` run:
  - **744 passed, 1 failed**
  - failing test: `Backend.Tests.Cascade.BrowserSocketCancellationTests.RunSessionAsync_BargeInDuringABackpressuredTtsWrite_DoesNotAbortTheBrowserSocket`
  - local failure mode here is a **30s timeout** in `DrainUntilAsync(...)` waiting for the loopback
    guest socket, not the coordinator-reported transcript mismatch.
- 5x repeat of that targeted test in this workspace:
  - **5/5 failed** (`exit=1` each run)
- Cascade conformance, **python leg** (`CONFORMANCE_BACKEND=python`, filter `Scenarios.Cascade`):
  - fixture startup failed for all 18 rows because the required interpreter was absent:
    `Python venv interpreter not found at '/workspace/ddt-290-fix/repo/.venv/bin/python'`.
