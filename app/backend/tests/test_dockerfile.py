"""Unit tests for app/Dockerfile's runtime CMD line (issue #144, design doc section
18.4).

This is a plain text/regex check against the Dockerfile itself, not a build --
`.github/workflows/conformance.yml`'s Docker job builds and boots the real image
separately (the CI image-boot check, both positive and negative).

Scope note (coordination with #146, Squanchy's `squad/146-entra-infra-scripts`,
which edits the SAME Dockerfile for build-stage `ARG VITE_AUTH_MODE` etc.): this
file only asserts on the runtime CMD line and its immediate neighborhood, kept
deliberately narrow so the two branches' Dockerfile diffs don't fight each other.
"""
import re
import unittest
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[3]
_DOCKERFILE = _REPO_ROOT / "app" / "Dockerfile"


class DockerfileCmdTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(_DOCKERFILE.is_file(), f"expected {_DOCKERFILE} to exist")
        self.text = _DOCKERFILE.read_text(encoding="utf-8")

    def _cmd_block(self) -> str:
        """The final `CMD [...]` instruction (the runtime gunicorn command), not
        the earlier `HEALTHCHECK ... CMD ...` line."""
        match = re.search(r"^CMD\s*\[.*?\]\s*$", self.text, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match, "expected a final `CMD [...]` instruction in the Dockerfile")
        return match.group(0)

    def test_cmd_targets_create_runner_factory(self):
        """`create_runner()` is the async factory gunicorn's `GunicornWebWorker`
        needs -- not the plain `app:create_app`/`web.Application` target this
        replaced (18.4: gunicorn's own `--keep-alive`/`--graceful-timeout` flags
        are otherwise ignored by that worker class)."""
        cmd = self._cmd_block()
        self.assertIn("app:create_runner", cmd)
        self.assertNotIn("app:create_app", cmd)

    def test_cmd_does_not_set_keep_alive_flag(self):
        """`--keep-alive` is silently ignored by `GunicornWebWorker` -- the REAL
        keep-alive timeout now lives in `create_runner()`'s `web.AppRunner`
        (65s). Leaving a stale `--keep-alive` flag in the Dockerfile would be
        misleading (implies control it no longer has)."""
        cmd = self._cmd_block()
        self.assertNotIn("--keep-alive", cmd)

    def test_cmd_keeps_graceful_timeout_flag(self):
        """`--graceful-timeout` IS still honored by gunicorn's arbiter itself
        (independent of the worker class) -- must be kept, unlike `--keep-alive`."""
        cmd = self._cmd_block()
        self.assertIn("--graceful-timeout", cmd)

    def test_cmd_never_sets_access_logformat(self):
        """`GunicornWebWorker._get_valid_log_format` raises `ValueError` on any
        gunicorn `%(name)s` access-log directive (aiohttp 3.14.3's `worker.py`),
        so `--access-logformat` would crash the worker at boot -- the path-only
        access logging lives entirely in `create_runner()`'s
        `access_log_kwargs()` / `PathOnlyAccessLogger` instead. Checked across
        every non-comment line of the Dockerfile (a `#`-prefixed line is allowed
        to mention the flag name while explaining why it's absent, as this file
        itself does)."""
        code_lines = [
            line for line in self.text.splitlines() if line.strip() and not line.strip().startswith("#")
        ]
        offending = [line for line in code_lines if "--access-logformat" in line]
        self.assertEqual(offending, [], f"--access-logformat must never appear as an actual flag: {offending}")

    def test_cmd_uses_gunicorn_web_worker_class(self):
        cmd = self._cmd_block()
        self.assertIn("aiohttp.GunicornWebWorker", cmd)

    def test_only_one_final_cmd_instruction(self):
        """Docker only honors the LAST top-level `CMD` instruction -- a second one
        added by mistake (e.g. during the #146 build-arg merge) would silently
        replace this one. Deliberately excludes `HEALTHCHECK`'s own indented
        `CMD` sub-instruction, which isn't a top-level Dockerfile instruction."""
        cmd_lines = [line for line in self.text.splitlines() if line.startswith("CMD")]
        self.assertEqual(len(cmd_lines), 1, f"expected exactly one top-level CMD instruction, found: {cmd_lines}")


if __name__ == "__main__":
    unittest.main()
