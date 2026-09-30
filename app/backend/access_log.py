"""Path-only access logging for the Python backend (issue #144, design doc section
18.4).

A gunicorn `--access-logformat` fix doesn't work here: the app runs under
`aiohttp.GunicornWebWorker`, whose `_get_valid_log_format` raises `ValueError` on any
gunicorn `%(name)s` directive (aiohttp 3.14.3 `worker.py`), so a directive like
`%(U)s` would crash the worker at boot. aiohttp's own access-log format has no
path-only directive either: `%r` is the full request line (query string included),
and the default format (`%a %t "%r" ...`) is exactly what leaks `?token=` today.

So instead of a format string, `PathOnlyAccessLogger` is a small
`aiohttp.abc.AbstractAccessLogger` that logs the method, the **matched route's
template** (`request.match_info.route.resource.canonical`, e.g.
`/personas/{persona_id}/menu.json`, or `<unmatched>` when nothing matched), the
status and the elapsed time. Never `path_qs`, `rel_url`, `url` or `raw_path` -- and
not `request.path` either, since it's percent-decoded (a hand-crafted
`/realtime%3Faccess_token=X` would log as `/realtime?access_token=X`). The route
template also keeps free-form/attacker-influenced paths out of the log.
"""
from __future__ import annotations

from aiohttp.abc import AbstractAccessLogger
from aiohttp.web_request import BaseRequest
from aiohttp.web_response import StreamResponse


class PathOnlyAccessLogger(AbstractAccessLogger):
    """Logs `METHOD <matched route template> <status> <elapsed>s` -- never the query
    string, and never a raw/decoded request path."""

    def log(self, request: BaseRequest, response: StreamResponse, time: float) -> None:
        route = None
        match_info = getattr(request, "match_info", None)
        if match_info is not None:
            route = getattr(match_info, "route", None)
        resource = getattr(route, "resource", None) if route is not None else None
        canonical = getattr(resource, "canonical", None) if resource is not None else None
        template = canonical if canonical else "<unmatched>"
        self.logger.info(
            "%s %s %s %.3fs", request.method, template, response.status, time
        )


def access_log_kwargs() -> dict:
    """One helper both run paths spread, so neither can drift (18.4). Each run path
    keeps its own timeouts -- this only ever carries the access-log class."""
    return {"access_log_class": PathOnlyAccessLogger}
