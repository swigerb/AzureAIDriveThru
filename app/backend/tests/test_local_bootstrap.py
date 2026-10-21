"""Execute local setup entrypoints against fake package installers, without network access."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SH = shutil.which("sh")
PWSH = shutil.which("pwsh")


@unittest.skipUnless(SH, "POSIX shell required (Git for Windows supplies sh)")
class ShellBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for folder in ("scripts", "app/backend", "app/frontend", "bin"):
            (self.root / folder).mkdir(parents=True, exist_ok=True)
        for script in ("load_python_env.sh", "start.sh", "setup_search_index.sh", "smoke_realtime.sh"):
            shutil.copyfile(REPO / "scripts" / script, self.root / "scripts" / script)
        self.log = self.root / "calls.log"
        self.python = self.root / "fake-python"
        self.python.write_text(
            '#!/bin/sh\n'
            'printf "python|%s|%s|%s\\n" "$PYYAML_FORCE_LIBYAML" "$PWD" "$*" >> "$CALL_LOG"\n'
            'if [ "$1 $2" = "-m pip" ]; then exit "${FAIL_PIP:-0}"; fi\n',
            encoding="utf-8",
        )
        bootstrap = self.root / "bin" / "python3"
        bootstrap.write_text(
            '#!/bin/sh\n'
            'printf "venv|%s\\n" "$*" >> "$CALL_LOG"\n'
            'if [ "${FAIL_VENV:-0}" != 0 ]; then exit "$FAIL_VENV"; fi\n'
            'mkdir -p "$3/bin"\n'
            'cp "$FAKE_PYTHON" "$3/bin/python"\n'
            'chmod +x "$3/bin/python"\n',
            encoding="utf-8",
        )
        npm = self.root / "bin" / "npm"
        npm.write_text(
            '#!/bin/sh\n'
            'printf "npm|%s|%s\\n" "$VITE_AUTH_MODE" "$*" >> "$CALL_LOG"\n'
            'exit "${FAIL_NPM:-0}"\n',
            encoding="utf-8",
        )
        for executable in (self.python, bootstrap, npm):
            executable.chmod(0o755)
        self.env = {
            **os.environ,
            "PATH": str(self.root / "bin") + os.pathsep + os.environ["PATH"],
            "CALL_LOG": self.log.as_posix(),
            "FAKE_PYTHON": self.python.as_posix(),
            "PYYAML_FORCE_LIBYAML": "previous",
            "VITE_AUTH_MODE": "",
            "SONIC_SKIP_REALTIME_SMOKE": "",
        }

    def run_script(self, name, *args, cwd=None):
        return subprocess.run(
            [SH, (self.root / "scripts" / name).as_posix(), *args],
            cwd=cwd or self.root,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=30,
        )

    def calls(self):
        return self.log.read_text(encoding="utf-8")

    def test_bootstrap_uses_root_venv_and_scopes_pure_python_install(self):
        result = self.run_script("load_python_env.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("venv|-m venv .venv", self.calls())
        self.assertIn("python|0|", self.calls())
        self.assertIn("-r app/backend/requirements.txt", self.calls())
        self.assertTrue((self.root / ".venv/bin/python").is_file())
        result = self.run_script("load_python_env.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls().count("venv|"), 1)

    def test_start_uses_installed_interpreter_from_any_working_directory(self):
        result = self.run_script("start.sh", cwd=self.root / "app")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("npm|Development|run build", self.calls())
        self.assertIn("python|previous|", self.calls())
        self.assertIn("/app/backend|-m app", self.calls())

    def test_production_start_uses_venv_gunicorn_without_npm(self):
        result = self.run_script("start.sh", "--production")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("/app/backend|-m gunicorn app:create_app", self.calls())
        self.assertNotIn("npm|", self.calls())

    def test_index_wrapper_uses_same_root_venv(self):
        result = self.run_script("setup_search_index.sh", cwd=self.root / "app")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("python|previous|", self.calls())
        self.assertIn("/app/backend/setup_search_index.py", self.calls())
        self.assertTrue((self.root / ".venv/bin/python").is_file())

    def test_install_failure_stops_every_caller(self):
        self.env["FAIL_PIP"] = "42"
        for script in ("load_python_env.sh", "start.sh", "setup_search_index.sh"):
            with self.subTest(script=script):
                self.log.unlink(missing_ok=True)
                result = self.run_script(script)
                self.assertEqual(result.returncode, 42, result.stderr)
                self.assertNotIn("npm|", self.calls())
                self.assertNotIn("|-m app", self.calls())
                self.assertNotIn("/app/backend/setup_search_index.py", self.calls())

    def test_smoke_prefers_root_venv_over_legacy_backend_venv(self):
        result = self.run_script("load_python_env.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        legacy = self.root / "app/backend/.venv/bin/python"
        legacy.parent.mkdir(parents=True)
        legacy.write_text('#!/bin/sh\necho legacy >> "$CALL_LOG"\nexit 45\n', encoding="utf-8")
        legacy.chmod(0o755)
        self.log.unlink()
        result = self.run_script("smoke_realtime.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("legacy", self.calls())
        self.assertIn("/scripts/smoke_realtime.py --pipeline realtime", self.calls())
        self.assertIn("/scripts/smoke_realtime.py --pipeline cascade", self.calls())

    def test_venv_failure_stops_before_install(self):
        self.env["FAIL_VENV"] = "43"
        result = self.run_script("start.sh")
        self.assertEqual(result.returncode, 43, result.stderr)
        self.assertNotIn("python|", self.calls())

    def test_frontend_failure_stops_before_backend(self):
        self.env["FAIL_NPM"] = "44"
        result = self.run_script("start.sh")
        self.assertEqual(result.returncode, 44, result.stderr)
        self.assertNotIn("|-m app", self.calls())


@unittest.skipUnless(PWSH, "PowerShell 7 required")
class PowerShellBootstrapTests(unittest.TestCase):
    def test_pip_failure_is_reported_and_yaml_setting_is_restored(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "app/backend").mkdir(parents=True)
            subprocess.run(
                [sys.executable, "-m", "venv", "--without-pip", str(root / ".venv")],
                check=True,
                capture_output=True,
                timeout=30,
            )
            (root / "pip.py").write_text(
                'import json, os, sys\n'
                'from pathlib import Path\n'
                'Path("pip-call.json").write_text(json.dumps({'
                '"yaml": os.environ.get("PYYAML_FORCE_LIBYAML"), "args": sys.argv[1:]}))\n'
                'sys.exit(42)\n',
                encoding="utf-8",
            )
            script = REPO / "scripts/load_python_env.ps1"
            command = (
                "$env:PYYAML_FORCE_LIBYAML='previous'; "
                f"try {{ & '{str(script).replace(chr(39), chr(39) * 2)}' }} "
                "finally { Write-Output ('restored=' + $env:PYYAML_FORCE_LIBYAML) }"
            )
            result = subprocess.run(
                [PWSH, "-NoProfile", "-Command", command],
                cwd=root,
                env={**os.environ, "PYTHONPATH": str(root)},
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Failed to install backend Python dependencies", result.stderr)
            self.assertIn("restored=previous", result.stdout)
            call = json.loads((root / "pip-call.json").read_text())
            self.assertEqual(call["yaml"], "0")
            self.assertEqual(call["args"][:2], ["install", "-r"])
            self.assertEqual(Path(call["args"][2]), root / "app/backend/requirements.txt")
