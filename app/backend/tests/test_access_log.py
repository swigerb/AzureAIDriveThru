"""Unit tests for access_log.py (issue #144, design doc section 18.4).

`PathOnlyAccessLogger` must log the method, the MATCHED ROUTE TEMPLATE, status and
elapsed time -- and nothing that could contain the raw request path or query
string (never `path_qs`, `rel_url`, `url`, `raw_path`, or `request.path`), since any
of those would leak a `?token=`/`?access_token=` value from a URL into plaintext
logs. `access_log_kwargs()` is the one place both run paths (`python app.py` and
the Dockerfile's gunicorn CMD) get this from, so they can't drift apart.
"""
import logging
import sys
import unittest
from pathlib import Path
from unittest import mock

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer

sys.path.append(str(Path(__file__).resolve().parents[1]))

from access_log import PathOnlyAccessLogger, access_log_kwargs


def _make_logger_instance():
    """`AbstractAccessLogger.__init__` requires a `logging.Logger` and a
    `log_format` string, neither of which `PathOnlyAccessLogger` actually uses
    (it overrides `log()` completely) -- pass a mock logger we can assert on."""
    fake_logger = mock.MagicMock(spec=logging.Logger)
    return PathOnlyAccessLogger(fake_logger, "unused-format"), fake_logger


def _make_request(*, route_template=None, method="GET", path_qs="/should/never/appear?token=SECRET"):
    request = mock.MagicMock()
    request.method = method
    # These must NEVER be read by `log()` -- configured to explode if touched,
    # so any regression that reads one of them fails loudly instead of just
    # leaking silently.
    request.path_qs = path_qs
    request.rel_url = path_qs
    request.url = f"http://example.com{path_qs}"
    request.raw_path = path_qs
    request.path = path_qs.split("?")[0]

    if route_template is None:
        request.match_info = None
    else:
        resource = mock.MagicMock()
        resource.canonical = route_template
        route = mock.MagicMock()
        route.resource = resource
        match_info = mock.MagicMock()
        match_info.route = route
        request.match_info = match_info
    return request


class _ExplodesOnForbiddenRead:
    """A REAL object (not a `MagicMock`) whose `path_qs`/`rel_url`/`url`/`raw_path`/
    `path` attributes are properties that raise if ever read. Unlike a `MagicMock`
    -- where a plain attribute READ is never recorded in `mock_calls` regardless of
    whether `log()` actually touches it, so asserting on `mock_calls` afterward can
    never fail -- this fails loudly and immediately the instant `log()` reads any
    of them (Rick's #159 round-1 review, required item 4: "fix the existing test
    that can never fail")."""

    method = "GET"

    def __init__(self, *, route_template):
        if route_template is None:
            self.match_info = None
        else:
            resource = mock.MagicMock()
            resource.canonical = route_template
            route = mock.MagicMock()
            route.resource = resource
            match_info = mock.MagicMock()
            match_info.route = route
            self.match_info = match_info

    @property
    def path_qs(self):
        raise AssertionError("log() must never read request.path_qs")

    @property
    def rel_url(self):
        raise AssertionError("log() must never read request.rel_url")

    @property
    def url(self):
        raise AssertionError("log() must never read request.url")

    @property
    def raw_path(self):
        raise AssertionError("log() must never read request.raw_path")

    @property
    def path(self):
        # `request.path` specifically: percent-decoded, so a hand-crafted
        # `/realtime%3Faccess_token=X` would look like a real query string.
        raise AssertionError("log() must never read request.path (it's percent-decoded)")


def _make_response(status=200):
    response = mock.MagicMock()
    response.status = status
    return response


class PathOnlyAccessLoggerTests(unittest.TestCase):
    def test_logs_method_route_template_status_and_elapsed(self):
        logger_instance, fake_logger = _make_logger_instance()
        request = _make_request(route_template="/personas/{persona_id}/menu.json", method="GET")
        response = _make_response(status=200)

        logger_instance.log(request, response, 0.1234)

        fake_logger.info.assert_called_once()
        args = fake_logger.info.call_args[0]
        formatted = args[0] % args[1:]
        self.assertIn("GET", formatted)
        self.assertIn("/personas/{persona_id}/menu.json", formatted)
        self.assertIn("200", formatted)
        self.assertIn("0.123", formatted)

    def test_never_logs_query_string_or_raw_path(self):
        """The request is wired so every path/url-ish attribute contains a
        `?token=SECRET` marker distinct from the route template -- `log()` must
        never surface it."""
        logger_instance, fake_logger = _make_logger_instance()
        request = _make_request(
            route_template="/api/auth/session",
            path_qs="/api/auth/session?token=SECRET-MARKER",
        )
        response = _make_response(status=200)

        logger_instance.log(request, response, 0.01)

        formatted = fake_logger.info.call_args[0][0] % fake_logger.info.call_args[0][1:]
        self.assertNotIn("SECRET-MARKER", formatted)
        self.assertNotIn("?", formatted)

    def test_never_reads_path_qs_rel_url_url_raw_path_or_path_attributes(self):
        """Belt-and-braces: `request` here is a real object (not a `MagicMock`)
        whose forbidden attributes raise `AssertionError` the instant they're
        read, regardless of whether `log()` ever surfaces their value anywhere."""
        logger_instance, _ = _make_logger_instance()
        request = _ExplodesOnForbiddenRead(route_template="/realtime")
        response = _make_response(status=101)

        logger_instance.log(request, response, 0.5)  # must not raise

    def test_unmatched_route_logs_placeholder(self):
        logger_instance, fake_logger = _make_logger_instance()
        request = _make_request(route_template=None)
        response = _make_response(status=404)

        logger_instance.log(request, response, 0.001)

        formatted = fake_logger.info.call_args[0][0] % fake_logger.info.call_args[0][1:]
        self.assertIn("<unmatched>", formatted)

    def test_missing_match_info_route_attr_falls_back_to_placeholder(self):
        """`match_info` present but with no `.route` at all (a bare mock without
        the attribute wired) must not raise -- falls back to `<unmatched>`."""
        logger_instance, fake_logger = _make_logger_instance()
        request = mock.MagicMock(spec=["method", "match_info"])
        request.method = "GET"
        request.match_info = mock.MagicMock(spec=[])  # no `.route` attribute at all
        response = _make_response(status=500)

        logger_instance.log(request, response, 0.2)

        formatted = fake_logger.info.call_args[0][0] % fake_logger.info.call_args[0][1:]
        self.assertIn("<unmatched>", formatted)

    def test_missing_resource_canonical_falls_back_to_placeholder(self):
        """A route present but with no `.resource.canonical` (e.g. a bare
        `SystemRoute` for a 404) must not raise -- falls back to `<unmatched>`."""
        logger_instance, fake_logger = _make_logger_instance()
        request = mock.MagicMock(spec=["method", "match_info"])
        request.method = "GET"
        match_info = mock.MagicMock(spec=["route"])
        match_info.route = mock.MagicMock(spec=[])  # no `.resource` attribute
        request.match_info = match_info
        response = _make_response(status=404)

        logger_instance.log(request, response, 0.001)

        formatted = fake_logger.info.call_args[0][0] % fake_logger.info.call_args[0][1:]
        self.assertIn("<unmatched>", formatted)

    def test_elapsed_time_formatted_to_three_decimals(self):
        logger_instance, fake_logger = _make_logger_instance()
        request = _make_request(route_template="/health")
        response = _make_response(status=200)

        logger_instance.log(request, response, 1.0)

        formatted = fake_logger.info.call_args[0][0] % fake_logger.info.call_args[0][1:]
        self.assertIn("1.000s", formatted)


class AccessLogKwargsTests(unittest.TestCase):
    def test_returns_the_path_only_access_logger_class(self):
        kwargs = access_log_kwargs()
        self.assertEqual(kwargs, {"access_log_class": PathOnlyAccessLogger})

    def test_returns_a_fresh_dict_each_call(self):
        """Callers (`python app.py`'s path and `create_runner()`) each spread
        this independently -- must not be a single shared mutable dict."""
        first = access_log_kwargs()
        second = access_log_kwargs()
        self.assertIsNot(first, second)


# ═══════════════════════════════════════════════════════════════════════════
# Real aiohttp `TestServer`/`TestClient` requests through `PathOnlyAccessLogger`
# -- not the hand-built mock requests above -- so the real `request.path_qs`,
# `request.match_info`, etc. objects are exercised, not a stand-in (Rick's #159
# round-1 review, required item 4; surviving mutation M7c -- logging
# `request.path` instead of the route template).
# ═══════════════════════════════════════════════════════════════════════════

class RealRequestAccessLogIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_realtime_query_string_never_appears_in_access_log(self):
        """A real `GET /realtime?access_token=...&token=...` request -- the exact
        shape the realtime WebSocket upgrade uses (18.3) -- must never put either
        query value into the access log, end to end through the real aiohttp
        request/response/logging pipeline."""

        async def handler(request):  # noqa: ARG001
            return web.Response(status=200, text="ok")

        app = web.Application()
        app.router.add_get("/realtime", handler, name="realtime")

        # `TestServer`/`TestClient`'s constructors silently swallow extra kwargs
        # like `access_log_class` (only `start_server()` itself forwards them to
        # `AppRunner`) -- so the server is started explicitly here with our access
        # logger wired in, before handing it to a `TestClient` for requests.
        server = TestServer(app)
        await server.start_server(access_log_class=PathOnlyAccessLogger)
        try:
            with self.assertLogs("aiohttp.access", level="INFO") as captured:
                async with TestClient(server) as client:
                    resp = await client.get("/realtime?access_token=ACCESS-TOKEN-CANARY&token=WS-TOKEN-CANARY")
                    self.assertEqual(resp.status, 200)
        finally:
            await server.close()

        joined = "\n".join(captured.output)
        self.assertNotIn("ACCESS-TOKEN-CANARY", joined)
        self.assertNotIn("WS-TOKEN-CANARY", joined)
        self.assertIn("/realtime", joined)

    async def test_percent_encoded_query_marker_in_path_never_appears_in_access_log(self):
        """A hand-crafted path like `/realtime%3Faccess_token=X` puts the `?`
        INSIDE the path segment itself (percent-decoded by `request.path`), not
        as a real query string -- must still never leak, which is why `log()`
        must never read `request.path` either (belt-and-braces, see the unit
        test above). No route matches this path, so it also exercises the
        `<unmatched>` fallback with a real framework-generated 404."""
        app = web.Application()

        # See the comment in the test above: `access_log_class` must be passed
        # to `start_server()` directly, not to `TestServer`/`TestClient`'s own
        # constructors.
        server = TestServer(app)
        await server.start_server(access_log_class=PathOnlyAccessLogger)
        try:
            with self.assertLogs("aiohttp.access", level="INFO") as captured:
                async with TestClient(server) as client:
                    resp = await client.get("/realtime%3Faccess_token=PERCENT-ENCODED-CANARY")
                    self.assertEqual(resp.status, 404)
        finally:
            await server.close()

        joined = "\n".join(captured.output)
        self.assertNotIn("PERCENT-ENCODED-CANARY", joined)
        self.assertIn("<unmatched>", joined)


if __name__ == "__main__":
    unittest.main()
