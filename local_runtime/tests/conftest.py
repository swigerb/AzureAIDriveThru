"""Process-wide pytest configuration for local_runtime/tests.

`app/backend/tests/test_local_runtime.py` (part 1, not touched here) inserts `app/backend` onto
`sys.path` and does `from local_runtime import (...)` to reach ITS OWN `app/backend/local_runtime.py`
file -- an unfortunate bare-name collision with THIS package (`local_runtime/`, the part-2 companion
service). `python -m pytest -q` with no path arguments collects every `test_*.py` under the repo
root in one process, and pytest's default rootdir walk visits `app/` before `local_runtime/`
(alphabetical), so by the time this directory is collected, `sys.modules["local_runtime"]` is
already bound to that plain FILE module, not this real PACKAGE -- and `import local_runtime.config`
etc. would otherwise fail here with "'local_runtime' is not a package".

This fixes it for every module under `local_runtime/tests/`: if the cached `local_runtime` entry is
a plain module (no `__path__`), it's dropped and the real package is (re-)imported fresh. This is
one-directional by design -- it only ever needs to repair the mistake in THIS process's favor,
right before importing anything from this package; `app/backend/tests/test_local_runtime.py` itself
already bound the names it needs (`HttpLocalRuntimeClient` etc.) into its own module namespace at
its own import time, before this file ever runs, so removing the stale cache entry afterwards can't
affect it.
"""

from __future__ import annotations

import sys
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[2]
_repo_root_str = str(_REPO_ROOT)
if _repo_root_str in sys.path:
    sys.path.remove(_repo_root_str)
sys.path.insert(0, _repo_root_str)

_cached = sys.modules.get("local_runtime")
if _cached is not None and not hasattr(_cached, "__path__"):
    del sys.modules["local_runtime"]

import local_runtime  # noqa: F401,E402 - re-registers the real package under its own name
