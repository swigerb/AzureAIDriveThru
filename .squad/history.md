# Project Context

- **Owner:** {user name}
- **Project:** {project description}
- **Stack:** {languages, frameworks, tools}
- **Created:** {timestamp}

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **2026-09-27, Birdperson (#76 P2-7 groundwork).** The conformance harness's persona
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
