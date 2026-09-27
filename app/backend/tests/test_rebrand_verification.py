"""Rebrand verification tests -- inverted for the multi-persona architecture (#76).

Historically (pre-#76) this file enforced a one-way rebrand: "no source file may say
Dunkin'". That assumption broke the moment #70 introduced persona packs (personas/sonic/**)
and #78/#79 planned McDonald's/Dunkin packs of their own -- a persona pack's OWN files are
*supposed* to say its brand name, and comparing brands by name is the entire point of
docs/adr/** and docs/persona-architecture.md.

#76 groundwork inverts the guard's premise: a brand word (Sonic, McDonald's, Dunkin) is
allowed only

  1. inside its OWN persona pack (personas/<id>/**) -- personas/sonic/** may say "Sonic",
     but not "Dunkin'"/"McDonald's" (a persona pack must not reference a different brand);
  2. inside the explicitly listed cross-brand docs (docs/adr/**, docs/persona-architecture.md)
     that compare all three brands by design (ADR-001, design doc section 16);
  3. inside one of exactly two DIRECTORY_EXCEPTIONS for generated/golden content
     (app/backend/static/, tests/conformance/testdata/) -- see rebrand_scan.py. A pack's own
     per-persona conformance testdata subfolder (tests/conformance/testdata/personas/<id>/**,
     #78/#79) is instead classified under rule 1's same per-pack-ownership logic, not limited
     to the flat directory exception's sonic-only rule;
  4. inside a shared-code file+brand pair that has an exact-match entry in the checked-in
     BASELINE (rebrand_baseline.yaml) -- the entry's line-hit count must equal the file's
     real count today: a rise means a new/uncontrolled reference snuck in, a silent drop
     means the fix landed but the baseline wasn't ratcheted down (round-2 review, replacing
     the original directory-prefix allowlist a PR reviewer flagged as a coverage trap: it
     rescued brand words anywhere under e.g. app/backend/ regardless of whether they were
     the SAME references being tracked or new ones).

Anywhere else, a brand word is forbidden. Every BASELINE entry today rescues only "sonic"
except two intentional McDonald's-brand-hex test fixtures -- if a stray "Dunkin" ever showed
up in shared code, it would still fail (no baseline entry would cover it).

The old "crew member" (should be carhop) and "coffee-chat" (old repo name) terminology checks
are unrelated to the brand-pack architecture and mostly unchanged by this inversion -- they
still apply everywhere except the same repo-meta/historical exclusions as before, with one
exception: "crew member" is scoped to SHARED code only (outside personas/<id>/**), since #78/
#79 give each pack its own role-name vocabulary and a pack (e.g. McDonald's) may legitimately
use "crew member" as its own, unlike Sonic's "carhop" -- see _is_inside_a_persona_pack.
"coffee-chat" has no such legitimate per-pack use, so it is unaffected.

Author: Birdperson (Tester); brand-word guard replaced with a per-file baseline in round 2
(Beth, PR #101 review response, issue #76).
"""

import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from rebrand_scan import (  # noqa: E402
    BRAND_PATTERNS,
    DIRECTORY_EXCEPTIONS,
    BaselineEntry,
    _classify_hit,
    _count_brand_occurrences,
    _load_baseline,
    _persona_pack_id,
    _relative_posix,
)

BASELINE = _load_baseline()

# ── Paths ────────────────────────────────────────────────────────────────
PROJECT_ROOT = Path(__file__).resolve().parents[3]  # SonicAIDriveThru/
BACKEND_DIR = PROJECT_ROOT / "app" / "backend"
FRONTEND_DIR = PROJECT_ROOT / "app" / "frontend"

# ── Terminology patterns (unrelated to brand packs; unchanged by #76) ───
FORBIDDEN_PATTERNS = [
    (re.compile(r"\bdunkin\b", re.IGNORECASE), "dunkin"),
    (re.compile(r"\bcrew\s+member\b", re.IGNORECASE), "crew member (should be carhop)"),
    (re.compile(r"\bcoffee[-\s]?chat\b", re.IGNORECASE), "coffee-chat (old repo name)"),
]

# File extensions to scan
SCAN_EXTENSIONS = {
    ".py", ".ts", ".tsx", ".js", ".jsx",
    ".html", ".css", ".json", ".md",
    ".yaml", ".yml", ".bicep", ".env-sample",
    ".sh",
}

# Filenames to scan that have no extension (e.g. Dockerfile)
SCAN_FILENAMES = {"Dockerfile"}

# Directories every scan skips regardless of which check is running: VCS/tooling/dependency
# noise, never source content. NOT persona-architecture-related, so both the terminology checks
# and the brand-word guard below share this base set.
BASE_EXCLUDED_DIRS = {
    ".git", "node_modules", "__pycache__",
    ".venv", "venv", "env",
    ".squad",
}

# ── Terminology-check scope (crew member / coffee-chat) -- unchanged from before #76 ─────
# .squad is excluded deliberately -- it contains historical rebrand records and Squad-managed
# scaffolding that legitimately reference the old brand name for traceability. "adr" was
# excluded here pre-#76 because ADR-001 compares all three brands by name; #76 keeps that
# exclusion for the terminology checks (ADRs have no reason to say "crew member"/"coffee-chat"
# either way, so this is a no-op change, but kept for minimal diff from the pre-#76 behaviour).
TERMINOLOGY_EXCLUDED_DIRS = BASE_EXCLUDED_DIRS | {"adr"}

TERMINOLOGY_EXCLUDED_FILES = {
    # The upstream attribution file is allowed to reference original names
    "voice_rag_README.md",
    # This test file itself contains the forbidden words by necessity
    "test_rebrand_verification.py",
    # The persona design doc compares the three brands (#19/ADR-001).
    "persona-architecture.md",
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


def _collect_terminology_scan_files() -> list[Path]:
    return _collect_source_files(TERMINOLOGY_EXCLUDED_DIRS, TERMINOLOGY_EXCLUDED_FILES)


# "crew member" is SHARED-CODE vocabulary only: pre-#76 it named a Sonic-only rebrand target
# (session_manager.py's role-name string had to become "carhop"), but #78/#79 give every pack
# its own roleName/vocabulary -- e.g. McDonald's real staff title is legitimately "crew member".
# A persona's own pack (personas/<id>/**) must be free to use it, same principle as the
# brand-word guard's per-pack-ownership rule above; "coffee-chat" (old repo name) has no such
# legitimate use anywhere a pack would need, so it keeps scanning everywhere unchanged.
def _is_inside_a_persona_pack(filepath: Path) -> bool:
    return _persona_pack_id(_relative_posix(filepath)) is not None


def _scan_for_forbidden(files: list[Path]) -> list[tuple[Path, int, str, str]]:
    """Return a list of (file, line_number, matched_text, label) hits for FORBIDDEN_PATTERNS."""
    hits: list[tuple[Path, int, str, str]] = []
    for filepath in files:
        try:
            lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
        except Exception:
            continue
        for line_no, line in enumerate(lines, start=1):
            # Skip upstream attribution URLs (e.g., links to the original repo)
            if "github.com/john-carroll-sw/coffee-chat" in line:
                continue
            for pattern, label in FORBIDDEN_PATTERNS:
                if pattern.search(line):
                    hits.append((filepath, line_no, line.strip(), label))
    return hits


# ── Brand-word guard (#76 inversion, per-file baseline as of round 2) ────
#
# BRAND_PATTERNS, the persona-pack/cross-brand-doc rules, DIRECTORY_EXCEPTIONS, the BASELINE
# loader, and the classification logic (_classify_hit) all live in rebrand_scan.py so that
# regenerate_rebrand_baseline.py can reuse the exact same rules when rewriting
# rebrand_baseline.yaml -- see that module's docstring for the full design rationale.


# ── Test class ───────────────────────────────────────────────────────────

class TestRebrandVerification(unittest.TestCase):
    """#76: brand words are allowed only in their own persona pack, the explicit cross-brand
    docs, one of the two generated/golden-content DIRECTORY_EXCEPTIONS, or a shared-code
    (file, brand) pair whose real line-hit count exactly matches its checked-in BASELINE
    entry -- forbidden everywhere else. "crew member"/"coffee-chat" terminology checks are
    unrelated and unchanged."""

    # ── Brand-word guard (inverted, #76; per-file baseline, round 2) ──

    def test_brand_word_counts_match_the_checked_in_baseline(self):
        """The main inverted guard: for every (file, brand) pair that has a real hit today, or
        that has a BASELINE entry, the real line-hit count must exactly match -- a rise, a
        silent drop, or a brand-new unbaselined hit are all failures (see rebrand_scan.py's
        docstring / _classify_hit)."""
        counts = _count_brand_occurrences()
        keys = set(counts) | set(BASELINE)
        formatted = []
        for rel_posix, brand in sorted(keys):
            reason = _classify_hit(rel_posix, brand, counts.get((rel_posix, brand), 0), BASELINE)
            if reason is not None:
                formatted.append(f"  [{brand}] {rel_posix}: {reason}")
        self.assertEqual(
            formatted, [],
            f"\n{len(formatted)} baseline mismatch(es):\n" + "\n".join(formatted),
        )

    def test_every_baseline_entry_has_a_valid_issue_reference(self):
        """Every BASELINE entry must carry a well-formed issue reference like '#74' -- an entry
        without one would be an untracked, silent permanent exception."""
        bad = [
            (e.file, e.brand) for e in BASELINE.values()
            if not re.fullmatch(r"#\d+", e.issue or "")
        ]
        self.assertEqual(
            bad, [],
            f"\nBaseline entries missing a valid issue reference (e.g. '#74'): {bad}",
        )

    def test_baseline_entries_with_an_increase_reason_have_a_valid_format(self):
        """`increase_reason` is only set by regenerate_rebrand_baseline.py's
        ``--allow-increase`` path (PR #101 round 3, Rick's review) on a raised/brand-new
        entry -- when present it must be a well-formed issue reference like '#123', the same
        as `issue`, so a raise can't be laundered through a free-text non-reference."""
        bad = [
            (e.file, e.brand, e.increase_reason) for e in BASELINE.values()
            if e.increase_reason and not re.fullmatch(r"#\d+", e.increase_reason)
        ]
        self.assertEqual(
            bad, [],
            f"\nBaseline entries with a malformed increase_reason (e.g. want '#123'): {bad}",
        )

    def test_baseline_entries_are_unique_per_file_and_brand(self):
        """Sanity: rebrand_scan._load_baseline already raises on a literal YAML duplicate, but
        assert it here too so a future refactor of the loader can't silently swallow one."""
        keys = [(e.file, e.brand) for e in BASELINE.values()]
        self.assertEqual(
            len(keys), len(set(keys)),
            f"Duplicate BASELINE (file, brand) entries found: {keys}",
        )

    def test_every_directory_exception_has_a_valid_issue_reference(self):
        """Every DIRECTORY_EXCEPTIONS entry must also carry a well-formed issue reference --
        these are exempt from per-file counting, not from being tracked at all."""
        bad = [
            exc.prefix for exc in DIRECTORY_EXCEPTIONS
            if not re.fullmatch(r"#\d+", exc.issue or "")
        ]
        self.assertEqual(
            bad, [],
            f"\nDirectory exceptions missing a valid issue reference (e.g. '#78'): {bad}",
        )

    def test_directory_exceptions_are_exactly_the_two_generated_or_golden_locations(self):
        """Mutation-style guard: DIRECTORY_EXCEPTIONS is deliberately a short, hardcoded list --
        this pins it to exactly the two locations Rick's review named, so a third
        directory-wide exception can't be added without a reviewer noticing this test change."""
        prefixes = {exc.prefix for exc in DIRECTORY_EXCEPTIONS}
        self.assertEqual(
            prefixes, {"app/backend/static/", "tests/conformance/testdata/"},
            f"DIRECTORY_EXCEPTIONS changed to {prefixes} -- add per-file BASELINE entries "
            f"instead of a new directory-wide exception unless the content is truly "
            f"generated/golden, and get that reviewed explicitly",
        )

    def test_sonic_persona_pack_may_say_sonic(self):
        """Sanity: the pack-ownership rule must not accidentally forbid a pack from saying its
        own brand -- personas/sonic/menu/menuItems.json (issue #70) is a real, populated file
        that must legitimately say 'Sonic'."""
        menu_path = PROJECT_ROOT / "personas" / "sonic" / "menu" / "menuItems.json"
        self.assertTrue(menu_path.exists(), "personas/sonic/menu/menuItems.json not found")
        rel_posix = _relative_posix(menu_path)
        self.assertIsNone(_classify_hit(rel_posix, "sonic", 1, {}))

    def test_cross_brand_docs_may_say_any_brand(self):
        """Sanity: docs/persona-architecture.md must legitimately be allowed to say all three
        brand names (it compares them by design, ADR-001)."""
        doc_path = PROJECT_ROOT / "docs" / "persona-architecture.md"
        self.assertTrue(doc_path.exists(), "docs/persona-architecture.md not found")
        rel_posix = _relative_posix(doc_path)
        for brand in BRAND_PATTERNS:
            self.assertIsNone(_classify_hit(rel_posix, brand, 1, {}))

    def test_a_foreign_brand_word_inside_a_persona_pack_is_forbidden(self):
        """Mutation-style unit check (no real file touched): 'dunkin' inside personas/sonic/**
        must be forbidden even though 'sonic' there is fine -- a persona pack must not
        reference a different brand, and no BASELINE entry can rescue it."""
        self.assertIsNone(_classify_hit("personas/sonic/menu/menuItems.json", "sonic", 1, {}))
        self.assertIsNotNone(
            _classify_hit(
                "personas/sonic/menu/menuItems.json", "dunkin", 1,
                {("personas/sonic/menu/menuItems.json", "dunkin"): BaselineEntry(
                    "personas/sonic/menu/menuItems.json", "dunkin", 1, "#78",
                )},
            )
        )

    def test_a_brand_word_in_an_unlisted_shared_file_is_forbidden(self):
        """Mutation-style unit check (no real file touched): a brand-new file with a brand-word
        hit and no BASELINE entry at all must be forbidden."""
        self.assertIsNotNone(_classify_hit("app/some_new_top_level_module.py", "dunkin", 1, {}))

    def test_a_brand_word_matching_its_baseline_entry_is_allowed(self):
        """Mutation-style unit check (no real file touched): a count that exactly matches its
        BASELINE entry is allowed."""
        baseline = {
            ("app/backend/some_module.py", "sonic"): BaselineEntry(
                "app/backend/some_module.py", "sonic", 3, "#74",
            ),
        }
        self.assertIsNone(_classify_hit("app/backend/some_module.py", "sonic", 3, baseline))

    def test_a_count_risen_above_its_baseline_max_is_forbidden(self):
        """Mutation-style unit check: rule 1 of the ratchet -- more hits than the checked-in
        max means a new, uncontrolled reference snuck in."""
        baseline = {
            ("app/backend/some_module.py", "sonic"): BaselineEntry(
                "app/backend/some_module.py", "sonic", 3, "#74",
            ),
        }
        reason = _classify_hit("app/backend/some_module.py", "sonic", 4, baseline)
        self.assertIsNotNone(reason)
        self.assertIn("above its BASELINE max", reason)

    def test_a_count_dropped_below_its_baseline_max_is_forbidden_until_lowered(self):
        """Mutation-style unit check: rule 2 of the ratchet -- fewer hits than the checked-in
        max (including 0, i.e. the file no longer has the brand word at all) must still fail,
        so the baseline can't silently drift out of sync with reality; it must be lowered."""
        baseline = {
            ("app/backend/some_module.py", "sonic"): BaselineEntry(
                "app/backend/some_module.py", "sonic", 3, "#74",
            ),
        }
        for new_count in (2, 0):
            with self.subTest(new_count=new_count):
                reason = _classify_hit("app/backend/some_module.py", "sonic", new_count, baseline)
                self.assertIsNotNone(reason)
                self.assertIn("lower", reason)

    def test_directory_exception_allows_any_count_for_its_own_brand(self):
        """Mutation-style unit check: app/backend/static/ and tests/conformance/testdata/ are
        exempt from per-file counting entirely -- any count of their allowed brand is fine."""
        for count in (0, 1, 999):
            with self.subTest(count=count):
                self.assertIsNone(
                    _classify_hit("app/backend/static/assets/app.js", "sonic", count, {})
                )
                self.assertIsNone(
                    _classify_hit(
                        "tests/conformance/testdata/menu.json", "sonic", count, {}
                    )
                )

    def test_directory_exception_still_forbids_a_brand_it_does_not_rescue(self):
        """Mutation-style unit check: a directory exception only rescues the brand(s) it lists
        (both today's exceptions list only 'sonic') -- a foreign brand there is still a
        failure, not silently waved through by the directory match."""
        self.assertIsNotNone(
            _classify_hit("app/backend/static/assets/app.js", "dunkin", 1, {})
        )

    def test_conformance_testdata_persona_subfolder_may_say_its_own_brand(self):
        """#78/#79: a pack's own per-persona conformance testdata subfolder
        (tests/conformance/testdata/personas/<id>/**) may say its own brand -- mirrors
        personas/<id>/**'s ownership rule, so a new pack's golden fixtures aren't limited to
        the flat tests/conformance/testdata/ directory exception's sonic-only rule."""
        for count in (0, 1, 999):
            with self.subTest(count=count):
                self.assertIsNone(
                    _classify_hit(
                        "tests/conformance/testdata/personas/dunkin/golden-menu-categories.json",
                        "dunkin", count, {},
                    )
                )
                self.assertIsNone(
                    _classify_hit(
                        "tests/conformance/testdata/personas/mcdonalds/golden-order-pricing.json",
                        "mcdonalds", count, {},
                    )
                )

    def test_conformance_testdata_persona_subfolder_forbids_a_foreign_brand(self):
        """Mutation-style unit check: a pack's per-persona testdata subfolder only rescues its
        OWN brand -- a foreign brand word there is still forbidden, and no BASELINE entry can
        rescue it (same as personas/<id>/**'s cross-brand-leak rule)."""
        self.assertIsNotNone(
            _classify_hit(
                "tests/conformance/testdata/personas/dunkin/golden-menu-categories.json",
                "sonic", 1, {},
            )
        )
        self.assertIsNotNone(
            _classify_hit(
                "tests/conformance/testdata/personas/dunkin/golden-menu-categories.json",
                "mcdonalds", 1,
                {("tests/conformance/testdata/personas/dunkin/golden-menu-categories.json", "mcdonalds"):
                    BaselineEntry(
                        "tests/conformance/testdata/personas/dunkin/golden-menu-categories.json",
                        "mcdonalds", 1, "#78",
                    )},
            )
        )

    def test_conformance_testdata_flat_files_are_unaffected_by_the_persona_subfolder_rule(self):
        """Sanity: a file directly under tests/conformance/testdata/ (not inside a personas/
        subfolder) still only goes through the flat, sonic-only DIRECTORY_EXCEPTIONS entry --
        the new per-persona rule must not accidentally widen what the flat files are allowed
        to say."""
        self.assertIsNone(
            _classify_hit("tests/conformance/testdata/golden-menu-categories.json", "sonic", 1, {})
        )
        self.assertIsNotNone(
            _classify_hit("tests/conformance/testdata/golden-menu-categories.json", "dunkin", 1, {})
        )

    # ── Terminology checks (unrelated to brand packs; unchanged by #76) ──

    def test_no_crew_member_references(self):
        """'crew member' should have been replaced with 'carhop' in SHARED code. Scoped away
        from a persona's own pack (personas/<id>/**, #78/#79): a pack may legitimately use
        'crew member' as its own role-name vocabulary (e.g. McDonald's real staff title) even
        though Sonic's own vocabulary is 'carhop' -- see the module docstring above
        _is_inside_a_persona_pack."""
        files = _collect_terminology_scan_files()
        pattern, label = FORBIDDEN_PATTERNS[1]  # crew member
        hits = []
        for filepath in files:
            if _is_inside_a_persona_pack(filepath):
                continue
            try:
                lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            for line_no, line in enumerate(lines, start=1):
                if pattern.search(line):
                    rel = filepath.relative_to(PROJECT_ROOT)
                    hits.append(f"  {rel}:{line_no}  →  {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\n{len(hits)} file(s) still reference '{label}':\n" + "\n".join(hits),
        )

    def test_persona_pack_may_legitimately_use_crew_member(self):
        """Sanity: the shared-code-only scoping (test_no_crew_member_references /
        test_no_terminology_forbidden_terms_combined) skips a hit precisely when
        _persona_pack_id recognizes the path as inside personas/<id>/** -- so a pack (e.g.
        McDonald's, whose real staff title is legitimately 'crew member', #78) is never
        flagged for using its own vocabulary, unlike Sonic's own choice of 'carhop'."""
        self.assertIsNotNone(_persona_pack_id("personas/mcdonalds/prompts/system_prompt.yaml"))
        self.assertIsNone(_persona_pack_id("app/backend/session_manager.py"))

    def test_no_coffee_chat_references(self):
        """Old repo name 'coffee-chat' should not appear in source files."""
        files = _collect_terminology_scan_files()
        pattern, label = FORBIDDEN_PATTERNS[2]  # coffee-chat
        hits = []
        for filepath in files:
            try:
                lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            for line_no, line in enumerate(lines, start=1):
                # Skip upstream attribution URLs
                if "github.com/john-carroll-sw/coffee-chat" in line:
                    continue
                if pattern.search(line):
                    rel = filepath.relative_to(PROJECT_ROOT)
                    hits.append(f"  {rel}:{line_no}  →  {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\n{len(hits)} file(s) still reference '{label}':\n" + "\n".join(hits),
        )

    def test_no_terminology_forbidden_terms_combined(self):
        """Catch-all: scan every source file for crew-member/coffee-chat at once (dunkin is
        covered separately/more precisely by the brand-word guard above). 'crew member' hits
        inside a persona's own pack are also skipped here, same scoping rationale as
        test_no_crew_member_references."""
        files = _collect_terminology_scan_files()
        hits = _scan_for_forbidden(files)
        formatted = []
        for filepath, line_no, line_text, label in hits:
            if label == "dunkin":
                continue  # superseded by test_no_disallowed_brand_words_in_shared_code
            if label.startswith("crew member") and _is_inside_a_persona_pack(filepath):
                continue  # shared-code-only check; packs may use their own vocabulary (#78/#79)
            rel = filepath.relative_to(PROJECT_ROOT)
            formatted.append(f"  [{label}] {rel}:{line_no}  →  {line_text}")

        self.assertEqual(
            formatted, [],
            f"\n{len(formatted)} forbidden reference(s) remain:\n" + "\n".join(formatted),
        )

    # ── Targeted file checks ─────────────────────────────────────────

    def test_readme_title_contains_azure(self):
        """README.md project title/heading must mention 'Azure'.

        #69 (P2-0): the repo-level title/intro were neutralized to
        AzureAIDriveThru — brand-specific wording (Sonic, McDonald's, Dunkin)
        now lives in persona packs and stays in the README body only. This
        guard used to require "Sonic" in the heading; it now requires
        "Azure" instead, and the frontend/backend Sonic-title guards below
        are unchanged (those files are out of #69's scope).
        """
        readme = PROJECT_ROOT / "README.md"
        self.assertTrue(readme.exists(), "README.md not found at project root")
        content = readme.read_text(encoding="utf-8", errors="replace")
        first_heading = ""
        for line in content.splitlines():
            if line.startswith("# "):
                first_heading = line
                break
        self.assertTrue(
            "azure" in first_heading.lower(),
            f"README.md first heading does not mention Azure: '{first_heading}'",
        )

    def test_readme_does_not_mention_dunkin(self):
        """README.md must be completely free of Dunkin references."""
        readme = PROJECT_ROOT / "README.md"
        self.assertTrue(readme.exists(), "README.md not found at project root")
        content = readme.read_text(encoding="utf-8", errors="replace")
        hits = []
        for line_no, line in enumerate(content.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  README.md:{line_no}  →  {line.strip()}")
        self.assertEqual(
            hits, [],
            "\nREADME.md still references Dunkin:\n" + "\n".join(hits),
        )

    def test_frontend_index_html_title_contains_sonic(self):
        """app/frontend/index.html <title> must contain 'Sonic'."""
        index = FRONTEND_DIR / "index.html"
        self.assertTrue(index.exists(), "app/frontend/index.html not found")
        content = index.read_text(encoding="utf-8", errors="replace")
        title_match = re.search(r"<title>(.*?)</title>", content, re.IGNORECASE)
        self.assertIsNotNone(title_match, "No <title> tag found in index.html")
        title_text = title_match.group(1)
        self.assertTrue(
            "sonic" in title_text.lower(),
            f"index.html <title> does not mention Sonic: '{title_text}'",
        )

    def test_frontend_index_html_no_dunkin(self):
        """app/frontend/index.html must not reference Dunkin anywhere."""
        index = FRONTEND_DIR / "index.html"
        self.assertTrue(index.exists(), "app/frontend/index.html not found")
        content = index.read_text(encoding="utf-8", errors="replace")
        hits = []
        for line_no, line in enumerate(content.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  index.html:{line_no}  →  {line.strip()}")
        self.assertEqual(
            hits, [],
            "\nindex.html still references Dunkin:\n" + "\n".join(hits),
        )

    def test_backend_system_prompt_mentions_sonic(self):
        """The system prompt must reference 'Sonic'."""
        # System prompt externalized to YAML — read from the source file
        prompt_yaml = PROJECT_ROOT / "personas" / "sonic" / "prompts" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace")
        else:
            # Fallback: check app.py for inline system_message
            app_py = BACKEND_DIR / "app.py"
            self.assertTrue(app_py.exists(), "app/backend/app.py not found")
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1)

        self.assertTrue(
            "sonic" in prompt_text.lower(),
            "system prompt does not mention 'Sonic'",
        )

    def test_backend_system_prompt_no_dunkin(self):
        """The backend system prompt must NOT reference 'Dunkin'."""
        prompt_yaml = PROJECT_ROOT / "personas" / "sonic" / "prompts" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace")
        else:
            app_py = BACKEND_DIR / "app.py"
            self.assertTrue(app_py.exists(), "app/backend/app.py not found")
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1)

        hits = []
        for i, line in enumerate(prompt_text.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  system prompt line {i}: {line.strip()}")

        self.assertEqual(
            hits, [],
            "\nsystem prompt still references Dunkin:\n" + "\n".join(hits),
        )

    def test_backend_system_prompt_uses_carhop_not_crew_member(self):
        """The system prompt should say 'carhop', not 'crew member'."""
        prompt_yaml = PROJECT_ROOT / "personas" / "sonic" / "prompts" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace").lower()
        else:
            app_py = BACKEND_DIR / "app.py"
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1).lower()

        self.assertNotIn(
            "crew member", prompt_text,
            "system prompt still uses 'crew member' — should be 'carhop'",
        )

    # ── Scan finds files sanity check ────────────────────────────────

    def test_scan_finds_expected_file_types(self):
        """Sanity: the scanner should find .py, .ts/.tsx, .html, and .md files."""
        files = _collect_terminology_scan_files()
        extensions_found = {p.suffix for p in files}
        for ext in (".py", ".html", ".md"):
            self.assertIn(
                ext, extensions_found,
                f"Scanner did not find any {ext} files — check SCAN_EXTENSIONS / TERMINOLOGY_EXCLUDED_DIRS",
            )


if __name__ == "__main__":
    unittest.main()
