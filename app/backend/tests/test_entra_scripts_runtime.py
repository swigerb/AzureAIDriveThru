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
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SCRIPTS_DIR = REPO / "scripts"
VERIFY_PROD = SCRIPTS_DIR / "Verify-ProductionAuth.ps1"
SETUP_ENTRA = SCRIPTS_DIR / "Setup-EntraAuth.ps1"

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
        # Round 3 review, item 8: asserting only a non-zero exit and the bare substring "FAIL"
        # would still pass if a DIFFERENT check crashed/failed instead of the active-revision
        # check itself (for example if Get-Prop threw on a malformed revision and the script
        # exited non-zero for an unrelated reason). Assert the specific "exactly one active
        # revision" line reports FAIL, in -RevisionsOnly's strict form (no step-5 wait/retry
        # wording -- that guidance is full-mode only).
        self.assertRegex(
            result.stdout,
            r"\[FAIL\][^\n]*exactly one active revision[^\n]*active=2",
            f"expected a FAIL line naming 'exactly one active revision' with active=2.\nstdout:\n{result.stdout}",
        )
        self.assertNotIn(
            "wait for the old one to retire", result.stdout,
            "the step-5 wait/retry guidance is full-mode only; -RevisionsOnly must stay the "
            "strict dark-provision gate with no such caveat",
        )


@unittest.skipUnless(PWSH, "pwsh (PowerShell 7+) not found on PATH")
class SetupEntraAuthSplitPatchMockedGraphTests(unittest.TestCase):
    """Round 3 review, item 2: an actual end-to-end -Apply run of Setup-EntraAuth.ps1 against
    mocked `az`/`azd` function stand-ins (never real Azure/Entra calls), proving the new-scope
    -plus-pre-authorized-client path really does split into two sequential Graph PATCHes -- the
    first committing only the new scope (with preAuthorizedApplications left at its EXISTING,
    pre-PATCH value), the second reconciling preAuthorizedApplications once the scope is
    guaranteed to exist -- rather than merely asserting the source text looks right.

    The mocked `az` function logs every `rest --method patch ...` call (URL + parsed JSON body,
    reading the temp file behind the script's `--body @<path>` argument) to a JSON-lines file,
    which this test reads back after the process exits so assertions are on structured data
    rather than colored Write-Host text.
    """

    MOCK_TENANT = "66666666-6666-6666-6666-666666666666"
    MOCK_OBJECT_ID = "11111111-1111-1111-1111-111111111111"
    MOCK_CLIENT_ID = "22222222-2222-2222-2222-222222222222"
    MOCK_SP_ID = "33333333-3333-3333-3333-333333333333"
    MOCK_USER_ID = "44444444-4444-4444-4444-444444444444"
    MOCK_ROLE_ID = "55555555-5555-5555-5555-555555555555"
    MOCK_UPN = "brian@contoso-mock.example"

    HARNESS_TEMPLATE = r"""
param(
    [Parameter(Mandatory = $true)][string]$SetupScriptPath,
    [Parameter(Mandatory = $true)][string]$PatchLogPath
)

$ErrorActionPreference = 'Stop'
$tenant = '__TENANT__'
$objectId = '__OBJECT_ID__'
$clientId = '__CLIENT_ID__'
$spId = '__SP_ID__'
$userId = '__USER_ID__'
$roleId = '__ROLE_ID__'
$upn = '__UPN__'

$mockApp = @{
    id             = $objectId
    appId          = $clientId
    displayName    = 'AzureAIDriveThru'
    signInAudience = 'AzureADMyOrg'
    identifierUris = @("api://$clientId")
    tags           = @('AzureAIDriveThruManaged')
    api            = @{ oauth2PermissionScopes = @(); preAuthorizedApplications = @(); requestedAccessTokenVersion = $null }
    spa            = @{ redirectUris = @('http://localhost:5173', 'http://localhost:8000') }
    web            = @{ redirectUris = @() }
    appRoles       = @(@{ id = $roleId; value = 'DriveThru.User'; displayName = 'AzureAIDriveThru User'; description = 'Users who may access the AzureAIDriveThru demo.'; allowedMemberTypes = @('User'); isEnabled = $true })
}

function az {
    $global:LASTEXITCODE = 0
    $a = $args

    if ($a[0] -eq 'account' -and $a[1] -eq 'show') {
        return (@{ tenantId = $tenant; user = @{ name = $upn } } | ConvertTo-Json -Depth 5 -Compress)
    }

    if ($a[0] -ne 'rest') {
        $global:LASTEXITCODE = 1
        Write-Error "unmocked az invocation in split-patch smoke test: $($a -join ' ')"
        return ''
    }

    $methodIdx = [array]::IndexOf($a, '--method')
    $method = $a[$methodIdx + 1]
    $urlIdx = [array]::IndexOf($a, '--url')
    $url = $a[$urlIdx + 1]
    $bodyIdx = [array]::IndexOf($a, '--body')
    $body = $null
    if ($bodyIdx -ge 0) {
        $bodyPath = $a[$bodyIdx + 1].TrimStart('@')
        $body = Get-Content -Path $bodyPath -Raw
    }

    if ($method -eq 'patch') {
        $record = @{ url = $url; body = ($body | ConvertFrom-Json -AsHashtable) } | ConvertTo-Json -Depth 20 -Compress
        Add-Content -Path $PatchLogPath -Value $record
        return ''
    }

    if ($method -eq 'get') {
        if ($url.Contains('/me?')) { return (@{ id = $userId } | ConvertTo-Json -Compress) }
        if ($url.Contains("/applications/$objectId/owners")) { return (@{ value = @(@{ id = $userId }) } | ConvertTo-Json -Depth 5 -Compress) }
        if ($url.Contains('/applications?') -and $url.Contains("appId eq '$clientId'")) { return (@{ value = @($mockApp) } | ConvertTo-Json -Depth 10 -Compress) }
        if ($url.Contains("/applications/$objectId")) { return ($mockApp | ConvertTo-Json -Depth 10 -Compress) }
        if ($url.Contains('/servicePrincipals?') -and $url.Contains("appId eq '$clientId'")) {
            return (@{ value = @(@{ id = $spId; appId = $clientId; appRoleAssignmentRequired = $true }) } | ConvertTo-Json -Depth 5 -Compress)
        }
        if ($url.Contains("/servicePrincipals/$spId")) { return (@{ appRoles = @(@{ id = $roleId; value = 'DriveThru.User' }) } | ConvertTo-Json -Depth 5 -Compress) }
        if ($url.Contains('/users/') -and $url.Contains('appRoleAssignments')) {
            return (@{ value = @(@{ id = 'assign-1'; resourceId = $spId; appRoleId = $roleId }) } | ConvertTo-Json -Depth 5 -Compress)
        }
        if ($url.Contains('/users/')) { return (@{ id = $userId; userPrincipalName = $upn } | ConvertTo-Json -Compress) }
        $global:LASTEXITCODE = 1
        Write-Error "unmocked GET url in split-patch smoke test: $url"
        return ''
    }

    $global:LASTEXITCODE = 1
    Write-Error "unmocked az rest method in split-patch smoke test: $method $url"
    return ''
}

function azd {
    $global:LASTEXITCODE = 0
    return ''
}

& $SetupScriptPath -TenantId $tenant -ClientId $clientId -Apply
exit $LASTEXITCODE
"""

    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory(prefix="setup-entra-splitpatch-")
        harness_text = (
            cls.HARNESS_TEMPLATE
            .replace("__TENANT__", cls.MOCK_TENANT)
            .replace("__OBJECT_ID__", cls.MOCK_OBJECT_ID)
            .replace("__CLIENT_ID__", cls.MOCK_CLIENT_ID)
            .replace("__SP_ID__", cls.MOCK_SP_ID)
            .replace("__USER_ID__", cls.MOCK_USER_ID)
            .replace("__ROLE_ID__", cls.MOCK_ROLE_ID)
            .replace("__UPN__", cls.MOCK_UPN)
        )
        cls._harness_path = Path(cls._tmpdir.name) / "harness.ps1"
        cls._harness_path.write_text(harness_text, encoding="utf-8")
        cls._patch_log_path = Path(cls._tmpdir.name) / "patches.jsonl"

    @classmethod
    def tearDownClass(cls):
        cls._tmpdir.cleanup()

    def setUp(self):
        if self._patch_log_path.exists():
            self._patch_log_path.unlink()

    def _run(self):
        return subprocess.run(
            [
                PWSH, "-NoProfile", "-NonInteractive", "-File", str(self._harness_path),
                "-SetupScriptPath", str(SETUP_ENTRA),
                "-PatchLogPath", str(self._patch_log_path),
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )

    def _read_patch_log(self):
        if not self._patch_log_path.exists():
            return []
        records = []
        for line in self._patch_log_path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                records.append(json.loads(line))
        return records

    def test_new_scope_with_pre_authorized_client_splits_into_two_ordered_patches(self):
        result = self._run()
        self.assertEqual(
            result.returncode, 0,
            f"expected exit 0.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        patches = self._read_patch_log()
        api_patches = [
            p for p in patches
            if isinstance(p.get("body"), dict) and "api" in p["body"]
            and "oauth2PermissionScopes" in p["body"]["api"]
        ]
        self.assertEqual(
            len(api_patches), 2,
            f"expected exactly 2 API-configuration PATCHes (split scope+preAuth), got {len(api_patches)}: {api_patches}",
        )
        first, second = api_patches[0], api_patches[1]

        # PATCH #1 commits the new scope but must NOT reference it from preAuthorizedApplications
        # yet (Graph would reject a pre-authorized client pointing at a not-yet-committed scope
        # id) -- it must resend the EXISTING (empty, in this mock) preAuthorizedApplications.
        self.assertEqual(
            first["body"]["api"].get("preAuthorizedApplications"), [],
            f"PATCH #1 must resend the EXISTING preAuthorizedApplications (empty here), not the desired one: {first}",
        )
        self.assertEqual(len(first["body"]["api"].get("oauth2PermissionScopes") or []), 1)
        new_scope = first["body"]["api"]["oauth2PermissionScopes"][0]
        self.assertEqual(new_scope.get("value"), "access_as_user")
        new_scope_id = new_scope.get("id")
        self.assertTrue(new_scope_id, "new scope must carry a generated id")

        # PATCH #2 reconciles preAuthorizedApplications, now safely referencing the id PATCH #1
        # already committed -- and must resend the SAME scope (never silently drop it).
        second_scopes = second["body"]["api"].get("oauth2PermissionScopes") or []
        self.assertEqual(len(second_scopes), 1)
        self.assertEqual(second_scopes[0].get("id"), new_scope_id)
        pre_auth = second["body"]["api"].get("preAuthorizedApplications") or []
        self.assertEqual(len(pre_auth), 1, f"expected the default Azure CLI pre-authorized client: {second}")
        self.assertEqual(pre_auth[0].get("appId"), "04b07795-8ddb-461a-bbee-02f9e1bf7b46")
        self.assertIn(new_scope_id, pre_auth[0].get("delegatedPermissionIds") or [])

        # Both PATCHes must resend requestedAccessTokenVersion (idempotency: a sibling field must
        # never be silently reset to its Graph default by either half of the split write).
        self.assertEqual(first["body"]["api"].get("requestedAccessTokenVersion"), 2)
        self.assertEqual(second["body"]["api"].get("requestedAccessTokenVersion"), 2)

    def test_rerun_against_an_already_reconciled_app_makes_no_api_configuration_patch(self):
        # Idempotency: once the scope exists and preAuthorizedApplications/token version already
        # match, a second -Apply run must not re-PATCH the API configuration at all.
        result = self._run()
        self.assertEqual(result.returncode, 0, f"first run failed.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}")
        first_patches = self._read_patch_log()
        api_patches = [
            p for p in first_patches
            if isinstance(p.get("body"), dict) and "api" in p["body"]
            and "oauth2PermissionScopes" in p["body"]["api"]
        ]
        self.assertEqual(len(api_patches), 2, "expected the split-patch path on the first run")
        new_scope_id = api_patches[0]["body"]["api"]["oauth2PermissionScopes"][0]["id"]

        # Patch the mock app in the harness to reflect the post-first-run state and re-run: no
        # further API-configuration PATCH should be issued (mirrors the reconcile invariant
        # already pinned for the non-split single-PATCH path).
        harness_text = self._harness_path.read_text(encoding="utf-8")
        reconciled_text = harness_text.replace(
            "    api            = @{ oauth2PermissionScopes = @(); preAuthorizedApplications = @(); requestedAccessTokenVersion = $null }\n",
            "    api            = @{ oauth2PermissionScopes = @(@{ id = '" + new_scope_id + "'; adminConsentDisplayName = 'Access AzureAIDriveThru API'; adminConsentDescription = 'Allow the app to access AzureAIDriveThru API on behalf of the signed-in user.'; value = 'access_as_user'; type = 'User'; isEnabled = $true }); preAuthorizedApplications = @(@{ appId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'; delegatedPermissionIds = @('" + new_scope_id + "') }); requestedAccessTokenVersion = 2 }\n",
        )
        self.assertNotEqual(harness_text, reconciled_text, "expected to find and replace the empty api placeholder")
        reconciled_path = Path(self._tmpdir.name) / "harness_reconciled.ps1"
        reconciled_path.write_text(reconciled_text, encoding="utf-8")
        self._patch_log_path.unlink(missing_ok=True)

        result2 = subprocess.run(
            [
                PWSH, "-NoProfile", "-NonInteractive", "-File", str(reconciled_path),
                "-SetupScriptPath", str(SETUP_ENTRA),
                "-PatchLogPath", str(self._patch_log_path),
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )
        self.assertEqual(result2.returncode, 0, f"second run failed.\nstdout:\n{result2.stdout}\nstderr:\n{result2.stderr}")
        second_patches = self._read_patch_log()
        api_patches2 = [
            p for p in second_patches
            if isinstance(p.get("body"), dict) and "api" in p["body"]
            and "oauth2PermissionScopes" in p["body"]["api"]
        ]
        self.assertEqual(
            len(api_patches2), 0,
            f"a re-run against an already-reconciled app must not re-PATCH the API configuration: {api_patches2}",
        )


class SetupEntraAuthRedirectUriDefaultBehaviorMockedGraphTests(unittest.TestCase):
    """Rick's round-2 review of #160: DEPLOY.md claimed a run that supplies none of
    -FrontendOrigin/-RedirectUri/-FromAzdEnv "leaves any existing SPA URIs untouched". That is
    false: -RedirectUri defaults to the two design-18.1 localhost origins (never empty), so
    $redirects.Count is never 0 unless the caller explicitly passes -RedirectUri @() with no
    -FrontendOrigin/-FromAzdEnv. Section 7's full-SET reconcile fires whenever
    $redirects.Count -gt 0 and would otherwise REPLACE the existing spa.redirectUris wholesale --
    so omitting all three flags would wipe an already-registered live frontend origin.

    #162: that silent wipe is now refused. -Apply throws instead of PATCHing when the reconcile
    would drop an already-registered, non-localhost SPA redirect URI, unless the caller passes
    -AllowRedirectUriRemoval. These mocked-Graph end-to-end runs pin the real behavior (mirroring
    SetupEntraAuthSplitPatchMockedGraphTests's harness technique): the refusal itself, the
    opt-in removal via -AllowRedirectUriRemoval, preview mode always reporting the would-be
    removal without ever PATCHing, and the always-empty-redirect-uri case that still leaves
    existing SPA URIs untouched.
    """

    MOCK_TENANT = "77777777-7777-7777-7777-777777777777"
    MOCK_OBJECT_ID = "88888888-8888-8888-8888-888888888888"
    MOCK_CLIENT_ID = "99999999-9999-9999-9999-999999999999"
    MOCK_SP_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    MOCK_USER_ID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
    MOCK_ROLE_ID = "cccccccc-cccc-cccc-cccc-cccccccccccc"
    MOCK_SCOPE_ID = "dddddddd-dddd-dddd-dddd-dddddddddddd"
    MOCK_UPN = "brian@contoso-mock.example"
    EXISTING_FRONTEND_ORIGIN = "https://myapp.happybush-123abc.eastus.azurecontainerapps.io"

    HARNESS_TEMPLATE = r"""
param(
    [Parameter(Mandatory = $true)][string]$SetupScriptPath,
    [Parameter(Mandatory = $true)][string]$PatchLogPath,
    [switch]$EmptyRedirectUri,
    [switch]$AllowRemoval,
    [switch]$PreviewOnly
)

$ErrorActionPreference = 'Stop'
$tenant = '__TENANT__'
$objectId = '__OBJECT_ID__'
$clientId = '__CLIENT_ID__'
$spId = '__SP_ID__'
$userId = '__USER_ID__'
$roleId = '__ROLE_ID__'
$scopeId = '__SCOPE_ID__'
$upn = '__UPN__'
$existingOrigin = '__EXISTING_ORIGIN__'

# API config, app role, and pre-authorized client already fully reconciled so the ONLY PATCH(es)
# a run can produce are the redirect-URI ones under test here.
$mockApp = @{
    id             = $objectId
    appId          = $clientId
    displayName    = 'AzureAIDriveThru'
    signInAudience = 'AzureADMyOrg'
    identifierUris = @("api://$clientId")
    tags           = @('AzureAIDriveThruManaged')
    api            = @{
        oauth2PermissionScopes     = @(@{ id = $scopeId; adminConsentDisplayName = 'Access AzureAIDriveThru API'; adminConsentDescription = 'Allow the app to access AzureAIDriveThru API on behalf of the signed-in user.'; value = 'access_as_user'; type = 'User'; isEnabled = $true })
        preAuthorizedApplications  = @(@{ appId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'; delegatedPermissionIds = @($scopeId) })
        requestedAccessTokenVersion = 2
    }
    spa            = @{ redirectUris = @($existingOrigin) }
    web            = @{ redirectUris = @() }
    appRoles       = @(@{ id = $roleId; value = 'DriveThru.User'; displayName = 'AzureAIDriveThru User'; description = 'Users who may access the AzureAIDriveThru demo.'; allowedMemberTypes = @('User'); isEnabled = $true })
}

function az {
    $global:LASTEXITCODE = 0
    $a = $args

    if ($a[0] -eq 'account' -and $a[1] -eq 'show') {
        return (@{ tenantId = $tenant; user = @{ name = $upn } } | ConvertTo-Json -Depth 5 -Compress)
    }

    if ($a[0] -ne 'rest') {
        $global:LASTEXITCODE = 1
        Write-Error "unmocked az invocation in redirect-default smoke test: $($a -join ' ')"
        return ''
    }

    $methodIdx = [array]::IndexOf($a, '--method')
    $method = $a[$methodIdx + 1]
    $urlIdx = [array]::IndexOf($a, '--url')
    $url = $a[$urlIdx + 1]
    $bodyIdx = [array]::IndexOf($a, '--body')
    $body = $null
    if ($bodyIdx -ge 0) {
        $bodyPath = $a[$bodyIdx + 1].TrimStart('@')
        $body = Get-Content -Path $bodyPath -Raw
    }

    if ($method -eq 'patch') {
        $record = @{ url = $url; body = ($body | ConvertFrom-Json -AsHashtable) } | ConvertTo-Json -Depth 20 -Compress
        Add-Content -Path $PatchLogPath -Value $record
        return ''
    }

    if ($method -eq 'get') {
        if ($url.Contains('/me?')) { return (@{ id = $userId } | ConvertTo-Json -Compress) }
        if ($url.Contains("/applications/$objectId/owners")) { return (@{ value = @(@{ id = $userId }) } | ConvertTo-Json -Depth 5 -Compress) }
        if ($url.Contains('/applications?') -and $url.Contains("appId eq '$clientId'")) { return (@{ value = @($mockApp) } | ConvertTo-Json -Depth 10 -Compress) }
        if ($url.Contains("/applications/$objectId")) { return ($mockApp | ConvertTo-Json -Depth 10 -Compress) }
        if ($url.Contains('/servicePrincipals?') -and $url.Contains("appId eq '$clientId'")) {
            return (@{ value = @(@{ id = $spId; appId = $clientId; appRoleAssignmentRequired = $true }) } | ConvertTo-Json -Depth 5 -Compress)
        }
        if ($url.Contains("/servicePrincipals/$spId")) { return (@{ appRoles = @(@{ id = $roleId; value = 'DriveThru.User' }) } | ConvertTo-Json -Depth 5 -Compress) }
        if ($url.Contains('/users/') -and $url.Contains('appRoleAssignments')) {
            return (@{ value = @(@{ id = 'assign-1'; resourceId = $spId; appRoleId = $roleId }) } | ConvertTo-Json -Depth 5 -Compress)
        }
        if ($url.Contains('/users/')) { return (@{ id = $userId; userPrincipalName = $upn } | ConvertTo-Json -Compress) }
        $global:LASTEXITCODE = 1
        Write-Error "unmocked GET url in redirect-default smoke test: $url"
        return ''
    }

    $global:LASTEXITCODE = 1
    Write-Error "unmocked az rest method in redirect-default smoke test: $method $url"
    return ''
}

function azd {
    $global:LASTEXITCODE = 0
    return ''
}

if ($EmptyRedirectUri) {
    & $SetupScriptPath -TenantId $tenant -ClientId $clientId -RedirectUri @() -Apply
}
elseif ($PreviewOnly) {
    & $SetupScriptPath -TenantId $tenant -ClientId $clientId
}
elseif ($AllowRemoval) {
    & $SetupScriptPath -TenantId $tenant -ClientId $clientId -Apply -AllowRedirectUriRemoval
}
else {
    & $SetupScriptPath -TenantId $tenant -ClientId $clientId -Apply
}
exit $LASTEXITCODE
"""

    @classmethod
    def setUpClass(cls):
        cls._tmpdir = tempfile.TemporaryDirectory(prefix="setup-entra-redirect-default-")
        harness_text = (
            cls.HARNESS_TEMPLATE
            .replace("__TENANT__", cls.MOCK_TENANT)
            .replace("__OBJECT_ID__", cls.MOCK_OBJECT_ID)
            .replace("__CLIENT_ID__", cls.MOCK_CLIENT_ID)
            .replace("__SP_ID__", cls.MOCK_SP_ID)
            .replace("__USER_ID__", cls.MOCK_USER_ID)
            .replace("__ROLE_ID__", cls.MOCK_ROLE_ID)
            .replace("__SCOPE_ID__", cls.MOCK_SCOPE_ID)
            .replace("__UPN__", cls.MOCK_UPN)
            .replace("__EXISTING_ORIGIN__", cls.EXISTING_FRONTEND_ORIGIN)
        )
        cls._harness_path = Path(cls._tmpdir.name) / "harness.ps1"
        cls._harness_path.write_text(harness_text, encoding="utf-8")
        cls._patch_log_path = Path(cls._tmpdir.name) / "patches.jsonl"

    @classmethod
    def tearDownClass(cls):
        cls._tmpdir.cleanup()

    def setUp(self):
        if self._patch_log_path.exists():
            self._patch_log_path.unlink()

    def _run(self, empty_redirect_uri=False, allow_removal=False, preview_only=False):
        args = [
            PWSH, "-NoProfile", "-NonInteractive", "-File", str(self._harness_path),
            "-SetupScriptPath", str(SETUP_ENTRA),
            "-PatchLogPath", str(self._patch_log_path),
        ]
        if empty_redirect_uri:
            args.append("-EmptyRedirectUri")
        if allow_removal:
            args.append("-AllowRemoval")
        if preview_only:
            args.append("-PreviewOnly")
        return subprocess.run(args, capture_output=True, text=True, timeout=60)

    def _read_patch_log(self):
        if not self._patch_log_path.exists():
            return []
        records = []
        for line in self._patch_log_path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                records.append(json.loads(line))
        return records

    def test_omitting_all_three_redirect_flags_refuses_to_drop_the_existing_live_origin(self):
        # #162: no -FrontendOrigin / -RedirectUri / -FromAzdEnv / -AllowRedirectUriRemoval:
        # -RedirectUri's default (the two localhost origins) means the section-7 full-SET
        # reconcile WOULD replace spa.redirectUris wholesale and drop the already-registered
        # live (non-localhost) origin. -Apply must refuse instead of silently wiping it -- a
        # non-zero exit, a clear message naming the dropped origin, and NO PATCH at all.
        result = self._run()
        self.assertNotEqual(
            result.returncode, 0,
            f"expected a non-zero exit when -Apply would drop a live SPA redirect URI without "
            f"-AllowRedirectUriRemoval.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        self.assertIn(self.EXISTING_FRONTEND_ORIGIN, result.stderr)
        self.assertIn("-AllowRedirectUriRemoval", result.stderr)
        patches = self._read_patch_log()
        spa_patches = [
            p for p in patches
            if isinstance(p.get("body"), dict) and "spa" in p["body"]
        ]
        self.assertEqual(
            len(spa_patches), 0,
            f"expected NO SPA redirect-URI PATCH when the removal is refused: {spa_patches}",
        )

    def test_allow_redirect_uri_removal_lets_the_reconcile_replace_the_existing_origin(self):
        # #162: the same scenario as above, but with the explicit opt-in switch -- the reconcile
        # is allowed to proceed and replaces spa.redirectUris with the localhost defaults only.
        result = self._run(allow_removal=True)
        self.assertEqual(
            result.returncode, 0,
            f"expected exit 0 with -AllowRedirectUriRemoval.\nstdout:\n{result.stdout}\n"
            f"stderr:\n{result.stderr}",
        )
        patches = self._read_patch_log()
        spa_patches = [
            p for p in patches
            if isinstance(p.get("body"), dict) and "spa" in p["body"]
        ]
        self.assertEqual(
            len(spa_patches), 1,
            f"expected exactly one SPA redirect-URI PATCH when removal is explicitly allowed, "
            f"got {len(spa_patches)}: {spa_patches}",
        )
        new_uris = set(spa_patches[0]["body"]["spa"].get("redirectUris") or [])
        self.assertEqual(
            new_uris, {"http://localhost:8000", "http://localhost:5173"},
            "expected the PATCH to replace spa.redirectUris with ONLY the two localhost "
            "defaults once removal is explicitly allowed",
        )
        self.assertNotIn(self.EXISTING_FRONTEND_ORIGIN, new_uris)

    def test_preview_mode_reports_the_would_be_removal_without_patching_or_throwing(self):
        # #162: preview (no -Apply) must always print the would-be removal so a missing
        # -FrontendOrigin is caught before applying -- but it must never throw and never PATCH,
        # regardless of -AllowRedirectUriRemoval.
        result = self._run(preview_only=True)
        self.assertEqual(
            result.returncode, 0,
            f"preview mode must never fail even when a live origin would be removed.\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        self.assertIn(self.EXISTING_FRONTEND_ORIGIN, result.stdout)
        patches = self._read_patch_log()
        self.assertEqual(patches, [], f"preview mode must never PATCH anything: {patches}")

    def test_explicit_empty_redirect_uri_with_no_origin_flags_leaves_existing_spa_uris_untouched(self):
        # -RedirectUri @() (explicit override to empty) with no -FrontendOrigin/-FromAzdEnv is
        # the ONLY way to make $redirects.Count -eq 0, which skips the section-7 SPA reconcile
        # entirely -- this is the one case where "existing SPA URIs untouched" is actually true.
        result = self._run(empty_redirect_uri=True)
        self.assertEqual(
            result.returncode, 0,
            f"expected exit 0.\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}",
        )
        patches = self._read_patch_log()
        spa_patches = [
            p for p in patches
            if isinstance(p.get("body"), dict) and "spa" in p["body"]
        ]
        self.assertEqual(
            len(spa_patches), 0,
            f"expected NO SPA redirect-URI PATCH when -RedirectUri @() is explicit and no "
            f"-FrontendOrigin/-FromAzdEnv is given: {spa_patches}",
        )


if __name__ == "__main__":
    unittest.main()
