#!/usr/bin/env python
"""CI-only check: compare the checked-in rebrand_baseline.yaml against the PR's base branch
and fail on any `max` increase, or brand-new (file, brand) entry, that lacks a valid
`increase_reason`.

This is the "make raises visible even if someone edits the YAML by hand" check Rick
recommended (PR #101 round 3) as a belt-and-suspenders companion to
regenerate_rebrand_baseline.py's --allow-increase refusal: the regen script only protects
people who run it -- nothing stops a hand-edit of the YAML that skips it entirely. This
script closes that gap in CI by diffing against the base branch's committed copy via git
history, rather than trusting the local working tree's own (unverifiable) history.

Usage (see .github/workflows/conformance.yml, python-tests job):

    python app/backend/tests/check_rebrand_baseline_against_base.py <path-to-base-baseline.yaml>

Exits 0 (and prints nothing alarming) if:
  * the base baseline can't be found/parsed (e.g. the base branch predates this file) -- there
    is nothing meaningful to ratchet against yet, so this is a no-op rather than a false
    failure that would block every PR forever; or
  * every `max` increase / brand-new entry relative to the base branch already carries a
    validly-formatted `increase_reason` (matching '#123', same as `issue`).

Exits 1 and lists every offending entry otherwise.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rebrand_scan import BASELINE_PATH, _load_baseline  # noqa: E402

ISSUE_REF_RE = re.compile(r"#\d+")


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("usage: check_rebrand_baseline_against_base.py <path-to-base-baseline.yaml>")
        return 2
    base_path = Path(argv[0])

    head = _load_baseline(BASELINE_PATH)
    try:
        base = _load_baseline(base_path)
    except (FileNotFoundError, ValueError):
        print(f"Base baseline at {base_path} not found/parseable -- skipping ratchet check.")
        return 0

    problems: list[str] = []
    for key, entry in sorted(head.items()):
        prior = base.get(key)
        if prior is None:
            if not ISSUE_REF_RE.fullmatch(entry.increase_reason or ""):
                problems.append(
                    f"NEW   {entry.file} [{entry.brand}] = {entry.max} "
                    f"(no valid increase_reason vs base branch)"
                )
            continue
        if entry.max > prior.max and not ISSUE_REF_RE.fullmatch(entry.increase_reason or ""):
            problems.append(
                f"RAISE {entry.file} [{entry.brand}] {prior.max} -> {entry.max} "
                f"(no valid increase_reason vs base branch)"
            )

    if problems:
        print(
            f"{len(problems)} rebrand_baseline.yaml change(s) vs the base branch lack a valid "
            f"`increase_reason` (e.g. '#123'):"
        )
        for p in problems:
            print(f"  {p}")
        return 1

    print(f"OK: no unjustified baseline raises vs the base branch ({len(head)} head entries checked).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
