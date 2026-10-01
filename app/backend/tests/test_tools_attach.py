"""Unit tests for `tools.attach_tools_rtmt`'s per-persona tool schema wiring.

Issue #170 R4 (Rick's PR #175 round-2 review, required item 1): `attach_tools_rtmt`
used to build `rtmt.tools[*].schema` ONCE from the single deployment-default
`prompt_loader.get_tool_schemas()` (or the hardcoded fallback) and every bound
session shared it -- so a non-default persona's session.tools[].description named
the DEFAULT persona's menu/ticket, never its own (the live bug: a bound persona's
guest hears its own tool list naming another persona's brand/ticket, not just the
system prompt). This now ALSO
builds `rtmt.persona_tool_schemas: dict[str, list[dict]]`, one entry per registered
persona, each resolved from THAT persona's own `prompt_loader.get_tool_schemas()`
with a per-tool-name fallback directly to the hardcoded schema (never through
another persona's resolved schema map).
"""

import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

sys.path.append(str(Path(__file__).resolve().parents[1]))

from azure.core.credentials import AzureKeyCredential

from tools import attach_tools_rtmt


def _fake_rtmt():
    return SimpleNamespace(tools={}, persona_tool_schemas={})


def _call_attach(rtmt, personas=None, prompt_loader=None):
    attach_tools_rtmt(
        rtmt,
        credentials=AzureKeyCredential("fake-key"),
        search_endpoint="https://fake.search.windows.net",
        search_index="fake-index",
        semantic_configuration="fake-config",
        identifier_field="id",
        content_field="content",
        embedding_field="embedding",
        title_field="title",
        use_vector_query=False,
        prompt_loader=prompt_loader,
        personas=personas,
    )


class AttachToolsRtmtPersonaToolSchemasTests(unittest.TestCase):
    """`attach_tools_rtmt` mutates tools.py's own module-level globals
    (`_prompt_loader`, `_persona_registry`, `_default_search_ctx`) -- this suite
    saves and restores them so repeatedly calling it with fake/MagicMock prompt
    loaders here never leaks into other test modules sharing this process
    (e.g. test_tools_search.py's own real-default-persona assumptions)."""

    def setUp(self):
        import tools
        self._tools = tools
        self._saved_prompt_loader = tools._prompt_loader
        self._saved_persona_registry = dict(tools._persona_registry)
        self._saved_default_search_ctx = dict(tools._default_search_ctx)

    def tearDown(self):
        self._tools._prompt_loader = self._saved_prompt_loader
        self._tools._persona_registry.clear()
        self._tools._persona_registry.update(self._saved_persona_registry)
        self._tools._default_search_ctx.clear()
        self._tools._default_search_ctx.update(self._saved_default_search_ctx)

    def test_no_personas_leaves_persona_tool_schemas_empty(self):
        rtmt = _fake_rtmt()
        _call_attach(rtmt, personas=None)
        self.assertEqual(rtmt.persona_tool_schemas, {})

    def test_each_registered_persona_gets_its_own_tool_schemas_entry(self):
        rtmt = _fake_rtmt()
        alpha_loader = MagicMock()
        alpha_loader.get_tool_schemas.return_value = [
            {"name": "search", "description": "Search the test-alpha menu.", "parameters": {}},
        ]
        beta_loader = MagicMock()
        beta_loader.get_tool_schemas.return_value = [
            {"name": "search", "description": "Search the test-beta menu.", "parameters": {}},
        ]
        _call_attach(rtmt, personas={
            "test-alpha": {"prompt_loader": alpha_loader},
            "test-beta": {"prompt_loader": beta_loader},
        })

        alpha_search = next(s for s in rtmt.persona_tool_schemas["test-alpha"] if s["name"] == "search")
        beta_search = next(s for s in rtmt.persona_tool_schemas["test-beta"] if s["name"] == "search")
        self.assertEqual(alpha_search["description"], "Search the test-alpha menu.")
        self.assertEqual(beta_search["description"], "Search the test-beta menu.")
        self.assertNotEqual(alpha_search["description"], beta_search["description"])

    def test_a_persona_missing_a_tool_in_its_own_yaml_falls_back_to_the_hardcoded_schema(self):
        """The fallback is DIRECTLY to the hardcoded schema (search_tool_schema et al), never
        through another persona's -- or the deployment default's -- resolved schema map."""
        rtmt = _fake_rtmt()
        partial_loader = MagicMock()
        # Only overrides "search" -- update_order/get_order/reset_order fall back.
        partial_loader.get_tool_schemas.return_value = [
            {"name": "search", "description": "Search the partial persona's menu.", "parameters": {}},
        ]
        _call_attach(rtmt, personas={"test-partial": {"prompt_loader": partial_loader}})

        from tools import (
            get_order_tool_schema,
            reset_order_tool_schema,
            update_order_tool_schema,
        )

        names_to_schema = {s["name"]: s for s in rtmt.persona_tool_schemas["test-partial"]}
        self.assertEqual(names_to_schema["update_order"], update_order_tool_schema)
        self.assertEqual(names_to_schema["get_order"], get_order_tool_schema)
        self.assertEqual(names_to_schema["reset_order"], reset_order_tool_schema)

    def test_a_persona_with_no_prompt_loader_entry_gets_all_hardcoded_schemas(self):
        rtmt = _fake_rtmt()
        _call_attach(rtmt, personas={"test-no-loader": {"prompt_loader": None}})

        from tools import search_tool_schema

        names_to_schema = {s["name"]: s for s in rtmt.persona_tool_schemas["test-no-loader"]}
        self.assertEqual(names_to_schema["search"], search_tool_schema)

    def test_persona_tool_schemas_is_independent_of_the_deployment_default(self):
        """The deployment-default's own `prompt_loader` (passed positionally, used for
        `rtmt.tools[*]`, the shared/legacy path) must not leak into a bound persona's own
        resolved list -- each persona's fallback is the hardcoded schema directly."""
        rtmt = _fake_rtmt()
        default_loader = MagicMock()
        default_loader.get_tool_schemas.return_value = [
            {"name": "search", "description": "Search the DEPLOYMENT DEFAULT's menu.", "parameters": {}},
        ]
        persona_loader = MagicMock()
        persona_loader.get_tool_schemas.return_value = [
            {"name": "search", "description": "Search the bound persona's own menu.", "parameters": {}},
        ]
        _call_attach(rtmt, personas={"test-persona": {"prompt_loader": persona_loader}}, prompt_loader=default_loader)

        bound_search = next(s for s in rtmt.persona_tool_schemas["test-persona"] if s["name"] == "search")
        self.assertEqual(bound_search["description"], "Search the bound persona's own menu.")
        self.assertNotEqual(bound_search["description"], "Search the DEPLOYMENT DEFAULT's menu.")


if __name__ == "__main__":
    unittest.main()
