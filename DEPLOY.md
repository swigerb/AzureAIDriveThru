# Deployment Instructions

## What The Deployment Script Does

1. Loads environment variables from the specified .env file
2. Creates or updates Azure resources:
   - Resource Group
   - Azure Container Registry
   - App Service Plan
   - Web App
   - Application Insights
   - Log Analytics Workspace
3. Builds the Docker image locally using the specified Dockerfile and context
4. Pushes the image to Azure Container Registry
5. Configures the Web App with all environment variables

## Build and Test Locally with Docker

From the root directory, execute the following commands:

```bash
docker build -t sonic-drive-in-app -f ./app/Dockerfile ./app
docker run -p 8000:8000 --env-file ./app/backend/.env sonic-drive-in-app:latest
```

## Deploy the Application

After testing locally, deploy the application with:

```bash
./scripts/deploy.sh \
    --env-file ./app/backend/.env \
    --dockerfile ./app/Dockerfile \
    --context ./app \
    sonic-drive-in-assistant
```

## New Environment: Personas, Search Indexes, and Model Deployments

A brand-new `azd` environment (its own resource group, Foundry/Azure OpenAI
account, and paid Search service — see `docs/persona-architecture.md` section
10) is configured with these `infra/main.bicep` parameters:

| Parameter | env var (in `main.parameters.json`) | Default | Notes |
|---|---|---|---|
| `personas` | `PERSONAS` | `sonic,mcdonalds` | Comma list; the app parses this to its own allow-list. The tracked default only lists the persona ids that are safe in committed source ahead of #76 (see below); override with `azd env set PERSONAS=...` for a real environment's full roster (`docs/persona-architecture.md` section 10). |
| `defaultPersona` | `DEFAULT_PERSONA` | `sonic` | Persona selected when a request doesn't specify one. |
| `personaSearchIndexNamesJson` | *(not wired to an env var yet)* | `{"sonic":"sonic-menu-items","mcdonalds":"mcdonalds-menu-items"}` | One Search index per persona on this environment's own (Basic SKU) Search service. Edit the Bicep default or pass `--parameters` to change it; wiring to an env var lands with #75/#84 once the ingestion pipeline needs to override it per environment. |
| `openAiModelDeploymentsJson` | *(not wired to an env var yet)* | Two entries: `gpt-realtime-2.1` (GlobalStandard, capacity from `realtimeDeploymentCapacity`, `isDefaultRealtime: true`) and `text-embedding-3-large` (capacity from `embeddingDeploymentCapacity`) | Parameterized list of `{catalogId, deploymentName, modelName, modelVersion, skuName, capacity, isDefaultRealtime}`; `AZURE_AI_MODEL_DEPLOYMENTS` output/env exposes the resulting catalogId → deploymentName map (section 7.2) for the model catalog in `app/backend/config.yaml`. Wiring a per-environment override env var lands with #75/#82 once the real model catalog is finalized. |
| `deployDotnetApp` | `DEPLOY_DOTNET_APP` | `false` | Deploys the `acaBackendDotnet` Container App module (same Foundry account, Search service, and managed identity as the Python app — no extra RBAC needed). Stays `false` until `app/backend-dotnet` exists (#17); `azure.yaml` has no `backend-dotnet` service yet, so `azd` never targets it while disabled. |
| `dotnetServiceName` | `AZURE_CONTAINER_APP_DOTNET_NAME` | *(auto-generated)* | Only used when `deployDotnetApp` is `true`. |

The `personas`/`personaSearchIndexNamesJson` defaults tracked in git only name
the persona ids that don't trip `test_rebrand_verification.py`'s pre-persona-pack
brand guard (`app/backend/tests/test_rebrand_verification.py`), which #76
inverts once brand words are scoped to their own `personas/<id>/` pack. A real
environment's full persona roster (all three brands in
`docs/persona-architecture.md` section 10) is supplied at provision time via
`azd env set PERSONAS=...` / `azd env set personaSearchIndexNamesJson ...` —
those values live only in the environment's local, gitignored `.azure/<env>`
folder, never in tracked source.

All resources use only user-assigned managed identity for authentication
(`AZURE_CLIENT_ID` env var) — no Azure OpenAI or Search keys are issued or
stored anywhere.

To validate infra changes against a **new** environment without deploying:

```bash
azd env new <new-env-name>
azd env set AZURE_SUBSCRIPTION_ID <subscription-guid>
azd env set AZURE_LOCATION eastus2
azd env set AZURE_RESOURCE_GROUP rg-<new-env-name>
az deployment sub what-if \
  --location eastus2 \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json
```

Never run this against an existing environment's `.azure/<env>` folder, and
never run `azd provision`/`azd up`/`azd down` as part of validating a
skeleton/infra-only change.

## Enable Entra ID Authentication (EasyAuth)

Authentication is **opt-in** — a plain `azd up` deploys without auth. To protect
the app with Entra ID (single-tenant), follow these one-time steps.

### 1. Create the App Registration

```bash
# Set your tenant ID (the Azure AD tenant that owns the app)
TENANT_ID="<your-tenant-id>"

# Create the app registration (single tenant)
az ad app create \
  --display-name "Sonic AI Drive-Thru Demo" \
  --sign-in-audience AzureADMyOrg \
  --enable-id-token-issuance true \
  --web-redirect-uris "https://<YOUR-CONTAINER-APP-FQDN>/.auth/login/aad/callback" \
  --query appId -o tsv
```

Save the returned `appId` (e.g., `<your-app-id>`).

> **Note:** Replace `<YOUR-CONTAINER-APP-FQDN>` with the actual FQDN from the
> `BACKEND_URI` output of your deployment (minus the `https://` prefix).

### 2. Create a Service Principal and Restrict Access

```bash
APP_ID="<your-app-id>"

# Create the service principal
az ad sp create --id $APP_ID

# OPTIONAL — restrict sign-in to explicitly assigned users/groups only.
# Read the warning below before enabling this.
az ad sp update --id $APP_ID --set appRoleAssignmentRequired=true
```

> ⚠️ **`appRoleAssignmentRequired=true` requires an administrator to grant
> consent, and will lock you out if you are not one.**
>
> When an enterprise application requires assignment, Entra ID disables
> *user* self-consent for that app. The first sign-in then fails with
> "Need admin approval — <app> needs permission to access resources in your
> organization that only an admin can grant", even for a user who has been
> assigned. Someone holding Application Administrator, Cloud Application
> Administrator or Global Administrator must run
> `az ad app permission admin-consent --id $APP_ID` first. Global *Reader*
> is not sufficient — it is read-only.
>
> If you do not have an admin account to hand, leave this setting off. The
> app is registered single-tenant (`AzureADMyOrg`), so sign-in is still
> limited to members of your own tenant; you simply cannot narrow it
> further to named individuals.

> With `appRoleAssignmentRequired=true`, only users explicitly assigned to
> this enterprise application can sign in. Without it, *any* member of the
> tenant can authenticate. Assign users in the Azure Portal under
> Enterprise Applications → Sonic AI Drive-Thru Demo → Users and groups,
> or via CLI:
>
> ```bash
> # Get the service principal object ID
> SP_OBJECT_ID=$(az ad sp show --id $APP_ID --query id -o tsv)
>
> # Get the user's object ID
> USER_OBJECT_ID=$(az ad user show --id "<user-principal-name>" --query id -o tsv)
>
> # Assign the user (default app role — empty GUID)
> az rest --method POST \
>   --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignments" \
>   --body "{\"principalId\": \"$USER_OBJECT_ID\", \"resourceId\": \"$SP_OBJECT_ID\", \"appRoleId\": \"00000000-0000-0000-0000-000000000000\"}"
> ```

### 3. Create a Client Secret

```bash
az ad app credential reset --id $APP_ID --display-name "azd-easyauth" --query password -o tsv
```

Save the secret value — it is shown only once.

### 4. Configure the azd Environment

```bash
azd env set AZURE_AUTH_ENABLED true
azd env set AZURE_AUTH_CLIENT_ID "<your-app-id>"
azd env set AZURE_AUTH_TENANT_ID "<your-tenant-id>"
azd env set AZURE_AUTH_CLIENT_SECRET "<secret-value-from-step-3>"
```

Then deploy normally:

```bash
azd up
```

The Bicep template will:
- Store `AZURE_AUTH_CLIENT_SECRET` as a Container App secret named `aad-client-secret`
- Deploy an `authConfigs/current` child resource with EasyAuth enabled
- Redirect unauthenticated requests to Entra ID login
- Protect all endpoints including WebSocket routes (`/realtime`)

### 5. Verify

```bash
# Anonymous GET should 302 redirect to Entra login
curl -s -o /dev/null -w "%{http_code}" https://<YOUR-CONTAINER-APP-FQDN>/

# Anonymous WebSocket handshake should return 401
curl -s -o /dev/null -w "%{http_code}" \
  -H "Upgrade: websocket" -H "Connection: Upgrade" \
  https://<YOUR-CONTAINER-APP-FQDN>/realtime
```

### Secret Management

The client secret **never** appears in source control or parameter files.
It flows through `azd env` (stored locally in `.azure/<env>/.env`, which is
gitignored) and is passed as a `@secure()` Bicep parameter at deployment time.

If you prefer to manage the secret entirely out-of-band (without putting it in
`azd env`), you can set it directly on the Container App after deployment:

```bash
az containerapp secret set \
  --name <container-app-name> \
  --resource-group <rg-name> \
  --secrets aad-client-secret=<secret-value>
```

In that case, leave `AZURE_AUTH_CLIENT_SECRET` empty — the Bicep template will
still deploy the auth config referencing the secret by name.
