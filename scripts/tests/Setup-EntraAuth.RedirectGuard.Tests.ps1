#Requires -Version 7.0
<#
.SYNOPSIS
    Focused Pester tests for the #162 redirect-URI removal guard in scripts/Setup-EntraAuth.ps1.

.DESCRIPTION
    Covers: Resolve-SpaRedirectReconcilePlan (the guard) and Test-IsLocalhostUri (its localhost
    classifier). These are pure helper functions defined near the TOP of Setup-EntraAuth.ps1,
    before any Azure CLI / Microsoft Graph call is made.

    NO AZURE OR GRAPH CALLS ARE MADE. `az` and `azd` are stubbed as no-op functions before the
    target script is dot-sourced, so the real external commands are never invoked (command-name
    resolution favors a function over a native executable in the current scope chain). Dot-sourcing
    then lets Setup-EntraAuth.ps1 run only as far as its "Validating Azure CLI context" check
    (section 1): with the stubbed `az account show` returning nothing, the script throws its own
    "Not logged in" error there and stops -- by that point every helper function defined earlier in
    the file (including the two under test) is already loaded into this scope. That expected abort
    is caught below; any OTHER failure while sourcing (e.g. a real syntax error) is treated as a
    test-setup failure and rethrown so it's not masked.

    Run from the repo root with PowerShell 7+ (Setup-EntraAuth.ps1 itself requires it):
        pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 3.4.0; Invoke-Pester -Script scripts/tests/Setup-EntraAuth.RedirectGuard.Tests.ps1"
#>

$scriptPath = Join-Path $PSScriptRoot '..\Setup-EntraAuth.ps1'

Describe 'Setup-EntraAuth: SPA redirect URI removal guard (#162)' {

    # --- Stub the only two external commands the script calls, then dot-source it ---------------
    # No real `az`/`azd` binaries are invoked. Defining these BEFORE dot-sourcing is what lets the
    # loop below hijack the script's own command resolution.
    function az { }
    function azd { }

    $sourceError = $null
    try {
        . $scriptPath -TenantId '00000000-0000-0000-0000-000000000000' | Out-Null
    }
    catch {
        $sourceError = $_
    }
    # The dot-sourced script sets Set-StrictMode -Version Latest and $ErrorActionPreference = 'Stop'
    # in THIS scope (dot-sourcing merges scope). Reset both so they don't leak into the rest of this
    # test file / the Pester run.
    Set-StrictMode -Off
    $ErrorActionPreference = 'Continue'

    It 'sources only as far as the mocked "az login" check (no Azure/Graph call happened)' {
        $sourceError | Should Not BeNullOrEmpty
        $sourceError.Exception.Message | Should Match 'Not logged in'
    }

    It 'defines the helper functions under test' {
        (Get-Command Test-IsLocalhostUri -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
        (Get-Command Resolve-SpaRedirectReconcilePlan -ErrorAction SilentlyContinue) | Should Not BeNullOrEmpty
    }

    Context 'Test-IsLocalhostUri' {
        It 'classifies the two localhost defaults as localhost' {
            Test-IsLocalhostUri 'http://localhost:8000' | Should Be $true
            Test-IsLocalhostUri 'http://localhost:5173' | Should Be $true
        }

        It 'classifies 127.0.0.1 and ::1 as localhost' {
            Test-IsLocalhostUri 'http://127.0.0.1:5173' | Should Be $true
            Test-IsLocalhostUri 'http://[::1]:5173' | Should Be $true
        }

        It 'classifies a deployed container-app origin as live (non-localhost)' {
            Test-IsLocalhostUri 'https://capps-backend-abc123.region.azurecontainerapps.io' | Should Be $false
        }

        It 'fails closed (treats as non-localhost) for an unparsable URI' {
            Test-IsLocalhostUri 'not a uri' | Should Be $false
        }
    }

    Context 'Resolve-SpaRedirectReconcilePlan -- the #162 guard' {
        $liveOrigin = 'https://capps-backend-abc123.region.azurecontainerapps.io'
        $localhost8000 = 'http://localhost:8000'
        $localhost5173 = 'http://localhost:5173'

        It 'BLOCKS removal of a live redirect URI under -Apply without -AllowRedirectUriRemoval' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($localhost8000, $localhost5173)
            { Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply } |
                Should Throw 'AllowRedirectUriRemoval'
        }

        It 'the blocking error names the live URI(s) that would be dropped' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($localhost8000, $localhost5173)
            { Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply } |
                Should Throw $liveOrigin
        }

        It 'ALLOWS removal of a live redirect URI under -Apply WITH -AllowRedirectUriRemoval' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($localhost8000, $localhost5173)
            $plan = Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply -AllowRedirectUriRemoval
            $plan.LiveRemoved.Count | Should Be 1
            $plan.LiveRemoved[0] | Should Be $liveOrigin
        }

        It 'reports NO removal (and never throws) when the live origin is re-supplied (-FrontendOrigin kept)' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($liveOrigin, $localhost8000, $localhost5173)
            $plan = Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply
            $plan.Removed.Count | Should Be 0
            $plan.LiveRemoved.Count | Should Be 0
        }

        It 'ALLOWS removal of localhost-ONLY origins under -Apply without the switch' {
            $current = @($localhost8000, $localhost5173)
            $desired = @($localhost5173)
            $plan = Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply
            $plan.Removed.Count | Should Be 1
            $plan.LiveRemoved.Count | Should Be 0
        }

        It 'NEVER throws in preview mode (no -Apply), even for a live removal without the switch' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($localhost8000, $localhost5173)
            { Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired } | Should Not Throw
        }

        It 'preview mode (no -Apply) still reports the would-be live removal' {
            $current = @($liveOrigin, $localhost8000)
            $desired = @($localhost8000, $localhost5173)
            $plan = Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired
            $plan.Removed.Count | Should Be 1
            $plan.LiveRemoved.Count | Should Be 1
            $plan.LiveRemoved[0] | Should Be $liveOrigin
        }

        It 'reports no removals when nothing currently registered would be dropped' {
            $current = @($localhost8000, $localhost5173)
            $desired = @($localhost8000, $localhost5173)
            $plan = Resolve-SpaRedirectReconcilePlan -CurrentSpaRedirectUris $current -DesiredRedirectUris $desired -Apply
            $plan.Removed.Count | Should Be 0
            $plan.LiveRemoved.Count | Should Be 0
        }
    }
}
