"""
setup_search_index.py — Headless Azure AI Search ingestion, one index per enabled persona pack.

Issue #84 (P2-15): every enabled persona pack (``personas/<id>/`` -- see ``persona_loader.py``,
issue #70) gets its own Azure AI Search index on the new environment's own Search service
(ADR-001 decision 7 / issue #85): ``sonic-menu-items``, ``mcdonalds-menu-items``,
``dunkin-menu-items``, and so on. The index NAME is never guessed or hardcoded here -- it is read
straight from each pack's ``persona.json`` (``search.indexName``), the same field
``app.py``/``tools.py`` use to build each session's bound-persona ``SearchClient`` (#74). The
document schema is one superset shared by every persona (id/category/name/description/
longDescription/origin/caffeineContent/brewingMethod/popularity/sizes/embedding) -- brands whose
menu items don't populate every optional field simply upload an empty string for it.

For each targeted persona this script: generates embeddings for its menu items using Azure OpenAI
text-embedding-3-large, creates/updates that persona's index with an ``AzureOpenAIVectorizer``
(for query-time ``VectorizableTextQuery``), and uploads documents with stable, sanitized
``category_name``-derived ids via ``merge_or_upload_documents``.

Idempotent: safe to re-run for one persona, a subset, or every enabled persona -- re-running
never leaves stale documents from a previous partial run, and never touches another persona's
index. Uses ``DefaultAzureCredential`` only: no Azure OpenAI or Search key is ever read, issued,
or stored.

Usage:
    python setup_search_index.py                        # every enabled persona (the azd hook's usage)
    python setup_search_index.py --persona sonic         # one persona
    python setup_search_index.py --persona sonic,dunkin  # a subset (comma-separated or repeated --persona)
    python setup_search_index.py --dry-run               # plan only -- no Azure calls, no credential needed
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import re
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass, field

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    AzureOpenAIVectorizer,
    AzureOpenAIVectorizerParameters,
    HnswAlgorithmConfiguration,
    HnswParameters,
    SearchField,
    SearchFieldDataType,
    SearchIndex,
    SemanticConfiguration,
    SemanticField,
    SemanticPrioritizedFields,
    SemanticSearch,
    SimpleField,
    VectorSearch,
    VectorSearchAlgorithmMetric,
    VectorSearchProfile,
)
from dotenv import load_dotenv
from openai import AzureOpenAI
from rich.logging import RichHandler

from persona_loader import Persona, PersonaCatalog

logging.basicConfig(
    level=logging.WARNING,
    format="%(message)s",
    datefmt="[%X]",
    handlers=[RichHandler(rich_tracebacks=True)],
)
logger = logging.getLogger("voicerag")
logger.setLevel(logging.INFO)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
EMBEDDING_MODEL = "text-embedding-3-large"
EMBEDDING_DIMENSIONS = 3072


def load_azd_env():
    """Load the default azd environment file via python-dotenv."""
    result = subprocess.run(
        "azd env list -o json", shell=True, capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError("Error loading azd env")
    env_json = json.loads(result.stdout)
    env_file_path = None
    for entry in env_json:
        if entry["IsDefault"]:
            env_file_path = entry["DotEnvPath"]
    if not env_file_path:
        raise RuntimeError("No default azd env file found")
    logger.info("Loading azd env from %s", env_file_path)
    load_dotenv(env_file_path, override=True)


def sanitize_key(key: str) -> str:
    """Sanitize a document key to contain only valid characters."""
    return re.sub(r"[^a-zA-Z0-9_\-]", "_", key)


def create_or_update_index(
    index_client: SearchIndexClient,
    index_name: str,
    openai_endpoint: str,
    embedding_deployment: str,
) -> None:
    """Create or update the search index with the menu item schema."""
    index = SearchIndex(
        name=index_name,
        fields=[
            SimpleField(
                name="id",
                type=SearchFieldDataType.String,
                key=True,
                sortable=True,
                filterable=True,
            ),
            SearchField(
                name="category",
                type=SearchFieldDataType.String,
                sortable=True,
                filterable=True,
                facetable=True,
            ),
            SearchField(
                name="name",
                type=SearchFieldDataType.String,
                sortable=True,
                filterable=True,
                facetable=True,
            ),
            SearchField(name="description", type=SearchFieldDataType.String),
            SearchField(name="longDescription", type=SearchFieldDataType.String),
            SearchField(
                name="origin",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="caffeineContent",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="brewingMethod",
                type=SearchFieldDataType.String,
                filterable=True,
            ),
            SearchField(
                name="popularity",
                type=SearchFieldDataType.String,
                filterable=True,
                facetable=True,
            ),
            SearchField(
                name="sizes",
                type=SearchFieldDataType.String,
                filterable=False,
                facetable=False,
            ),
            SearchField(
                name="embedding",
                type=SearchFieldDataType.Collection(SearchFieldDataType.Single),
                vector_search_dimensions=EMBEDDING_DIMENSIONS,
                vector_search_profile_name="menuHnswProfile",
            ),
        ],
        vector_search=VectorSearch(
            algorithms=[
                HnswAlgorithmConfiguration(
                    name="menuHnsw",
                    parameters=HnswParameters(
                        metric=VectorSearchAlgorithmMetric.COSINE,
                        m=10,
                        ef_construction=200,
                    ),
                ),
            ],
            profiles=[
                VectorSearchProfile(
                    name="menuHnswProfile",
                    algorithm_configuration_name="menuHnsw",
                    vectorizer_name="menuVectorizer",
                ),
            ],
            vectorizers=[
                AzureOpenAIVectorizer(
                    vectorizer_name="menuVectorizer",
                    parameters=AzureOpenAIVectorizerParameters(
                        resource_url=openai_endpoint,
                        deployment_name=embedding_deployment,
                        model_name=EMBEDDING_MODEL,
                    ),
                ),
            ],
        ),
        semantic_search=SemanticSearch(
            configurations=[
                SemanticConfiguration(
                    name="menuSemanticConfig",
                    prioritized_fields=SemanticPrioritizedFields(
                        title_field=SemanticField(field_name="name"),
                        content_fields=[
                            SemanticField(field_name="description"),
                            SemanticField(field_name="longDescription"),
                            SemanticField(field_name="category"),
                        ],
                    ),
                ),
            ],
        ),
    )

    # create_or_update_index is idempotent — updates schema if index exists
    index_client.create_or_update_index(index)
    logger.info("Index '%s' created or updated successfully", index_name)


def prepare_documents(menu_data: dict) -> tuple[list[dict], list[str]]:
    """Transform menu JSON into search documents and embedding input texts."""
    documents = []
    texts_for_embedding = []

    for category_group in menu_data["menuItems"]:
        category_name = category_group["category"]
        for item in category_group["items"]:
            doc_id = sanitize_key(
                f"{category_name}_{item['name'].replace(' ', '_')}".lower()
            )
            combined_text = (
                f"{category_name} {item['name']} "
                f"{item['description']} {item.get('longDescription', '')}"
            )
            texts_for_embedding.append(combined_text)
            documents.append(
                {
                    "id": doc_id,
                    "category": category_name,
                    "name": item["name"],
                    "description": item["description"],
                    "longDescription": item.get("longDescription", ""),
                    "origin": item.get("origin", ""),
                    "caffeineContent": item.get("caffeineContent", ""),
                    "brewingMethod": item.get("brewingMethod", ""),
                    "popularity": item.get("popularity", ""),
                    "sizes": json.dumps(item["sizes"]),
                }
            )

    return documents, texts_for_embedding


def generate_embeddings(
    openai_client: AzureOpenAI, texts: list[str], deployment: str
) -> list[list[float]]:
    """Generate embeddings in batch using Azure OpenAI."""
    response = openai_client.embeddings.create(input=texts, model=deployment)
    return [item.embedding for item in response.data]


def upload_documents(
    search_client: SearchClient, documents: list[dict]
) -> None:
    """Upload documents to the search index using merge_or_upload (idempotent)."""
    batch_size = 100
    for i in range(0, len(documents), batch_size):
        batch = documents[i : i + batch_size]
        result = search_client.merge_or_upload_documents(batch)
        succeeded = sum(1 for r in result if r.succeeded)
        logger.info(
            "Uploaded batch %d-%d: %d/%d succeeded",
            i,
            i + len(batch),
            succeeded,
            len(batch),
        )


@dataclass
class PersonaIngestPlan:
    """What would be (or, outside ``--dry-run``, is being) ingested for one persona."""

    persona_id: str
    index_name: str
    documents: list[dict] = field(repr=False)
    texts_for_embedding: list[str] = field(repr=False)

    @property
    def document_count(self) -> int:
        return len(self.documents)


def resolve_target_personas(catalog: PersonaCatalog, requested: Sequence[str] | None) -> list[Persona]:
    """Resolve which of *catalog*'s enabled personas this run should ingest.

    *requested* (``--persona``, repeatable and/or comma-separated): a subset of the enabled
    persona ids. Omitted/empty (the default): every enabled persona in *catalog*, so a plain
    re-run of this script -- the azd ``postprovision`` hook's usage, and both
    ``scripts/setup_search_index.ps1``/``.sh`` -- always builds one index per enabled persona
    with no separate persona loop needed at the shell-script layer.

    Raises ``SystemExit`` naming any requested id that isn't an enabled persona, so a typo in
    ``--persona`` fails fast instead of silently ingesting nothing for it.
    """
    if not requested:
        return [catalog.get(pid) for pid in catalog.ids]

    ids: list[str] = []
    for item in requested:
        ids.extend(p.strip() for p in item.split(",") if p.strip())

    unknown = [pid for pid in ids if pid not in catalog]
    if unknown:
        raise SystemExit(
            f"--persona named {unknown} -- not in the enabled persona catalog ({', '.join(catalog.ids) or '(none)'})"
        )
    return [catalog.get(pid) for pid in ids]


def build_plan(persona: Persona) -> PersonaIngestPlan:
    """Load *persona*'s menu data and turn it into a :class:`PersonaIngestPlan`.

    Read-only: never touches Azure. Safe to call for every target persona before deciding
    whether this is a dry run.
    """
    menu_path = persona.menu_path
    if not menu_path.exists():
        logger.critical("Persona '%s': menu data file not found at %s", persona.id, menu_path)
        sys.exit(1)
    with menu_path.open(encoding="utf-8") as f:
        menu_data = json.load(f)
    documents, texts_for_embedding = prepare_documents(menu_data)
    return PersonaIngestPlan(
        persona_id=persona.id,
        index_name=persona.manifest.search.indexName,
        documents=documents,
        texts_for_embedding=texts_for_embedding,
    )


def ingest_plan(
    plan: PersonaIngestPlan,
    *,
    index_client: SearchIndexClient,
    openai_client: AzureOpenAI,
    search_endpoint: str,
    openai_endpoint: str,
    embedding_deployment: str,
    credential,
) -> None:
    """Create/update *plan*'s index and merge-or-upload its documents (idempotent)."""
    logger.info(
        "Persona '%s': setting up Azure AI Search index '%s'...", plan.persona_id, plan.index_name
    )
    create_or_update_index(index_client, plan.index_name, openai_endpoint, embedding_deployment)

    logger.info(
        "Persona '%s': generating embeddings for %d document(s)...",
        plan.persona_id,
        plan.document_count,
    )
    embeddings = generate_embeddings(openai_client, plan.texts_for_embedding, embedding_deployment)
    for doc, emb in zip(plan.documents, embeddings):
        doc["embedding"] = emb

    search_client = SearchClient(
        search_endpoint, plan.index_name, credential, user_agent="setup_search_index"
    )
    upload_documents(search_client, plan.documents)
    logger.info("Persona '%s': search index setup complete.", plan.persona_id)


def run(
    catalog: PersonaCatalog,
    requested: Sequence[str] | None,
    *,
    dry_run: bool,
) -> list[PersonaIngestPlan]:
    """Build (or, in dry-run mode, plan) one index per targeted persona.

    Returns the plans that were built, for callers (including tests) to assert on. In dry-run
    mode this makes zero Azure calls and needs no credential -- only the persona packs on disk
    are read.
    """
    plans = [build_plan(p) for p in resolve_target_personas(catalog, requested)]

    if dry_run:
        for plan in plans:
            logger.info(
                "[dry-run] persona '%s': index '%s', %d document(s) planned",
                plan.persona_id,
                plan.index_name,
                plan.document_count,
            )
        return plans

    search_endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]
    openai_endpoint = os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]
    embedding_deployment = os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", EMBEDDING_MODEL)

    # Authenticate with DefaultAzureCredential only (works with azd auth + managed identity) --
    # no Azure OpenAI or Search key is ever read, issued, or stored.
    credential = DefaultAzureCredential()
    token_provider = get_bearer_token_provider(credential, "https://cognitiveservices.azure.com/.default")
    index_client = SearchIndexClient(search_endpoint, credential)
    openai_client = AzureOpenAI(
        azure_ad_token_provider=token_provider,
        api_version="2024-06-01",
        azure_endpoint=openai_endpoint,
    )

    for plan in plans:
        ingest_plan(
            plan,
            index_client=index_client,
            openai_client=openai_client,
            search_endpoint=search_endpoint,
            openai_endpoint=openai_endpoint,
            embedding_deployment=embedding_deployment,
            credential=credential,
        )

    return plans


def _build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Build one Azure AI Search index per enabled persona pack (issue #84). "
            "Index names come from each pack's persona.json (search.indexName)."
        )
    )
    parser.add_argument(
        "--persona",
        action="append",
        default=None,
        metavar="ID[,ID...]",
        help=(
            "Restrict ingestion to these persona id(s) -- repeatable and/or comma-separated. "
            "Default: every persona PersonaCatalog.load() enables (PERSONAS env var, or every "
            "pack found under PERSONAS_DIR when PERSONAS is unset)."
        ),
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help=(
            "Print the planned index name and document count for each targeted persona and "
            "exit -- no Azure Search/OpenAI calls, no credential required, no azd env loaded."
        ),
    )
    parser.add_argument(
        "--personas-dir",
        default=None,
        metavar="PATH",
        help="Override PERSONAS_DIR (mainly for local testing against a fixture pack directory).",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> None:
    args = _build_arg_parser().parse_args(argv)

    if not args.dry_run:
        load_azd_env()
        # A live run may opt out of touching Azure at all (e.g. AZURE_SEARCH_REUSE_EXISTING plus
        # indexes a prior run already populated). This is a manual escape hatch, never set
        # automatically by infra -- AZURE_SEARCH_REUSE_EXISTING itself is a Bicep-only flag ("don't
        # provision a new Search *service*") and must NOT imply skipping index setup, since every
        # enabled persona still needs its own index created on whichever service is in play.
        if os.environ.get("AZURE_SEARCH_SKIP_INDEX_SETUP") == "true":
            logger.info("AZURE_SEARCH_SKIP_INDEX_SETUP is set — leaving every persona's index untouched.")
            return

    catalog = PersonaCatalog.load(personas_dir=args.personas_dir)
    run(catalog, args.persona, dry_run=args.dry_run)


if __name__ == "__main__":
    main()
