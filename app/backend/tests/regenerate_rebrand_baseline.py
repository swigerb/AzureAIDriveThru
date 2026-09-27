#!/usr/bin/env python
"""Regenerate app/backend/tests/rebrand_baseline.yaml from the actual repo state.

Run from anywhere (paths are resolved relative to this file):

    python app/backend/tests/regenerate_rebrand_baseline.py

Rescans every shared-code brand-word reference using the exact same rules
test_rebrand_verification.py enforces (see rebrand_scan.py).

By DEFAULT this script is lower-only / ratchet-safe:

  * A count lower than the checked-in `max` lowers it.
  * A count that drops to 0 removes the entry entirely.
  * Any RAISE (a count higher than the checked-in `max`), or any BRAND-NEW
    (file, brand) entry, is REFUSED: the script prints every offending entry,
    exits 1, and writes NOTHING -- the baseline file on disk is left exactly
    as it was.

This closes the round-2->round-3 gap Rick's review flagged (PR #101): the
old version of this script rewrote every entry's `max` to today's count
unconditionally, so "add a brand word, run the regen, it passes" laundered a
real increase through what looked like a routine lower/refresh, with only a
one-line YAML diff to show for it.

A genuine increase (or a genuine brand-new shared-code reference) must be
explicit and traceable to a review decision:

    python app/backend/tests/regenerate_rebrand_baseline.py \\
        --allow-increase --increase-reason '#123'

`--increase-reason` must look like an issue reference (e.g. '#123') that
names the issue justifying the raise. It is stamped onto every raised/new
entry as that entry's `increase_reason` field, which
test_rebrand_verification.py validates the same way it validates `issue`
whenever the field is non-empty. `--allow-increase` without
`--increase-reason` (or with a malformed one) is refused the same way a
plain raise is: nothing is written.

Brand-new entries still get `issue` stamped as "#TODO" (this script does not
invent real issue references for them) -- review the diff, fill in a real
issue reference, and only then commit.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rebrand_scan import (  # noqa: E402
    BASELINE_PATH,
    BaselineEntry,
    _count_brand_occurrences,
    _directory_exception_for,
    _dump_baseline,
    _is_cross_brand_doc,
    _load_baseline,
    _persona_pack_id,
)

ISSUE_REF_RE = re.compile(r"#\d+")


def _needs_baseline_entry(rel_posix: str, brand: str) -> bool:
    """True if a (rel_posix, brand) hit is shared code that needs a BASELINE entry (i.e. not
    a persona pack's own brand, not a cross-brand doc, not a directory exception)."""
    if _is_cross_brand_doc(rel_posix):
        return False
    pack_id = _persona_pack_id(rel_posix)
    if pack_id is not None and brand == pack_id:
        return False
    if pack_id is not None and brand != pack_id:
        # A foreign brand inside a persona pack is never baseline-able -- it's always a bug.
        return False
    if _directory_exception_for(rel_posix) is not None:
        return False
    return True


def _plan(
    counts: dict[tuple[str, str], int],
    previous: dict[tuple[str, str], BaselineEntry],
    *,
    allow_increase: bool,
    increase_reason: str | None,
) -> tuple[list[BaselineEntry], list[str]]:
    """Compute the entries this run would write, plus a list of human-readable descriptions
    of any raise/brand-new entry that ``allow_increase`` didn't authorize.

    When ``allow_increase`` is False, every raise/new entry is reported in the second element
    and simply skipped in the first (the caller must treat a non-empty second element as "do
    not write anything"). When ``allow_increase`` is True, raises/new entries ARE included in
    the first element (stamped with ``increase_reason``), and the second element is always
    empty -- the caller is responsible for checking ``increase_reason`` is present/valid
    *before* calling with ``allow_increase=True``.
    """
    entries: list[BaselineEntry] = []
    blocked: list[str] = []
    for (rel_posix, brand), count in sorted(counts.items()):
        if count <= 0 or not _needs_baseline_entry(rel_posix, brand):
            continue
        prior = previous.get((rel_posix, brand))

        if prior is None:
            if not allow_increase:
                blocked.append(f"NEW   {rel_posix} [{brand}] = {count}")
                continue
            entries.append(
                BaselineEntry(
                    file=rel_posix,
                    brand=brand,
                    max=count,
                    issue="#TODO",
                    reason="",
                    increase_reason=increase_reason or "",
                )
            )
            continue

        if count > prior.max:
            if not allow_increase:
                blocked.append(f"RAISE {rel_posix} [{brand}] {prior.max} -> {count}")
                continue
            entries.append(
                BaselineEntry(
                    file=rel_posix,
                    brand=brand,
                    max=count,
                    issue=prior.issue,
                    reason=prior.reason,
                    increase_reason=increase_reason or "",
                )
            )
            continue

        # count <= prior.max: unchanged or a lower -- always allowed, never needs a reason.
        entries.append(
            BaselineEntry(
                file=rel_posix,
                brand=brand,
                max=count,
                issue=prior.issue,
                reason=prior.reason,
                # A genuine lower supersedes whatever justified the old (higher) max; only
                # keep increase_reason when the count truly didn't change.
                increase_reason=prior.increase_reason if count == prior.max else "",
            )
        )

    return entries, blocked


def _parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--allow-increase",
        action="store_true",
        help="Permit writing a raised/new baseline entry (requires --increase-reason).",
    )
    parser.add_argument(
        "--increase-reason",
        default=None,
        metavar="#123",
        help="Issue reference justifying every raised/new entry in this run (e.g. '#123'). "
        "Required whenever --allow-increase actually needs to authorize something.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)

    counts = _count_brand_occurrences()
    try:
        previous = _load_baseline()
    except (FileNotFoundError, ValueError):
        previous = {}

    entries, blocked = _plan(counts, previous, allow_increase=False, increase_reason=None)

    if blocked:
        if not args.allow_increase:
            print(
                f"Refusing to regenerate: {len(blocked)} entry(ies) would rise or be newly "
                f"added (baseline NOT written):"
            )
            for line in blocked:
                print(f"  {line}")
            print(
                "If this is intentional, re-run with --allow-increase and "
                "--increase-reason '#<issue>' naming the issue that justifies it."
            )
            return 1

        if not args.increase_reason:
            print(
                f"--allow-increase was given but --increase-reason was not -- {len(blocked)} "
                f"entry(ies) need one naming the issue that justifies the increase (baseline "
                f"NOT written):"
            )
            for line in blocked:
                print(f"  {line}")
            return 1

        if not ISSUE_REF_RE.fullmatch(args.increase_reason):
            print(
                f"--increase-reason must look like an issue reference (e.g. '#123'), got "
                f"{args.increase_reason!r} (baseline NOT written)."
            )
            return 1

        entries, blocked = _plan(
            counts, previous, allow_increase=True, increase_reason=args.increase_reason
        )

    _dump_baseline(entries, BASELINE_PATH)

    todo = [e for e in entries if e.issue == "#TODO"]
    print(f"Wrote {len(entries)} baseline entries to {BASELINE_PATH}")
    if todo:
        print(f"  {len(todo)} brand-new entry(ies) need a real issue reference (currently #TODO):")
        for e in todo:
            print(f"    {e.file} [{e.brand}] = {e.max}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
