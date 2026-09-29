#Requires -Version 7.0
<#
.SYNOPSIS
    Read-only verification of the AzureAIDriveThru Entra app registration (ADR-002,
    docs/persona-architecture.md 18.1/18.9).

.DESCRIPTION
    Checks the app registration created by Setup-EntraAuth.ps1 against the required posture and
    prints a PASS/FAIL report:
      * Single tenant (signInAudience = AzureADMyOrg).
      * identifierUris contains exactly api://{clientId}.
      * requestedAccessTokenVersion = 2 (v2 access tokens only).
      * Delegated scope -ApiScopeName exists and is enabled.
      * App role -AppRoleValue exists and is enabled.
      * SPA-only redirect platform: at least one SPA redirect URI is registered, and NO Web
        platform redirect URIs exist (design 18.1: tighter than Retail Pulse).
      * No password credentials (client secrets) on the application -- it must remain a public
        client authenticated only via PKCE.
      * Service principal exists with appRoleAssignmentRequired = true.

    This script makes ONLY GET calls to Microsoft Graph via `az rest` (the caller's delegated
    token) -- it never writes, never creates credentials, and never prints a token. Exits non-zero
    if ANY check fails so it can gate CI/manual rollout steps.

.PARAMETER TenantId
    The Entra tenant (directory) GUID. REQUIRED.

.PARAMETER ClientId
    appId (client id GUID) of the application to verify. REQUIRED.

.PARAMETER ApiScopeName
    Expected delegated scope name. Default: access_as_user.

.PARAMETER AppRoleValue
    Expected app role value. Default: DriveThru.User.

.EXAMPLE
    ./scripts/Verify-EntraAuth.ps1 -TenantId <guid> -ClientId <guid>

.NOTES
    Read-only. Safe to run at any time, including in CI (given a delegated `az` login with Graph
    read access) or ad hoc by any team member with reader access to the registration.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$ClientId,

    [string]$ApiScopeName = 'access_as_user',

    [string]$AppRoleValue = 'DriveThru.User'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'
$script:RequiredTokenVersion = 2

function Invoke-GraphGet {
    param([Parameter(Mandatory)][string]$Url)
    $out = az rest --method get --url $Url 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Graph GET $Url failed: $out" }
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    return ($out | ConvertFrom-Json)
}

function Get-Prop {
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($Object -and ($Object.PSObject.Properties.Name -contains $Name)) { return $Object.$Name }
    return $Default
}

$results = New-Object System.Collections.Generic.List[pscustomobject]
function Add-Result([string]$Check, [bool]$Pass, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Pass = $Pass; Detail = $Detail })
}

# --- Validate az context + tenant --------------------------------------------
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not logged in. Run: az login --tenant <tenantId>' }
if ($account.tenantId -ne $TenantId) {
    throw "Signed-in tenant '$($account.tenantId)' != requested -TenantId '$TenantId'. Run: az login --tenant $TenantId"
}

# --- Resolve application by appId (read-only; ambiguity is a hard failure) ----
$select = 'id,appId,displayName,signInAudience,identifierUris,api,appRoles,spa,web,passwordCredentials,tags'
$resp = Invoke-GraphGet -Url "$graph/applications?`$filter=appId eq '$ClientId'&`$select=$select"
$apps = @()
if ($resp -and $resp.value) { $apps = @($resp.value) }
if ($apps.Count -eq 0) { throw "No application found with appId (client id) '$ClientId' in tenant '$TenantId'." }
if ($apps.Count -gt 1) { throw "Ambiguous: $($apps.Count) applications share appId '$ClientId'." }
$app = $apps[0]

$audience = "api://$ClientId"

# --- Check 1: single tenant ----------------------------------------------------
$signInAudience = Get-Prop $app 'signInAudience'
Add-Result -Check 'Single tenant (signInAudience = AzureADMyOrg)' -Pass ($signInAudience -eq 'AzureADMyOrg') -Detail "signInAudience=$signInAudience"

# --- Check 2: identifierUris = api://{clientId} --------------------------------
$identifierUris = @(Get-Prop $app 'identifierUris' @())
$audienceOk = ($identifierUris.Count -eq 1) -and ($identifierUris[0] -eq $audience)
Add-Result -Check "identifierUris = [$audience]" -Pass $audienceOk -Detail "identifierUris=[$($identifierUris -join ', ')]"

# --- Check 3: requestedAccessTokenVersion = 2 ----------------------------------
$api = Get-Prop $app 'api' $null
$tokenVersion = if ($api) { Get-Prop $api 'requestedAccessTokenVersion' $null } else { $null }
Add-Result -Check "requestedAccessTokenVersion = $($script:RequiredTokenVersion) (v2 tokens only)" -Pass ($tokenVersion -eq $script:RequiredTokenVersion) -Detail "requestedAccessTokenVersion=$tokenVersion"

# --- Check 4: delegated scope exists + enabled ----------------------------------
$scopes = @()
if ($api -and (Get-Prop $api 'oauth2PermissionScopes' $null)) { $scopes = @($api.oauth2PermissionScopes) }
$scope = $scopes | Where-Object { $_.value -eq $ApiScopeName } | Select-Object -First 1
$scopeOk = $scope -and $scope.isEnabled
Add-Result -Check "Delegated scope '$ApiScopeName' exists and is enabled" -Pass ([bool]$scopeOk) -Detail $(if ($scope) { "isEnabled=$($scope.isEnabled)" } else { 'not found' })

# --- Check 5: app role exists + enabled -----------------------------------------
$roles = @(Get-Prop $app 'appRoles' @())
$role = $roles | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
$roleOk = $role -and $role.isEnabled
Add-Result -Check "App role '$AppRoleValue' exists and is enabled" -Pass ([bool]$roleOk) -Detail $(if ($role) { "isEnabled=$($role.isEnabled)" } else { 'not found' })

# --- Check 6: SPA-only redirect platform -----------------------------------------
$spaUris = @()
$spaObj = Get-Prop $app 'spa' $null
if ($spaObj -and (Get-Prop $spaObj 'redirectUris' $null)) { $spaUris = @($spaObj.redirectUris) }
$webUris = @()
$webObj = Get-Prop $app 'web' $null
if ($webObj -and (Get-Prop $webObj 'redirectUris' $null)) { $webUris = @($webObj.redirectUris) }
$spaOk = ($spaUris.Count -gt 0) -and ($webUris.Count -eq 0)
Add-Result -Check 'SPA-only redirect platform (>=1 SPA URI, 0 Web URIs)' -Pass $spaOk -Detail "spa=[$($spaUris -join ', ')] web=[$($webUris -join ', ')]"

# --- Check 7: no client secret (public client / PKCE only) ------------------------
$pwdCreds = @(Get-Prop $app 'passwordCredentials' @())
Add-Result -Check 'No client secret (public client, PKCE only)' -Pass ($pwdCreds.Count -eq 0) -Detail "passwordCredentials count=$($pwdCreds.Count)"

# --- Check 8: service principal + appRoleAssignmentRequired -----------------------
$spResp = Invoke-GraphGet -Url "$graph/servicePrincipals?`$filter=appId eq '$ClientId'&`$select=id,appRoleAssignmentRequired"
$sp = if ($spResp -and $spResp.value -and $spResp.value.Count -gt 0) { $spResp.value[0] } else { $null }
$spOk = $sp -and $sp.appRoleAssignmentRequired
Add-Result -Check 'Service principal exists with appRoleAssignmentRequired = true' -Pass ([bool]$spOk) -Detail $(if ($sp) { "appRoleAssignmentRequired=$($sp.appRoleAssignmentRequired)" } else { 'service principal not found' })

# --- Report ------------------------------------------------------------------
Write-Host "`nEntra app registration verification -- appId=$ClientId tenant=$TenantId`n" -ForegroundColor Cyan
foreach ($r in $results) {
    $mark = if ($r.Pass) { 'PASS' } else { 'FAIL' }
    $color = if ($r.Pass) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} -- {2}" -f $mark, $r.Check, $r.Detail) -ForegroundColor $color
}

$failed = @($results | Where-Object { -not $_.Pass })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "All $($results.Count) checks passed." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "$($failed.Count) of $($results.Count) checks FAILED." -ForegroundColor Red
    exit 1
}
