"""Repo-root pytest configuration.

Loaded before ANY test module in the whole session (pytest always imports the rootdir's own
`conftest.py` first, and this repo's rootdir is here -- `pyproject.toml` sits alongside this file).

Issue #81 part 2 added a real Python PACKAGE at the repo root: `local_runtime/` (the on-device
companion service, `local_runtime/__init__.py`). That collides with an unrelated, pre-existing
module of the exact same bare name: `app/backend/local_runtime.py` (part 1's HTTP client to that
companion service). Every file under `app/backend/tests/` already adds `app/backend` to `sys.path`
itself (`sys.path.append(...)`/`sys.path.insert(0, ...)`, one or the other, an existing per-file
convention this file does not change) so that `app.py`/`local_processor.py`'s own bare
`import local_runtime` resolves to the sibling FILE, as designed. That has always worked because
nothing at the repo root was ever importable by that name -- until now.

The trouble is `python -m pytest` (however invoked, e.g. this repo's own CI
`.github/workflows/conformance.yml` step, or the ad-hoc `python -m pytest -q`) auto-inserts the
current working directory (repo root, resolved from the `''` sys.path entry) alongside whatever
each test file appends. If that entry sits ahead of `app/backend` in `sys.path` -- which it
does, since app/backend/tests/test_app.py is the very first file pytest collects alphabetically,
and it uses `sys.path.append` (lowest priority) -- Python resolves the very first bare
`import local_runtime` anywhere in the whole test session against the repo-root PACKAGE instead of
the intended sibling FILE, and every test that transitively imports `app.py`/`local_processor.py`
fails with `ImportError: cannot import name 'HttpLocalRuntimeClient' from 'local_runtime'`.

The fix: drop the repo root (and the bare `''` cwd marker) from `sys.path` here, before any test
file's own module-level code runs. Every existing test file's own explicit
`sys.path.append/insert` of `app/backend` (or another specific absolute directory) is completely
unaffected -- none of them rely on the ambient repo-root entry for anything, so this is a pure
subtraction with no behavior change for the existing suite. `local_runtime/tests/conftest.py` (this
package's OWN tests) deliberately re-adds the repo root afterwards, scoped to when it actually needs
the real package.
"""

from __future__ import annotations

import sys
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parent
_repo_root_str = str(_REPO_ROOT)

for _entry in ("", _repo_root_str):
    while _entry in sys.path:
        sys.path.remove(_entry)
