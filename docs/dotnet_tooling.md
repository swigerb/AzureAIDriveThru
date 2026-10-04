# .NET tooling inventory (`tools/dotnet`)

This document tracks every Python script and notebook outside `app/backend` (plus every `.ps1`/
`.sh` wrapper that shells out to Python), per issue #16 (S6, epic #6's "one contract, two
backends" decision extended to repo tooling). It exists for the same reason
`docs/dotnet_mapping.md` exists for the application backend: so a port has an obvious place to
record what moved, what didn't, and why, instead of that history living only in commit messages.

Per epic #6 and the issue #16 P1 update (ADR-001), this is a multi-wave effort. The original PR
(#224) was **wave 1**: the inventory below, a scaffold for C# tooling (`tools/dotnet/`), and ONE
representative, low-risk port end-to-end (`update_menu_sizes.py`) with an output-parity test against
its Python twin. **Batch 1** (this update) ports the inventory's own stated next candidate,
`extract_production_items.py` -- see "Batch 1 port" below. Every other script and notebook in the
inventory stays Python-only until a later batch/wave explicitly ports it (tracked in the Squad's wave
plan; see "What's next" for the remaining candidates' design). **The Python versions are not removed
or modified by this work**, and azd hooks / CI keep running the Python implementations by default.

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
launcher wrappers are in scope, below.

## Inventory: `.ps1`/`.sh` wrappers that shell out to Python

| File(s) | Wraps | azd hook | Proposed C# shape |
| --- | --- | --- | --- |
| `scripts/setup_search_index.ps1`, `scripts/setup_search_index.sh` | `app/backend/setup_search_index.py` (builds/refreshes the Azure AI Search index from the production menu export) | **azd `postprovision` hook** (after `postprovision_auth`/`write_env`) | Not ported (wraps an Azure-dependent, in-scope-elsewhere script); stays Python. A C# equivalent is only relevant once/if `app/backend/setup_search_index.py` itself is ported, which is outside this issue's `app/backend`-excluded scope. |
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

* **`"price": true`/`"price": false` (a JSON boolean, not a number).** Python's
  `product.get("price", 0.0)` would simply return the C `bool` (Python's `bool` is a subtype of
  `int`, so it would be printed as `1.0`/`0.0` after `float()`-style formatting). The C# port's
  `GetDoubleOrDefault` treats a boolean JSON value as absent and falls back to the default instead.
* **Duplicate keys within the same JSON object** (e.g. two `"a"` entries inside one
  `relatedProducts.alternatives` or `productGroups` object). Python's `json.load` silently keeps
  only the *last* occurrence; `System.Text.Json`'s `JsonElement.GetProperty`/enumeration semantics
  are not guaranteed to match that exact "last wins" rule for every malformed-duplicate shape.
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

## `TOOLING_IMPL` (future, not wired in this PR)

Issue #16's body describes a future `TOOLING_IMPL=python|dotnet` environment variable so azd hooks
could eventually choose which implementation to run, defaulting to `python`. **This PR does not
introduce that variable or any hook wiring for it** -- per this PR's explicit scope, azd hooks and
CI defaults stay on the Python implementations unconditionally. A later wave, once more of the
inventory above is ported, is the right place to introduce `TOOLING_IMPL` for real.

## What's next

With both no-Azure candidates in the inventory (`update_menu_sizes.py`, `extract_production_items.py`)
now ported, every remaining script/notebook in the inventory needs a **live** Azure OpenAI, Azure AI
Search, or Azure Speech dependency, or (for `e2e_order_resume.py`) a real browser engine -- none of
them can be ported and tested the way this doc's two batches were (a deterministic, no-network,
no-subprocess transform/report proven byte- or stdout-identical against real fixture data). Porting
any of them for real is deferred until there is a concrete reason to run them from C# rather than
Python; this section instead sketches how each *would* be ported and tested without ever touching a
live Azure resource, so a future wave has a starting design rather than a blank page.

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

None of this is implemented in this batch -- it is a design note only, so a later wave has a starting
point instead of re-deriving this shape from scratch.
