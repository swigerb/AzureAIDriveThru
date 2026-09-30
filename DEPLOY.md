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
docker build -t sonic-drive-in-app -f ./app/Dockerfile .
docker run -p 8000:8000 --env-file ./app/backend/.env sonic-drive-in-app:latest
```

## Deploy the Application

After testing locally, deploy the application with:

```bash
./scripts/deploy.sh \
    --env-file ./app/backend/.env \
    --dockerfile ./app/Dockerfile \
    --context . \
    sonic-drive-in-assistant
```

## New Environment: Personas, Search Indexes, and Model Deployments

A brand-new `azd` environment (its own resource group, Foundry/Azure OpenAI
account, and paid Search service; see `docs/persona-architecture.md` section
10) is configured with these `infra/main.bicep` parameters:

| Parameter | env var (in `main.parameters.json`) | Default | Notes |
|---|---|---|---|
| `personas` | `PERSONAS` | *(empty)* | Comma list; the app parses this to its own allow-list. Empty (the tracked default) means "every persona pack found under `PERSONAS_DIR`" (`app/backend/persona_loader.py`), so all brands are served without naming any of them in tracked infra. The container app omits the `PERSONAS` env var entirely when this is empty, so the loader's own default applies. Override with `azd env set PERSONAS=...` only to restrict an environment to a subset of packs. |
| `defaultPersona` | `DEFAULT_PERSONA` | *(empty)* | Persona selected when a request doesn't specify one. Empty (the tracked default) means the container app omits `DEFAULT_PERSONA` entirely, so `app/backend/persona_loader.py` picks its own default (its first-party pack if enabled, else the first enabled id alphabetically) -- same "don't name a brand in tracked infra" treatment as `personas` above. |
| `openAiModelDeployments` | *(not wired to an env var)* | `infra/model-deployments.json` (loaded via `loadJsonContent()`): `gpt-realtime-2.1` (GlobalStandard, capacity from `realtimeDeploymentCapacity`, `isDefaultRealtime: true`) and `text-embedding-3-large` (capacity from `embeddingDeploymentCapacity`) | Each entry is `{catalogId, deploymentName, modelName, modelVersion, format, skuName, capacity, isDefaultRealtime}` (`format` is the Foundry model-format id, defaults to `OpenAI` when omitted). `AZURE_AI_MODEL_DEPLOYMENTS` output/env exposes the resulting catalogId to deploymentName map (section 7.2) for the model catalog in `app/backend/config.yaml`. Adding a #82 model is one new entry in `infra/model-deployments.json`, no Bicep edits. |
| `realtimeDeploymentCapacity` | `AZURE_OPENAI_REALTIME_DEPLOYMENT_CAPACITY` | `10` | Scale-only override for the `gpt-realtime-2.1` entry above (section 10.3): bump the param, then `azd provision`. |
| `searchServiceSkuName` | `AZURE_SEARCH_SERVICE_SKU` | `basic` | Paid tier for a clean-clone `azd up` (design section 10.2): Basic removes the free tier's 3-index cap at roughly a third of Standard's cost. |
| `searchServiceLocation` | `AZURE_SEARCH_SERVICE_LOCATION` | *(empty -- falls back to `location`)* | Independent region override for the Search module only (same pattern as `openAiServiceLocation`/`AZURE_OPENAI_SERVICE_LOCATION`). Set this when the main `location` has no Basic-SKU Search capacity: `azureaidrivethru-prod` (#87) uses `eastus` here while everything else stays in `eastus2` (design section 10.2). |
| `deployDotnetApp` | `DEPLOY_DOTNET_APP` | `false` | Deploys the `acaBackendDotnet` Container App module (same Foundry account, Search service, and managed identity as the Python app, no extra RBAC needed). Stays `false` until `app/backend-dotnet` exists (#17); `azure.yaml` has no `backend-dotnet` service yet, so `azd` never targets it while disabled. |
| `dotnetServiceName` | `AZURE_CONTAINER_APP_DOTNET_NAME` | *(auto-generated)* | Only used when `deployDotnetApp` is `true`. |

Search index names are not tracked in infra at all: each persona pack's own
`persona.json` `search.indexName` (design section 4.2) is the one source of
truth, read by the #84 ingestion hook and by the app itself. An infra-level
index-name map would be a second source of truth for the same data.

All resources use only user-assigned managed identity for authentication
(`AZURE_CLIENT_ID` env var); no Azure OpenAI or Search keys are issued or
stored anywhere.

To validate infra changes against a **new** environment without deploying,
run a preview from a clean environment (no local overrides), so the tracked
defaults above (not a previous local override) are what gets checked:

```bash
azd env new <new-env-name>
azd env set AZURE_SUBSCRIPTION_ID <subscription-guid>
azd env set AZURE_LOCATION eastus2
azd env set AZURE_RESOURCE_GROUP rg-<new-env-name>
azd env set AZURE_OPENAI_SERVICE_LOCATION eastus2
azd provision --preview
```

`AZURE_OPENAI_SERVICE_LOCATION` has no tracked default (`openAiServiceLocation`
in `main.parameters.json` is `${AZURE_OPENAI_SERVICE_LOCATION}` with no
`=default`) -- it must be set explicitly or `azd provision --preview` fails to
resolve the substitution. `eastus2` matches the realtime/embedding model
availability the rest of this doc assumes; only change it if you know the
target region also has the required model capacity.

`azd provision --preview` is a read-only what-if: it resolves the
`${VAR=default}` substitutions in `main.parameters.json` the same way a real
`azd provision` would (a raw `az deployment sub what-if` against
`main.parameters.json` does not, since those substitutions only resolve
under `azd`), but it never applies anything. Never run this against an
existing environment's `.azure/<env>` folder, and never run `azd
provision`/`azd up`/`azd down` (without `--preview`) as part of validating a
skeleton/infra-only change.

### Production Environment: `azureaidrivethru-prod`

The tracked default for `DEFAULT_PERSONA` is empty (section above), which is
correct for a demo/dev environment serving every enabled persona. A production
environment sets its default persona explicitly, so the choice is recorded in
that environment's `.azure/<env>` folder rather than in tracked infra:

```bash
azd env select azureaidrivethru-prod
azd env set DEFAULT_PERSONA <persona-id>
azd provision
```

The production value is recorded in the deploy checklist on issue #87.

This only changes which persona a session gets when it doesn't name one
(`app/backend/persona_loader.py`'s fallback); it does not restrict `PERSONAS`
-- every enabled persona pack still gets its own index and is still
reachable by a session that requests it explicitly.

## Entra ID Authentication (ADR-002)

The app requires Microsoft Entra ID sign-in on every request, including the `/realtime` WebSocket, which
consumes metered Azure OpenAI realtime tokens on your subscription. There is no unauthenticated mode in Azure;
`AUTH_MODE=Entra` is pinned by `infra/main.bicep`. See [ADR-002](docs/adr/ADR-002-entra-authentication.md) and
[docs/persona-architecture.md](docs/persona-architecture.md) section 18 for the full design. EasyAuth (the old
`az containerapp auth` / `authConfigs` approach) is removed; the app validates bearer tokens itself.

### Setup

`scripts/Setup-EntraAuth.ps1` creates or reconciles the single Entra app registration (single tenant, `api://{clientId}`,
v2 tokens, the `access_as_user` scope, the `DriveThru.User` app role, SPA-only redirect URIs, the Azure CLI client
pre-authorized) and assigns the caller (or `-AssignUserUpn`) the app role. It is **preview by default**: it writes
nothing to Entra unless you pass `-Apply`, and it never adopts an existing app by display name. Reconciling an
existing registration (every run after the first) requires `-ClientId`/`-AppObjectId` plus caller ownership and the
`AzureAIDriveThruManaged` tag. `-RedirectUri` defaults to the two local-dev origins from design 18.1
(`http://localhost:8000`, `http://localhost:5173`), so those two count as "supplied" even when you pass nothing:
the redirect-URI reconcile is a full SET, so a run that omits `-FrontendOrigin`/`-RedirectUri`/`-FromAzdEnv`
registers the localhost defaults only, replacing any already-registered frontend origin. Only a run whose
combined total is empty (`-RedirectUri @()` explicitly, with no `-FrontendOrigin`/`-FromAzdEnv`) leaves the
existing SPA URIs untouched.

```powershell
az login --tenant <tenant-id>
```

**(a) Today's staging env (ingress off, `BACKEND_URI` blank).** `-FromAzdEnv` fails hard on an empty `BACKEND_URI`
(see below), so derive the Python container app's stable FQDN read-only instead: it is `<app-name>.<environment
defaultDomain>`, which does not change when ingress is toggled.

```powershell
azd env select <env-name>
az account set --subscription (azd env get-value AZURE_SUBSCRIPTION_ID)

$rg = azd env get-value AZURE_RESOURCE_GROUP
$app = (az containerapp list -g $rg -o json | ConvertFrom-Json | Where-Object { $_.tags.'azd-service-name' -eq 'backend' } | Select-Object -First 1).name
$envId = az containerapp show -n $app -g $rg --query properties.managedEnvironmentId -o tsv
$domain = az containerapp env show --ids $envId --query properties.defaultDomain -o tsv
if (-not $app -or -not $domain) { throw 'backend container app or environment domain not found' }

# Preview: prints what would change, writes nothing
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -FrontendOrigin "https://$app.$domain"

# Apply: creates/reconciles the registration and assigns the caller DriveThru.User
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -FrontendOrigin "https://$app.$domain" -Apply
```

**(b) A fresh env, before the first deployment.** Only the `-RedirectUri` localhost defaults are available yet:

```powershell
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -Apply
azd env set ENTRA_TENANT_ID "<tenant-id>"
azd env set ENTRA_CLIENT_ID "<client-id>"
azd up
```

**(c) Reconcile, once `BACKEND_URI` is populated (after the public provision, step 5 below).**

```powershell
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <client-id> -FromAzdEnv
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <client-id> -FromAzdEnv -Apply
```

`-FromAzdEnv` reads `BACKEND_URI` (and `BACKEND_DOTNET_URI` once the dotnet app is live) from the azd environment to
derive the SPA redirect URIs; it fails hard on an empty `BACKEND_URI` rather than silently skipping a redirect URI
(this is expected while ingress is dark, case (a) above). Alternatively, pass `-FrontendOrigin`/`-RedirectUri`
explicitly.

The script prints only ids and two `azd env set` lines, never a secret or a token:

```powershell
azd env set ENTRA_TENANT_ID "<tenant-id>"
azd env set ENTRA_CLIENT_ID "<client-id>"
```

Run those, then provision/deploy per the rollout order below.

### Verify

`scripts/Verify-EntraAuth.ps1` is a read-only check of the app registration itself (single tenant, `api://{clientId}`,
v2 tokens, the scope and role enabled, SPA-only redirect URIs, `appRoleAssignmentRequired`). Non-zero exit on any gap:

```powershell
./scripts/Verify-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <client-id>
```

`scripts/Verify-ProductionAuth.ps1` is a read-only live posture check for every deployed container app: the expected
image on every active revision, the `AUTH_MODE=Entra`/production-flag/`ENTRA_*` env pins, no `ENTRA_INSTANCE`, EasyAuth
observed disabled with no leftover `aad-client-secret` secret, and (unless `-RevisionsOnly`) anonymous HTTP probes
against the live app. It delegates the registration check to `Verify-EntraAuth.ps1`.

```powershell
# Dark check (ingress still off): az-only, no HTTP, fails if ingress is enabled
./scripts/Verify-ProductionAuth.ps1 -RevisionsOnly

# Full check once ingress is public
./scripts/Verify-ProductionAuth.ps1

# Also confirm an authenticated request succeeds (never prints the token)
./scripts/Verify-ProductionAuth.ps1 -Authenticated
```

### Troubleshooting

**Setup fails at "API configuration" (a `preAuthorizedApplications` or permission-id error) on a brand-new app, or
any `-Apply` failure after `Created application appId=...`.** Setup adds the delegated scope and reconciles
`preAuthorizedApplications` in two separate Graph requests specifically so a pre-authorized client entry is never
sent referencing a scope id Graph hasn't committed yet, but if a run still fails partway through (for example, a
partial write from an interrupted prior run left the app in an unexpected state), the fallback is idempotent. Run it
in the same session as case (a) above so `$app` and `$domain` are still set:

```powershell
# 1. Re-run the same Apply line with -ClientId (idempotent): only the steps that did not finish run again.
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <appId it printed> -FrontendOrigin "https://$app.$domain" -Apply

# 2. If it still fails at "API configuration", reconcile in two passes:
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <appId> -FrontendOrigin "https://$app.$domain" -PreAuthorizedClientAppId @() -Apply
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <appId> -FrontendOrigin "https://$app.$domain" -Apply
```

Always include `-FrontendOrigin` on every line above: `-Apply` reconciles the whole SPA redirect-URI set, so a
fallback re-run without it replaces the live URL with only the two localhost defaults, and sign-in on the live URL
then fails at step 6 with AADSTS50011 (redirect URI mismatch).

### Rollout (ingress last)

`azd provision` re-enables external ingress and sets min replicas to 1, and the apps run in `Single` revision mode:
ACA keeps the previous (unauthenticated) revision active until the new one is ready, so a naive `azd provision` can
leave an unauthenticated revision reachable if the new one fails to boot. **So enabling ingress is the last, separate
step.** `backendIngressEnabled` (from the azd env `BACKEND_INGRESS_ENABLED`, default `true`) makes this dark rollout
possible without touching every other environment's default behavior.

1. The infra, scripts and backend/frontend Entra changes merge to `dev`, including #144 (Python Entra auth) and
   #145 (CI build-arg wiring), which this rollout depends on.
2. Brian runs Setup (above), then the two `azd env set` lines it prints.
3. `azd deploy` while ingress is still disabled. The new image in Production without `AUTH_MODE` fails fast (a clean
   gunicorn exit). **That is expected:** `azd deploy` may report the revision as failed or unhealthy. Don't "fix"
   it by provisioning with ingress on.
4. **Dark provision:** `azd env set BACKEND_INGRESS_ENABLED false`, then `azd provision`. This pins `AUTH_MODE=Entra`
   and the ids with ingress still off; postprovision disables EasyAuth on every app.
   - **4a. Check while dark:** `./scripts/Verify-ProductionAuth.ps1 -RevisionsOnly` must show exactly one active
     revision, on the new image, with `AUTH_MODE=Entra`, healthy and running, before proceeding. If an old revision
     is still active, the new one isn't ready: read its logs and fix it while dark. Don't touch the revision mode.
5. **Public provision:** `azd env set BACKEND_INGRESS_ENABLED true`, then `azd provision`. Enabling ingress DOES
   create a new revision: `infra/core/host/container-app.bicep` puts the HTTP scale rule in the revision template
   (`scale.rules` depends on `ingressEnabled`), so this is a new revision on the same (dark-verified) image and env.
   Run `./scripts/Verify-ProductionAuth.ps1` (and `-Authenticated`) immediately after.
   - **If it reports two active revisions right after this step:** that is expected and transient, not a failure to
     "fix." The dark-verified revision from step 4a keeps serving (both revisions run `AUTH_MODE=Entra`, so there is
     no unauthenticated window) until the new one is ready. Wait a short while for the old revision to retire, then
     re-run `./scripts/Verify-ProductionAuth.ps1`. Do **not** run `az containerapp ingress disable` or flip
     `BACKEND_INGRESS_ENABLED` back to `false` just because two revisions are briefly active.
   - On any OTHER failure (wrong image, `AUTH_MODE` missing, EasyAuth re-enabled, a probe 401/failing where it should
     pass), run `az containerapp ingress disable` (or set the variable back to `false` and provision), then fix it.
6. Brian signs in on the live URL, and posts the result on #85.

**The rule:** never run `azd provision` or `azd up` against `azureaidrivethru-prod` with `BACKEND_INGRESS_ENABLED`
true (or unset) until step 4a has passed with ingress off. This also covers the manual `Deploy to Azure with azd`
workflow (`.github/workflows/azure-dev.yaml`, `workflow_dispatch` only): it runs `azd provision` without setting
`BACKEND_INGRESS_ENABLED` or the `ENTRA_*` values, so dispatching it against `azureaidrivethru-prod` before 4a has
passed would publish the old, unauthenticated image with ingress on.

