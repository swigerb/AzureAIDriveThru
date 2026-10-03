# Connecting Microsoft Foundry AI Drive Thru to existing services

Microsoft Foundry AI Drive Thru can reuse an existing Foundry AIServices account and an existing Azure AI Search service. Reusing a service does not remove the app's schema requirements: the Foundry resource must expose the deployments the model catalog maps to, and Search must contain one menu index per enabled persona.

## Reuse an existing Foundry AIServices account

Run these commands before `azd up`:

```powershell
azd env set AZURE_OPENAI_REUSE_EXISTING true
azd env set AZURE_OPENAI_RESOURCE_GROUP <resource-group-containing-foundry>
azd env set AZURE_OPENAI_EASTUS2_ENDPOINT https://<foundry-subdomain>.openai.azure.com
```

The standard deployment expects the catalog ids in `app/backend/config.yaml` to resolve through `AZURE_AI_MODEL_DEPLOYMENTS`, which Bicep emits from `infra/model-deployments.json`. If you reuse a Foundry resource, create deployments with those deployment names, or update `infra/model-deployments.json` before provisioning.

The current standard deployment names are:

- `gpt-realtime-2.1`
- `text-embedding-3-large`
- `gpt-5-mini`
- `phi-4`
- `gpt-4o-transcribe`
- `gpt-4o-mini-tts`

`AZURE_OPENAI_REALTIME_DEPLOYMENT` is still emitted for backward compatibility and local fallback, but the model picker uses `AZURE_AI_MODEL_DEPLOYMENTS` when the map contains the selected catalog id.

## Reuse an existing Azure AI Search service

Run these commands before `azd up`:

```powershell
azd env set AZURE_SEARCH_REUSE_EXISTING true
azd env set AZURE_SEARCH_SERVICE_RESOURCE_GROUP <resource-group-containing-search>
azd env set AZURE_SEARCH_ENDPOINT https://<search-service>.search.windows.net
```

The app expects the menu index schema created by `app/backend/setup_search_index.py`: `id`, `category`, `name`, `description`, `longDescription`, `origin`, `caffeineContent`, `brewingMethod`, `popularity`, `menuPeriod`, `sizes`, and `embedding`, with semantic configuration `menuSemanticConfig` and vector profile `menuHnswProfile`.

The index names come from each `personas/<id>/persona.json` `search.indexName`. After provisioning RBAC, populate or refresh the indexes with:

```powershell
./scripts/setup_search_index.ps1
```

On macOS or Linux, use `./scripts/setup_search_index.sh`.

## Local development with existing services

For local runs, create `app/backend/.env` or run `scripts/write_env.ps1` after selecting an azd environment. A minimal hand-written `.env` looks like this:

```bash
AZURE_TENANT_ID=<tenant-id>
AZURE_OPENAI_EASTUS2_ENDPOINT=https://<foundry-subdomain>.openai.azure.com
AZURE_OPENAI_REALTIME_DEPLOYMENT=gpt-realtime-2.1
AZURE_AI_FOUNDRY_ENDPOINT=https://<foundry-subdomain>.services.ai.azure.com/models
AZURE_AI_MODEL_DEPLOYMENTS={"gpt-realtime-2.1":"gpt-realtime-2.1","text-embedding-3-large":"text-embedding-3-large","gpt-5-mini":"gpt-5-mini","phi-4":"phi-4","gpt-4o-transcribe":"gpt-4o-transcribe","gpt-4o-mini-tts":"gpt-4o-mini-tts"}
AZURE_OPENAI_REALTIME_VOICE_CHOICE=marin
AZURE_SEARCH_ENDPOINT=https://<search-service>.search.windows.net
AZURE_SEARCH_SEMANTIC_CONFIGURATION=menuSemanticConfig
AZURE_SEARCH_IDENTIFIER_FIELD=id
AZURE_SEARCH_CONTENT_FIELD=description
AZURE_SEARCH_TITLE_FIELD=name
AZURE_SEARCH_EMBEDDING_FIELD=embedding
AZURE_SEARCH_USE_VECTOR_QUERY=true
```

Then follow the [README quick start](../README.md#quick-start). The legacy `AZURE_SEARCH_INDEX` setting is still accepted for fallback paths, but normal persona sessions use the index name from the active persona manifest.
