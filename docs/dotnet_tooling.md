# .NET tooling inventory (`tools/dotnet`)

This document tracks every Python script and notebook outside `app/backend` (plus every `.ps1`/
`.sh` wrapper that shells out to Python), per issue #16 (S6, epic #6's "one contract, two
backends" decision extended to repo tooling). It exists for the same reason
`docs/dotnet_mapping.md` exists for the application backend: so a port has an obvious place to
record what moved, what didn't, and why, instead of that history living only in commit messages.

Per epic #6 and the issue #16 P1 update (ADR-001), this is a multi-wave effort. **This PR is wave
1**: the inventory below, a scaffold for C# tooling (`tools/dotnet/`), and ONE representative,
low-risk port end-to-end (`update_menu_sizes.py`) with an output-parity test against its Python
twin. Every other script and notebook in the inventory stays Python-only until a later PR explicitly
ports it (tracked in the Squad's wave plan). **The Python versions are not removed or modified by
this PR**, and azd hooks / CI keep running the Python implementations by default.

## Inventory: Python scripts and notebooks outside `app/backend`

| File | Purpose | Inputs / outputs | azd hook / CI usage | Azure dependency | Proposed C# shape |
| --- | --- | --- | --- | --- | --- |
| `scripts/update_menu_sizes.py` | Adds Mini/Small/Medium/Large/RT 44 size+price variants (parsed from the production POS export) to a handful of drink/slush/shake/blast items in the UI menu file. | In: `personas/sonic/menu/source/sonic-menu-items.json` (read-only), `personas/sonic/menu/menuItems.json`. Out: rewrites `menuItems.json` in place. | Manual dev tool only; not an azd hook, not referenced by CI. | None (pure JSON transform, no network/SDK calls). | **Ported this PR** -- `tools/dotnet/src/UpdateMenuSizes` (small console tool project). See "This PR's port" below. |
| `scripts/extract_production_items.py` | Produces a stdout report of every item in the production POS export grouped by category, then a gap analysis (items in the UI menu but not production, and vice versa) against `menuItems.json`. Shares its category-walking logic with `sonic_menu_ingestion_search.ipynb` ("using the SAME logic" per its own docstring). | In: same two files as `update_menu_sizes.py` (read-only). Out: stdout report only, no file written. | Manual dev/report tool only; not an azd hook, not referenced by CI. | None (pure JSON read + report, no network/SDK calls). | Deferred to a later wave -- a second candidate as easy as `update_menu_sizes.py` (same no-Azure, pure-JSON shape), proposed as a small console tool project (`tools/dotnet/src/ExtractProductionItems`) alongside it. |
| `scripts/benchmark_reasoning.py` | Runs scripted guest utterances against a **live** Azure OpenAI realtime deployment to benchmark `reasoning.effort`/`parallel_tool_calls` latency and tool-call correctness. | In: live realtime deployment (via `azd env get-values`/env vars), optional `--tools real` hits Azure AI Search too. Out: JSON/JSONL results file (`--out`/`--resume`). | Manual dev benchmarking tool only; not an azd hook, not referenced by CI. | **Yes** -- requires a live Azure OpenAI realtime deployment; explicitly out of scope for this issue ("NOT ... anything that calls Azure"). | Deferred indefinitely (or until a C# realtime client exists to drive it) -- excluded from the "first port" candidate set for this reason. |
| `scripts/generate_apology_clips.py` | Records one pre-recorded "rate limited twice in a row" apology audio clip per UI language, using a live realtime model, with Whisper transcription verification. | In: live Azure OpenAI realtime + Whisper. Out: `personas/<persona-id>/assets/audio/apology-<lang>.wav`. | Manual, occasional dev tool (re-run only when a persona's default voice changes); not an azd hook, not referenced by CI. | **Yes** -- live realtime model + Whisper transcription. | Deferred indefinitely, same reason as `benchmark_reasoning.py`. |
| `scripts/generate_demo_guest_voice.py` | Generates scripted guest-voice MP3 clips for persona demo packs via Azure Speech, with `ffmpeg`/`subprocess` post-processing (silence trim, crossfade). | In: Azure Speech synthesis, local `ffmpeg`. Out: MP3 clip files under a persona's demo assets. | Manual dev tool only; not an azd hook, not referenced by CI. | **Yes** -- Azure Speech voice synthesis. | Deferred indefinitely, same reason as the other audio-generation tools. |
| `scripts/smoke_realtime.py` | Post-deploy smoke check: builds the exact `session.update` payloads the middle tier sends and fires them at a **live** Azure OpenAI realtime deployment, failing if tools/instructions/transcription don't come back correctly. | In: live Azure OpenAI realtime deployment (via flags/env/`azd env get-values`). Out: exit code + stdout diagnostics (no file). | **azd `postdeploy` hook** (via `scripts/smoke_realtime.ps1`/`.sh`). Not referenced by CI. | **Yes** -- the entire point of the script is to probe a live deployment. | Deferred indefinitely -- explicitly excluded ("NOT setup_search_index or anything that calls Azure"); the azd hook keeps running the Python version by default (`TOOLING_IMPL` note below). |
| `scripts/e2e_order_resume.py` | Real-browser (Playwright/headless Chromium or Edge) end-to-end check of order-resume behavior (reconnect, tap-to-continue, page reload, idle close) against the built frontend and the real middle tier, with a fake realtime upstream. No Azure calls. | In: a built frontend (`app/backend/static`), a running middle tier, Playwright-driven browser. Out: pass/fail + console diagnostics (no file). | Not part of the default test run (needs a browser + Playwright installed); not an azd hook, not referenced by CI. | None directly, but needs a real browser engine and a built frontend bundle -- too heavy/complex to be the "representative, low-risk" first port. | Deferred -- a plausible future candidate once a C# equivalent of Playwright-driven browser automation is justified for a later wave (not attempted here). |
| `scripts/menu_ingestion_search_json.ipynb` | Generic (persona-agnostic) notebook walkthrough: configure Azure OpenAI + Azure AI Search, prepare menu JSON, and upload it to a search index for hybrid semantic search. | In: a raw menu JSON export. Out: documents upserted into a live Azure AI Search index. | Manual, interactive notebook only; not an azd hook, not referenced by CI (the production path is `app/backend/setup_search_index.py`, which already lives in, and is out of scope per, `app/backend`). | **Yes** -- Azure OpenAI + Azure AI Search throughout. | Deferred indefinitely -- notebooks are explicitly the kind of Azure-dependent, interactive tooling this issue's "no Azure calls" constraint excludes from a first port; also the least "representative, low-risk" shape (interactive exploration, not a deterministic CLI tool). |
| `scripts/sonic_menu_ingestion_search.ipynb` | Same pipeline as `menu_ingestion_search_json.ipynb`, specialized to ingest one persona's menu (`personas/<PERSONA_ID>/`) by id. | Same as above, scoped to one persona. | Manual, interactive notebook only; not an azd hook, not referenced by CI. | **Yes** -- Azure OpenAI + Azure AI Search throughout. | Deferred indefinitely, same reason as `menu_ingestion_search_json.ipynb`. |

Note: `app/backend/setup_search_index.py` itself is inside `app/backend` and is therefore **out of
this inventory's scope** (the issue is scoped to Python outside `app/backend`); only its `scripts/`
launcher wrappers are in scope, below.

## Inventory: `.ps1`/`.sh` wrappers that shell out to Python

| File(s) | Wraps | azd hook | Proposed C# shape |
| --- | --- | --- | --- |
| `scripts/setup_search_index.ps1`, `scripts/setup_search_index.sh` | `app/backend/setup_search_index.py` (builds/refreshes the Azure AI Search index from the production menu export) | **azd `postprovision` hook** (after `postprovision_auth`/`write_env`) | Not ported (wraps an Azure-dependent, in-scope-elsewhere script); stays Python. A C# equivalent is only relevant once/if `app/backend/setup_search_index.py` itself is ported, which is outside this issue's `app/backend`-excluded scope. |
| `scripts/smoke_realtime.ps1`, `scripts/smoke_realtime.sh` | `scripts/smoke_realtime.py` (realtime session smoke check; never fails the deployment, warns only) | **azd `postdeploy` hook** | Not ported (wraps an explicitly-excluded, Azure-dependent script); stays Python. |
| `scripts/start.ps1`, `scripts/start.sh` | `app/backend/app.py` directly (runs the Python backend itself via gunicorn, the application entry point) | Not an azd hook; local dev convenience only. | Out of scope -- this is the application, not "tooling" (and a C# backend entry point is `app/backend-dotnet`'s own, separately-tracked concern, not this issue's). |
| `scripts/load_python_env.ps1`, `scripts/load_python_env.sh` | Nothing Python-specific to port -- it bootstraps the Python `.venv` itself (creates it, installs `app/backend/requirements.txt`) so the OTHER scripts above have an interpreter to run. | Sourced by `setup_search_index.ps1`/`.sh` before they invoke Python. | Out of scope -- there is no Python *logic* here to port; it is the venv bootstrap `update_menu_sizes.py` et al. depend on existing at all. |

`postprovision_auth.ps1`/`.sh`, `write_env.ps1`/`.sh`, and `install_prerequisites.ps1`/`.sh` were
also checked and confirmed to shell out only to `az`/`azd` CLIs, never Python -- they are not
included above because they have no Python twin to track.

CI (`.github/workflows/*`) was grepped for every file above; **none of them are referenced by any
GitHub Actions workflow**. Only `azure.yaml`'s `postprovision`/`postdeploy` hooks reference
`setup_search_index`/`smoke_realtime` (plus `postprovision_auth`/`write_env`, which have no Python
twin). This means none of this inventory currently gates a PR; a port's own tests are the only CI
signal for it until/unless a later wave adds real wiring.

## This PR's port: `update_menu_sizes.py` -> `tools/dotnet/src/UpdateMenuSizes`

Chosen because it is the smallest, most deterministic, least risky candidate in the inventory: a
pure JSON-to-JSON transform with **no** Azure/network/subprocess dependency, a small fixed set of
hardcoded inputs/outputs already checked into the repo, and no moving parts that would make an
output-parity test flaky. Every other script either needs a live Azure OpenAI/Speech/Search
dependency (`benchmark_reasoning.py`, `generate_apology_clips.py`, `generate_demo_guest_voice.py`,
`smoke_realtime.py`, both notebooks) or a real browser + built frontend
(`e2e_order_resume.py`) -- all explicitly out of scope for a first port per the issue.

### Scaffold shape and why

`tools/dotnet/` is a **third, independent** .NET solution alongside `app/backend-dotnet/` and
`tests/conformance/` -- it does not reference either, and neither references it. It reuses the
one repo-root `global.json` (no new SDK pin needed; `global.json` lookup walks up from any project
below the repo root) and follows the same conventions as the other two solutions:

* `tools/dotnet/Directory.Build.props` / `Directory.Packages.props` -- same `net11.0` target,
  nullable/implicit usings, `TreatWarningsAsErrors`, `RestorePackagesWithLockFile`, and central
  package management as `app/backend-dotnet`'s. No `NuGet.Config` (restores go through the
  machine-configured proxy, same as the other two solutions).
* `tools/dotnet/Tooling.slnx` -- hand-authored XML (same `/src/`+`/tests/` folder shape as
  `Backend.slnx`), not generated via `dotnet sln add` (that command has a known bug against the
  `.slnx` format on this SDK build; `Backend.slnx`/`Conformance.slnx` are also hand-authored for
  this reason).
* One **project per tool** under `tools/dotnet/src/<ToolName>/` (`Microsoft.NET.Sdk`, a plain
  console app, not `.Web`), plus a matching `tools/dotnet/tests/<ToolName>.Tests/` xUnit v3
  project (`xunit.v3.mtp-off`, matching `Backend.Tests`/`Conformance.Tests` -- NOT
  `xunit.v3.mtp-v2`, whose first-run scaffolding would rewrite the one shared repo-root
  `global.json`'s test-runner setting).

**Why a tool project instead of a true ".NET file-based app" (`dotnet run script.cs`, no
`.csproj`)**, which the issue text also allows: a file-based app has no natural home for a
companion xUnit test project to reference its logic from (xUnit needs an assembly to
`ProjectReference`), and this issue requires an output-parity test, not just a runnable script. A
thin `Program.cs` (CLI argument parsing only) over a testable `MenuSizeUpdater` static class gets
the same "one small, obviously-scoped file you can `dotnet run`" ergonomics as a file-based app
(`dotnet run --project tools/dotnet/src/UpdateMenuSizes` with no arguments behaves exactly like
`python scripts/update_menu_sizes.py`, same default input/output paths) while staying unit-testable
and consistent with how the other two existing solutions structure a src/tests split. Later waves
can revisit this choice per tool if a genuinely trivial, dependency-free, one-shot script doesn't
warrant a test project at all.

### The port itself

`tools/dotnet/src/UpdateMenuSizes/MenuSizeUpdater.cs` is a deliberately **faithful**, not
"improved," port of `update_menu_sizes.py`'s `extract_size`/`find_sizes_for_product`/
`update_menu` functions, including one notable wart preserved on purpose: the Python source's
`SIZE_PREFIXES` has a raw-string entry, `r"RT 44\u00ae "`, which is the 12 *literal* characters
`RT 44\u00ae ` (a backslash followed by `u`, `0`, `0`, `a`, `e`), not the registered-trademark
symbol -- dead code that can never match a real production `displayName`. The very next entry,
written as a normal string with the actual `®` character, is the one that does real work. This
port reproduces both entries exactly rather than quietly dropping the dead one, because a port's
job is to match its twin's *observable behavior*, including its harmless bugs -- fixing it would be
a silent, undocumented behavior decision smuggled into what should be a mechanical port. (Any real
fix belongs in the Python script first, with this port following.)

The one load-bearing difference from a literal transliteration: where the Python script hardcodes
its two file paths via `Path(__file__).resolve().parent.parent / ...`, the C# tool accepts optional
`--production <path>` and `--menu <path>` arguments (falling back to the identical repo-relative
defaults when omitted), purely so tests can point it at throwaway copies instead of the real,
checked-in `personas/sonic/menu/**` files.

### Output-parity test (and the mutation check)

`tools/dotnet/tests/UpdateMenuSizes.Tests/PythonParityTests.cs` is the issue's acceptance bar made
concrete: it copies the REAL `personas/sonic/menu/source/sonic-menu-items.json` and
`personas/sonic/menu/menuItems.json` into two private temp directories, runs the actual
`scripts/update_menu_sizes.py` as a genuine subprocess against one copy, runs the C# port
in-process against the other, then asserts the two resulting `menuItems.json` files are
**structurally** identical JSON (property order within an object is not significant; array order
is, since these documents are ordered lists of categories/items/sizes). It never touches the real,
checked-in fixture files. It resolves a Python interpreter the same way
`tests/conformance`'s `FakeEntraIssuerPyJwtValidationTests` does (repo-root `.venv` first, then a
bare `python3`/`python` on PATH), skips locally if none is found, and fails (not skips) in CI
(`GITHUB_ACTIONS=true`) -- update_menu_sizes.py needs only the standard library, so any Python 3
interpreter qualifies.

`tools/dotnet/tests/UpdateMenuSizes.Tests/MenuSizeUpdaterTests.cs` unit-tests the individual
pieces (`ExtractSize`'s prefix matching including the dead-prefix case above, the Cherry
Limeade/Ocean Water slush/diet exclusions, first-match-per-size-key-wins, idempotency, and that
`UpdateMenu` only replaces a changed item's `sizes` array, leaving every other field untouched)
against small synthetic fixtures, independent of the real menu data.

**Mutation check performed**: temporarily removed the Cherry Limeade "slush" exclusion from
`FindSizesForProduct` and reran the suite -- `FindSizesForProduct_ExcludesCherryLimeadeSlushAndDietVariants`
failed as expected (it picked up an unrelated "...Slush" product's price under a size key the
legitimate product didn't already have). Restored the guard and reran; all tests passed again.

### CI wiring

A new, narrowly-scoped workflow, `.github/workflows/dotnet-tooling.yml`, builds and tests
**only** `tools/dotnet/Tooling.slnx`, triggered only on changes under `tools/dotnet/**` or the
workflow file itself. This is new wiring (none of this PR's new files were previously built/tested
by any existing workflow), added as a dedicated file rather than a new job inside the existing
`conformance.yml` (which already has its own `dotnet-tests` job for `app/backend-dotnet`) so this
PR does not need to touch that file at all -- `conformance.yml` is shared, frequently-edited
ground for other in-flight work, and `tools/dotnet` is a fully independent solution with nothing to
gain from sharing a workflow file with it.

## `TOOLING_IMPL` (future, not wired in this PR)

Issue #16's body describes a future `TOOLING_IMPL=python|dotnet` environment variable so azd hooks
could eventually choose which implementation to run, defaulting to `python`. **This PR does not
introduce that variable or any hook wiring for it** -- per this PR's explicit scope, azd hooks and
CI defaults stay on the Python implementations unconditionally. A later wave, once more of the
inventory above is ported, is the right place to introduce `TOOLING_IMPL` for real.

## What's next

Later batches (see the squad's wave plan) continue porting the remaining inventory above,
roughly in this order of risk: `extract_production_items.py` next (same no-Azure, pure-JSON shape
as this PR's port), then `e2e_order_resume.py` once a C# browser-automation story is justified,
then the Azure-dependent tools and notebooks last (and only once there is a reason to run them from
C# rather than Python, since they need a live Azure OpenAI/Search/Speech dependency regardless of
implementation language).
