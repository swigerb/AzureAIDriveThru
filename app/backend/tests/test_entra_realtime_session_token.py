"""Issue #144 (ADR-002, design doc section 18.3): `/realtime`'s layered HMAC
session-token check, and its oid-binding to the Entra principal when
`RTMiddleTier.entra_mode` is True.

These tests drive the real `RTMiddleTier._websocket_handler` through aiohttp's
TestClient with a plain (non-upgrading) GET, exactly like the existing
`?persona=`/`?model=` 404 tests in test_persona_binding.py/test_model_selection.py
do -- the origin, session-token and oid checks all return a plain `web.Response`
before any `ws.prepare()` call, so a non-WebSocket GET is enough to observe them.

A tiny test-only middleware stands in for `entra_auth.py`'s real Entra middleware
(which is app.py's concern, not rtmt.py's) -- it just sets
`request[entra_auth.PRINCIPAL_KEY]` to a fixed dict, the same contract the real
middleware guarantees before `_websocket_handler` ever runs.
"""

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.append(str(Path(__file__).resolve().parents[1]))

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer
from azure.core.credentials import AzureKeyCredential

import entra_auth
import rtmt as rtmt_module
from rtmt import RTMiddleTier, create_hmac_token

_SECRET = b"test-realtime-session-secret"
_OID = "11111111-2222-3333-4444-555555555555"


def _principal_middleware(principal: dict | None):
    @web.middleware
    async def middleware(request: web.Request, handler):
        if principal is not None:
            request[entra_auth.PRINCIPAL_KEY] = dict(principal)
        return await handler(request)
    return middleware


class _OidBindingHarness(unittest.IsolatedAsyncioTestCase):
    """Builds a real RTMiddleTier + a standalone app with /realtime attached,
    with a test-only middleware that can inject (or omit)
    `request[entra_auth.PRINCIPAL_KEY]` -- the same shape the real Entra
    middleware guarantees, without needing the whole of app.py's create_app()."""

    principal: dict | None = None  # overridden per subclass/test

    async def asyncSetUp(self):
        self.rtmt = RTMiddleTier(
            endpoint="https://fake.openai.azure.com",
            deployment="gpt-realtime-test",
            credentials=AzureKeyCredential("test-key"),
            voice_choice="shimmer",
        )
        self.rtmt.app_secret = _SECRET
        app = web.Application(middlewares=[_principal_middleware(self.principal)])
        self.rtmt.attach_to_app(app, "/realtime")
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()


class EntraModeForcesSessionTokenTests(_OidBindingHarness):
    """`entra_mode=True` forces the session-token check on regardless of
    config.yaml's `security.require_session_token` (18.3) -- config can't turn
    it back off."""

    principal = {"oid": _OID}

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.rtmt.entra_mode = True

    async def test_missing_token_rejected_even_if_config_would_allow_it(self):
        resp = await self.client.get("/realtime")
        self.assertEqual(resp.status, 401)

    async def test_empty_token_rejected(self):
        resp = await self.client.get("/realtime", params={"token": ""})
        self.assertEqual(resp.status, 401)

    async def test_malformed_token_rejected(self):
        resp = await self.client.get("/realtime", params={"token": "not-a-real-token"})
        self.assertEqual(resp.status, 401)

    async def test_expired_token_rejected(self):
        token = create_hmac_token(_SECRET, expiry_seconds=-1, oid=_OID)
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertEqual(resp.status, 401)

    async def test_token_signed_with_wrong_secret_rejected(self):
        token = create_hmac_token(b"wrong-secret", expiry_seconds=60, oid=_OID)
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertEqual(resp.status, 401)

    async def test_valid_token_with_no_oid_claim_rejected(self):
        """A token minted with no principal at all (oid=None) -- the pre-#144
        shape -- must be rejected in Entra mode: there is nothing to compare
        against the Entra principal's own oid."""
        token = create_hmac_token(_SECRET, expiry_seconds=60)  # no oid=
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertEqual(resp.status, 401)

    async def test_valid_token_with_mismatched_oid_rejected(self):
        token = create_hmac_token(_SECRET, expiry_seconds=60, oid="00000000-0000-0000-0000-000000000099")
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertEqual(resp.status, 401)

    async def test_valid_token_with_matching_oid_passes_this_check(self):
        """Passes the session-token/oid check -- proceeds to persona binding
        (the harness's default catalog), which is NOT a 401. The exact
        downstream status is out of scope here; only "not 401" distinguishes
        this check having been satisfied."""
        token = create_hmac_token(_SECRET, expiry_seconds=60, oid=_OID)
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertNotEqual(resp.status, 401)


class EntraModeNoPrincipalTests(_OidBindingHarness):
    """No `request[entra_auth.PRINCIPAL_KEY]` at all (middleware never ran /
    Development pass-through somehow skipped) -- must never be treated as a
    wildcard match; a present, valid oid claim with nothing to compare against
    is still 401."""

    principal = None

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.rtmt.entra_mode = True

    async def test_valid_token_with_oid_but_no_principal_rejected(self):
        token = create_hmac_token(_SECRET, expiry_seconds=60, oid=_OID)
        resp = await self.client.get("/realtime", params={"token": token})
        self.assertEqual(resp.status, 401)


class DevelopmentModeSessionTokenFollowsConfigTests(_OidBindingHarness):
    """`entra_mode=False` (the Development pass-through, or Entra unconfigured) --
    the session-token check follows config.yaml's `security.require_session_token`
    exactly as it did before #144; oid-binding never applies."""

    principal = {"oid": _OID}

    async def test_missing_token_not_rejected_by_this_check_when_not_required(self):
        """Default config.yaml has require_session_token=False -- confirms #144
        didn't flip that default for non-Entra deployments."""
        with mock.patch.dict(rtmt_module._security_cfg, {"require_session_token": False}):
            resp = await self.client.get("/realtime")
            self.assertNotEqual(resp.status, 401)

    async def test_missing_token_rejected_when_config_requires_it(self):
        with mock.patch.dict(rtmt_module._security_cfg, {"require_session_token": True}):
            resp = await self.client.get("/realtime")
            self.assertEqual(resp.status, 401)

    async def test_valid_token_with_wrong_oid_still_passes_when_not_entra_mode(self):
        """oid is never checked outside Entra mode, even if the token happens to
        carry one -- entra_mode gates the WHOLE oid comparison, not just whether
        a token is required."""
        with mock.patch.dict(rtmt_module._security_cfg, {"require_session_token": True}):
            token = create_hmac_token(_SECRET, expiry_seconds=60, oid="totally-different-oid")
            resp = await self.client.get("/realtime", params={"token": token})
            self.assertNotEqual(resp.status, 401)


if __name__ == "__main__":
    unittest.main()
