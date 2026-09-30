#!/bin/bash
# ADR-002 (issue #146): EasyAuth is removed from infra/main.bicep, but an environment provisioned
# before this change may still carry `az containerapp auth` config and/or a leftover
# `aad-client-secret` Container App secret from a prior EasyAuth-enabled provision. Neither is
# cleaned up by `azd provision` itself (Bicep only manages resources/params it still declares), so
# this hook disables auth and removes the secret on every container app in the environment, every
# time, before write_env runs. Idempotent: running it against an environment that never had
# EasyAuth (or already had it removed) is a no-op. Fails the postprovision hook on any real error
# (not "already disabled"/"already absent", which are the expected steady state).
set -euo pipefail

resource_group="$(azd env get-value AZURE_RESOURCE_GROUP 2>/dev/null || true)"
if [ -z "$resource_group" ]; then
  echo "postprovision_auth: AZURE_RESOURCE_GROUP is empty in the azd environment -- cannot enumerate container apps." >&2
  exit 1
fi

echo "postprovision_auth: disabling EasyAuth on every container app in resource group '$resource_group'..."

app_names="$(az containerapp list --resource-group "$resource_group" --query "[].name" -o tsv)"

if [ -z "$app_names" ]; then
  echo "postprovision_auth: no container apps found in '$resource_group' yet -- nothing to do."
  exit 0
fi

while IFS= read -r app_name; do
  [ -z "$app_name" ] && continue
  echo "postprovision_auth: [$app_name] disabling auth..."
  az containerapp auth update --name "$app_name" --resource-group "$resource_group" --enabled false --output none

  # Idempotent: check the secret list first so this also works against older az CLI versions
  # that error on removing a missing secret.
  secret_names="$(az containerapp secret list --name "$app_name" --resource-group "$resource_group" --query "[].name" -o tsv)"
  if printf '%s\n' "$secret_names" | grep -qx "aad-client-secret"; then
    echo "postprovision_auth: [$app_name] removing leftover 'aad-client-secret'..."
    az containerapp secret remove --name "$app_name" --resource-group "$resource_group" --secret-names "aad-client-secret" --output none
  fi
done <<< "$app_names"

echo "postprovision_auth: done."
