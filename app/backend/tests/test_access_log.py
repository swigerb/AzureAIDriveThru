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
        """Belt-and-braces: assert those specific attributes were never even
        accessed on the mock, not just that their value didn't leak."""
        logger_instance, _ = _make_logger_instance()
        request = _make_request(route_template="/realtime")
        response = _make_response(status=101)

        logger_instance.log(request, response, 0.5)

        for forbidden_attr in ("path_qs", "rel_url", "url", "raw_path"):
            with self.subTest(attr=forbidden_attr):
                self.assertNotIn(
                    mock.call.__getattr__(forbidden_attr),
                    request.mock_calls,
                    f"log() must never read request.{forbidden_attr}",
                )
        # `request.path` specifically: percent-decoded, so a hand-crafted
        # `/realtime%3Faccess_token=X` would look like a real query string.
        self.assertNotIn(
            mock.call.__getattr__("path"),
            request.mock_calls,
            "log() must never read request.path (it's percent-decoded)",
        )

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


if __name__ == "__main__":
    unittest.main()
