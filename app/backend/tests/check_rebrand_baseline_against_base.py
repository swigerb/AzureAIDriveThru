#!/usr/bin/env python
"""CI-only check: compare the checked-in rebrand_baseline.yaml against the PR's base branch
(or, on a push to dev, the pre-push commit) and fail on any unjustified change.

Round 1 (PR #101 round 3) only checked "does a `max` increase / brand-new entry have a
validly-formatted `increase_reason`?". Issue #105 hardened every corner that left open:

  1. A raise's `increase_reason` must be a FRESH reason, not the same one already recorded on
     the entry being raised -- reusing a stale reason across multiple raises would let a
     single old, possibly-unrelated decision cover an unbounded number of later increases.
  2. The `increase_reason` must reference a real, OPEN GitHub issue (verified against the
     GitHub REST API with GITHUB_TOKEN) -- a syntactically-valid-looking but fake/closed/
     already-fixed issue reference no longer passes. If the API check can't run at all in a
     context where it's required, that fails closed (see REBRAND_REQUIRE_ISSUE_API_CHECK
     below) rather than silently trusting the reference.
  3. A RAISE's `increase_reason` may not cite ANY issue this PR itself references, via ANY
     GitHub closing/non-closing keyword -- close(s/d), fix(es/ed), resolve(s/d), ref(s),
     case-insensitive (REBRAND_PR_ISSUES, extracted from the PR body in CI via
     `parse_pr_issue_refs()`) -- the issue that justifies raising an EXISTING entry must be a
     separate, already-settled decision, not "because this very PR is making the change".
     Deliberately scoped to raises only, not brand-new entries -- see item 4's note on why a
     brand-new entry legitimately may (and, per issue #105 itself, must) cite the PR that
     first adds tracking for it.
  3b. Separately, and regardless of label (RAISE *or* brand-new), no entry's `increase_reason`
     may cite an issue this PR CLOSES specifically (REBRAND_PR_CLOSES -- the subset of item 3
     reached via a closing keyword, not a bare "Refs"). A brand-new entry that seeds
     `increase_reason: '#N'` while the PR body says "Closes #N" would pass its own PR check,
     then have #N closed by the merge, then fail the post-merge push-to-dev re-check with
     "#N is not open" -- dev goes red right after a green merge. #105 round 2 (Rick's PR #153
     review), closing that gap. Round 1's extraction regex also only recognised "Refs/Closes/
     Fixes/Resolves" (missing "close", "closed", "fix", "fixed", "resolve", "resolved") and
     kept only the FIRST match in the body -- both bypasses are fixed by
     `parse_pr_issue_refs()` matching every occurrence of every keyword form.
  4. A brand-new (file, brand) entry is refused outright -- regardless of `increase_reason` --
     if that file already has a baseline entry for a DIFFERENT brand. This is the exact shape
     of the #109 bug (a foreign brand quietly baselined into a file that only ever tracked
     "sonic"). A file with NO prior baseline entries at all (e.g. a newly-scanned .cs file,
     #105's own SCAN_EXTENSIONS change) is unaffected -- it may still gain its first entry(ies)
     for any brand it actually contains, self-citing the PR that added its scan coverage (item
     3 does not apply to these -- there is no "previous" decision to point to instead).
  5. This check now also runs on `push` to dev (comparing against `github.event.before`, see
     .github/workflows/conformance.yml), not only on pull_request -- issue #24's original
     "catch anything that reached dev without a PR check" rationale applies to this ratchet
     too, not just the rest of the suite.
  6. Every ALLOWED raise/new entry is surfaced as a `::warning::` GitHub Actions annotation on
     the PR's checks (not just a silent pass) -- an authorized increase should still be
     visible to reviewers, not buried in green.
  7. The missing-base-baseline skip (base branch predates rebrand_baseline.yaml) is now a
     `::warning::` annotation too, not a plain, easy-to-miss print.
  8. #227 (Rick's review of PR #220): `parse_pr_issue_refs()` only matched the first `#N`
     after a keyword, so "Refs #76, #63" (a real PR #220 body shape) extracted only `#76` --
     an `increase_reason` citing `#63` would then slip past item 3's self-citation check. The
     regex now consumes a trailing comma-/"and"-separated list after the first ref, and every
     `#N` in the whole match is extracted. Bare `#N` mentions in the PR TITLE (no keyword
     required there) now also count toward item 3's *all_issues* set, but never toward
     *closing_issues* -- GitHub itself has no closing-keyword syntax for titles.

Usage (see .github/workflows/conformance.yml, python-tests job):

    python app/backend/tests/check_rebrand_baseline_against_base.py <path-to-base-baseline.yaml>

Environment variables (all optional for local/manual runs; CI sets every one of them):
    GITHUB_TOKEN                    Bearer token for the GitHub REST API issue lookup.
    GITHUB_REPOSITORY               "owner/name" of the repo to look issues up in.
    REBRAND_PR_ISSUES               Comma-separated '#N' issue refs this PR's body mentions
                                     via ANY supported keyword (close/closes/closed,
                                     fix/fixes/fixed, resolve/resolves/resolved, ref/refs),
                                     case-insensitive, each optionally followed by a comma-/
                                     "and"-separated list of further '#N' refs, PLUS every
                                     bare '#N' mentioned in the PR's TITLE (#227) -- a RAISE's
                                     increase_reason may not equal any of these (item 3).
    REBRAND_PR_CLOSES               Comma-separated '#N' issue refs this PR's body mentions
                                     via a CLOSING keyword specifically (REBRAND_PR_ISSUES
                                     minus any ref/refs-only matches) -- NO entry, RAISE or
                                     NEW, may cite one of these: merging this PR closes the
                                     issue, so the post-merge push-to-dev check would then
                                     fail (item 3b).
    REBRAND_REQUIRE_ISSUE_API_CHECK Set to "1"/"true" to make the open-issue API check
                                     mandatory: if GITHUB_TOKEN/GITHUB_REPOSITORY are missing,
                                     or the API call fails for any reason, every raise/new
                                     entry fails closed instead of being silently trusted. CI
                                     always sets this; local/manual runs default to skipping
                                     the API call (with a warning) when it's unset, so the
                                     other rules remain runnable without a token.

Exits 0 if the base baseline can't be found/parsed (nothing to ratchet against yet -- a no-op
rather than a false failure that would block every PR forever) or every change passes all of
the above. Exits 1 and lists every offending entry otherwise.
"""
from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rebrand_scan import BASELINE_PATH, BaselineEntry, _load_baseline  # noqa: E402

ISSUE_REF_RE = re.compile(r"#\d+")

# #105 R1 (round 2, Rick's PR #153 review): GitHub recognises these closing keywords on a PR
# body, case-insensitively, each optionally followed by a colon -- "close(s/d)", "fix(es/ed)",
# "resolve(s/d)". "ref(s)" is NOT a closing keyword (it links an issue without closing it),
# which is exactly why callers need both the full set (REBRAND_PR_ISSUES) and the
# closing-only subset (REBRAND_PR_CLOSES) separately -- see parse_pr_issue_refs() below.
#
# #227 (Rick's review of PR #220): the keyword only had to precede the FIRST '#N' -- "Refs
# #76, #63" matched just "#76", silently dropping "#63" from both sets. The trailing group
# below consumes zero or more further ", #N" / "and #N" list items immediately after that
# first ref, so the whole match spans the entire list (every number is then pulled back out
# with ISSUE_REF_RE in parse_pr_issue_refs() -- simpler than naming an unbounded number of
# capture groups). A keyword appearing again later ("Fixes #1 and refs #2") is deliberately
# NOT absorbed into the list -- "and refs #2" fails the bare "and #N" continuation (there's a
# second keyword in the way), so it's left for its own, separate match with its own keyword.
PR_ISSUE_REF_RE = re.compile(
    r"\b(close[sd]?|fix(?:e[sd])?|resolve[sd]?|refs?)\s*:?\s+#\d+"
    r"(?:\s*(?:,|\band\b)\s*#\d+)*",
    re.IGNORECASE,
)

REBRAND_PR_ISSUES_ENV = "REBRAND_PR_ISSUES"
REBRAND_PR_CLOSES_ENV = "REBRAND_PR_CLOSES"
REQUIRE_ISSUE_API_ENV = "REBRAND_REQUIRE_ISSUE_API_CHECK"
GITHUB_TOKEN_ENV = "GITHUB_TOKEN"  # noqa: S105 -- this is an env var NAME, not a secret
GITHUB_REPOSITORY_ENV = "GITHUB_REPOSITORY"

_TRUTHY = {"1", "true", "True", "yes", "on"}


def parse_pr_issue_refs(body: str, title: str = "") -> tuple[frozenset[str], frozenset[str]]:
    """Extract every issue reference this PR's body (and, optionally, title) makes.

    Returns ``(all_issues, closing_issues)``, both as frozensets of ``'#N'`` strings:

    - *all_issues* is every issue referenced by ANY supported keyword in the body -- close(s/d),
      fix(es/ed), resolve(s/d), ref(s) -- case-insensitive, every match in the body (not just
      the first), PLUS every bare ``#N`` mentioned in *title* (see below).
    - *closing_issues* is the subset referenced via a keyword GitHub itself treats as
      CLOSING the issue on merge (i.e. *all_issues* minus any ref/refs-only matches, and minus
      every title mention -- a title has no keyword syntax, so GitHub itself never closes an
      issue from the title alone).

    #105 R1 (round 2, Rick's PR #153 review): the original single-match regex only recognised
    "Refs/Closes/Fixes/Resolves" (missing GitHub's own "close", "closed", "fix", "fixed",
    "resolve", "resolved" forms) and stopped at the first hit in the body -- both let a
    closing reference slip past the checker uncaught.

    #227 (Rick's review of PR #220): a keyword followed by a comma- or "and"-separated list of
    refs -- e.g. "Refs #76, #63" -- only extracted the FIRST number; every ``#N`` in the list
    is now pulled out of the whole match (PR_ISSUE_REF_RE's trailing repeated group). GitHub's
    own PR title is also scanned for bare ``#N`` mentions (no keyword required there) and
    folded into *all_issues* only -- Rule 3's self-citation check reads *all_issues*, so a
    `increase_reason` matching a title-mentioned issue is still caught, without requiring a
    title keyword GitHub itself doesn't recognise as closing syntax.

    This helper is imported directly by the "Determine this PR's own issue reference"
    workflow step so the regex is tested once, here, rather than duplicated in workflow YAML.
    """
    all_issues: set[str] = set()
    closing_issues: set[str] = set()
    for match in PR_ISSUE_REF_RE.finditer(body or ""):
        keyword = match.group(1).lower()
        refs = ISSUE_REF_RE.findall(match.group(0))
        all_issues.update(refs)
        if not keyword.startswith("ref"):
            closing_issues.update(refs)
    all_issues.update(ISSUE_REF_RE.findall(title or ""))
    return frozenset(all_issues), frozenset(closing_issues)


def check_issue_is_open(issue_ref: str, repo: str, token: str) -> str | None:
    """Verify *issue_ref* (e.g. '#123') is an OPEN GitHub issue (not a pull request, not
    closed, not missing) in *repo* ('owner/name'), via the GitHub REST API.

    Returns None if it checks out, else a short human-readable reason it doesn't. ANY
    failure -- network error, timeout, non-200 status, malformed JSON -- is treated the same
    as "not verified open" (fail-closed, #105 item 2): a raise/new entry citing an issue we
    can't positively confirm is open must not be silently trusted.
    """
    number = issue_ref.lstrip("#")
    url = f"https://api.github.com/repos/{repo}/issues/{number}"
    req = urllib.request.Request(  # noqa: S310 -- fixed https://api.github.com host, not user input
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "rebrand-baseline-ratchet-check",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:  # noqa: S310
            if resp.status != 200:
                return f"GitHub API returned HTTP {resp.status} for issue {issue_ref}"
            payload = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        return f"GitHub API returned HTTP {exc.code} for issue {issue_ref}"
    except Exception as exc:  # noqa: BLE001 -- fail-closed on ANY error, not just HTTP ones
        return f"GitHub API lookup for issue {issue_ref} failed: {exc!r}"

    if "pull_request" in payload:
        return f"{issue_ref} is a pull request, not an issue"
    if payload.get("state") != "open":
        return f"{issue_ref} is not open (state={payload.get('state')!r})"
    return None


def _existing_brands_for_file(baseline: dict[tuple[str, str], BaselineEntry], file: str) -> set[str]:
    return {brand for (f, brand) in baseline if f == file}


def _check_entry(
    entry: BaselineEntry,
    prior: BaselineEntry | None,
    base: dict[tuple[str, str], BaselineEntry],
    *,
    pr_issues: frozenset[str],
    pr_closes: frozenset[str],
    require_api_check: bool,
    token: str,
    repo: str,
) -> tuple[str | None, str | None]:
    """Check one head baseline entry against its base-branch counterpart (``prior``, or None
    if brand-new). Returns (problem, warning): at most one is non-None. ``problem`` means the
    entry fails the ratchet (caller should fail CI); ``warning`` means it's an ALLOWED raise/
    new entry worth surfacing (item 6). Both None means "unchanged or lowered -- nothing to
    say"."""
    desc = f"{entry.file} [{entry.brand}]"

    if prior is None:
        existing_brands = _existing_brands_for_file(base, entry.file)
        if existing_brands and entry.brand not in existing_brands:
            return (
                f"NEW   {desc} = {entry.max} -- {entry.file} already has a baseline entry "
                f"for {sorted(existing_brands)}, not '{entry.brand}': a brand-new entry may "
                f"only add a brand a file doesn't already track (#105, closing the #109 gap)",
                None,
            )
        label, prior_max = "NEW", None
    else:
        if entry.max <= prior.max:
            return None, None  # unchanged or lowered -- never needs a reason
        label, prior_max = "RAISE", prior.max

    reason = entry.increase_reason or ""
    if not ISSUE_REF_RE.fullmatch(reason):
        suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
        return f"{label} {desc}{suffix} (no valid increase_reason vs base branch)", None

    if label == "RAISE" and reason == (prior.increase_reason or ""):
        return (
            f"RAISE {desc} {prior_max} -> {entry.max} reuses the same increase_reason "
            f"({reason}) as the entry it's raising -- #105 requires a fresh reason naming "
            f"why THIS raise is justified, not the reason recorded for a previous one",
            None,
        )

    if reason in pr_closes:
        # #105 R1b (round 2, Rick's PR #153 review), applies to EITHER label -- unlike the
        # RAISE-only self-citation check below, a brand-new entry citing an issue this PR
        # CLOSES is just as broken: the PR's own check passes, the merge closes the issue,
        # and the post-merge push-to-dev re-check then fails "reason is not open" on a
        # perfectly green merge.
        suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
        return (
            f"{label} {desc}{suffix} cites {reason}, which this PR closes on merge -- the "
            f"post-merge push-to-dev check re-verifies every entry's issue is still open, so "
            f"this would fail closed right after a green merge; cite an issue that stays "
            f"open, or reference this PR with a non-closing keyword (e.g. 'Refs {reason}') "
            f"instead",
            None,
        )

    if label == "RAISE" and reason in pr_issues:
        # Scoped to RAISES only -- deliberately NOT applied to brand-new entries on a file
        # with zero prior baseline entries at all. Rick's #109 example combined self-citation
        # WITH a foreign brand slipped into an already-tracked file (item 4 above already
        # refuses that unconditionally); it was never about a PR seeding first-time coverage
        # for code that was simply never scanned before (e.g. #105's own .cs seeding, which
        # the issue explicitly requires be "attributed to this issue"). Blocking self-citation
        # on NEW entries too would make that kind of legitimate scan-expansion PR impossible
        # to land in one pass.
        suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
        return (
            f"{label} {desc}{suffix} cites this PR's own issue ({reason}) as its "
            f"increase_reason -- the issue justifying a raise must be a separate, "
            f"already-settled decision, not the change this PR itself is making",
            None,
        )

    if require_api_check:
        if not (token and repo):
            suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
            return (
                f"{label} {desc}{suffix}: cannot verify {reason} is an open GitHub issue -- "
                f"GITHUB_TOKEN/GITHUB_REPOSITORY are required when "
                f"{REQUIRE_ISSUE_API_ENV}=1 (#105: fail closed rather than trust an "
                f"unverified reference)",
                None,
            )
        api_problem = check_issue_is_open(reason, repo, token)
        if api_problem is not None:
            suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
            return f"{label} {desc}{suffix}: increase_reason {reason} -- {api_problem}", None

    suffix = f" {prior_max} -> {entry.max}" if prior_max is not None else f" = {entry.max}"
    return None, f"{label} {desc}{suffix} ({reason})"


def _parse_issue_ref_env(raw: str) -> frozenset[str]:
    """Parse a comma-separated '#N,#M' env var value into a frozenset of '#N' strings,
    ignoring blanks (e.g. an unset/empty env var, or a trailing comma)."""
    return frozenset(ref.strip() for ref in raw.split(",") if ref.strip())


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("usage: check_rebrand_baseline_against_base.py <path-to-base-baseline.yaml>")
        return 2
    base_path = Path(argv[0])

    head = _load_baseline(BASELINE_PATH)
    try:
        base = _load_baseline(base_path)
    except (FileNotFoundError, ValueError):
        print(
            f"::warning::Base baseline at {base_path} not found/parseable -- skipping "
            f"rebrand ratchet check (nothing to ratchet against yet)."
        )
        return 0

    pr_issues = _parse_issue_ref_env(os.environ.get(REBRAND_PR_ISSUES_ENV) or "")
    pr_closes = _parse_issue_ref_env(os.environ.get(REBRAND_PR_CLOSES_ENV) or "")
    require_api_check = (os.environ.get(REQUIRE_ISSUE_API_ENV) or "").strip() in _TRUTHY
    token = (os.environ.get(GITHUB_TOKEN_ENV) or "").strip()
    repo = (os.environ.get(GITHUB_REPOSITORY_ENV) or "").strip()

    if require_api_check and not (token and repo):
        print(
            f"::warning::{REQUIRE_ISSUE_API_ENV}=1 but GITHUB_TOKEN/GITHUB_REPOSITORY are "
            f"not both set -- every raise/new entry needing the open-issue check will fail "
            f"closed below."
        )

    problems: list[str] = []
    warnings: list[str] = []
    for key, entry in sorted(head.items()):
        prior = base.get(key)
        problem, warning = _check_entry(
            entry, prior, base,
            pr_issues=pr_issues, pr_closes=pr_closes,
            require_api_check=require_api_check, token=token, repo=repo,
        )
        if problem is not None:
            problems.append(problem)
        if warning is not None:
            warnings.append(warning)

    for warning in warnings:
        print(f"::warning file=app/backend/tests/rebrand_baseline.yaml::{warning}")

    if problems:
        print(
            f"{len(problems)} rebrand_baseline.yaml change(s) vs the base branch failed the "
            f"ratchet check:"
        )
        for p in problems:
            print(f"  {p}")
        return 1

    print(
        f"OK: no unjustified baseline raises vs the base branch ({len(head)} head entries "
        f"checked, {len(warnings)} allowed raise(s)/new entry(ies))."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
