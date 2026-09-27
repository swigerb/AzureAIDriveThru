"""Tests for issue #74 (P2-5): per-session persona binding in the Python backend.

Scope covered here:
  * a TEST-ONLY fixture persona pack (tests/fixtures/personas/{test-alpha,test-beta}) --
    NOT the real personas/ directory, since only the deployment's single default persona
    pack exists there today -- proves multi-persona isolation is real, not just plausible
    from reading the code;
  * /realtime?persona=<id> binding: unknown id -> 404 before the WS upgrade, omitted
    falls back to the deployment default, a valid id binds the whole session (persona
    id, voice, menu/search index) for its entire lifetime;
  * resume() rejects a session resuming under any persona other than the one it was
    originally bound to ("persona_mismatch"), design doc section 5.1/5.2;
  * /api/personas and /api/personas/{id} response shapes match design doc section 5.2;
  * no mid-conversation persona switching: OrderState exposes no setter for an existing
    session's persona id.

Rick's #92 note ("remove the hardcoded PromptLoader with a fixed brand argument") and the
#97 fold-in
(explicit per-session event-loop confinement for OrderState) are covered by
test_app.py / test_performance.py respectively -- not duplicated here.
"""

import os
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))
sys.path.append(str(Path(__file__).resolve().parent))

from test_session_bootstrap import BROWSER_SESSION_UPDATE, _RealtimeHarness

import default_persona
import menu_utils
from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from session_manager import SessionManager

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _load_fixture_catalog(default: str = "test-alpha") -> PersonaCatalog:
    """The TEST-ONLY fixture pack: two personas with genuinely distinct search index
    names, voices, tax rates, happy-hour config (test-beta has none, decision 5) and
    menu items -- built once per test rather than module-global so tests can't leak
    the (module-level) MenuCatalog cache into each other in surprising ways."""
    return PersonaCatalog.load(
        personas_dir=FIXTURES_DIR,
        enabled=["test-alpha", "test-beta"],
        default_persona_id=default,
    )


def _ws():
    ws = MagicMock()
    ws.close = AsyncMock()
    return ws


# ═══════════════════════════════════════════════════════════════════════════════
# FIXTURE PACK SANITY
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaCatalogFixtureTests(unittest.TestCase):
    """The fixture pack itself loads and the two personas are genuinely distinct --
    if this fails, every other test in this file is testing nothing."""

    def test_fixture_catalog_loads_both_personas(self):
        catalog = _load_fixture_catalog()
        self.assertEqual(catalog.ids, ["test-alpha", "test-beta"])
        self.assertEqual(catalog.default_persona_id, "test-alpha")

    def test_fixture_personas_have_different_search_index_and_voice(self):
        catalog = _load_fixture_catalog()
        alpha, beta = catalog.get("test-alpha"), catalog.get("test-beta")
        self.assertNotEqual(alpha.manifest.search.indexName, beta.manifest.search.indexName)
        self.assertNotEqual(alpha.manifest.voice.default, beta.manifest.voice.default)

    def test_fixture_beta_has_no_happy_hour_alpha_does(self):
        """Decision 5: `pricing.happyHour: null` means none, per persona."""
        catalog = _load_fixture_catalog()
        self.assertIsNone(catalog.get("test-beta").manifest.pricing.happyHour)
        self.assertIsNotNone(catalog.get("test-alpha").manifest.pricing.happyHour)

    def test_unknown_persona_id_is_absent_from_the_catalog(self):
        catalog = _load_fixture_catalog()
        self.assertNotIn("does-not-exist", catalog)


# ═══════════════════════════════════════════════════════════════════════════════
# MENU / CATALOG ISOLATION
# ═══════════════════════════════════════════════════════════════════════════════

class MenuCatalogIsolationTests(unittest.TestCase):
    """#74: each persona's menu comes from its OWN bound MenuCatalog -- one persona's
    session can never resolve, price, or categorize another persona's items."""

    def setUp(self):
        catalog = _load_fixture_catalog()
        self.alpha = catalog.get("test-alpha")
        self.beta = catalog.get("test-beta")

    def test_alpha_menu_resolves_its_own_item_and_price(self):
        mc = menu_utils.get_catalog_for_persona(self.alpha)
        item = mc.resolve_menu_item("Alpha Cola")
        self.assertIsNotNone(item)
        self.assertEqual(item["category"], "drinks")
        self.assertEqual(mc.price_for("Alpha Cola", "small"), 1.99)

    def test_beta_menu_does_not_resolve_alphas_item(self):
        mc = menu_utils.get_catalog_for_persona(self.beta)
        self.assertIsNone(mc.resolve_menu_item("Alpha Cola"))

    def test_alpha_menu_does_not_resolve_betas_item(self):
        mc = menu_utils.get_catalog_for_persona(self.alpha)
        self.assertIsNone(mc.resolve_menu_item("Beta Root Beer"))

    def test_alpha_and_beta_use_their_own_size_vocabularies(self):
        mc_alpha = menu_utils.get_catalog_for_persona(self.alpha)
        mc_beta = menu_utils.get_catalog_for_persona(self.beta)
        self.assertEqual(mc_alpha.normalize_size("s"), "Small")
        self.assertEqual(mc_beta.normalize_size("reg"), "Regular")


# ═══════════════════════════════════════════════════════════════════════════════
# ORDER_STATE PERSONA BINDING
# ═══════════════════════════════════════════════════════════════════════════════

class OrderStatePersonaBindingTests(unittest.TestCase):
    """#74: order_state.py sessions bound to different personas get their own menu
    catalog, happy-hour behavior, and persona id -- no cross-session leakage through
    module globals (the whole point of removing the brand-specific module-level singletons)."""

    def setUp(self):
        catalog = _load_fixture_catalog()
        self.alpha = catalog.get("test-alpha")
        self.beta = catalog.get("test-beta")
        self._sessions_created: list[str] = []
        self.addCleanup(self._cleanup_sessions)

    def _cleanup_sessions(self):
        for sid in self._sessions_created:
            order_state_singleton.delete_session(sid)

    def _new_session(self, persona=None) -> str:
        sid = order_state_singleton.create_session(persona=persona)
        self._sessions_created.append(sid)
        return sid

    def test_two_sessions_get_their_own_bound_persona_id(self):
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        self.assertEqual(order_state_singleton.get_persona_id(sid_a), "test-alpha")
        self.assertEqual(order_state_singleton.get_persona_id(sid_b), "test-beta")

    def test_two_sessions_get_different_menu_catalogs(self):
        sid_a = self._new_session(self.alpha)
        sid_b = self._new_session(self.beta)
        menu_a = order_state_singleton.get_menu_catalog(sid_a)
        menu_b = order_state_singleton.get_menu_catalog(sid_b)
        self.assertIsNotNone(menu_a.resolve_menu_item("Alpha Cola"))
        self.assertIsNone(menu_b.resolve_menu_item("Alpha Cola"))

    def test_beta_session_never_reports_happy_hour(self):
        sid_b = self._new_session(self.beta)
        self.assertFalse(order_state_singleton.is_happy_hour_for_session(sid_b))

    def test_default_bound_session_binds_to_the_real_default_persona(self):
        """No persona argument at all -- the deployment default binding path (#74, Rick's PR
        #102 review item 2) -- resolves to a REAL persona id (the deployment's own default),
        never ``None``: there is no more unbound-session state to be "exactly as
        before" about -- every session, including this one, is bound through the identical
        mandatory-catalog path."""
        sid = self._new_session()
        self.assertEqual(
            order_state_singleton.get_persona_id(sid),
            default_persona.get_default_persona().id,
        )


class NoMidConversationPersonaSwitchTests(unittest.TestCase):
    """Design decision (5.1): no mid-conversation persona switching, ever -- not even
    across a transport drop/resume (see ResumePersonaMismatchTests below for that
    vector). This test guards the OTHER vector: there must be no public API at all
    that could rebind an existing session's persona once created."""

    def test_order_state_exposes_no_persona_rebind_method(self):
        self.assertFalse(
            hasattr(order_state_singleton, "set_persona_id"),
            "a persona-rebind setter on OrderState would allow mid-conversation "
            "persona switching, which #74's design explicitly forbids",
        )

    def test_persona_id_is_never_reassigned_by_normal_session_activity(self):
        catalog = _load_fixture_catalog()
        alpha = catalog.get("test-alpha")
        sid = order_state_singleton.create_session(persona=alpha)
        try:
            self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
            order_state_singleton.get_menu_catalog(sid)
            order_state_singleton.is_happy_hour_for_session(sid)
            self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
        finally:
            order_state_singleton.delete_session(sid)


# ═══════════════════════════════════════════════════════════════════════════════
# RESUME PERSONA MISMATCH (design doc 5.1/5.2, resume() docstring)
# ═══════════════════════════════════════════════════════════════════════════════

class ResumePersonaMismatchTests(unittest.TestCase):
    """SessionManager.resume()'s own persona guard, exercised directly (no WS
    transport needed -- resume() only needs a resume id and a mock socket)."""

    def setUp(self):
        self.catalog = _load_fixture_catalog()
        self.sm = SessionManager()
        self.addCleanup(self._end_all)

    def _end_all(self):
        for sid in list(order_state_singleton.sessions):
            self.sm.end_session(sid, "test teardown")

    def test_resume_with_the_same_persona_is_accepted(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha")
        self.assertTrue(outcome.accepted)
        self.assertEqual(outcome.session_id, sid)

    def test_resume_with_a_different_persona_is_rejected(self):
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-beta")
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")
        # Rejected BEFORE the presented resume id was consumed -- the guest's real
        # credential (retried against the correct persona) still works.
        self.assertIn(sid, order_state_singleton.sessions)

    def test_resume_of_a_default_bound_session_requesting_a_persona_is_rejected(self):
        """Symmetric case: a session bound to the real deployment default (no persona arg,
        #74's mandatory-catalog path) can't be "claimed" into a different persona via resume
        either -- the mismatch check is unconditional now, not skipped for default-bound
        sessions."""
        sid = self.sm.create_session(_ws())   # binds to the real default persona
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id="test-alpha")
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")

    def test_resume_with_no_persona_requested_matches_a_default_bound_session(self):
        """requested_persona_id=None means "the caller means the deployment default"
        (#74/Rick's review item 2 -- the mismatch check is unconditional, never skipped) --
        a session itself bound to that same real default therefore matches."""
        sid = self.sm.create_session(_ws())   # binds to the real default persona
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id=None)
        self.assertTrue(outcome.accepted)

    def test_resume_with_no_persona_requested_mismatches_a_non_default_bound_session(self):
        """requested_persona_id=None resolves to the real deployment default, so a session
        bound to some OTHER persona (here the fixture's "test-alpha", not the real default)
        is correctly rejected as a mismatch -- there is no more "skip the check" state."""
        alpha = self.catalog.get("test-alpha")
        sid = self.sm.create_session(_ws(), persona=alpha)
        resume_id = self.sm.issue_resume_id(sid)
        outcome = self.sm.resume(_ws(), resume_id, requested_persona_id=None)
        self.assertFalse(outcome.accepted)
        self.assertEqual(outcome.reason, "persona_mismatch")


# ═══════════════════════════════════════════════════════════════════════════════
# /api/personas AND /api/personas/{id} WIRE SHAPE (design doc 5.2)
# ═══════════════════════════════════════════════════════════════════════════════

class ApiPersonasResponseShapeTests(unittest.TestCase):
    """app.py's pure response-body builders, exercised directly against the fixture
    catalog (avoids needing a full create_app() with real Azure OpenAI/Search env)."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import (
            _persona_detail_body,
            _persona_summary_body,
            _personas_index_body,
        )
        self.catalog = _load_fixture_catalog()
        self._index_body = _personas_index_body
        self._detail_body = _persona_detail_body
        self._summary_body = _persona_summary_body

    def test_personas_index_lists_default_and_both_personas(self):
        body = self._index_body(self.catalog)
        self.assertEqual(body["default"], "test-alpha")
        ids = [p["id"] for p in body["personas"]]
        self.assertEqual(ids, ["test-alpha", "test-beta"])

    def test_backends_python_falls_back_to_same_origin_when_backend_uri_is_unset(self):
        """Rick's PR #102 review item 4: BACKEND_URI/BACKEND_DOTNET_URI unset (local dev)
        -- python still gets an entry (same-origin ""), never omitted; dotnet, with no
        configured URI, is omitted entirely rather than reported with a placeholder."""
        with patch.dict("os.environ", {}, clear=False):
            for var in ("BACKEND_URI", "BACKEND_DOTNET_URI"):
                os.environ.pop(var, None)
            body = self._index_body(self.catalog)
        self.assertEqual(body["backends"], [{"id": "python", "url": ""}])

    def test_backends_url_is_each_backend_public_base_url_not_realtime(self):
        """`backends[].url` (design doc 5.2/10.1) is each backend's PUBLIC BASE URL, built
        from the #93 azd outputs BACKEND_URI/BACKEND_DOTNET_URI -- never the `/realtime`
        WebSocket path itself, so the F11 header switch can link across hostnames."""
        with patch.dict(
            "os.environ",
            {
                "BACKEND_URI": "https://python-backend.example.azurecontainerapps.io",
                "BACKEND_DOTNET_URI": "https://dotnet-backend.example.azurecontainerapps.io",
            },
        ):
            body = self._index_body(self.catalog)
        self.assertEqual(
            body["backends"],
            [
                {"id": "python", "url": "https://python-backend.example.azurecontainerapps.io"},
                {"id": "dotnet", "url": "https://dotnet-backend.example.azurecontainerapps.io"},
            ],
        )

    def test_backends_omits_dotnet_when_its_uri_is_unset(self):
        with patch.dict("os.environ", {"BACKEND_URI": "https://python-backend.example.azurecontainerapps.io"}):
            os.environ.pop("BACKEND_DOTNET_URI", None)
            body = self._index_body(self.catalog)
        self.assertEqual(
            body["backends"],
            [{"id": "python", "url": "https://python-backend.example.azurecontainerapps.io"}],
        )

    def test_persona_summary_has_the_designed_fields(self):
        summary = self._summary_body(self.catalog.get("test-alpha"))
        self.assertEqual(summary["id"], "test-alpha")
        self.assertEqual(summary["displayName"], "Test Alpha Drive-In")
        self.assertEqual(summary["logoUrl"], "/personas/test-alpha/assets/logo.svg")
        self.assertIn("light", summary["theme"])

    def test_persona_detail_has_voice_locales_features_menu_and_models(self):
        detail = self._detail_body(self.catalog.get("test-beta"))
        self.assertEqual(detail["id"], "test-beta")
        self.assertEqual(detail["voice"], {"default": "cedar"})
        self.assertEqual(detail["locales"]["default"], "en")
        self.assertEqual(detail["features"], {"dayparts": False})
        self.assertEqual(detail["menuUrl"], "/personas/test-beta/menu.json")
        self.assertEqual(detail["models"]["realtime"]["default"], "gpt-realtime-mini")
        self.assertNotIn("cascade", detail["models"])
        self.assertNotIn("local", detail["models"])


# ═══════════════════════════════════════════════════════════════════════════════
# PERSONA ASSET + MENU.JSON ROUTES (issue #74, design doc section 5.2, Rick's PR #102
# review item 5): served only for ENABLED packs, path traversal rejected, correct
# content types + immutable cache headers.
# ═══════════════════════════════════════════════════════════════════════════════

class PersonaAssetPathResolutionTests(unittest.TestCase):
    """Unit-level tests of `_resolve_persona_asset_path` -- deterministic, exercises the
    exact input string with no HTTP client / URL-normalization ambiguity. Covers every
    traversal variant Rick's item 5 calls out by name: `../`, encoded variants, absolute
    paths, and symlink escape (simulated, since creating real symlinks needs elevated
    privileges on Windows CI runners)."""

    def setUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from app import _resolve_persona_asset_path
        self._resolve = _resolve_persona_asset_path
        self.persona = _load_fixture_catalog().get("test-alpha")

    def test_resolves_a_real_top_level_asset(self):
        resolved = self._resolve(self.persona, "logo.svg")
        self.assertIsNotNone(resolved)
        self.assertTrue(resolved.is_file())

    def test_resolves_a_real_nested_asset(self):
        resolved = self._resolve(self.persona, "sub/icon.png")
        self.assertIsNotNone(resolved)

    def test_none_for_a_filename_that_does_not_exist(self):
        self.assertIsNone(self._resolve(self.persona, "does-not-exist.svg"))

    def test_none_for_dotdot_traversal(self):
        self.assertIsNone(self._resolve(self.persona, "../persona.json"))
        self.assertIsNone(self._resolve(self.persona, "../../../../etc/passwd"))
        self.assertIsNone(self._resolve(self.persona, "sub/../../persona.json"))

    def test_none_for_backslash_dotdot_traversal(self):
        """Windows-style separators must be normalized before segment-splitting, not
        smuggled past the `/`-based check."""
        self.assertIsNone(self._resolve(self.persona, "..\\persona.json"))

    def test_none_for_percent_encoded_dotdot(self):
        """aiohttp's router percent-decodes match_info before handlers see it, so an
        encoded variant like %2e%2e%2f arrives here already looking like a literal
        `../` -- confirms the structural check still catches it once decoded."""
        self.assertIsNone(self._resolve(self.persona, "%2e%2e/persona.json".replace("%2e", ".")))

    def test_none_for_absolute_posix_path(self):
        self.assertIsNone(self._resolve(self.persona, "/etc/passwd"))

    def test_none_for_absolute_windows_path(self):
        self.assertIsNone(self._resolve(self.persona, "C:/Windows/win.ini"))
        self.assertIsNone(self._resolve(self.persona, "C:\\Windows\\win.ini"))

    def test_none_for_empty_or_null_byte_path(self):
        self.assertIsNone(self._resolve(self.persona, ""))
        self.assertIsNone(self._resolve(self.persona, "logo.svg\x00.png"))

    def test_none_when_resolved_path_escapes_assets_dir_even_without_dotdot_segments(self):
        """Simulates a symlink escape: a segment-only check would pass (no `..` in the
        string), but `.resolve()` following the link lands outside `assets_dir`, so the
        `relative_to(assets_root)` guard must still reject it. Patches `Path.resolve` so
        only the specific asset-file candidate resolves outside the pack -- everything
        else (including `assets_dir` itself) resolves normally -- since creating a real
        symlink needs elevated privileges on Windows CI runners."""
        escaped_target = self.persona.pack_dir.parent.resolve() / "persona.schema.json"
        real_resolve = Path.resolve

        def fake_resolve(path_self, strict=False):
            if path_self.name == "logo.svg":
                return escaped_target
            return real_resolve(path_self, strict=strict)

        with patch.object(Path, "resolve", fake_resolve):
            self.assertIsNone(self._resolve(self.persona, "logo.svg"))


class PersonaAssetAndMenuRouteTests(unittest.IsolatedAsyncioTestCase):
    """HTTP-level conformance tests: registers the real `register_persona_routes` (app.py)
    on a standalone `web.Application` against the fixture catalog -- no `create_app()` /
    Azure OpenAI/Search dependency needed, but actual route matching, percent-decoding
    and status codes are exercised end-to-end via aiohttp's TestClient."""

    async def asyncSetUp(self):
        sys.path.append(str(Path(__file__).resolve().parents[1]))
        from aiohttp import web
        from aiohttp.test_utils import TestClient, TestServer

        from app import register_persona_routes

        # Only test-alpha enabled: test-beta's pack exists on disk (FIXTURES_DIR) but is a
        # "disabled pack" for this catalog -- its routes must 404 exactly like an unknown
        # persona id, never fall through to serving its files (Rick's item 5).
        self.catalog = PersonaCatalog.load(
            personas_dir=FIXTURES_DIR, enabled=["test-alpha"], default_persona_id="test-alpha",
        )
        app = web.Application()
        register_persona_routes(app, self.catalog)
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()

    async def test_logo_asset_returns_200_with_immutable_cache_header(self):
        from app import _STATIC_IMMUTABLE_MAX_AGE
        resp = await self.client.get("/personas/test-alpha/assets/logo.svg")
        self.assertEqual(resp.status, 200)
        self.assertIn("<svg", await resp.text())
        self.assertEqual(
            resp.headers["Cache-Control"], f"public, max-age={_STATIC_IMMUTABLE_MAX_AGE}, immutable",
        )

    async def test_nested_asset_path_returns_200(self):
        resp = await self.client.get("/personas/test-alpha/assets/sub/icon.png")
        self.assertEqual(resp.status, 200)

    async def test_menu_json_returns_200_as_application_json_with_immutable_cache_header(self):
        from app import _STATIC_IMMUTABLE_MAX_AGE
        resp = await self.client.get("/personas/test-alpha/menu.json")
        self.assertEqual(resp.status, 200)
        self.assertEqual(resp.content_type, "application/json")
        self.assertEqual(
            resp.headers["Cache-Control"], f"public, max-age={_STATIC_IMMUTABLE_MAX_AGE}, immutable",
        )
        self.assertIn("menuItems", await resp.json())

    async def test_asset_404s_for_unknown_persona_id(self):
        resp = await self.client.get("/personas/does-not-exist/assets/logo.svg")
        self.assertEqual(resp.status, 404)

    async def test_menu_404s_for_unknown_persona_id(self):
        resp = await self.client.get("/personas/does-not-exist/menu.json")
        self.assertEqual(resp.status, 404)

    async def test_asset_404s_for_a_disabled_persona_even_though_its_pack_exists_on_disk(self):
        resp = await self.client.get("/personas/test-beta/assets/logo.svg")
        self.assertEqual(resp.status, 404)

    async def test_menu_404s_for_a_disabled_persona(self):
        resp = await self.client.get("/personas/test-beta/menu.json")
        self.assertEqual(resp.status, 404)

    async def test_asset_404s_for_a_filename_that_does_not_exist_in_the_pack(self):
        resp = await self.client.get("/personas/test-alpha/assets/does-not-exist.svg")
        self.assertEqual(resp.status, 404)

    async def test_percent_encoded_dotdot_traversal_is_rejected(self):
        """The encoded variant Rick's item 5 calls out by name -- aiohttp's router
        percent-decodes match_info before the handler runs, so this must land in the
        same structural-rejection path as a literal `../`, not slip through as 200."""
        resp = await self.client.get(
            "/personas/test-alpha/assets/%2e%2e%2f%2e%2e%2f%2e%2e%2fpersona.json", allow_redirects=False,
        )
        self.assertIn(resp.status, (400, 404))

    async def test_literal_dotdot_traversal_out_of_the_pack_is_rejected(self):
        resp = await self.client.get(
            "/personas/test-alpha/assets/../../../persona.schema.json", allow_redirects=False,
        )
        self.assertIn(resp.status, (400, 404))
        # If the route even matched (rather than the underlying HTTP client/router
        # normalizing the dots away into a different, non-matching path), it must not be
        # a 200 -- the fixture pack's own schema file must never be served this way.
        self.assertNotEqual(resp.status, 200)


class PersonaWebSocketHandlerTests(_RealtimeHarness):
    """Drives the real RTMiddleTier._websocket_handler / _forward_messages through
    aiohttp's TestClient, with persona_catalog set to the fixture pack -- proves the
    end-to-end wiring, not just the unit pieces above."""

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.catalog = _load_fixture_catalog(default="test-alpha")
        self.rtmt.persona_catalog = self.catalog
        self.rtmt.persona_prompt_loaders = {}
        # The fixture personas' voices (marin, cedar) must be allow-listed like any
        # real deployment's model.allowed_voices would list them.
        self.rtmt.allowed_voices = frozenset({"marin", "cedar", "shimmer"})

    async def test_unknown_persona_is_rejected_with_404_before_the_ws_upgrade(self):
        resp = await self.client.get("/realtime", params={"persona": "does-not-exist"})
        self.assertEqual(resp.status, 404)
        self.assertEqual(self.rtmt._sessions.active_session_count, 0,
                          "a rejected persona must never create a session")

    async def test_valid_persona_binds_the_whole_session(self):
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_persona_id(sid), "test-beta")
        await browser.close()

    async def test_omitted_persona_falls_back_to_the_deployment_default(self):
        browser = await self.client.ws_connect("/realtime")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 1)
        sid = next(iter(self.rtmt._sessions._session_map.values()))
        self.assertEqual(order_state_singleton.get_persona_id(sid), "test-alpha")
        await browser.close()

    async def test_session_metadata_carries_the_bound_persona_id(self):
        """Rick's PR #102 review item 3 (#74 acceptance, design doc 5.2): every
        `extension.session_metadata` message reports the persona this session is bound
        to -- both for an explicitly requested persona and for the omitted-persona
        default-binding path."""
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        events = await self._browser_events(browser, duration=0.5)
        meta = next(e for e in events if e.get("type") == "extension.session_metadata")
        self.assertEqual(meta["persona"], "test-beta")
        await browser.close()

        default_browser = await self.client.ws_connect("/realtime")
        await default_browser.send_json(BROWSER_SESSION_UPDATE)
        default_events = await self._browser_events(default_browser, duration=0.5)
        default_meta = next(e for e in default_events if e.get("type") == "extension.session_metadata")
        self.assertEqual(default_meta["persona"], "test-alpha")
        await default_browser.close()

    async def test_bound_persona_voice_flows_into_the_bootstrap_session_update(self):
        browser = await self.client.ws_connect("/realtime?persona=test-beta")
        await browser.send_json(BROWSER_SESSION_UPDATE)
        await self._response_done(browser)
        voices = [
            u["session"]["audio"]["output"]["voice"]
            for u in self._session_updates()
            if "audio" in u.get("session", {})
        ]
        self.assertIn("cedar", voices)
        self.assertNotIn("marin", voices, "test-beta's session must never pick up test-alpha's voice")
        await browser.close()

    async def test_two_concurrent_sessions_each_keep_their_own_persona(self):
        """The isolation guarantee end to end: two simultaneous connections, one per
        persona, must never cross-contaminate."""
        browser_a = await self.client.ws_connect("/realtime?persona=test-alpha")
        browser_b = await self.client.ws_connect("/realtime?persona=test-beta")
        await self._until(lambda: self.rtmt._sessions.active_session_count >= 2)
        persona_ids = {
            order_state_singleton.get_persona_id(sid)
            for sid in self.rtmt._sessions._session_map.values()
        }
        self.assertEqual(persona_ids, {"test-alpha", "test-beta"})
        await browser_a.close()
        await browser_b.close()


if __name__ == "__main__":
    unittest.main()
