"""Tests for setup_search_index.py (issue #84, P2-15: ingestion and search per persona).

Covers:
  - ``resolve_target_personas``: defaults to every enabled persona, accepts an explicit subset
    (repeated ``--persona`` and/or comma-separated), and fails fast on an unknown id.
  - ``build_plan``: index name comes from the pack's own ``persona.json`` (``search.indexName``),
    never guessed/hardcoded; document ids are stable across repeated calls (idempotent by
    construction, since the plan is pure data derived from the pack).
  - ``run()`` in ``--dry-run`` mode makes zero Azure Search/OpenAI calls and needs no credential.
  - ``run()`` in live mode builds exactly one index per targeted persona, using a fake/mocked
    Search + OpenAI client -- never a real network call -- and asserts each persona's documents
    land in ITS OWN index, not a shared/default one.
  - Re-running ``run()`` (idempotency) issues the same document ids and the same
    create-or-update-index call both times, never a delete/recreate.

These tests never touch a real Azure resource: ``azure.identity``/``azure.search.documents``/
``openai`` clients are patched to in-process fakes for every non-dry-run test.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import setup_search_index as ssi  # noqa: E402
from persona_loader import PersonaCatalog  # noqa: E402

_FIXTURES_PERSONAS_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"
_REAL_PERSONAS_DIR = Path(__file__).resolve().parents[3] / "personas"


def _fixture_catalog(enabled=None) -> PersonaCatalog:
    return PersonaCatalog.load(
        personas_dir=_FIXTURES_PERSONAS_DIR,
        enabled=enabled or ["test-alpha", "test-beta"],
        default_persona_id="test-alpha",
    )


# ===========================================================================
# resolve_target_personas
# ===========================================================================


class ResolveTargetPersonasTests(unittest.TestCase):
    def test_no_request_returns_every_enabled_persona(self):
        catalog = _fixture_catalog()
        personas = ssi.resolve_target_personas(catalog, None)
        self.assertEqual(sorted(p.id for p in personas), ["test-alpha", "test-beta"])

    def test_explicit_single_persona(self):
        catalog = _fixture_catalog()
        personas = ssi.resolve_target_personas(catalog, ["test-alpha"])
        self.assertEqual([p.id for p in personas], ["test-alpha"])

    def test_comma_separated_persona_list(self):
        catalog = _fixture_catalog()
        personas = ssi.resolve_target_personas(catalog, ["test-alpha,test-beta"])
        self.assertEqual([p.id for p in personas], ["test-alpha", "test-beta"])

    def test_repeated_persona_flag(self):
        """argparse's action='append' collects repeated --persona into a list of strings --
        each individually may also be comma-separated."""
        catalog = _fixture_catalog()
        personas = ssi.resolve_target_personas(catalog, ["test-alpha", "test-beta"])
        self.assertEqual([p.id for p in personas], ["test-alpha", "test-beta"])

    def test_unknown_persona_raises_system_exit_naming_it(self):
        catalog = _fixture_catalog()
        with self.assertRaises(SystemExit) as exc_info:
            ssi.resolve_target_personas(catalog, ["not-a-real-persona"])
        message = str(exc_info.exception)
        self.assertIn("not-a-real-persona", message)


# ===========================================================================
# build_plan
# ===========================================================================


class BuildPlanTests(unittest.TestCase):
    def test_index_name_comes_from_the_pack_not_hardcoded(self):
        catalog = _fixture_catalog()
        plan = ssi.build_plan(catalog.get("test-alpha"))
        self.assertEqual(plan.index_name, "test-alpha-menu-items")
        plan_beta = ssi.build_plan(catalog.get("test-beta"))
        self.assertEqual(plan_beta.index_name, "test-beta-menu-items")

    def test_document_count_matches_menu_item_count(self):
        catalog = _fixture_catalog()
        plan = ssi.build_plan(catalog.get("test-alpha"))
        # fixtures/personas/test-alpha/menu/menuItems.json: 2 drinks + 1 main = 3 items.
        self.assertEqual(plan.document_count, 3)
        self.assertEqual(len(plan.texts_for_embedding), 3)

    def test_document_ids_are_stable_across_repeated_builds(self):
        """Re-running ingestion for the same pack must produce the exact same document ids
        every time (merge_or_upload keys on 'id') -- never a fresh random id per run."""
        catalog = _fixture_catalog()
        persona = catalog.get("test-alpha")
        first_ids = sorted(d["id"] for d in ssi.build_plan(persona).documents)
        second_ids = sorted(d["id"] for d in ssi.build_plan(persona).documents)
        self.assertEqual(first_ids, second_ids)
        self.assertEqual(len(first_ids), len(set(first_ids)), "document ids must be unique")

    def test_two_personas_never_share_a_document_id_namespace_assumption(self):
        """Not a hard requirement (each persona has its own index), but documents for two
        different personas built in the same run must not accidentally collide if ever
        uploaded to the same index by a future bug -- confirms today's id scheme already
        differs by category/name, which differs by pack."""
        catalog = _fixture_catalog()
        alpha_ids = {d["id"] for d in ssi.build_plan(catalog.get("test-alpha")).documents}
        beta_ids = {d["id"] for d in ssi.build_plan(catalog.get("test-beta")).documents}
        self.assertEqual(alpha_ids & beta_ids, set())

    def test_real_default_pack_builds_a_plan(self):
        """Sanity check against the real, non-fixture default pack (never mutated by this test)."""
        catalog = PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR)
        persona = catalog.get(catalog.default_persona_id)
        plan = ssi.build_plan(persona)
        self.assertEqual(plan.index_name, persona.manifest.search.indexName)
        self.assertGreater(plan.document_count, 0)


# ===========================================================================
# run() -- dry-run mode: zero Azure calls
# ===========================================================================


class DryRunTests(unittest.TestCase):
    def test_dry_run_returns_plans_for_every_enabled_persona(self):
        catalog = _fixture_catalog()
        plans = ssi.run(catalog, None, dry_run=True)
        self.assertEqual(sorted(p.persona_id for p in plans), ["test-alpha", "test-beta"])

    def test_dry_run_restricts_to_requested_persona(self):
        catalog = _fixture_catalog()
        plans = ssi.run(catalog, ["test-beta"], dry_run=True)
        self.assertEqual([p.persona_id for p in plans], ["test-beta"])
        self.assertEqual(plans[0].index_name, "test-beta-menu-items")

    def test_dry_run_never_constructs_a_credential_or_azure_client(self):
        """The whole point of --dry-run: it must be safe to run with no Azure sign-in at all."""
        catalog = _fixture_catalog()
        with (
            patch.object(ssi, "DefaultAzureCredential", side_effect=AssertionError("must not run")),
            patch.object(ssi, "SearchIndexClient", side_effect=AssertionError("must not run")),
            patch.object(ssi, "SearchClient", side_effect=AssertionError("must not run")),
            patch.object(ssi, "AzureOpenAI", side_effect=AssertionError("must not run")),
        ):
            plans = ssi.run(catalog, None, dry_run=True)
        self.assertEqual(len(plans), 2)


# ===========================================================================
# run() -- live mode with a fake/mocked Search + OpenAI client
# ===========================================================================


class _FakeSearchResult:
    def __init__(self, succeeded: bool = True):
        self.succeeded = succeeded


class LiveRunWithFakeClientsTests(unittest.TestCase):
    """Exercises the real create-index / embed / upload call sequence end to end, but every
    Azure SDK class is a mock -- no network call, no key, no real credential."""

    def setUp(self):
        # patch.stopall() unwinds every patch.*(...).start() below, regardless of type
        # (patch.dict included) -- registered once, up front, so setUp can just keep calling
        # .start() without juggling individual .stop() handles.
        self.addCleanup(patch.stopall)

        patch.dict(
            ssi.os.environ,
            {
                "AZURE_SEARCH_ENDPOINT": "https://fake-search.search.windows.net",
                "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake-openai.openai.azure.com",
                "AZURE_OPENAI_EMBEDDING_DEPLOYMENT": "text-embedding-3-large",
            },
        ).start()

        self.mock_credential_cls = patch.object(ssi, "DefaultAzureCredential").start()
        patch.object(ssi, "get_bearer_token_provider", return_value=lambda: "fake-token").start()

        self.index_client = MagicMock(name="SearchIndexClient instance")
        self.mock_index_client_cls = patch.object(
            ssi, "SearchIndexClient", return_value=self.index_client
        ).start()

        # search_client_factory: a fresh MagicMock per SearchClient(...) call, keyed by the
        # index_name argument, so assertions can look up "the client used for persona X's index".
        self.search_clients_by_index: dict[str, MagicMock] = {}

        # Each fake index keeps its document ids across SearchClient instances (and runs), so
        # stale-document deletion and the post-ingest count check behave like a real index.
        self.index_contents: dict[str, set[str]] = {}

        def _make_search_client(endpoint, index_name, credential, **kwargs):
            contents = self.index_contents.setdefault(index_name, set())
            client = MagicMock(name=f"SearchClient[{index_name}]")

            def _merge_or_upload(batch):
                contents.update(doc["id"] for doc in batch)
                return [_FakeSearchResult(True) for _ in batch]

            def _delete(batch):
                contents.difference_update(doc["id"] for doc in batch)
                return [_FakeSearchResult(True) for _ in batch]

            client.merge_or_upload_documents.side_effect = _merge_or_upload
            client.delete_documents.side_effect = _delete
            client.search.side_effect = lambda **kwargs: [{"id": doc_id} for doc_id in sorted(contents)]
            client.get_document_count.side_effect = lambda: len(contents)
            self.search_clients_by_index[index_name] = client
            return client

        self.mock_search_client_cls = patch.object(
            ssi, "SearchClient", side_effect=_make_search_client
        ).start()

        self.openai_client = MagicMock(name="AzureOpenAI instance")

        def _fake_embeddings_create(input, model):  # noqa: A002 (matches openai SDK's kwarg name)
            response = MagicMock()
            response.data = [MagicMock(embedding=[0.0] * ssi.EMBEDDING_DIMENSIONS) for _ in input]
            return response

        self.openai_client.embeddings.create.side_effect = _fake_embeddings_create
        patch.object(ssi, "AzureOpenAI", return_value=self.openai_client).start()

    def test_builds_one_index_per_persona_named_from_its_own_pack(self):
        catalog = _fixture_catalog()
        ssi.run(catalog, None, dry_run=False)

        created_index_names = [
            call.args[0].name for call in self.index_client.create_or_update_index.call_args_list
        ]
        self.assertEqual(sorted(created_index_names), ["test-alpha-menu-items", "test-beta-menu-items"])

    def test_each_persona_uploads_only_to_its_own_index(self):
        catalog = _fixture_catalog()
        ssi.run(catalog, None, dry_run=False)

        self.assertEqual(set(self.search_clients_by_index), {"test-alpha-menu-items", "test-beta-menu-items"})
        alpha_client = self.search_clients_by_index["test-alpha-menu-items"]
        beta_client = self.search_clients_by_index["test-beta-menu-items"]
        alpha_client.merge_or_upload_documents.assert_called_once()
        beta_client.merge_or_upload_documents.assert_called_once()

        alpha_docs = alpha_client.merge_or_upload_documents.call_args.args[0]
        beta_docs = beta_client.merge_or_upload_documents.call_args.args[0]
        self.assertEqual(len(alpha_docs), 3)
        self.assertEqual(len(beta_docs), 3)
        self.assertEqual({d["name"] for d in alpha_docs}, {"Alpha Cola", "Alpha Flavor Shot", "Alpha Burger"})
        self.assertEqual({d["name"] for d in beta_docs}, {"Beta Root Beer", "Beta Double Burger", "Beta Cheese Sauce"})

    def test_documents_carry_embeddings_of_the_expected_dimension(self):
        catalog = _fixture_catalog(["test-alpha"])
        ssi.run(catalog, None, dry_run=False)
        docs = self.search_clients_by_index["test-alpha-menu-items"].merge_or_upload_documents.call_args.args[0]
        for doc in docs:
            self.assertIn("embedding", doc)
            self.assertEqual(len(doc["embedding"]), ssi.EMBEDDING_DIMENSIONS)

    def test_restricting_to_one_persona_never_touches_the_others_index(self):
        catalog = _fixture_catalog()
        ssi.run(catalog, ["test-beta"], dry_run=False)
        self.assertEqual(set(self.search_clients_by_index), {"test-beta-menu-items"})

    def test_upload_uses_merge_or_upload_never_a_destructive_replace(self):
        """merge_or_upload_documents is what makes re-running idempotent -- a plain
        upload_documents (full replace) would leave orphaned old docs after a menu item is
        removed from the pack. Guards against ever swapping the call back to that."""
        catalog = _fixture_catalog(["test-alpha"])
        ssi.run(catalog, None, dry_run=False)
        client = self.search_clients_by_index["test-alpha-menu-items"]
        client.merge_or_upload_documents.assert_called_once()
        client.upload_documents.assert_not_called()

    def test_rerunning_is_idempotent_same_ids_same_index_calls(self):
        catalog = _fixture_catalog(["test-alpha"])
        ssi.run(catalog, None, dry_run=False)
        first_ids = sorted(
            d["id"] for d in self.search_clients_by_index["test-alpha-menu-items"].merge_or_upload_documents.call_args.args[0]
        )

        ssi.run(catalog, None, dry_run=False)
        second_ids = sorted(
            d["id"] for d in self.search_clients_by_index["test-alpha-menu-items"].merge_or_upload_documents.call_args.args[0]
        )

        self.assertEqual(first_ids, second_ids)
        # create_or_update_index (never a delete) was called once per run == 2 total.
        self.assertEqual(self.index_client.create_or_update_index.call_count, 2)


    def test_stale_documents_are_deleted_and_other_indexes_untouched(self):
        """An item removed from a pack's menu must leave its index on the next run (#84): it
        would otherwise stay searchable and the carhop would offer a not_on_menu item."""
        self.index_contents["test-alpha-menu-items"] = {"stale_removed_item"}
        self.index_contents["test-beta-menu-items"] = {"beta_only_doc"}
        catalog = _fixture_catalog()

        ssi.run(catalog, ["test-alpha"], dry_run=False)

        alpha_client = self.search_clients_by_index["test-alpha-menu-items"]
        alpha_client.delete_documents.assert_called_once_with([{"id": "stale_removed_item"}])
        plan = ssi.build_plan(catalog.get("test-alpha"))
        self.assertEqual(self.index_contents["test-alpha-menu-items"], {d["id"] for d in plan.documents})
        self.assertNotIn("test-beta-menu-items", self.search_clients_by_index)
        self.assertEqual(self.index_contents["test-beta-menu-items"], {"beta_only_doc"})

    def test_no_delete_call_when_nothing_is_stale(self):
        catalog = _fixture_catalog(["test-alpha"])
        ssi.run(catalog, None, dry_run=False)
        self.search_clients_by_index["test-alpha-menu-items"].delete_documents.assert_not_called()

    def test_ingest_verifies_the_index_count_against_the_plan(self):
        self.index_contents["test-alpha-menu-items"] = set()
        catalog = _fixture_catalog(["test-alpha"])
        with patch.object(ssi, "verify_document_count", wraps=ssi.verify_document_count) as verify:
            ssi.run(catalog, None, dry_run=False)
        verify.assert_called_once()
        self.assertEqual(verify.call_args.args[1], 3)
        self.assertEqual(verify.call_args.kwargs["index_name"], "test-alpha-menu-items")


class VerifyDocumentCountTests(unittest.TestCase):
    def test_returns_once_the_count_matches(self):
        client = MagicMock()
        client.get_document_count.side_effect = [1, 2, 3]
        sleeps: list[float] = []
        ssi.verify_document_count(client, 3, index_name="x", attempts=5, delay_seconds=0.5, sleep=sleeps.append)
        self.assertEqual(sleeps, [0.5, 0.5])

    def test_raises_when_the_count_never_matches(self):
        client = MagicMock()
        client.get_document_count.return_value = 2
        with self.assertRaisesRegex(RuntimeError, "holds 2 document"):
            ssi.verify_document_count(client, 3, index_name="x", attempts=3, delay_seconds=0, sleep=lambda _s: None)
        self.assertEqual(client.get_document_count.call_count, 3)


if __name__ == "__main__":
    unittest.main()
