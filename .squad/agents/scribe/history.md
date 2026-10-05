# Project Context

- **Project:** SonicAIDriveThru
- **Created:** 2026-03-19

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-03-19  
📌 Sonic Rebrand project completed 2026-03-19T04-06:
  - Rick: Scope analysis (~100+ refs identified)
  - Morty: Frontend UI overhaul (13 tests pass)
  - Summer: Backend rebrand (69 tests pass)
  - Birdperson: Verification tests (12 tests created, all pass)

## Learnings

- **Sonic Rebrand**: Frontend theme (CSS custom properties, Nunito Sans, Sonic colors), backend system prompts rewritten as carhop persona, menu data replaced, verification tests cover all source files for forbidden terms.
- **Menu category coupling**: tools.py MENU_CATEGORY_MAP loads from frontend menuItems.json at init; ALLOWED/BLOCKED must include both JSON names and keyword-inferred fallbacks.
- **Decision documentation**: Merged 4 inbox decisions into decisions.md; cleaned up inbox folder.

## 2026-10-05: C# 100% parity program wrap-up

- Recorded durable decisions from the completed C# parity program into `.squad/decisions.md`: Squad on ACA operating model, conformance floor measurement rules, cascade echo-suppression and nudge semantics, the barge-in receive-loop deadlock rule, #17 go-live gating defaults, and the architecture PNG Windows-rendering note.
- Logged the wrap-up session under `.squad/log/`.
- Did not touch governance-locked paths; only appended to this history file and wrote to decisions.md / log per the coordinator's brief.
