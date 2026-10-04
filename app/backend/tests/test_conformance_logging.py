"""Issue #233 (N32, split from #63): app.py self-timestamps every log line under
CONFORMANCE_TEST_HOOKS=1 so the .NET conformance harness's CapturedProcessOutput can prefer the
backend's own write-time instant over its own read-time one (see app.py's
_ConformanceTimestampFormatter doc comment for the full motivation).

Two layers of coverage:
  * TestConformanceTimestampFormatter drives the Formatter class directly against a hand-built
    LogRecord -- fast, no subprocess, pins the exact formatTime()/format-string shape.
  * TestModuleLevelInstall spawns real `python -c` subprocesses (app.py's formatter install is
    module-level code gated on conformance_hooks.HOOKS_ENABLED, which is frozen at import time --
    an in-process importlib.reload(app) would also re-run app.py's other startup side effects, so
    a subprocess is the faithful way to exercise both the enabled and disabled import-time paths
    without risking cross-test pollution of the shared `app` module every other test file in this
    suite already imports) proving: (a) the installed format matches the harness's expected shape
    end-to-end, (b) the default (hooks-disabled) format is byte-identical to logging.basicConfig's
    own default -- i.e. this change is completely inert in production.

Mutation-check: delete the `if conformance_hooks.HOOKS_ENABLED:` block in app.py (or make it
always take the disabled branch) and every TestModuleLevelInstall...enabled... test fails; force
it to always install the formatter and
test_disabled_by_default_format_is_byte_identical_to_before fails.
"""

import logging
import os
import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

import app  # noqa: E402

_BACKEND_DIR = Path(__file__).resolve().parents[1]


def _subprocess_env(**overrides: str) -> dict[str, str]:
    """A full copy of the current environment (Windows' asyncio/WinSock init needs SYSTEMROOT
    etc., which a hand-built minimal env silently lacks -- see this fix's own discovery) with
    CONFORMANCE_TEST_HOOKS/VERBOSE_LOGGING/VERBOSE_LOG_FILE cleared first so each subprocess call
    starts from a known-clean slate regardless of what the ambient shell or conftest.py already
    set, then the requested overrides applied on top."""
    env = os.environ.copy()
    for key in ("CONFORMANCE_TEST_HOOKS", "VERBOSE_LOGGING", "VERBOSE_LOG_FILE"):
        env.pop(key, None)
    env.update(overrides)
    return env


class TestConformanceTimestampFormatter:
    """Direct, subprocess-free coverage of the Formatter class itself."""

    def _format(self, *, created: float, msg: str = "boom", levelname: str = "ERROR", name: str = "test233") -> str:
        formatter = app._ConformanceTimestampFormatter("%(asctime)sZ %(levelname)s:%(name)s:%(message)s")
        record = logging.LogRecord(
            name=name,
            level=getattr(logging, levelname),
            pathname=__file__,
            lineno=1,
            msg=msg,
            args=None,
            exc_info=None,
        )
        record.created = created
        return formatter.format(record)

    def test_exact_shape_for_a_known_instant(self):
        # 2026-07-04T15:30:00.123456+00:00 as a POSIX timestamp.
        created = datetime(2026, 7, 4, 15, 30, 0, 123456, tzinfo=UTC).timestamp()
        result = self._format(created=created)
        assert result == "2026-07-04T15:30:00.123456Z ERROR:test233:boom"

    def test_uses_utc_regardless_of_local_timezone(self, monkeypatch):
        # record.created is always a UTC POSIX timestamp (time.time()'s contract); formatTime must
        # never reinterpret it through the local tz, which datetime.fromtimestamp(ts) (no tz=)
        # would silently do.
        created = datetime(2026, 1, 1, 0, 0, 0, 0, tzinfo=UTC).timestamp()
        result = self._format(created=created, msg="midnight utc")
        assert result.startswith("2026-01-01T00:00:00.000000Z ")

    def test_matches_the_harness_regex_shape(self):
        import re

        created = datetime(2026, 3, 15, 9, 5, 1, 7, tzinfo=UTC).timestamp()
        result = self._format(created=created)
        # Mirrors tests/conformance's CapturedProcessOutput.BackendTimestampPrefix exactly.
        assert re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z ", result)

    def test_does_not_mutate_the_message_or_level_formatting(self):
        created = datetime(2026, 7, 4, 15, 30, 0, 0, tzinfo=UTC).timestamp()
        result = self._format(created=created, msg="a message with: colons", levelname="WARNING", name="sonic-drive-in")
        assert result == "2026-07-04T15:30:00.000000Z WARNING:sonic-drive-in:a message with: colons"


class TestModuleLevelInstall:
    """Subprocess-level proof that app.py's conditional formatter install actually fires (or
    doesn't) at real process import time, for both CONFORMANCE_TEST_HOOKS states."""

    def _run(self, *, hooks_enabled: bool) -> str:
        script = (
            "import logging\n"
            "import app\n"
            "logging.getLogger('test233').error('boom')\n"
        )
        env = _subprocess_env(CONFORMANCE_TEST_HOOKS="1") if hooks_enabled else _subprocess_env()
        result = subprocess.run(
            [sys.executable, "-c", script],
            cwd=_BACKEND_DIR,
            env=env,
            capture_output=True,
            text=True,
            timeout=30,
            check=True,
        )
        return result.stderr

    def test_enabled_output_is_self_timestamped_in_the_shared_shape(self):
        import re

        stderr = self._run(hooks_enabled=True)
        lines = [line for line in stderr.splitlines() if "test233" in line]
        assert len(lines) == 1, f"expected exactly one matching line, got: {stderr!r}"
        assert re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z ERROR:test233:boom", lines[0])

    def test_disabled_by_default_format_is_byte_identical_to_before(self):
        stderr = self._run(hooks_enabled=False)
        lines = [line for line in stderr.splitlines() if "test233" in line]
        assert lines == ["ERROR:test233:boom"]

    def test_enabled_also_applies_to_the_access_log_handler(self):
        # access_log.py's PathOnlyAccessLogger routes through the same standard `logging` module
        # and root-logger handler -- no separate wiring, so it must pick up the same formatter
        # with zero extra code.
        import re

        script = (
            "import logging\n"
            "import app\n"
            "logging.getLogger('aiohttp.access').info('plain access log line')\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", script],
            cwd=_BACKEND_DIR,
            env=_subprocess_env(CONFORMANCE_TEST_HOOKS="1"),
            capture_output=True,
            text=True,
            timeout=30,
            check=True,
        )
        lines = [line for line in result.stderr.splitlines() if "aiohttp.access" in line]
        assert len(lines) == 1
        assert re.fullmatch(
            r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z INFO:aiohttp\.access:plain access log line",
            lines[0],
        )


class TestVerboseLoggingHandlerUntouched:
    """Out-of-scope guard (see this PR's description): the separate "sonic-verbose" (vlogger)
    handler gated by VERBOSE_LOGGING/extension.set_verbose_logging is a pre-existing, deliberately
    un-timestamped feature -- #233 only targets CONFORMANCE_TEST_HOOKS-gated root-logger output,
    not this one. Proves the two are actually independent: enabling conformance hooks must not
    change the verbose logger's own "%(message)s"-only format. audio_pipeline.py only attaches a
    handler to the "sonic-verbose" logger when VERBOSE_LOGGING is set at import time, so this has
    to run as its own subprocess with both env vars set."""

    def test_verbose_logger_format_is_unaffected_by_conformance_hooks(self):
        script = (
            "import logging\n"
            "import audio_pipeline\n"
            "import app\n"
            "assert audio_pipeline.vlogger.handlers, 'expected sonic-verbose to have a handler'\n"
            "for h in audio_pipeline.vlogger.handlers:\n"
            "    assert h.formatter._fmt == '%(message)s', h.formatter._fmt\n"
            "print('OK')\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", script],
            cwd=_BACKEND_DIR,
            env=_subprocess_env(CONFORMANCE_TEST_HOOKS="1", VERBOSE_LOGGING="1"),
            capture_output=True,
            text=True,
            timeout=30,
        )
        assert result.returncode == 0, result.stderr
        assert "OK" in result.stdout


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
