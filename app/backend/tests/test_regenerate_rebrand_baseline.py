"""Unit tests for regenerate_rebrand_baseline.py (PR #101 round 3, Rick's review).

Round 2's version of this script rewrote every existing entry's `max` to today's real count
unconditionally, so "add a Sonic word to app.py, the guard test fails, re-run the regen
script, it passes" laundered a real brand-word increase through what looked like a routine
refresh -- the guard test couldn't tell, because it was comparing against the file the
regen script had just rewritten.

These tests exercise the script's `main()` end-to-end against a throwaway baseline file
(never the real, checked-in rebrand_baseline.yaml) with `_count_brand_occurrences` and
`_load_baseline` patched to synthetic data, so they run in isolation from the actual repo
scan and never touch real source files:

  * default run only ever lowers a count or drops an entry that hit 0;
  * a raise is refused: exit 1, printed, and the file on disk is byte-for-byte unchanged;
  * a brand-new (file, brand) entry is refused the same way;
  * `--allow-increase` without `--increase-reason` still refuses (and doesn't write);
  * `--allow-increase --increase-reason '#123'` succeeds and stamps `increase_reason`.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import regenerate_rebrand_baseline as regen  # noqa: E402
from rebrand_scan import BaselineEntry, _dump_baseline, _load_baseline  # noqa: E402


class TestRegenerateRebrandBaseline(unittest.TestCase):
    def setUp(self):
        tmp = TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.baseline_path = Path(tmp.name) / "rebrand_baseline.yaml"
        patcher = mock.patch.object(regen, "BASELINE_PATH", self.baseline_path)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _seed(self, entries: list[BaselineEntry]) -> None:
        """Write an initial baseline file to disk, standing in for a checked-in one."""
        _dump_baseline(entries, self.baseline_path)

    def _run(
        self,
        counts: dict[tuple[str, str], int],
        previous: dict[tuple[str, str], BaselineEntry],
        argv: list[str] | None = None,
    ) -> int:
        with (
            mock.patch.object(regen, "_count_brand_occurrences", return_value=counts),
            mock.patch.object(regen, "_load_baseline", return_value=previous),
        ):
            return regen.main(argv or [])

    def test_default_lowers_only(self):
        """A count strictly below its checked-in max is written as the new, lower max."""
        prior = BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")
        self._seed([prior])
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 3},
            previous={("app/backend/foo.py", "sonic"): prior},
        )
        self.assertEqual(exit_code, 0)
        written = _load_baseline(self.baseline_path)
        self.assertEqual(written[("app/backend/foo.py", "sonic")].max, 3)
        self.assertEqual(written[("app/backend/foo.py", "sonic")].issue, "#74")

    def test_default_drops_entries_that_hit_zero(self):
        """An entry with no remaining hits (absent from the rescanned counts) is removed."""
        prior = BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")
        self._seed([prior])
        exit_code = self._run(counts={}, previous={("app/backend/foo.py", "sonic"): prior})
        self.assertEqual(exit_code, 0)
        written = _load_baseline(self.baseline_path)
        self.assertNotIn(("app/backend/foo.py", "sonic"), written)

    def test_refuses_raise(self):
        """A count above its checked-in max is refused: exit 1, file unchanged."""
        prior = BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")
        self._seed([prior])
        before = self.baseline_path.read_bytes()
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 5},
            previous={("app/backend/foo.py", "sonic"): prior},
        )
        self.assertEqual(exit_code, 1)
        self.assertEqual(self.baseline_path.read_bytes(), before, "baseline must be unchanged")

    def test_refuses_new_entry(self):
        """A brand-new (file, brand) pair with no prior baseline entry is refused the same
        way: exit 1, file unchanged."""
        self._seed([])
        before = self.baseline_path.read_bytes()
        exit_code = self._run(
            counts={("app/backend/newfile.py", "sonic"): 2},
            previous={},
        )
        self.assertEqual(exit_code, 1)
        self.assertEqual(self.baseline_path.read_bytes(), before, "baseline must be unchanged")

    def test_allow_increase_without_reason_fails(self):
        """--allow-increase alone (no --increase-reason) still refuses to write a raise."""
        prior = BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")
        self._seed([prior])
        before = self.baseline_path.read_bytes()
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 5},
            previous={("app/backend/foo.py", "sonic"): prior},
            argv=["--allow-increase"],
        )
        self.assertEqual(exit_code, 1)
        self.assertEqual(self.baseline_path.read_bytes(), before, "baseline must be unchanged")

    def test_allow_increase_with_malformed_reason_fails(self):
        """A non-issue-shaped --increase-reason is rejected just like a missing one."""
        prior = BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")
        self._seed([prior])
        before = self.baseline_path.read_bytes()
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 5},
            previous={("app/backend/foo.py", "sonic"): prior},
            argv=["--allow-increase", "--increase-reason", "because I said so"],
        )
        self.assertEqual(exit_code, 1)
        self.assertEqual(self.baseline_path.read_bytes(), before, "baseline must be unchanged")

    def test_allow_increase_with_reason_succeeds(self):
        """--allow-increase with a valid --increase-reason writes the raise and stamps
        increase_reason, carrying over the entry's existing `issue`."""
        prior = BaselineEntry(
            file="app/backend/foo.py", brand="sonic", max=3, issue="#74", reason="orig reason"
        )
        self._seed([prior])
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 5},
            previous={("app/backend/foo.py", "sonic"): prior},
            argv=["--allow-increase", "--increase-reason", "#123"],
        )
        self.assertEqual(exit_code, 0)
        written = _load_baseline(self.baseline_path)
        entry = written[("app/backend/foo.py", "sonic")]
        self.assertEqual(entry.max, 5)
        self.assertEqual(entry.increase_reason, "#123")
        self.assertEqual(entry.issue, "#74")
        self.assertEqual(entry.reason, "orig reason")

    def test_allow_increase_new_entry_still_flags_todo_issue(self):
        """A brand-new entry written via --allow-increase still gets issue="#TODO" (this
        script never invents a real issue reference) and the script still exits 1 to force a
        human to fill it in before committing -- --allow-increase only unblocks the *count*
        raise/new-entry refusal, not the separate "needs a real issue" requirement."""
        self._seed([])
        exit_code = self._run(
            counts={("app/backend/newfile.py", "sonic"): 2},
            previous={},
            argv=["--allow-increase", "--increase-reason", "#123"],
        )
        self.assertEqual(exit_code, 1)
        written = _load_baseline(self.baseline_path)
        entry = written[("app/backend/newfile.py", "sonic")]
        self.assertEqual(entry.max, 2)
        self.assertEqual(entry.issue, "#TODO")
        self.assertEqual(entry.increase_reason, "#123")

    def test_unchanged_count_preserves_prior_increase_reason(self):
        """A count that stays exactly at its checked-in max keeps whatever increase_reason it
        already had (nothing changed, so nothing needs re-justifying)."""
        prior = BaselineEntry(
            file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
            increase_reason="#123",
        )
        self._seed([prior])
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 5},
            previous={("app/backend/foo.py", "sonic"): prior},
        )
        self.assertEqual(exit_code, 0)
        written = _load_baseline(self.baseline_path)
        self.assertEqual(written[("app/backend/foo.py", "sonic")].increase_reason, "#123")

    def test_lower_clears_stale_increase_reason(self):
        """A genuine lower supersedes whatever justified the old (higher) max -- the stale
        increase_reason should not carry forward onto the new, lower entry."""
        prior = BaselineEntry(
            file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
            increase_reason="#123",
        )
        self._seed([prior])
        exit_code = self._run(
            counts={("app/backend/foo.py", "sonic"): 2},
            previous={("app/backend/foo.py", "sonic"): prior},
        )
        self.assertEqual(exit_code, 0)
        written = _load_baseline(self.baseline_path)
        self.assertEqual(written[("app/backend/foo.py", "sonic")].increase_reason, "")


if __name__ == "__main__":
    unittest.main()
