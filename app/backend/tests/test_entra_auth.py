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
    allow-list (by route name), the persona-asset extension split (including
    its CASE-INSENSITIVE extension matching, F4/#163), `/realtime`
    `?access_token=` being honored ONLY on that one path, 401
    (`WWW-Authenticate: Bearer`) vs 403 response shapes, and
    `request[PRINCIPAL_KEY]`.
"""

import asyncio
import http.client
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

import default_persona
from entra_auth import (
    ANONYMOUS_ASSET_EXTENSIONS,
    PERSONA_ASSET_ROUTE_NAME,
    PRINCIPAL_KEY,
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

# The default persona's own id -- used instead of a hardcoded brand name so these
# middleware tests don't add fresh brand-literal occurrences to the rebrand-word-count
# ratchet (#76). It resolves to a real, on-disk persona pack, so the persona-asset route
# below still serves a real asset directory; only the literal spelling in this test's own
# source is avoided (Rick's #159 round-1 review, required item 1).
_DEFAULT_PERSONA_ID = default_persona.get_default_persona().id


# ═══════════════════════════════════════════════════════════════════════════
# The anonymous asset extension contract (18.2): exactly these seven, no more
# (Rick's #159 round-1 review, required item 2 -- `.jpeg` was removed).
# ═══════════════════════════════════════════════════════════════════════════

class AnonymousAssetExtensionsTests(unittest.TestCase):
    def test_exact_extension_set(self):
        self.assertEqual(
            ANONYMOUS_ASSET_EXTENSIONS,
            frozenset({".svg", ".png", ".jpg", ".webp", ".ico", ".wav", ".mp3"}),
        )


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

    def test_tenant_and_client_ids_normalized_to_lower_case(self):
        """N4 (#163 round-1 review): a real Entra token's `tid`/`aud` claims are
        always lower-case GUIDs -- an upper-case (but otherwise valid) id typed
        into ENTRA_TENANT_ID/ENTRA_CLIENT_ID must still match every token, not
        fail every request with a confusing mismatch. Uses GUIDs with actual
        hex LETTERS (unlike `_TENANT`/`_CLIENT`, which are digit-only and so
        `.upper()`/`.lower()` on them is a silent no-op that would let this
        test pass even with the lower-casing removed entirely)."""
        mixed_tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        mixed_client = "ffffffff-aaaa-bbbb-cccc-dddddddddddd"
        settings = resolve_settings(_env(
            AUTH_MODE="Entra",
            ENTRA_TENANT_ID=mixed_tenant.upper(),
            ENTRA_CLIENT_ID=mixed_client.upper(),
        ))
        self.assertEqual(settings.tenant_id, mixed_tenant)
        self.assertEqual(settings.client_id, mixed_client)
        self.assertEqual(settings.issuer, f"https://login.microsoftonline.com/{mixed_tenant}/v2.0")

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

    async def test_roles_claim_not_a_list_rejected_as_forbidden(self):
        token = self._sign(_claims(self.settings, roles="DriveThru.User"))
        with self.assertRaises(EntraForbidden):
            await self.validator.validate(token)

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

    async def test_scp_claim_not_a_string_rejected_as_forbidden_not_a_crash(self):
        """N2 (#163 round-1 review): `scp` is attacker/tenant controlled -- a
        non-string value (e.g. the list shape JSON would give a repeated claim)
        must be treated as "no scopes" (403), the same as a missing `scp`, never
        crash `.split()` with `AttributeError` (a 500)."""
        token = self._sign(_claims(self.settings, scp=["access_as_user"]))
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

    async def test_jwks_client_error_message_distinguishes_from_malformed_token(self):
        """N6 (#163 round-1 review): `jwt.PyJWKClientError` is a SUBCLASS of
        `jwt.PyJWTError`, so the except clauses in `_validate_sync` must catch
        it FIRST -- the reverse order (as this used to be) made this branch
        unreachable dead code, and a signing-key-resolution failure was always
        mislabeled "Malformed token" instead of its own, more specific message."""
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_RaisingJWKSClient(jwt.PyJWKClientError("no matching kid"))
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized) as ctx:
            await self.validator.validate(token)
        self.assertIn("signing key", str(ctx.exception).lower())
        self.assertNotIn("malformed", str(ctx.exception).lower())

    async def test_jwks_body_read_oserror_becomes_unauthorized(self):
        """#223 round 2 (Rick's review): a LIVE JWKS refetch's body-read can
        fail with a plain `OSError` subclass (e.g. the peer resets the
        connection mid-response) -- `PyJWKClient.fetch_data` only wraps
        `URLError`/`TimeoutError` in `PyJWKClientConnectionError`, never this.
        Before this fix that propagated uncaught past every `except` clause
        in `_validate_sync`, surfacing as a raw 500 instead of the fail-closed
        401 every other JWKS/discovery failure gets."""
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_RaisingJWKSClient(ConnectionResetError("connection reset by peer"))
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_jwks_body_read_http_exception_becomes_unauthorized(self):
        """#223 round 2: `http.client.HTTPException` subclasses (e.g.
        `IncompleteRead`) are not `OSError` and not `URLError` -- a distinct
        family that must also be caught on the JWKS-refetch path, same as the
        discovery path above."""
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_RaisingJWKSClient(http.client.IncompleteRead(b"partial"))
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_jwks_body_read_json_decode_error_becomes_unauthorized(self):
        """#223 round 2: a non-JSON JWKS body raises `json.JSONDecodeError`,
        a `ValueError` subclass PyJWT itself never catches inside
        `fetch_data` -- must also fail closed as a 401, not a raw 500."""
        self.validator._get_jwks_client = mock.MagicMock(  # noqa: SLF001
            return_value=_RaisingJWKSClient(json.JSONDecodeError("Expecting value", "not json", 0))
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate(token)

    async def test_jwks_body_read_failure_engages_negative_cache_cooldown(self):
        """#223 round 2: wrapping body-read failures as `EntraUnauthorized` is
        only useful if it engages a negative cache -- a second request within
        the cooldown window must short-circuit WITHOUT calling
        `get_signing_key_from_jwt` (the network) again, mirroring the
        discovery-failure cooldown above."""
        validator = TokenValidator(self.settings, jwks_failure_cooldown=60.0)
        client = mock.MagicMock()
        client.get_signing_key_from_jwt.side_effect = ConnectionResetError("connection reset by peer")
        validator._get_jwks_client = mock.MagicMock(return_value=client)  # noqa: SLF001
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        client.get_signing_key_from_jwt.assert_called_once()

    async def test_jwks_retried_once_cooldown_elapses(self):
        """#223 round 2: the JWKS-refetch negative cache is time-bounded, not
        permanent -- once `jwks_failure_cooldown` elapses, the next request
        must retry the network call for real (and here, succeed) instead of
        staying stuck refusing forever."""
        validator = TokenValidator(self.settings, jwks_failure_cooldown=0.05)
        client = mock.MagicMock()
        client.get_signing_key_from_jwt.side_effect = [
            ConnectionResetError("connection reset by peer"),
            _StubSigningKey(self.public_key),
        ]
        validator._get_jwks_client = mock.MagicMock(return_value=client)  # noqa: SLF001
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        await asyncio.sleep(0.1)  # let the 0.05s cooldown window elapse
        principal = await validator.validate(token)  # must not raise this time
        self.assertEqual(principal["oid"], "oid-abc-123")
        self.assertEqual(client.get_signing_key_from_jwt.call_count, 2)

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

    async def test_discovery_body_read_oserror_becomes_unauthorized(self):
        """#223 (Rick's #222 review): a body-read failure (e.g. the peer resets
        the connection mid-response) raises a plain `OSError` subclass from
        `response.read()`, never a `urllib.error.URLError` -- `urlopen()` itself
        already succeeded by the time the read fails. Before this fix that
        propagated uncaught past the 401 fail-closed path."""
        validator = TokenValidator(self.settings)
        fake_response = mock.MagicMock()
        fake_response.read.side_effect = ConnectionResetError("connection reset by peer")
        fake_response.__enter__ = mock.MagicMock(return_value=fake_response)
        fake_response.__exit__ = mock.MagicMock(return_value=False)
        with mock.patch("urllib.request.urlopen", return_value=fake_response):
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)

    async def test_discovery_body_read_http_exception_becomes_unauthorized(self):
        """#223: `http.client.HTTPException` subclasses (e.g. `IncompleteRead`,
        raised by `http.client` well below `urllib`) are not `OSError` and not
        `URLError` -- a distinct family that must also be caught."""
        validator = TokenValidator(self.settings)
        fake_response = mock.MagicMock()
        fake_response.read.side_effect = http.client.IncompleteRead(b"partial")
        fake_response.__enter__ = mock.MagicMock(return_value=fake_response)
        fake_response.__exit__ = mock.MagicMock(return_value=False)
        with mock.patch("urllib.request.urlopen", return_value=fake_response):
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)

    async def test_discovery_non_dict_json_body_becomes_unauthorized(self):
        """#223: a syntactically-valid JSON body that isn't an object (e.g. a
        bare JSON list) would otherwise crash `discovery.get("jwks_uri")` with
        an uncaught `AttributeError` instead of failing closed as a 401."""
        validator = TokenValidator(self.settings)
        fake_response = mock.MagicMock()
        fake_response.read.return_value = json.dumps(["not", "a", "dict"]).encode()
        fake_response.__enter__ = mock.MagicMock(return_value=fake_response)
        fake_response.__exit__ = mock.MagicMock(return_value=False)
        with mock.patch("urllib.request.urlopen", return_value=fake_response):
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)

    async def test_discovery_body_read_failure_engages_negative_cache_cooldown(self):
        """#223: the whole point of wrapping body-read failures as
        `EntraUnauthorized` is so they engage the SAME 30s negative-discovery
        cooldown as every other discovery failure -- a second request within
        the window must short-circuit without calling `urlopen` again."""
        validator = TokenValidator(self.settings, discovery_failure_cooldown=60.0)
        fake_response = mock.MagicMock()
        fake_response.read.side_effect = ConnectionResetError("connection reset by peer")
        fake_response.__enter__ = mock.MagicMock(return_value=fake_response)
        fake_response.__exit__ = mock.MagicMock(return_value=False)
        with mock.patch(
            "urllib.request.urlopen", return_value=fake_response
        ) as mock_urlopen:
            token = self._sign(_claims(self.settings))
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)
            with self.assertRaises(EntraUnauthorized):
                await validator.validate(token)
            mock_urlopen.assert_called_once()


# ═══════════════════════════════════════════════════════════════════════════
# The JWKS cache contract (18.4): 24h lifespan, at most one forced refetch per
# 5 minutes on an unknown `kid`, and validation running off the event loop via
# `asyncio.to_thread` -- pinned against the REAL `jwt.PyJWKClient`, not the
# `_StubJWKSClient` used above (Rick's #159 round-1 review, required item 3;
# surviving mutations M8 -- cache 300s/cooldown 0 -- and M9 -- no to_thread).
# ═══════════════════════════════════════════════════════════════════════════

class JwksCachingAndThreadingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.private_key, self.public_key = _rsa_keypair()
        self.settings = _settings()
        self.validator = TokenValidator(self.settings)
        self.validator._fetch_discovery_document = mock.MagicMock(  # noqa: SLF001
            return_value={"issuer": self.settings.issuer, "jwks_uri": "https://fake.example/jwks"}
        )

    def _sign(self, claims: dict, *, kid: str | None = None) -> str:
        headers = {"kid": kid} if kid else None
        return jwt.encode(claims, self.private_key, algorithm="RS256", headers=headers)

    def test_lifespan_is_24_hours_and_cooldown_is_5_minutes(self):
        """Pins the two `jwt.PyJWKClient` constructor arguments directly against
        the real client object -- a regression back to PyJWT's own 300s/30s
        defaults must fail this test."""
        client = self.validator._get_jwks_client()  # noqa: SLF001
        self.assertEqual(client.jwk_set_cache.lifespan, 86400)
        self.assertEqual(client.cooldown_duration, 300)

    def test_jwks_client_uses_validators_own_timeout(self):
        """N1 (#163 round-1 review): `jwt.PyJWKClient`'s own default timeout is
        30s -- this pins that the validator's `timeout` (10s by default; also
        the discovery fetch's own timeout) is passed through to the JWKS
        client's constructor too, so a hung Entra endpoint can't hold the
        client's internal lock open for 30s while requests queue behind it."""
        validator = TokenValidator(self.settings, timeout=7.5)
        validator._fetch_discovery_document = mock.MagicMock(  # noqa: SLF001
            return_value={"issuer": self.settings.issuer, "jwks_uri": "https://fake.example/jwks"}
        )
        client = validator._get_jwks_client()  # noqa: SLF001
        self.assertEqual(client.timeout, 7.5)

    async def test_malformed_token_never_triggers_discovery(self):
        """N1 (#163 round-1 review): a structurally-invalid token must be
        rejected before `_get_jwks_client`/discovery is ever attempted -- a
        flood of garbage tokens during a real Entra outage must not each
        independently retry a hung discovery endpoint."""
        with self.assertRaises(EntraUnauthorized):
            await self.validator.validate("not-a-jwt-at-all")
        self.validator._fetch_discovery_document.assert_not_called()  # noqa: SLF001

    async def test_discovery_failure_is_cached_negatively(self):
        """N1 (#163 round-1 review): a failed discovery fetch must not be
        retried on every subsequent request within the cooldown window -- the
        second call here must short-circuit to an immediate `EntraUnauthorized`
        WITHOUT calling the (failing) discovery fetch again."""
        validator = TokenValidator(self.settings, discovery_failure_cooldown=60.0)
        validator._fetch_discovery_document = mock.MagicMock(  # noqa: SLF001
            side_effect=EntraUnauthorized("OIDC discovery failed: boom")
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        validator._fetch_discovery_document.assert_called_once()  # noqa: SLF001

    async def test_discovery_retried_once_cooldown_elapses(self):
        """N1: the negative cache is time-bounded, not permanent -- once
        `discovery_failure_cooldown` has elapsed, the NEXT request must retry
        discovery for real (and here, succeed) rather than staying stuck
        refusing forever. Uses a real (tiny) cooldown and `asyncio.sleep`
        rather than mocking `time.monotonic` directly -- that global clock is
        also read by asyncio's own event loop internals, so patching it would
        corrupt the loop itself rather than just this validator."""
        validator = TokenValidator(self.settings, discovery_failure_cooldown=0.05)
        validator._fetch_discovery_document = mock.MagicMock(  # noqa: SLF001
            side_effect=[
                EntraUnauthorized("OIDC discovery failed: boom"),
                {"issuer": self.settings.issuer, "jwks_uri": "https://fake.example/jwks"},
            ]
        )
        token = self._sign(_claims(self.settings))
        with self.assertRaises(EntraUnauthorized):
            await validator.validate(token)
        await asyncio.sleep(0.1)  # let the 0.05s cooldown window elapse
        client = validator._get_jwks_client()  # noqa: SLF001
        self.assertIsNotNone(client)
        self.assertEqual(validator._fetch_discovery_document.call_count, 2)  # noqa: SLF001

    def test_get_jwks_client_is_memoized_discovery_fetched_once(self):
        first = self.validator._get_jwks_client()  # noqa: SLF001
        second = self.validator._get_jwks_client()  # noqa: SLF001
        self.assertIs(first, second)
        self.validator._fetch_discovery_document.assert_called_once()  # noqa: SLF001

    async def test_unknown_kid_refetches_at_most_once_within_cooldown(self):
        """Two back-to-back tokens with an unrecognized `kid` must trigger the
        JWKS endpoint's underlying network fetch at most once in total -- the
        first cache-miss legitimately fetches, but the cooldown started by that
        fetch must block a second one moments later, even though the `kid` is
        still unmatched both times. Patches only the network primitive
        (`OpenerDirector.open`) so `fetch_data`'s real cache-write and
        cooldown-timestamp bookkeeping still run for real."""
        kid = "the-real-signing-key"
        jwk_dict = jwt.algorithms.RSAAlgorithm.to_jwk(self.public_key, as_dict=True)
        jwk_dict.update({"kid": kid, "use": "sig"})
        jwks_payload = json.dumps({"keys": [jwk_dict]}).encode()

        class _FakeJwksResponse:
            def read(self_inner):
                return jwks_payload

            def __enter__(self_inner):
                return self_inner

            def __exit__(self_inner, *exc):
                return False

        with mock.patch(
            "urllib.request.OpenerDirector.open", return_value=_FakeJwksResponse()
        ) as opener_open:
            token_1 = self._sign(_claims(self.settings), kid="unknown-kid-one")
            token_2 = self._sign(_claims(self.settings), kid="unknown-kid-two")
            with self.assertRaises(EntraUnauthorized):
                await self.validator.validate(token_1)
            with self.assertRaises(EntraUnauthorized):
                await self.validator.validate(token_2)
            self.assertEqual(opener_open.call_count, 1)

    async def test_validate_runs_the_synchronous_work_through_to_thread(self):
        """`validate()` must hand its synchronous PyJWT work to `asyncio.to_thread`
        rather than running it inline on the event loop -- pinned by replacing
        `to_thread` itself and checking it was awaited with `_validate_sync`."""
        sentinel = {"oid": "sentinel-oid", "tid": self.settings.tenant_id, "name": "Sentinel"}
        with mock.patch("entra_auth.asyncio.to_thread", new=mock.AsyncMock(return_value=sentinel)) as to_thread:
            result = await self.validator.validate("irrelevant-token-value")
        self.assertEqual(result, sentinel)
        to_thread.assert_awaited_once_with(self.validator._validate_sync, "irrelevant-token-value")  # noqa: SLF001


# ═══════════════════════════════════════════════════════════════════════════
# create_middleware -- the deny-by-default route matrix (18.2), the anonymous
# allow-list, the persona-asset extension split, /realtime's ?access_token,
# 401 vs 403 response shapes, and request[PRINCIPAL_KEY].
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
        return web.json_response({"principal": request.get(PRINCIPAL_KEY)})

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


class PrincipalKeyTests(unittest.TestCase):
    def test_principal_key_is_a_request_key_not_a_plain_string(self):
        """F6 (#163 round-2 review): `PRINCIPAL_KEY` must be an
        `aiohttp.web.RequestKey`, not a plain string -- a plain string would
        let ANY code write `request["principal"] = ...` directly (bypassing
        this module entirely) and collide with this namespace, and would also
        raise aiohttp's own `NotAppKeyWarning` on every `request[...] = `
        write. A regression back to a string here is exactly the bug F6
        fixed."""
        self.assertIsInstance(PRINCIPAL_KEY, web.RequestKey)
        self.assertNotIsInstance(PRINCIPAL_KEY, str)


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
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/logo.svg")
            self.assertEqual(resp.status, 200)
            self.assertEqual(validator.calls, [])

    async def test_persona_asset_protected_extension_requires_token(self):
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/demo/dummyOrder.json")
            self.assertEqual(resp.status, 401)

    async def test_persona_asset_jpeg_extension_requires_token(self):
        """`.jpeg` was removed from `ANONYMOUS_ASSET_EXTENSIONS` (Rick's #159
        round-1 review, required item 2): the contract is exactly `.svg .png .jpg
        .webp .ico .wav .mp3`, and `.jpeg` widened it. A `.jpeg` asset must now
        require a token like any other non-anonymous extension."""
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/x.jpeg")
            self.assertEqual(resp.status, 401)
            self.assertEqual(validator.calls, [])

    async def test_persona_asset_uppercase_anonymous_extension_passes_without_token(self):
        """F4 (#163 round-1/round-2 review, decided for #147 C# parity): the
        extension match is CASE-INSENSITIVE -- `.JPG` (uppercase) is anonymous
        exactly like `.jpg`. This is the exact extension the review called out
        by name; `persona_loader.py`'s load-time validation applies the
        identical rule (see `test_persona_loader.py`)."""
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/logo.JPG")
            self.assertEqual(resp.status, 200)
            self.assertEqual(validator.calls, [])

    async def test_persona_asset_uppercase_non_anonymous_extension_still_requires_token(self):
        """F4: case-insensitivity only ever ADDS matches within the fixed 7-member
        set -- it must never accidentally widen it to extensions that were never
        anonymous. `.JSON` (any case) is never anonymous; `demo/dummyOrder.JSON`
        must still require a token exactly like the lower-case form."""
        validator = _StubValidator({})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            resp = await client.get(f"/personas/{_DEFAULT_PERSONA_ID}/assets/demo/dummyOrder.JSON")
            self.assertEqual(resp.status, 401)
            self.assertEqual(validator.calls, [])

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

    async def test_invalid_token_log_message_is_repr_escaped(self):
        """N6 (#163 round-1 review): the log message must `%r` (not `%s`) the
        exception, so attacker-controlled content folded into it (e.g. a `kid`
        containing a newline) can't forge additional, fake-looking log lines --
        pins that an embedded newline comes out of the formatted log record as
        the two characters backslash+n, never a real line break."""
        validator = _StubValidator({
            "evil-token": EntraUnauthorized("boom\nFAKE LOG LINE: admin login succeeded"),
        })
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            with self.assertLogs("entra_auth", level="WARNING") as ctx:
                resp = await client.get(
                    "/api/personas", headers={"Authorization": "Bearer evil-token"}
                )
            self.assertEqual(resp.status, 401)
        self.assertEqual(len(ctx.output), 1)
        message = ctx.output[0]
        self.assertNotIn("\nFAKE LOG LINE", message)
        self.assertIn("\\nFAKE LOG LINE", message)

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

    async def test_bearer_scheme_matched_case_insensitively(self):
        """N3 (#163 round-1 review): `bearer`/`BEARER`/`Bearer` must all be
        accepted, matching ASP.NET Core's `JwtBearerHandler` so the two backends
        (this one and #147's C# port) behave identically."""
        principal = {"oid": "the-oid", "tid": _TENANT, "name": "Someone"}
        validator = _StubValidator({"good-token": principal})
        app = _build_app(self.settings, validator)
        async with TestClient(TestServer(app)) as client:
            for scheme in ("bearer", "BEARER", "Bearer", "BeArEr"):
                with self.subTest(scheme=scheme):
                    resp = await client.get(
                        "/api/personas", headers={"Authorization": f"{scheme} good-token"}
                    )
                    self.assertEqual(resp.status, 200)

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
