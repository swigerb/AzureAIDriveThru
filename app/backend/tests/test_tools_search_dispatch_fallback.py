"""Unit tests for `tools._search_dispatch`'s no-search-context fallback (#313 item 4, Rick's
review): `_run_tool_call` in `scripts/eval_voice.py` routes the mandatory `search` tool call to
`tools._search_dispatch`, but `scripts/smoke_realtime.py`'s `build_middle_tier` registers tool
schemas only (`Tool(target=None, ...)`) and never calls `tools.attach_tools_rtmt` -- so
`_default_search_ctx` stays `{}` for the life of a live eval_voice.py session. `_search_dispatch`
used to blindly index `cfg["search_client"]`, raising a bare `KeyError` on the very first
`search` call (every persona's own prompt makes `search` mandatory before `update_order`), which
neither `run_one_session` nor `main` caught -- aborting the whole run with a traceback on turn 1.

These tests drive `_search_dispatch` directly with no search context registered (mirroring
eval_voice.py's exact setup) and assert it falls back to a local, in-process menu-catalog lookup
instead of raising, using the synthetic test-only `test-zeta` fixture pack (never a real brand
pack, consistent with item 2/3's fixture-pack fix elsewhere in this same review round) so the
`(say: ...)` spoken-name hint can be asserted against a known `spokenName` override.
"""

import asyncio
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

import tools  # noqa: E402
from order_state import order_state_singleton  # noqa: E402
from persona_loader import PersonaCatalog  # noqa: E402
from rtmt import ToolResultDirection  # noqa: E402

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


class SearchDispatchNoSearchContextFallbackTests(unittest.TestCase):
    """`tools._search_dispatch` mutates no module-level globals itself, but reads
    `tools._persona_registry`/`tools._default_search_ctx` -- this suite saves and restores both
    so asserting they're empty (the exact eval_voice.py condition) never leaks into other test
    modules sharing this process (e.g. test_tools_attach.py's own attach_tools_rtmt calls)."""

    @classmethod
    def setUpClass(cls):
        persona_id = "test-zeta"
        cls.persona = PersonaCatalog.load(
            personas_dir=FIXTURES_DIR, enabled=[persona_id], default_persona_id=persona_id,
        ).get(persona_id)

    def setUp(self):
        self._saved_persona_registry = dict(tools._persona_registry)
        self._saved_default_search_ctx = dict(tools._default_search_ctx)
        tools._persona_registry.clear()
        tools._default_search_ctx.clear()
        order_state_singleton.sessions = {}

    def tearDown(self):
        tools._persona_registry.clear()
        tools._persona_registry.update(self._saved_persona_registry)
        tools._default_search_ctx.clear()
        tools._default_search_ctx.update(self._saved_default_search_ctx)
        order_state_singleton.sessions = {}

    def test_search_with_no_search_context_does_not_raise_key_error(self):
        """The exact crash from Rick's review: `cfg["search_client"]` on an empty cfg."""
        session_id = order_state_singleton.create_session(persona=self.persona)
        try:
            result = asyncio.run(tools._search_dispatch({"query": "Zorb"}, session_id))
        except KeyError:
            self.fail("_search_dispatch raised KeyError with no search context configured")
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)

    def test_local_fallback_finds_a_real_menu_item_with_spoken_name_hint(self):
        session_id = order_state_singleton.create_session(persona=self.persona)
        result = asyncio.run(tools._search_dispatch({"query": "Zorb"}, session_id))
        text = result.to_text()
        self.assertIn("ZORBS® Bite Treats", text)
        self.assertIn("(say: Zorb Bite Treats)", text)

    def test_local_fallback_with_no_match_reports_no_results_instead_of_raising(self):
        session_id = order_state_singleton.create_session(persona=self.persona)
        result = asyncio.run(tools._search_dispatch({"query": "no-such-item-xyz"}, session_id))
        text = result.to_text()
        self.assertNotIn("ZORBS", text)

    def test_unbound_session_id_also_falls_back_without_raising(self):
        """A `None` session_id (a direct call with no session in hand at all) must resolve
        through the same no-crash fallback path, not just a session bound to a real persona."""
        try:
            result = asyncio.run(tools._search_dispatch({"query": "anything"}, None))
        except KeyError:
            self.fail("_search_dispatch raised KeyError for an unbound session with no search context")
        self.assertEqual(result.destination, ToolResultDirection.TO_SERVER)


if __name__ == "__main__":
    unittest.main()
