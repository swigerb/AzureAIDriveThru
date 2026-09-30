"""Round 2 required additions (Rick's review of #156, item 1):

1. A pwsh-backed parse test covering every script under scripts/*.ps1, using the same
   [System.Management.Automation.Language.Parser]::ParseFile API that caught the 21
   Verify-ProductionAuth.ps1 errors during review, so a future PR can't reintroduce an
   unparseable script without a targeted test failing (the existing source-scan contract
   tests in test_entra_scripts_contract.py only ever read the *.ps1 files as plain text and
   never actually asked PowerShell to parse them).
2. A mocked, end-to-end smoke test that actually *runs*
   Verify-ProductionAuth.ps1 -RevisionsOnly against dot-sourced `az`/`azd` function mocks
   (never real Azure/Entra calls), covering both a healthy single-active-revision app and a
   two-active-revision app that must fail.

Both test classes skip (not fail) when `pwsh` is not on PATH, since CI runs on
ubuntu-latest with PowerShell 7 preinstalled, but a contributor's machine may not have it.
"""
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SCRIPTS_DIR = REPO / "scripts"
VERIFY_PROD = SCRIPTS_DIR / "Verify-ProductionAuth.ps1"

PWSH = shutil.which("pwsh")


def _run_pwsh(args, cwd=None, timeout=60):
    return subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", *args],
        cwd=cwd,
        capture_output=True,
        text=True,
        timeout=timeout,
    )


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7+) not found on PATH")
class AllScriptsParseCleanlyTests(unittest.TestCase):
    """Round 2 item 1: a real PowerShell parser must report zero errors for every script.

    This is the exact technique used to find (and, above, fix) the 21
    Verify-ProductionAuth.ps1 InvalidVariableReferenceWithDrive errors during review -- a
    future edit that reintroduces an unescaped `"$name:` interpolation, or any other parse
    error, in ANY scripts/*.ps1 file now fails a test instead of only failing at runtime.
    """

    def test_every_ps1_script_parses_with_zero_errors(self):
        scripts = sorted(SCRIPTS_DIR.glob("*.ps1"))
        self.assertGreater(len(scripts), 0, "expected at least one scripts/*.ps1 file")
        failures = {}
        for script in scripts:
            # Ask pwsh itself to parse the file and report the error COUNT on stdout, so a
            # parse error in the *test harness* command below can never be confused with a
            # parse error in the SCRIPT UNDER TEST. The path is embedded as a single-quoted
            # PowerShell literal (escaping any embedded single quotes) rather than passed as a
            # trailing CLI argument, since pwsh -Command does not bind trailing args to $args.
            literal_path = str(script).replace("'", "''")
            command = (
                "$tokens = $null; $errors = $null; "
                f"[void][System.Management.Automation.Language.Parser]::ParseFile("
                f"'{literal_path}', [ref]$tokens, [ref]$errors); "
                "Write-Output $errors.Count; "
                "$errors | ForEach-Object { Write-Output $_.Message }"
            )
            result = _run_pwsh(["-Command", command])
            lines = result.stdout.splitlines()
            error_count = int(lines[0]) if lines and lines[0].strip().isdigit() else -1
            if error_count != 0:
                failures[script.name] = {
                    "reported_error_count": error_count,
                    "messages": lines[1:],
                    "stderr": result.stderr,
                }
        self.assertEqual(failures, {}, f"one or more scripts/*.ps1 files fail to parse: {failures}")


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7+) not found on PATH")
class VerifyProductionAuthMockedSmokeTests(unittest.TestCase):
    """Round 2 item 1: an actual end-to-end run of -RevisionsOnly against mocked az/azd.

    No real `az`/`azd`/Azure/Entra call is ever made -- `az` and `azd` are replaced with
    PowerShell *functions* (which take call-site precedence over any external executable of
    the same name) that return canned JSON, so this is safe to run anywhere, including a
    contributor's machine with no Azure CLI installed at all.
    """

    EXPECTED_IMAGE = "mockacr.azurecr.io/backend:deadbeef"

    HARNESS_TEMPLATE = r"""
param(
    [Parameter(Mandatory = $true)][string]$VerifyScriptPath,
    [Parameter(Mandatory = $true)][string]$Scenario
)

$ErrorActionPreference = 'Stop'
$mockImageValue = '__EXPECTED_IMAGE__'

function az {
    $global:LASTEXITCODE = 0
    $joined = $args -join ' '

    if ($joined -match 'containerapp\s+list') {
        $apps = @(
            [ordered]@{
                name       = 'ca-backend-mock'
                tags       = @{ 'azd-service-name' = 'backend' }
                properties = @{ configuration = @{} }
            }
        )
        return ($apps | ConvertTo-Json -Depth 10 -Compress)
    }

    if ($joined -match 'containerapp\s+revision\s+list') {
        $envVars = @(
            @{ name = 'AUTH_MODE'; value = 'Entra' }
            @{ name = 'RUNNING_IN_PRODUCTION'; value = 'true' }
            @{ name = 'ENTRA_TENANT_ID'; value = 'mock-tenant' }
            @{ name = 'ENTRA_CLIENT_ID'; value = 'mock-client' }
        )
        $container = @{ image = $mockImageValue; env = $envVars }
        $rev1 = [ordered]@{
            name       = 'ca-backend-mock--rev1'
            properties = @{
                active       = $true
                healthState  = 'Healthy'
                runningState = 'Running'
                template     = @{ containers = @($container) }
            }
        }
        $revisions = @($rev1)
        if ($Scenario -eq 'two_active') {
            $rev2 = [ordered]@{
                name       = 'ca-backend-mock--rev2'
                properties = @{
                    active       = $true
                    healthState  = 'Healthy'
                    runningState = 'Running'
                    template     = @{ containers = @($container) }
                }
            }
            $revisions += $rev2
        }
        return ($revisions | ConvertTo-Json -Depth 10 -Compress)
    }

    if ($joined -match 'containerapp\s+auth\s+show') {
        return (@{ platform = @{ enabled = $false } } | ConvertTo-Json -Depth 5 -Compress)
    }

    if ($joined -match 'containerapp\s+secret\s+list') {
        return '[]'
    }

    $global:LASTEXITCODE = 1
    Write-Error "unmocked az invocation in smoke test: $joined"
    return ''
}

function azd {
    $global:LASTEXITCODE = 0
    if ($args.Count -ge 3 -and $args[0] -eq 'env' -and $args[1] -eq 'get-value') {
        $name = $args[2]
        if ($name -eq 'SERVICE_BACKEND_IMAGE_NAME') { return $mockImageValue }
        return ''
    }
    return ''
}

& $VerifyScriptPath -RevisionsOnly -TenantId 'mock-tenant' -ClientId 'mock-client' -ResourceGroup 'rg-mock'
exit $LASTEXITCODE
"""

    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory(prefix="verify-prod-smoke-")
        harness_text = cls.HARNESS_TEMPLATE.replace("__EXPECTED_IMAGE__", cls.EXPECTED_IMAGE)
        cls._harness_path = Path(cls._tmpdir.name) / "harness.ps1"
        cls._harness_path.write_text(harness_text, encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        cls._tmpdir.cleanup()

    def _run_scenario(self, scenario):
        return subprocess.run(
            [
                PWSH, "-NoProfile", "-NonInteractive", "-File", str(self._harness_path),
                "-VerifyScriptPath", str(VERIFY_PROD),
                "-Scenario", scenario,
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )

    def test_healthy_single_active_revision_with_no_default_persona_exits_zero(self):
        result = self._run_scenario("healthy")
        self.assertEqual(
            result.returncode, 0,
            f"expected exit 0 for the healthy scenario.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        self.assertIn("PASS", result.stdout)

    def test_two_active_revisions_exits_nonzero(self):
        result = self._run_scenario("two_active")
        self.assertNotEqual(
            result.returncode, 0,
            f"expected a non-zero exit for the two-active-revisions scenario.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        self.assertIn("FAIL", result.stdout)


if __name__ == "__main__":
    unittest.main()
