"""Guard the #311 infra contract: BACKEND_URI / BACKEND_DOTNET_URI injection into both container
apps, and the .NET container app's default name fitting the 32-char Container Apps limit.

See infra/main.bicep (backendAppName/dotnetAppName/backendUri/dotnetUri vars, and the acaBackend /
acaBackendDotnet modules' env blocks).
"""

import re
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
MAIN_BICEP = REPO / "infra" / "main.bicep"

# Production's pinned override (AZURE_CONTAINER_APP_DOTNET_NAME) -- the new default must produce
# this exact name when resourceToken happens to be 'pwvzk3t22wttm'.
_PRODUCTION_DOTNET_NAME = "capps-dotnet-pwvzk3t22wttm"
_MAX_CONTAINER_APP_NAME_LENGTH = 32


def _module_block(text: str, module_name: str) -> str:
    """Extract exactly one `module <module_name> '...' = ... { ... }` block via brace matching,
    so it never overreaches into an unrelated following module/var (unlike a fixed-token lookahead
    like the next `\\nvar `)."""
    start = text.index(f"module {module_name} ")
    open_pos = text.index("{", start)
    depth = 0
    for i in range(open_pos, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    raise AssertionError(f"unbalanced braces scanning for module {module_name}")


class BackendUriInjectionTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.bicep_text = MAIN_BICEP.read_text(encoding="utf-8")
        cls.aca_backend = _module_block(cls.bicep_text, "acaBackend")
        cls.aca_backend_dotnet = _module_block(cls.bicep_text, "acaBackendDotnet")

    def test_both_apps_inject_backend_uri_when_python_ingress_enabled(self):
        for block in (self.aca_backend, self.aca_backend_dotnet):
            self.assertRegex(
                block,
                r"backendIngressEnabled\s*\?\s*\{\s*BACKEND_URI:\s*backendUri\s*\}\s*:\s*\{\s*\}",
                "expected BACKEND_URI included only when backendIngressEnabled is true",
            )

    def test_both_apps_inject_backend_dotnet_uri_only_when_deployed_and_ingress_on(self):
        for block in (self.aca_backend, self.aca_backend_dotnet):
            self.assertRegex(
                block,
                r"\(deployDotnetApp\s*&&\s*backendDotnetIngressEnabled\)\s*\?\s*"
                r"\{\s*BACKEND_DOTNET_URI:\s*dotnetUri\s*\}\s*:\s*\{\s*\}",
                "expected BACKEND_DOTNET_URI gated on deployDotnetApp && backendDotnetIngressEnabled",
            )

    def test_uris_are_computed_from_name_and_environment_default_domain_not_the_other_module(self):
        # No module-output cycle (coordinator brief, #311): neither app's env should reference the
        # *other* app's module output (acaBackend.outputs.uri / acaBackendDotnet.outputs.uri).
        self.assertNotIn("acaBackend.outputs.uri", self.aca_backend_dotnet)
        self.assertNotIn("acaBackendDotnet.outputs.uri", self.aca_backend)
        self.assertRegex(
            self.bicep_text,
            r"var backendUri = 'https://\$\{backendAppName\}\.\$\{containerApps\.outputs\.defaultDomain\}'",
        )
        self.assertRegex(
            self.bicep_text,
            r"var dotnetUri = 'https://\$\{dotnetAppName\}\.\$\{containerApps\.outputs\.defaultDomain\}'",
        )

    def test_both_app_modules_use_the_shared_name_variables(self):
        self.assertRegex(self.aca_backend, r"name:\s*backendAppName")
        self.assertRegex(self.aca_backend_dotnet, r"name:\s*dotnetAppName")


class DotnetDefaultNameLengthTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.bicep_text = MAIN_BICEP.read_text(encoding="utf-8")

    def _dotnet_app_name_expr(self) -> str:
        match = re.search(r"var dotnetAppName = (.+)", self.bicep_text)
        self.assertIsNotNone(match, "expected a dotnetAppName var in main.bicep")
        return match.group(1)

    def test_dotnet_default_name_matches_production_override_exactly(self):
        # abbrs.webSitesContainerApps == 'capps-' (infra/abbreviations.json); resourceToken is a
        # 13-char uniqueString(). 'capps-dotnet-' + 'pwvzk3t22wttm' == the pinned production name.
        resource_token = "pwvzk3t22wttm"
        expr = self._dotnet_app_name_expr()
        default_literal_match = re.search(r"'\$\{abbrs\.webSitesContainerApps\}([a-z0-9-]*)\$\{resourceToken\}'", expr)
        self.assertIsNotNone(default_literal_match, f"could not find the default-name literal in: {expr}")
        suffix = default_literal_match.group(1)
        computed_name = f"capps-{suffix}{resource_token}"
        self.assertEqual(computed_name, _PRODUCTION_DOTNET_NAME)

    def test_dotnet_default_name_is_32_characters_or_fewer_for_any_13_char_token(self):
        expr = self._dotnet_app_name_expr()
        default_literal_match = re.search(r"'\$\{abbrs\.webSitesContainerApps\}([a-z0-9-]*)\$\{resourceToken\}'", expr)
        self.assertIsNotNone(default_literal_match)
        suffix = default_literal_match.group(1)
        # uniqueString() always returns exactly 13 lowercase alphanumeric characters.
        worst_case_token = "p" * 13
        computed_name = f"capps-{suffix}{worst_case_token}"
        self.assertLessEqual(len(computed_name), _MAX_CONTAINER_APP_NAME_LENGTH)

    def test_dotnet_service_name_override_still_takes_precedence(self):
        expr = self._dotnet_app_name_expr()
        self.assertRegex(expr, r"!empty\(dotnetServiceName\)\s*\?\s*dotnetServiceName\s*:")


if __name__ == "__main__":
    unittest.main()
