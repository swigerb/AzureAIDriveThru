"""Unit tests for check_rebrand_baseline_against_base.py (issue #105, hardening the ratchet
Rick recommended in PR #101 round 3).

These tests call `main()` directly against throwaway temp files (never the real, checked-in
rebrand_baseline.yaml) with `BASELINE_PATH` patched, so they never touch real source files or
the network -- every GitHub API lookup is mocked via `check_issue_is_open`.
"""
from __future__ import annotations

import sys
import unittest
from io import StringIO
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

        # None of these tests want to hit the real network or need real env vars unless a
        # specific test opts in -- default to a clean slate every time.
        env_patcher = mock.patch.dict(
            checker.os.environ,
            {
                checker.REBRAND_PR_ISSUES_ENV: "",
                checker.REBRAND_PR_CLOSES_ENV: "",
                checker.REQUIRE_ISSUE_API_ENV: "",
                checker.GITHUB_TOKEN_ENV: "",
                checker.GITHUB_REPOSITORY_ENV: "",
            },
            clear=False,
        )
        env_patcher.start()
        self.addCleanup(env_patcher.stop)

        # Default: nobody asked for a real API check -- always mock it open unless a test
        # says otherwise, so tests that don't care about item 2 aren't coupled to it.
        api_patcher = mock.patch.object(checker, "check_issue_is_open", return_value=None)
        self.api_mock = api_patcher.start()
        self.addCleanup(api_patcher.stop)

    def _run(self, argv=None) -> tuple[int, str]:
        buf = StringIO()
        with mock.patch("sys.stdout", buf):
            exit_code = checker.main(argv if argv is not None else [str(self.base_path)])
        return exit_code, buf.getvalue()

    # ── Pre-existing behaviour (still must hold) ──────────────────────────

    def test_missing_base_baseline_skips_gracefully(self):
        """A base branch that predates rebrand_baseline.yaml (file doesn't exist there) is a
        no-op pass, not a false failure -- now surfaced as a ::warning:: (item 7)."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.head_path,
        )
        exit_code, out = self._run([str(self.tmp_dir / "does-not-exist.yaml")])
        self.assertEqual(exit_code, 0)
        self.assertIn("::warning::", out)

    def test_raise_without_increase_reason_fails(self):
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 1)

    def test_raise_with_valid_fresh_increase_reason_passes(self):
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
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)

    def test_new_entry_without_increase_reason_fails(self):
        _dump_baseline([], self.base_path)
        _dump_baseline(
            [BaselineEntry(file="app/backend/newfile.py", brand="sonic", max=2, issue="#TODO")],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 1)

    def test_lower_or_unchanged_never_needs_a_reason(self):
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)

    # ── #105 item 1: fresh reason required per raise ──────────────────────

    def test_raise_reusing_the_prior_increase_reason_fails(self):
        """A second raise that cites the SAME increase_reason as the entry it's raising must
        fail -- reusing one old decision to cover an unbounded number of later increases is
        exactly what #105 closes."""
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#100",
                )
            ],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=8, issue="#74",
                    increase_reason="#100",
                )
            ],
            self.head_path,
        )
        exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("reuses the same increase_reason", out)

    def test_raise_with_a_different_fresh_reason_than_the_prior_raise_passes(self):
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#100",
                )
            ],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=8, issue="#74",
                    increase_reason="#200",
                )
            ],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)

    # ── #105 item 2: open-issue GitHub API verification, fail-closed ─────

    def test_raise_with_a_closed_issue_reason_fails(self):
        self.api_mock.return_value = "#999 is not open (state='closed')"
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#999",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(
            checker.os.environ,
            {
                checker.REQUIRE_ISSUE_API_ENV: "1",
                checker.GITHUB_TOKEN_ENV: "test-token",
                checker.GITHUB_REPOSITORY_ENV: "swigerb/AzureAIDriveThru",
            },
        ):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("not open", out)
        self.api_mock.assert_called_once_with("#999", "swigerb/AzureAIDriveThru", "test-token")

    def test_raise_with_a_verified_open_issue_reason_passes(self):
        self.api_mock.return_value = None
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#999",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(
            checker.os.environ,
            {
                checker.REQUIRE_ISSUE_API_ENV: "1",
                checker.GITHUB_TOKEN_ENV: "test-token",
                checker.GITHUB_REPOSITORY_ENV: "swigerb/AzureAIDriveThru",
            },
        ):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 0)
        self.assertIn("::warning file=", out)

    def test_api_check_required_but_token_missing_fails_closed(self):
        """REBRAND_REQUIRE_ISSUE_API_CHECK=1 with no GITHUB_TOKEN/GITHUB_REPOSITORY must fail
        the raise closed, not silently skip the check."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#999",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(checker.os.environ, {checker.REQUIRE_ISSUE_API_ENV: "1"}):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("cannot verify", out)
        self.api_mock.assert_not_called()

    def test_api_check_not_required_and_no_token_skips_the_lookup(self):
        """Without REBRAND_REQUIRE_ISSUE_API_CHECK, local/manual runs still pass on format
        alone (no token needed) -- CI is what sets the flag that makes it mandatory."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#999",
                )
            ],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)
        self.api_mock.assert_not_called()

    # ── #105 item 3: reject the PR's own issue as a RAISE's own justification ─

    def test_raise_citing_the_prs_own_issue_fails(self):
        """A raise on an EXISTING entry may not cite the PR's own issue as its
        increase_reason -- that would let a PR "justify" its own change instead of pointing
        at a separate, already-settled decision."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#105",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(checker.os.environ, {checker.REBRAND_PR_ISSUES_ENV: "#105"}):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("this PR's own issue", out)

    def test_raise_citing_a_different_issue_than_the_pr_passes(self):
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#42",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(checker.os.environ, {checker.REBRAND_PR_ISSUES_ENV: "#105"}):
            exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)

    def test_new_entry_on_a_brand_new_file_may_cite_the_prs_own_issue_via_refs(self):
        """A brand-new (file, brand) entry -- a file with ZERO prior baseline entries, e.g. a
        newly-scanned .cs file (#105's own SCAN_EXTENSIONS change) -- is NOT subject to the
        self-citation rule: there is no "separate, already-settled decision" to point to
        instead, since the PR adding scan coverage IS the decision. #105 itself requires the
        seeded .cs entries to be attributed to #105. Cited via a non-closing keyword (e.g.
        "Refs #105", so it's in REBRAND_PR_ISSUES but NOT REBRAND_PR_CLOSES) -- item 3b does
        not apply."""
        _dump_baseline([], self.base_path)
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend-dotnet/src/Backend/Program.cs", brand="sonic", max=1,
                    issue="#105", increase_reason="#105",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(checker.os.environ, {checker.REBRAND_PR_ISSUES_ENV: "#105"}):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 0)
        self.assertIn("::warning file=", out)

    def test_raise_citing_an_issue_this_pr_closes_fails(self):
        """#105 R1b (round 2, Rick's PR #153 review): a RAISE citing an issue in
        REBRAND_PR_CLOSES fails even though it's a valid, different-from-self-citation issue
        reference -- because merging this PR would close that issue, and the post-merge
        push-to-dev re-check would then fail "issue is not open"."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend/foo.py", brand="sonic", max=5, issue="#74",
                    increase_reason="#84",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(
            checker.os.environ,
            {checker.REBRAND_PR_ISSUES_ENV: "#84", checker.REBRAND_PR_CLOSES_ENV: "#84"},
        ):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("which this PR closes on merge", out)

    def test_new_entry_citing_an_issue_this_pr_closes_fails(self):
        """#105 R1b: unlike plain self-citation (item 3, RAISE-only), a brand-new entry citing
        an issue this PR CLOSES fails too -- a NEW entry seeding `increase_reason: '#84'` while
        the PR body says "Closes #84" would pass its own PR check, then have #84 closed by the
        merge, then fail the post-merge push-to-dev re-check with "#84 is not open"."""
        _dump_baseline([], self.base_path)
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend-dotnet/src/Backend/Program.cs", brand="sonic", max=1,
                    issue="#84", increase_reason="#84",
                )
            ],
            self.head_path,
        )
        with mock.patch.dict(
            checker.os.environ,
            {checker.REBRAND_PR_ISSUES_ENV: "#84", checker.REBRAND_PR_CLOSES_ENV: "#84"},
        ):
            exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("which this PR closes on merge", out)

    # ── #105 item 4: refuse a foreign brand into an already-baselined file ─

    def test_new_entry_for_a_brand_the_file_does_not_already_track_fails(self):
        """Reproduces the #109 bug: the file already has a 'sonic' entry, and this PR tries
        to add a 'dunkin' entry for the SAME file -- refused outright, even with an otherwise
        perfectly valid increase_reason."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/setup_search_index.py", brand="sonic", max=4, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [
                BaselineEntry(file="app/backend/setup_search_index.py", brand="sonic", max=4, issue="#74"),
                BaselineEntry(
                    file="app/backend/setup_search_index.py", brand="dunkin", max=1, issue="#TODO",
                    increase_reason="#123",
                ),
            ],
            self.head_path,
        )
        exit_code, out = self._run()
        self.assertEqual(exit_code, 1)
        self.assertIn("already has a baseline entry", out)

    def test_new_entry_for_a_brand_new_file_with_no_prior_entries_at_all_passes(self):
        """A file with ZERO prior baseline entries (e.g. a newly-scanned .cs file, #108) may
        still gain its first entry for whichever brand(s) it actually contains -- including
        more than one brand at once."""
        _dump_baseline([], self.base_path)
        _dump_baseline(
            [
                BaselineEntry(
                    file="app/backend-dotnet/tests/Backend.Tests/Personas/PersonaCatalogTests.cs",
                    brand="sonic", max=33, issue="#105", increase_reason="#105",
                ),
                BaselineEntry(
                    file="app/backend-dotnet/tests/Backend.Tests/Personas/PersonaCatalogTests.cs",
                    brand="dunkin", max=3, issue="#105", increase_reason="#105",
                ),
            ],
            self.head_path,
        )
        exit_code, _ = self._run()
        self.assertEqual(exit_code, 0)

    # ── #105 item 6: allowed raises/new entries are surfaced as warnings ─

    def test_allowed_raise_is_surfaced_as_a_warning(self):
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
        exit_code, out = self._run()
        self.assertEqual(exit_code, 0)
        self.assertIn("::warning file=app/backend/tests/rebrand_baseline.yaml::RAISE", out)
        self.assertIn("3 -> 5", out)

    def test_lower_is_not_surfaced_as_a_warning(self):
        """Only genuine raises/new entries are noteworthy -- a routine lower shouldn't spam
        the checks tab with warnings."""
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=5, issue="#74")],
            self.base_path,
        )
        _dump_baseline(
            [BaselineEntry(file="app/backend/foo.py", brand="sonic", max=3, issue="#74")],
            self.head_path,
        )
        exit_code, out = self._run()
        self.assertEqual(exit_code, 0)
        self.assertNotIn("::warning", out)


class TestParsePrIssueRefs(unittest.TestCase):
    """Unit tests for parse_pr_issue_refs() -- #105 R1 (round 2, Rick's PR #153 review): the
    workflow's "Determine this PR's own issue reference" step calls this helper directly
    rather than re-implementing the regex in workflow YAML, so it's tested once, here."""

    def test_fix_hash_n_is_extracted_and_treated_as_closing(self):
        issues, closes = checker.parse_pr_issue_refs("Fix #84")
        self.assertEqual(issues, frozenset({"#84"}))
        self.assertEqual(closes, frozenset({"#84"}))

    def test_fixed_hash_n_is_extracted_and_treated_as_closing(self):
        """GitHub's own closing-keyword set includes 'fixed', not just 'fix'/'fixes' -- the
        original regex (Refs|Closes|Fixes|Resolves) missed this entirely."""
        issues, closes = checker.parse_pr_issue_refs("This fixed #84 for good.")
        self.assertEqual(issues, frozenset({"#84"}))
        self.assertEqual(closes, frozenset({"#84"}))

    def test_every_github_closing_keyword_form_is_recognised(self):
        for keyword in (
            "close", "closes", "closed",
            "fix", "fixes", "fixed",
            "resolve", "resolves", "resolved",
        ):
            with self.subTest(keyword=keyword):
                issues, closes = checker.parse_pr_issue_refs(f"{keyword} #1")
                self.assertEqual(issues, frozenset({"#1"}))
                self.assertEqual(closes, frozenset({"#1"}))

    def test_ref_and_refs_are_extracted_but_not_treated_as_closing(self):
        issues, closes = checker.parse_pr_issue_refs("Refs #105")
        self.assertEqual(issues, frozenset({"#105"}))
        self.assertEqual(closes, frozenset())

        issues, closes = checker.parse_pr_issue_refs("ref #105")
        self.assertEqual(issues, frozenset({"#105"}))
        self.assertEqual(closes, frozenset())

    def test_a_second_issue_listed_after_a_first_is_still_caught(self):
        """The original regex used re.search (first match only) -- re.finditer must catch
        every match in the body, not just the first."""
        issues, closes = checker.parse_pr_issue_refs("Closes #105. Also refs #42 for context.")
        self.assertEqual(issues, frozenset({"#105", "#42"}))
        self.assertEqual(closes, frozenset({"#105"}))

    def test_matching_is_case_insensitive(self):
        issues, closes = checker.parse_pr_issue_refs("CLOSES #7")
        self.assertEqual(issues, frozenset({"#7"}))
        self.assertEqual(closes, frozenset({"#7"}))

    def test_optional_colon_after_keyword_is_accepted(self):
        issues, closes = checker.parse_pr_issue_refs("Closes: #9")
        self.assertEqual(issues, frozenset({"#9"}))
        self.assertEqual(closes, frozenset({"#9"}))

    def test_empty_body_yields_empty_sets(self):
        issues, closes = checker.parse_pr_issue_refs("")
        self.assertEqual(issues, frozenset())
        self.assertEqual(closes, frozenset())

    def test_body_with_no_keyword_match_yields_empty_sets(self):
        """A bare '#84' with no preceding keyword doesn't count -- GitHub itself requires the
        keyword to auto-close on merge, and item 3's self-citation rule is about the PR's
        stated issue references, not every hash-number that happens to appear."""
        issues, closes = checker.parse_pr_issue_refs("See #84 for background.")
        self.assertEqual(issues, frozenset())
        self.assertEqual(closes, frozenset())

    # -- #227 (Rick's review of PR #220): comma-/"and"-separated lists and title refs ------

    def test_comma_separated_list_after_keyword_extracts_every_number(self):
        """The original bug report: 'Refs #76, #63' only extracted '#76'. Both numbers must
        now be caught, and since 'refs' isn't a closing keyword, neither is a closer."""
        issues, closes = checker.parse_pr_issue_refs("Refs #76, #63")
        self.assertEqual(issues, frozenset({"#76", "#63"}))
        self.assertEqual(closes, frozenset())

    def test_comma_separated_list_after_closing_keyword_closes_every_number(self):
        """'Fixes #1, #2' must close BOTH #1 and #2, not just the first."""
        issues, closes = checker.parse_pr_issue_refs("Fixes #1, #2")
        self.assertEqual(issues, frozenset({"#1", "#2"}))
        self.assertEqual(closes, frozenset({"#1", "#2"}))

    def test_longer_comma_separated_list_extracts_every_number(self):
        issues, closes = checker.parse_pr_issue_refs("Closes #1, #2, #3")
        self.assertEqual(issues, frozenset({"#1", "#2", "#3"}))
        self.assertEqual(closes, frozenset({"#1", "#2", "#3"}))

    def test_and_separated_list_after_keyword_extracts_every_number(self):
        issues, closes = checker.parse_pr_issue_refs("Refs #10 and #20")
        self.assertEqual(issues, frozenset({"#10", "#20"}))
        self.assertEqual(closes, frozenset())

    def test_mixed_comma_and_and_list_extracts_every_number(self):
        issues, closes = checker.parse_pr_issue_refs("Fixes #1, #2 and #3")
        self.assertEqual(issues, frozenset({"#1", "#2", "#3"}))
        self.assertEqual(closes, frozenset({"#1", "#2", "#3"}))

    def test_mixed_closing_then_referencing_keyword_classifies_each_independently(self):
        """'Fixes #1 and refs #2' is NOT a single list -- 'and' is followed by the keyword
        'refs', not directly by '#2', so the continuation after '#1' stops there and 'refs #2'
        is picked up by its own, separate match with its own (non-closing) keyword."""
        issues, closes = checker.parse_pr_issue_refs("Fixes #1 and refs #2")
        self.assertEqual(issues, frozenset({"#1", "#2"}))
        self.assertEqual(closes, frozenset({"#1"}))

    def test_bare_hash_n_in_title_counts_as_reference_but_not_closing(self):
        issues, closes = checker.parse_pr_issue_refs("", title="Follow-up for #227")
        self.assertEqual(issues, frozenset({"#227"}))
        self.assertEqual(closes, frozenset())

    def test_title_refs_are_unioned_with_body_refs(self):
        issues, closes = checker.parse_pr_issue_refs("Fixes #1", title="See also #2")
        self.assertEqual(issues, frozenset({"#1", "#2"}))
        self.assertEqual(closes, frozenset({"#1"}))

    def test_default_title_argument_does_not_change_body_only_behaviour(self):
        """Every pre-#227 call site (and every pre-#227 test above) calls
        parse_pr_issue_refs() with a single positional argument -- title must default to not
        adding anything, not raise TypeError."""
        issues, closes = checker.parse_pr_issue_refs("Fixes #1")
        self.assertEqual(issues, frozenset({"#1"}))
        self.assertEqual(closes, frozenset({"#1"}))

    def test_live_pr_220_body_shape_extracts_both_issues(self):
        """Regression-pins the exact live shape that surfaced this bug (#227): PR #220's body
        opens with '## Refs #76, #63' (the "### #76: ..." / "### #63: ..." section headings
        further down have no keyword before the '#N', so they don't themselves match) and its
        title is 'Fix #76: ... / Refs #63: ...'. Both #76 and #63 must end up in *issues*
        (from the body's "Refs #76, #63" list, and independently from the title's bare
        mentions). *closes* must be EMPTY: the body's only keyword match is "Refs" (not a
        closer), and a title's bare '#N' mentions never count toward closing_issues regardless
        of what word precedes them in the title text (see parse_pr_issue_refs()'s docstring)."""
        body = (
            "## Refs #76, #63\n\n"
            "### #76: real-persona extras conformance rows\n\n"
            "- New conformance coverage for real personas with extras.\n\n"
            "### #63: harness follow-ups\n\n"
            "- Follow-up fixes to the conformance harness.\n"
        )
        title = "Fix #76: real-persona extras conformance rows / Refs #63: harness follow-ups"
        issues, closes = checker.parse_pr_issue_refs(body, title)
        self.assertEqual(issues, frozenset({"#76", "#63"}))
        self.assertEqual(closes, frozenset())


if __name__ == "__main__":
    unittest.main()
