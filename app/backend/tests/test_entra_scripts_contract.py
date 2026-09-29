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
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SCRIPTS = REPO / "scripts"

SETUP = SCRIPTS / "Setup-EntraAuth.ps1"
VERIFY_ENTRA = SCRIPTS / "Verify-EntraAuth.ps1"
VERIFY_PROD = SCRIPTS / "Verify-ProductionAuth.ps1"

ALL_SCRIPTS = {"Setup-EntraAuth.ps1": SETUP, "Verify-EntraAuth.ps1": VERIFY_ENTRA, "Verify-ProductionAuth.ps1": VERIFY_PROD}

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


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


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

    def test_no_dotenv_reads(self):
        self.assertNotRegex(self.text, _ENV_FILE_READ)

    def test_checks_v2_tokens_and_spa_only_redirects(self):
        self.assertIn("RequiredTokenVersion", self.text)
        self.assertIn("SPA-only redirect platform", self.text)

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
        # The delegated token variable must never be handed to a print/output cmdlet.
        self.assertNotRegex(self.text, _TOKEN_PRINT)
        self.assertIn("never print", self.text)

    def test_no_dotenv_reads(self):
        self.assertNotRegex(self.text, _ENV_FILE_READ)

    def test_has_revisions_only_switch_and_gates_http_probes(self):
        self.assertIn("[switch]$RevisionsOnly", self.text)
        self.assertRegex(self.text, r"if\s*\(-not \$RevisionsOnly\)")

    def test_revisions_only_fails_when_ingress_enabled(self):
        idx = self.text.index("ingress disabled (dark-provision gate)")
        window = self.text[max(0, idx - 400): idx + 150]
        self.assertIn("RevisionsOnly", window)
        self.assertIn("-not $ingressEnabled", window)

    def test_has_authenticated_switch(self):
        self.assertIn("[switch]$Authenticated", self.text)
        self.assertIn("get-access-token", self.text)

    def test_delegates_registration_check_to_verify_entra_auth(self):
        self.assertIn("Verify-EntraAuth.ps1", self.text)

    def test_easyauth_unknown_state_fails_closed(self):
        idx = self.text.index("unknown state, treated as failure")
        self.assertGreater(idx, 0)

    def test_redacts_guids_in_output(self):
        self.assertIn("function Format-Redacted", self.text)

    def test_exits_non_zero_on_failure(self):
        self.assertIn("exit 1", self.text)
        self.assertIn("exit 0", self.text)


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

    def test_all_require_powershell_7(self):
        for name, path in ALL_SCRIPTS.items():
            text = _read(path)
            self.assertIn("#Requires -Version 7.0", text, f"{name} should pin PowerShell 7+")


if __name__ == "__main__":
    unittest.main()
