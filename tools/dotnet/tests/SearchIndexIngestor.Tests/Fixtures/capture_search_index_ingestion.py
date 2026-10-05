"""Capture the exact Azure AI Search REST requests setup_search_index.py's `ingest_plan` sends for
its index-create-or-update and document-upload-batch steps -- method, URL (incl. index name and
api-version), selected headers, and bodies.

Issue #16's "client-seam + recorded-fixture" design note (docs/dotnet_tooling.md), extended from
Fixtures/capture_search_index_requests.py's own pattern (SearchIndexRequestBuilder.Tests): this is
NOT a new dev tool and is never invoked by a human or by CI outside this test project. It exists
only so tools/dotnet/tests/SearchIndexIngestor.Tests/PythonParityTests.cs can compare the REAL,
unmodified app/backend/setup_search_index.py's actual on-wire REST calls against
SearchIndexIngestor's independent C# reconstruction of the same requests.

Unlike the sibling SearchIndexRequestBuilder.Tests harness (which only ever needed to compare JSON
BODIES, since SearchIndexRequestBuilder itself never opens a socket), this harness ALSO records
method/url/selected headers, because SearchIndexIngestor's SearchIndexHttpClient is a genuine raw
HttpClient that really does issue these two calls over HTTP in production -- so this port's own
method/URL-shape/header correctness is just as much in scope here as its body-building.

Captures ONLY the two calls this task's test-requirement enumeration names for strict parity
("index create-or-update and the upload-batch loop") -- NOT delete_stale_documents/
verify_document_count (unit-tested separately in SearchIndexHttpClientTests.cs/
SearchIndexOrchestratorTests.cs against a fake HTTP handler instead, since those two are about real
index STATE/pagination, not a deterministic request-building concern a captured fixture could
usefully pin down).

Prints one JSON object to stdout: {"personas": {"<id>": {"index_name", "document_count",
"index_request": {"method", "url", "headers", "body"},
"upload_batch_requests": [{"method", "url", "headers", "body"}, ...]}, ...}}.
"""

from __future__ import annotations

import hashlib
import json
import logging
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(REPO_ROOT / "app" / "backend"))

import setup_search_index as sut  # noqa: E402  (sys.path must be set up first)

# See capture_search_index_requests.py's own identical comment: setup_search_index.py configures a
# RichHandler at import time that would otherwise interleave its own INFO-level progress output
# with the single JSON object this harness prints to stdout.
logging.disable(logging.CRITICAL)

from azure.core.credentials import AzureKeyCredential  # noqa: E402
from azure.core.pipeline.transport import HttpResponse, HttpTransport  # noqa: E402
from azure.search.documents import SearchClient  # noqa: E402
from azure.search.documents.indexes import SearchIndexClient  # noqa: E402

# Must stay string-identical to SearchIndexIngestor.Tests/TestFixtureValues.cs's own
# FakeSearchEndpoint/FakeOpenAiEndpoint/FakeEmbeddingDeployment constants -- both sides' captured/
# reconstructed requests embed these values directly (the request URL's host, and the index
# definition's vectorizer fields).
FAKE_SEARCH_ENDPOINT = "https://fake.search.windows.net"
FAKE_OPENAI_ENDPOINT = "https://fake.openai.azure.com"
FAKE_EMBEDDING_DEPLOYMENT = "fake-embedding-deployment"

# Must match SearchIndexIngestor.Tests/FixtureEmbeddingClient.cs's Dimensions constant exactly.
EMBEDDING_FIXTURE_DIMENSIONS = 8

# Headers that matter for this port's own correctness (auth/credential headers are deliberately
# excluded -- this harness uses an AzureKeyCredential fake key, production uses a DefaultAzureCredential
# bearer token; neither value nor presence of an Authorization-style header is a meaningful
# comparison point here, see PythonParityTests.cs's own remarks).
HEADERS_THAT_MATTER = ("accept", "prefer", "content-type")


def fixture_embedding(text: str) -> list[float]:
    """Identical formula to SearchIndexIngestor.Tests/FixtureEmbeddingClient.cs's For(text) -- see
    that file's own remarks for why this is a deliberately small, non-real embedding."""
    digest = hashlib.sha256(text.encode("utf-8")).digest()
    return [float(f"{(digest[i] / 255.0) * 2 - 1:.6f}") for i in range(EMBEDDING_FIXTURE_DIMENSIONS)]


class _FakeHttpResponse(HttpResponse):
    """A minimal, schema-correct 200 response so the real SDK's own deserialization succeeds and
    `upload_documents`'s batching loop keeps running across every batch."""

    def __init__(self, request, body_bytes, status_code=200):
        super().__init__(request, None)
        self.status_code = status_code
        self.headers = {"content-type": "application/json; charset=utf-8"}
        self.reason = "OK"
        self.content_type = "application/json; charset=utf-8"
        self._body_bytes = body_bytes

    def body(self):
        return self._body_bytes

    def json(self):
        return json.loads(self._body_bytes)


class RecordingTransport(HttpTransport):
    """Captures every outbound request's method/url/selected headers/body without making any real
    network call."""

    def __init__(self):
        self.captured: list[dict] = []

    def send(self, request, **kwargs):
        body = request.body
        if isinstance(body, (bytes, bytearray)):
            body = body.decode("utf-8")
        parsed = json.loads(body) if body else None
        headers = {
            name: value
            for name, value in request.headers.items()
            if name.lower() in HEADERS_THAT_MATTER
        }
        self.captured.append(
            {"method": request.method, "url": request.url, "headers": headers, "body": parsed}
        )

        if "/docs/search.index" in request.url:
            # IndexDocumentsResult: one IndexingResult per document in the batch, keyed by id.
            results = [
                {"key": doc["id"], "status": True, "errorMessage": None, "statusCode": 200}
                for doc in parsed["value"]
            ]
            response_body = json.dumps({"value": results}).encode("utf-8")
        else:
            # create_or_update_index: Azure AI Search echoes the created/updated index back.
            response_body = json.dumps(parsed).encode("utf-8")
        return _FakeHttpResponse(request, response_body)

    def open(self):
        pass

    def close(self):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass


def capture_persona(persona) -> dict:
    """Builds `persona`'s plan via the REAL, read-only build_plan (no Azure call), attaches the
    fixture embedding in place of a live Azure OpenAI call, then drives the REAL
    create_or_update_index/upload_documents against RecordingTransport-backed clients to capture
    their exact on-wire method/url/headers/body."""
    plan = sut.build_plan(persona)

    index_transport = RecordingTransport()
    index_client = SearchIndexClient(
        FAKE_SEARCH_ENDPOINT, AzureKeyCredential("fake-key"), transport=index_transport
    )
    sut.create_or_update_index(index_client, plan.index_name, FAKE_OPENAI_ENDPOINT, FAKE_EMBEDDING_DEPLOYMENT)
    assert len(index_transport.captured) == 1, (
        f"expected exactly one create_or_update_index request, got {len(index_transport.captured)}"
    )

    for doc, text in zip(plan.documents, plan.texts_for_embedding):
        doc["embedding"] = fixture_embedding(text)

    docs_transport = RecordingTransport()
    search_client = SearchClient(
        FAKE_SEARCH_ENDPOINT, plan.index_name, AzureKeyCredential("fake-key"), transport=docs_transport
    )
    sut.upload_documents(search_client, plan.documents)

    return {
        "index_name": plan.index_name,
        "document_count": plan.document_count,
        "index_request": index_transport.captured[0],
        "upload_batch_requests": docs_transport.captured,
    }


def main() -> None:
    # Mirrors persona_loader.PersonaCatalog.load()'s default enabled_ids resolution: every folder
    # directly under personas/ that has a persona.json, sorted -- never a hardcoded persona id, and
    # never just one persona (discovered purely from the data on disk).
    personas_dir = REPO_ROOT / "personas"
    persona_ids = sorted(
        p.name for p in personas_dir.iterdir() if p.is_dir() and (p / "persona.json").is_file()
    )

    catalog = sut.PersonaCatalog.load(personas_dir=personas_dir, enabled=persona_ids)
    result = {"personas": {pid: capture_persona(catalog.get(pid)) for pid in persona_ids}}
    print(json.dumps(result))


if __name__ == "__main__":
    main()
