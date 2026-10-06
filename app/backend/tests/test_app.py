import json
import os
import re
import runpy
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

from aiohttp.test_utils import TestClient, TestServer

sys.path.append(str(Path(__file__).resolve().parents[1]))

import default_persona
import entra_auth
from access_log import PathOnlyAccessLogger
from app import _get_bool_env

# The default persona's own id -- used instead of a hardcoded brand name in the route
# walk and its fixed anonymous-route probes below, so these tests don't add fresh
# brand-literal occurrences to the rebrand-word-count ratchet (#76). Rick's #159
# round-1 review, required item 1.
_DEFAULT_PERSONA_ID = default_persona.get_default_persona().id

# Sample values for named path params discovered while walking app.router.routes()
# (issue #144 acceptance: route-coverage test, Rick's #159 round-1 review, required
# item 6). Any param not listed here (there are none today besides persona_id and
# persona-asset's asset_path, which is excluded entirely -- see
# RouteCoverageTests._protected_routes) gets the generic sample "x".
_PARAM_SAMPLE_VALUES = {"persona_id": _DEFAULT_PERSONA_ID}


def _fill_path_params(canonical: str) -> str:
    """Replaces every `{name}` in a route's canonical template with a sample value,
    so a real request can be sent to it."""
    return re.sub(r"\{([^{}]+)\}", lambda m: _PARAM_SAMPLE_VALUES.get(m.group(1), "x"), canonical)


class GetBoolEnvTests(unittest.TestCase):
    """Tests for the _get_bool_env helper that parses boolean env vars."""

    def test_returns_default_when_unset(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(_get_bool_env("MISSING_VAR", False))
            self.assertTrue(_get_bool_env("MISSING_VAR", True))

    def test_truthy_values(self):
        for value in ("1", "true", "True", "TRUE", "yes", "Yes", "YES", "on", "On", "ON"):
            with patch.dict(os.environ, {"TEST_VAR": value}):
                self.assertTrue(_get_bool_env("TEST_VAR", False), f"Expected True for '{value}'")

    def test_falsy_values(self):
        for value in ("0", "false", "False", "no", "off", "maybe", ""):
            with patch.dict(os.environ, {"TEST_VAR": value}):
                self.assertFalse(_get_bool_env("TEST_VAR", False), f"Expected False for '{value}'")

    def test_whitespace_is_stripped(self):
        with patch.dict(os.environ, {"TEST_VAR": "  true  "}):
            self.assertTrue(_get_bool_env("TEST_VAR", False))

    def test_default_parameter_defaults_to_false(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(_get_bool_env("MISSING_VAR"))


class CreateAppConfigTests(unittest.IsolatedAsyncioTestCase):
    """Tests for create_app voice choice and system prompt configuration."""

    async def _run_create_app(self):
        """Run create_app with mocked Azure services; return (class_mock, instance_mock)."""
        with patch("app.RTMiddleTier") as mock_cls, \
             patch("app.attach_tools_rtmt"), \
             patch("app._check_service_connectivity", new_callable=AsyncMock), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 # This suite's own conftest.py sets CONFORMANCE_TEST_HOOKS=1 process-wide
                 # (for unrelated reasons -- see that file's docstring); a genuine production
                 # simulation must override it back off for this scope, or the new prod guard
                 # (Rick's #118 review item 4) would treat this as the real, forbidden
                 # combination it's designed to catch.
                 "CONFORMANCE_TEST_HOOKS": "",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "test-index",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
                 # Issue #144: Production now requires an explicit, valid Entra auth
                 # configuration (design doc section 18.5) -- without these three, the
                 # new fail-fast in create_app() would sys.exit(1) before ever reaching
                 # the RTMiddleTier construction this suite is actually testing.
                 "AUTH_MODE": "Entra",
                 "ENTRA_TENANT_ID": "11111111-1111-1111-1111-111111111111",
                 "ENTRA_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
                 "APP_SESSION_SECRET": "test-session-secret-0123456789abcdef",
             }):
            mock_instance = MagicMock()
            mock_cls.return_value = mock_instance
            from app import create_app
            await create_app()
            return mock_cls, mock_instance

    async def test_default_voice_matches_config(self):
        """The configured default voice reaches RTMiddleTier.

        Asserts against config.yaml rather than a hardcoded name so changing the
        carhop voice is a one-line config edit, not a test edit too.
        """
        from config_loader import get_config
        expected = get_config().get("model", {}).get("default_voice")
        self.assertTrue(expected, "config.yaml must define model.default_voice")
        mock_cls, _ = await self._run_create_app()
        _, kwargs = mock_cls.call_args
        self.assertEqual(kwargs["voice_choice"], expected)

    async def test_system_prompt_contains_carhop_closing(self):
        _, mock_instance = await self._run_create_app()
        self.assertIn(
            "Your carhop will have that right out",
            mock_instance.system_message,
        )

    async def test_system_prompt_contains_get_order_tool_instruction(self):
        _, mock_instance = await self._run_create_app()
        self.assertIn("get_order", mock_instance.system_message)


class ProductionGuardTests(unittest.IsolatedAsyncioTestCase):
    """Rick's PR #118 review, required item 4: refuse to start with
    CONFORMANCE_TEST_HOOKS=1 alongside RUNNING_IN_PRODUCTION=1."""

    async def test_refuses_to_start_with_conformance_hooks_in_production(self):
        """Supplies every required env var and mocks all downstream startup work so that,
        absent the guard, create_app() would otherwise succeed. This ensures the SystemExit
        asserted here can only come from the prod_guard check itself, not incidentally from
        the (also SystemExit-raising) missing-required-env-vars check -- a guard that's been
        neutered (e.g. `if False:`) must still fail this test."""
        with patch("app.RTMiddleTier") as mock_cls, \
             patch("app.attach_tools_rtmt"), \
             patch("app._check_service_connectivity", new_callable=AsyncMock), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "CONFORMANCE_TEST_HOOKS": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "test-index",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
             }, clear=True):
            mock_cls.return_value = MagicMock()
            from app import create_app
            with self.assertRaises(SystemExit):
                await create_app()

    async def test_allows_conformance_hooks_when_not_in_production(self):
        """A mutated guard that used `or` instead of `and` would refuse to start here too --
        CONFORMANCE_TEST_HOOKS=1 alone (the normal shape for local dev/CI) must never be
        fatal on its own."""
        with patch("app.RTMiddleTier") as mock_cls, \
             patch("app.attach_tools_rtmt"), \
             patch("app._check_service_connectivity", new_callable=AsyncMock), \
             patch.dict(os.environ, {
                 "CONFORMANCE_TEST_HOOKS": "1",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "test-index",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
             }, clear=True):
            mock_instance = MagicMock()
            mock_cls.return_value = mock_instance
            from app import create_app
            app = await create_app()
            self.assertIsNotNone(app)

    async def test_allows_production_without_conformance_hooks(self):
        """A mutated guard that checked RUNNING_IN_PRODUCTION alone (ignoring
        CONFORMANCE_TEST_HOOKS entirely) would refuse to start every normal
        production deployment -- this must keep succeeding."""
        mock_cls, mock_instance = await CreateAppConfigTests()._run_create_app()
        self.assertIsNotNone(mock_instance)


class RouteCoverageTests(unittest.IsolatedAsyncioTestCase):
    """Issue #144 acceptance criteria: walk the app's REAL registered routes (not a
    mocked `RTMiddleTier` -- see `_build_real_app`'s docstring) and assert (a) the
    exact expected route-name set, (b) every non-anonymous route 401s without a
    token, and (c) the anonymous allow-list (by route name) and the persona-asset
    extension split are NOT 401.

    Requires `static/index.html` to exist (the frontend build's output) -- like
    several pre-existing tests (e.g. `test_performance.py`'s
    `test_root_serves_index_html`), this only genuinely passes where the frontend
    has been built first (CI's Docker/test job), a pre-existing, unrelated gap in
    a bare backend-only worktree.
    """

    async def _build_real_app(self):
        """Unlike `CreateAppConfigTests._run_create_app`, this does NOT mock
        `app.RTMiddleTier` -- constructing a REAL instance does no network I/O
        (it only stores config and builds lazy `SearchClient`/credential objects),
        and only a real instance's `attach_to_app()` registers the real, named
        `'realtime'` route this test needs to see. Only `attach_tools_rtmt` and
        `_check_service_connectivity` (both genuinely do outbound I/O) are mocked."""
        with patch("app.attach_tools_rtmt"), \
             patch("app._check_service_connectivity", new_callable=AsyncMock), \
             patch.dict(os.environ, {
                 "RUNNING_IN_PRODUCTION": "1",
                 "CONFORMANCE_TEST_HOOKS": "",
                 "AZURE_OPENAI_EASTUS2_ENDPOINT": "https://fake.openai.azure.com",
                 "AZURE_OPENAI_REALTIME_DEPLOYMENT": "gpt-realtime-2.1",
                 "AZURE_OPENAI_EASTUS2_API_KEY": "fake-key",
                 "AZURE_SEARCH_API_KEY": "fake-search-key",
                 "AZURE_SEARCH_ENDPOINT": "https://fake.search.windows.net",
                 "AZURE_SEARCH_INDEX": "test-index",
                 "AZURE_OPENAI_REALTIME_VOICE_CHOICE": "",
                 "AUTH_MODE": "Entra",
                 "ENTRA_TENANT_ID": "11111111-1111-1111-1111-111111111111",
                 "ENTRA_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
                 "APP_SESSION_SECRET": "test-session-secret-0123456789abcdef",
             }):
            from app import create_app
            return await create_app()

    async def test_exact_route_name_set(self):
        """A route added later gets counted here too -- silently forgetting to
        classify it as anonymous or protected in entra_auth.py must fail this
        test, not slip through as an unnoticed extra 401 or 200. Keeps EVERY
        route, including any with `route.name is None` -- a future unnamed route
        would show up as a `None` in `names` and fail the equality below, rather
        than silently being dropped from coverage (Rick's #159 round-1 review,
        required item 6)."""
        app = await self._build_real_app()
        names = {route.name for route in app.router.routes()}
        expected = {
            "index", "health", "session-token", "personas-index",
            "persona-detail", "persona-menu", "persona-asset", "static", "realtime",
        }
        self.assertEqual(names, expected)

    def test_anonymous_route_names_pinned(self):
        """F1 (#163 round-2 review): pins the exact anonymous-by-name set as its
        own literal, independent of `_build_real_app` (and so independent of the
        bare-worktree `static/index.html` gap documented on this class) -- a
        change here is a deliberate, reviewable auth-allow-list change, not an
        incidental side effect of adding a route elsewhere."""
        self.assertEqual(entra_auth.ANONYMOUS_ROUTE_NAMES, frozenset({"index", "health", "static"}))

    async def test_every_protected_route_401s_without_a_token(self):
        """Built from the REAL route walk (`app.router.routes()`), not a
        hand-written path list, so a newly added protected route is automatically
        covered -- a route left unclassified in `entra_auth.ANONYMOUS_ROUTE_NAMES`
        fails this test instead of silently passing (Rick's #159 round-1 review,
        required item 6). `persona-asset` is walked and pinned separately
        (`test_persona_asset_protected_extension_still_401s` below) since it is
        anonymous or protected PER REQUEST depending on the requested file's
        extension, not per route."""
        app = await self._build_real_app()
        async with TestClient(TestServer(app)) as client:
            seen_any = False
            for route in app.router.routes():
                name = route.name
                if name in entra_auth.ANONYMOUS_ROUTE_NAMES or name == entra_auth.PERSONA_ASSET_ROUTE_NAME:
                    continue
                canonical = route.resource.canonical
                path = _fill_path_params(canonical)
                seen_any = True
                with self.subTest(method=route.method, path=path):
                    resp = await client.request(route.method, path)
                    self.assertEqual(
                        resp.status, 401, f"{route.method} {path} ({name}) should require a token"
                    )
                    self.assertEqual(resp.headers.get("WWW-Authenticate"), "Bearer")
            self.assertTrue(seen_any, "the route walk found nothing to protect -- fixture is broken")

    async def test_anonymous_routes_are_not_401(self):
        app = await self._build_real_app()
        async with TestClient(TestServer(app)) as client:
            for path in ("/", "/health", f"/personas/{_DEFAULT_PERSONA_ID}/assets/logo.svg"):
                with self.subTest(path=path):
                    resp = await client.get(path)
                    self.assertNotEqual(resp.status, 401, f"{path} should be anonymous")

    async def test_persona_asset_protected_extension_still_401s(self):
        """The persona-asset route itself is registered once, but is anonymous or
        protected PER REQUEST depending on the requested file's extension --
        covered separately from the anonymous-svg case above."""
        app = await self._build_real_app()
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/demo/dummyOrder.json")
            self.assertEqual(resp.status, 401)


class CreateRunnerTests(unittest.IsolatedAsyncioTestCase):
    """create_runner() (issue #144, design doc section 18.4): a `SystemExit` from
    `create_app()` (every existing startup fail-fast) must become a plain
    `RuntimeError`, since gunicorn's arbiter only treats an `Exception` -- never a
    `SystemExit` -- raised before boot as a genuine boot failure; left unwrapped,
    the worker would respawn in a tight loop instead of failing fast. On success,
    the runner must carry the 65s/28.5s timeouts and `access_log_kwargs()`."""

    async def test_system_exit_becomes_runtime_error(self):
        from app import create_runner
        original_exc = SystemExit(1)
        with patch("app.create_app", new_callable=AsyncMock, side_effect=original_exc):
            with self.assertRaises(RuntimeError) as ctx:
                await create_runner()
            self.assertEqual(str(ctx.exception), "startup failed")
            self.assertIs(ctx.exception.__cause__, original_exc)

    async def test_successful_path_passes_the_required_runner_kwargs(self):
        from access_log import PathOnlyAccessLogger
        from app import create_runner
        fake_app = MagicMock()
        fake_runner = MagicMock()
        with patch("app.create_app", new_callable=AsyncMock, return_value=fake_app), \
             patch("app.web.AppRunner", return_value=fake_runner) as mock_runner_cls:
            runner = await create_runner()

        self.assertIs(runner, fake_runner)
        args, kwargs = mock_runner_cls.call_args
        self.assertIs(args[0], fake_app)
        self.assertEqual(kwargs["keepalive_timeout"], 65)
        self.assertEqual(kwargs["shutdown_timeout"], 28.5)
        self.assertIs(kwargs["access_log_class"], PathOnlyAccessLogger)


class MainEntrypointTests(unittest.TestCase):
    """`python app.py`'s `if __name__ == "__main__":` path is the OTHER runner --
    gunicorn's `GunicornWebWorker` never executes it, so it must independently
    pass `access_log_kwargs()` to `web.run_app()` itself, not rely on
    `create_runner()`'s coverage above (Rick's #159 round-1 review, required
    item 5). Runs the real module body via `runpy.run_path(..., run_name="__main__")`
    with `aiohttp.web.run_app` replaced so the coroutine it's given is closed
    immediately instead of actually starting a server."""

    def test_python_app_py_passes_access_log_kwargs_to_run_app(self):
        app_module_path = str(Path(__file__).resolve().parents[1] / "app.py")
        captured_kwargs = {}

        def _fake_run_app(app_coro, **kwargs):
            captured_kwargs.update(kwargs)
            app_coro.close()  # never actually run -- just inspect how it was called

        with patch("aiohttp.web.run_app", side_effect=_fake_run_app):
            runpy.run_path(app_module_path, run_name="__main__")

        self.assertIs(captured_kwargs.get("access_log_class"), PathOnlyAccessLogger)
        # F2 (#163 round-2 review): this path's timeouts come from config.yaml's
        # `connection.shutdown_timeout`/`connection.keepalive_timeout` (10.0/75.0
        # today) -- a DIFFERENT source than `create_runner()`'s own hardcoded
        # 65/28.5 above, since `python app.py` and the gunicorn worker are two
        # independent run paths that must each be pinned on their own terms.
        self.assertEqual(captured_kwargs.get("shutdown_timeout"), 10.0)
        self.assertEqual(captured_kwargs.get("keepalive_timeout"), 75.0)


class HealthEndpointTests(unittest.IsolatedAsyncioTestCase):
    """Tests for the /health endpoint response structure."""

    async def test_health_returns_200_when_all_checks_pass(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(personas_loaded=True, prompts_loaded=True, config_loaded=True, env_vars=True, prod_guard=True)
        try:
            response = await _health_handler(MagicMock())
            self.assertEqual(response.status, 200)
            body = json.loads(response.body)
            self.assertEqual(body["status"], "healthy")
            self.assertEqual(body["version"], "1.0.0")
            self.assertTrue(all(body["checks"].values()))
        finally:
            _startup_checks.update(original)

    async def test_health_returns_503_when_check_fails(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(personas_loaded=True, prompts_loaded=False, config_loaded=True, env_vars=True, prod_guard=True)
        try:
            response = await _health_handler(MagicMock())
            self.assertEqual(response.status, 503)
            body = json.loads(response.body)
            self.assertEqual(body["status"], "unhealthy")
            self.assertFalse(body["checks"]["prompts_loaded"])
        finally:
            _startup_checks.update(original)

    async def test_health_response_has_required_fields(self):
        from app import _health_handler, _startup_checks
        original = dict(_startup_checks)
        _startup_checks.update(personas_loaded=True, prompts_loaded=True, config_loaded=True, env_vars=True, prod_guard=True)
        try:
            response = await _health_handler(MagicMock())
            body = json.loads(response.body)
            self.assertIn("status", body)
            self.assertIn("version", body)
            self.assertIn("checks", body)
            self.assertIn("personas_loaded", body["checks"])
            self.assertIn("prompts_loaded", body["checks"])
            self.assertIn("config_loaded", body["checks"])
            self.assertIn("env_vars", body["checks"])
        finally:
            _startup_checks.update(original)


if __name__ == "__main__":
    unittest.main()
