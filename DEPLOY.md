# Deployment Instructions

## What azd deploys

AZD provisions or updates the Microsoft Foundry AI Drive Thru environment:

1. Resource group, Azure Container Registry, Log Analytics, Storage, and a user-assigned managed identity.
2. Microsoft Foundry `AIServices` with the model deployments in `infra/model-deployments.json`, or the configured existing account.
3. Azure AI Search with one index per persona.
4. Azure Container Apps with the Python backend and an optional C# backend.
5. Entra ID in-app auth settings, managed-identity RBAC, Search ingestion, and a non-fatal realtime smoke check through AZD hooks.

## Build and test locally with Docker

From the root directory, execute the following commands:

```bash
docker build -t azure-ai-drive-thru -f ./app/Dockerfile --build-arg VITE_AUTH_MODE=Development .
docker run -p 8000:8000 --env-file ./app/backend/.env azure-ai-drive-thru:latest
```

`VITE_AUTH_MODE=Development` skips the Entra sign-in build check for a local smoke test. Without it the image defaults to Entra and the frontend build fails unless real `VITE_ENTRA_TENANT_ID` and `VITE_ENTRA_CLIENT_ID` values are passed.

## Deploy the application

The deployed app always uses Entra ID auth, so a fresh environment needs the app registration before the first image build. See [Setup](#setup), case (b), for details.

```powershell
azd auth login
azd env new <env-name>
azd env set AZURE_LOCATION eastus2
azd env set AZURE_OPENAI_SERVICE_LOCATION eastus2

# Create (or reconcile) the Entra app registration, then pin its ids
./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -Apply
azd env set ENTRA_TENANT_ID "<tenant-id>"
azd env set ENTRA_CLIENT_ID "<client-id>"

```

Follow the ingress-last rollout below. `DEPLOY_DOTNET_APP` defaults to `false`; while C# is
disabled, deploy the Python service explicitly with `azd deploy backend`. Do not run bare
`azd up` or `azd deploy` until the C# service is enabled and provisioned.

## New environment: personas, Search indexes, and model deployments

A brand-new `azd` environment, with its own resource group, Foundry AIServices account, and paid Search service, is configured with these `infra/main.bicep` parameters:

The tracked default leaves `DEFAULT_PERSONA` empty. When no query string is supplied, `app/backend/persona_loader.py` chooses `sonic` when that persona is enabled, else the first enabled id alphabetically. Use `?persona=<id>` for an explicit deep link.

| Parameter | env var (in `main.parameters.json`) | Default | Notes |
|---|---|---|---|
| `personas` | `PERSONAS` | *(empty)* | Comma list; the app parses this to its own allow-list. Empty (the tracked default) means "every persona found under `PERSONAS_DIR`" (`app/backend/persona_loader.py`), so all personas are served without naming any of them in tracked infra. The container app omits the `PERSONAS` env var entirely when this is empty, so the loader's own default applies. Override with `azd env set PERSONAS=...` only to restrict an environment to a subset of personas. |
| `defaultPersona` | `DEFAULT_PERSONA` | *(empty)* | Persona selected when a request doesn't specify one. Empty (the tracked default) means the container app omits `DEFAULT_PERSONA` entirely, so `app/backend/persona_loader.py` picks its own default (its sonic persona if enabled, else the first enabled id alphabetically) -- same "don't name a brand in tracked infra" treatment as `personas` above. |
| `openAiModelDeployments` | *(not wired to an env var)* | `infra/model-deployments.json` (loaded via `loadJsonContent()`): `gpt-realtime-2.1`, `gpt-realtime-2.1-mini`, `text-embedding-3-large`, `gpt-5-mini`, `phi-4`, `gpt-4o-transcribe`, and `gpt-4o-mini-tts` | Each entry is `{catalogId, deploymentName, modelName, modelVersion, format, skuName, capacity, isDefaultRealtime}` (`format` is the Foundry model-format id, defaults to `OpenAI` when omitted). `AZURE_AI_MODEL_DEPLOYMENTS` exposes the catalogId-to-deploymentName map used by `app/backend/config.yaml`. Add deployments in `infra/model-deployments.json`; no Bicep edit is needed. `gpt-realtime-2.1-mini` is the default realtime model. |
| `realtimeDeploymentCapacity` | `AZURE_OPENAI_REALTIME_DEPLOYMENT_CAPACITY` | `10` | Scale-only override for the `gpt-realtime-2.1` entry above (section 10.3): bump the param, then `azd provision`. |
| `realtime21MiniDeploymentCapacity` | `AZURE_OPENAI_REALTIME_21_MINI_DEPLOYMENT_CAPACITY` | `10` | Scale-only override for the default `gpt-realtime-2.1-mini` model; change the parameter, then run `azd provision`. |
| `searchServiceSkuName` | `AZURE_SEARCH_SERVICE_SKU` | `basic` | Paid tier for a clean-clone `azd up` (design section 10.2): Basic removes the free tier's 3-index cap at roughly a third of Standard's cost. |
| `searchServiceLocation` | `AZURE_SEARCH_SERVICE_LOCATION` | *(empty -- falls back to `location`)* | Independent region override for the Search module only (same pattern as `openAiServiceLocation`/`AZURE_OPENAI_SERVICE_LOCATION`). Set this when the main `location` has no Basic-SKU Search capacity. |
| `containerAppMaxReplicas` | `AZURE_CONTAINER_APP_MAX_REPLICAS` | `5` | Maximum replicas for each backend Container App. Set to `1` for a cost-bounded test environment; this caps app scaling but is not a total-spend limit. |
| `deployDotnetApp` | `DEPLOY_DOTNET_APP` | `false` | Deploys the optional C# Container App. Enabling it adds a second always-on app; review cost and rollout for the target environment before setting it to `true`. See [.NET container app](#net-container-app). |
| `dotnetServiceName` | `AZURE_CONTAINER_APP_DOTNET_NAME` | *(auto-generated: `capps-dotnet-<resourceToken>`)* | Used only when `deployDotnetApp` is `true`; override it only when the generated name does not fit the target environment. |
| `backendDotnetIngressEnabled` | `BACKEND_DOTNET_INGRESS_ENABLED` | `false` | Enables public ingress for the C# app. Keep it disabled until the dark revision passes auth checks. |

Search index names are not tracked in infra at all: each persona's own
`persona.json` `search.indexName` (design section 4.2) is the one source of
truth, read by the ingestion hook and by the app itself. An infra-level
index-name map would be a second source of truth for the same data.

### `BACKEND_URI` and `BACKEND_DOTNET_URI` container environment variables

Both container apps' own env blocks (not just the azd output/`.env` file -- see
"Entra redirect URIs" below) now carry `BACKEND_URI` and, once the dotnet app is deployed with
ingress on, `BACKEND_DOTNET_URI`: each is the running app's (or sibling app's) own public HTTPS
origin, so either backend can address the other (e.g. a future cross-backend health check or
redirect) without a hardcoded hostname. Each is computed deterministically as
`https://<app-name>.<ACA environment defaultDomain>` -- from the app's own name and the shared ACA
environment's `defaultDomain` output, not from the *other* app's module output -- so neither
container app module depends on the other's deployment finishing first. Each var is included only
when the app it names actually has public ingress: `BACKEND_URI` requires `backendIngressEnabled`;
`BACKEND_DOTNET_URI` requires both `deployDotnetApp` and `backendDotnetIngressEnabled`. An app with
ingress off is omitted entirely rather than injecting a URL nothing can reach.

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

This only changes which persona a session gets when it doesn't name one
(`app/backend/persona_loader.py`'s fallback); it does not restrict `PERSONAS`
-- every enabled persona still gets its own index and is still
reachable by a session that requests it explicitly.

## Realtime and cascade smoke checks

The azd `postdeploy` hook (`scripts/smoke_realtime.sh` / `.ps1`) runs
`scripts/smoke_realtime.py` once per persona (discovered live via
`PersonaCatalog.load().ids` -- never a hardcoded brand list) for each of the
two realtime pipelines the app supports, so a bad deployment doesn't go
unnoticed just because the default persona happened to work:

- `--pipeline realtime` (default): opens a GA Realtime API session against
  the shared Azure OpenAI realtime deployment, asserts the `session.update`
  shape and a transcription event, and then runs a **one-tool-call check**:
  it sends a scripted guest turn designed to trigger the `search` function
  tool, asserts the resulting `function_call` has a well-formed name/arguments
  JSON/call_id, and then actually runs that query against the persona's own
  Azure AI Search index through the same code path the backend uses
  (`app/backend/tools.py`'s search context), so the check fails if the
  persona's index is empty or misconfigured, not just if the realtime
  deployment is reachable. Pass `--skip-search` to skip the Search-side half
  (session/tool-call shape is still asserted) when running against an
  environment with no search credential available.
- `--pipeline cascade`: exercises the cascade (non-realtime) pipeline end to
  end -- `gpt-4o-transcribe` for speech-to-text, the persona's configured
  cascade chat model for the order-taking turn, and `gpt-4o-mini-tts` for the
  spoken response -- using the same `ModelCatalog`/`PersonaCatalog`
  configuration and Entra credential (`AsyncDefaultAzureCredential`, never an
  API key) the backend itself uses.

Both pipelines, for every persona, run serially in the wrapper script. The
wrapper is always non-fatal: every failure is reported (`report_result` /
`Report-Result`) but the script still exits `0`, so a smoke failure never
blocks `azd up`/`azd deploy` from completing -- it's a signal to check
afterward, not a deployment gate. See `app/backend/tests/test_smoke_realtime.py`
for the fake-server-only unit coverage of both pipelines (no live Azure calls
are made in tests).

For an isolated deployment where the C# app itself is the realtime test client,
set `SONIC_SKIP_REALTIME_SMOKE=true` to avoid separate client-level AI smoke
calls. `scripts/setup_search_index.py` loads only its allowlisted, non-secret
runtime settings with `azd env get-value`; it does not read an AZD `.env` file.
Set `AZURE_SKIP_LOCAL_ENV_WRITE=true` to keep the postprovision hook from
overwriting `app/backend/.env`.

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
the redirect-URI reconcile is a full SET: a run that omits `-FrontendOrigin`/`-RedirectUri`/`-FromAzdEnv` would set
the registered SPA redirect URIs to the localhost defaults only, dropping any already-registered frontend origin
from that set. Only a run whose combined total is empty (`-RedirectUri @()` explicitly, with no
`-FrontendOrigin`/`-FromAzdEnv`) leaves the existing SPA URIs untouched.

**However, `-Apply` never does that replacement silently.** Always pass `-FrontendOrigin` (or
`-RedirectUri`/`-FromAzdEnv`) on every re-run to keep a live origin registered: if the reconcile above would
remove an already-registered, non-localhost SPA redirect URI, the script refuses and stops with an error naming
the URI(s) instead of replacing them with the localhost defaults. Pass `-AllowRedirectUriRemoval` only if you
really mean to drop that origin (for example, retiring an old environment); removing localhost-only origins
never needs the switch. Preview (no `-Apply`) always prints the would-be removals so you can catch a missing
`-FrontendOrigin` before applying.

```powershell
az login --tenant <tenant-id>
```

**(a) An existing environment with ingress off (`BACKEND_URI` blank).** `-FromAzdEnv` fails hard on an empty `BACKEND_URI`
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

Always include `-FrontendOrigin` (or another redirect source) on every apply. The script refuses to remove a
registered non-localhost origin unless you explicitly pass `-AllowRedirectUriRemoval`.

### Rollout (ingress last)

`azd provision` re-enables external ingress and sets min replicas to 1, and the apps run in `Single` revision mode:
ACA keeps the previous (unauthenticated) revision active until the new one is ready, so a naive `azd provision` can
leave an unauthenticated revision reachable if the new one fails to boot. **So enabling ingress is the last, separate
step.** `backendIngressEnabled` (from the azd env `BACKEND_INGRESS_ENABLED`, default `true`) makes this dark rollout
possible without touching every other environment's default behavior.

1. Confirm the infrastructure, Entra auth, and frontend build-argument changes are in the release branch.
2. Run the Entra setup script and set the tenant and client ids it returns.
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
6. Sign in with the test user and complete the live authenticated checks.

**The rule:** never run `azd provision` or `azd up` against a production environment with `BACKEND_INGRESS_ENABLED`
true (or unset) until step 4a has passed with ingress off. This also covers the manual `Deploy to Azure with azd`
workflow (`.github/workflows/azure-dev.yaml`, `workflow_dispatch` only): it runs `azd provision` without setting
`BACKEND_INGRESS_ENABLED` or the `ENTRA_*` values, so dispatching it against `azureaidrivethru-prod` before 4a has
passed would publish the old, unauthenticated image with ingress on.

## .NET container app

The C# backend (`app/backend-dotnet`, built from `app/Dockerfile.dotnet`) can run beside the Python
app in the same resource group, Container Apps environment, Foundry account, Search service, and
managed identity. `DEPLOY_DOTNET_APP` gates the C# Container App and defaults to `false`. Enabling
it adds a second always-on app; review cost and approve the target environment before setting it.

`azure.yaml` declares the `backend-dotnet` service, and the Bicep module adds its matching
`azd-service-name` tag only when `deployDotnetApp` is enabled. Until the app exists in an
environment, scope deployment to Python. Once enabled and provisioned, AZD can target either
service explicitly:

```bash
azd provision
azd deploy backend
azd deploy backend-dotnet
```

`test_azd_service_wiring.py` verifies that each Container App service has a matching tag and that
the C# tag remains behind the `DEPLOY_DOTNET_APP` gate.

### Turning it on: `azd env set` steps

```bash
azd env select <env-name>
azd env set DEPLOY_DOTNET_APP true
azd env set BACKEND_DOTNET_INGRESS_ENABLED false   # dark provision first -- see rollout below
azd provision
```

This creates the `acaBackendDotnet` Container App (no ingress yet, `exists:` resolved from the
`SERVICE_BACKEND_DOTNET_RESOURCE_EXISTS` env var azd sets automatically, same pattern as the
Python app's `webAppExists`). `APP_SESSION_SECRET`, `AUTH_MODE=Entra` and the `ENTRA_*` env block
are wired byte-identical to `acaBackend` (same `effectiveAppSessionSecret`/`effectiveEntraTenantId`
Bicep variables feed both container apps), so no separate secret/registration work is needed here.

### Rollout (ingress last, same pattern as the Python app)

1. `azd deploy backend-dotnet` while `BACKEND_DOTNET_INGRESS_ENABLED` is still `false`. The dotnet
   app has no ingress yet, so this just gets the new image onto a dark revision.
2. **Dark check:** `./scripts/Verify-ProductionAuth.ps1 -RevisionsOnly` now reports on EVERY
   deployed container app it can discover, `backend-dotnet` included (it is resolved either by its
   `azd-service-name` tag or, if that tag is momentarily absent, by matching
   `AZURE_CONTAINER_APP_DOTNET_NAME`) -- confirm it shows exactly one active revision, on the
   expected image, healthy and running, before proceeding.
3. **Public provision:** `azd env set BACKEND_DOTNET_INGRESS_ENABLED true`, then `azd provision`.
   Same two-active-revisions-is-transient caveat as the Python app's step 5 above applies here too.
4. **Register the redirect URI after ingress is public:** run
   `./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -ClientId <client-id>
   -FromAzdEnv -Apply`. `-FromAzdEnv` reads both `BACKEND_URI` (must already be non-empty -- the
   Python app's public rollout) and `BACKEND_DOTNET_URI`. Once the C# app has public ingress, the
   script adds its hostname to the SPA redirect URIs. Registering while ingress is dark leaves
   `BACKEND_DOTNET_URI` empty and causes a redirect mismatch.
5. Run `./scripts/Verify-ProductionAuth.ps1` (and `-Authenticated`) again; it now has an `fqdn` to
   anonymously probe for `backend-dotnet` as well. Run this AFTER step 4 registers the redirect URI
   -- an authenticated check against a hostname Entra doesn't recognize yet as a valid redirect URI
   will fail at sign-in, not because the app itself is broken.
6. Verify the C# realtime path separately. The `postdeploy` smoke hook targets the Python backend
   and does not validate the C# endpoint.

### Rollback

Same shape as the Python app's dark-provision escape hatch: `az containerapp ingress disable
--name <dotnet-app-name> --resource-group <rg>` AND `azd env set BACKEND_DOTNET_INGRESS_ENABLED
false` then `azd provision` (run both -- the imperative `ingress disable` alone is reverted by the
next provision if the env value is still `true`) takes the dotnet app dark again without touching
the Python app or its traffic at all -- the two Container Apps are provisioned and scaled
completely independently.

### Teardown of just the dotnet app

To remove the dotnet app alone (keep the Python app, Foundry account, Search service, and shared
identity):

```bash
azd env set DEPLOY_DOTNET_APP false
azd provision
az containerapp delete --name <dotnet-app-name> --resource-group <rg> --yes
```

Bicep's conditional module (`if (deployDotnetApp)`) is an incremental ARM/Bicep deployment -- once
the flag flips back to `false`, the next `azd provision` simply SKIPS the `acaBackendDotnet`
module; it does NOT delete the Container App that a previous provision already created. With
`containerMinReplicas: 1`, that orphaned app keeps running (and costing money) until it is deleted
explicitly, which is why the `az containerapp delete` step above is required, not optional. After
deleting it, also revert `azd deploy` scoping back to `azd deploy backend` (see above) for this
environment, and consider re-running `Setup-EntraAuth.ps1 -FromAzdEnv -Apply` with
`-AllowRedirectUriRemoval` if the dotnet app's redirect URI should be dropped from the Entra
registration too (it is not removed automatically by this teardown).
