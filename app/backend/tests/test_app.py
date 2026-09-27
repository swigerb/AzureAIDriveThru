import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.append(str(Path(__file__).resolve().parents[1]))

from app import _get_bool_env


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
