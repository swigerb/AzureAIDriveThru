targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the the environment which is used to generate a short unique hash used in all resources.')
param environmentName string

@minLength(1)
@description('Location for AI Search and Storage resources')
// Constrained due to semantic ranker availability: https://learn.microsoft.com/azure/search/search-region-support#americas
@allowed([
  'brazilsouth'
  'canadacentral'
  'canadaeast'
  'eastus2'
  'northcentralus'
  'westus'
  'westus2'
  'westcentralus'
  'northeurope'
  'francecentral'
  'switzerlandnorth'
  'switzerlandwest'
  'uksouth'
  'australiaeast'
  'eastasia'
  'southeastasia'
  'centralindia'
  'jioindiawest'
  'japanwest'
  'koreacentral'
])
@metadata({
  azd: {
    type: 'location'
  }
})
param location string

param backendServiceName string = ''
param resourceGroupName string = ''

param logAnalyticsName string = ''

param reuseExistingSearch bool
param searchEndpoint string = ''
param searchServiceName string = ''
param searchServiceResourceGroupName string = ''
param searchServiceLocation string = ''
// The free tier does not support managed identity (required) or semantic search (optional)
@allowed(['free', 'basic', 'standard', 'standard2', 'standard3', 'storage_optimized_l1', 'storage_optimized_l2'])
param searchServiceSkuName string
param searchIndexName string
param searchSemanticConfiguration string
param searchServiceSemanticRankerLevel string
var actualSearchServiceSemanticRankerLevel = (searchServiceSkuName == 'free')
  ? 'disabled'
  : searchServiceSemanticRankerLevel
param searchIdentifierField string
param searchContentField string
param searchTitleField string
param searchEmbeddingField string
param searchUseVectorQuery bool

param storageAccountName string = ''
param storageResourceGroupName string = ''
param storageResourceGroupLocation string = location
param storageContainerName string = 'content'
param storageSkuName string

param reuseExistingOpenAi bool = false
param openAiServiceName string = ''
param openAiResourceGroupName string = ''
param openAiEndpoint string = ''
param openAiRealtimeDeployment string = ''
param openAiRealtimeVoiceChoice string = ''
@description('Optional reasoning.effort override for gpt-realtime-2.x (none|minimal|low|medium|high|xhigh, or off). Empty = app/backend/config.yaml')
param openAiRealtimeReasoningEffort string = ''
@description('Optional: is the realtime deployment a reasoning model (true|false|auto)? Empty = app/backend/config.yaml (auto = infer from the deployment name)')
param openAiRealtimeReasoningModel string = ''
@description('Optional input transcription model/deployment override. Empty = app/backend/config.yaml (whisper-1)')
param openAiRealtimeTranscriptionModel string = ''

@description('Location for the OpenAI resource group')
@allowed([
  'eastus2'
  'swedencentral'
])
@metadata({
  azd: {
    type: 'location'
  }
})
param openAiServiceLocation string

param realtimeDeploymentCapacity int
param embeddingDeploymentCapacity int

// --- Persona picker (ADR-001, docs/persona-architecture.md section 10) ---
// This environment is independent of the old demo resource groups (see
// docs/persona-architecture.md section 10 for the full list): its own
// Foundry (Azure OpenAI) account, its own paid AI Search service. Adding
// another persona is a personas/<id>/ pack (#70) -- no Bicep edits. Index
// names are NOT tracked here: each pack's own persona.json `search.indexName`
// (4.2) is the one source of truth, read by the #84 ingestion hook and by
// the app itself. The tracked default for `personas` is empty on purpose
// (Rick's review of #93): an empty PERSONAS env var makes the app's own
// loader (app/backend/persona_loader.py, PersonaCatalog.load) enable every
// pack it finds under PERSONAS_DIR, so no brand name needs to sit in
// committed infra ahead of #76 (which inverts test_rebrand_verification.py's
// pre-persona-pack brand guard) and no later flip is needed when a new
// brand's pack (#79) lands. Override at provision time (azd env set
// PERSONAS=...) only to restrict this environment to a subset of packs.
@description('Allow-list of persona ids served by this environment, comma-separated (matches the app PERSONAS env var). Empty (the tracked default) means "every persona pack found under PERSONAS_DIR" -- app/backend/persona_loader.py discovers them automatically. Override at provision time (azd env set PERSONAS=...) only to restrict this environment to a subset of packs.')
param personas string = ''

@description('Default persona id when a session omits ?persona= (app DEFAULT_PERSONA env var). Should be one of the comma-separated ids in personas. Empty (the tracked default) means app/backend/persona_loader.py picks its own default (its first-party pack if enabled, else the first enabled id alphabetically) -- keeps tracked infra free of persona names, same as personas above.')
param defaultPersona string = ''

@description('JSON array of Foundry/Azure OpenAI model deployments to create on this environment\'s own account (section 7.2, 10.3). Each entry: catalogId (matches app/backend/config.yaml models.catalog), deploymentName, modelName, modelVersion, format (Foundry model-format id, defaults to OpenAI), skuName, capacity, isDefaultRealtime (exactly one entry should be true -- it becomes AZURE_OPENAI_REALTIME_DEPLOYMENT). realtimeDeploymentCapacity/embeddingDeploymentCapacity above still override the matching entries by catalogId, so the existing "bump a param, azd provision" scaling flow (10.3) keeps working. The tracked list lives in infra/model-deployments.json (Rick\'s review of #93): adding a #82 model is one JSON entry there, no Bicep edits.')
param openAiModelDeploymentsData array = loadJsonContent('model-deployments.json')

// --- C# backend (section 10.1 option A, 10.2; added by S7, #17) ---
// The module is always present so #17 only has to flip this flag, but it
// deploys nothing until then: no app/backend-dotnet project exists yet.
@description('Deploy the .NET container app alongside the Python one, sharing the same ACA environment, Foundry account, Search service and managed identity (10.2). Leave false until #12-#17 land a real image; the module compiles either way.')
param deployDotnetApp bool = false

@description('Name override for the .NET container app. Empty = derived from the environment token, matching the Python app\'s naming convention.')
param dotnetServiceName string = ''

param tenantId string = tenant().tenantId

@description('Id of the user or app to assign application roles')
param principalId string = ''

var abbrs = loadJsonContent('abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName }

@description('Whether the deployment is running on GitHub Actions')
param runningOnGh string = ''

@description('Whether the deployment is running on Azure DevOps Pipeline')
param runningOnAdo string = ''

@description('Used by azd for containerapps deployment')
param webAppExists bool

@description('Used by azd for the .NET container app deployment (S7, #17). Defaults false, unlike webAppExists, since azure.yaml only gains the backend-dotnet service (and azd only starts generating SERVICE_BACKEND_DOTNET_RESOURCE_EXISTS) with this same change.')
param dotnetAppExists bool = false

@allowed(['Consumption', 'D4', 'D8', 'D16', 'D32', 'E4', 'E8', 'E16', 'E32', 'NC24-A100', 'NC48-A100', 'NC96-A100'])
param azureContainerAppsWorkloadProfile string

param acaIdentityName string = '${environmentName}-aca-identity'
param containerRegistryName string = '${replace(environmentName, '-', '')}acr'

// --- EasyAuth (Entra ID) parameters ---
@description('Enable Entra ID authentication on the Container App. Requires authClientId and a pre-provisioned aad-client-secret.')
param enableAuth bool = false

@description('Entra ID application (client) ID for EasyAuth. Leave empty to skip auth configuration.')
param authClientId string = ''

@description('Entra ID tenant ID used to build the OpenID issuer URL. Defaults to the deployment subscription tenant.')
param authTenantId string = tenant().tenantId

@secure()
@description('Entra ID client secret. Stored as Container App secret "aad-client-secret". Provision via azd env or out-of-band — never commit to source.')
param authClientSecret string = ''

// --- HMAC session-token secret (/api/auth/session) ---
// Every replica/restart must validate tokens minted by any other, so the secret
// is shared via a Container App secret instead of os.urandom per process.
@secure()
@description('HMAC secret for /api/auth/session tokens (container env APP_SESSION_SECRET). Pin it with `azd env set APP_SESSION_SECRET <random>`; empty = a random secret generated on each provision.')
param appSessionSecret string = ''

@secure()
@description('Do not set. Random fallback for appSessionSecret (newGuid() is only allowed as a parameter default).')
param appSessionSecretFallback string = '${newGuid()}${newGuid()}'

var effectiveAppSessionSecret = !empty(appSessionSecret) ? appSessionSecret : appSessionSecretFallback

// --- Entra ID token validation (ADR-002, docs/persona-architecture.md section 18) ---
// EasyAuth (enableAuth/authClientId/authClientSecret above) is superseded by in-app bearer
// validation on both backends: one Entra app registration, AUTH_MODE=Entra pinned in Azure, no
// client secret. These four values are the shared contract (section 18.8) both container apps'
// env blocks read; AUTH_MODE itself is not a param -- it's hardcoded 'Entra' on each app below,
// same as RUNNING_IN_PRODUCTION, so this environment can never accidentally provision a
// pass-through backend. This wave (#17) only wires the C# app's env with the same names Python
// will read once #146 (EasyAuth removal, tracked separately -- section 18.12) lands; it does not
// modify the Python app's existing EasyAuth wiring above, to avoid conflicting with that PR.
@description('Entra ID tenant id for AUTH_MODE=Entra token validation (ADR-002 18.1/18.4). Defaults to the deployment subscription tenant; override only if the app registration lives in a different tenant.')
param entraTenantId string = tenant().tenantId

@description('Entra ID application (client) id for AUTH_MODE=Entra token validation (ADR-002 18.1/18.4). Empty until Setup-EntraAuth.ps1 (#146) creates the registration -- both backends fail fast in Production without a valid id, so an empty value here just means this environment is not provisioned in Production mode yet.')
param entraClientId string = ''

@description('Delegated scope required on an Entra access token (ADR-002 18.1/18.4).')
param entraApiScope string = 'access_as_user'

@description('App role required on an Entra access token (ADR-002 18.1/18.4).')
param entraAppRole string = 'DriveThru.User'

// Ingress switch (ADR-002 18.8/18.10): lets the .NET app be provisioned and warmed up without
// being publicly reachable. Independent of the Python app's own (not yet added -- #146) switch,
// because the C# app must not go public before its Entra parity work lands (#147, design 18.12);
// deployDotnetApp alone only controls whether the container app resource exists at all.
@description('Ingress switch for the .NET container app. Defaults false: the C# app stays internal-only (no public ingress) even when deployDotnetApp is true, until #147 lands its own Entra token validation. Maps to the azd env BACKEND_DOTNET_INGRESS_ENABLED.')
param backendDotnetIngressEnabled bool = false

// Figure out if we're running as a user or service principal
var principalType = empty(runningOnGh) && empty(runningOnAdo) ? 'User' : 'ServicePrincipal'

// Organize resources in a resource group
resource resourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

resource openAiResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' existing = if (!empty(openAiResourceGroupName)) {
  name: !empty(openAiResourceGroupName) ? openAiResourceGroupName : resourceGroup.name
}

resource searchServiceResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' existing = if (!empty(searchServiceResourceGroupName)) {
  name: !empty(searchServiceResourceGroupName) ? searchServiceResourceGroupName : resourceGroup.name
}

resource storageResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' existing = if (!empty(storageResourceGroupName)) {
  name: !empty(storageResourceGroupName) ? storageResourceGroupName : resourceGroup.name
}

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.7.0' = {
  name: 'loganalytics'
  scope: resourceGroup
  params: {
    name: !empty(logAnalyticsName) ? logAnalyticsName : '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    location: location
    tags: tags
    skuName: 'PerGB2018'
    dataRetention: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    useResourcePermissions: true
  }
}

// Azure container apps resources

// User-assigned identity for pulling images from ACR
module acaIdentity 'core/security/aca-identity.bicep' = {
  name: 'aca-identity'
  scope: resourceGroup
  params: {
    identityName: acaIdentityName
    location: location
  }
}

module containerApps 'core/host/container-apps.bicep' = {
  name: 'container-apps'
  scope: resourceGroup
  params: {
    name: 'app'
    tags: tags
    location: location
    workloadProfile: azureContainerAppsWorkloadProfile
    containerAppsEnvironmentName: '${environmentName}-aca-env'
    containerRegistryName: '${containerRegistryName}${resourceToken}'
    logAnalyticsWorkspaceResourceId: logAnalytics.outputs.resourceId
  }
}

// Container Apps for the web application (Python Quart app with JS frontend)
module acaBackend 'core/host/container-app-upsert.bicep' = {
  name: 'aca-web'
  scope: resourceGroup
  dependsOn: [
    containerApps
    acaIdentity
  ]
  params: {
    name: !empty(backendServiceName) ? backendServiceName : '${abbrs.webSitesContainerApps}backend-${resourceToken}'
    location: location
    identityName: acaIdentityName
    exists: webAppExists
    workloadProfile: azureContainerAppsWorkloadProfile
    containerRegistryName: containerApps.outputs.registryName
    containerAppsEnvironmentName: containerApps.outputs.environmentName
    identityType: 'UserAssigned'
    tags: union(tags, { 'azd-service-name': 'backend' })
    targetPort: 8000
    containerCpuCoreCount: '1.0'
    containerMemory: '2Gi'
    containerMinReplicas: 1
    containerMaxReplicas: 5
    healthProbePath: '/health'
    enableWebSocket: true
    // Order state and the resume credential live in one process (gunicorn
    // --workers 1), so a reconnecting browser must land on the same replica.
    // The Envoy affinity cookie is set on the page load and sent on the
    // websocket upgrade. Needs single revision mode (container-app.bicep default).
    stickySessionsAffinity: 'sticky'
    secrets: union(enableAuth && !empty(authClientSecret) ? { 'aad-client-secret': authClientSecret } : {}, {
      'app-session-secret': effectiveAppSessionSecret
    })
    // Sending a secrets list replaces the app's secrets, so keep an
    // out-of-band aad-client-secret alive when it isn't supplied here.
    preserveExistingSecretNames: enableAuth && empty(authClientSecret) ? [ 'aad-client-secret' ] : []
    secretEnv: {
      APP_SESSION_SECRET: 'app-session-secret'
    }
    env: union({
      AZURE_SEARCH_ENDPOINT: resolvedSearchEndpoint
      AZURE_SEARCH_INDEX: searchIndexName
      AZURE_SEARCH_SEMANTIC_CONFIGURATION: searchSemanticConfiguration
      AZURE_SEARCH_IDENTIFIER_FIELD: searchIdentifierField
      AZURE_SEARCH_CONTENT_FIELD: searchContentField
      AZURE_SEARCH_TITLE_FIELD: searchTitleField
      AZURE_SEARCH_EMBEDDING_FIELD: searchEmbeddingField
      AZURE_SEARCH_USE_VECTOR_QUERY: searchUseVectorQuery
      // Free SKU has no semantic ranker; the app must not request one or every
      // query returns HTTP 400.
      AZURE_SEARCH_SEMANTIC_RANKER: actualSearchServiceSemanticRankerLevel
      AZURE_OPENAI_EASTUS2_ENDPOINT: resolvedOpenAiEndpoint
      AZURE_OPENAI_REALTIME_DEPLOYMENT: reuseExistingOpenAi ? openAiRealtimeDeployment : defaultRealtimeDeployment.deploymentName
      AZURE_OPENAI_REALTIME_VOICE_CHOICE: openAiRealtimeVoiceChoice
      // Issue #82: the cascade pipeline's Foundry chat client endpoint.
      AZURE_AI_FOUNDRY_ENDPOINT: resolvedFoundryEndpoint
      // Catalog id -> deployment name for every model this environment created (7.2).
      AZURE_AI_MODEL_DEPLOYMENTS: string(modelDeploymentsMap)
      RUNNING_IN_PRODUCTION: 'true'
      // Changing a secret alone does not restart running replicas; a changed
      // fingerprint changes the template, so every replica restarts on the new
      // secret together instead of old and new replicas disagreeing.
      APP_SESSION_SECRET_FINGERPRINT: uniqueString(effectiveAppSessionSecret)
      // For using managed identity to access Azure resources. See https://github.com/microsoft/azure-container-apps/issues/442
      AZURE_CLIENT_ID: acaIdentity.outputs.clientId
    },
    // Persona allow-list (4.2, 10.2): omit PERSONAS entirely when empty (the
    // tracked default) so app/backend/persona_loader.py's own "no PERSONAS ->
    // enable every pack found under PERSONAS_DIR" behavior applies, instead of
    // the app seeing an explicit empty string (Rick's review of #93).
    empty(personas) ? {} : { PERSONAS: personas },
    // Same "omit when empty" treatment for the default persona id, so the
    // loader's own default (its first-party pack if enabled, else the first
    // enabled id alphabetically) applies instead of a hardcoded name here.
    empty(defaultPersona) ? {} : { DEFAULT_PERSONA: defaultPersona },
    // Optional overrides of model.reasoning_effort / reasoning_model / transcription_model
    // in app/backend/config.yaml; unset means the config.yaml value applies.
    empty(openAiRealtimeReasoningEffort) ? {} : { AZURE_OPENAI_REALTIME_REASONING_EFFORT: openAiRealtimeReasoningEffort },
    empty(openAiRealtimeReasoningModel) ? {} : { AZURE_OPENAI_REALTIME_REASONING_MODEL: openAiRealtimeReasoningModel },
    empty(openAiRealtimeTranscriptionModel) ? {} : { AZURE_OPENAI_REALTIME_TRANSCRIPTION_MODEL: openAiRealtimeTranscriptionModel })
  }
}

// Container App for the .NET backend (10.1 option A, 10.2). Disabled by
// default (deployDotnetApp = false): S7 (#17) flips the flag once
// app/backend-dotnet exists. It shares acaIdentity, so the RBAC already
// granted below (openAiRoleBackend, searchRoleBackend) covers it too -- no
// separate role assignments needed for a second app on the same identity.
module acaBackendDotnet 'core/host/container-app-upsert.bicep' = if (deployDotnetApp) {
  name: 'aca-web-dotnet'
  scope: resourceGroup
  params: {
    name: !empty(dotnetServiceName) ? dotnetServiceName : '${abbrs.webSitesContainerApps}backend-dotnet-${resourceToken}'
    location: location
    identityName: acaIdentityName
    exists: dotnetAppExists
    workloadProfile: azureContainerAppsWorkloadProfile
    containerRegistryName: containerApps.outputs.registryName
    containerAppsEnvironmentName: containerApps.outputs.environmentName
    identityType: 'UserAssigned'
    tags: union(tags, { 'azd-service-name': 'backend-dotnet' })
    targetPort: 8000
    containerCpuCoreCount: '1.0'
    containerMemory: '2Gi'
    containerMinReplicas: 1
    containerMaxReplicas: 5
    healthProbePath: '/health'
    enableWebSocket: true
    stickySessionsAffinity: 'sticky'
    // Ingress last (ADR-002 18.8/18.10): defaults false, independent of the Python app's own
    // ingress, until #147's parity work lands.
    ingressEnabled: backendDotnetIngressEnabled
    // Same shared HMAC session secret as the Python app (#44) -- byte-identical because both
    // read the same effectiveAppSessionSecret variable into a Container App secret of the same
    // name, not because Container App secrets are shared by identity.
    secrets: {
      'app-session-secret': effectiveAppSessionSecret
    }
    secretEnv: {
      APP_SESSION_SECRET: 'app-session-secret'
    }
    // Same Foundry account, Search service and persona/model config as the Python app (10.2).
    // AUTH_MODE=Entra + ENTRA_* (ADR-002 18.8): NO EasyAuth for this app -- EasyAuth is superseded
    // tenant-wide by in-app bearer validation (ADR-002), so the C# app never gets the EasyAuth
    // wiring the Python app currently has above; it goes straight to the ADR-002 shape instead.
    // The env names below match what #147 will read (persona-architecture.md 18.8); the
    // validation logic itself is #147's scope, not this prep task's.
    env: union({
      AZURE_SEARCH_ENDPOINT: resolvedSearchEndpoint
      AZURE_SEARCH_INDEX: searchIndexName
      AZURE_SEARCH_SEMANTIC_CONFIGURATION: searchSemanticConfiguration
      AZURE_SEARCH_IDENTIFIER_FIELD: searchIdentifierField
      AZURE_SEARCH_CONTENT_FIELD: searchContentField
      AZURE_SEARCH_TITLE_FIELD: searchTitleField
      AZURE_SEARCH_EMBEDDING_FIELD: searchEmbeddingField
      AZURE_SEARCH_USE_VECTOR_QUERY: searchUseVectorQuery
      AZURE_SEARCH_SEMANTIC_RANKER: actualSearchServiceSemanticRankerLevel
      AZURE_OPENAI_EASTUS2_ENDPOINT: resolvedOpenAiEndpoint
      AZURE_OPENAI_REALTIME_DEPLOYMENT: reuseExistingOpenAi ? openAiRealtimeDeployment : defaultRealtimeDeployment.deploymentName
      AZURE_OPENAI_REALTIME_VOICE_CHOICE: openAiRealtimeVoiceChoice
      // Issue #82: the cascade pipeline's Foundry chat client endpoint (unused by the
      // .NET backend until it ports the cascade processor -- see issue #82's scope note).
      AZURE_AI_FOUNDRY_ENDPOINT: resolvedFoundryEndpoint
      AZURE_AI_MODEL_DEPLOYMENTS: string(modelDeploymentsMap)
      RUNNING_IN_PRODUCTION: 'true'
      AZURE_CLIENT_ID: acaIdentity.outputs.clientId
      // Same fingerprint trick as the Python app: a changed secret changes the template, so
      // every replica restarts together instead of old and new replicas disagreeing.
      APP_SESSION_SECRET_FINGERPRINT: uniqueString(effectiveAppSessionSecret)
      AUTH_MODE: 'Entra'
      ENTRA_TENANT_ID: entraTenantId
      ENTRA_CLIENT_ID: entraClientId
      ENTRA_API_SCOPE: entraApiScope
      ENTRA_APP_ROLE: entraAppRole
    },
    // Same "omit when empty" persona behavior as the Python app's env, above.
    empty(personas) ? {} : { PERSONAS: personas },
    empty(defaultPersona) ? {} : { DEFAULT_PERSONA: defaultPersona })
  }
}

var embedModel = 'text-embedding-3-large'

// Computed once so both container apps (and the outputs below) reference the
// same value instead of repeating the reuseExisting ternary at every call site.
var resolvedSearchEndpoint = reuseExistingSearch ? searchEndpoint : 'https://${searchService.outputs.name}.search.windows.net'
// The AVM cognitive-services/account module's generic `endpoint` output
// (cognitiveService.properties.endpoint) is the multi-service Foundry
// endpoint for an `AIServices`-kind account, not the OpenAI-compatible host.
// The realtime relay (rtmt.py) needs the `<subdomain>.openai.azure.com` host
// specifically (Rick's review of #93; confirmed against the Foundry realtime
// docs, which show `https://{your-resource}.openai.azure.com` regardless of
// account kind) -- built explicitly from the same customSubDomainName passed
// to the module below, instead of trusting the module's generic output.
var openAiCustomSubDomainName = !empty(openAiServiceName)
  ? openAiServiceName
  : '${abbrs.cognitiveServicesAccounts}${resourceToken}'
var resolvedOpenAiEndpoint = reuseExistingOpenAi ? openAiEndpoint : 'https://${openAiCustomSubDomainName}.openai.azure.com'
// Issue #82: the cascade pipeline's Foundry chat model calls (azure-ai-inference SDK,
// DefaultAzureCredential only) go through the Azure AI Model Inference API instead of
// the OpenAI-compatible host above -- the surface that also serves the design's
// non-OpenAI-format models (Phi-4), which the OpenAI-compatible endpoint cannot.
// Same account, same `openAiCustomSubDomainName`, different DNS name and `/models`
// route suffix (Microsoft Learn: Azure AI Model Inference API reference). Reuse mode
// has no dedicated override param yet (no reuse-existing environment has exercised
// cascade so far); it derives from the same subdomain as resolvedOpenAiEndpoint until
// one is needed.
var resolvedFoundryEndpoint = 'https://${openAiCustomSubDomainName}.services.ai.azure.com/models'

// The model deployment list (10.3, tracked in infra/model-deployments.json --
// Rick's review of #93: adding a #82 model is one JSON entry there, no Bicep
// edits) with the two existing scale-only params (realtimeDeploymentCapacity,
// embeddingDeploymentCapacity) still overriding their matching entries by
// catalogId, so "bump a param, azd provision" keeps working for the two
// knobs the design calls out even though the list itself is data, not
// hardcoded Bicep.
var capacityOverridesByCatalogId = {
  'gpt-realtime-2.1': realtimeDeploymentCapacity
  '${embedModel}': embeddingDeploymentCapacity
}
var openAiModelDeployments = [for d in openAiModelDeploymentsData: union(d, {
  capacity: capacityOverridesByCatalogId[?d.catalogId] ?? d.capacity
})]
// Exactly one entry should be isDefaultRealtime: true -- it feeds
// AZURE_OPENAI_REALTIME_DEPLOYMENT, same as the old openAiDeployments[0]. Falls
// back to the list's first entry so a catalog with no isDefaultRealtime flag
// (or an empty override list) still resolves to something instead of null.
var defaultRealtimeDeployment = first(filter(openAiModelDeployments, d => d.isDefaultRealtime)) ?? openAiModelDeployments[0]
// Bicep output AZURE_AI_MODEL_DEPLOYMENTS (7.2): catalog id -> deployment name,
// for whichever deployments this environment actually created.
var modelDeploymentsMap = reduce(openAiModelDeployments, {}, (cur, d) => union(cur, { '${d.catalogId}': d.deploymentName }))

var openAiDeployments = [for d in openAiModelDeployments: {
  name: d.deploymentName
  model: {
    // Per-model Foundry format id (Rick's review of #93), defaulting to
    // OpenAI so an entry that omits it (or an older cached parameter file)
    // still deploys exactly as before.
    format: d.?format ?? 'OpenAI'
    name: d.modelName
    version: d.modelVersion
  }
  sku: {
    name: d.skuName
    capacity: d.capacity
  }
}]

module openAi 'br/public:avm/res/cognitive-services/account:0.8.0' = if (!reuseExistingOpenAi) {
  name: 'openai'
  scope: openAiResourceGroup
  params: {
    name: !empty(openAiServiceName) ? openAiServiceName : '${abbrs.cognitiveServicesAccounts}${resourceToken}'
    location: openAiServiceLocation
    tags: tags
    // A Foundry (multi-service AI Services) account, not a plain OpenAI one
    // (Rick's review of #93): #82's non-OpenAI Foundry chat models can only
    // deploy on an `AIServices`-kind account, and `kind` is immutable after
    // the first provision. The realtime host stays `<subdomain>.openai.azure.com`
    // regardless (see resolvedOpenAiEndpoint above and openAiCustomSubDomainName).
    kind: 'AIServices'
    customSubDomainName: openAiCustomSubDomainName
    sku: 'S0'
    deployments: openAiDeployments
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {}
    roleAssignments: [
      {
        roleDefinitionIdOrName: 'Cognitive Services OpenAI User'
        principalId: principalId
        principalType: principalType
      }
      {
        // Issue #82 (Rick's #82 review notes): the cascade pipeline's Foundry chat
        // client calls the Azure AI Model Inference API (resolvedFoundryEndpoint),
        // which authorizes against the generic "Cognitive Services User" role rather
        // than (or in addition to) "Cognitive Services OpenAI User" above.
        roleDefinitionIdOrName: 'Cognitive Services User'
        principalId: principalId
        principalType: principalType
      }
    ]
  }
}

module searchService 'br/public:avm/res/search/search-service:0.7.1' = if (!reuseExistingSearch) {
  name: 'search-service'
  scope: searchServiceResourceGroup
  params: {
    name: !empty(searchServiceName) ? searchServiceName : 'gptkb-${resourceToken}'
    location: !empty(searchServiceLocation) ? searchServiceLocation : location
    tags: tags
    disableLocalAuth: true
    sku: searchServiceSkuName
    replicaCount: 1
    semanticSearch: actualSearchServiceSemanticRankerLevel
    // An outbound managed identity is required for integrated vectorization to work,
    // and is only supported on non-free tiers:
    managedIdentities: { systemAssigned: true }
    roleAssignments: [
      {
        roleDefinitionIdOrName: 'Search Index Data Reader'
        principalId: principalId
        principalType: principalType
      }
      {
        roleDefinitionIdOrName: 'Search Index Data Contributor'
        principalId: principalId
        principalType: principalType
      }
      {
        roleDefinitionIdOrName: 'Search Service Contributor'
        principalId: principalId
        principalType: principalType
      }
    ]
  }
}

module storage 'br/public:avm/res/storage/storage-account:0.9.1' = {
  name: 'storage'
  scope: storageResourceGroup
  params: {
    name: !empty(storageAccountName) ? storageAccountName : '${abbrs.storageStorageAccounts}${resourceToken}'
    location: storageResourceGroupLocation
    tags: tags
    kind: 'StorageV2'
    skuName: storageSkuName
    publicNetworkAccess: 'Enabled' // Necessary for uploading documents to storage container
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    blobServices: {
      deleteRetentionPolicyDays: 2
      deleteRetentionPolicyEnabled: true
      containers: [
        {
          name: storageContainerName
          publicAccess: 'None'
        }
      ]
    }
    roleAssignments: [
      {
        roleDefinitionIdOrName: 'Storage Blob Data Reader'
        principalId: principalId
        principalType: principalType
      }
      // For uploading documents to storage container:
      {
        roleDefinitionIdOrName: 'Storage Blob Data Contributor'
        principalId: principalId
        principalType: principalType
      }
    ]
  }
}

// Roles for the backend to access other services
module openAiRoleBackend 'core/security/role.bicep' = {
  scope: openAiResourceGroup
  name: 'openai-role-backend'
  params: {
    principalId: acaBackend.outputs.identityPrincipalId
    roleDefinitionId: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    principalType: 'ServicePrincipal'
  }
}

// Issue #82 (Rick's PR #118 review, required item 6): the cascade pipeline's Foundry chat
// client (ChatCompletionsClient against resolvedFoundryEndpoint) authorizes with the generic
// "Cognitive Services User" role, same as the `principalId` grant already on the `openAi`
// module above -- that earlier grant only covers the deploying user's own `az` session/local
// dev, never the deployed container app, so without this module the deployed backend's
// managed identity 401s the very first cascade chat-completions call. STT/TTS need no separate
// grant here: they call the SAME AIServices account's `/openai/v1/audio/*` routes, already
// covered by `openAiRoleBackend`'s "Cognitive Services OpenAI User" below.
module openAiRoleBackendCascade 'core/security/role.bicep' = {
  scope: openAiResourceGroup
  name: 'openai-role-backend-cascade'
  params: {
    principalId: acaBackend.outputs.identityPrincipalId
    roleDefinitionId: 'a97b65f3-24c7-4388-baec-2e87135dc908' // Cognitive Services User
    principalType: 'ServicePrincipal'
  }
}

// Used to issue search queries
// https://learn.microsoft.com/azure/search/search-security-rbac
module searchRoleBackend 'core/security/role.bicep' = {
  scope: searchServiceResourceGroup
  name: 'search-role-backend'
  params: {
    principalId: acaBackend.outputs.identityPrincipalId
    roleDefinitionId: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
    principalType: 'ServicePrincipal'
  }
}

// Necessary for integrated vectorization, for search service to access storage
module storageRoleSearchService 'core/security/role.bicep' = if (!reuseExistingSearch) {
  scope: storageResourceGroup
  name: 'storage-role-searchservice'
  params: {
    principalId: !reuseExistingSearch ? searchService.outputs.systemAssignedMIPrincipalId : ''
    roleDefinitionId: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' // Storage Blob Data Reader
    principalType: 'ServicePrincipal'
  }
}

// Necessary for integrated vectorization, for search service to access OpenAI embeddings
module openAiRoleSearchService 'core/security/role.bicep' = if (!reuseExistingSearch) {
  scope: openAiResourceGroup
  name: 'openai-role-searchservice'
  params: {
    principalId: !reuseExistingSearch ? searchService.outputs.systemAssignedMIPrincipalId : ''
    roleDefinitionId: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    principalType: 'ServicePrincipal'
  }
}

// --- EasyAuth: Entra ID authentication on the Container App ---
// Only deployed when enableAuth is true and a client ID is provided.
// The Container App secret 'aad-client-secret' must be provisioned before or
// alongside this resource (handled above via the secrets param when authClientSecret is supplied,
// or provisioned out-of-band via `az containerapp secret set`).
module containerAppAuth 'core/security/container-app-auth.bicep' = if (enableAuth && !empty(authClientId)) {
  name: 'container-app-auth'
  scope: resourceGroup
  dependsOn: [
    acaBackend
  ]
  params: {
    containerAppName: acaBackend.outputs.name
    clientId: authClientId
    tenantId: authTenantId
  }
}

output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenantId
output AZURE_RESOURCE_GROUP string = resourceGroup.name

output AZURE_OPENAI_EASTUS2_ENDPOINT string = resolvedOpenAiEndpoint
// Issue #82: the cascade pipeline's Foundry chat client endpoint (see
// resolvedFoundryEndpoint above). Container apps read this as AZURE_AI_FOUNDRY_ENDPOINT.
output AZURE_AI_FOUNDRY_ENDPOINT string = resolvedFoundryEndpoint
output AZURE_OPENAI_REALTIME_DEPLOYMENT string = reuseExistingOpenAi
  ? openAiRealtimeDeployment
  : defaultRealtimeDeployment.deploymentName
output AZURE_OPENAI_REALTIME_VOICE_CHOICE string = openAiRealtimeVoiceChoice
output AZURE_OPENAI_EMBEDDING_DEPLOYMENT string = embedModel
output AZURE_OPENAI_EMBEDDING_MODEL string = embedModel
// Catalog id -> deployment name for every model this environment created
// (section 7.2). A catalog entry (app/backend/config.yaml) with no matching
// key here isn't selectable.
output AZURE_AI_MODEL_DEPLOYMENTS string = string(modelDeploymentsMap)

output AZURE_SEARCH_ENDPOINT string = resolvedSearchEndpoint
output AZURE_SEARCH_INDEX string = searchIndexName
output AZURE_SEARCH_SEMANTIC_CONFIGURATION string = searchSemanticConfiguration
output AZURE_SEARCH_IDENTIFIER_FIELD string = searchIdentifierField
output AZURE_SEARCH_CONTENT_FIELD string = searchContentField
output AZURE_SEARCH_TITLE_FIELD string = searchTitleField
output AZURE_SEARCH_EMBEDDING_FIELD string = searchEmbeddingField
output AZURE_SEARCH_USE_VECTOR_QUERY bool = searchUseVectorQuery
// Surfaces the *effective* ranker level so the app knows whether it may issue
// semantic queries. On the free SKU this resolves to 'disabled', and sending
// query_type="semantic" to a service without the ranker returns HTTP 400.
output AZURE_SEARCH_SEMANTIC_RANKER string = actualSearchServiceSemanticRankerLevel

output AZURE_STORAGE_ENDPOINT string = 'https://${storage.outputs.name}.blob.core.windows.net'
output AZURE_STORAGE_ACCOUNT string = storage.outputs.name
output AZURE_STORAGE_CONNECTION_STRING string = 'ResourceId=/subscriptions/${subscription().subscriptionId}/resourceGroups/${storageResourceGroup.name}/providers/Microsoft.Storage/storageAccounts/${storage.outputs.name}'
output AZURE_STORAGE_CONTAINER string = storageContainerName
output AZURE_STORAGE_RESOURCE_GROUP string = storageResourceGroup.name

output PERSONAS string = personas
output DEFAULT_PERSONA string = defaultPersona

output BACKEND_URI string = acaBackend.outputs.uri
// Empty until deployDotnetApp is flipped on (S7, #17); the C# app then serves
// the same frontend and personas behind its own hostname (10.1 option A).
output AZURE_CONTAINER_APP_DOTNET_NAME string = deployDotnetApp ? acaBackendDotnet.outputs.name : ''
output BACKEND_DOTNET_URI string = deployDotnetApp ? acaBackendDotnet.outputs.uri : ''
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerApps.outputs.registryLoginServer
