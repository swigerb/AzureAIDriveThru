#!/usr/bin/env python
"""Regenerate app/backend/tests/rebrand_baseline.yaml from the actual repo state.

Run from anywhere (paths are resolved relative to this file):

    python app/backend/tests/regenerate_rebrand_baseline.py

Rescans every shared-code brand-word reference using the exact same rules
test_rebrand_verification.py enforces (see rebrand_scan.py), then rewrites
rebrand_baseline.yaml so every (file, brand) baseline `max` matches today's
real line-hit count. Review the diff before committing:

  * Counts should only ever go DOWN, except when the change you just made
    legitimately added a new, intentional shared-code reference.
  * A brand-new entry appearing means a previously-unbaselined file now has a
    brand-word hit -- double check that's expected before committing it.
  * An entry disappearing means its count dropped to 0 (the leftover was
    fully cleaned up) -- nothing further to do.
  * This script does not invent `issue` references for brand-new entries: it
    carries over the issue/reason from the previous baseline entry for that
    (file, brand) pair when one existed, and otherwise leaves `issue` as
    "#TODO" and `reason` blank so the diff review can't miss it -- fill in a
    real issue reference before committing.
"""
from __future__ import annotations

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


def main() -> int:
    counts = _count_brand_occurrences()
    try:
        previous = _load_baseline()
    except (FileNotFoundError, ValueError):
        previous = {}

    entries: list[BaselineEntry] = []
    for (rel_posix, brand), count in sorted(counts.items()):
        if count <= 0 or not _needs_baseline_entry(rel_posix, brand):
            continue
        prior = previous.get((rel_posix, brand))
        entries.append(
            BaselineEntry(
                file=rel_posix,
                brand=brand,
                max=count,
                issue=prior.issue if prior else "#TODO",
                reason=prior.reason if prior else "",
            )
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
