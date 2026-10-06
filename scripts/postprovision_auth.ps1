# ADR-002 (issue #146): EasyAuth is removed from infra/main.bicep, but an environment provisioned
# before this change may still carry `az containerapp auth` config and/or a leftover
# `aad-client-secret` Container App secret from a prior EasyAuth-enabled provision. Neither is
# cleaned up by `azd provision` itself (Bicep only manages resources/params it still declares), so
# this hook disables auth and removes the secret on every container app in the environment, every
# time, before write_env runs. Idempotent: running it against an environment that never had
# EasyAuth (or already had it removed) is a no-op. Fails the postprovision hook on any real error
# (not "already disabled"/"already absent", which are the expected steady state).
$ErrorActionPreference = "Stop"

function Get-AzdEnvValue {
    param([Parameter(Mandatory)] [string] $Name)
    $value = azd env get-value $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return "" }
    return $value
}

$resourceGroup = Get-AzdEnvValue "AZURE_RESOURCE_GROUP"
if ([string]::IsNullOrWhiteSpace($resourceGroup)) {
    throw "postprovision_auth: AZURE_RESOURCE_GROUP is empty in the azd environment -- cannot enumerate container apps."
}

Write-Host "postprovision_auth: disabling EasyAuth on every container app in resource group '$resourceGroup'..."

$appNamesJson = az containerapp list --resource-group $resourceGroup --query "[].name" -o json
if ($LASTEXITCODE -ne 0) {
    throw "postprovision_auth: 'az containerapp list' failed with exit code $LASTEXITCODE."
}
$appNames = @($appNamesJson | ConvertFrom-Json)

if ($appNames.Count -eq 0) {
    Write-Host "postprovision_auth: no container apps found in '$resourceGroup' yet -- nothing to do."
    exit 0
}

foreach ($appName in $appNames) {
    Write-Host "postprovision_auth: [$appName] disabling auth..."
    az containerapp auth update --name $appName --resource-group $resourceGroup --enabled false --output none
    if ($LASTEXITCODE -ne 0) {
        throw "postprovision_auth: 'az containerapp auth update' failed for '$appName' with exit code $LASTEXITCODE."
    }

    # Idempotent: `az containerapp secret remove` on a secret that doesn't exist is a no-op
    # success on current az CLI versions, but check the secret list first so this script also
    # works against older CLI versions that error on removing a missing secret.
    $secretNamesJson = az containerapp secret list --name $appName --resource-group $resourceGroup --query "[].name" -o json
    if ($LASTEXITCODE -ne 0) {
        throw "postprovision_auth: 'az containerapp secret list' failed for '$appName' with exit code $LASTEXITCODE."
    }
    $secretNames = @($secretNamesJson | ConvertFrom-Json)
    if ($secretNames -contains "aad-client-secret") {
        Write-Host "postprovision_auth: [$appName] removing leftover 'aad-client-secret'..."
        az containerapp secret remove --name $appName --resource-group $resourceGroup --secret-names "aad-client-secret" --output none
        if ($LASTEXITCODE -ne 0) {
            throw "postprovision_auth: 'az containerapp secret remove' failed for '$appName' with exit code $LASTEXITCODE."
        }
    }
}

Write-Host "postprovision_auth: done ($($appNames.Count) container app(s) checked)."
