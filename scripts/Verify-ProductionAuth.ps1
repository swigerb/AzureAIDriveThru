#Requires -Version 7.0
<#
.SYNOPSIS
    Read-only live posture check of the deployed Entra auth configuration for AzureAIDriveThru
    (ADR-002, docs/persona-architecture.md 18.9/18.10).

.DESCRIPTION
    Verifies, for EACH deployed backend container app (Python `backend`, and `backend-dotnet` once
    #17 ships), that:
      * Env pins: `AUTH_MODE=Entra`; the app's production flag (`RUNNING_IN_PRODUCTION=true` for
        Python, `ASPNETCORE_ENVIRONMENT=Production` for dotnet); the `ENTRA_*` values match the
        expected ids (printed REDACTED); and `ENTRA_INSTANCE` is NOT set (it must never be pinned
        in Azure -- only the fake-issuer harness sets it, to a loopback address).
      * Active revisions: EVERY revision with `properties.active == true` (not only the latest
        template) runs the expected image and carries the same env pins. Any active revision on a
        different image, or missing `AUTH_MODE=Entra`, fails the check. Zero active revisions is
        also a failure.
      * EasyAuth is off: `az containerapp auth show` reports `platform.enabled == false` (an
        UNKNOWN/unreadable state is treated as a failure, not a pass) and no leftover
        `aad-client-secret` container app secret remains.
      * Registration: delegates to Verify-EntraAuth.ps1 (single tenant, v2 tokens, scope/role,
        SPA-only redirects, no client secret, appRoleAssignmentRequired).
      * Anonymous probes (skipped under -RevisionsOnly): `/` is 200 and carries the
        `drivethru-auth-mode` Entra marker; `/health` is 200; a branding asset is 200;
        `/api/personas`, `/api/auth/session`, a `menu.json` and an asset JSON path are 401;
        `/api/personas?access_token=<synthetic>` is 401 (query-string tokens are honored on
        `/realtime` only); the `/realtime` upgrade is 401 with no token and with a synthetic
        token.

    -RevisionsOnly: runs ONLY the env-pin, active-revision and EasyAuth checks, entirely through
    `az` (no HTTP calls at all), so it works while ingress is disabled (18.10 step 4a, the "dark"
    check during the dark provision). It also FAILS if ingress is currently enabled on any app,
    since this switch is specifically the dark-provision gate and an ingress-enabled app means the
    rollout has moved past that step.

    -Authenticated: additionally acquires a delegated user token via
    `az account get-access-token --scope api://{clientId}/{ApiScopeName}` (the caller must already
    hold the `DriveThru.User` role) and expects 200 from `GET /api/personas` with that bearer. The
    token itself is NEVER printed or logged.

    Every GUID (tenant id, client id) is REDACTED in output (first 8 chars + `...`). No writes are
    made anywhere; this script only reads.

.PARAMETER TenantId
    Entra tenant GUID. Defaults to the azd env `ENTRA_TENANT_ID` (fallback `AZURE_TENANT_ID`).

.PARAMETER ClientId
    Application (client) id GUID. Defaults to the azd env `ENTRA_CLIENT_ID`.

.PARAMETER ResourceGroup
    Resource group containing the container apps. Defaults to the azd env `AZURE_RESOURCE_GROUP`.

.PARAMETER ApiScopeName
    Expected delegated scope name. Default: access_as_user.

.PARAMETER AppRoleValue
    Expected app role value. Default: DriveThru.User.

.PARAMETER ExpectedImage
    Optional hashtable overriding the expected image per azd service name, e.g.
    @{ backend = 'myacr.azurecr.io/backend:abc123' }. Any service not present in this hashtable
    falls back to the azd env value `SERVICE_<NAME>_IMAGE_NAME` (dashes -> underscores, upper
    case), e.g. `SERVICE_BACKEND_IMAGE_NAME`, `SERVICE_BACKEND_DOTNET_IMAGE_NAME`.

.PARAMETER RevisionsOnly
    Only run env-pin, active-revision and EasyAuth checks via `az` (no HTTP). Fails if ingress is
    enabled on any app. Use for the dark-provision check (18.10 step 4a).

.PARAMETER Authenticated
    Additionally verify a real delegated token is accepted by `GET /api/personas` (200). Requires
    `az login` as a user assigned the app role. Never prints the token.

.PARAMETER SkipRegistrationCheck
    Skip delegating to Verify-EntraAuth.ps1 (useful if it was already run separately in the same
    session).

.PARAMETER ProbePersona
    Persona id used to build the branding-asset, menu.json and non-branding-asset-JSON probe paths
    under the anonymous checks (design 18.2). Resolved, when not passed explicitly, from the azd
    env `DEFAULT_PERSONA`, then the first id in the azd env `PERSONAS`. Generic (never hardcodes a
    brand), so this script gives an accurate branding-probe FAIL, not a false one, on any
    environment whose `PERSONAS` excludes a particular pack.

.EXAMPLE
    ./scripts/Verify-ProductionAuth.ps1 -RevisionsOnly
    # Dark-provision check (18.10 step 4a): reads everything from the azd env.

.EXAMPLE
    ./scripts/Verify-ProductionAuth.ps1 -Authenticated
    # Full live check after the public provision (18.10 step 5).

.NOTES
    Read-only. Requires `az login` with Reader access to the resource group (and, for
    -Authenticated, a delegated token the caller is entitled to).
#>
[CmdletBinding()]
param(
    [string]$TenantId,
    [string]$ClientId,
    [string]$ResourceGroup,
    [string]$ApiScopeName = 'access_as_user',
    [string]$AppRoleValue = 'DriveThru.User',
    [hashtable]$ExpectedImage = @{},
    [switch]$RevisionsOnly,
    [switch]$Authenticated,
    [switch]$SkipRegistrationCheck,

    # Generic persona id for the branding/menu/asset probes (review item 8): never hardcode a
    # brand word in this script. Resolved from the azd env when not supplied (below).
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$ProbePersona
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Backends this script knows how to check, keyed by the azd-service-name tag stamped on the
# container app resource (infra/main.bicep). Each entry names the production-flag env var this
# service's runtime requires (design 18.5) and the azd env var carrying its expected image.
$script:KnownServices = @{
    'backend'         = @{ ProductionFlag = 'RUNNING_IN_PRODUCTION'; ProductionValue = 'true'; ImageEnvVar = 'SERVICE_BACKEND_IMAGE_NAME' }
    'backend-dotnet'  = @{ ProductionFlag = 'ASPNETCORE_ENVIRONMENT'; ProductionValue = 'Production'; ImageEnvVar = 'SERVICE_BACKEND_DOTNET_IMAGE_NAME' }
}

function Get-AzdEnvValue {
    param([Parameter(Mandatory)][string]$Name)
    ((azd env get-value $Name 2>$null) | Out-String).Trim()
}

function Format-Redacted {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '<empty>' }
    if ($Value.Length -le 8) { return "$Value..." }
    return "$($Value.Substring(0, 8))..."
}

# Reads a property off a plain object (JSON-deserialized or hashtable) safely under StrictMode.
function Get-Prop {
    param($Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [hashtable]) {
        if ($Object.ContainsKey($Name)) { return $Object[$Name] }
        return $Default
    }
    if ($null -ne $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $Default
}

# Round 3 review, item 6 (hardening): `az ... --output json 2>&1 | ConvertFrom-Json` breaks if
# `az` ever prints a stderr warning on an otherwise-successful call (for example a containerapp
# extension update notice) -- the warning text becomes an extra non-JSON line in the merged
# stream and ConvertFrom-Json throws. Native-command stderr merged via 2>&1 arrives as
# ErrorRecord objects, not strings, so partitioning the merged stream by type cleanly separates
# stdout (parsed as JSON) from stderr (surfaced only in the thrown error, never silently dropped)
# without a temp file. Scoped to the `az containerapp list/revision list/secret list` calls,
# which return a JSON array Verify-ProductionAuth immediately parses; `az account get-access-token`
# (Test-AuthenticatedProbe) is left as-is; it is already covered by a separate, more specific
# review pin (`_TOKEN_REFERENCE_ALLOWLIST`) that this refactor would otherwise have to widen.
function Invoke-AzJsonList {
    param([Parameter(Mandatory)][string]$Description, [Parameter(Mandatory)][string[]]$Arguments)
    $raw = @(& az @Arguments --output json 2>&1)
    $stdout = @($raw | Where-Object { $_ -is [string] })
    $stderr = @($raw | Where-Object { $_ -isnot [string] } | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed: $($stderr -join ' | ')"
    }
    if ($stderr.Count -gt 0) {
        Write-Host "Warning: $Description printed to stderr (ignored, command exit 0): $($stderr -join ' | ')" -ForegroundColor Yellow
    }
    $joined = ($stdout -join [Environment]::NewLine)
    if ([string]::IsNullOrWhiteSpace($joined)) { return @() }
    return @($joined | ConvertFrom-Json)
}

$results = New-Object System.Collections.Generic.List[pscustomobject]
function Add-Result([string]$Check, [bool]$Pass, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Pass = $Pass; Detail = $Detail })
}

# A synthetic (structurally-plausible but never valid) bearer used only to prove the endpoint
# rejects an untrusted/garbage token, never a real credential.
$script:SyntheticToken = 'eyJhbGciOiJub25lIn0.eyJzdWIiOiJzeW50aGV0aWMtcHJvYmUifQ.'

function Invoke-ProbeRequest {
    param([Parameter(Mandatory)][string]$Url, [string]$BearerToken, [hashtable]$Headers = @{})
    $reqHeaders = @{} + $Headers
    if ($BearerToken) { $reqHeaders['Authorization'] = "Bearer $BearerToken" }
    try {
        $resp = Invoke-WebRequest -Uri $Url -Headers $reqHeaders -SkipHttpErrorCheck -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
        return $resp
    }
    catch {
        # Non-HTTP failure (DNS/timeout/connection refused) -- surface as a synthetic 0-status
        # response so callers can still record a clear failing check instead of throwing.
        return [pscustomobject]@{ StatusCode = 0; Content = "probe error: $($_.Exception.Message)"; Headers = @{} }
    }
}

# Anonymous-access probes (design 18.2 route matrix, 18.9 Verify-ProductionAuth spec). Skipped
# entirely under -RevisionsOnly (no HTTP while ingress may be off).
function Test-AnonymousProbes {
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$ServiceName,
        # Generic persona id (review item 8): this script never hardcodes a brand word. When
        # unresolved, the persona-scoped probes below record an explicit FAIL asking for
        # -ProbePersona instead of guessing a brand id.
        [string]$ProbePersona
    )

    $root = Invoke-ProbeRequest -Url $BaseUrl
    $rootOk = $root.StatusCode -eq 200
    $markerOk = $rootOk -and ($root.Content -match 'drivethru-auth-mode["'']?\s*content=["'']Entra')
    Add-Result "${ServiceName}: GET / is 200" $rootOk "status=$($root.StatusCode)"
    Add-Result "${ServiceName}: GET / carries the Entra auth-mode marker" $markerOk 'meta[name=drivethru-auth-mode][content=Entra] present'

    $health = Invoke-ProbeRequest -Url "$BaseUrl/health"
    Add-Result "${ServiceName}: GET /health is 200 (anonymous)" ($health.StatusCode -eq 200) "status=$($health.StatusCode)"

    if (-not $ProbePersona) {
        Add-Result "${ServiceName}: GET branding asset (logo.svg) is 200 (anonymous)" $false 'no persona resolved (azd env DEFAULT_PERSONA/PERSONAS empty); pass -ProbePersona'
    }
    else {
        # A public branding asset (18.2): image/audio types stay anonymous so <img>/<audio>/favicon
        # never need a bearer. Any enabled persona's logo is a stable, always-present fixture.
        $branding = Invoke-ProbeRequest -Url "$BaseUrl/personas/$ProbePersona/assets/logo.svg"
        Add-Result "${ServiceName}: GET branding asset (logo.svg) is 200 (anonymous)" ($branding.StatusCode -eq 200) "status=$($branding.StatusCode)"
    }

    $protectedGets = @(
        @{ Path = '/api/personas'; Label = 'GET /api/personas' }
        @{ Path = '/api/auth/session'; Label = 'GET /api/auth/session' }
    )
    if ($ProbePersona) {
        $protectedGets += @{ Path = "/personas/$ProbePersona/menu.json"; Label = 'GET menu.json' }
        $protectedGets += @{ Path = "/personas/$ProbePersona/assets/demo/dummyOrder.json"; Label = 'GET asset JSON (non-branding)' }
    }
    else {
        Add-Result "${ServiceName}: GET menu.json is 401 (no token)" $false 'no persona resolved; pass -ProbePersona'
        Add-Result "${ServiceName}: GET asset JSON (non-branding) is 401 (no token)" $false 'no persona resolved; pass -ProbePersona'
    }
    foreach ($p in $protectedGets) {
        $r = Invoke-ProbeRequest -Url "$BaseUrl$($p.Path)"
        Add-Result "${ServiceName}: $($p.Label) is 401 (no token)" ($r.StatusCode -eq 401) "status=$($r.StatusCode)"
    }

    # Query-string tokens are honored on /realtime only (design 18.3): the same synthetic token as
    # a REST query parameter must still be rejected.
    $queryToken = Invoke-ProbeRequest -Url "$BaseUrl/api/personas?access_token=$($script:SyntheticToken)"
    Add-Result "${ServiceName}: GET /api/personas?access_token=<synthetic> is 401 (query token ignored on REST)" ($queryToken.StatusCode -eq 401) "status=$($queryToken.StatusCode)"

    # /realtime is a WebSocket upgrade; a plain HTTP GET without the Upgrade handshake still
    # reaches the same auth middleware and must be rejected with 401 before any upgrade occurs.
    $wsNoToken = Invoke-ProbeRequest -Url "$BaseUrl/realtime" -Headers @{ Connection = 'Upgrade'; Upgrade = 'websocket' }
    Add-Result "${ServiceName}: /realtime upgrade with no token is 401" ($wsNoToken.StatusCode -eq 401) "status=$($wsNoToken.StatusCode)"

    $wsSynthetic = Invoke-ProbeRequest -Url "$BaseUrl/realtime?access_token=$($script:SyntheticToken)" -Headers @{ Connection = 'Upgrade'; Upgrade = 'websocket' }
    Add-Result "${ServiceName}: /realtime upgrade with a synthetic token is 401" ($wsSynthetic.StatusCode -eq 401) "status=$($wsSynthetic.StatusCode)"
}

# -Authenticated: acquire a REAL delegated user token (the caller must already hold the app role)
# and confirm the API accepts it. The token is used only in-memory for this one request and is
# never written to output, logs, or any variable that gets printed.
function Test-AuthenticatedProbe {
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][string]$ApiScopeName
    )
    $scope = "api://$ClientId/$ApiScopeName"
    $tokenJson = az account get-access-token --scope $scope --tenant $TenantId --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        Add-Result "${ServiceName}: acquire delegated token for -Authenticated" $false "az account get-access-token failed (see stderr; token never logged): exit $LASTEXITCODE"
        return
    }
    $token = ($tokenJson | ConvertFrom-Json).accessToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        Add-Result "${ServiceName}: acquire delegated token for -Authenticated" $false 'no accessToken in response'
        return
    }
    try {
        $resp = Invoke-ProbeRequest -Url "$BaseUrl/api/personas" -BearerToken $token
        Add-Result "${ServiceName}: GET /api/personas with a valid delegated token is 200 (-Authenticated)" ($resp.StatusCode -eq 200) "status=$($resp.StatusCode)"
    }
    finally {
        # Best-effort scrub of the local reference; PowerShell strings are immutable so this does
        # not guarantee the value is gone from memory, but nothing above ever wrote it to output.
        $token = $null
    }
}

# --- Resolve identifiers from the azd environment when not explicitly supplied ---------------
if (-not $TenantId) {
    $TenantId = Get-AzdEnvValue 'ENTRA_TENANT_ID'
    if ([string]::IsNullOrWhiteSpace($TenantId)) { $TenantId = Get-AzdEnvValue 'AZURE_TENANT_ID' }
}
if (-not $ClientId) { $ClientId = Get-AzdEnvValue 'ENTRA_CLIENT_ID' }
if (-not $ResourceGroup) { $ResourceGroup = Get-AzdEnvValue 'AZURE_RESOURCE_GROUP' }

if ([string]::IsNullOrWhiteSpace($TenantId)) { throw 'Unable to resolve -TenantId (azd env ENTRA_TENANT_ID / AZURE_TENANT_ID is empty). Pass -TenantId explicitly.' }
if ([string]::IsNullOrWhiteSpace($ClientId)) { throw 'Unable to resolve -ClientId (azd env ENTRA_CLIENT_ID is empty). Pass -ClientId explicitly, or run Setup-EntraAuth.ps1 first.' }
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) { throw 'Unable to resolve -ResourceGroup (azd env AZURE_RESOURCE_GROUP is empty). Pass -ResourceGroup explicitly.' }

# Generic persona resolution (review item 8): never hardcode a brand id in this script. Resolved
# into a separate local, $probePersonaId, instead of reassigning the validated -ProbePersona
# parameter (round 2 blocker 3): a [ValidatePattern(...)] parameter re-validates on every
# assignment, so reassigning it to an empty/unresolved value (or $null) throws "The variable
# cannot be validated" instead of failing closed with a normal FAIL result.
$probePersonaId = $ProbePersona
if (-not $probePersonaId) {
    $probePersonaId = Get-AzdEnvValue 'DEFAULT_PERSONA'
    if ([string]::IsNullOrWhiteSpace($probePersonaId)) {
        $personasEnv = Get-AzdEnvValue 'PERSONAS'
        if (-not [string]::IsNullOrWhiteSpace($personasEnv)) {
            $probePersonaId = ($personasEnv -split ',')[0].Trim()
        }
    }
}
if ($probePersonaId -and $probePersonaId -cnotmatch '^[a-z0-9-]+$') {
    Add-Result 'ProbePersona resolves to a valid persona id' $false "resolved '$probePersonaId' does not match ^[a-z0-9-]+$ (pass -ProbePersona explicitly)"
    $probePersonaId = $null
}
if ([string]::IsNullOrWhiteSpace($probePersonaId)) {
    Write-Host 'Warning: no persona resolved (azd env DEFAULT_PERSONA/PERSONAS empty); pass -ProbePersona. Branding/menu/asset probes will FAIL.' -ForegroundColor Yellow
    $probePersonaId = $null
}

# The dotnet app's bicep resource DOES carry the 'azd-service-name': 'backend-dotnet' tag as of
# the #17 go-live PR, but only while deployDotnetApp is true (the whole module is conditional) --
# an environment that provisioned the dotnet app before this PR, or any future app whose tag write
# races this read, still needs a name-based fallback. Kept as a belt-and-suspenders discovery path
# rather than assuming the tag is always present.
$script:DotnetAppName = Get-AzdEnvValue 'AZURE_CONTAINER_APP_DOTNET_NAME'

# Resolves a container app's KnownServices key, safely under StrictMode: an app's `tags` may be
# absent entirely, or present without an `azd-service-name` key (an older dotnet deployment).
function Resolve-ServiceName {
    param([Parameter(Mandatory)]$ContainerApp)
    $svc = Get-Prop (Get-Prop $ContainerApp 'tags' $null) 'azd-service-name' $null
    if ($svc) { return $svc }
    if ($script:DotnetAppName -and $ContainerApp.name -eq $script:DotnetAppName) { return 'backend-dotnet' }
    return $null
}

Write-Host "Verify-ProductionAuth: tenant=$(Format-Redacted $TenantId) client=$(Format-Redacted $ClientId) rg=$ResourceGroup" -ForegroundColor Cyan
if ($RevisionsOnly) { Write-Host 'Mode: -RevisionsOnly (dark-provision check, no HTTP probes)' -ForegroundColor Cyan }
if ($Authenticated) { Write-Host 'Mode: -Authenticated (acquiring a delegated token; never printed)' -ForegroundColor Cyan }

# --- Discover deployed container apps by azd-service-name tag --------------------------------
$allApps = Invoke-AzJsonList -Description "az containerapp list (rg=$ResourceGroup)" -Arguments @('containerapp', 'list', '-g', $ResourceGroup)
$targetApps = @($allApps | Where-Object {
        $svc = Resolve-ServiceName $_
        $svc -and $script:KnownServices.ContainsKey($svc)
    })

if ($targetApps.Count -eq 0) {
    throw "No container apps tagged 'azd-service-name' in {$($script:KnownServices.Keys -join ', ')} found in resource group '$ResourceGroup'."
}
Write-Host "Discovered $($targetApps.Count) app(s): $(($targetApps | ForEach-Object { Resolve-ServiceName $_ }) -join ', ')`n"

foreach ($capp in $targetApps) {
    $svcName = Resolve-ServiceName $capp
    $svcSpec = $script:KnownServices[$svcName]
    $appName = $capp.name
    Write-Host "--- $svcName ($appName) ---" -ForegroundColor Cyan

    # Expected image: explicit override, else azd env SERVICE_<NAME>_IMAGE_NAME. Named $wantImage,
    # not $expectedImage: PowerShell variable names are case-insensitive, so $expectedImage would
    # be the same variable as the [hashtable]$ExpectedImage parameter (review round 2 blocker 2)
    # and assigning a string to it here would throw "Cannot convert ... to Hashtable".
    $wantImage = if ($ExpectedImage.ContainsKey($svcName)) { $ExpectedImage[$svcName] } else { Get-AzdEnvValue $svcSpec.ImageEnvVar }
    if ([string]::IsNullOrWhiteSpace($wantImage)) {
        Add-Result "${svcName}: expected image resolvable" $false "azd env $($svcSpec.ImageEnvVar) is empty and no -ExpectedImage override was given"
        continue
    }

    # --- Ingress state (used both to report and, under -RevisionsOnly, to fail-if-public) ------
    $ingressEnabled = [bool](Get-Prop $capp.properties.configuration 'ingress' $null)
    if ($RevisionsOnly) {
        Add-Result "${svcName}: ingress disabled (dark-provision gate)" (-not $ingressEnabled) "ingress present=$ingressEnabled"
    }
    else {
        Write-Host "  ingress enabled: $ingressEnabled"
    }

    # --- Active revisions --------------------------------------------------------------------
    $revisions = Invoke-AzJsonList -Description "az containerapp revision list for $appName" -Arguments @('containerapp', 'revision', 'list', '-n', $appName, '-g', $ResourceGroup)
    $activeRevisions = @($revisions | Where-Object { $_.properties.active -eq $true })

    # Exactly one active revision, healthy and running (review item 4): more than one active
    # revision means a stuck/blue-green rollout the operator must resolve before trusting any
    # other check below, and zero is the pre-existing failure this replaces.
    #
    # Round 3 review, item 1: enabling ingress (18.10 step 5) DOES create a new revision (the
    # HTTP scale rule lives in the revision template), so a transient two-active-revisions result
    # right after that step is EXPECTED, not a rollout problem -- the dark-verified revision keeps
    # serving (both revisions run AUTH_MODE=Entra) until the new one is ready. That guidance only
    # applies in full mode, once ingress may legitimately be enabled; -RevisionsOnly stays the
    # strict dark-provision gate (18.10 step 4a) with no such caveat, since two actives there means
    # the new dark revision genuinely is not ready yet.
    $activeNames = ($activeRevisions | ForEach-Object { $_.name }) -join ', '
    $activeDetail = "active=$($activeRevisions.Count) ($activeNames)"
    if (-not $RevisionsOnly -and $activeRevisions.Count -gt 1) {
        $activeDetail += '; if this immediately follows enabling ingress (18.10 step 5), this is transient and expected: enabling ingress creates a new revision, and both the old and new revisions run AUTH_MODE=Entra, so wait for the old one to retire and re-run Verify-ProductionAuth.ps1 rather than disabling ingress.'
    }
    Add-Result "${svcName}: exactly one active revision" ($activeRevisions.Count -eq 1) $activeDetail

    foreach ($rev in $activeRevisions) {
        $revName = $rev.name
        $template = $rev.properties.template
        $container = @($template.containers) | Select-Object -First 1
        $image = if ($container) { $container.image } else { $null }
        $imageOk = $image -and ($image -eq $wantImage)
        Add-Result "$svcName/$revName`: runs expected image" $imageOk "image=$image expected=$wantImage"

        $healthState = Get-Prop $rev.properties 'healthState' $null
        Add-Result "$svcName/$revName`: healthState=Healthy" ($healthState -eq 'Healthy') "healthState=$healthState"

        $runningState = Get-Prop $rev.properties 'runningState' $null
        Add-Result "$svcName/$revName`: runningState=Running" ($runningState -in @('Running', 'RunningAtMaxScale')) "runningState=$runningState"

        $envList = if ($container -and $container.env) { @($container.env) } else { @() }
        function Get-EnvVal([string]$name) {
            $e = $envList | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if ($e) { return $e.value }
            return $null
        }

        $authMode = Get-EnvVal 'AUTH_MODE'
        Add-Result "$svcName/$revName`: AUTH_MODE=Entra" ($authMode -eq 'Entra') "AUTH_MODE=$authMode"

        $prodVal = Get-EnvVal $svcSpec.ProductionFlag
        Add-Result "$svcName/$revName`: $($svcSpec.ProductionFlag)=$($svcSpec.ProductionValue)" ($prodVal -eq $svcSpec.ProductionValue) "$($svcSpec.ProductionFlag)=$prodVal"

        $tenantVal = Get-EnvVal 'ENTRA_TENANT_ID'
        Add-Result "$svcName/$revName`: ENTRA_TENANT_ID matches" ($tenantVal -eq $TenantId) "ENTRA_TENANT_ID=$(Format-Redacted $tenantVal) expected=$(Format-Redacted $TenantId)"

        $clientVal = Get-EnvVal 'ENTRA_CLIENT_ID'
        Add-Result "$svcName/$revName`: ENTRA_CLIENT_ID matches" ($clientVal -eq $ClientId) "ENTRA_CLIENT_ID=$(Format-Redacted $clientVal) expected=$(Format-Redacted $ClientId)"

        $scopeVal = Get-EnvVal 'ENTRA_API_SCOPE'
        $scopeOk = (-not $scopeVal) -or ($scopeVal -eq $ApiScopeName)
        Add-Result "$svcName/$revName`: ENTRA_API_SCOPE matches (or unset -> default)" $scopeOk "ENTRA_API_SCOPE=$scopeVal expected=$ApiScopeName"

        $roleVal = Get-EnvVal 'ENTRA_APP_ROLE'
        $roleOk = (-not $roleVal) -or ($roleVal -eq $AppRoleValue)
        Add-Result "$svcName/$revName`: ENTRA_APP_ROLE matches (or unset -> default)" $roleOk "ENTRA_APP_ROLE=$roleVal expected=$AppRoleValue"

        $instanceVal = Get-EnvVal 'ENTRA_INSTANCE'
        Add-Result "$svcName/$revName`: ENTRA_INSTANCE not set" ([string]::IsNullOrEmpty($instanceVal)) "ENTRA_INSTANCE=$instanceVal (must never be pinned in Azure)"
    }

    # --- EasyAuth off -------------------------------------------------------------------------
    $authShow = az containerapp auth show -n $appName -g $ResourceGroup --output json 2>&1
    # Round 3 review, item 6 (hardening): partition the merged stream by type before parsing, so
    # a stderr warning on an otherwise-successful call can never corrupt the JSON parse (native
    # stderr merged via 2>&1 arrives as ErrorRecord objects, not strings).
    $authStdout = @($authShow | Where-Object { $_ -is [string] })
    $authStderr = @($authShow | Where-Object { $_ -isnot [string] } | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        # Any error is an unknown state (review item 3/9): a prior version treated
        # "ResourceNotFound"/"could not be found" as an implicit pass, which is unsafe because the
        # same text can appear for auth transiently unreachable, not just "nothing configured".
        # Every failure to read EasyAuth state must fail closed.
        Add-Result "${svcName}: EasyAuth disabled (platform.enabled=false)" $false "az containerapp auth show failed (unknown state, treated as failure): $($authStderr -join ' | ')"
    }
    else {
        $authCfg = ($authStdout -join [Environment]::NewLine) | ConvertFrom-Json
        $platformEnabled = Get-Prop (Get-Prop $authCfg 'platform' @{}) 'enabled' $null
        $easyAuthOff = $platformEnabled -eq $false
        Add-Result "${svcName}: EasyAuth disabled (platform.enabled=false)" $easyAuthOff "platform.enabled=$platformEnabled"
    }

    $secrets = Invoke-AzJsonList -Description "az containerapp secret list for $appName" -Arguments @('containerapp', 'secret', 'list', '-n', $appName, '-g', $ResourceGroup)
    $leftoverSecret = $secrets | Where-Object { $_.name -eq 'aad-client-secret' }
    Add-Result "${svcName}: no leftover 'aad-client-secret'" (-not $leftoverSecret) $(if ($leftoverSecret) { 'secret still present' } else { 'not present' })

    # --- Anonymous HTTP probes (skipped under -RevisionsOnly) ----------------------------------
    if (-not $RevisionsOnly) {
        $fqdn = Get-Prop (Get-Prop $capp.properties.configuration 'ingress' @{}) 'fqdn' $null
        if (-not $fqdn) {
            Add-Result "${svcName}: HTTP probes (ingress has an fqdn)" $false 'ingress is disabled or has no fqdn -- run with -RevisionsOnly while dark'
        }
        else {
            $base = "https://$fqdn"
            Test-AnonymousProbes -BaseUrl $base -ServiceName $svcName -ProbePersona $probePersonaId
            if ($Authenticated) {
                Test-AuthenticatedProbe -BaseUrl $base -ServiceName $svcName -TenantId $TenantId -ClientId $ClientId -ApiScopeName $ApiScopeName
            }
        }
    }
    Write-Host ''
}

# --- Delegate registration checks to Verify-EntraAuth.ps1 -------------------------------------
# -RevisionsOnly runs only the env-pin/active-revision/EasyAuth az checks (18.9 spec, item 6): the
# registration delegation below makes Graph HTTP calls and must be skipped in that mode too.
if (-not $SkipRegistrationCheck -and -not $RevisionsOnly) {
    Write-Host '--- Registration (delegated to Verify-EntraAuth.ps1) ---' -ForegroundColor Cyan
    $verifyScript = Join-Path $PSScriptRoot 'Verify-EntraAuth.ps1'
    & $verifyScript -TenantId $TenantId -ClientId $ClientId -ApiScopeName $ApiScopeName -AppRoleValue $AppRoleValue
    Add-Result 'Registration checks (Verify-EntraAuth.ps1)' ($LASTEXITCODE -eq 0) "exit code $LASTEXITCODE"
}

# --- Report ------------------------------------------------------------------------------------
Write-Host "`n=== Verify-ProductionAuth summary ===" -ForegroundColor Cyan
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
