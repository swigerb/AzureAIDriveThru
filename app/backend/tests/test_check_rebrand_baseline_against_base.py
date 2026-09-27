"""Unit tests for check_rebrand_baseline_against_base.py (PR #101 round 3, optional CI
ratchet Rick recommended alongside the required regen-script fix).

The regen script's --allow-increase refusal only protects people who actually run it --
nothing stops a hand-edit of rebrand_baseline.yaml that skips it entirely. This CI-only
script closes that gap by diffing the checked-in baseline against a base-branch copy of the
file and failing on any `max` increase / brand-new entry that lacks a valid `increase_reason`.

These tests call `main()` directly against throwaway temp files (never the real, checked-in
rebrand_baseline.yaml) with `BASELINE_PATH` patched, so they never touch real source files.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_rebrand_baseline_against_base as checker  # noqa: E402
from rebrand_scan import BaselineEntry, _dump_baseline  # noqa: E402


class TestCheckRebrandBaselineAgainstBase(unittest.TestCase):
    def setUp(self):
        tmp = TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp_dir = Path(tmp.name)
        self.head_path = self.tmp_dir / "head.yaml"
        self.base_path = self.tmp_dir / "base.yaml"
        patcher = mock.patch.object(checker, "BASELINE_PATH", self.head_path)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_missing_base_baseline_skips_gracefully(self):
        """A base branch that predates rebrand_baseline.yaml (file doesn't exist there) is a
        no-op pass, not a false failure."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.head_path,
        )
        exit_code = checker.main([str(self.tmp_dir / "does-not-exist.yaml")])
        self.assertEqual(exit_code, 0)

    def test_raise_without_increase_reason_fails(self):
        """A max increase vs the base branch with no valid increase_reason fails."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.head_path,
        )
        exit_code = checker.main([str(self.base_path)])
        self.assertEqual(exit_code, 1)

    def test_raise_with_valid_increase_reason_passes(self):
        """The same raise passes once a validly-formatted increase_reason is present."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#123",
                )
            ],
            self.head_path,
        )
        exit_code = checker.main([str(self.base_path)])
        self.assertEqual(exit_code, 0)

    def test_new_entry_without_increase_reason_fails(self):
        """A brand-new (file, brand) pair not present on the base branch fails without a
        valid increase_reason."""
        _dump_baseline([], self.base_path)
        _dump_baseline(
            [BaselineEntry(file="app/backend/newfile.py", brand="sonic", max=2, issue="#TODO")],
            self.head_path,
        )
        exit_code = checker.main([str(self.base_path)])
        self.assertEqual(exit_code, 1)

    def test_lower_or_unchanged_never_needs_a_reason(self):
        """A lower (or unchanged) count vs the base branch never needs increase_reason."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.head_path,
        )
        exit_code = checker.main([str(self.base_path)])
        self.assertEqual(exit_code, 0)


if __name__ == "__main__":
    unittest.main()
