"""Capture the exact Azure AI Search REST request bodies setup_search_index.py would send.

Issue #16's "client-seam + recorded-fixture" design note (docs/dotnet_tooling.md): this is NOT a
new dev tool and is never invoked by a human or by CI outside this test project. It exists only so
tools/dotnet/tests/SearchIndexRequestBuilder.Tests/PythonParityTests.cs can compare the REAL,
unmodified app/backend/setup_search_index.py against SearchIndexRequestBuilder's independent C#
reconstruction of the same request bodies.

How it avoids a live Azure/OpenAI call entirely:

* The index-definition PUT (`create_or_update_index`) and the document-upload-batch POSTs
  (`upload_documents` -> `merge_or_upload_documents`) are both intercepted at the azure-core HTTP
  transport layer: `SearchIndexClient`/`SearchClient` are constructed with a custom `transport=`
  (RecordingTransport below) whose `send()` never touches a socket. It records the outbound
  request's JSON body, then fabricates a minimal, schema-correct 200 response so the SDK's own
  response deserialization succeeds and execution continues normally (needed so `upload_documents`'s
  internal 100-document batching loop runs to completion across every batch, not just the first).
* `generate_embeddings`'s real `openai_client.embeddings.create(...)` call is never reached at all:
  this harness calls `build_plan`/`prepare_documents` directly (the real, read-only functions) and
  attaches FIXTURE_EMBEDDING(text) in its place, exactly where `ingest_plan` would normally zip in
  real embeddings. FIXTURE_EMBEDDING's formula must stay byte-for-byte identical to
  SearchIndexRequestBuilder/FixtureEmbedding.cs -- see the comment on EMBEDDING_FIXTURE_DIMENSIONS.
* FAKE_OPENAI_ENDPOINT/FAKE_EMBEDDING_DEPLOYMENT must stay identical to
  SearchIndexRequestPlanner.FakeOpenAiEndpoint/FakeEmbeddingDeployment (C#): both flow directly into
  the index definition's vectorizer fields, so a mismatch would make the parity test fail for a
  reason that has nothing to do with the port's own correctness.

Targets every enabled persona (`PersonaCatalog`-style default: every personas/*/persona.json
folder, sorted), matching setup_search_index.py's own default `run()` behaviour with no
`--persona` -- never a single hardcoded persona id.

Prints one JSON object to stdout: {"personas": {"<id>": {"index_name", "document_count",
"index_definition", "document_batches"}, ...}}.
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

# setup_search_index.py configures a RichHandler at import time (its own module-level
# logging.basicConfig) that writes INFO-level progress ("Uploaded batch N-M...", "Index ... created
# or updated successfully") straight to stdout -- which would otherwise interleave with, and
# corrupt, the single JSON object this harness prints to stdout for the C# test to parse. This
# harness only cares about the captured request bodies, never the twin's own log output, so every
# logger is silenced unconditionally (not just "voicerag") right after import.
logging.disable(logging.CRITICAL)

from azure.core.credentials import AzureKeyCredential  # noqa: E402
from azure.core.pipeline.transport import HttpResponse, HttpTransport  # noqa: E402
from azure.search.documents import SearchClient  # noqa: E402
from azure.search.documents.indexes import SearchIndexClient  # noqa: E402

FAKE_SEARCH_ENDPOINT = "https://fake.search.windows.net"
FAKE_OPENAI_ENDPOINT = "https://fake.openai.azure.com"
FAKE_EMBEDDING_DEPLOYMENT = "fake-embedding-deployment"

# Must match SearchIndexRequestBuilder/FixtureEmbedding.cs's Dimensions constant exactly.
EMBEDDING_FIXTURE_DIMENSIONS = 8


def fixture_embedding(text: str) -> list[float]:
    """sha256(text)'s first EMBEDDING_FIXTURE_DIMENSIONS bytes, each mapped from [0, 255] to
    [-1, 1] and rounded to 6 decimal places via fixed-point text formatting (not a raw float
    round), so both this harness and FixtureEmbedding.cs land on the exact same IEEE-754 value --
    see FixtureEmbedding.cs's own remarks for why this is a deliberately small, non-real
    embedding."""
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
    """Captures every outbound request's JSON body without making any real network call."""

    def __init__(self):
        self.captured: list[dict] = []

    def send(self, request, **kwargs):
        body = request.body
        if isinstance(body, (bytes, bytearray)):
            body = body.decode("utf-8")
        parsed = json.loads(body) if body else None
        self.captured.append({"method": request.method, "url": request.url, "body": parsed})

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
    their exact on-wire JSON bodies."""
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
        "index_definition": index_transport.captured[0]["body"],
        "document_batches": [c["body"]["value"] for c in docs_transport.captured],
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
