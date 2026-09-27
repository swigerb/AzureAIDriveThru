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
  3. inside a shared-code file that has an explicit ALLOWLIST entry below, each of which MUST
     carry an issue reference (validated by test_every_allowlist_entry_has_an_issue_reference)
     naming the work that will remove it -- #74 (session-scoped persona binding), #78/#79
     (the McDonald's/Dunkin packs themselves), #80 (frontend runtime theming), #86 (README
     naming all three brands), or #76 itself (this groundwork's own CI matrix literal).

Anywhere else, a brand word is forbidden. Every ALLOWLIST entry today rescues only "sonic"
(the actual, current leftover) -- if a stray "Dunkin"/"McDonald's" ever showed up in shared
code, it would still fail (see AllowlistEntry.brands).

The old "crew member" (should be carhop) and "coffee-chat" (old repo name) terminology checks
are unrelated to the brand-pack architecture and are unchanged by this inversion -- they still
apply everywhere except the same repo-meta/historical exclusions as before.

Author: Birdperson (Tester)
"""

import re
import unittest
from dataclasses import dataclass, field
from pathlib import Path

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


# ── Brand-word guard (#76 inversion) ──────────────────────────────────────

# Canonical brand -> matching pattern. A pack under personas/<id>/** may only contain the
# word matching its own id; every other brand word there is a cross-brand leak, same as in
# any other shared file.
BRAND_PATTERNS: dict[str, "re.Pattern[str]"] = {
    "sonic": re.compile(r"\bsonic\b", re.IGNORECASE),
    "mcdonalds": re.compile(r"\bmcdonald'?s?\b", re.IGNORECASE),
    "dunkin": re.compile(r"\bdunkin'?\b", re.IGNORECASE),
}

# Brand-word scan intentionally does NOT exclude "adr" or "personas" the way the terminology
# scan does -- #76 needs both to actively participate so the cross-brand-doc and
# persona-pack-ownership rules below can classify each hit, instead of the pre-#76 approach of
# blanket-skipping them so a "no Dunkin anywhere" rule wouldn't misfire on them.
BRAND_EXCLUDED_DIRS = BASE_EXCLUDED_DIRS
BRAND_EXCLUDED_FILES = {
    "voice_rag_README.md",
    "test_rebrand_verification.py",
}


@dataclass(frozen=True)
class AllowlistEntry:
    """One shared-code location where a brand word is tolerated pending migration.

    `prefix` is a POSIX-style, repo-root-relative path: a trailing "/" means "this directory
    and everything under it", no trailing "/" means "this exact file". `issue` MUST be a
    GitHub issue reference like "#74" -- test_every_allowlist_entry_has_an_issue_reference
    fails the moment any entry's issue is missing/malformed, so nothing can land in this
    allowlist as a silent, untracked permanent exception. `brands` restricts which brand
    word(s) this entry rescues (default: only "sonic", since every real hit in the repo today
    is a pending-Sonic-migration leftover, not a genuine McDonald's/Dunkin reference slipping
    into shared code -- narrowing the default keeps a stray wrong-brand word from accidentally
    riding along on an entry that was only ever meant to cover "sonic").
    """

    prefix: str
    issue: str
    reason: str
    brands: frozenset = field(default_factory=lambda: frozenset({"sonic"}))


ALLOWLIST: list[AllowlistEntry] = [
    AllowlistEntry(
        "app/backend/tests/", "#78",
        "Backend test fixtures/paths reference personas/sonic/** directly because it's the "
        "only persona pack that exists yet -- #78/#79 land the McDonald's/Dunkin packs, at "
        "which point these tests get parameterized per-persona instead of hardcoding Sonic.",
    ),
    AllowlistEntry(
        "app/backend/static/", "#80",
        "Gitignored built-frontend output (npm run build's outDir) -- mirrors the frontend's "
        "own pending persona theming (#80); nothing here is hand-edited.",
    ),
    AllowlistEntry(
        "app/backend/", "#74",
        "Backend config/persona/prompt loader defaults, logger names and illustrative "
        "docstring examples still default/hardcode to the single Sonic persona pending #74's "
        "session-scoped persona binding.",
    ),
    AllowlistEntry(
        "app/frontend/", "#80",
        "Frontend UI copy, locales, theming and menu data are Sonic-only pending #80's "
        "persona-aware runtime theming (frontend is out of this issue's scope to edit). "
        "Also rescues 'mcdonalds' for brandColorTokens.test.ts, which uses McDonald's red "
        "as a non-Sonic-brand-hex test fixture, not real branding.",
        brands=frozenset({"sonic", "mcdonalds"}),
    ),
    AllowlistEntry(
        ".copilot/skills/sonic-menu-parsing/", "#78",
        "Copilot skill describing how to parse personas/sonic's own menu JSON specifically; "
        "#78/#79 will need an equivalent (or generalized) skill once other persona menu files "
        "exist.",
    ),
    AllowlistEntry(
        "README.md", "#86",
        "Top-level README currently reads Sonic-only; #86 tracks updating it to name all "
        "three brands. Also rescues 'mcdonalds' for the Local-mode paragraph's reference to "
        "the upstream voice_rag McDonald's demo this repo was forked from.",
        brands=frozenset({"sonic", "mcdonalds"}),
    ),
    AllowlistEntry(
        "DEPLOY.md", "#86",
        "Deployment walkthrough uses the Sonic app name/image tag as its running example; "
        "same top-level-docs naming work as #86.",
    ),
    AllowlistEntry(
        "docs/", "#78",
        "Non-ADR docs (customizing_deploy.md, dotnet_mapping.md, existing_services.md, "
        "order_resume.md) use the Sonic persona as the illustrative current example pending "
        "other persona packs landing. (docs/adr/** and persona-architecture.md are handled by "
        "the cross-brand-doc rule above this allowlist, not this entry.)",
    ),
    AllowlistEntry(
        "scripts/", "#78",
        "Operational scripts (deploy/build/benchmark/menu-maintenance) are Sonic-only pending "
        "other persona packs; #78/#79 will need them parameterized or duplicated per persona.",
    ),
    AllowlistEntry(
        "infra/main.parameters.json", "#78",
        "Infra default naming/tags reference Sonic pending other persona packs landing. "
        "(Recorded here only -- infra/** itself is out of this issue's scope to edit.)",
    ),
    AllowlistEntry(
        "tests/conformance/README.md", "#78",
        "Harness docs reference Sonic as the only persona pack that exists today.",
    ),
    AllowlistEntry(
        "tests/conformance/testdata/", "#78",
        "Golden order-pricing/menu-category datasets are sourced from Sonic's own menu; will "
        "need per-persona equivalents once other packs exist.",
    ),
    AllowlistEntry(
        ".github/workflows/conformance.yml", "#76",
        "This groundwork's own persona matrix literal (`persona: [sonic]`) and surrounding "
        "comments -- the only enabled persona today; ConformancePersonas' own disk discovery "
        "means no further CI edit is needed once #78/#79 add more packs. Also rescues "
        "'mcdonalds'/'dunkin' for the comment naming the not-yet-existing persona packs "
        "#78/#79 will add.",
        brands=frozenset({"sonic", "mcdonalds", "dunkin"}),
    ),
]


def _relative_posix(path: Path) -> str:
    return path.relative_to(PROJECT_ROOT).as_posix()


def _persona_pack_id(rel_posix: str):
    """Returns the persona id if rel_posix is under personas/<id>/**, else None."""
    parts = rel_posix.split("/")
    return parts[1] if len(parts) >= 2 and parts[0] == "personas" else None


def _is_cross_brand_doc(rel_posix: str) -> bool:
    """docs/adr/** and docs/persona-architecture.md compare all three brands by design (ADR-001)."""
    return rel_posix == "docs/persona-architecture.md" or rel_posix.startswith("docs/adr/")


def _allowlist_entry_for(rel_posix: str):
    """Longest-prefix match: a more specific entry (e.g. app/backend/tests/) wins over a more
    general one (app/backend/) covering the same file."""
    matches = [
        e for e in ALLOWLIST
        if (not e.prefix.endswith("/") and rel_posix == e.prefix)
        or (e.prefix.endswith("/") and rel_posix.startswith(e.prefix))
    ]
    if not matches:
        return None
    return max(matches, key=lambda e: len(e.prefix))


def _forbidden_reason(rel_posix: str, brand: str):
    """Returns None if `brand` is allowed at rel_posix, else a human-readable reason it's not."""
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

    entry = _allowlist_entry_for(rel_posix)
    if entry is not None and brand in entry.brands:
        return None

    return (
        f"'{brand}' appears in shared code with no allowlist entry covering it -- either this "
        f"is a genuine leftover to fix, or (if pending migration) add an ALLOWLIST entry "
        f"above with an issue reference"
    )


def _scan_for_brand_violations() -> list[tuple[str, int, str, str, str]]:
    """Returns (rel_posix, line_no, brand, line_text, reason) for every disallowed brand-word hit."""
    files = _collect_source_files(BRAND_EXCLUDED_DIRS, BRAND_EXCLUDED_FILES)
    violations: list[tuple[str, int, str, str, str]] = []
    for filepath in files:
        rel_posix = _relative_posix(filepath)
        try:
            lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
        except Exception:
            continue
        for line_no, line in enumerate(lines, start=1):
            for brand, pattern in BRAND_PATTERNS.items():
                if not pattern.search(line):
                    continue
                reason = _forbidden_reason(rel_posix, brand)
                if reason is not None:
                    violations.append((rel_posix, line_no, brand, line.strip(), reason))
    return violations


# ── Test class ───────────────────────────────────────────────────────────

class TestRebrandVerification(unittest.TestCase):
    """#76: brand words are allowed only in their own persona pack, the explicit cross-brand
    docs, or an allowlisted shared-code location that carries an issue reference -- forbidden
    everywhere else. "crew member"/"coffee-chat" terminology checks are unrelated and
    unchanged."""

    # ── Brand-word guard (inverted, #76) ─────────────────────────────

    def test_no_disallowed_brand_words_in_shared_code(self):
        """The main inverted guard: every brand-word hit outside its own persona pack, the
        cross-brand docs, or an allowlisted location is a failure."""
        violations = _scan_for_brand_violations()
        formatted = [
            f"  [{brand}] {rel}:{line_no}  →  {line_text}\n      {reason}"
            for rel, line_no, brand, line_text, reason in violations
        ]
        self.assertEqual(
            formatted, [],
            f"\n{len(formatted)} disallowed brand-word reference(s):\n" + "\n".join(formatted),
        )

    def test_every_allowlist_entry_has_an_issue_reference(self):
        """Every ALLOWLIST entry must carry a well-formed issue reference like '#74' -- an
        entry without one would be an untracked, silent permanent exception."""
        bad = [e.prefix for e in ALLOWLIST if not re.fullmatch(r"#\d+", e.issue or "")]
        self.assertEqual(
            bad, [],
            f"\nAllowlist entries missing a valid issue reference (e.g. '#74'): {bad}",
        )

    def test_allowlist_prefixes_are_unique(self):
        """Sanity: no two entries should target the exact same prefix (the longest-prefix-wins
        matcher would silently pick one and ignore the other's reason/issue)."""
        prefixes = [e.prefix for e in ALLOWLIST]
        self.assertEqual(
            len(prefixes), len(set(prefixes)),
            f"Duplicate ALLOWLIST prefixes found: {prefixes}",
        )

    def test_sonic_persona_pack_may_say_sonic(self):
        """Sanity: the pack-ownership rule must not accidentally forbid a pack from saying its
        own brand -- personas/sonic/menu/menuItems.json (issue #70) is a real, populated file
        that must legitimately say 'Sonic'."""
        menu_path = PROJECT_ROOT / "personas" / "sonic" / "menu" / "menuItems.json"
        self.assertTrue(menu_path.exists(), "personas/sonic/menu/menuItems.json not found")
        rel_posix = _relative_posix(menu_path)
        self.assertIsNone(_forbidden_reason(rel_posix, "sonic"))

    def test_cross_brand_docs_may_say_any_brand(self):
        """Sanity: docs/persona-architecture.md must legitimately be allowed to say all three
        brand names (it compares them by design, ADR-001)."""
        doc_path = PROJECT_ROOT / "docs" / "persona-architecture.md"
        self.assertTrue(doc_path.exists(), "docs/persona-architecture.md not found")
        rel_posix = _relative_posix(doc_path)
        for brand in BRAND_PATTERNS:
            self.assertIsNone(_forbidden_reason(rel_posix, brand))

    def test_a_foreign_brand_word_inside_a_persona_pack_is_forbidden(self):
        """Mutation-style unit check (no real file touched): 'dunkin' inside personas/sonic/**
        must be forbidden even though 'sonic' there is fine -- a persona pack must not
        reference a different brand."""
        self.assertIsNone(_forbidden_reason("personas/sonic/menu/menuItems.json", "sonic"))
        self.assertIsNotNone(_forbidden_reason("personas/sonic/menu/menuItems.json", "dunkin"))

    def test_a_brand_word_in_an_unlisted_shared_file_is_forbidden(self):
        """Mutation-style unit check (no real file touched): a brand word in a shared-code path
        with no ALLOWLIST entry at all must be forbidden."""
        self.assertIsNotNone(_forbidden_reason("app/some_new_top_level_module.py", "dunkin"))

    def test_a_brand_word_in_an_allowlisted_shared_file_is_allowed(self):
        """Mutation-style unit check (no real file touched): 'sonic' inside an allowlisted
        prefix (e.g. app/backend/) is allowed, matching what a real 'Sonic' leftover there
        resolves to today."""
        self.assertIsNone(_forbidden_reason("app/backend/some_new_module.py", "sonic"))

    # ── Terminology checks (unrelated to brand packs; unchanged by #76) ──

    def test_no_crew_member_references(self):
        """'crew member' should have been replaced with 'carhop' everywhere."""
        files = _collect_terminology_scan_files()
        pattern, label = FORBIDDEN_PATTERNS[1]  # crew member
        hits = []
        for filepath in files:
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
        covered separately/more precisely by the brand-word guard above)."""
        files = _collect_terminology_scan_files()
        hits = _scan_for_forbidden(files)
        formatted = []
        for filepath, line_no, line_text, label in hits:
            if label == "dunkin":
                continue  # superseded by test_no_disallowed_brand_words_in_shared_code
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
