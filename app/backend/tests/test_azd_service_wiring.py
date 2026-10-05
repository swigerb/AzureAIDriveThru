"""Guard the azd service name <-> infra parameter wiring.

azd exports SERVICE_<NAME>_RESOURCE_EXISTS for each service in azure.yaml
(name upper-cased, '-' -> '_'). If main.parameters.json reads a variable for a
service that doesn't exist, `exists` is always false and every `azd provision`
redeploys the container app with the helloworld placeholder image (prod,
2026-09-22: SERVICE_WEB_* was read while the service is named `backend`).
"""

import json
import re
import unittest
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
AZURE_YAML = REPO / "azure.yaml"
PARAMS = REPO / "infra" / "main.parameters.json"
MAIN_BICEP = REPO / "infra" / "main.bicep"

_EXISTS_VAR = re.compile(r"\$\{SERVICE_([A-Z0-9_]+)_RESOURCE_EXISTS(?:=[^}]*)?\}")
_SERVICE_TAG = re.compile(r"'azd-service-name'\s*:\s*'([^']+)'")
# Matches a top-level `module <name> '<path>' = [if (<cond>)] {` declaration so a tag can be
# attributed to the module (and its gating condition, if any) that contains it.
_MODULE_HEADER = re.compile(r"^module\s+(\w+)\s+'[^']+'\s*=\s*(?:if\s*\(([^)]*)\)\s*)?\{", re.MULTILINE)
# Rick's #281 review, item 6: the specific gate the `backend-dotnet` tag must stay inside.
_DOTNET_GATE_PARAM = "deployDotnetApp"


def _azd_env_name(service: str) -> str:
    return service.upper().replace("-", "_")


# Rick's #281 review, item 1/6: `dotnetWebAppExists` is deliberately pre-staged ahead of the
# `backend-dotnet` azure.yaml service entry, which only lands in the owner-gated flip commit (see
# DEPLOY.md's ".NET container app (S7, #17)" section, "Step 0") -- until then azd has no such
# service to set SERVICE_BACKEND_DOTNET_RESOURCE_EXISTS for, and the param just keeps its `false`
# default. This is the one documented exception to the "every exists-var names a real service"
# rule below; it must stay a single, explicit, named exception -- not a loophole other params can
# quietly reuse -- so anything added here needs its own review-sourced justification like this one.
_PRE_STAGED_EXISTS_PARAMS = {"dotnetWebAppExists"}


def _find_module_bodies(bicep_text: str) -> list[tuple[str, str | None, str]]:
    """Return (module_name, if_condition_or_None, module_body_text) for every top-level module."""
    modules = []
    for match in _MODULE_HEADER.finditer(bicep_text):
        name = match.group(1)
        condition = match.group(2)
        # Brace-count from the opening '{' (the last char of the match) to find the matching close.
        depth = 1
        pos = match.end()
        start_body = pos
        while depth > 0 and pos < len(bicep_text):
            char = bicep_text[pos]
            if char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
            pos += 1
        modules.append((name, condition, bicep_text[start_body:pos - 1]))
    return modules


class AzdServiceWiringTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.services = yaml.safe_load(AZURE_YAML.read_text(encoding="utf-8"))["services"]
        cls.params = json.loads(PARAMS.read_text(encoding="utf-8"))["parameters"]

    def _exists_vars(self) -> dict[str, str]:
        found = {}
        for param, spec in self.params.items():
            match = _EXISTS_VAR.search(json.dumps(spec.get("value", "")))
            if match:
                found[param] = match.group(1)
        return found

    def test_resource_exists_vars_name_real_azd_services(self):
        exists_vars = self._exists_vars()
        self.assertTrue(exists_vars, "no SERVICE_*_RESOURCE_EXISTS mapping found in main.parameters.json")
        known = {_azd_env_name(s): s for s in self.services}
        for param, env_service in exists_vars.items():
            if param in _PRE_STAGED_EXISTS_PARAMS:
                continue
            self.assertIn(
                env_service, known,
                f"{param} reads SERVICE_{env_service}_RESOURCE_EXISTS, but azure.yaml services are "
                f"{sorted(self.services)} -> azd never sets it, so `exists` is always false and "
                "provision falls back to the helloworld image")

    def test_every_containerapp_service_has_an_exists_mapping(self):
        mapped = set(self._exists_vars().values())
        for name, svc in self.services.items():
            if svc.get("host") == "containerapp":
                self.assertIn(_azd_env_name(name), mapped,
                              f"containerapp service '{name}' has no SERVICE_*_RESOURCE_EXISTS parameter")

    def test_bicep_service_tags_match_azure_yaml(self):
        tags = set(_SERVICE_TAG.findall(MAIN_BICEP.read_text(encoding="utf-8")))
        self.assertTrue(tags, "no azd-service-name tag found in main.bicep")
        self.assertLessEqual(tags, set(self.services), "azd-service-name tag not declared in azure.yaml")

    def test_every_containerapp_service_has_a_bicep_tag(self):
        # Rick's #281 review, item 6: the old subset check only caught a tag with no service
        # (every tag ⊆ services). It missed the other direction -- a declared `host: containerapp`
        # service with NO resource anywhere carrying its tag, which is exactly how a bare
        # `azd deploy`/`azd up` breaks for every environment that has not flipped the matching
        # gate flag on (see azure.yaml's S7 comment and DEPLOY.md's ".NET container app" section).
        tags = set(_SERVICE_TAG.findall(MAIN_BICEP.read_text(encoding="utf-8")))
        for name, svc in self.services.items():
            if svc.get("host") == "containerapp":
                self.assertIn(
                    name, tags,
                    f"containerapp service '{name}' is declared in azure.yaml but no "
                    f"'azd-service-name': '{name}' tag exists anywhere in main.bicep -- a bare "
                    "`azd deploy`/`azd up` will fail resolving it")

    def test_dotnet_tag_is_gated_behind_deploy_flag(self):
        # Rick's #281 review, item 6: the `backend-dotnet` tag (once it lands in the owner-gated
        # flip commit, see DEPLOY.md's "Step 0") must live inside the `acaBackendDotnet` module's
        # `if (deployDotnetApp)` condition -- not unconditionally -- so an environment that has not
        # flipped DEPLOY_DOTNET_APP=true never provisions a second always-on Container App.
        bicep_text = MAIN_BICEP.read_text(encoding="utf-8")
        modules = _find_module_bodies(bicep_text)
        tagging_modules = [
            (name, condition) for name, condition, body in modules
            if "'azd-service-name': 'backend-dotnet'" in body
        ]
        if not tagging_modules:
            # Not landed yet (this PR keeps it out of azure.yaml/bicep until the flip commit) --
            # nothing to gate yet. test_every_containerapp_service_has_a_bicep_tag above still
            # catches the case where the azure.yaml service exists without a matching tag.
            return
        for name, condition in tagging_modules:
            self.assertIsNotNone(
                condition,
                f"module '{name}' carries the 'backend-dotnet' azd-service-name tag "
                f"unconditionally -- it must be gated `if ({_DOTNET_GATE_PARAM})` so an "
                "environment with the flag off never provisions this Container App")
            self.assertIn(
                _DOTNET_GATE_PARAM, condition,
                f"module '{name}' is gated on `{condition}`, not `{_DOTNET_GATE_PARAM}` -- "
                "the backend-dotnet tag must sit behind that specific flag")

    def test_deploy_dotnet_app_defaults_false(self):
        # Rick's #281 review, item 6: the gate itself must default off, in both the bicep param
        # and the azd env mapping, so a fresh environment that never sets DEPLOY_DOTNET_APP stays
        # cost-neutral.
        bicep_text = MAIN_BICEP.read_text(encoding="utf-8")
        self.assertRegex(
            bicep_text, rf"param\s+{_DOTNET_GATE_PARAM}\s+bool\s*=\s*false",
            f"infra/main.bicep's '{_DOTNET_GATE_PARAM}' parameter must default to false")
        value = self.params.get(_DOTNET_GATE_PARAM, {}).get("value", "")
        self.assertEqual(
            value, "${DEPLOY_DOTNET_APP=false}",
            f"infra/main.parameters.json's '{_DOTNET_GATE_PARAM}' must map to "
            "${DEPLOY_DOTNET_APP=false} so an unset azd env var keeps the flag off")


if __name__ == "__main__":
    unittest.main()
