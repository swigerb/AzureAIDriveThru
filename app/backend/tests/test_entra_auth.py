"""Unit tests for entra_auth.py (issue #144, ADR-002, design doc section 18.2-18.5).

Covers:
  - GUID / placeholder validation (`validate_entra_ids`), mirroring
    `app/frontend/src/auth/authMode.ts`.
  - `resolve_settings`'s full mode-resolution table (18.5): every AUTH_MODE x
    ids-present x RUNNING_IN_PRODUCTION combination, plus APP_SESSION_SECRET's
    Production-Entra-only requirement and ENTRA_INSTANCE's https/loopback-http
    rule.
  - `TokenValidator`: lazy discovery, RS256-only, exact issuer/tid/audience,
    exp/nbf with 5-minute leeway, required role AND scope (app-only tokens --
    no `scp` at all -- rejected), and the JWKS/discovery failure paths, all
    against REAL RS256-signed JWTs (no mocked `jwt.decode`) so signature
    verification is exercised for real; only the network fetch of the JWKS
    itself is stubbed.
  - The deny-by-default middleware: Development pass-through, the anonymous
    allow-list (by route name), the persona-asset extension split, `/realtime`
    `?access_token=` being honored ONLY on that one path, 401
    (`WWW-Authenticate: Bearer`) vs 403 response shapes, and `request["principal"]`.
"""

import json
import sys
import time
import unittest
import urllib.error
from pathlib import Path
from unittest import mock

sys.path.append(str(Path(__file__).resolve().parents[1]))

import jwt
from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer
from cryptography.hazmat.primitives.asymmetric import rsa

from entra_auth import (
    PERSONA_ASSET_ROUTE_NAME,
    REALTIME_PATH,
    SYNTHETIC_PRINCIPAL,
    EntraConfigError,
    EntraForbidden,
    EntraSettings,
    EntraUnauthorized,
    Mode,
    TokenValidator,
    create_middleware,
    resolve_settings,
    validate_entra_ids,
)

_TENANT = "11111111-1111-1111-1111-111111111111"
_CLIENT = "22222222-2222-2222-2222-222222222222"
_OTHER_TENANT = "99999999-9999-9999-9999-999999999999"


# ═══════════════════════════════════════════════════════════════════════════
# GUID / placeholder validation (validate_entra_ids)
# ═══════════════════════════════════════════════════════════════════════════

class ValidateEntraIdsTests(unittest.TestCase):
    def test_valid_guids_pass(self):
        self.assertIsNone(validate_entra_ids(_TENANT, _CLIENT))

    def test_uppercase_guid_passes(self):
        self.assertIsNone(validate_entra_ids(_TENANT.upper(), _CLIENT.upper()))

    def test_empty_tenant_rejected(self):
        self.assertIsNotNone(validate_entra_ids("", _CLIENT))

    def test_empty_client_rejected(self):
        self.assertIsNotNone(validate_entra_ids(_TENANT, ""))

    def test_all_zeros_guid_rejected(self):
        zero = "00000000-0000-0000-0000-000000000000"
        self.assertIsNotNone(validate_entra_ids(zero, _CLIENT))
        self.assertIsNotNone(validate_entra_ids(_TENANT, zero))

    def test_not_a_guid_rejected(self):
        self.assertIsNotNone(validate_entra_ids("not-a-guid", _CLIENT))

    def test_placeholder_words_rejected(self):
        for bad in ("your-tenant-id", "placeholder", "changeme", "example", "TODO", "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", "fixme"):
            with self.subTest(bad=bad):
                self.assertIsNotNone(validate_entra_ids(bad, _CLIENT))

    def test_whitespace_only_rejected(self):
        self.assertIsNotNone(validate_entra_ids("   ", _CLIENT))

    def test_angle_bracket_placeholder_rejected(self):
        self.assertIsNotNone(validate_entra_ids("<your-tenant-id>", _CLIENT))

    def test_embedded_whitespace_rejected(self):
        self.assertIsNotNone(validate_entra_ids("11111111 1111 1111 1111 111111111111", _CLIENT))

    def test_client_checked_after_tenant_passes(self):
        """Whichever id is broken is named -- tenant errors don't mask client errors."""
        msg = validate_entra_ids(_TENANT, "placeholder")
        self.assertIn("client", msg.lower())

    def test_tenant_error_reported_before_client(self):
        msg = validate_entra_ids("placeholder", "placeholder")
        self.assertIn("tenant", msg.lower())


# ═══════════════════════════════════════════════════════════════════════════
# resolve_settings -- 18.5's mode table
# ═══════════════════════════════════════════════════════════════════════════

def _env(**overrides) -> dict:
    base = {}
    base.update(overrides)
    return base


class ResolveSettingsModeTableTests(unittest.TestCase):
    """Every row of 18.5's table: AUTH_MODE (unset/Entra/Development/unknown) x
    ids (blank/valid/invalid) x RUNNING_IN_PRODUCTION (true/false)."""

    # ── AUTH_MODE unset ──

    def test_unset_mode_no_ids_not_production_is_development(self):
        settings = resolve_settings(_env())
        self.assertEqual(settings.mode, Mode.DEVELOPMENT)

    def test_unset_mode_no_ids_production_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(RUNNING_IN_PRODUCTION="1"))

    def test_unset_mode_valid_ids_not_production_is_entra(self):
        settings = resolve_settings(_env(ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.mode, Mode.ENTRA)
        self.assertEqual(settings.tenant_id, _TENANT)
        self.assertEqual(settings.client_id, _CLIENT)

    def test_unset_mode_valid_ids_production_refused(self):
        """Production requires AUTH_MODE=Entra set EXPLICITLY -- ids alone are not enough."""
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(
                ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT, RUNNING_IN_PRODUCTION="1",
                APP_SESSION_SECRET="s" * 32,
            ))

    def test_unset_mode_invalid_ids_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(ENTRA_TENANT_ID="placeholder", ENTRA_CLIENT_ID=_CLIENT))

    def test_unset_mode_only_tenant_id_set_refused(self):
        """Only ONE of the pair set is not a valid 'blank' state either."""
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(ENTRA_TENANT_ID=_TENANT))

    # ── AUTH_MODE=Entra ──

    def test_entra_mode_valid_ids_accepted(self):
        settings = resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.mode, Mode.ENTRA)

    def test_entra_mode_case_insensitive(self):
        settings = resolve_settings(_env(AUTH_MODE="ENTRA", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.mode, Mode.ENTRA)

    def test_entra_mode_missing_ids_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(AUTH_MODE="Entra"))

    def test_entra_mode_invalid_ids_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID="bad", ENTRA_CLIENT_ID=_CLIENT))

    def test_entra_mode_works_in_production_with_secret(self):
        settings = resolve_settings(_env(
            AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
            RUNNING_IN_PRODUCTION="1", APP_SESSION_SECRET="s" * 32,
        ))
        self.assertEqual(settings.mode, Mode.ENTRA)

    # ── AUTH_MODE=Development ──

    def test_development_mode_no_ids_not_production_accepted(self):
        settings = resolve_settings(_env(AUTH_MODE="Development"))
        self.assertEqual(settings.mode, Mode.DEVELOPMENT)

    def test_development_mode_case_insensitive(self):
        settings = resolve_settings(_env(AUTH_MODE="development"))
        self.assertEqual(settings.mode, Mode.DEVELOPMENT)

    def test_development_mode_production_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(AUTH_MODE="Development", RUNNING_IN_PRODUCTION="1"))

    def test_development_mode_with_ids_refused(self):
        """Must not be combined with Entra ids -- ambiguous configuration."""
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(AUTH_MODE="Development", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))

    # ── Unknown AUTH_MODE ──

    def test_unknown_auth_mode_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(AUTH_MODE="Bogus"))

    # ── APP_SESSION_SECRET (Production Entra mode only) ──

    def test_production_entra_without_session_secret_refused(self):
        with self.assertRaises(EntraConfigError) as ctx:
            resolve_settings(_env(
                AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
                RUNNING_IN_PRODUCTION="1",
            ))
        self.assertIn("APP_SESSION_SECRET", str(ctx.exception))

    def test_non_production_entra_without_session_secret_accepted(self):
        """APP_SESSION_SECRET is only REQUIRED in Production Entra mode -- local
        Entra-mode dev without it must still start (a random per-process secret
        is used elsewhere, same as today)."""
        settings = resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.mode, Mode.ENTRA)

    def test_production_entra_with_blank_session_secret_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(
                AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
                RUNNING_IN_PRODUCTION="1", APP_SESSION_SECRET="   ",
            ))

    # ── ENTRA_INSTANCE ──

    def test_default_instance_is_login_microsoftonline(self):
        settings = resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.instance, "https://login.microsoftonline.com/")

    def test_https_instance_accepted(self):
        settings = resolve_settings(_env(
            AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
            ENTRA_INSTANCE="https://fake-issuer.example.com",
        ))
        self.assertEqual(settings.instance, "https://fake-issuer.example.com/")

    def test_http_loopback_instance_accepted(self):
        """#143's conformance fake issuer needs plain http:// to a loopback host."""
        for host in ("127.0.0.1", "localhost", "[::1]"):
            with self.subTest(host=host):
                settings = resolve_settings(_env(
                    AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
                    ENTRA_INSTANCE=f"http://{host}:5001",
                ))
                self.assertTrue(settings.instance.startswith("http://"))

    def test_http_non_loopback_instance_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(
                AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
                ENTRA_INSTANCE="http://evil.example.com",
            ))

    def test_ftp_instance_refused(self):
        with self.assertRaises(EntraConfigError):
            resolve_settings(_env(
                AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
                ENTRA_INSTANCE="ftp://login.microsoftonline.com",
            ))

    def test_instance_gains_trailing_slash(self):
        settings = resolve_settings(_env(
            AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
            ENTRA_INSTANCE="https://fake-issuer.example.com/already/",
        ))
        self.assertTrue(settings.instance.endswith("/"))
        self.assertEqual(settings.instance.count("//"), 1)  # only the scheme's //, no doubling

    # ── Derived issuer/discovery_url ──

    def test_issuer_and_discovery_url_shape(self):
        settings = resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.issuer, f"https://login.microsoftonline.com/{_TENANT}/v2.0")
        self.assertEqual(
            settings.discovery_url,
            f"https://login.microsoftonline.com/{_TENANT}/v2.0/.well-known/openid-configuration",
        )

    # ── api_scope / app_role defaults and overrides ──

    def test_default_scope_and_role(self):
        settings = resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))
        self.assertEqual(settings.api_scope, "access_as_user")
        self.assertEqual(settings.app_role, "DriveThru.User")

    def test_scope_and_role_overridable(self):
        settings = resolve_settings(_env(
            AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT,
            ENTRA_API_SCOPE="custom_scope", ENTRA_APP_ROLE="Custom.Role",
        ))
        self.assertEqual(settings.api_scope, "custom_scope")
        self.assertEqual(settings.app_role, "Custom.Role")

    # ── Development pass-through's loud warning ──

    def test_development_mode_logs_a_warning(self):
        with self.assertLogs("entra_auth", level="WARNING") as ctx:
            resolve_settings(_env())
        self.assertTrue(any("SYNTHETIC" in m for m in ctx.output))

    def test_entra_mode_does_not_log_a_warning(self):
        with self.assertNoLogs("entra_auth", level="WARNING"):
            resolve_settings(_env(AUTH_MODE="Entra", ENTRA_TENANT_ID=_TENANT, ENTRA_CLIENT_ID=_CLIENT))

    def test_synthetic_principal_has_no_docstring_placeholder_left_over(self):
        self.assertEqual(SYNTHETIC_PRINCIPAL["tid"], "00000000-0000-0000-0000-000000000000")
        self.assertIn("oid", SYNTHETIC_PRINCIPAL)
        self.assertIn("name", SYNTHETIC_PRINCIPAL)


# ═══════════════════════════════════════════════════════════════════════════
# TokenValidator -- real RS256-signed JWTs, only the network JWKS/discovery
# fetch is stubbed (never `jwt.decode` itself, so signature verification,
# issuer/audience/tid checks, exp/nbf leeway, and role/scope enforcement are
# all exercised for real).
# ═══════════════════════════════════════════════════════════════════════════

def _rsa_keypair():
    private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return private_key, private_key.public_key()


class _StubSigningKey:
    def __init__(self, key):
        self.key = key


class _StubJWKSClient:
    """Stands in for `jwt.PyJWKClient`: returns a fixed public key regardless of
    the token's `kid`, so tests never touch the network."""

    def __init__(self, key):
        self._key = key

    def get_signing_key_from_jwt(self, token):  # noqa: ARG002 -- matches PyJWKClient's signature
        return _StubSigningKey(self._key)


class _RaisingJWKSClient:
    def __init__(self, exc):
        self._exc = exc

    def get_signing_key_from_jwt(self, token):  # noqa: ARG002
        raise self._exc


def _settings(**overrides) -> EntraSettings:
    defaults = dict(
        mode=Mode.ENTRA, tenant_id=_TENANT, client_id=_CLIENT,
        api_scope="access_as_user", app_role="DriveThru.User",
        instance="https://login.microsoftonline.com/",
    )
    defaults.update(overrides)
    return EntraSettings(**defaults)


def _claims(settings: EntraSettings, **overrides) -> dict:
    now = int(time.time())
    claims = {
        "iss": settings.issuer,
        "aud": settings.client_id,
        "tid": settings.tenant_id,
        "exp": now + 3600,
        "nbf": now - 10,
        "roles": [settings.app_role],
        "scp": settings.api_scope,
        "oid": "oid-abc-123",
        "name": "Test User",
    }
    claims.update(overrides)
    return claims


class TokenValidatorTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.private_key, self.public_key = _rsa_keypair()
        self.settings = _settings()
        self.validator = TokenValidator(self.settings)
        # Stub out the network entirely -- `_get_jwks_client` is the ONE seam
        # between this class and the outside world.
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_StubJWKSClient(self.public_key)
        )

    def _sign(self, claims: dict, *, algorithm="RS256", key=None, headers=None) -> str:
        key = key if key is not None else self.private_key
        return jwt.encode(claims, key, algorithm=algorithm, headers=headers)

    async def test_valid_token_returns_principal(self):
        token = self._sign(_claims(self.settings))
        principal = await self.validator.validate(token)
        self.assertEqual(principal, {"oid": "oid-abc-123", "tid": _TENANT, "name": "Test User"})

    async def test_audience_accepts_bare_client_id(self):
        token = self._sign(_claims(self.settings, aud=self.settings.client_id))
        await self.validator.validate(token)  # must not raise

    async def test_audience_accepts_api_uri_form(self):
        token = self._sign(_claims(self.settings, aud=f"api://{self.settings.client_id}"))
        await self.validator.validate(token)  # must not raise

    async def test_wrong_audience_rejected(self):
        token = self._sign(_claims(self.settings, aud="00000000-0000-0000-0000-000000000abc"))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_wrong_issuer_rejected(self):
        token = self._sign(_claims(self.settings, iss="https://login.microsoftonline.com/wrong-tenant/v2.0"))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_wrong_tid_rejected_even_with_correct_issuer(self):
        """`tid` is checked separately from `iss` -- a token minted for the exact
        right issuer string but a spoofed `tid` claim must still be rejected."""
        token = self._sign(_claims(self.settings, tid=_OTHER_TENANT))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_expired_token_rejected(self):
        now = int(time.time())
        token = self._sign(_claims(self.settings, exp=now - 600, nbf=now - 1000))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_expired_within_leeway_accepted(self):
        """exp 2 minutes ago is within the 5-minute leeway -- must be accepted."""
        now = int(time.time())
        token = self._sign(_claims(self.settings, exp=now - 120))
        await self.validator.validate(token)  # must not raise

    async def test_expired_beyond_leeway_rejected(self):
        now = int(time.time())
        token = self._sign(_claims(self.settings, exp=now - 600))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_premature_token_rejected(self):
        now = int(time.time())
        token = self._sign(_claims(self.settings, nbf=now + 600, exp=now + 1200))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_premature_within_leeway_accepted(self):
        now = int(time.time())
        token = self._sign(_claims(self.settings, nbf=now + 120))
        await self.validator.validate(token)  # must not raise

    async def test_missing_exp_claim_rejected(self):
        claims = _claims(self.settings)
        del claims["exp"]
        token = self._sign(claims)
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_missing_nbf_claim_rejected(self):
        claims = _claims(self.settings)
        del claims["nbf"]
        token = self._sign(claims)
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_missing_role_rejected_as_forbidden(self):
        token = self._sign(_claims(self.settings, roles=[]))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_wrong_role_rejected_as_forbidden(self):
        token = self._sign(_claims(self.settings, roles=["Some.Other.Role"]))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_single_role_as_bare_string_accepted(self):
        """Real Entra ID (and this project's own conformance FakeEntraIssuer, matching
        System.IdentityModel.Tokens.Jwt's JwtPayload behavior) encodes `roles` as a bare
        scalar string -- not a one-item array -- when the principal has exactly one
        assigned app role. Rejecting that shape would 403 every legitimate single-role
        token, including the conformance harness's own default "valid" token (18.11 row 8)."""
        token = self._sign(_claims(self.settings, roles="DriveThru.User"))
        principal = await self.validator.validate(token)
        self.assertEqual(principal["oid"], "oid-abc-123")

    async def test_wrong_role_as_bare_string_rejected_as_forbidden(self):
        token = self._sign(_claims(self.settings, roles="Some.Other.Role"))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_roles_claim_wrong_type_rejected_as_forbidden(self):
        """Neither a list nor a string (e.g. a number) -- must fail closed, not raise."""
        token = self._sign(_claims(self.settings, roles=123))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_required_role_among_several_in_array_accepted(self):
        token = self._sign(
            _claims(self.settings, roles=["Some.Other.Role", self.settings.app_role])
        )
        principal = await self.validator.validate(token)
        self.assertEqual(principal["oid"], "oid-abc-123")

    async def test_app_only_token_no_scp_at_all_rejected_as_forbidden(self):
        """Client-credentials/app-only tokens carry no `scp` claim whatsoever --
        must be rejected, on purpose, never an opt-in (18.4)."""
        claims = _claims(self.settings)
        del claims["scp"]
        token = self._sign(claims)
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_wrong_scope_rejected_as_forbidden(self):
        token = self._sign(_claims(self.settings, scp="some_other_scope"))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

    async def test_scope_matches_one_of_several_space_separated(self):
        token = self._sign(_claims(self.settings, scp=f"some_other_scope {self.settings.api_scope} yet_another"))
        await self.validator.validate(token)  # must not raise

    async def test_hs256_signed_token_rejected_rs256_only(self):
        """Even if a signature-verification key were somehow available, a token
        signed with a symmetric algorithm must never be accepted -- `algorithms`
        is pinned to `["RS256"]` in the `jwt.decode` call itself."""
        token = self._sign(_claims(self.settings), algorithm="HS256", key="some-shared-secret-that-is-long-enough")
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_none_algorithm_rejected(self):
        token = jwt.encode(_claims(self.settings), key=None, algorithm="none")
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_malformed_token_rejected(self):
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate("not-a-jwt-at-all")

    async def test_empty_token_rejected(self):
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate("")

    async def test_jwks_client_error_becomes_unauthorized(self):
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_RaisingJWKSClient(jwt.PyJWKClientError("no matching kid"))
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_lazy_discovery_not_fetched_at_construction(self):
        """Constructing a `TokenValidator` (and even signing a valid token) must
        never touch the network -- only `validate()` may."""
        validator = TokenValidator(self.settings)
        self.assertIsNone(validator._jwks_client)  # noqa: SLF001

    async def test_discovery_fetch_failure_becomes_unauthorized(self):
        validator = TokenValidator(_settings(instance="https://nonexistent.invalid.example/"))
        with mock.patch("urllib.request.urlopen", side_effect=urllib.error.URLError("boom")):
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)

    async def test_discovery_document_missing_jwks_uri_becomes_unauthorized(self):
        validator = TokenValidator(self.settings)
        fake_response = mock.MagicMock()
        fake_response.read.return_value = json.dumps({"issuer": self.settings.issuer}).encode()
        fake_response.__enter__ = mock.MagicMock(return_value=fake_response)
        fake_response.__exit__ = mock.MagicMock(return_value=False)
        with mock.patch("urllib.request.urlopen", return_value=fake_response):
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)


# ═══════════════════════════════════════════════════════════════════════════
# create_middleware -- the deny-by-default route matrix (18.2), the anonymous
# allow-list, the persona-asset extension split, /realtime's ?access_token,
# 401 vs 403 response shapes, and request["principal"].
# ═══════════════════════════════════════════════════════════════════════════

class _StubValidator:
    """A `TokenValidator` stand-in for middleware-level tests: no real crypto,
    just a token->outcome table, so these tests are only about the middleware's
    own routing/response logic, not re-testing `TokenValidator` itself."""

    def __init__(self, outcomes: dict):
        self.outcomes = outcomes
        self.calls: list[str] = []

    async def validate(self, token: str) -> dict:
        self.calls.append(token)
        outcome = self.outcomes[token]
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


def _build_app(settings: EntraSettings, validator=None) -> web.Application:
    app = web.Application(middlewares=[create_middleware(settings, validator)])

    async def ok(request):
        return web.json_response({"principal": request.get("principal")})

    app.router.add_get("/", ok, name="index")
    app.router.add_get("/health", ok, name="health")
    app.router.add_get("/static/app.js", ok, name="static")
    app.router.add_get("/api/personas", ok, name="personas-index")
    app.router.add_get("/api/auth/session", ok, name="session-token")
    app.router.add_get(
        "/personas/{persona_id}/assets/{asset_path:.*}", ok, name=PERSONA_ASSET_ROUTE_NAME
    )
    app.router.add_get(REALTIME_PATH, ok, name="realtime")
    return app


class MiddlewareDevelopmentModeTests(unittest.IsolatedAsyncioTestCase):
    async def test_every_request_gets_synthetic_principal_no_token_needed(self):
        settings = _settings(mode=Mode.DEVELOPMENT, tenant_id=None, client_id=None)
        app = _build_app(settings)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas")
            self.assertEqual(resp.status, 200)
            body = await resp.json()
            self.assertEqual(body["principal"]["oid"], SYNTHETIC_PRINCIPAL["oid"])


class MiddlewareEntraModeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = _settings()

    async def test_anonymous_routes_pass_without_a_token(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            for path in ("/", "/health", "/static/app.js"):
                with self.subTest(path=path):
                    resp = await client.get(path)
                    self.assertEqual(resp.status, 200)
            self.assertEqual(validator.calls, [])  # validator never even invoked

    async def test_persona_asset_anonymous_extension_passes_without_token(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/personas/sonic/assets/logo.svg")
            self.assertEqual(resp.status, 200)
            self.assertEqual(validator.calls, [])

    async def test_persona_asset_protected_extension_requires_token(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/personas/sonic/assets/demo/dummyOrder.json")
            self.assertEqual(resp.status, 401)

    async def test_protected_route_no_authorization_header_401(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas")
            self.assertEqual(resp.status, 401)
            self.assertEqual(resp.headers.get("WWW-Authenticate"), "Bearer")
            body = await resp.json()
            self.assertEqual(body, {"error": "unauthorized"})

    async def test_protected_route_valid_token_sets_principal(self):
        principal = {"oid": "the-oid", "tid": _TENANT, "name": "Someone"}
        validator = _StubValidator({"good-token": principal})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", headers={"Authorization": "Bearer good-token"})
            self.assertEqual(resp.status, 200)
            body = await resp.json()
            self.assertEqual(body["principal"], principal)

    async def test_protected_route_invalid_token_401(self):
        validator = _StubValidator({"bad-token": EntraUnauthorized("nope")})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", headers={"Authorization": "Bearer bad-token"})
            self.assertEqual(resp.status, 401)
            self.assertEqual(resp.headers.get("WWW-Authenticate"), "Bearer")

    async def test_protected_route_forbidden_token_403_no_www_authenticate(self):
        validator = _StubValidator({"no-role-token": EntraForbidden("missing role")})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", headers={"Authorization": "Bearer no-role-token"})
            self.assertEqual(resp.status, 403)
            self.assertNotIn("WWW-Authenticate", resp.headers)
            body = await resp.json()
            self.assertEqual(body, {"error": "forbidden"})

    async def test_empty_bearer_value_treated_as_no_token(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", headers={"Authorization": "Bearer "})
            self.assertEqual(resp.status, 401)
            self.assertEqual(validator.calls, [])

    async def test_non_bearer_authorization_header_ignored(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", headers={"Authorization": "Basic dXNlcjpwYXNz"})
            self.assertEqual(resp.status, 401)

    async def test_realtime_accepts_access_token_query_param(self):
        principal = {"oid": "the-oid", "tid": _TENANT, "name": "Someone"}
        validator = _StubValidator({"qs-token": principal})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(REALTIME_PATH, params={"access_token": "qs-token"})
            self.assertEqual(resp.status, 200)
            self.assertEqual(validator.calls, ["qs-token"])

    async def test_access_token_query_param_ignored_on_other_routes(self):
        """Only /realtime may read a token from the query string -- everywhere
        else it's silently ignored, so this must still 401."""
        principal = {"oid": "the-oid", "tid": _TENANT, "name": "Someone"}
        validator = _StubValidator({"qs-token": principal})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas", params={"access_token": "qs-token"})
            self.assertEqual(resp.status, 401)
            self.assertEqual(validator.calls, [])

    async def test_authorization_header_preferred_over_query_param_on_realtime(self):
        header_principal = {"oid": "header-oid", "tid": _TENANT, "name": "Header"}
        qs_principal = {"oid": "qs-oid", "tid": _TENANT, "name": "QS"}
        validator = _StubValidator({"header-token": header_principal, "qs-token": qs_principal})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(
                REALTIME_PATH,
                params={"access_token": "qs-token"},
                headers={"Authorization": "Bearer header-token"},
            )
            self.assertEqual(resp.status, 200)
            body = await resp.json()
            self.assertEqual(body["principal"], header_principal)

    async def test_unmatched_route_denied_by_default(self):
        """A 404 for a totally unknown path must still be a 401, not a 200 --
        deny-by-default applies even when there's no route name at all to
        classify (`route.name` is `None` for aiohttp's synthesized 404 route)."""
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/totally/unknown/path")
            self.assertEqual(resp.status, 401)

    async def test_create_middleware_builds_its_own_validator_when_none_given(self):
        """Omitting `validator=` in Entra mode must still produce a fully working
        deny-by-default middleware (a real `TokenValidator` is constructed
        internally) -- exercised end-to-end via an unauthenticated request."""
        settings = _settings()
        app = web.Application(middlewares=[create_middleware(settings)])

        async def ok(request):
            return web.json_response({"ok": True})

        app.router.add_get("/api/personas", ok, name="personas-index")
        async with TestClient(TestServer(app)) as client:
            resp = await client.get("/api/personas")
            self.assertEqual(resp.status, 401)


if __name__ == "__main__":
    unittest.main()
