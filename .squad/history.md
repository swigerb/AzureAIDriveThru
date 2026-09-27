# Project Context

- **Owner:** {user name}
- **Project:** {project description}
- **Stack:** {languages, frameworks, tools}
- **Created:** {timestamp}

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-09-27 — Summer, #74 (P2-5) persona binding

- A fresh `git worktree add` has neither a Python `.venv` nor a built frontend
  (`app/backend/static/`) — **both** are required before `dotnet test Conformance.slnx` can even
  launch the Python backend subprocess, and the failure symptoms don't say so directly: a missing
  `.venv` shows up as `System.ComponentModel.Win32Exception` trying to spawn
  `.venv\Scripts\python.exe` (~474/589 fail), a missing `static/index.html` shows up as
  `System.InvalidOperationException` from `PythonBackendLauncher` (~463/589 fail). Fix each fresh
  worktree once with `python -m venv .venv` + `pip install -r app/backend/requirements.txt`
  (real venv, never a junction/symlink) and `npm install && npm run build` in `app/frontend`.
  Neither step touches tracked files (both outputs are gitignored) — pure one-time local setup,
  not a code change.
- The "default context" delegation pattern (every persona-specific lookup falls back exactly to
  the pre-existing module-level/env-driven behavior when no persona catalog is configured) let
  the whole persona-binding feature (#74) land with zero changes to the existing test suite's
  pass/fail set — the entire risk of a large cross-cutting change (order_state/menu_utils/tools/
  rtmt/app.py) can be pushed into new, additive code paths only exercised when a persona catalog
  is actually configured, rather than forking existing logic.
- `BackendUnderTest.IsPython` exists in the C# conformance harness (`BackendUnderTest.cs`) as a
  gating property for backend-specific scenarios, but has zero actual test-file usages anywhere
  in the suite — only a hypothetical pattern documented in the README. Before relying on it for a
  Python-only feature's conformance rows, expect to have to build the first real usage from
  scratch, not just follow an existing example.
