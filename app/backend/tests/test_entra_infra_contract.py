"""Guard the ADR-002 Entra infra/build contract (issue #146): pins on both apps, no EasyAuth
remnants anywhere, build-arg wiring for the frontend's baked-in Entra config, and the
ingress-last dark-provision switch on the Python app.

See docs/adr/ADR-002-entra-authentication.md and docs/persona-architecture.md section 18.8.
"""

import json
import unittest
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
AZURE_YAML = REPO / "azure.yaml"
PARAMS = REPO / "infra" / "main.parameters.json"
MAIN_BICEP = REPO / "infra" / "main.bicep"
DOCKERFILE = REPO / "app" / "Dockerfile"
DOCKERFILE_DOTNET = REPO / "app" / "Dockerfile.dotnet"
CONTAINER_APP_AUTH_BICEP = REPO / "infra" / "core" / "security" / "container-app-auth.bicep"

_VITE_BUILD_ARGS = {
    "VITE_AUTH_MODE",
    "VITE_ENTRA_TENANT_ID",
    "VITE_ENTRA_CLIENT_ID",
    "VITE_ENTRA_API_SCOPE",
}

# Anything left over from the removed EasyAuth (`az containerapp auth` / authConfigs) approach.
# None of these identifiers should appear anywhere in main.bicep after #146.
_EASYAUTH_REMNANTS = [
    "enableAuth",
    "authClientId",
    "authClientSecret",
    "authTenantId",
    "aad-client-secret",
    "containerAppAuth",
    "container-app-auth.bicep",
]


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


class EntraInfraPinsTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.bicep_text = MAIN_BICEP.read_text(encoding="utf-8")
        cls.aca_backend = _module_block(cls.bicep_text, "acaBackend")
        cls.aca_backend_dotnet = _module_block(cls.bicep_text, "acaBackendDotnet")

    def test_python_app_pins_auth_mode_entra_and_production_flag(self):
        self.assertRegex(self.aca_backend, r"AUTH_MODE:\s*'Entra'")
        self.assertRegex(self.aca_backend, r"RUNNING_IN_PRODUCTION:\s*'true'")

    def test_dotnet_app_pins_auth_mode_entra_and_production_flag(self):
        self.assertRegex(self.aca_backend_dotnet, r"AUTH_MODE:\s*'Entra'")
        self.assertRegex(self.aca_backend_dotnet, r"RUNNING_IN_PRODUCTION:\s*'true'")
        self.assertRegex(self.aca_backend_dotnet, r"ASPNETCORE_ENVIRONMENT:\s*'Production'")
        self.assertRegex(self.aca_backend_dotnet, r"APP_SESSION_SECRET:\s*'app-session-secret'")

    def test_both_apps_share_the_same_entra_ids(self):
        for block in (self.aca_backend, self.aca_backend_dotnet):
            self.assertRegex(block, r"ENTRA_TENANT_ID:\s*effectiveEntraTenantId")
            self.assertRegex(block, r"ENTRA_CLIENT_ID:\s*entraClientId")
            self.assertRegex(block, r"ENTRA_API_SCOPE:\s*entraApiScope")
            self.assertRegex(block, r"ENTRA_APP_ROLE:\s*entraAppRole")

    def test_no_app_sets_entra_instance(self):
        # ENTRA_INSTANCE is a harness-only knob (persona-architecture.md 18.5/18.11); Azure must
        # never set it, so both backends' resolver always talks to real Entra.
        self.assertNotIn("ENTRA_INSTANCE", self.bicep_text)

    def test_no_easyauth_remnants_in_main_bicep(self):
        for needle in _EASYAUTH_REMNANTS:
            self.assertNotIn(needle, self.bicep_text, f"found EasyAuth remnant '{needle}' in main.bicep")

    def test_container_app_auth_module_file_is_deleted(self):
        self.assertFalse(CONTAINER_APP_AUTH_BICEP.exists(),
                          "infra/core/security/container-app-auth.bicep should be deleted with EasyAuth")

    def test_no_easyauth_params_in_main_parameters_json(self):
        params = json.loads(PARAMS.read_text(encoding="utf-8"))["parameters"]
        for needle in ("enableAuth", "authClientId", "authClientSecret", "authTenantId"):
            self.assertNotIn(needle, params)


class BackendIngressSwitchTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.bicep_text = MAIN_BICEP.read_text(encoding="utf-8")
        cls.params = json.loads(PARAMS.read_text(encoding="utf-8"))["parameters"]
        cls.aca_backend = _module_block(cls.bicep_text, "acaBackend")

    def test_backend_ingress_enabled_param_defaults_true(self):
        self.assertRegex(self.bicep_text, r"param backendIngressEnabled bool = true")

    def test_backend_ingress_enabled_mapped_from_azd_env(self):
        self.assertEqual(self.params["backendIngressEnabled"]["value"], "${BACKEND_INGRESS_ENABLED=true}")

    def test_backend_ingress_enabled_wired_to_python_app(self):
        self.assertRegex(self.aca_backend, r"ingressEnabled:\s*backendIngressEnabled")


class BuildArgWiringTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.services = yaml.safe_load(AZURE_YAML.read_text(encoding="utf-8"))["services"]

    def test_every_service_build_args_carry_the_four_vite_values(self):
        for name, svc in self.services.items():
            docker = svc.get("docker") or {}
            build_args = docker.get("buildArgs")
            self.assertTrue(build_args, f"service '{name}' has no docker.buildArgs")
            keys = {arg.split("=", 1)[0] for arg in build_args}
            self.assertEqual(keys, _VITE_BUILD_ARGS, f"service '{name}' buildArgs keys mismatch")

    def test_build_arg_mode_defaults_to_entra_not_development(self):
        for name, svc in self.services.items():
            build_args = (svc.get("docker") or {}).get("buildArgs") or []
            mode_arg = next(a for a in build_args if a.startswith("VITE_AUTH_MODE="))
            self.assertEqual(mode_arg, "VITE_AUTH_MODE=Entra", f"service '{name}' should default to Entra")


class DockerfileArgTests(unittest.TestCase):

    def _assert_dockerfile_declares_entra_args(self, path: Path):
        text = path.read_text(encoding="utf-8")
        self.assertRegex(text, r"ARG VITE_AUTH_MODE=Entra")
        for arg in ("VITE_ENTRA_TENANT_ID", "VITE_ENTRA_CLIENT_ID", "VITE_ENTRA_API_SCOPE"):
            self.assertRegex(text, rf"ARG {arg}\b")
            self.assertRegex(text, rf"ENV {arg}=\$\{{{arg}\}}")

    def test_python_dockerfile_declares_entra_build_args(self):
        self._assert_dockerfile_declares_entra_args(DOCKERFILE)

    def test_dotnet_dockerfile_declares_entra_build_args(self):
        self._assert_dockerfile_declares_entra_args(DOCKERFILE_DOTNET)

    def test_no_dotenv_generation_in_dockerfiles(self):
        for path in (DOCKERFILE, DOCKERFILE_DOTNET):
            text = path.read_text(encoding="utf-8")
            self.assertNotIn(".env", text, f"{path} should no longer generate/read a frontend .env file")

    def test_dockerignore_has_no_frontend_env_exception(self):
        dockerignore = (REPO / ".dockerignore").read_text(encoding="utf-8")
        self.assertNotIn("!app/frontend/.env", dockerignore)


if __name__ == "__main__":
    unittest.main()
