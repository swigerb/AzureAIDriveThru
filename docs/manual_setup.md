# Manual setup guide

Prefer `azd up` for repeatable deployments. Use this guide only when you need to wire Microsoft Foundry AI Drive Thru to resources that were created outside the template.

## Required Azure resources

1. A Microsoft Foundry or Azure AI services account of kind `AIServices`. It must have the deployments listed in `infra/model-deployments.json` for realtime, embeddings, cascade STT, reasoning, and TTS. See [Deploy models in Azure AI Foundry](https://learn.microsoft.com/azure/ai-foundry/how-to/deploy-models-openai).
2. An Azure AI Search service, Basic tier or higher for the standard persona set. The app uses vector search and semantic configuration `menuSemanticConfig`. See [Create an Azure AI Search service](https://learn.microsoft.com/azure/search/search-create-service-portal).
3. Azure Container Apps, Azure Container Registry, Log Analytics, Storage, and a user-assigned managed identity if you are matching the production topology. The Bicep modules in `infra/` show the exact environment variables, secrets, RBAC roles, and local-auth settings.
4. A Microsoft Entra app registration for production auth. Use `scripts/Setup-EntraAuth.ps1` rather than creating it by hand when possible.

## Search indexes

The current app does not use a generic document RAG index. It uses one menu index per enabled persona. Index names are defined in `personas/<id>/persona.json` under `search.indexName`.

The standard schema is created by `app/backend/setup_search_index.py` and includes:

- Text fields: `id`, `category`, `name`, `description`, `longDescription`, `origin`, `caffeineContent`, `brewingMethod`, `popularity`, `menuPeriod`, and `sizes`.
- Vector field: `embedding` with 3072 dimensions from `text-embedding-3-large`.
- Semantic configuration: `menuSemanticConfig`.
- Vector profile: `menuHnswProfile` with an Azure OpenAI vectorizer.

To create or refresh every enabled persona index after your environment variables and RBAC are set, run:

```powershell
./scripts/setup_search_index.ps1
```

On macOS or Linux, run:

```bash
./scripts/setup_search_index.sh
```

For a dry run with no Azure calls:

```powershell
python app/backend/setup_search_index.py --dry-run
```

## Backend environment

At minimum, the Python backend needs these settings for local development with existing resources:

```bash
AZURE_TENANT_ID=<tenant-id>
AZURE_OPENAI_EASTUS2_ENDPOINT=https://<foundry-subdomain>.openai.azure.com
AZURE_AI_FOUNDRY_ENDPOINT=https://<foundry-subdomain>.services.ai.azure.com/models
AZURE_OPENAI_REALTIME_DEPLOYMENT=gpt-realtime-2.1
AZURE_AI_MODEL_DEPLOYMENTS={"gpt-realtime-2.1":"gpt-realtime-2.1","text-embedding-3-large":"text-embedding-3-large","gpt-5-mini":"gpt-5-mini","phi-4":"phi-4","gpt-4o-transcribe":"gpt-4o-transcribe","gpt-4o-mini-tts":"gpt-4o-mini-tts"}
AZURE_SEARCH_ENDPOINT=https://<search-service>.search.windows.net
AZURE_SEARCH_SEMANTIC_CONFIGURATION=menuSemanticConfig
AZURE_SEARCH_IDENTIFIER_FIELD=id
AZURE_SEARCH_CONTENT_FIELD=description
AZURE_SEARCH_TITLE_FIELD=name
AZURE_SEARCH_EMBEDDING_FIELD=embedding
AZURE_SEARCH_USE_VECTOR_QUERY=true
```

For production, let `azd` and `infra/main.bicep` set the Container Apps environment. They also set `AUTH_MODE=Entra`, `RUNNING_IN_PRODUCTION=true`, `APP_SESSION_SECRET`, `AZURE_CLIENT_ID`, and the Entra settings required by the API.

## Validation

- Run `python app/backend/setup_search_index.py --dry-run` to verify persona discovery and index plans.
- Run `python scripts/smoke_realtime.py --deployment gpt-realtime-2.1` to verify the realtime deployment accepts the session configuration.
- Run `./scripts/Verify-ProductionAuth.ps1` after production auth is configured and the app is public.
