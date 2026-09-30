#Requires -Version 7.0
<#
.SYNOPSIS
    Idempotently provisions the single-tenant Entra app registration that fronts
    AzureAIDriveThru's backends with MSAL PKCE auth (ADR-002, docs/persona-architecture.md 18.1/18.9).

.DESCRIPTION
    Creates and configures ONE single-tenant Entra application + service principal:
      * Public SPA client (PKCE, no client secret / no password credential).
      * Delegated API scope (default: access_as_user) exposed as api://{clientId}.
      * requestedAccessTokenVersion = 2 (v2 access tokens only -- tighter than Retail Pulse,
        which also accepts the v1 sts.windows.net issuer form).
      * Pre-authorized client applications (default: the Azure CLI public client
        04b07795-8ddb-461a-bbee-02f9e1bf7b46), so operators can run
        `az account get-access-token --scope api://{clientId}/access_as_user` non-interactively
        for the headless checks in Verify-ProductionAuth.ps1.
      * App role (default: DriveThru.User) required for API/realtime authorization.
      * Service principal with appRoleAssignmentRequired = true (assignment required).
      * SPA-ONLY redirect URIs derived from -FrontendOrigin, -RedirectUri and -FromAzdEnv. No
        Web platform redirect URIs are ever set; any that already exist on an adopted app are
        cleared under -Apply.
      * An app-role assignment for the operator (or -AssignUserUpn) so they can sign in.

    The script is IDEMPOTENT: re-running with the SAME explicit -ClientId / -AppObjectId reconciles
    that exact app in place and never creates duplicates, extra scopes, extra roles, or secrets.
    EVERY run after the first must pass -ClientId (or -AppObjectId): with neither, the script is
    CREATE-ONLY and a same-DisplayName collision (including the app this same script created on a
    prior run) is a hard failure, not a reconcile. If -Apply fails partway through creating a NEW
    app, re-run with -ClientId set to the appId printed on the "Created application" line to finish
    reconciling it (a bare retry with no -ClientId hits the same-name collision).

    SAFE RECONCILIATION (no app hijack): the script NEVER adopts an app located by display name.
    To modify an existing registration you must pass -ClientId (its appId) or -AppObjectId (its
    object id); the target is then verified to be owned by the caller, to carry this tool's
    managed marker tag (or be explicitly overridden with -AllowUnmarkedAdoption), and to have an
    identifier URI that is unset or already the expected api://{clientId}. With no explicit
    identifier the script is CREATE-ONLY and hard-fails if any app already uses the requested
    -DisplayName (default 'AzureAIDriveThru'). Apps created here are stamped with the
    'AzureAIDriveThruManaged' tag.

    It talks to Microsoft Graph exclusively via `az rest` using the CALLER's delegated Azure CLI
    token -- it never writes secrets, never prints tokens, and never reads .env files. Output is
    limited to SAFE, PUBLIC identifiers (tenant ID, client/app ID, audience, scope name, app-role
    value) plus the two `azd env set` commands the operator runs next.

    SAFETY: preview-by-default. No write call is made unless you pass -Apply.
    Without -Apply the script prints exactly what it WOULD create or change.

.PARAMETER TenantId
    The Entra tenant (directory) GUID to provision in. REQUIRED. The script fails fast if the
    signed-in `az` context is a different tenant.

.PARAMETER DisplayName
    Display name of the app registration. Default: 'AzureAIDriveThru'. Used ONLY to name a newly
    created app and to detect same-name collisions in create-only mode. It is NEVER used to adopt
    an existing app (that requires -ClientId or -AppObjectId).

.PARAMETER ClientId
    appId (client id GUID) of an EXISTING app to reconcile in place. The app must be owned by the
    caller and carry the managed marker (or use -AllowUnmarkedAdoption). Mutually usable with
    -AppObjectId.

.PARAMETER AppObjectId
    Directory object id (GUID) of an EXISTING app to reconcile in place. Same ownership/marker
    verification as -ClientId.

.PARAMETER AllowUnmarkedAdoption
    Permit adoption of an owned app that lacks the 'AzureAIDriveThruManaged' marker tag. Ownership
    and identifier-URI checks still apply. Use only after confirming you targeted the right app.

.PARAMETER FrontendOrigin
    Origin(s) of a deployed backend (e.g. https://capps-backend-abc123.region.azurecontainerapps.io).
    Redirect URIs are the bare origin. May be passed multiple times.

.PARAMETER RedirectUri
    Extra explicit redirect URIs to register. Combined with the -FrontendOrigin values. Defaults to
    the two local-dev origins from design 18.1 (http://localhost:8000, http://localhost:5173). The
    redirect-URI reconcile (section 7) is a full SET, not a merge: a later run that omits
    -RedirectUri, -FrontendOrigin and -FromAzdEnv would otherwise silently delete these local
    origins too, which is why they are the default here rather than only documented. Pass an empty
    array (-RedirectUri @()) together with omitting -FrontendOrigin/-FromAzdEnv to get an empty
    combined total; that does NOT clear the app's existing SPA redirect URIs -- section 7 treats an
    empty combined total as "nothing supplied" and leaves the existing SPA URIs untouched (only the
    separate, always-cleared Web platform list is affected), specifically to avoid an empty run
    silently breaking sign-in for every already-registered origin.

.PARAMETER FromAzdEnv
    Read the deployed backend origins from the selected azd environment: `BACKEND_URI` and
    `BACKEND_DOTNET_URI` (design 18.7/18.10). FAILS if `BACKEND_URI` is empty -- that's the
    expected value while ingress is disabled during the dark provision (18.10 step 4), and
    reconciling redirect URIs without it would silently drop the Python backend's origin instead
    of surfacing the problem. `BACKEND_DOTNET_URI` may legitimately be empty (the .NET app not yet
    deployed, #17) and is simply omitted in that case.

.PARAMETER ApiScopeName
    Delegated scope name exposed by the API. Default: access_as_user.

.PARAMETER PreAuthorizedClientAppId
    appId(s) of public client applications that are pre-authorized on the delegated API scope.
    Pre-authorized clients skip the interactive user-consent prompt for the scope, which is what
    lets `az account get-access-token --scope api://{clientId}/{scope}` (and other headless MSAL
    public clients) acquire a delegated user token non-interactively. Default: the well-known
    Azure CLI first-party client (04b07795-8ddb-461a-bbee-02f9e1bf7b46). May be passed multiple
    times. Pass an empty array to disable and clear the list.

.PARAMETER AppRoleValue
    App role value required for protected API/realtime access. Default: DriveThru.User.

.PARAMETER AssignUserUpn
    UPN/email of the user to grant the app role. Default: the signed-in `az` user.

.PARAMETER Apply
    Perform the writes. Omit for a read-only preview (the default).

.EXAMPLE
    # (a) Today's DARK env: ingress off, so the azd env BACKEND_URI is blank and -FromAzdEnv would
    # fail (see the error below). Derive the Python container app's stable FQDN read-only instead
    # (<app-name>.<environment-defaultDomain> never changes when ingress is toggled) and pass it
    # as -FrontendOrigin.
    $rg = azd env get-value AZURE_RESOURCE_GROUP
    $app = (az containerapp list -g $rg -o json | ConvertFrom-Json | Where-Object { $_.tags.'azd-service-name' -eq 'backend' } | Select-Object -First 1).name
    $envId = az containerapp show -n $app -g $rg --query properties.managedEnvironmentId -o tsv
    $domain = az containerapp env show --ids $envId --query properties.defaultDomain -o tsv
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -FrontendOrigin "https://$app.$domain"
    # Preview only, changes nothing. Re-run the identical command with -Apply to provision:
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -FrontendOrigin "https://$app.$domain" -Apply

.EXAMPLE
    # (b) A FRESH env, before the first deployment: no -FrontendOrigin/-FromAzdEnv is available yet,
    # so only the -RedirectUri localhost defaults are registered.
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -Apply
    azd env set ENTRA_TENANT_ID "<tenant-id>"
    azd env set ENTRA_CLIENT_ID "<client-id>"
    azd up
    # Then run (c) below once the app is deployed (design 18.10 step 5, the public provision) to
    # reconcile the deployed origin into the redirect URIs.

.EXAMPLE
    # (c) RECONCILE, every run after the first: pass -ClientId (the appId Setup printed) so the
    # script edits the existing app in place instead of hitting the create-only name collision.
    # After the public provision (step 5) its preview should report "SPA redirect URIs already
    # reconciled" as a cross-check that nothing drifted.
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -ClientId <appId> -FromAzdEnv
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -ClientId <appId> -FromAzdEnv -Apply

.NOTES
    Requires: Azure CLI (az) logged in as a user who can create app registrations and app-role
    assignments in the target tenant. No secrets are created. Must run as Brian (design 18.13):
    it creates an app registration in his tenant and assigns him the role.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [string]$DisplayName = 'AzureAIDriveThru',

    [string[]]$FrontendOrigin = @(),

    # Design 18.1: the two local-dev origins, defaulted so a run that forgets -FrontendOrigin /
    # -FromAzdEnv never wipes them out via the section-7 full-SET reconcile (review item 1/2).
    [string[]]$RedirectUri = @('http://localhost:8000', 'http://localhost:5173'),

    [switch]$FromAzdEnv,

    [string]$ApiScopeName = 'access_as_user',

    # Public client appIds that skip the interactive user-consent prompt for the delegated API
    # scope. Default = Azure CLI (04b07795-8ddb-461a-bbee-02f9e1bf7b46) so Verify-ProductionAuth.ps1
    # -Authenticated can acquire a delegated user token via
    # `az account get-access-token --scope api://{clientId}/{scope}`.
    # GUID-validated to prevent injection into the Graph PATCH body.
    [ValidatePattern('(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')]
    [string[]]$PreAuthorizedClientAppId = @('04b07795-8ddb-461a-bbee-02f9e1bf7b46'),

    [string]$AppRoleValue = 'DriveThru.User',

    [string]$AssignUserUpn,

    # Explicit identifier of an EXISTING app to reconcile. Supply one of these to adopt an app in
    # place. When neither is supplied the script is CREATE-ONLY and will never adopt an app
    # located by display name (that would allow a same-name attacker app to be hijacked).
    # GUID-validated so a malformed value fails fast at binding and can never be interpolated into
    # the appId OData filter or the object-id URL path below as an injection vector.
    [ValidatePattern('(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')]
    [string]$ClientId,

    [ValidatePattern('(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')]
    [string]$AppObjectId,

    # Adopt an existing app that does NOT carry this tool's managed marker tag. Requires that the
    # signed-in user still owns the app and its identifier URI is unset or already correct.
    [switch]$AllowUnmarkedAdoption,

    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'

# Marker tag stamped on apps this tool creates. Adoption of an existing app requires this marker
# (or an explicit -AllowUnmarkedAdoption override plus ownership + identifier-URI checks) so the
# reconciler never mutates an unrelated or attacker-planted application.
$script:ManagedTag = 'AzureAIDriveThruManaged'

# Required Entra access-token version (design 18.1): tighter than Retail Pulse, which also
# accepts the v1 sts.windows.net issuer form. Both backends validate the v2 issuer only.
$script:RequestedAccessTokenVersion = 2

# Central write-enable flag. Every Graph mutation (POST/PATCH/DELETE) is hard-gated on this so
# preview mode (no -Apply) can NEVER make a write, even if a future code path forgets a guard.
$script:ApplyWrites = [bool]$Apply

function Write-Section([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Plan([string]$text) { Write-Host "  [plan] $text" -ForegroundColor Yellow }
function Write-Done([string]$text) { Write-Host "  [done] $text" -ForegroundColor Green }
function Write-Skip([string]$text) { Write-Host "  [ok]   $text" -ForegroundColor DarkGray }

# --- Graph helpers (delegated az token; never prints the token) ---------------
function Invoke-Graph {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'DELETE')][string]$Method,
        [Parameter(Mandatory)][string]$Url,
        [object]$Body
    )
    # Preview-no-writes choke point: refuse every mutating verb unless -Apply was passed.
    if ($Method -in @('POST', 'PATCH', 'DELETE') -and -not $script:ApplyWrites) {
        throw "Refusing $Method $Url in preview mode. Re-run with -Apply to perform writes."
    }
    $restArgs = @('rest', '--method', $Method.ToLower(), '--url', $Url,
        '--headers', 'Content-Type=application/json')
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 20 -Compress
        # Write body to a temp file to avoid shell-quoting issues on all platforms.
        $tmp = New-TemporaryFile
        try {
            Set-Content -Path $tmp -Value $json -Encoding utf8
            $restArgs += @('--body', "@$tmp")
            $out = az @restArgs 2>&1
        }
        finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
    else {
        $out = az @restArgs 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Graph $Method $Url failed: $out"
    }
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    return ($out | ConvertFrom-Json)
}

# --- Safe application resolution helpers --------------------------------------
# Returns the object id of the signed-in caller (used to prove app ownership).
function Get-CallerObjectId {
    $me = Invoke-Graph -Method GET -Url "$graph/me?`$select=id"
    if (-not $me -or -not $me.id) { throw 'Unable to resolve the signed-in caller from Microsoft Graph (/me).' }
    return $me.id
}

# True when $callerId is listed among the application's registered owners.
function Test-AppOwnedByCaller {
    param([Parameter(Mandatory)][string]$AppObjectId, [Parameter(Mandatory)][string]$CallerId)
    $owners = Invoke-Graph -Method GET -Url "$graph/applications/$AppObjectId/owners?`$select=id"
    $ownerIds = @()
    if ($owners -and $owners.value) { $ownerIds = @($owners.value).id }
    return $ownerIds -contains $CallerId
}

# Reads a property off a Graph object safely under StrictMode (missing property -> $default).
function Get-Prop {
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($Object -and ($Object.PSObject.Properties.Name -contains $Name)) { return $Object.$Name }
    return $Default
}

# Gate BEFORE any mutation of an EXISTING app that was explicitly targeted for adoption. Refuses
# unless: (1) the caller owns the app, (2) its identifierUris are unset or already equal the
# expected api://{clientId} audience (never repoints another app's URI), and (3) it carries the
# managed marker tag -- unless -AllowUnmarkedAdoption was supplied.
function Assert-SafeToAdopt {
    param(
        [Parameter(Mandatory)]$App,
        [Parameter(Mandatory)][string]$CallerId,
        [Parameter(Mandatory)][string]$ExpectedAudience
    )
    $objectId = Get-Prop $App 'id'

    if (-not (Test-AppOwnedByCaller -AppObjectId $objectId -CallerId $CallerId)) {
        throw "Refusing to modify application ${objectId}: the signed-in user is not a registered owner. Adopt only apps you own."
    }

    $uris = @(Get-Prop $App 'identifierUris' @())
    $uriSafe = ($uris.Count -eq 0) -or ($uris.Count -eq 1 -and $uris[0] -eq $ExpectedAudience)
    if (-not $uriSafe) {
        throw "Refusing to modify application ${objectId}: identifierUris [$($uris -join ', ')] do not match expected '$ExpectedAudience'. This app is not AzureAIDriveThru's API."
    }

    $tags = @(Get-Prop $App 'tags' @())
    $hasMarker = $tags -contains $script:ManagedTag
    if (-not $hasMarker) {
        if (-not $AllowUnmarkedAdoption) {
            throw "Refusing to adopt application ${objectId}: it lacks the '$($script:ManagedTag)' marker (not provisioned by this tool). Verify you own the correct app, then re-run with -AllowUnmarkedAdoption to override."
        }
        Write-Warning "Application $objectId lacks the '$($script:ManagedTag)' marker; adopting anyway because -AllowUnmarkedAdoption was supplied (ownership + identifier URI already verified)."
    }
}

# Resolves the app to reconcile. Explicit -AppObjectId / -ClientId adopt a verified, owned app.
# With NO explicit identifier the script is create-only: a display-name lookup is used ONLY to
# detect a collision and HARD-FAIL -- it never adopts an app located by name, which is what closes
# the same-name app-hijack hole. Returns $null to signal "create a new app".
function Resolve-TargetApplication {
    $callerId = Get-CallerObjectId
    $selectFields = 'id,appId,displayName,signInAudience,identifierUris,tags,api,spa,web'

    if ($AppObjectId) {
        $app = Invoke-Graph -Method GET -Url "$graph/applications/$AppObjectId`?`$select=$selectFields"
        if (-not $app) { throw "No application found with object id '$AppObjectId'." }
        Assert-SafeToAdopt -App $app -CallerId $callerId -ExpectedAudience "api://$(Get-Prop $app 'appId')"
        return $app
    }

    if ($ClientId) {
        $resp = Invoke-Graph -Method GET -Url "$graph/applications?`$filter=appId eq '$ClientId'&`$select=$selectFields"
        $found = @()
        if ($resp -and $resp.value) { $found = @($resp.value) }
        if ($found.Count -eq 0) { throw "No application found with appId (client id) '$ClientId'." }
        if ($found.Count -gt 1) { throw "Ambiguous: $($found.Count) applications share appId '$ClientId'. Refusing to guess. Use -AppObjectId." }
        $app = $found[0]
        Assert-SafeToAdopt -App $app -CallerId $callerId -ExpectedAudience "api://$(Get-Prop $app 'appId')"
        return $app
    }

    # No explicit identifier -> CREATE-ONLY. Detect same-name collisions and refuse; never adopt.
    $escapedName = $DisplayName.Replace("'", "''")
    $resp = Invoke-Graph -Method GET -Url "$graph/applications?`$filter=displayName eq '$escapedName'&`$select=id,appId,displayName"
    $sameName = @()
    if ($resp -and $resp.value) { $sameName = @($resp.value) }
    if ($sameName.Count -gt 0) {
        $ids = ($sameName | ForEach-Object { "appId=$($_.appId)" }) -join ', '
        throw "Found $($sameName.Count) existing application(s) named '$DisplayName' ($ids). This tool will NOT adopt an app by display name (a same-name app may be attacker-controlled). Re-run with -ClientId <appId> or -AppObjectId <objectId> of the app you own to reconcile it, or choose a different -DisplayName to create a new one."
    }
    return $null
}

# --- 1. Validate az context + tenant -----------------------------------------
Write-Section 'Validating Azure CLI context'
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not logged in. Run: az login --tenant <tenantId>' }
if ($account.tenantId -ne $TenantId) {
    throw "Signed-in tenant '$($account.tenantId)' != requested -TenantId '$TenantId'. Run: az login --tenant $TenantId"
}
Write-Done "Tenant $TenantId confirmed"

if (-not $AssignUserUpn) {
    $AssignUserUpn = $account.user.name
}
Write-Skip "App-role assignee: $AssignUserUpn"
if (-not $Apply) {
    Write-Host "`nPREVIEW MODE -- no changes will be made. Re-run with -Apply to provision." -ForegroundColor Magenta
}

# --- 2. Resolve redirect URIs, including -FromAzdEnv (design 18.7/18.9/18.10) --
# -FromAzdEnv reads the azd environment directly (no .env file, per the "reads no .env file"
# safety invariant): BACKEND_URI (the Python app) and BACKEND_DOTNET_URI (the .NET app, #17).
# BACKEND_URI is REQUIRED here: it is blank while BACKEND_INGRESS_ENABLED=false (the dark
# provision, 18.10 step 4), and reconciling redirect URIs without it would silently drop the
# Python backend's origin instead of surfacing that the rollout hasn't reached the public step.
$azdOrigins = @()
if ($FromAzdEnv) {
    Write-Section 'Reading backend origins from the azd environment'
    $backendUri = (azd env get-value BACKEND_URI 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($backendUri)) {
        throw "-FromAzdEnv: azd env value BACKEND_URI is empty. This is expected while BACKEND_INGRESS_ENABLED=false (design 18.10 step 4, the dark provision) -- do not reconcile redirect URIs without it. Derive the FQDN with the commands in .EXAMPLE (a) and pass -FrontendOrigin, or re-run once the public provision (step 5) has set BACKEND_URI."
    }
    $azdOrigins += $backendUri
    Write-Done "BACKEND_URI = $backendUri"

    $backendDotnetUri = (azd env get-value BACKEND_DOTNET_URI 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($backendDotnetUri)) {
        Write-Skip 'BACKEND_DOTNET_URI is empty (the .NET app is not deployed yet, #17) -- skipping'
    }
    else {
        $azdOrigins += $backendDotnetUri
        Write-Done "BACKEND_DOTNET_URI = $backendDotnetUri"
    }
}

# Assemble redirect URIs (trim trailing slashes; de-dupe).
$redirects = @()
foreach ($u in @($FrontendOrigin + $RedirectUri + $azdOrigins)) {
    if (-not [string]::IsNullOrWhiteSpace($u)) { $redirects += $u.TrimEnd('/') }
}
$redirects = @($redirects | Select-Object -Unique)
if ($redirects.Count -eq 0) {
    Write-Warning 'No -FrontendOrigin / -RedirectUri / -FromAzdEnv origin supplied. SPA redirect URIs will be left unset.'
}

# --- 3. Resolve or create the application (SAFE reconciliation) ---------------
# Never adopts by display name. Explicit -ClientId/-AppObjectId adopt an owned, verified, marked
# app; otherwise create-only after a hard-fail collision check. See helpers above.
Write-Section "Application registration '$DisplayName'"
$app = Resolve-TargetApplication

if (-not $app) {
    Write-Plan "Create single-tenant application '$DisplayName' (signInAudience=AzureADMyOrg, SPA public client, no secret, tag=$($script:ManagedTag))"
    if ($Apply) {
        # SPA-ONLY (design 18.1: tighter than Retail Pulse -- no Web platform redirect URIs).
        $body = @{
            displayName    = $DisplayName
            signInAudience = 'AzureADMyOrg'
            tags           = @($script:ManagedTag)
            spa            = @{ redirectUris = @($redirects) }
        }
        $app = Invoke-Graph -Method POST -Url "$graph/applications" -Body $body
        Write-Done "Created application appId=$($app.appId) objectId=$($app.id) (marked '$($script:ManagedTag)')"
    }
}
else {
    Write-Skip "Adopting owned+verified application: appId=$($app.appId) objectId=$($app.id)"
    if ((Get-Prop $app 'signInAudience') -ne 'AzureADMyOrg') {
        Write-Plan "Set signInAudience to AzureADMyOrg (single tenant) -- currently '$(Get-Prop $app 'signInAudience')'"
        if ($Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ signInAudience = 'AzureADMyOrg' } | Out-Null
            Write-Done 'signInAudience set to AzureADMyOrg'
        }
    }
}

# In preview with no pre-existing app we synthesize a placeholder so the plan reads clearly.
$resolvedAppObjectId = if ($app) { $app.id } else { '<new-app-object-id>' }
$resolvedClientId = if ($app -and $app.appId) { $app.appId } else { '<new-client-id>' }
$audience = "api://$resolvedClientId"

# --- 4. identifierUris (Application ID URI = api://{clientId}) -----------------
Write-Section 'Application ID URI (audience)'
$hasAudience = $app -and $app.identifierUris -and ($app.identifierUris -contains $audience)
if ($hasAudience) {
    Write-Skip "identifierUris already contains $audience"
}
else {
    Write-Plan "Set identifierUris = [ $audience ]"
    if ($Apply -and $app) {
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ identifierUris = @($audience) } | Out-Null
        Write-Done "identifierUris set to $audience"
    }
}

# --- 5. api sub-object: delegated scope, pre-authorized clients, token version -
# All three live under the application's `api` complex property. Reconciled together in one
# PATCH so an earlier partial write can never leave a sibling api field (e.g.
# requestedAccessTokenVersion) reset to its default.
Write-Section "API configuration (scope '$ApiScopeName', v$($script:RequestedAccessTokenVersion) tokens, pre-authorized clients)"
$appApi = if ($app) { Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=api" } else { $null }
$existingScopes = @()
if ($appApi -and $appApi.api -and $appApi.api.oauth2PermissionScopes) {
    $existingScopes = @($appApi.api.oauth2PermissionScopes)
}
$scope = $existingScopes | Where-Object { $_.value -eq $ApiScopeName } | Select-Object -First 1
if ($scope) {
    $desiredScopes = $existingScopes
}
else {
    $newScope = @{
        id                      = [guid]::NewGuid().ToString()
        value                   = $ApiScopeName
        type                    = 'User'
        isEnabled               = $true
        adminConsentDisplayName = 'Access AzureAIDriveThru as the signed-in user'
        adminConsentDescription = 'Allow the app to access the AzureAIDriveThru API on behalf of the signed-in user.'
        userConsentDisplayName  = 'Access AzureAIDriveThru'
        userConsentDescription  = 'Allow the app to access the AzureAIDriveThru API on your behalf.'
    }
    $desiredScopes = @($existingScopes + $newScope)
}
$scopeIdForPreAuth = if ($scope) { $scope.id } else { $desiredScopes[-1].id }

$existingPreAuth = @()
if ($appApi -and $appApi.api -and $appApi.api.preAuthorizedApplications) {
    $existingPreAuth = @($appApi.api.preAuthorizedApplications)
}
$desiredPreAuth = @()
if ($PreAuthorizedClientAppId.Count -gt 0) {
    $desiredPreAuth = @($PreAuthorizedClientAppId | ForEach-Object {
            [pscustomobject]@{ appId = $_; delegatedPermissionIds = @($scopeIdForPreAuth) }
        })
}
function ConvertTo-PreAuthKey($entries) {
    ($entries | ForEach-Object {
        $ids = @($_.delegatedPermissionIds | Sort-Object) -join ','
        "$($_.appId)|$ids"
    } | Sort-Object) -join ';'
}

$existingTokenVersion = if ($appApi -and $appApi.api) { Get-Prop $appApi.api 'requestedAccessTokenVersion' $null } else { $null }

$scopeChanged = ($scope -eq $null)
$preAuthChanged = (ConvertTo-PreAuthKey $existingPreAuth) -ne (ConvertTo-PreAuthKey $desiredPreAuth)
$tokenVersionChanged = $existingTokenVersion -ne $script:RequestedAccessTokenVersion

if ($scope) { Write-Skip "Scope '$ApiScopeName' exists (id=$($scope.id))" }
else { Write-Plan "Add delegated scope '$ApiScopeName' (adminConsent, enabled)" }

if ($desiredPreAuth.Count -gt 0) {
    $preview = ($desiredPreAuth | ForEach-Object { $_.appId }) -join ', '
}
else { $preview = '<empty>' }
if ($preAuthChanged) { Write-Plan "Reconcile preAuthorizedApplications to: $preview (scope '$ApiScopeName')" }
else { Write-Skip "preAuthorizedApplications already reconciled: $preview" }

if ($tokenVersionChanged) { Write-Plan "Set requestedAccessTokenVersion = $($script:RequestedAccessTokenVersion) (currently '$existingTokenVersion')" }
else { Write-Skip "requestedAccessTokenVersion already $($script:RequestedAccessTokenVersion)" }

if (($scopeChanged -or $preAuthChanged -or $tokenVersionChanged) -and $Apply -and $app) {
    Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{
        api = @{
            oauth2PermissionScopes     = $desiredScopes
            preAuthorizedApplications  = $desiredPreAuth
            requestedAccessTokenVersion = $script:RequestedAccessTokenVersion
        }
    } | Out-Null
    Write-Done 'API configuration reconciled'
}

# --- 6. App role (DriveThru.User) ---------------------------------------------
Write-Section "App role '$AppRoleValue'"
$appRoles = if ($app) {
    $r = Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=appRoles"
    @($r.appRoles)
}
else { @() }
$role = $appRoles | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
if ($role) {
    Write-Skip "App role '$AppRoleValue' exists (id=$($role.id))"
}
else {
    $roleId = [guid]::NewGuid().ToString()
    Write-Plan "Add app role '$AppRoleValue' (id=$roleId, allowedMemberTypes=User, enabled)"
    if ($Apply -and $app) {
        $newRole = @{
            id                 = $roleId
            value              = $AppRoleValue
            displayName        = 'AzureAIDriveThru User'
            description        = 'Users who may access the AzureAIDriveThru demo.'
            allowedMemberTypes = @('User')
            isEnabled          = $true
        }
        $merged = @($appRoles + $newRole)
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ appRoles = $merged } | Out-Null
        Write-Done "App role '$AppRoleValue' added"
    }
}

# --- 7. Redirect URIs: SPA-only, full reconcile; clear any Web URIs ------------
# Design 18.1: tighter than Retail Pulse -- SPA platform only, no Web platform redirect URIs.
# A full SET (not merge) so a stale origin (e.g. a re-provisioned environment's old FQDN) is
# actually removed, not just supplemented. Review item 2: a full SET with an EMPTY list would
# silently wipe every existing SPA redirect URI on a run that supplied none (e.g. -RedirectUri
# @() with no -FrontendOrigin/-FromAzdEnv), breaking sign-in for every already-registered origin.
# So the SPA reconcile below only ever runs when $redirects.Count -gt 0; with zero redirects the
# existing SPA URIs are left untouched and only the (always SPA-only) Web platform is cleared.
Write-Section 'Redirect URIs (SPA-only)'
if ($app) {
    $current = Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=spa,web"
    $curSpa = @()
    if ($current.spa -and $current.spa.redirectUris) { $curSpa = @($current.spa.redirectUris) }
    $curWeb = @()
    if ($current.web -and $current.web.redirectUris) { $curWeb = @($current.web.redirectUris) }

    $webChanged = $curWeb.Count -gt 0
    if ($webChanged) { Write-Plan "Clear Web platform redirect URIs (found: $($curWeb -join ', '))" }
    else { Write-Skip 'No Web platform redirect URIs present' }

    if ($redirects.Count -eq 0) {
        Write-Skip 'no redirect URIs supplied, SPA URIs left unchanged'
        if ($webChanged -and $Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{
                web = @{ redirectUris = @() }
            } | Out-Null
            Write-Done 'Web platform redirect URIs cleared'
        }
    }
    elseif ($redirects.Count -gt 0) {
        $spaKey = ($curSpa | Sort-Object) -join ';'
        $desiredKey = ($redirects | Sort-Object) -join ';'
        $spaChanged = $spaKey -ne $desiredKey

        if ($spaChanged) { Write-Plan "Set SPA redirect URIs: $($redirects -join ', ')" }
        else { Write-Skip "SPA redirect URIs already reconciled: $($redirects -join ', ')" }

        if (($spaChanged -or $webChanged) -and $Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{
                spa = @{ redirectUris = @($redirects) }
                web = @{ redirectUris = @() }
            } | Out-Null
            Write-Done 'Redirect URIs reconciled (SPA-only)'
        }
    }
}
else {
    Write-Skip 'No app yet to reconcile redirect URIs on (preview of a new app)'
}

# --- 8. Service principal + assignmentRequired --------------------------------
Write-Section 'Service principal (assignment required)'
$sp = $null
if ($resolvedClientId -and $resolvedClientId -notlike '<*') {
    $spResp = Invoke-Graph -Method GET -Url "$graph/servicePrincipals?`$filter=appId eq '$resolvedClientId'&`$select=id,appId,appRoleAssignmentRequired"
    if ($spResp.value.Count -gt 0) { $sp = $spResp.value[0] }
}
if (-not $sp) {
    Write-Plan "Create service principal for appId=$resolvedClientId with appRoleAssignmentRequired=true"
    if ($Apply -and $app) {
        $sp = Invoke-Graph -Method POST -Url "$graph/servicePrincipals" -Body @{ appId = $resolvedClientId }
        Invoke-Graph -Method PATCH -Url "$graph/servicePrincipals/$($sp.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
        Write-Done "Service principal created (id=$($sp.id)), assignment required = true"
    }
}
else {
    Write-Skip "Service principal exists (id=$($sp.id))"
    if (-not $sp.appRoleAssignmentRequired) {
        Write-Plan 'Set appRoleAssignmentRequired = true'
        if ($Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/servicePrincipals/$($sp.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
            Write-Done 'appRoleAssignmentRequired set to true'
        }
    }
    else {
        Write-Skip 'appRoleAssignmentRequired already true'
    }
}

# --- 9. Assign the operator (or -AssignUserUpn) the app role ------------------
Write-Section "App-role assignment for $AssignUserUpn"
if ($sp) {
    # The lookups below are all GET (always allowed, even in preview) so a preview run can report
    # "already assigned" accurately instead of always printing a generic "[plan] Assign ..." --
    # cosmetic, but avoids the misleading suggestion of a pending write when Apply would in fact
    # no-op.
    #
    # URL-encode the UPN before placing it in the Graph path segment so guest UPNs (which contain
    # '#', e.g. alice_contoso.com#EXT#@tenant.onmicrosoft.com) and any other reserved characters
    # resolve correctly instead of being truncated at '#' or altering the request path.
    $user = Invoke-Graph -Method GET -Url "$graph/users/$([uri]::EscapeDataString($AssignUserUpn))`?`$select=id,userPrincipalName"
    # Resolve the role id from the SP's published appRoles (post-apply it exists).
    $spRoles = Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$($sp.id)?`$select=appRoles"
    $targetRole = @($spRoles.appRoles) | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
    if (-not $targetRole) {
        if ($Apply) { throw "App role '$AppRoleValue' not found on service principal yet." }
        Write-Plan "Assign $AssignUserUpn to app role '$AppRoleValue' on the service principal (role not published yet; will exist post-Apply)"
    }
    else {
        # Query from the user's relationship. Some tenants reject filtered reads of
        # servicePrincipals/{id}/appRoleAssignedTo even though assignment writes are permitted. The
        # user relationship is broadly supported and we filter the bounded assignment list locally
        # by resource and role.
        $existingAssignments = Invoke-Graph -Method GET -Url "$graph/users/$($user.id)/appRoleAssignments?`$select=id,resourceId,appRoleId"
        $already = @($existingAssignments.value) | Where-Object {
            $_.resourceId -eq $sp.id -and $_.appRoleId -eq $targetRole.id
        }
        if ($already) {
            Write-Skip "$AssignUserUpn already assigned to '$AppRoleValue'"
        }
        elseif ($Apply) {
            Invoke-Graph -Method POST -Url "$graph/users/$($user.id)/appRoleAssignments" -Body @{
                principalId = $user.id
                resourceId  = $sp.id
                appRoleId   = $targetRole.id
            } | Out-Null
            Write-Done "Assigned $AssignUserUpn to '$AppRoleValue'"
        }
        else {
            Write-Plan "Assign $AssignUserUpn to app role '$AppRoleValue' on the service principal"
        }
    }
}
else {
    Write-Plan "Assign $AssignUserUpn to app role '$AppRoleValue' on the service principal"
}

# --- 10. Emit SAFE config (no secrets) -----------------------------------------
Write-Section 'Safe configuration output (non-secret)'
$finalScope = "$audience/$ApiScopeName"
[PSCustomObject]@{
    TenantId    = $TenantId
    ClientId    = $resolvedClientId
    Audience    = $audience
    ApiScope    = $ApiScopeName
    ApiScopeUri = $finalScope
    AppRole     = $AppRoleValue
} | Format-List | Out-String | Write-Host

Write-Host 'Next -- wire these into azd (public identifiers only, safe to commit to your azd env):' -ForegroundColor Cyan
Write-Host "  azd env set ENTRA_TENANT_ID $TenantId"
Write-Host "  azd env set ENTRA_CLIENT_ID $resolvedClientId"
Write-Host ''
Write-Host "Then verify with: ./scripts/Verify-EntraAuth.ps1 -TenantId $TenantId -ClientId $resolvedClientId" -ForegroundColor Cyan
if (-not $Apply) {
    Write-Host "`nPreview complete. Re-run with -Apply to make these changes." -ForegroundColor Magenta
}
