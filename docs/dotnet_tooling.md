# .NET tooling inventory (`tools/dotnet`)

This document tracks every Python script and notebook outside `app/backend` (plus every `.ps1`/
`.sh` wrapper that shells out to Python), per issue #16 (S6, epic #6's "one contract, two
backends" decision extended to repo tooling). It exists for the same reason
`docs/dotnet_mapping.md` exists for the application backend: so a port has an obvious place to
record what moved, what didn't, and why, instead of that history living only in commit messages.

Per epic #6 and the issue #16 P1 update (ADR-001), this is a multi-wave effort. The original PR
(#224) was **wave 1**: the inventory below, a scaffold for C# tooling (`tools/dotnet/`), and ONE
representative, low-risk port end-to-end (`update_menu_sizes.py`) with an output-parity test against
its Python twin. **Batch 1** ported the inventory's own stated next no-Azure candidate,
`extract_production_items.py` -- see "Batch 1 port" below. **Batch 2** (this update), with no
no-Azure candidates left, instead proves out this doc's own design note for porting an
Azure-dependent tool without live Azure calls, for `app/backend/setup_search_index.py`'s
request-building half -- see "Batch 2 port" below. Every other script and notebook in the inventory
stays Python-only until a later batch/wave explicitly ports it (tracked in the Squad's wave plan; see
"What's next" for the remaining candidates' design). **The Python versions are not removed or
modified by this work**, and azd hooks / CI keep running the Python implementations by default.

## Inventory: Python scripts and notebooks outside `app/backend`

| File | Purpose | Inputs / outputs | azd hook / CI usage | Azure dependency | Proposed C# shape |
| --- | --- | --- | --- | --- | --- |
| `scripts/update_menu_sizes.py` | Adds Mini/Small/Medium/Large/RT 44 size+price variants (parsed from the production POS export) to a handful of drink/slush/shake/blast items in the UI menu file. | In: `personas/<id>/menu/source/<id>-menu-items.json` (read-only), `personas/<id>/menu/menuItems.json`. Out: rewrites `menuItems.json` in place. | Manual dev tool only; not an azd hook, not referenced by CI. | None (pure JSON transform, no network/SDK calls). | **Ported this PR** -- `tools/dotnet/src/UpdateMenuSizes` (small console tool project). See "This PR's port" below. |
| `scripts/extract_production_items.py` | Produces a stdout report of every item in the production POS export grouped by category, then a gap analysis (items in the UI menu but not production, and vice versa) against `menuItems.json`. Shares its category-walking logic with `sonic_menu_ingestion_search.ipynb` ("using the SAME logic" per its own docstring). | In: same two files as `update_menu_sizes.py` (read-only). Out: stdout report only, no file written. | Manual dev/report tool only; not an azd hook, not referenced by CI. | None (pure JSON read + report, no network/SDK calls). | **Ported in batch 1** -- `tools/dotnet/src/ExtractProductionItems` (small console tool project). See "Batch 1 port" below. |
| `scripts/benchmark_reasoning.py` | Runs scripted guest utterances against a **live** Azure OpenAI realtime deployment to benchmark `reasoning.effort`/`parallel_tool_calls` latency and tool-call correctness. | In: live realtime deployment (via `azd env get-values`/env vars), optional `--tools real` hits Azure AI Search too. Out: JSON/JSONL results file (`--out`/`--resume`). | Manual dev benchmarking tool only; not an azd hook, not referenced by CI. | **Yes** -- requires a live Azure OpenAI realtime deployment; explicitly out of scope for this issue ("NOT ... anything that calls Azure"). | Deferred indefinitely (or until a C# realtime client exists to drive it) -- excluded from the "first port" candidate set for this reason. |
| `scripts/generate_apology_clips.py` | Records one pre-recorded "rate limited twice in a row" apology audio clip per UI language, using a live realtime model, with Whisper transcription verification. | In: live Azure OpenAI realtime + Whisper. Out: `personas/<persona-id>/assets/audio/apology-<lang>.wav`. | Manual, occasional dev tool (re-run only when a persona's default voice changes); not an azd hook, not referenced by CI. | **Yes** -- live realtime model + Whisper transcription. | Deferred indefinitely, same reason as `benchmark_reasoning.py`. |
| `scripts/generate_demo_guest_voice.py` | Generates scripted guest-voice MP3 clips for persona demo packs via Azure Speech, with `ffmpeg`/`subprocess` post-processing (silence trim, crossfade). | In: Azure Speech synthesis, local `ffmpeg`. Out: MP3 clip files under a persona's demo assets. | Manual dev tool only; not an azd hook, not referenced by CI. | **Yes** -- Azure Speech voice synthesis. | Deferred indefinitely, same reason as the other audio-generation tools. |
| `scripts/smoke_realtime.py` | Post-deploy smoke check: builds the exact `session.update` payloads the middle tier sends and fires them at a **live** Azure OpenAI realtime deployment, failing if tools/instructions/transcription don't come back correctly. | In: live Azure OpenAI realtime deployment (via flags/env/`azd env get-values`). Out: exit code + stdout diagnostics (no file). | **azd `postdeploy` hook** (via `scripts/smoke_realtime.ps1`/`.sh`). Not referenced by CI. | **Yes** -- the entire point of the script is to probe a live deployment. | Deferred indefinitely -- explicitly excluded ("NOT setup_search_index or anything that calls Azure"); the azd hook keeps running the Python version by default (`TOOLING_IMPL` note below). |
| `scripts/e2e_order_resume.py` | Real-browser (Playwright/headless Chromium or Edge) end-to-end check of order-resume behavior (reconnect, tap-to-continue, page reload, idle close) against the built frontend and the real middle tier, with a fake realtime upstream. No Azure calls. | In: a built frontend (`app/backend/static`), a running middle tier, Playwright-driven browser. Out: pass/fail + console diagnostics (no file). | Not part of the default test run (needs a browser + Playwright installed); not an azd hook, not referenced by CI. | None directly, but needs a real browser engine and a built frontend bundle -- too heavy/complex to be the "representative, low-risk" first port. | Deferred -- a plausible future candidate once a C# equivalent of Playwright-driven browser automation is justified for a later wave (not attempted here). |
| `scripts/menu_ingestion_search_json.ipynb` | Generic (persona-agnostic) notebook walkthrough: configure Azure OpenAI + Azure AI Search, prepare menu JSON, and upload it to a search index for hybrid semantic search. | In: a raw menu JSON export. Out: documents upserted into a live Azure AI Search index. | Manual, interactive notebook only; not an azd hook, not referenced by CI (the production path is `app/backend/setup_search_index.py`, which already lives in, and is out of scope per, `app/backend`). | **Yes** -- Azure OpenAI + Azure AI Search throughout. | Deferred indefinitely -- notebooks are explicitly the kind of Azure-dependent, interactive tooling this issue's "no Azure calls" constraint excludes from a first port; also the least "representative, low-risk" shape (interactive exploration, not a deterministic CLI tool). |
| `scripts/sonic_menu_ingestion_search.ipynb` | Same pipeline as `menu_ingestion_search_json.ipynb`, specialized to ingest one persona's menu (`personas/<PERSONA_ID>/`) by id. | Same as above, scoped to one persona. | Manual, interactive notebook only; not an azd hook, not referenced by CI. | **Yes** -- Azure OpenAI + Azure AI Search throughout. | Deferred indefinitely, same reason as `menu_ingestion_search_json.ipynb`. |
| `tests/conformance/tests/Conformance.Tests/Scripts/verify_fake_entra_token.py` | Verifies a `FakeEntraIssuer`-minted JWT the same way a real client-side PyJWT consumer would (signature, issuer, audience, claims) -- an interop check that the fake issuer's tokens are genuinely PyJWT-compatible, not just internally self-consistent. | In: a signing key + token passed as CLI args by its caller. Out: exit code only (no file). | Not an azd hook, not referenced by any workflow directly; invoked as a subprocess by `FakeEntraIssuerPyJwtValidationTests.cs` (an xUnit test in `tests/conformance`), which IS exercised by CI's existing `conformance.yml` `dotnet-tests` job. | None (local JWT verification only). | **Intentionally stays Python** -- its entire purpose is PyJWT interop verification (would the real Python `PyJWT` library accept this token), so porting it to a C# JWT library would defeat the point of the check. Not a candidate for any future wave. |

Note: `app/backend/setup_search_index.py` itself is inside `app/backend` and is therefore **out of
this inventory's scope** (the issue is scoped to Python outside `app/backend`); only its `scripts/`
launcher wrappers are in scope, below. Batch 2 made one explicit, narrow exception to this rule for
this one script's request-building half only, at the Squad's own direction -- see "Batch 2 port"
below for what was (and, just as importantly, was NOT) ported, and why this stays an exception rather
than a scope change.

## Inventory: `.ps1`/`.sh` wrappers that shell out to Python

| File(s) | Wraps | azd hook | Proposed C# shape |
| --- | --- | --- | --- |
| `scripts/setup_search_index.ps1`, `scripts/setup_search_index.sh` | `app/backend/setup_search_index.py` (builds/refreshes the Azure AI Search index from the production menu export) | **azd `postprovision` hook** (after `postprovision_auth`/`write_env`) | Not ported (wraps an Azure-dependent script). Still stays Python for the live azd hook: batch 2 ported `setup_search_index.py`'s own request-building logic in C# as a proof of concept (see "Batch 2 port" below), but that port has no CLI/`run()` orchestration and is not wired into this wrapper or the azd hook, which keep running the Python implementation unconditionally. |
| `scripts/smoke_realtime.ps1`, `scripts/smoke_realtime.sh` | `scripts/smoke_realtime.py` (realtime session smoke check; never fails the deployment, warns only) | **azd `postdeploy` hook** | Not ported (wraps an explicitly-excluded, Azure-dependent script); stays Python. |
| `scripts/start.ps1`, `scripts/start.sh` | `app/backend/app.py` directly (runs the Python backend itself via gunicorn, the application entry point) | Not an azd hook; local dev convenience only. | Out of scope -- this is the application, not "tooling" (and a C# backend entry point is `app/backend-dotnet`'s own, separately-tracked concern, not this issue's). |
| `scripts/load_python_env.ps1`, `scripts/load_python_env.sh` | Nothing Python-specific to port -- it bootstraps the Python `.venv` itself (creates it, installs `app/backend/requirements.txt`) so the OTHER scripts above have an interpreter to run. | Sourced by `setup_search_index.ps1`/`.sh` AND `start.ps1`/`.sh` before they invoke Python. | Out of scope -- there is no Python *logic* here to port; it is the venv bootstrap `update_menu_sizes.py` et al. depend on existing at all. |

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

The two load-bearing differences from a literal transliteration:

* Where the Python script hardcodes its file paths via `Path(__file__).resolve().parent.parent /
  "personas" / "<a specific persona id>" / ...`, the C# tool discovers whichever persona pack is
  actually checked in (`tools/dotnet/src/UpdateMenuSizes/PersonaMenuLocator.cs`, globbing
  `personas/*/menu/source/*-menu-items.json`) and derives `menuItems.json` and
  `product_search_map.json`'s paths from that match, rather than hardcoding a persona id -- so the
  tool (and its tests) keep working unchanged as persona packs are added, renamed, or removed.
  `--production`/`--menu`/`--product-search-map` arguments still let tests (or a developer) point
  it at throwaway copies instead of the real, checked-in files. Per PR #224 review R3,
  `docs/persona-architecture.md`'s planned multi-persona layout means a *second* persona can add
  its own `menu/source/*-menu-items.json` export without wanting this tool at all, so discovery
  further filters matches down to whichever persona(s) have **opted in** by also placing a sibling
  `menu/product_search_map.json` next to their export; zero or more-than-one opted-in match is a
  clear, actionable error (see "Clean CLI errors, not stack traces" below), not an ambiguous crash.
* `PRODUCT_SEARCH_MAP` (the Python script's hardcoded `menuItems.json`-name -> production-search-term
  dictionary, which names actual brand products like `"Oreo® Peanut Butter Shake"`) is **not**
  duplicated as a hardcoded C# dictionary -- that data is a persona's own menu/brand data, not
  something a generic, persona-agnostic tooling port should own. It was copied into each persona's
  own `personas/<id>/menu/product_search_map.json` (loaded via
  `MenuSizeUpdater.LoadProductSearchMap`), which also keeps this brand-specific data exempt from
  the repo's brand-word rebrand scanner the same way the rest of `personas/<id>/**` already is.
  Because a hand-copied file can silently drift from the Python script's own hardcoded dict (PR
  #224 review R2 found exactly this: a product's search term was changed in the JSON file and
  parity on real data stayed green, since that product never triggers a visible size change), a
  dedicated test,
  `PythonParityTests.ProductSearchMapFile_MatchesPythonScriptsHardcodedDict_ExactlyInOrder`, loads
  the Python script's module-level `PRODUCT_SEARCH_MAP` dict directly (`runpy.run_path` plus
  `json.dumps`, which does not trigger the script's `__main__` guard) and asserts it equals the JSON
  file's entries exactly -- same keys, same values, same order -- so the copy is kept identical by
  test, not merely by discipline. `LoadProductSearchMap` also throws (naming the offending key)
  rather than silently mapping a non-string JSON value to `""`, which would otherwise make
  `FindSizesForProduct`'s substring search match *every* product.

### Byte-for-byte output parity, not just equivalent JSON

Matching Python's `json.dump(menu_data, f, indent=4, ensure_ascii=False)` + `f.write("\n")` exactly
(not just producing structurally-equal JSON) needed four separate fixes, each independently
mutation-tested (see below):

* **`PythonJsonEncoder.cs`**: a fully custom `System.Text.Encodings.Web.JavaScriptEncoder`
  reproducing `ensure_ascii=False`'s exact escape set (only `"`, `\`, and C0 control characters) --
  neither of System.Text.Json's built-in encoders matches this; both still force-escape emoji and
  non-breaking spaces that Python writes raw as UTF-8.
* **Newline handling**: `JsonSerializerOptions.NewLine` and the manually-appended final trailing
  newline both use `Environment.NewLine`, matching Python's text-mode `"w"` newline translation
  (CRLF on Windows, LF elsewhere) for every newline the write emits, including the last one.
* **`PythonFloatRepr` and `FormatPriceLikePython`** (PR #224 review R4 fixed a wrong assumption
  here): not every production price has a decimal point -- a whole-number price like `"price": 2`
  parses as a Python `int`, and `json.dump` writes it back unchanged as `2`, never `2.0`, while a
  price with a decimal point (e.g. `1.50`) parses as a `float` and is re-serialized via Python's
  shortest-round-trip `repr` (`1.50` becomes `1.5`), not whatever trailing-zero scale a C# `decimal`
  happened to retain from parsing. `ProductEntry` keeps the original raw JSON price token
  (`PriceText`) alongside the parsed `decimal`, so `FormatPriceLikePython` can tell which case
  applies: it reuses `PriceText` verbatim when it has no `.`/`e`/`E`, otherwise falls back to
  `PythonFloatRepr`. Either way the price is inserted into the JSON tree by parsing that exact
  string (`JsonNode.Parse`), not by assigning the decimal/double directly.
* **Whole-tree number-literal normalization** (PR #224 review R2): Python's `json.load`/`json.dump`
  round-trip applies the float-vs-int distinction above to *every* number in the parsed document,
  not just the price fields this tool explicitly rewrites -- a pre-existing `2.50` elsewhere in
  `menuItems.json` becomes `2.5` on any run, while a pre-existing bare integer like `"quantity": 2`
  stays `2` (never `2.0`), because Python's `json` module itself distinguishes `int` and `float`
  tokens on parse and re-serializes each with its own type's `repr`. A naive C# port that only
  reformats the handful of fields it touches would leave every *other* number exactly as typed in
  the source file, silently breaking byte parity the moment any untouched number has a trailing
  zero. `MenuSizeUpdater.NormalizeNumberLiteralsLikePythonJsonDump` walks the entire parsed
  `JsonNode` tree after the per-item update loop and, for every numeric leaf whose raw JSON text
  contains `.`, `e`, or `E` (i.e. was written as a float), replaces it with `PythonFloatRepr`'s
  canonical string; leaves bare-integer leaves (no `.`/`e`/`E`) completely untouched.

### Multi-persona discovery, and clean CLI errors, not stack traces

`PersonaMenuLocator.Locate` (PR #224 review R3) first globs `personas/*/menu/source/*-menu-items.json`
for *candidate* persona production exports, then filters those down to whichever candidate(s) have
also **opted in** by placing a sibling `personas/<id>/menu/product_search_map.json` next to their
export. This two-step filter exists because `docs/persona-architecture.md`'s planned layout lets a
second persona add its own production export (porting its own POS data) without wanting this
specific size-reconciliation tool at all -- an un-opted-in persona's export must stay invisible to
this tool, so adding one never breaks the first, already-opted-in persona's existing automated run.
Zero candidates, candidates with zero opted in, or more than one opted in are each a distinct,
actionable `InvalidOperationException` message (see `PersonaMenuLocator.cs`); exactly one opted-in
match is auto-selected, matching today's single-persona behavior.

`Program.cs`'s entry point is a single line delegating to `CliRunner.Run(args, Console.Out,
Console.Error)` -- all argument parsing, persona discovery, and top-level error handling lives in
`CliRunner.cs` instead of C# top-level-statement local functions, which compile to `private`
methods on the compiler-synthesized `Program` class and so cannot be unit-tested directly (even
`InternalsVisibleTo` only affects `internal`-or-looser members). `CliRunner.Run` takes `TextWriter`s
for stdout/stderr specifically so `CliRunnerTests.cs` can assert on exactly what a real run would
print, without spawning a subprocess. Two behaviors `CliRunner.Run` is responsible for:

* **`--production` skips persona discovery entirely.** An explicit `--production` means the caller
  has already picked a persona, so the run must not also need (or be blocked by) however many other
  persona packs happen to exist on disk -- including a case `PersonaMenuLocator` itself would
  otherwise refuse (two personas both opted in). `--menu`/`--product-search-map` still default to
  the same menu/source-sibling convention `PersonaMenuLocator` uses when only `--production` is
  given.
* **A clean one-line error, not a stack trace, when discovery fails.** When no `--production` is
  given, `CliRunner.Run` calls `PersonaMenuLocator.Locate` inside a `try`/`catch
  (InvalidOperationException)`; on catch, it writes `update-menu-sizes: <message>` to `stderr` and
  returns exit code `1`, instead of letting the exception propagate as an unhandled-exception stack
  trace. The catch is scoped to `InvalidOperationException` specifically (not a blanket catch-all),
  so a genuine bug elsewhere (e.g. a bad `--production` path the caller supplied explicitly) still
  surfaces as a real, diagnosable exception.

### Output-parity test (and the mutation checks)

`tools/dotnet/tests/UpdateMenuSizes.Tests/PythonParityTests.cs` is the issue's acceptance bar made
concrete: it discovers the real persona fixtures via `PersonaMenuLocator`, copies them (production
export, `menuItems.json`, and `product_search_map.json`) into two private temp directories, runs the
actual `scripts/update_menu_sizes.py` as a genuine subprocess against one copy, runs the C# port
in-process against the other, then asserts:

1. the two resulting `menuItems.json` files are **byte-identical** (`File.ReadAllBytes` compared as
   spans) -- not merely structurally-equal JSON, since the encoding/newline/float-repr fixes above
   are only provable at the byte level;
2. the two programs' captured console output is identical, including the per-item SKIP/UPDATED
   lines (with Python's `['mini', 'small']`-style list repr) and the trailing blank-line-then-summary
   shape; and
3. the "Updated N items" count each program reports (parsed from its own stdout, not just read off
   an in-process result object) agrees.

It never touches the real, checked-in fixture files. It resolves a Python interpreter the same way
`tests/conformance`'s `FakeEntraIssuerPyJwtValidationTests` does (repo-root `.venv` first, then a
bare `python3`/`python` on PATH), skips locally if none is found, and fails (not skips) in CI
(`GITHUB_ACTIONS=true`) -- update_menu_sizes.py needs only the standard library, so any Python 3
interpreter qualifies.

`tools/dotnet/tests/UpdateMenuSizes.Tests/MenuSizeUpdaterTests.cs` unit-tests the individual
pieces against small synthetic fixtures, independent of the real menu data: `ExtractSize`'s prefix
matching (including the dead-prefix case above), the Cherry Limeade/Ocean Water slush/diet
exclusions, first-match-per-size-key-wins, idempotency, that `UpdateMenu` only replaces a changed
item's `sizes` array (leaving every other field untouched), `LoadProductSearchMap`'s parsing (and
its non-string-value guard), and each of the four byte-parity fixes above in isolation
(trailing-zero price formatting, the Python list-repr log line, non-ASCII characters written raw,
the trailing `Environment.NewLine`, and whole-tree number-literal normalization -- both the
trailing-zero-becomes-trimmed case and the bare-integer-stays-untouched case).

`tools/dotnet/tests/UpdateMenuSizes.Tests/PersonaMenuLocatorTests.cs` builds synthetic
`personas/` layouts under a fresh temp directory per test (0 personas, 1 opted-in persona, 2
personas with only one opted in, and 2 personas both opted in) to exercise the multi-persona
discovery logic described above without depending on however many real persona packs exist in the
repo today.

`tools/dotnet/tests/UpdateMenuSizes.Tests/CliRunnerTests.cs` drives `CliRunner.Run` directly
against in-memory `TextWriter`s (no subprocess) to verify `--production` bypasses persona discovery
entirely (even deriving `--menu`/`--product-search-map` defaults from `--production`'s own sibling
`menu/` directory when they're omitted), and that a `PersonaMenuLocator` discovery failure becomes a
clean, single-line `stderr` message plus exit code `1` -- never an unhandled stack trace.

**Mutation checks performed**:

* Temporarily removed the Cherry Limeade "slush" exclusion from `FindSizesForProduct` and reran the
  suite -- `FindSizesForProduct_ExcludesCherryLimeadeSlushAndDietVariants` failed as expected (it
  picked up an unrelated "...Slush" product's price under a size key the legitimate product didn't
  already have). Restored the guard and reran; all tests passed again.
* Reverted `PythonJsonEncoder.Instance` to `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` -- both the
  byte-identical parity test and the dedicated non-ASCII unit test failed as expected. Reverted the
  trailing-newline `Environment.NewLine` back to a literal `"\n"` -- both the parity test and the
  dedicated trailing-newline unit test failed as expected. Reverted the `PythonFloatRepr`-based price
  write-out back to assigning the raw `decimal` -- the dedicated trailing-zero-price unit test failed
  as expected (the real fixtures happen not to contain a trailing-zero price today, so the parity
  test alone would not have caught this one). Restored all three fixes and reran; all 31 tests passed
  again.
* Removed the `NormalizeNumberLiteralsLikePythonJsonDump` call from `UpdateMenu` -- the dedicated
  "untouched trailing-zero number elsewhere in the tree" unit test failed as expected (the stray
  `2.50` stayed `2.50` instead of becoming `2.5`). Restored the call, then removed the
  `'.'`/`'e'`/`'E'` guard so *every* number got reformatted -- the dedicated "whole-number integer
  literal stays unchanged" unit test failed as expected (`"quantity": 2` became `"quantity": 2.0`).
  Restored the guard and reran; all tests passed again.
* Replaced `LoadProductSearchMap`'s non-string-value guard with the original silent
  `?? string.Empty` fallback -- `LoadProductSearchMap_RejectsNonStringValue` failed as expected ("No
  exception was thrown"). Restored the guard and reran; all tests passed again.
* Reproduced PR #224 review R2's exact scenario by changing `"Classic Vanilla Shake"` to a wrong
  term directly in the checked-in persona's `product_search_map.json` -- confirmed
  `ProductSearchMapFile_MatchesPythonScriptsHardcodedDict_ExactlyInOrder` failed with a clear
  collection-diff at that exact entry, proving this test (unlike the real-fixture byte-parity test
  alone) actually catches this class of drift. Restored the file (`git diff` confirmed byte-identical
  afterward) and reran; all tests passed again.
* In `PersonaMenuLocator.Locate`, bypassed the opt-in filter (`candidates.Where(HasOptedIn)` ->
  `candidates`) -- both `Locate_ThrowsCleanError_WhenOnePersonaExistsButHasNotOptedIn` and
  `Locate_SelectsTheOptedInPersona_WhenASecondPersonaAddsAnExportWithoutOptingIn` failed as expected
  (the latter now hit the "more than one persona has opted in" ambiguity error instead of cleanly
  selecting the one true opted-in persona). Restored the filter and reran; all tests passed again.
* In `CliRunner.Run`, changed the discovery-failure branch's `return 1` to `return 0` -- 
  `Run_PrintsCleanErrorAndReturnsNonZeroExitCode_WhenPersonaDiscoveryFails` failed as expected
  (`Expected: 1, Actual: 0`). Restored the fix and reran; all 46 tests passed again.

### CI wiring

A new, narrowly-scoped workflow, `.github/workflows/dotnet-tooling.yml`, builds and tests
**only** `tools/dotnet/Tooling.slnx`, triggered only on changes under `tools/dotnet/**` or the
workflow file itself. This is new wiring (none of this PR's new files were previously built/tested
by any existing workflow), added as a dedicated file rather than a new job inside the existing
`conformance.yml` (which already has its own `dotnet-tests` job for `app/backend-dotnet`) so this
PR does not need to touch that file at all -- `conformance.yml` is shared, frequently-edited
ground for other in-flight work, and `tools/dotnet` is a fully independent solution with nothing to
gain from sharing a workflow file with it.

## Batch 1 port: `extract_production_items.py` -> `tools/dotnet/src/ExtractProductionItems`

Issue #16's first follow-up wave (`squad/16-tooling-batch-1`), ported with the same rigor as
`update_menu_sizes.py` above. Chosen because it was the inventory's own stated next candidate: a
read-only report (no file mutation at all, an even smaller surface than `update_menu_sizes.py`'s
JSON rewrite), with **no** Azure/network/subprocess dependency and no moving parts that would make a
stdout-parity test flaky.

### Scaffold shape

Same conventions as `UpdateMenuSizes`: a plain console-app project
(`tools/dotnet/src/ExtractProductionItems/ExtractProductionItems.csproj`) with a thin `Program.cs`
delegating to a testable `CliRunner.Run(args, Console.Out, Console.Error)`, plus a matching
`tools/dotnet/tests/ExtractProductionItems.Tests/` xUnit v3 project, both registered in the shared
`Tooling.slnx`. `ProductionItemsExtractor.cs` holds the faithful, line-for-line port of
`extract_production_items.py`'s module-level functions (`collect_products_from_category`,
`normalize_size_name`, `get_size_variants`, `extract_production_items`, `load_ui_items`,
`normalize`, and `main`'s report-printing body); `ProductionExportLocator.cs` is a simpler sibling of
`PersonaMenuLocator.cs` -- this tool needs no `product_search_map.json` opt-in concept (it reads a
production export and a sibling `menuItems.json` only), so **every** persona with a production
export is a discovery candidate; zero or more than one is a distinct, actionable
`InvalidOperationException`, caught by `CliRunner.Run` into a clean one-line `stderr` message plus
exit code `1`, the same "clean CLI errors, not stack traces" convention as `UpdateMenuSizes`.
`Program.cs` also sets `Console.OutputEncoding = Encoding.UTF8` before running -- the report's
box-drawing and emoji characters otherwise get mangled by Windows' legacy console codepage, a
failure mode this port hit directly against the real Python twin too (which needs
`PYTHONIOENCODING=utf-8` set for the exact same reason; the Python twin does not set this for itself).

### Faithful ordering/formatting details worth calling out

A read-only report tool's "observable behavior" is almost entirely about **order** and
**formatting**, not state mutation, so most of this port's care went into reproducing two
non-obvious Python semantics exactly:

* **`Counter.most_common()`'s tie-break is stable, not alphabetical.** Python's
  `sorted(counter.items(), key=itemgetter(1), reverse=True)` is documented to stay stable even with
  `reverse=True`, so two categories with an equal item count keep the `Counter`'s own
  first-occurrence-in-`production` order, not alphabetical order. The port builds `catCounts` as an
  explicitly-ordered `List<(string,int)>` (first-occurrence order, not a `Dictionary`, whose
  enumeration order is not a documented guarantee) and sorts it with LINQ's `OrderByDescending`
  (also a documented-stable sort) to reproduce the same tie-break -- proven by
  `ReportBuilderTests.BuildReport_PreservesFirstOccurrenceOrder_ForCategoriesTiedOnCount` (see
  mutation check below).
* **Codepoint, not culture-aware, string sorting**, for both the within-category item-name sort
  (`sorted(..., key=lambda x: x["name"])`) and the gap-analysis name lists (`sorted(set_difference)`)
  -- Python's default string comparison sorts by Unicode **codepoint**, so e.g. `"apple"`
  (lowercase) sorts *after* `"Banana"`/`"Cherry"` (uppercase), which a culture-aware or
  case-insensitive C# comparer would get backwards. This was first reproduced via
  `StringComparer.Ordinal`, which agrees with Python for every name actually seen in real data, but
  is not exactly the same thing: `StringComparer.Ordinal` compares UTF-16 *code units*, which
  diverges from a true codepoint comparison for astral characters outside the Basic Multilingual
  Plane (e.g. emoji) -- an emoji's leading UTF-16 surrogate value can sort *before* a BMP character
  whose actual codepoint is lower, the opposite of what Python's `sorted()` does. PR #243 review
  fixed this by replacing `StringComparer.Ordinal` with a small purpose-built
  `CodePointComparer` (`tools/dotnet/src/ExtractProductionItems/CodePointComparer.cs`, using
  `System.Text.Rune.DecodeFromUtf16` to decode and compare actual codepoints) at every name-sort
  call site, proven against both BMP and astral-character cases in `CodePointComparerTests.cs` and
  `ReportBuilderTests.BuildReport_SortsAnAstralCharacter_ByItsActualCodePointValue_NotItsUtf16SurrogateValue`.
  (Ordinal *equality* -- used by this port's `HashSet`/`Dictionary` lookups -- was left unchanged:
  ordinal equality is bijective with codepoint-sequence equality, so only *ordering* was affected.)

`string.Replace(oldValue, newValue)` throws `ArgumentException` for an empty `oldValue` (Python's
`str.replace("", "")` is a harmless no-op by contrast) -- `NormalizeSizeName` guards this explicitly
for a product with an empty `displayName`, with its own regression test
(`NormalizeSizeName_DoesNotThrow_WhenParentDisplayNameIsEmpty`).

### Data-driven persona discovery, zero rebrand hits

Same as `UpdateMenuSizes`: no persona id or brand product name is hardcoded anywhere in this port's
C# or this doc section -- `ProductionExportLocator.Locate` discovers whichever persona pack(s)
actually have a production export on disk, and is exactly what the shipped CLI's
`--production`/`--menu` defaults use (see `CliRunnerTests.cs`). (The real, checked-in Python twin
itself does hardcode one persona id/path, same as `update_menu_sizes.py` -- out of this issue's
scope to change.)

The output-parity test below, however, **does not** use `ProductionExportLocator` to find its
fixtures (see "Output-parity test" below for why) -- `Locate`'s discovery-and-disambiguate behavior
is specifically a property of the shipped CLI, proven by `ProductionExportLocatorTests.cs` and
`CliRunnerTests.cs` alone.

### Output-parity test (stdout only -- this tool never writes a file)

`tools/dotnet/tests/ExtractProductionItems.Tests/PythonParityTests.cs` follows the same spirit as
`UpdateMenuSizes.Tests/PythonParityTests.cs`: it runs the actual, unmodified
`scripts/extract_production_items.py` as a genuine subprocess at its real repo location, runs
`ProductionItemsExtractor`'s equivalent pipeline in-process against the same real fixtures, then
asserts the two programs' captured stdout is **identical byte-for-byte** (not merely each line's
parsed content) -- including the box-drawing divider characters, the emoji gap-analysis markers,
and the verbatim-preserved em dash in `"(none — UI is clean)"` (the Python twin's own literal
output text, not newly-authored prose, so the squad's own no-em-dash style rule does not apply to
it) -- plus an independent cross-check that both programs' own "Total production items: N" line
agrees with each other and with the C# port's own data. It resolves a Python interpreter the same
way `UpdateMenuSizes.Tests` does (repo-root `.venv` first, then a bare `python3`/`python` on PATH),
skipping locally if none is found and failing (not skipping) in CI.

**PR #243 review R1**: this test originally found its two fixture files via
`ProductionExportLocator.Locate` -- the same discovery the shipped CLI uses. That was wrong for a
test (as opposed to the CLI itself): `Locate` is *supposed* to throw the moment a second persona
pack adds its own `menu/source/*-menu-items.json` (planned per `docs/persona-architecture.md`), so
the CLI operator can disambiguate explicitly -- but the real Python script never discovers
anything; it hardcodes `personas/<id>/...` directly in its own `POS_DATA_PATH`/`UI_MENU_PATH`
module constants (relative to its own `__file__`) and keeps working regardless of how many other
persona packs exist. A `Locate`-based parity test would therefore start failing the instant a
second persona pack lands -- for a reason having nothing to do with whether the C# port still
matches its Python twin. The fix: the test now resolves its own fixtures the same
(discovery-free) way the Python script resolves its own, by inspecting
`POS_DATA_PATH`/`UI_MENU_PATH` via `runpy.run_path` -- the same technique
`UpdateMenuSizes.Tests/PythonParityTests.cs` already uses to read `PRODUCT_SEARCH_MAP` out of
`update_menu_sizes.py` (`runpy.run_path`'s default `run_name` is `"<run_path>"`, not `"__main__"`,
so the script's own `if __name__ == "__main__":` guard never fires -- only its module-level
constants are evaluated). A new regression test,
`DotnetPort_ParityApproach_IsUnaffectedByASecondPersonaExport`, builds a synthetic two-persona repo
layout and proves both halves of this: `ProductionExportLocator.Locate` genuinely throws in that
layout (confirming the regression is real), while the real script run unmodified from that same
layout, and this test file's own `runpy`-based resolution, are both unaffected by it.

### Known, accepted Python-parity divergences

A handful of edge cases were deliberately left un-reproduced, per PR #243 review: real menu/
production data never exercises them, and matching them exactly would add meaningfully more
complexity than the risk justifies. If any of these ever becomes a real concern, fix the
underlying C# (`ProductionItemsExtractor.cs`, see its XML doc comments at each site) rather than
just updating this note.

* **`"price": true` (a JSON boolean, not a number).** Python's `product.get("price", 0.0)` would
  simply return the Python `bool` (Python's `bool` is a subtype of `int`, so `True` would be printed
  as `1.00` after `:.2f`-style formatting). The C# port's `GetDoubleOrDefault` treats a boolean
  JSON value as absent and falls back to `0.0` instead, printing `0.00`. **`"price": false` does
  NOT diverge**: Python's `False` is `int` `0`, which also formats as `0.00` -- the exact same
  value this port's fallback already produces, so the two sides agree on `false` by coincidence of
  both landing on zero, even though the C# path gets there by a different route (treating it as
  absent, not by reading a falsy `0`).
* **Duplicate keys within the same JSON object** (e.g. two `"a"` entries inside one
  `relatedProducts.alternatives` or `productGroups` object). The *lookup* dictionaries
  (`BuildLookup`'s `Dictionary<string, JsonElement>`, used for `dict.get(id)`-style access) DO match
  Python's `json.load` "last occurrence wins" semantics exactly -- a plain `Dictionary` indexer
  assignment is itself last-write-wins, same as Python's parsed `dict`. The real divergence is
  elsewhere: anywhere this port iterates the *original* `JsonElement` for document order (not a
  `BuildLookup` lookup), `JsonElement.EnumerateObject()` yields **every** property occurrence,
  duplicates included -- unlike Python's `dict`, which collapses to one entry before any iteration
  ever sees it. For a product whose `relatedProducts.alternatives`/`productGroups` object has a
  literal duplicate key, this means the C# port can enumerate and process that one logical size
  variant **twice**, doubling it in the output, where Python would only ever see it once. See
  `BuildLookup`'s own XML doc comment for the authoritative statement of this.
* **İ (Turkish dotted capital I, U+0130) lower-casing.** Python's `str.lower()` and .NET's
  `ToLowerInvariant()` disagree on this one specific codepoint's invariant-culture lowercase
  mapping; `Normalize` uses `ToLowerInvariant()` as everywhere else in this port.

`tools/dotnet/tests/ExtractProductionItems.Tests/ProductionItemsExtractorTests.cs` unit-tests the
individual pieces against small synthetic fixtures: every `NormalizeSizeName` prefix case plus the
strip-parent-name/trademark-stripping/Standard-fallback cases (including the empty-parent-name
no-throw regression above), `Normalize`'s lowercasing/symbol-stripping/whitespace-collapsing,
nested-category document-order walking with first-category-wins de-duplication (a product reachable
from two different top-level categories keeps the first one's name and is not duplicated),
`isRecipe` skipping, and the `relatedProducts.alternatives` -> `productGroups` size-variant
resolution (including the Standard-price fallback when none resolve).
`ProductionExportLocatorTests.cs` builds synthetic `personas/` layouts (0, 1, and 2 persona exports)
under a fresh temp directory per test, mirroring `PersonaMenuLocatorTests.cs`'s structure but without
an opt-in-file concept. `CliRunnerTests.cs` drives `CliRunner.Run` directly against in-memory
`TextWriter`s to verify `--production` bypasses persona discovery entirely (deriving `--menu`'s
default from `--production`'s own sibling `menu/` directory when omitted) and that a discovery
failure becomes a clean, single-line `stderr` message plus exit code `1`. `ReportBuilderTests.cs`
unit-tests `BuildReport`'s sorting/formatting/line-splitting behavior in isolation, constructing
`ProductionItem`/`UiItem` records directly rather than via JSON fixtures.

**Mutation check performed**: temporarily added a `.ThenBy(c => c.Category, StringComparer.Ordinal)`
secondary sort key to `BuildReport`'s category-count ordering (simulating an "also alphabetize the
tie-break" regression) -- `BuildReport_PreservesFirstOccurrenceOrder_ForCategoriesTiedOnCount` failed
as expected (`Zeta` and `Alpha` swapped order). Restored the fix and reran; all 89 tests across both
`tools/dotnet` test projects passed again.

**PR #243 review mutation checks**: each independently reverted and confirmed failing before being
restored --
(a) reverting `CodePointComparer.Instance` back to `StringComparer.Ordinal` at the within-category
sort call site failed `BuildReport_SortsAnAstralCharacter_ByItsActualCodePointValue_NotItsUtf16SurrogateValue`
(asserted the exact wrong order `StringComparer.Ordinal` would produce);
(b) reverting the product-level and child-product-level Python-falsy (`{}`-as-falsy) checks each
failed their own new regression test (`ExtractProductionItems_SkipsAProductThatIsPresentButAnEmptyObject`,
`ExtractProductionItems_SkipsASizeVariantChildProduct_ThatIsPresentButAnEmptyObject`);
(c) reverting the Standard-fallback `PriceOrZero` helper back to a plain default-value read failed
`ExtractProductionItems_NormalizesANegativeZeroStandardFallbackPrice_ToPositiveZero`;
(d) the new `DotnetPort_ParityApproach_IsUnaffectedByASecondPersonaExport` regression test itself
directly proves the R1 fix's necessity: in its synthetic two-persona layout,
`ProductionExportLocator.Locate` genuinely throws (the exact failure mode the old,
`Locate`-dependent parity test design would have hit), while the `runpy`-based fixture resolution
the parity test now uses is unaffected by the same layout.

### CI wiring

`scripts/extract_production_items.py` was added to `.github/workflows/dotnet-tooling.yml`'s
`pull_request`/`push` path filters alongside `scripts/update_menu_sizes.py` (its real fixture data,
`personas/*/menu/**`, was already covered by the existing filter).

## Batch 2 port: `app/backend/setup_search_index.py`'s request-building, via client-seam + recorded-fixture (no live Azure calls)

Issue #16's instruction for this batch: since both non-Azure candidates in the inventory
(`update_menu_sizes.py`, `extract_production_items.py`) are already ported, implement the
"client-seam + recorded-fixture" design note above for ONE Azure-dependent tool, still with no live
Azure calls. `app/backend/setup_search_index.py` was the suggested target.

### Scope: narrower than a full port, and an explicit, narrow exception to this doc's own `app/backend`-exclusion rule

Two things make this different from every other entry in this doc:

* **This is a REQUEST-BUILDING port only**, not a full port of `setup_search_index.py`. It
  reconstructs, independently in C#, the exact Azure AI Search REST request bodies the real script's
  `create_or_update_index` (the index definition PUT) and `upload_documents` (the 100-document batch
  POSTs) would send -- and nothing past that. It never builds a real `SearchIndexClient`/
  `SearchClient` against a real endpoint, never calls `generate_embeddings`'s real Azure OpenAI
  embedding call (a deterministic fixture stands in -- see below), and implements none of
  `delete_stale_documents`/`verify_document_count`/the CLI's `--dry-run`/`--persona` argument
  surface/`run()`'s own orchestration. It is a demonstration that the REQUEST-BUILDING half of an
  Azure-dependent tool can be ported and proven correct with zero live calls, per the design note
  above -- not a drop-in replacement for the Python script.
* **`app/backend/setup_search_index.py` is normally out of this doc's own stated scope** ("this
  inventory's scope" note above: issue #16 covers Python *outside* `app/backend`). This batch is an
  explicit, narrow exception authorized by the Squad's own instruction for this specific port (the
  Squad's message literally named "the search index setup's request builder" as the example target).
  It remains a narrow exception, not a scope change for the rest of this doc: `app/backend/`'s own
  Python/C# story (`docs/dotnet_mapping.md`) is untouched, `app/backend/setup_search_index.py` itself
  is read-only (never modified) throughout this batch, and the new C# code lives entirely under
  `tools/dotnet/`, never under `app/backend-dotnet`.

### Why HTTP-transport capture instead of the design note's "client-seam interface" shape

The design note above (for `benchmark_reasoning.py`/`smoke_realtime.py`/the audio-generation
scripts) proposed a thin C# client-seam interface (`ISearchIndexClient` etc.) with a real SDK
implementation and a recorded-response fake, because those tools' parity bar is "the port's
*handling* of a fixed response matches the Python twin's" -- the live response content itself is
non-deterministic model output neither side can reproduce byte-for-byte.

`setup_search_index.py`'s shape is different: nothing about the REQUEST bodies it sends is
non-deterministic (given the same persona data and the same embedding values) -- unlike a model's
response, the exact JSON a correctly-implemented port sends IS reproducible and byte/structurally
comparable on both sides. So instead of a C# client-seam interface wrapping a *response* fake, this
batch captures the Python twin's own REAL request bodies directly, by monkeypatching
`azure-search-documents`' HTTP transport layer (`azure.core.pipeline.transport.HttpTransport`) with a
non-raising `RecordingTransport` that records every outbound request's JSON body and returns a
fabricated, schema-correct 200 response so the SDK's own response deserialization succeeds and
`upload_documents`'s 100-document batching loop runs to completion across every batch -- never
opening a socket. This is a stronger, more direct form of parity proof than the design note's
response-fake shape (it compares what the REAL, unmodified Python script's REAL code actually
produces, not a hand-maintained guess at its request shape) and was only possible because this
specific tool's outputs are deterministic. The design note above still stands as the right shape for
the tools whose parity bar genuinely depends on response-handling, not request-building.

### What's captured, and how (`tools/dotnet/src/SearchIndexRequestBuilder`, `tools/dotnet/tests/SearchIndexRequestBuilder.Tests`)

* `EnabledPersonaDiscovery.cs` -- discovers every enabled persona (every `personas/*/persona.json`
  folder, sorted), the same default-enabled-set `persona_loader.PersonaCatalog.load()` resolves with
  no `PERSONAS` override, deliberately without loading jsonschema/pydantic validation (same
  "no heavy validation pipeline" convention as `ProductionExportLocator.cs`/`PersonaMenuLocator.cs`).
  Unlike those two tools, there is no "pick exactly one persona" ambiguity here: `setup_search_index.py`'s
  own default `run()` targets every enabled persona, and so does this discovery -- a second or third
  persona pack (the real repo already has three, each discovered purely from its own
  `personas/*/persona.json`) is never a
  configuration error, only more personas to build a plan for.
* `MenuDocumentBuilder.cs` -- a faithful port of `prepare_documents` (field-for-field, same
  `sanitize_key` semantics, same empty-string field defaults, same `combined_text` f-string spacing,
  same file order with no sorting so later 100-document batch slicing matches exactly). `SanitizeKey`
  iterates by `System.Text.Rune` (Unicode code point), not by `char`/`Regex` (UTF-16 code unit) --
  Python's `re` module iterates `str` by code point, so one astral character (e.g. an emoji outside
  the Basic Multilingual Plane, encoded in .NET as a UTF-16 surrogate PAIR) is a single invalid match
  there, replaced by one `"_"`; a plain `Regex.Replace` would instead treat each surrogate half as its
  own invalid character and emit `"__"` (PR #250 review R4; see `MenuDocumentBuilderTests.cs`'s
  dedicated emoji test).
* `PythonJsonDumps.cs` -- a byte-for-byte port of Python's `json.dumps(value)` with its DEFAULT
  arguments (`ensure_ascii=True`, `", "`/`": "` separators) -- the OPPOSITE default from
  `update_menu_sizes.py`'s own output file (`ensure_ascii=False`; see `PythonJsonEncoder.cs`), used
  because `prepare_documents` calls `json.dumps(item["sizes"])` with no extra arguments. Whole
  numbers stay whole (Python int `2` stays `2`, never `2.0`) using the same
  `PythonFloatRepr`/number-token-detection convention as `MenuSizeUpdater.cs`.
* `OpenAiSettingsResolver.cs` (`tools/dotnet/src/SearchIndexRequestBuilder/`) -- resolves the real
  Azure OpenAI endpoint/embedding-deployment the shipped CLI prints in the index definition's
  vectorizer, matching `setup_search_index.py`'s own `run()` (lines 471-473): `--openai-endpoint` flag
  > `AZURE_OPENAI_EASTUS2_ENDPOINT` env var, required (throws a clean, single-line, catchable
  `InvalidOperationException` if neither is set -- Python's own equivalent,
  `os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]`, raises an unhandled `KeyError` with a full stack
  trace instead; this port deliberately improves on that, it is not a divergence); and
  `--embedding-deployment` flag > `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` env var > default
  `"text-embedding-3-large"` (`EMBEDDING_MODEL` in `setup_search_index.py`). `CliRunner.cs` catches
  that exception and turns it into a one-line stderr message plus exit code 1, same pattern as the
  existing persona-discovery failure handling. **PR #250 review R1 fix**: before this, the shipped CLI
  *always* printed hardcoded placeholder values (`resourceUri: https://fake.openai.azure.com`,
  `deploymentId: fake-embedding-deployment`) regardless of the real environment, and the fixture types
  (`FixtureEmbedding`, the fake constants) lived in the production project. Both are fixed now: the
  real resolution logic above is the only thing a shipped CLI run ever uses, and `FixtureEmbedding.cs`
  plus the fake endpoint/deployment constants (now `TestFixtureValues.cs`) have moved entirely into
  `tools/dotnet/tests/SearchIndexRequestBuilder.Tests/` -- only `PythonParityTests.cs` ever
  constructs/injects them, via `SearchIndexRequestPlanner.BuildPlan`'s optional `embeddingProvider`
  parameter (`null` in production, meaning a shipped CLI run never attaches a document's `"embedding"`
  field at all -- this tool makes no Azure OpenAI call under any flag, so it has nothing real to put
  there; the CLI prints a one-line note saying so). See `OpenAiSettingsResolverTests.cs`/
  `CliRunnerTests.cs` for the default/override/missing-endpoint coverage.
* **PR #250 review R2 fix -- azd environment precedence.** Python's `main()` (lines 535-539) calls
  `load_azd_env()` before `run()` whenever `--dry-run` is absent; that helper shells out to
  `azd env list -o json`, finds the default environment's `.env` file, and calls
  `load_dotenv(path, override=True)` -- which *overwrites* any same-named value already present in the
  process environment. Python's real, effective precedence is therefore
  **azd default-environment `.env` value > process env var > built-in default**, with the CLI flag
  layered on top by `run()`'s own argument handling. The original C# port only implemented
  flag > process env > default and never consulted azd at all -- a real gap, since the live app's
  actual `AZURE_OPENAI_EASTUS2_ENDPOINT` typically lives only in the azd environment file, not in the
  calling shell's process environment. Fixed by adding `AzdEnvLoader.cs`
  (`tools/dotnet/src/SearchIndexRequestBuilder/`) and threading its result through
  `OpenAiSettingsResolver.Resolve`, giving the final precedence **flag > azd value (non-empty) >
  process env var (non-empty) > built-in default** (deployment) / **throw** (endpoint).
  `AzdEnvLoader.LoadDefaultEnvValues` deliberately does **not** shell out to the real `azd` CLI (unlike
  Python); it reads azd's own on-disk state directly -- `.azure/config.json`'s `"defaultEnvironment"`
  key, then that environment's `.azure/<name>/.env` file -- parsing the `KEY="VALUE"` lines azd itself
  always emits (always double-quoted, with `\"`/`\\`/`\n` backslash-escaping), confirmed empirically
  against a real (disposable, local-only) `azd env new`/`azd env set` run during development, never
  against a live Azure resource. This keeps every test (`AzdEnvLoaderTests.cs`,
  `OpenAiSettingsResolverTests.cs`'s azd-precedence cases, `CliRunnerTests.cs`'s two azd-wiring cases)
  free of any real `azd` process invocation -- they use real, disposable temp-directory `.azure`
  folders instead, or inject the resolved dictionary directly. If `.azure/config.json` is missing, has
  no `defaultEnvironment`, points at a `.env` file that doesn't exist, or anything else about the
  lookup fails, `LoadDefaultEnvValues` swallows the failure and returns an empty dictionary -- azd
  simply contributes nothing, and resolution falls through to process env / default exactly as before
  this fix, matching "handle azd missing gracefully" rather than Python's own behaviour here (an azd
  failure crashes Python's `main()` with an unhandled exception before `run()` is ever reached; this
  port intentionally does not reproduce that crash for a case that isn't really about OpenAI settings
  at all).
* **Empty-string divergence, generalized -- applies to BOTH settings, not just the deployment.**
  Python's `os.environ.get(name, default)`/`os.environ[name]` returns an empty string, not the
  default, when the key exists with an empty value -- and after the R2 fix, this ambiguity applies
  to *both* possible non-flag sources: a `.env`-file line like `AZURE_OPENAI_EMBEDDING_DEPLOYMENT=""`
  (or `AZURE_OPENAI_EASTUS2_ENDPOINT=""`) from the azd environment, or an empty-but-set process
  environment variable of the same name. This port treats an empty string as equivalent to "not
  set" at **every** source -- flag, azd value, and process env var alike
  (`OpenAiSettingsResolver.GetNonEmptyOrNull`) -- always falling through to the next source or the
  built-in default instead.

  An earlier draft of this note claimed an empty *endpoint* could "never happen" this way and was
  always a hard failure on both sides -- **that was wrong** (Rick's review): the real, concrete case
  is a developer's shell already having a real `AZURE_OPENAI_EASTUS2_ENDPOINT` set, while the azd
  default environment's `.env` file separately has it set to `""`. Because Python's
  `load_dotenv(path, override=True)` *overwrites* same-named process-env values with the azd file's
  value -- even an empty one -- Python ends up using the blanked-out `""`, not the real shell value,
  whatever downstream consequence that has (the request never reaching a real Azure OpenAI client
  in this preview tool, but a real `AZURE_OPENAI_EASTUS2_ENDPOINT=""` would eventually fail
  whatever else reads it). This port's `GetNonEmptyOrNull` instead skips the empty azd value and
  falls through to the real, non-empty process-env value -- so in this exact scenario this port
  *succeeds* with the real, intended endpoint while Python would have silently used the empty one.
  The two settings differ only in how visibly wrong the blanked value is afterwards (an empty
  deployment id is itself still a valid-looking string Python happily sends onward; an empty
  endpoint fails loudly the moment anything tries to use it as a URL) -- the root-cause divergence
  itself (this port recovers the real value; Python's `override=True` does not) is identical for
  both. See `Resolve_TreatsEmptyStringFlag_SameAsMissingFlag`,
  `Resolve_TreatsEmptyStringAzdValue_SameAsMissing_FallsBackToProcessEnvThenDefault`, and the
  endpoint-side `Resolve_TreatsEmptyStringAzdValue_ForEndpoint_SameAsMissing_FallsBackToProcessEnvThenSucceeds`
  in `OpenAiSettingsResolverTests.cs`.
* **azd's `\$`/`\!`/backtick escaping -- a real but practically-unreachable divergence.** Rick
  re-verified `AzdEnvLoader.cs`'s assumptions against a real azd 1.34.2 install: beyond the
  `\"`-escaped embedded quote already documented above, azd also backslash-escapes a literal `$`,
  `!`, and backtick in any value it writes (shell-safety for a file that's also meant to be
  `source`-able). `AzdEnvLoader.UnquoteDotEnvValue`'s catch-all case decodes all three back to the
  plain character, matching azd's own intent -- but python-dotenv's own parser does **not**
  recognize `\$`/`\!`/`` \` `` as escape sequences at all, and leaves the literal backslash in the
  parsed value instead of stripping it. This is a genuine parser-level divergence, but one that can
  never be exercised by either of the two keys this tool actually reads: `AZURE_OPENAI_EASTUS2_ENDPOINT`
  is a URL and `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` is an Azure OpenAI deployment name, and neither
  can legally contain a `$`, `!`, or backtick in the first place. See
  `LoadDefaultEnvValues_UnescapesBackslashEscapedDollarBangAndBacktick` in `AzdEnvLoaderTests.cs`,
  which pins this parser's behavior deliberately even though real data never reaches it.
* `FixtureEmbedding.cs` (now test-only, under `tools/dotnet/tests/SearchIndexRequestBuilder.Tests/`) --
  a deterministic, pure-function stand-in for `generate_embeddings`'s real Azure OpenAI call:
  `sha256(text)`'s first 8 bytes, each mapped from `[0, 255]` to `[-1, 1]` and rounded to 6 decimals
  via a fixed-point string round-trip (not a raw `Math.Round` call -- see the mutation-check note on
  this below) so both this C# code and the Python capture harness land on the exact same IEEE-754
  value. **Deliberately 8 dimensions, not the real 3072** (`EMBEDDING_DIMENSIONS` in
  `setup_search_index.py`) -- a documented, intentional divergence: the point of this fixture is to
  exercise the request-building code path end to end, not emulate real embedding content, which is
  non-deterministic model output neither side could reproduce anyway. The index definition's own
  `"dimensions": 3072` field comes from the real constant independently in
  `SearchIndexDefinitionBuilder.cs` and is completely unaffected by this fixture's length.
* `SearchIndexDefinitionBuilder.cs`/`DocumentBatchBuilder.cs` -- independently reconstruct the exact
  REST JSON bodies for the index definition PUT and the document-upload-batch POSTs, with field
  names/shapes captured empirically from the real `azure-search-documents==12.0.0` SDK (its Python
  model attribute names, e.g. `vector_search_dimensions`, don't match the REST JSON's camelCase,
  e.g. `"dimensions"` -- these were captured via the wire body, never guessed from the SDK's model).
* `tests/SearchIndexRequestBuilder.Tests/Fixtures/capture_search_index_requests.py` -- the capture
  harness described above: drives the REAL `build_plan`/`create_or_update_index`/`upload_documents`
  against `RecordingTransport`-backed clients, attaches `FixtureEmbedding`'s formula in place of
  `generate_embeddings`'s real call, and prints one JSON blob to stdout for every enabled persona. It
  silences the twin's own `logging.basicConfig(..., handlers=[RichHandler(...)])` (configured at
  import time) via `logging.disable(logging.CRITICAL)`, since that handler otherwise writes
  human-readable progress straight to stdout and corrupts the single JSON object this harness prints.
* `PythonParityTests.cs` -- runs the capture harness as a genuine subprocess, then structurally
  compares (`JsonStructuralAssert.cs`: same keys/values recursively, array order preserved, JSON
  string leaves compared character-for-character) its captured request bodies against
  `SearchIndexRequestPlanner.BuildPlan`'s independent C# reconstruction, for every enabled persona in
  the real repo. Structural rather than literal-byte comparison was chosen because these are
  ephemeral HTTP request bodies -- never written to disk or diffed by a human/git, unlike
  `update_menu_sizes.py`'s output file or `extract_production_items.py`'s stdout report, where
  literal byte-identity is the point -- except for the "sizes" field specifically, which IS a plain
  JSON string value and so is still compared character-for-character by the same structural
  comparison (a JSON string leaf is still just a string). On top of the semantic double-value
  equality (a Python `int` and a C# whole-number `double` with the same value compare equal), it also
  requires both sides to agree on whether a number is logically an int or a float (PR #250 review
  R3): a Python `"dimensions": 3072` vs a hypothetical C# `3072.0` now fails, even though they're
  numerically equal, because Azure AI Search's REST API can itself be type-sensitive about this
  distinction for some fields. See `JsonStructuralAssertTests.cs` for synthetic, isolated coverage of
  this rule (the real persona data's own numbers never happen to exercise an int/float divergence).

### Mutation checks (each performed for real, then reverted, while implementing this batch)

* Changing `DocumentBatchBuilder.cs`'s `BatchSize` from 100 to 99 made both the real-data parity test
  (the largest real persona's 180 documents split `[99, 81]` instead of Azure Search's real
  `[100, 80]` boundary)
  and the dedicated synthetic `DocumentBatchBuilderTests.cs` boundary test fail -- confirmed, then
  restored.
* Changing `FixtureEmbedding.cs`'s byte-to-`[-1, 1]` divisor from 255.0 to 256.0 made both the
  real-data parity test (every persona's embedding values differ) and a dedicated cross-language
  oracle test (`FixtureEmbeddingTests.cs`, comparing directly against the capture harness's own
  `fixture_embedding()` for sample texts) fail -- confirmed, then restored.
* Temporarily mismatching `TestFixtureValues.FakeOpenAiEndpoint` against the harness's own
  `FAKE_OPENAI_ENDPOINT` made the real-data parity test fail on the index definition's vectorizer
  `resourceUri` field -- confirmed, then restored.
* Removing `PythonJsonDumps.cs`'s `ensure_ascii` `\uXXXX` escaping left the REAL-DATA parity test
  GREEN -- today's real persona "sizes" data is always plain ASCII size/price pairs, so this guard is
  never exercised by real fixture data alone -- but made a dedicated synthetic test
  (`PythonJsonDumpsTests.cs`, a non-ASCII + astral-emoji string, checked directly against a Python
  `json.dumps` subprocess oracle) fail immediately. This is recorded explicitly because it is the one
  case where the real-data parity test alone would NOT have caught a real regression.
* Separately verified, and explicitly NOT claimed as a mutation-check finding: swapping
  `FixtureEmbedding.cs`'s six-decimal string-round-trip rounding for a raw `Math.Round(x, 6)` call
  produces bit-identical results for all 256 possible input byte values this specific formula can
  ever produce -- so that particular implementation choice has no test able to distinguish it from
  the alternative, and is kept for its closer conceptual match to Python's `f"{x:.6f}"` formatting
  rather than for a provable behavioural difference.

**PR #250 review fixes** (each mutation-checked the same way: break it, confirm the expected test
fails, revert):

* Reverting `MenuDocumentBuilder.cs`'s `SanitizeKey` from the Rune-based loop back to a plain
  `char`-by-`char`/`Regex.Replace` implementation made
  `MenuDocumentBuilderTests.cs`'s dedicated astral-emoji test fail (`"drinks_party__shake"`, two
  underscores, instead of the expected `"drinks_party_shake"`, one) -- confirmed, then restored.
* Replacing `JsonStructuralAssert.cs`'s `IsFloatShaped` check with an always-true condition made
  `JsonStructuralAssertTests.cs`'s `3072` vs `3072.0` test stop failing (i.e. the regression the check
  exists to catch became invisible) -- confirmed, then restored. This check was implemented twice:
  the first attempt compared raw JSON token text on both sides and looked correct against synthetic
  data, but FAILED the real-data `PythonParityTests` run (Python's captured `-1.0` vs C#'s
  `JsonValue.Create(-1.0).ToJsonString()`, which renders as `"-1"` with no decimal point -- .NET's
  `JsonValue` serializer collapses whole-number doubles to int-looking text, unlike Python's
  `json.dumps`, which always keeps a float's decimal point). The real-data test catching this before
  it shipped is exactly why the real-fixture parity test exists alongside synthetic unit tests. Fixed
  by detecting int-vs-float via CLR-boxed-type inspection for programmatically-built values
  (`JsonValue.TryGetValue(out double _)` succeeds only for a genuine boxed `double`, not `int`/`long`)
  instead of text shape, while still trusting the parsed source text (`.`/`e`/`E` presence via
  `GetRawText()`) for values read from real JSON.
* Hardcoding `CliRunner.cs`'s settings resolution to always use
  `new OpenAiSettingsResolver.Settings("https://fake.openai.azure.com", "fake-embedding-deployment")`
  (bypassing `OpenAiSettingsResolver.Resolve(...)` entirely) made
  `CliRunnerTests.cs`'s missing-endpoint test fail (stderr showed the unrelated persona-discovery
  error instead of the expected "no OpenAI endpoint configured" message, because the hardcoded fake
  endpoint always "succeeded") -- confirmed, then restored.
* **PR #250 review R2 fix (azd precedence)**: reordering `OpenAiSettingsResolver.Resolve`'s endpoint
  check to consult the process env var before the azd value made
  `Resolve_AzdEnvValue_TakesPriorityOverProcessEnvVar` fail as expected (it returned the process-env
  value instead of the azd value) -- confirmed, then restored. Separately, changing `CliRunner.cs` to
  pass `null` instead of the computed azd-values dictionary into `OpenAiSettingsResolver.Resolve(...)`
  (simulating the wiring being dropped entirely) made both new `CliRunnerTests.cs` azd cases
  (`Run_ResolvesOpenAiEndpoint_FromInjectedAzdEnvValues_WhenNoFlagOrProcessEnvVar` and
  `Run_ResolvesOpenAiEndpoint_FromARealTempAzureFolder_WithNoInjectionAndNoRealAzdCall`) fail as
  expected (both printed the "no OpenAI endpoint configured" error instead of succeeding) -- confirmed,
  then restored. Finally, making `AzdEnvLoader.cs`'s `UnquoteDotEnvValue` a no-op (returning azd's raw,
  still-double-quoted `"VALUE"` text unchanged) made four of the eight `AzdEnvLoaderTests.cs` cases
  fail as expected (asserted values still had their surrounding quotes, e.g. `"dev"` instead of `dev`)
  -- confirmed, then restored.

### CI wiring

`.github/workflows/dotnet-tooling.yml`'s existing job needed a new
`pip install -r app/backend/requirements.txt` step: unlike every prior ported tool (stdlib-only),
`capture_search_index_requests.py` imports the real `app/backend/setup_search_index.py`, which
imports `persona_loader.py`, which pulls in `jsonschema`/`pydantic`/`PyJWT`/`aiohttp` on top of
`setup_search_index.py`'s own direct `azure-identity`/`azure-search-documents`/`openai`/
`python-dotenv`/`rich` imports -- essentially the whole of `app/backend/requirements.txt`. Path
filters already cover `tools/dotnet/**` and `personas/*/menu/**` (this batch's new persona-data reads
are all `menuItems.json`/`persona.json`, already covered); no new filter entries were needed.

## `TOOLING_IMPL` (future, not wired in this PR)

Issue #16's body describes a future `TOOLING_IMPL=python|dotnet` environment variable so azd hooks
could eventually choose which implementation to run, defaulting to `python`. **This PR does not
introduce that variable or any hook wiring for it** -- per this PR's explicit scope, azd hooks and
CI defaults stay on the Python implementations unconditionally. A later wave, once more of the
inventory above is ported, is the right place to introduce `TOOLING_IMPL` for real.

## What's next

With both no-Azure candidates in the inventory (`update_menu_sizes.py`, `extract_production_items.py`)
now ported, and the design note below's approach now proven once for real (the "Batch 2 port" section
above, `app/backend/setup_search_index.py`'s request-building half), every remaining script/notebook
in the inventory still needs a **live** Azure OpenAI realtime/Whisper, Azure Speech dependency, or
(for `e2e_order_resume.py`) a real browser engine -- none of them has the same "fully deterministic
request AND response shape" property that made `setup_search_index.py`'s request-building half
portable with zero live calls (its responses are genuinely non-deterministic model output, not just
an SDK call this repo hasn't wired a fake for yet). Porting any of them for real is deferred until
there is a concrete reason to run them from C# rather than Python; this section instead sketches how
each *would* be ported and tested without ever touching a live Azure resource, so a future wave has a
starting design rather than a blank page -- updated below to note which parts of this design are now
proven and which remain a sketch.

### Design note: porting the Azure-dependent tools without live Azure calls

The common shape across `benchmark_reasoning.py`, `smoke_realtime.py`,
`generate_apology_clips.py`, `generate_demo_guest_voice.py`, and both ingestion notebooks is: build a
request payload from local/static inputs, call exactly one Azure SDK client (Azure OpenAI realtime,
Azure AI Search, or Azure Speech), then validate/transform the response. That shape is already
testable without live Azure in this repo's own existing C# code -- `tests/conformance`'s
`FakeEntraIssuer` fakes an entire auth provider for exactly this reason, and `app/backend-dotnet`'s
own middle tier already has to mock its outbound Azure OpenAI realtime client for its own unit tests.
A future port of any of these tools should follow the same two-layer split that already exists for
`update_menu_sizes.py`/`extract_production_items.py`, but with the Azure call itself behind a seam:

* **A thin client-seam interface** (e.g. `IRealtimeSessionClient`, `ISearchIndexClient`,
  `ISpeechSynthesisClient`) wrapping the one Azure SDK call each tool makes, with exactly one real
  implementation (the actual SDK client, used by the shipped CLI) and one **recorded-response fake**
  used by tests -- not a hand-rolled mock asserting on call shape, since these tools' whole point is
  validating the *content* of a real response (tool-call payloads, transcription text, synthesized
  audio), not just that a call was made.
* **Recorded fixtures, not live calls, drive the tests.** Each tool would ship one or more small
  recorded-response fixtures (JSON for the realtime/search tools; a short reference WAV plus its
  expected Whisper transcript for the audio-generation tools) captured once, by hand, against a real
  Azure resource, and checked into the repo next to the tool (same spirit as this repo's existing
  `personas/<id>/menu/**` fixtures, or `tests/conformance`'s own recorded HTTP fixtures where they
  exist) -- never regenerated automatically by CI, and never requiring live credentials to run.
  `benchmark_reasoning.py`'s own `--tools stub` / `--resume` flags and `smoke_realtime.py`'s existing
  "warn, never fail the deployment" design already lean this direction for their Python twins today,
  which is a useful head start.
* **Parity, in this model, means "the fake client's fixture result flows through the port's
  request-building and response-handling logic identically to the Python twin's,"** not
  "the live Azure response is byte-identical between runs" (which isn't guaranteed even for two runs
  of the *same* Python script, since these are non-deterministic model calls). The output-parity test
  would therefore compare each side's *handling* of the same fixed, recorded input/response pair --
  the constructed request payload, and the pass/fail verdict or transformed output derived from the
  fixed response -- rather than attempting byte-identical live output the way the two deterministic
  JSON tools above do.
* **The real Azure SDK client implementation itself would stay untested by this repo's own test
  suite** (same as today: nothing in this repo's CI exercises a live Azure OpenAI/Search/Speech call),
  with its correctness instead covered by whatever manual or live-smoke verification already gates a
  real deployment (`smoke_realtime.py`'s own `postdeploy` hook, for instance) -- a port would not
  change that boundary, only move where the *deterministic* parts of the logic live.
* `e2e_order_resume.py` is a different shape again (a Playwright-driven browser, not an Azure SDK
  call) and needs its own design once/if a C# browser-automation story (e.g. Playwright for .NET) is
  justified; the recorded-fixture approach above does not directly apply to it.

**Status: this client-seam/recorded-fixture shape is still a sketch for every tool listed above**
(`benchmark_reasoning.py`, `smoke_realtime.py`, `generate_apology_clips.py`,
`generate_demo_guest_voice.py`, both ingestion notebooks) -- none of them has been ported. The "Batch
2 port" section above DOES implement this batch's design for `app/backend/setup_search_index.py`'s
request-building half, but via a variant technique (HTTP-transport capture of the real Python twin's
actual request bodies, since those are fully deterministic) rather than this note's
client-seam-plus-recorded-response-fake shape (needed for tools whose *responses*, not just requests,
are the non-deterministic part being handled). A future port of any of the tools above should follow
this note's shape, not the request-capture variant, since their Azure calls' response content -- not
just their request content -- is what the port's logic actually has to handle correctly.
