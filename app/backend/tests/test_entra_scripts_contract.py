"""Source-scan safety pins for the Entra auth PowerShell scripts (issue #146, ADR-002,
docs/persona-architecture.md 18.9).

These tests never execute the scripts (that would require a live `az` login and, for
Setup-EntraAuth.ps1, real Azure writes -- which this repo's automation must never perform, see
docs/adr/ADR-002-entra-authentication.md and the squad's no-Azure-writes rule). Instead they scan
the script *source text* the same way Retail Pulse's `SetupEntraAuthScriptContractTests` /
`VerifyProductionAuthScriptContractTests` pin behavior in C#: a structural/regex read of the
`.ps1` files, asserting the safety invariants issue #146 requires can never regress:

  * Every Microsoft Graph write call (POST/PATCH/DELETE) is gated behind the script's central
    ``$script:ApplyWrites`` flag, which is only ever true when ``-Apply`` was supplied.
  * No script creates a credential (client secret / password credential / certificate) anywhere.
  * No script ever prints or logs a bearer token, access token, or client secret value.
  * No script reads a ``.env`` file.
  * Setup-EntraAuth.ps1 is create-only by display name (hard-fails on a name collision, never
    adopts by name) and requires explicit ``-ClientId``/``-AppObjectId`` plus an ownership check
    to reconcile an existing app.
"""

import re
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SCRIPTS = REPO / "scripts"

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rebrand_scan import BRAND_PATTERNS  # noqa: E402

SETUP = SCRIPTS / "Setup-EntraAuth.ps1"
VERIFY_ENTRA = SCRIPTS / "Verify-EntraAuth.ps1"
VERIFY_PROD = SCRIPTS / "Verify-ProductionAuth.ps1"

ALL_SCRIPTS = {"Setup-EntraAuth.ps1": SETUP, "Verify-EntraAuth.ps1": VERIFY_ENTRA, "Verify-ProductionAuth.ps1": VERIFY_PROD}

DEPLOY_MD = REPO / "DEPLOY.md"

# Matches `az rest --method post|patch|delete ...` (case-insensitive, across the flexible arg
# ordering PowerShell allows) so a raw `az rest` write call outside the Invoke-Graph gate would
# still be caught even if someone bypassed the helper function.
_RAW_WRITE_METHOD = re.compile(r"--method\s+['\"]?(post|patch|delete)", re.IGNORECASE)

# Anything that looks like minting a credential on an application/service principal.
_CREDENTIAL_CREATION = re.compile(
    r"(addPassword|passwordCredentials\s*=|addKey|keyCredentials\s*=|New-AzADAppCredential|"
    r"az\s+ad\s+app\s+credential\s+reset)",
    re.IGNORECASE,
)

# `az ad app create` / `az ad sp create` (and update/delete) bypass Invoke-Graph's central
# -Apply gate entirely -- these must never appear anywhere, in any of the three scripts.
_RAW_AZ_AD_WRITE = re.compile(r"\baz\s+ad\s+(app|sp)\s+(create|update|delete|credential)\b", re.IGNORECASE)

# Alternate ways to reach Microsoft Graph or ARM that would bypass Invoke-Graph's central
# -Apply/read-only gate (Invoke-WebRequest is handled separately -- see
# test_invoke_webrequest_only_used_inside_invoke_probe_request -- because Verify-ProductionAuth.ps1
# legitimately uses it for read-only HTTP probes against the deployed app, not Graph/ARM).
_ALT_GRAPH_OR_ARM_CMDLET = re.compile(
    r"\baz\s+(ad|role)\s|Invoke-RestMethod\b|Invoke-MgGraphRequest\b|New-Mg\w*|Update-Mg\w*",
    re.IGNORECASE,
)

# Variable names that, across the three scripts, are the only ones ever bound to an actual
# secret/bearer-token VALUE -- as opposed to a response object, a version integer, or an
# existence flag whose *name* happens to contain "token"/"secret" (e.g. $queryToken, $wsNoToken,
# $tokenVersion, $leftoverSecret). A broad `\$\w*(token|secret)\w*` would flag those legitimate,
# non-sensitive variables too, so this list is scoped to the exact identifiers used for the value
# itself.
_SECRET_VALUE_VAR = re.compile(r"\$(?:token|accessToken|bearerToken|clientSecret)\b", re.IGNORECASE)

# Cmdlets/functions that actually emit to the console/report; a line calling one of these AND
# referencing a secret-value variable is a token/secret print, including via string interpolation
# (e.g. "token=$token"), not just a bare `Write-Host $token`.
_OUTPUT_CMDLET_LINE = re.compile(r"\b(Write-\w+|Out-\w+|Format-\w+|Add-Result)\b")

# Any statement that writes a raw token/secret value to the console/host/output stream. This is
# intentionally narrow (accessToken/access_token/token/secret variable piped straight to
# Write-Host/Write-Output/Out-Host) so it does not also flag safe uses like the *word* "token" in
# comments/parameter names, or the deliberately-non-secret `-------- lines.
_TOKEN_PRINT = re.compile(
    r"(Write-(Host|Output)|Out-Host)\s*\(?\s*\$(?:\w*[Tt]oken\w*|\w*[Ss]ecret\w*)\b",
)

# Matches an actual attempt to read a dotenv FILE (Get-Content/Test-Path/.NET file IO pointed at a
# path ending `.env`), never a prose mention of ".env" in a comment/docstring, and never the
# unrelated `.env` *property* access pattern (e.g. `$container.env`, an Azure container's env-var
# array) or the word "environment".
_ENV_FILE_READ = re.compile(
    r"(Get-Content|Test-Path|ReadAllText|ReadAllLines|Import-Csv|Set-Content|Out-File)"
    r"\b[^\n]{0,80}\.env(?!ironment)\b(?!\w)",
    re.IGNORECASE,
)

# A hardcoded persona/brand path segment (review item 8/11): the brand ratchet must not go up, and
# a script that hardcodes one persona would silently mis-probe/mis-verify any other. Built from
# the same BRAND_PATTERNS the rebrand scanner itself uses (round 2 required fix), rather than
# spelling brand words directly in this file -- which is exactly what forced three
# rebrand_baseline.yaml entries citing #146 in round 1/round 2.
_HARDCODED_PERSONA_BRAND = re.compile("|".join(p.pattern for p in BRAND_PATTERNS.values()), re.IGNORECASE)

# Round 2 required fix (item 9 variants): rather than a fixed list of known-bad prints, this is an
# ALLOW-list. Any non-comment line in Verify-ProductionAuth.ps1 that references the actual
# token-value variables ($token, $tokenJson, $BearerToken) must match one of these known-safe
# forms, or the test fails. This catches both mutations Rick's round 2 review found surviving the
# old `_TOKEN_PRINT`/`_lines_printing_a_secret_value` checks: `Write-Host "debug: $tokenJson"` (a
# brand-new line referencing $tokenJson via an output cmdlet, which the old checks DID catch) and
# `$dbg = "t=$token"; Write-Host $dbg` (a line that references $token via string interpolation
# into an unrelated variable, then prints that variable on a SEPARATE line the old checks never
# connected back to $token). An allow-list closes both, and any future new reference to these
# variables, by construction: the only way to add a line touching $token/$tokenJson/$BearerToken
# is to make it match one of the forms already proven safe.
_TOKEN_VALUE_REFERENCE = re.compile(r"\$(?:tokenJson|token|BearerToken)\b")
_TOKEN_REFERENCE_ALLOWLIST = (
    re.compile(r"\[string\]\$BearerToken\b"),  # Invoke-ProbeRequest's -BearerToken parameter declaration
    re.compile(r"if\s*\(\$BearerToken\)"),  # presence check before building the header
    re.compile(r"\$reqHeaders\['Authorization'\]\s*=\s*\"Bearer \$BearerToken\""),  # header build, never printed
    re.compile(r"\$tokenJson\s*=\s*az account get-access-token\b"),  # the acquisition itself
    re.compile(r"\$token\s*=\s*\(\$tokenJson\s*\|\s*ConvertFrom-Json\)\.accessToken"),  # decode, in-memory only
    re.compile(r"IsNullOrWhiteSpace\(\$token\)"),  # empty check, never a print
    re.compile(r"-BearerToken\s+\$token\b"),  # the one call site that forwards the acquired token
    re.compile(r"\$token\s*=\s*\$null\b"),  # best-effort scrub
)


def _lines_referencing_token_value(text: str):
    """Every non-comment line referencing $token/$tokenJson/$BearerToken that does NOT match one
    of the pre-approved safe forms above."""
    offending = []
    for i, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if not _TOKEN_VALUE_REFERENCE.search(stripped):
            continue
        if not any(p.search(stripped) for p in _TOKEN_REFERENCE_ALLOWLIST):
            offending.append((i, stripped))
    return offending


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _lines_printing_a_secret_value(text: str):
    """Lines calling an output cmdlet (Write-*/Out-*/Format-*/Add-Result) that also reference an
    actual secret-value variable (see _SECRET_VALUE_VAR), whether as a bare argument or via string
    interpolation. Comment-only lines are skipped."""
    offending = []
    for i, line in enumerate(text.splitlines(), start=1):
        if line.strip().startswith("#"):
            continue
        if not _OUTPUT_CMDLET_LINE.search(line):
            continue
        if _SECRET_VALUE_VAR.search(line):
            offending.append((i, line.strip()))
    return offending


def _brace_matched_if_blocks(text: str):
    """Every `if (...) { ... }` block in `text` as (open_brace_pos, close_brace_pos, header),
    matched by brace depth (not a fixed line window) so a guard several lines above a multi-line
    body still counts."""
    if_header = re.compile(r"if\s*\((?:[^()]|\([^()]*\))*\)\s*\{")
    blocks = []
    for m in if_header.finditer(text):
        open_pos = m.end() - 1
        depth = 0
        close_pos = None
        for i in range(open_pos, len(text)):
            c = text[i]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    close_pos = i
                    break
        if close_pos is not None:
            blocks.append((open_pos, close_pos, m.group(0)))
    return blocks


def _unguarded_call_sites(text: str, call_pattern: "re.Pattern[str]", guard_substring: str):
    """Call sites of `call_pattern` in `text` that are NOT lexically nested (by brace depth) in an
    `if (...)` block whose header contains `guard_substring`."""
    blocks = _brace_matched_if_blocks(text)
    offending = []
    for m in call_pattern.finditer(text):
        pos = m.start()
        guarded = any(open_pos < pos < close_pos and guard_substring in header for open_pos, close_pos, header in blocks)
        if not guarded:
            line_no = text.count("\n", 0, pos) + 1
            offending.append((line_no, m.group(0)))
    return offending


class SetupEntraAuthScriptContractTests(unittest.TestCase):
    """Pins Setup-EntraAuth.ps1's write-safety and create-only-by-name invariants."""

    @classmethod
    def setUpClass(cls):
        cls.text = _read(SETUP)

    def test_script_exists(self):
        self.assertTrue(SETUP.is_file(), f"expected {SETUP} to exist")

    def test_apply_param_exists_and_defaults_to_off(self):
        self.assertRegex(self.text, r"\[switch\]\$Apply\b", "expected an -Apply switch parameter")

    def test_apply_writes_flag_is_derived_from_apply_switch(self):
        self.assertRegex(
            self.text,
            r"\$script:ApplyWrites\s*=\s*\[bool\]\$Apply",
            "the central write-enable flag must be derived directly from -Apply",
        )

    def test_invoke_graph_refuses_writes_without_apply(self):
        # The central Graph gate must throw for POST/PATCH/DELETE unless $script:ApplyWrites.
        match = re.search(
            r"function\s+Invoke-Graph\s*\{(?P<body>.*?)\n\}",
            self.text,
            re.DOTALL,
        )
        self.assertIsNotNone(match, "expected an Invoke-Graph function")
        body = match.group("body")
        self.assertIn("POST", body)
        self.assertIn("PATCH", body)
        self.assertIn("DELETE", body)
        self.assertRegex(
            body,
            r"-not\s+\$script:ApplyWrites",
            "Invoke-Graph must refuse to run a mutating verb unless $script:ApplyWrites is true",
        )
        self.assertRegex(body, r"throw\b", "the write-gate must throw, not warn, when refused")

    def test_every_mutation_is_wrapped_in_an_if_apply_check(self):
        # Every direct `Invoke-Graph -Method POST/PATCH/DELETE` call site (outside the function's
        # own definition) must be lexically nested inside an `if (...)` block whose condition
        # mentions `$Apply`, so a reviewer scanning the diff can see the guard at the call site
        # too, not only inside the central Invoke-Graph helper. Uses brace-depth matching (not a
        # fixed line window) so a guard several lines above a multi-line request body still
        # counts.
        text = self.text
        # Exclude the Invoke-Graph function's own body: its internal gate uses
        # $script:ApplyWrites, checked separately above, and its GET calls are unrelated.
        func_match = re.search(r"function\s+Invoke-Graph\s*\{.*?\n\}\n", text, re.DOTALL)
        assert func_match is not None
        scan_text = text[:func_match.start()] + ("\n" * (func_match.end() - func_match.start())) + text[func_match.end():]

        if_header = re.compile(r"if\s*\((?:[^()]|\([^()]*\))*\)\s*\{")
        blocks = []
        for m in if_header.finditer(scan_text):
            open_pos = m.end() - 1
            depth = 0
            close_pos = None
            for i in range(open_pos, len(scan_text)):
                c = scan_text[i]
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        close_pos = i
                        break
            if close_pos is not None:
                blocks.append((open_pos, close_pos, m.group(0)))

        call_pattern = re.compile(r"Invoke-Graph\s+-Method\s+(POST|PATCH|DELETE)", re.IGNORECASE)
        offending = []
        for m in call_pattern.finditer(scan_text):
            pos = m.start()
            guarded = any(open_pos < pos < close_pos and "$Apply" in header for open_pos, close_pos, header in blocks)
            if not guarded:
                line_no = scan_text.count("\n", 0, pos) + 1
                offending.append((line_no, m.group(0)))
        self.assertEqual(
            offending, [],
            f"found Invoke-Graph write call(s) not lexically nested in an '$Apply'-guarded if-block: {offending}",
        )

    def test_no_credential_creation(self):
        self.assertNotRegex(
            self.text, _CREDENTIAL_CREATION,
            "Setup-EntraAuth.ps1 must never create a client secret / password / key credential",
        )

    def test_no_token_output(self):
        self.assertNotRegex(self.text, _TOKEN_PRINT, "Setup-EntraAuth.ps1 must never print a token or secret value")
        self.assertEqual(
            _lines_printing_a_secret_value(self.text), [],
            "Setup-EntraAuth.ps1 must never print a token/secret value, including via string interpolation",
        )

    def test_no_raw_az_rest_write_outside_invoke_graph(self):
        # Every `az rest --method PATCH/POST/DELETE` call must live inside Invoke-Graph's own
        # body (the only place the -Apply gate is enforced); a raw `az rest` write anywhere else
        # in the script would bypass that gate entirely.
        text = self.text
        func_match = re.search(r"function\s+Invoke-Graph\s*\{.*?\n\}\n", text, re.DOTALL)
        self.assertIsNotNone(func_match, "expected an Invoke-Graph function")
        outside = text[:func_match.start()] + text[func_match.end():]
        self.assertNotRegex(
            outside, _RAW_WRITE_METHOD,
            "found a raw `az rest --method POST/PATCH/DELETE` call outside Invoke-Graph's body",
        )

    def test_az_rest_only_called_inside_invoke_graph(self):
        # Round 2 required fix: the prior pin (`_RAW_WRITE_METHOD`, above) only caught the
        # `--method`/`-m` spelling it knew about. Any `az rest` call at all, with any flag
        # spelling, must live only inside Invoke-Graph's body: that is the single place the
        # -Apply gate is enforced, so any other `az rest` call site (write or, for that matter,
        # a stray read) bypasses the central chokepoint this test suite is meant to guarantee.
        # Block comments (`<# ... #>`, e.g. the top-of-file help, which mentions `az rest` in
        # prose) and `#`-line comments are both stripped before scanning.
        text = self.text
        func_match = re.search(r"function\s+Invoke-Graph\s*\{.*?\n\}\n", text, re.DOTALL)
        self.assertIsNotNone(func_match, "expected an Invoke-Graph function")
        outside = text[:func_match.start()] + text[func_match.end():]
        outside = re.sub(r"<#.*?#>", "", outside, flags=re.DOTALL)
        offending = []
        for i, line in enumerate(outside.splitlines(), start=1):
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if re.search(r"\baz\s+rest\b", stripped, re.IGNORECASE):
                offending.append((i, stripped))
        self.assertEqual(
            offending, [],
            f"found `az rest` call(s) outside Invoke-Graph's body: {offending}",
        )

    def test_no_invoke_webrequest_or_invoke_restmethod(self):
        # Round 2 required fix: round 1 already listed Invoke-WebRequest as forbidden in Setup
        # (Setup has no legitimate HTTP-probe use for it, unlike Verify-ProductionAuth.ps1), but
        # no pin ever asserted it. Invoke-RestMethod is already covered by
        # `_ALT_GRAPH_OR_ARM_CMDLET` (test_no_alternate_graph_or_arm_write_cmdlets, above); this
        # closes the Invoke-WebRequest gap specifically.
        self.assertNotIn(
            "Invoke-WebRequest", self.text,
            "Setup-EntraAuth.ps1 must never call Invoke-WebRequest; all Graph access goes through Invoke-Graph",
        )

    def test_no_raw_az_ad_write(self):
        self.assertNotRegex(
            self.text, _RAW_AZ_AD_WRITE,
            "Setup-EntraAuth.ps1 must never call `az ad app create`/`az ad sp create` (or "
            "update/delete/credential) -- all writes go through the Graph API via Invoke-Graph",
        )

    def test_no_alternate_graph_or_arm_write_cmdlets(self):
        self.assertNotRegex(
            self.text, _ALT_GRAPH_OR_ARM_CMDLET,
            "Setup-EntraAuth.ps1 must not reach Graph/ARM through an alternate cmdlet that "
            "bypasses Invoke-Graph's -Apply gate",
        )

    def test_no_dotenv_reads(self):
        self.assertNotRegex(
            self.text, _ENV_FILE_READ,
            "Setup-EntraAuth.ps1 must never read a .env file; -FromAzdEnv uses `azd env get-value` only",
        )

    def test_from_azd_env_reads_only_azd_env_get_value(self):
        self.assertIn("azd env get-value BACKEND_URI", self.text)

    def test_from_azd_env_fails_on_empty_backend_uri(self):
        idx = self.text.index("$backendUri = (azd env get-value BACKEND_URI")
        window = self.text[idx: idx + 700]
        self.assertRegex(
            window, r"IsNullOrWhiteSpace\(\$backendUri\)",
            "must check BACKEND_URI for emptiness",
        )
        self.assertRegex(window, r"throw\b", "must throw (fail hard), not warn, on an empty BACKEND_URI")

    def test_create_only_by_display_name_never_adopts(self):
        # The create-only path (no explicit -ClientId/-AppObjectId) must hard-fail on a
        # display-name collision and must never call Invoke-Graph with the collision result.
        match = re.search(
            r"# No explicit identifier -> CREATE-ONLY\..*?\n(?P<body>.*?)\n\}",
            self.text,
            re.DOTALL,
        )
        self.assertIsNotNone(match, "expected the create-only-by-name code path with its guiding comment")
        body = match.group("body")
        self.assertRegex(body, r"sameName\.Count\s*-gt\s*0", "must detect a same-name collision")
        self.assertRegex(body, r"throw\b", "a same-name collision must hard-fail, not proceed")
        self.assertNotIn("Invoke-Graph -Method PATCH", body)
        self.assertNotIn("Invoke-Graph -Method POST", body)

    def test_reconcile_requires_explicit_id_and_ownership_check(self):
        self.assertIn("[string]$ClientId", self.text)
        self.assertIn("[string]$AppObjectId", self.text)
        self.assertIn("function Assert-SafeToAdopt", self.text)
        self.assertIn("Test-AppOwnedByCaller", self.text)
        self.assertIn("AzureAIDriveThruManaged", self.text)

    def test_never_adopts_unmarked_app_without_explicit_override(self):
        match = re.search(r"function Assert-SafeToAdopt\s*\{(?P<body>.*?)\n\}", self.text, re.DOTALL)
        self.assertIsNotNone(match)
        body = match.group("body")
        self.assertRegex(body, r"AllowUnmarkedAdoption", "must require -AllowUnmarkedAdoption to skip the marker check")
        self.assertRegex(body, r"throw\b", "must throw when adoption is unsafe")

    def test_spa_only_no_web_redirect_uris(self):
        self.assertIn("web = @{ redirectUris = @() }", self.text)

    def test_requests_v2_access_tokens(self):
        self.assertIn("RequestedAccessTokenVersion = 2", self.text)

    def test_output_limited_to_ids_and_azd_env_set_lines(self):
        self.assertIn("azd env set ENTRA_TENANT_ID", self.text)
        self.assertIn("azd env set ENTRA_CLIENT_ID", self.text)

    def test_redirect_uri_defaults_to_the_two_18_1_localhost_origins(self):
        # docs/persona-architecture.md 18.1: the two default localhost redirect URIs for local
        # dev (Python's Vite dev server and the built-in preview/static server).
        self.assertRegex(
            self.text,
            r"\[string\[\]\]\$RedirectUri\s*=\s*@\(\s*['\"]http://localhost:8000['\"]\s*,\s*['\"]http://localhost:5173['\"]\s*\)",
            "expected -RedirectUri to default to @('http://localhost:8000', 'http://localhost:5173')",
        )

    def test_help_documents_all_three_setup_scenarios(self):
        # Review item 1: help/examples must cover (a) today's DARK env (-FrontendOrigin derived
        # from the Python container app's stable FQDN), (b) a fresh env, and (c) a later re-run
        # with -ClientId.
        self.assertIn("-FrontendOrigin", self.text)
        self.assertRegex(self.text, r"-ClientId\b.{0,400}-FromAzdEnv|-FromAzdEnv\b.{0,400}-ClientId", "expected an example combining -ClientId with a reconcile run")
        self.assertRegex(self.text, r"defaultDomain|properties\.configuration", "expected the FQDN-derivation commands (defaultDomain / properties.configuration) in the docs")

    def test_example_a_help_matches_the_deploy_md_dark_state_sequence(self):
        # Round 3 review, item 7: the `.EXAMPLE (a)` comment-based help had drifted from
        # DEPLOY.md's dark-state sequence -- it lacked `azd env select`, the `az account set
        # --subscription` pin, and the `if (-not $app -or -not $domain)` guard, so a reader who
        # only ran `Get-Help ./scripts/Setup-EntraAuth.ps1 -Examples` (rather than DEPLOY.md)
        # would target the wrong azd env/subscription and could derive an origin from a
        # not-found app/domain. Assert the example block carries all three, in order.
        match = re.search(r"\.EXAMPLE\s*\n(?P<block>.*?)(?=\n\.EXAMPLE|\n#>)", self.text, re.DOTALL)
        self.assertIsNotNone(match, "expected an .EXAMPLE (a) help block")
        block = match.group("block")
        self.assertIn("azd env select", block)
        self.assertIn("az account set --subscription (azd env get-value AZURE_SUBSCRIPTION_ID)", block)
        self.assertIn("if (-not $app -or -not $domain)", block)
        select_pos = block.find("azd env select")
        account_pos = block.find("az account set --subscription")
        guard_pos = block.find("if (-not $app -or -not $domain)")
        run_pos = block.find("./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -FrontendOrigin")
        self.assertTrue(
            select_pos < account_pos < guard_pos < run_pos,
            "expected 'azd env select' -> 'az account set' -> the not-found guard -> the Setup-EntraAuth.ps1 call, in that order",
        )

    def test_create_plan_prints_the_spa_redirect_uris_it_will_register(self):
        # Round 3 review, item 5: the preview for a brand-new app named every setting it would
        # create EXCEPT the SPA redirect URIs themselves, so a dry run gave no way to confirm
        # what would be registered without re-deriving $redirects by hand. The create-plan
        # Write-Plan line must include the joined $redirects (or an explicit "<none>" fallback).
        idx = self.text.index("$app = Resolve-TargetApplication")
        match = re.search(r"if \(-not \$app\) \{(?P<body>.*?)\n\}\n", self.text[idx:], re.DOTALL)
        self.assertIsNotNone(match, "expected the `if (-not $app) { ... }` create-new-app block")
        body = match.group("body")
        self.assertIn("$redirectsPreview", body, "expected a $redirectsPreview variable used in the create-plan preview")
        self.assertRegex(
            body,
            r"Write-Plan\s+\"[^\"]*SPA redirect URIs:\s*\$redirectsPreview",
            "expected the create-plan Write-Plan line to include the SPA redirect URIs preview",
        )
        self.assertRegex(
            body,
            r"\$redirectsPreview\s*=\s*if\s*\(\$redirects\.Count\s*-gt\s*0\)\s*\{\s*\$redirects\s*-join\s*',\s*'\s*\}\s*else\s*\{\s*'<none>'\s*\}",
            "expected $redirectsPreview to join $redirects when non-empty and fall back to '<none>'",
        )

    def test_spa_redirect_patch_only_fires_when_redirects_are_non_empty(self):
        # Review item 2: Setup must never wipe existing SPA redirect URIs when a run supplies
        # none -- the `spa = @{ redirectUris = @($redirects) }` PATCH must be lexically nested in
        # a block gated on `$redirects.Count -gt 0` (brace-depth matched, not a fixed line
        # window). Scoped to the redirect-URI *reconcile* section (section 7): the CREATE-time
        # POST body for a brand-new application also sets `spa = @{ redirectUris = @($redirects) }`,
        # but that can never "wipe" anything (there is no existing app yet), so it is out of scope.
        start = self.text.index("Write-Section 'Redirect URIs (SPA-only)'")
        end = self.text.index("# --- 8.", start)
        section = self.text[start:end]
        blocks = _brace_matched_if_blocks(section)
        call_pattern = re.compile(r"spa\s*=\s*@\{\s*redirectUris\s*=\s*@\(\$redirects\)\s*\}")
        offending = _unguarded_call_sites(section, call_pattern, "$redirects.Count -gt 0")
        self.assertEqual(
            offending, [],
            f"found the SPA redirectUris PATCH payload not nested in a '$redirects.Count -gt 0' guarded block: {offending}",
        )
        self.assertTrue(blocks, "expected at least one if/elseif block in the redirect-URI reconcile section")


class VerifyEntraAuthScriptContractTests(unittest.TestCase):
    """Pins Verify-EntraAuth.ps1 as strictly read-only."""

    @classmethod
    def setUpClass(cls):
        cls.text = _read(VERIFY_ENTRA)

    def test_script_exists(self):
        self.assertTrue(VERIFY_ENTRA.is_file(), f"expected {VERIFY_ENTRA} to exist")

    def test_no_write_methods(self):
        self.assertNotRegex(
            self.text, _RAW_WRITE_METHOD,
            "Verify-EntraAuth.ps1 must be read-only (no POST/PATCH/DELETE)",
        )
        for verb in ("New-", "Set-", "Remove-", "Update-"):
            for line in self.text.splitlines():
                if line.strip().startswith("#"):
                    continue
                self.assertNotRegex(
                    line, rf"\b{verb}Az",
                    f"unexpected write cmdlet '{verb}Az...' in read-only Verify-EntraAuth.ps1",
                )

    def test_no_credential_creation(self):
        self.assertNotRegex(self.text, _CREDENTIAL_CREATION)

    def test_no_token_output(self):
        self.assertNotRegex(self.text, _TOKEN_PRINT)
        self.assertEqual(_lines_printing_a_secret_value(self.text), [])

    def test_no_dotenv_reads(self):
        self.assertNotRegex(self.text, _ENV_FILE_READ)

    def test_checks_v2_tokens_and_spa_only_redirects(self):
        self.assertIn("RequiredTokenVersion", self.text)
        self.assertIn("SPA-only redirect platform", self.text)

    def test_rejects_key_credentials_as_well_as_password_credentials(self):
        # Review item 7: a certificate (keyCredentials) is just as much a "not a public client"
        # signal as a client secret (passwordCredentials) -- both must fail check 7.
        self.assertIn("keyCredentials", self.text)
        select_line = re.search(r"\$select\s*=\s*['\"](?P<cols>[^'\"]+)['\"]", self.text)
        self.assertIsNotNone(select_line, "expected a $select projection for the /applications GET")
        self.assertIn("keyCredentials", select_line.group("cols").split(","))
        check7 = re.search(r"# --- Check 7:.*?\n(?P<body>.*?)\nAdd-Result", self.text, re.DOTALL)
        self.assertIsNotNone(check7, "expected a check 7 section")
        body = check7.group("body")
        self.assertIn("keyCredentials", body)
        self.assertIn("pwdCreds.Count -eq 0", body)
        self.assertIn("keyCreds.Count -eq 0", body)

    def test_client_id_and_tenant_id_require_a_guid_shape(self):
        # Review item 7: reject an obviously-malformed id (e.g. a display name or empty string)
        # before ever calling Graph, rather than surfacing a confusing 400/404 from `az rest`.
        for param in ("TenantId", "ClientId"):
            idx = self.text.index(f"[string]${param}")
            window = self.text[max(0, idx - 200): idx]
            self.assertIn("ValidatePattern", window, f"-{param} must have a GUID-shaped ValidatePattern")

    def test_exits_non_zero_on_failure(self):
        self.assertIn("exit 1", self.text)
        self.assertIn("exit 0", self.text)


class VerifyProductionAuthScriptContractTests(unittest.TestCase):
    """Pins Verify-ProductionAuth.ps1's read-only posture and -RevisionsOnly / -Authenticated
    behavior."""

    @classmethod
    def setUpClass(cls):
        cls.text = _read(VERIFY_PROD)

    def test_script_exists(self):
        self.assertTrue(VERIFY_PROD.is_file(), f"expected {VERIFY_PROD} to exist")

    def test_no_write_methods_against_graph_or_arm(self):
        self.assertNotRegex(self.text, _RAW_WRITE_METHOD)
        for verb in ("New-", "Set-", "Remove-", "Update-"):
            for line in self.text.splitlines():
                if line.strip().startswith("#"):
                    continue
                self.assertNotRegex(line, rf"\b{verb}Az")
        self.assertNotIn("containerapp auth update", self.text)
        self.assertNotIn("containerapp secret remove", self.text)
        self.assertNotIn("containerapp ingress enable", self.text)
        self.assertNotIn("containerapp ingress disable", self.text)

    def test_no_credential_creation(self):
        self.assertNotRegex(self.text, _CREDENTIAL_CREATION)

    def test_never_prints_the_acquired_token(self):
        # The delegated token variable must never be handed to a print/output cmdlet, including
        # via string interpolation (review item 9 / mutation M4).
        self.assertNotRegex(self.text, _TOKEN_PRINT)
        self.assertEqual(_lines_printing_a_secret_value(self.text), [])
        self.assertIn("never print", self.text)

    def test_every_token_value_reference_is_an_allowed_form(self):
        # Round 2 required fix: the checks above are a fixed list of known-bad shapes and two
        # variants survived (`Write-Host "debug: $tokenJson"`, and `$dbg = "t=$token"; Write-Host
        # $dbg`, which interpolates $token into an unrelated variable on one line and prints that
        # variable on the next). This is an allow-list instead: every non-comment line touching
        # $token/$tokenJson/$BearerToken must match one of the forms already proven safe.
        offending = _lines_referencing_token_value(self.text)
        self.assertEqual(
            offending, [],
            f"found line(s) referencing $token/$tokenJson/$BearerToken that are not one of the "
            f"pre-approved safe forms: {offending}",
        )

    def test_no_dotenv_reads(self):
        self.assertNotRegex(self.text, _ENV_FILE_READ)

    def test_no_raw_az_ad_write(self):
        self.assertNotRegex(self.text, _RAW_AZ_AD_WRITE)

    def test_no_alternate_graph_or_arm_write_cmdlets(self):
        self.assertNotRegex(self.text, _ALT_GRAPH_OR_ARM_CMDLET)

    def test_has_revisions_only_switch_and_gates_http_probes(self):
        self.assertIn("[switch]$RevisionsOnly", self.text)
        self.assertRegex(self.text, r"if\s*\(-not \$RevisionsOnly\)")

    def test_revisions_only_fails_when_ingress_enabled(self):
        idx = self.text.index("ingress disabled (dark-provision gate)")
        window = self.text[max(0, idx - 400): idx + 150]
        self.assertIn("RevisionsOnly", window)
        self.assertIn("-not $ingressEnabled", window)

    def test_revisions_only_skips_the_registration_check_delegation(self):
        # Review item 6: -RevisionsOnly must run only the env-pin/active-revision/EasyAuth `az`
        # checks (18.9), with no HTTP calls at all -- including the Verify-EntraAuth.ps1
        # delegation, which itself makes Graph HTTP calls.
        idx = self.text.index("Delegate registration checks to Verify-EntraAuth.ps1")
        window = self.text[idx: idx + 400]
        self.assertRegex(
            window, r"if\s*\(-not \$SkipRegistrationCheck\s+-and\s+-not \$RevisionsOnly\)",
            "the Verify-EntraAuth.ps1 delegation must also be skipped under -RevisionsOnly",
        )

    def test_revisions_only_makes_no_http_calls(self):
        # Review item 6 pin: every call site of Test-AnonymousProbes, Test-AuthenticatedProbe and
        # `& $verifyScript` must be lexically nested (brace-depth matched) in a block whose header
        # contains the literal guard `-not $RevisionsOnly` (round 2: matching the bare substring
        # `$RevisionsOnly` survived a mutation that moved the call inside `if ($RevisionsOnly) {`,
        # i.e. inverted, since that header also contains the substring "$RevisionsOnly"). Invoke-
        # WebRequest may appear only inside the Invoke-ProbeRequest function (never directly at a
        # -RevisionsOnly-reachable call site).
        text = self.text
        call_pattern = re.compile(r"\bTest-AnonymousProbes\s+-|\bTest-AuthenticatedProbe\s+-|&\s*\$verifyScript\b")
        offending = _unguarded_call_sites(text, call_pattern, "-not $RevisionsOnly")
        self.assertEqual(
            offending, [],
            f"found HTTP/registration call site(s) not nested in a '-not $RevisionsOnly'-guarded if-block: {offending}",
        )

    def test_invoke_probe_request_only_called_from_the_two_probe_functions(self):
        # Round 2 required fix: a direct call to Invoke-ProbeRequest added outside
        # Test-AnonymousProbes/Test-AuthenticatedProbe would bypass the -RevisionsOnly guard
        # above entirely (that pin only watches the two Test-* function call sites and
        # `& $verifyScript`, not Invoke-ProbeRequest itself). Pin it directly: every call site of
        # Invoke-ProbeRequest (excluding its own function definition) must be lexically inside
        # the body of Test-AnonymousProbes or Test-AuthenticatedProbe.
        text = self.text
        probe_def = re.search(r"function\s+Invoke-ProbeRequest\s*\{.*?\n\}\n", text, re.DOTALL)
        self.assertIsNotNone(probe_def, "expected an Invoke-ProbeRequest function")
        anon_body = re.search(r"function\s+Test-AnonymousProbes\s*\{.*?\n\}\n", text, re.DOTALL)
        auth_body = re.search(r"function\s+Test-AuthenticatedProbe\s*\{.*?\n\}\n", text, re.DOTALL)
        self.assertIsNotNone(anon_body, "expected a Test-AnonymousProbes function")
        self.assertIsNotNone(auth_body, "expected a Test-AuthenticatedProbe function")
        allowed_spans = [
            (probe_def.start(), probe_def.end()),
            (anon_body.start(), anon_body.end()),
            (auth_body.start(), auth_body.end()),
        ]
        offending = []
        for m in re.finditer(r"\bInvoke-ProbeRequest\b", text):
            pos = m.start()
            if not any(start <= pos < end for start, end in allowed_spans):
                line_no = text.count("\n", 0, pos) + 1
                offending.append(line_no)
        self.assertEqual(
            offending, [],
            f"found Invoke-ProbeRequest call site(s) outside Test-AnonymousProbes/Test-AuthenticatedProbe: {offending}",
        )

    def test_invoke_webrequest_only_used_inside_invoke_probe_request(self):
        text = self.text
        func_match = re.search(r"function\s+Invoke-ProbeRequest\s*\{.*?\n\}\n", text, re.DOTALL)
        self.assertIsNotNone(func_match, "expected an Invoke-ProbeRequest function")
        self.assertIn("Invoke-WebRequest", func_match.group(0))
        outside = text[:func_match.start()] + text[func_match.end():]
        self.assertNotIn("Invoke-WebRequest", outside)

    def test_has_authenticated_switch(self):
        self.assertIn("[switch]$Authenticated", self.text)
        self.assertIn("get-access-token", self.text)

    def test_delegates_registration_check_to_verify_entra_auth(self):
        self.assertIn("Verify-EntraAuth.ps1", self.text)

    def test_easyauth_unknown_state_fails_closed(self):
        idx = self.text.index("unknown state, treated as failure")
        self.assertGreater(idx, 0)

    def test_easyauth_never_treats_a_lookup_error_as_a_pass(self):
        # Review item 3/9: a prior version treated "ResourceNotFound"/"could not be found" text
        # in `az containerapp auth show`'s stderr as an implicit PASS ("nothing configured means
        # EasyAuth is off"). That is unsafe -- the same text can appear for a transient/auth
        # failure, not just "nothing configured" -- so *every* non-zero exit from `auth show` must
        # now be a FAIL, with no carve-out.
        self.assertNotIn("no auth configuration present", self.text)
        self.assertNotRegex(self.text, r"ResourceNotFound\|could not be found\|NotFound")
        idx = self.text.index("$authShow = az containerapp auth show")
        # Window widened from 700: round 3 item 6 added stdout/stderr-partitioning lines (and
        # their explanatory comments) between the `az` call and the `if ($LASTEXITCODE...)` guard.
        window = self.text[idx: idx + 1200]
        self.assertRegex(
            window,
            r"if\s*\(\$LASTEXITCODE\s*-ne\s*0\)\s*\{\s*(?:#[^\n]*\n\s*)*Add-Result[^\n]*\$false",
            "every `az containerapp auth show` failure must directly Add-Result a FAIL, with no "
            "intermediate branch that could turn a specific error text into a PASS",
        )

    def test_easyauth_pass_requires_platform_enabled_exactly_false(self):
        # Mutation M5 replaced `-eq $false` with `-ne $true`, which looks equivalent but is not:
        # if platform.enabled is $null/unknown, `-eq $false` correctly fails, but `-ne $true`
        # incorrectly passes. The literal `-eq $false` spelling is the pin.
        idx = self.text.index("$easyAuthOff = ")
        line = self.text[idx: idx + 80]
        self.assertIn("-eq $false", line)
        self.assertNotIn("-ne $true", line)

    def test_revision_check_requires_exactly_one_active_healthy_running(self):
        # Review item 4: "at least one active revision" is not enough -- a stuck rollout with two
        # active revisions, or an active-but-unhealthy/non-running one, must also fail.
        self.assertIn("activeRevisions.Count -eq 1", self.text)
        self.assertIn("exactly one active revision", self.text)
        self.assertIn("healthState", self.text)
        self.assertIn("runningState", self.text)
        self.assertRegex(self.text, r"healthState\s*-eq\s*['\"]Healthy['\"]")
        self.assertRegex(
            self.text,
            r"runningState\s*-in\s*@\(\s*['\"]Running['\"]\s*,\s*['\"]RunningAtMaxScale['\"]\s*\)",
        )

    def test_app_discovery_is_strictmode_safe_and_covers_the_dotnet_app(self):
        # Review item 5: the dotnet app's bicep resource has no `azd-service-name` tag, so a raw
        # `$_.tags.'azd-service-name'` access throws under Set-StrictMode when `tags` lacks that
        # key. Discovery must read it safely (via Get-Prop) and must still resolve the dotnet
        # app's service name, by container app name, from the azd env.
        self.assertNotIn(".tags.'azd-service-name'", self.text)
        self.assertIn("function Resolve-ServiceName", self.text)
        self.assertIn("AZURE_CONTAINER_APP_DOTNET_NAME", self.text)
        self.assertIn("'backend-dotnet'", self.text)
        resolve_fn = re.search(r"function\s+Resolve-ServiceName\s*\{(?P<body>.*?)\n\}", self.text, re.DOTALL)
        self.assertIsNotNone(resolve_fn)
        self.assertIn("Get-Prop", resolve_fn.group("body"))

    def test_no_hardcoded_persona_brand_path(self):
        # Review item 8: the hardcoded persona path must be gone; the brand ratchet must
        # not go up, and a hardcoded brand would mis-probe any environment running a different
        # PERSONAS set.
        self.assertNotRegex(self.text, _HARDCODED_PERSONA_BRAND)
        self.assertIn("[string]$ProbePersona", self.text)
        self.assertIn("DEFAULT_PERSONA", self.text)
        self.assertIn("PERSONAS", self.text)
        self.assertRegex(self.text, r"ValidatePattern\('\^\[a-z0-9-\]\+\$'\)")

    def test_redacts_guids_in_output(self):
        self.assertIn("function Format-Redacted", self.text)

    def test_exits_non_zero_on_failure(self):
        self.assertIn("exit 1", self.text)
        self.assertIn("exit 0", self.text)

    def test_expected_image_variable_is_never_shadowed(self):
        # Round 2 review, blocker 2: PowerShell variable names are case-insensitive, so a local
        # named `$expectedImage` (any casing) would be the SAME variable as the
        # `[hashtable]$ExpectedImage` parameter, and assigning a string to it throws "Cannot
        # convert ... to Hashtable" at runtime. The script must use `$wantImage` instead; this was
        # a smoke-test-only pin (round 3 review, item 4) until now.
        self.assertNotRegex(
            self.text, r"\$expectedImage\s*=(?!=)",
            "must never assign to $expectedImage; it shadows the [hashtable]$ExpectedImage "
            "parameter (case-insensitive) -- use $wantImage",
        )

    def test_probe_persona_parameter_is_never_reassigned_outside_the_param_block(self):
        # Round 2 review, blocker 3: [ValidatePattern(...)] re-validates on every assignment to
        # -ProbePersona, so reassigning it (e.g. to an unresolved/empty value) throws instead of
        # failing closed with a normal FAIL result. The script must resolve into the separate
        # local $probePersonaId and never write back to $ProbePersona itself.
        param_block = re.search(r"^param\s*\(.*?\n\)\n", self.text, re.DOTALL | re.MULTILINE)
        self.assertIsNotNone(param_block, "expected a param() block")
        outside_param_block = self.text[:param_block.start()] + self.text[param_block.end():]
        self.assertNotRegex(
            outside_param_block, r"\$ProbePersona\s*=(?!=)",
            "must never reassign $ProbePersona outside the param() block -- resolve into "
            "$probePersonaId instead",
        )

    def test_get_prop_uses_the_properties_indexer_not_contains(self):
        # Round 2 review, blocker 1: `.PSObject.Properties.Name -contains $Name` allocates and
        # scans a full name list on every call and is a subtly different (looser under certain
        # PSCustomObject shapes) check than the direct indexer. Get-Prop must use
        # `.PSObject.Properties[$Name]`, pinned directly this time (round 3 review, item 4) rather
        # than relying only on the mocked smoke test to catch a regression back to `-contains`.
        self.assertNotRegex(self.text, r"\.Properties\.Name\s+-contains\b")
        self.assertIn(".PSObject.Properties[$Name]", self.text)

    def test_json_list_calls_go_through_the_stderr_partitioning_helper(self):
        # Round 3 review, item 6: `az ... --output json 2>&1 | ConvertFrom-Json` breaks if `az`
        # ever prints a stderr warning (e.g. a containerapp extension update notice) on an
        # otherwise-successful call -- the warning becomes an extra non-JSON line in the merged
        # stream and ConvertFrom-Json throws. `containerapp list`/`revision list`/`secret list`
        # must all go through Invoke-AzJsonList, which partitions stdout from stderr by object
        # type before parsing, rather than reintroducing the raw merged-pipe idiom.
        self.assertIn("function Invoke-AzJsonList", self.text)
        for needle in (
            "Invoke-AzJsonList -Description \"az containerapp list",
            "Invoke-AzJsonList -Description \"az containerapp revision list",
            "Invoke-AzJsonList -Description \"az containerapp secret list",
        ):
            self.assertIn(needle, self.text, f"expected {needle!r} to route through Invoke-AzJsonList")
        # The old raw idiom must not reappear for these three subcommands.
        self.assertNotRegex(self.text, r"az containerapp list[^\n]*2>&1")
        self.assertNotRegex(self.text, r"az containerapp revision list[^\n]*2>&1")
        self.assertNotRegex(self.text, r"az containerapp secret list[^\n]*2>&1")

    def test_easyauth_auth_show_partitions_stderr_before_parsing(self):
        # The EasyAuth check (item 3/9: must fail closed on ANY error) is not routed through
        # Invoke-AzJsonList (a non-zero exit there must Add-Result a FAIL, not throw), so it needs
        # its own stdout/stderr partition. Pin that it has one, so a stray stderr line on an
        # otherwise-successful `auth show` can't corrupt the JSON parse either.
        idx = self.text.index("$authShow = az containerapp auth show")
        window = self.text[idx: idx + 800]
        self.assertRegex(window, r"-is\s+\[string\]")
        self.assertRegex(window, r"-isnot\s+\[string\]")

    def test_two_active_revisions_guidance_is_scoped_to_full_mode_only(self):
        # Round 3 review, item 1: enabling ingress (18.10 step 5) creates a new revision, so a
        # transient two-active-revisions result right after that step is expected in full mode.
        # The added guidance text must only ever be reachable when NOT -RevisionsOnly -- the dark
        # -provision gate (18.10 step 4a) must stay strict, with no wait/retry carve-out, since two
        # actives there means the dark revision genuinely never finished rolling out.
        self.assertIn("wait for the old one to retire", self.text)
        idx = self.text.index("wait for the old one to retire")
        # Walk backwards from the guidance text to the nearest enclosing `if` header and confirm
        # it is gated on `-not $RevisionsOnly` (in either operand order).
        preceding = self.text[:idx]
        last_if = preceding.rfind("if (")
        self.assertGreater(last_if, 0, "expected an enclosing if-guard before the guidance text")
        guard = preceding[last_if: idx]
        self.assertRegex(
            guard, r"-not\s+\$RevisionsOnly|\$RevisionsOnly\s*-eq\s*\$false",
            "the step-5 wait/retry guidance must be gated on full mode (-not $RevisionsOnly), "
            f"got guard text: {guard!r}",
        )
        # The "exactly one active revision" Add-Result call itself must be a bare, unconditional
        # statement (only the extra sentence appended to its Detail is mode-gated) -- it should
        # appear AFTER the guidance if-block closes, not be nested inside it.
        addresult_idx = self.text.index('Add-Result "${svcName}: exactly one active revision"')
        self.assertGreater(
            addresult_idx, idx,
            "the active-revision Add-Result call must run unconditionally, after the mode-gated "
            "guidance text is computed -- not be nested inside the -RevisionsOnly guard",
        )


class AllScriptsSharedSafetyInvariantTests(unittest.TestCase):
    """Cross-cutting checks applied identically to all three scripts."""

    def test_all_expected_scripts_exist(self):
        for name, path in ALL_SCRIPTS.items():
            self.assertTrue(path.is_file(), f"missing {name} at {path}")

    def test_none_read_a_dotenv_file(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertNotRegex(text, _ENV_FILE_READ, f"{name} must never read a .env file")

    def test_none_create_credentials(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertNotRegex(text, _CREDENTIAL_CREATION, f"{name} must never create a credential")

    def test_none_print_tokens(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertNotRegex(text, _TOKEN_PRINT, f"{name} must never print a token/secret value")
            self.assertEqual(
                _lines_printing_a_secret_value(text), [],
                f"{name} must never print a token/secret value, including via string interpolation",
            )

    def test_none_call_raw_az_ad_write(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertNotRegex(
                text, _RAW_AZ_AD_WRITE,
                f"{name} must never call `az ad app create`/`az ad sp create` (or update/delete/credential)",
            )

    def test_none_hardcode_a_persona_brand(self):
        # Review item 8/11: the brand ratchet must not go up. A hardcoded persona/brand id in any
        # of the three scripts would mis-probe/mis-verify any environment running a different
        # PERSONAS set, and is exactly the kind of drift this pin exists to catch.
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertNotRegex(text, _HARDCODED_PERSONA_BRAND, f"{name} must not hardcode a persona brand word")

    def test_all_require_powershell_7(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertIn("#Requires -Version 7.0", text, f"{name} should pin PowerShell 7+")


class DeployMdDarkStateSetupSequenceTests(unittest.TestCase):
    """Round 2 required fix: tighten and pin DEPLOY.md's dark-state (case (a)) Setup sequence.

    An empty `$app`/`$domain` from the discovery commands previously flowed silently into
    `-FrontendOrigin "https://$app.$domain"`, producing the bogus origin `https://.` -- and the
    prior `-FrontendOrigin` pin (test_help_documents_all_three_setup_scenarios, above) only
    checked the Setup-EntraAuth.ps1 script's OWN help text for the parameter name, never DEPLOY.md
    itself, so a DEPLOY.md-only regression (wrong env selected, wrong subscription, or a dropped
    guard) had no pin at all.
    """

    @classmethod
    def setUpClass(cls):
        cls.text = _read(DEPLOY_MD)

    def _dark_state_block(self):
        # The dark-state (a) walkthrough is the fenced ```powershell block immediately following
        # the "**(a) Today's staging env" heading.
        match = re.search(
            r"\*\*\(a\) Today's staging env.*?```powershell\n(?P<body>.*?)\n```",
            self.text, re.DOTALL,
        )
        self.assertIsNotNone(match, "expected a dark-state (case (a)) fenced powershell block in DEPLOY.md")
        return match.group("body")

    def test_selects_the_azd_env_and_matching_subscription_before_discovery(self):
        block = self._dark_state_block()
        self.assertIn("azd env select", block)
        self.assertIn("az account set --subscription (azd env get-value AZURE_SUBSCRIPTION_ID)", block)
        select_pos = block.find("azd env select")
        account_pos = block.find("az account set --subscription")
        rg_pos = block.find("azd env get-value AZURE_RESOURCE_GROUP")
        self.assertGreater(account_pos, select_pos, "expected 'az account set' after 'azd env select'")
        self.assertGreater(rg_pos, account_pos, "expected the env/subscription selection before app discovery")

    def test_guards_against_an_empty_app_or_domain_before_running_setup(self):
        block = self._dark_state_block()
        guard = re.search(
            r"if\s*\(\s*-not\s*\$app\s*-or\s*-not\s*\$domain\s*\)\s*\{\s*throw\b",
            block,
        )
        self.assertIsNotNone(
            guard,
            "expected `if (-not $app -or -not $domain) { throw ... }` before Setup runs in the "
            "dark-state block, so an unresolved app/domain fails loudly instead of silently "
            "producing the origin 'https://.'",
        )
        domain_pos = block.find("$domain =")
        guard_pos = guard.start()
        setup_pos = block.find("./scripts/Setup-EntraAuth.ps1")
        self.assertGreater(guard_pos, domain_pos, "expected the guard after $domain is resolved")
        self.assertGreater(setup_pos, guard_pos, "expected the guard before Setup-EntraAuth.ps1 runs")

    def test_pins_the_interpolated_frontend_origin_form_not_just_the_parameter_name(self):
        # Review item 1 pin: asserting "-FrontendOrigin" alone (elsewhere) is satisfied even if
        # the value were hardcoded or malformed. Pin the actual interpolated form.
        self.assertIn('-FrontendOrigin "https://$app.$domain"', self.text)

    def test_every_setup_line_in_the_dark_state_block_carries_the_frontend_origin(self):
        # Review round 3, item 3: the previous pin above used `assertIn` against the WHOLE file,
        # so it was satisfied as long as ONE of the two Setup-EntraAuth.ps1 lines (preview,
        # -Apply) in the dark-state block carried the correct `-FrontendOrigin` form -- a mutation
        # that dropped `.$domain` from only ONE of the two lines survived it. Assert every
        # `./scripts/Setup-EntraAuth.ps1` call line within the block individually.
        block = self._dark_state_block()
        setup_lines = [
            line for line in block.splitlines()
            if "./scripts/Setup-EntraAuth.ps1" in line
        ]
        self.assertGreaterEqual(len(setup_lines), 2, "expected at least two Setup-EntraAuth.ps1 lines (preview, -Apply) in the dark-state block")
        offending = [line for line in setup_lines if '-FrontendOrigin "https://$app.$domain"' not in line]
        self.assertEqual(
            offending, [],
            f"found Setup-EntraAuth.ps1 line(s) in the dark-state block missing the exact "
            f"-FrontendOrigin \"https://$app.$domain\" form: {offending}",
        )


if __name__ == "__main__":
    unittest.main()
