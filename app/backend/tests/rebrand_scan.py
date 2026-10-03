"""Shared brand-word scanning/classification logic for the #76 rebrand guard.

This module is imported by both ``test_rebrand_verification.py`` (the pytest
guard) and ``regenerate_rebrand_baseline.py`` (the standalone script that
(re)writes ``rebrand_baseline.yaml``), so the two never drift: the script
counts brand-word hits using the exact same file-collection, exclusion, and
classification rules the test enforces.

Rick's #101 review round-2 replaced the old directory-prefix ALLOWLIST with:

  1. Two DIRECTORY_EXCEPTIONS (generated/golden content, no per-file ratchet
     needed): ``app/backend/static/`` (gitignored frontend build output) and
     ``tests/conformance/testdata/`` (golden data sourced from Sonic, for the
     original flat golden files that predate any per-persona subfolder).
  2. A checked-in per-file BASELINE (``rebrand_baseline.yaml``) for every
     other shared-code brand-word reference, keyed by (file, brand), each
     entry recording the exact current line-hit count and an issue reference.
     The count must match EXACTLY: a rise means a new/uncontrolled reference
     snuck in; a silent drop means the fix landed but nobody ratcheted the
     baseline down (see docs/persona-architecture.md section 8).

Everywhere else (a persona's own pack, or the cross-brand docs that compare
all three brands by design) a brand word is classified without reference to
the baseline at all -- see ``_classify_hit``. A pack's own per-persona
conformance testdata subfolder (``tests/conformance/testdata/personas/<id>/**``,
#78/#79) is classified the same way as the pack's own ``personas/<id>/**`` --
see ``_conformance_testdata_pack_id`` -- rather than being limited to the
flat directory exception's sonic-only rule.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

import yaml

# ── Paths ────────────────────────────────────────────────────────────────
PROJECT_ROOT = Path(__file__).resolve().parents[3]  # SonicAIDriveThru/
BASELINE_PATH = Path(__file__).resolve().parent / "rebrand_baseline.yaml"

# File extensions/filenames to scan -- kept identical to the terminology
# scanner in test_rebrand_verification.py so "what counts as source" never
# differs between the two guards.
SCAN_EXTENSIONS = {
    ".py", ".ts", ".tsx", ".js", ".jsx",
    ".html", ".css", ".json", ".md",
    ".yaml", ".yml", ".bicep", ".env-sample",
    ".sh",
    # #105/#108: the C# backend (app/backend-dotnet/**) and its conformance harness
    # (tests/conformance/**) were never scanned for brand words at all -- ".cs" closes that
    # gap. bin/obj (below) are excluded so build output isn't double-counted/churned.
    ".cs",
}
SCAN_FILENAMES = {"Dockerfile"}

# Directories every scan skips regardless of which check is running: VCS/tooling/dependency
# noise, never source content.
BASE_EXCLUDED_DIRS = {
    ".git", "node_modules", "__pycache__",
    ".venv", "venv", "env",
    ".squad",
    # #105/#108: MSBuild output for the newly-scanned .cs files -- generated, not hand-edited,
    # and would otherwise churn the baseline on every build (same rationale as the
    # app/backend/static/ DIRECTORY_EXCEPTION already gives npm's build output).
    "bin", "obj",
}

# Canonical brand -> matching pattern. A pack under personas/<id>/** may only contain the
# word matching its own id; every other brand word there is a cross-brand leak, same as in
# any other shared file.
BRAND_PATTERNS: dict[str, re.Pattern[str]] = {
    "sonic": re.compile(r"\bsonic\b", re.IGNORECASE),
    "mcdonalds": re.compile(r"\bmcdonald'?s?\b", re.IGNORECASE),
    "dunkin": re.compile(r"\bdunkin'?\b", re.IGNORECASE),
}

# Brand-word scan intentionally does NOT exclude "adr" or "personas" the way the terminology
# scan does -- both need to actively participate so the cross-brand-doc and
# persona-pack-ownership rules below can classify each hit.
BRAND_EXCLUDED_DIRS = BASE_EXCLUDED_DIRS
BRAND_EXCLUDED_FILES = {
    "voice_rag_README.md",
    "test_rebrand_verification.py",
    "rebrand_scan.py",
    "regenerate_rebrand_baseline.py",
    # #105: this CI ratchet script's own docstrings/comments illustrate its rules with a real
    # brand word (e.g. explaining the #109 foreign-brand bug it closes) -- tooling about the
    # guard, not shared product code, same rationale as excluding rebrand_scan.py itself.
    "check_rebrand_baseline_against_base.py",
    # Unit tests for the regen script (PR #101 round 3) use "sonic" purely as fixture/example
    # data for BaselineEntry objects, not a real shared-code brand reference -- same rationale
    # as excluding test_rebrand_verification.py itself.
    "test_regenerate_rebrand_baseline.py",
    "test_check_rebrand_baseline_against_base.py",
    # The baseline itself necessarily lists every brand word it tracks (as data, in `file`/
    # `brand` fields), which would otherwise make it its own violation.
    "rebrand_baseline.yaml",
}


def _should_scan(path: Path, excluded_dirs: set[str], excluded_files: set[str]) -> bool:
    """Return True if *path* should be included in a scan using the given exclusion sets."""
    if path.suffix not in SCAN_EXTENSIONS and path.name not in SCAN_FILENAMES:
        return False
    if path.name in excluded_files:
        return False
    parts = path.relative_to(PROJECT_ROOT).parts
    if any(part in excluded_dirs for part in parts):
        return False
    return True


def _collect_source_files(excluded_dirs: set[str], excluded_files: set[str]) -> list[Path]:
    """Gather every scannable source file under PROJECT_ROOT for the given exclusion sets."""
    return sorted(
        p for p in PROJECT_ROOT.rglob("*")
        if p.is_file() and _should_scan(p, excluded_dirs, excluded_files)
    )


def _relative_posix(path: Path) -> str:
    return path.relative_to(PROJECT_ROOT).as_posix()


def _persona_pack_id(rel_posix: str):
    """Returns the persona id if rel_posix is under personas/<id>/**, else None.

    Requires a directory segment after the id (len(parts) >= 3): a file sitting directly under
    personas/ itself (e.g. personas/persona.schema.json, a shared JSON Schema) is SHARED code,
    not inside any particular pack, so it must not be treated as if it belonged to a pack named
    after that filename."""
    parts = rel_posix.split("/")
    return parts[1] if len(parts) >= 3 and parts[0] == "personas" else None


def _is_cross_brand_doc(rel_posix: str) -> bool:
    """Docs that compare personas by design may name every brand."""
    return (
        rel_posix in {
            "README.md",
            "docs/DEMO_SCRIPT.md",
            "docs/persona-architecture.md",
        }
        or rel_posix.startswith("docs/adr/")
    )


_CONFORMANCE_TESTDATA_PERSONAS_PREFIX = "tests/conformance/testdata/personas/"


def _conformance_testdata_pack_id(rel_posix: str):
    """Returns the persona id if rel_posix is under
    tests/conformance/testdata/personas/<id>/**, else None.

    Mirrors ``_persona_pack_id``'s ownership rule for personas/<id>/** itself: a pack's own
    per-persona golden conformance fixtures (#78/#79 -- e.g.
    tests/conformance/testdata/personas/<id>/smoke.json) are sourced from
    that pack's own menu/pricing and so may say its own brand, same as the pack that sourced
    them. Checked BEFORE the generic tests/conformance/testdata/ DIRECTORY_EXCEPTIONS entry
    (which only ever rescues 'sonic', for the original flat golden files that predate any
    per-persona subfolder) so a new pack's own subfolder isn't limited to the sonic-only rule.

    Requires a "/" in the remainder: a file sitting directly under
    tests/conformance/testdata/personas/ itself is SHARED code, not inside any particular
    pack's subfolder, same rationale as ``_persona_pack_id``'s directory-segment requirement."""
    if not rel_posix.startswith(_CONFORMANCE_TESTDATA_PERSONAS_PREFIX):
        return None
    remainder = rel_posix[len(_CONFORMANCE_TESTDATA_PERSONAS_PREFIX):]
    parts = remainder.split("/")
    return parts[0] if len(parts) >= 2 and parts[0] else None


# ── Directory exceptions (generated/golden content; no per-file ratchet) ─────────────────

@dataclass(frozen=True)
class DirectoryException:
    """A directory whose entire contents are generated or golden-sourced, so an individual
    per-file line-count ratchet would just churn on every build/fixture-refresh. Still
    requires an issue reference -- nothing here is a silent, untracked permanent exception."""

    prefix: str  # POSIX-style, repo-root-relative, MUST end with "/"
    brands: frozenset
    issue: str
    reason: str = ""


# Exactly the two locations Rick's review named -- deliberately NOT extensible via the
# baseline file, so a new directory-wide exception can't be snuck in without a code review
# of this literal list. Any other shared-code brand reference must go through BASELINE.
DIRECTORY_EXCEPTIONS: list[DirectoryException] = [
    DirectoryException(
        "app/backend/static/", frozenset({"sonic"}), "#80",
        "Gitignored built-frontend output (npm run build's outDir) -- mirrors the frontend's "
        "own pending persona theming (#80); nothing here is hand-edited, so a per-file "
        "baseline count would just churn on every build.",
    ),
    DirectoryException(
        "tests/conformance/testdata/", frozenset({"sonic"}), "#78",
        "Golden order-pricing/menu-category datasets sourced from Sonic's own menu (design doc "
        "section 8); a per-file baseline count would just churn as golden fixtures are "
        "added/edited. Only covers files directly here, not a personas/<id>/ subfolder -- see "
        "_conformance_testdata_pack_id, which lets each pack's own per-persona testdata "
        "subfolder (tests/conformance/testdata/personas/<id>/**, #78/#79) say its own brand "
        "the same way personas/<id>/** does, instead of being limited to this sonic-only rule.",
    ),
]


def _directory_exception_for(rel_posix: str) -> DirectoryException | None:
    for exc in DIRECTORY_EXCEPTIONS:
        if rel_posix.startswith(exc.prefix):
            return exc
    return None


# ── Baseline (checked-in ratchet for everything else) ─────────────────────────────────────

@dataclass(frozen=True)
class BaselineEntry:
    """One checked-in (file, brand) exception with the exact current line-hit count.

    `max` MUST equal the real current count -- test_brand_word_counts_match_the_baseline
    fails the moment it doesn't, in either direction (see this module's docstring). `issue`
    MUST be a GitHub issue reference like "#74", validated by
    test_every_baseline_entry_has_a_valid_issue_reference. `increase_reason` is set only when
    regenerate_rebrand_baseline.py raised this entry's `max` (or added it as brand-new) via
    ``--allow-increase``; when present it MUST also be a GitHub issue reference like "#123",
    validated by test_baseline_entries_with_an_increase_reason_have_a_valid_format (PR #101
    round 3, Rick's review: the regen script previously rewrote every `max` to today's count
    unconditionally, so a brand-word increase could be laundered by re-running it).
    """

    file: str  # POSIX-style, repo-root-relative, exact file (no trailing slash / no globs)
    brand: str
    max: int
    issue: str
    reason: str = field(default="")
    increase_reason: str = field(default="")


def _load_baseline(path: Path = BASELINE_PATH) -> dict[tuple[str, str], BaselineEntry]:
    """Load rebrand_baseline.yaml into a {(file, brand): BaselineEntry} map.

    Fails closed: a missing/malformed file raises rather than silently scanning as if the
    baseline were empty (which would make every existing hit look like a brand-new,
    unbaselined violation instead of surfacing the real "baseline file is broken" problem).
    """
    raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict) or not isinstance(raw.get("entries"), list):
        raise ValueError(
            f"{path} does not have the expected shape ({{'entries': [...]}}) -- "
            f"regenerate it with regenerate_rebrand_baseline.py"
        )
    result: dict[tuple[str, str], BaselineEntry] = {}
    for item in raw["entries"]:
        entry = BaselineEntry(
            file=item["file"],
            brand=item["brand"],
            max=int(item["max"]),
            issue=item.get("issue", ""),
            reason=item.get("reason", ""),
            increase_reason=item.get("increase_reason", ""),
        )
        key = (entry.file, entry.brand)
        if key in result:
            raise ValueError(f"{path} has a duplicate entry for {key}")
        result[key] = entry
    return result


def _dump_baseline(entries: list[BaselineEntry], path: Path = BASELINE_PATH) -> None:
    """Write entries back out as YAML, sorted for a stable/reviewable diff."""
    ordered = sorted(entries, key=lambda e: (e.file, e.brand))
    payload = {
        "entries": [
            {
                "file": e.file,
                "brand": e.brand,
                "max": e.max,
                "issue": e.issue,
                "reason": e.reason,
                "increase_reason": e.increase_reason,
            }
            for e in ordered
        ],
    }
    header = (
        "# Rebrand baseline (#76 round 3) -- checked-in ratchet of every shared-code brand-word\n"
        "# reference outside a persona pack / the cross-brand docs / the two directory\n"
        "# exceptions in rebrand_scan.py. Lower after a fix by running:\n"
        "#   python app/backend/tests/regenerate_rebrand_baseline.py\n"
        "# from the repo root -- by default this ONLY lowers counts (and drops entries that hit\n"
        "# 0); it refuses (exit 1, writes nothing) if any count would rise or a new (file, brand)\n"
        "# entry would be added. A genuine raise/new entry requires:\n"
        "#   python app/backend/tests/regenerate_rebrand_baseline.py --allow-increase "
        "--increase-reason '#123'\n"
        "# where '#123' names the issue that justifies the increase -- it is stamped onto every\n"
        "# raised/new entry as `increase_reason` (validated the same way as `issue`).\n"
    )
    with path.open("w", encoding="utf-8", newline="\n") as f:
        f.write(header)
        f.write(yaml.safe_dump(payload, sort_keys=False, allow_unicode=True))


def _count_brand_occurrences() -> dict[tuple[str, str], int]:
    """Returns {(rel_posix, brand): number_of_matching_lines} for every source file."""
    files = _collect_source_files(BRAND_EXCLUDED_DIRS, BRAND_EXCLUDED_FILES)
    counts: dict[tuple[str, str], int] = {}
    for filepath in files:
        rel_posix = _relative_posix(filepath)
        try:
            lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
        except Exception:
            continue
        for line in lines:
            for brand, pattern in BRAND_PATTERNS.items():
                if pattern.search(line):
                    key = (rel_posix, brand)
                    counts[key] = counts.get(key, 0) + 1
    return counts


def _classify_hit(
    rel_posix: str,
    brand: str,
    count: int,
    baseline: dict[tuple[str, str], BaselineEntry],
) -> str | None:
    """Returns None if `count` occurrences of `brand` at `rel_posix` are allowed, else a
    human-readable reason they are not. `count` is the number of matching lines in
    `rel_posix` for `brand` today (0 if the file/brand no longer has any hit)."""
    if _is_cross_brand_doc(rel_posix):
        return None

    pack_id = _persona_pack_id(rel_posix)
    if pack_id is not None:
        if brand == pack_id:
            return None
        return (
            f"'{brand}' appears inside the '{pack_id}' persona pack (personas/{pack_id}/**) -- "
            f"a persona pack must only reference its own brand"
        )

    testdata_pack_id = _conformance_testdata_pack_id(rel_posix)
    if testdata_pack_id is not None:
        if brand == testdata_pack_id:
            return None
        return (
            f"'{brand}' appears inside the '{testdata_pack_id}' per-persona conformance "
            f"testdata ({_CONFORMANCE_TESTDATA_PERSONAS_PREFIX}{testdata_pack_id}/**) -- it "
            f"may only reference its own brand, same as personas/{testdata_pack_id}/**"
        )

    exc = _directory_exception_for(rel_posix)
    if exc is not None:
        if brand in exc.brands:
            return None
        return (
            f"'{brand}' appears under the directory exception '{exc.prefix}' (issue {exc.issue}), "
            f"which only rescues {sorted(exc.brands)} -- add/adjust the exception or fix the "
            f"reference"
        )

    entry = baseline.get((rel_posix, brand))
    if entry is None:
        if count > 0:
            return (
                f"'{brand}' appears {count} time(s) in shared code with no BASELINE entry "
                f"covering it -- either this is a genuine leftover to fix, or (if pending "
                f"migration) add a BASELINE entry to {BASELINE_PATH.name} with an issue "
                f"reference"
            )
        return None

    if count > entry.max:
        return (
            f"'{brand}' now appears {count} time(s) in {rel_posix}, above its BASELINE max of "
            f"{entry.max} (issue {entry.issue}) -- fix the new reference(s), or if intentional, "
            f"raise the baseline max in {BASELINE_PATH.name}"
        )
    if count < entry.max:
        return (
            f"'{brand}' now appears only {count} time(s) in {rel_posix}, below its BASELINE max "
            f"of {entry.max} (issue {entry.issue}) -- lower (or remove, if 0) the baseline entry "
            f"in {BASELINE_PATH.name} to ratchet it down to the new count"
        )
    return None
