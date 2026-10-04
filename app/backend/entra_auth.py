"""Entra ID authentication for the Python backend (issue #144, ADR-002, design doc
section 18.2-18.5, 18.11).

Everything lives in this one module, on purpose (18.4): the settings/mode resolver,
the PyJWT-based token validator, and the aiohttp deny-by-default middleware. We
rejected `msal` and `azure-identity` here because they *acquire* tokens; this module
only *validates* tokens someone else already acquired.

Two modes only (18.5), resolved once at startup, never auto-detected:
- ``Mode.ENTRA``: every non-anonymous route requires a valid Entra access token.
- ``Mode.DEVELOPMENT``: pass-through with a synthetic principal, for local dev only.

"Fail fast" means ``resolve_settings`` raises :class:`EntraConfigError` -- the
process must not listen for connections with an invalid or ambiguous auth
configuration. The caller (``app.py``'s ``create_app()``) turns that into
``sys.exit(1)``, matching every other startup fail-fast in this codebase.
"""
from __future__ import annotations

import asyncio
import http.client
import json
import logging
import re
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Mapping
from dataclasses import dataclass
from enum import Enum
from pathlib import PurePosixPath
from typing import Any
from urllib.parse import urlparse

import jwt
from aiohttp import web

logger = logging.getLogger(__name__)


class Mode(Enum):
    ENTRA = "entra"
    DEVELOPMENT = "development"


class EntraConfigError(Exception):
    """Raised by `resolve_settings` on any invalid or ambiguous configuration (18.5).
    The caller must fail fast (`sys.exit(1)`), never start listening."""


class EntraUnauthorized(Exception):
    """A missing, malformed, badly signed, expired, wrong-issuer, wrong-audience or
    wrong-tenant token -- 401 with `WWW-Authenticate: Bearer` (18.4)."""


class EntraForbidden(Exception):
    """A structurally valid token missing the required role or scope -- 403 (18.4)."""


# ---------------------------------------------------------------------------
# GUID / placeholder validation (mirrors app/frontend/src/auth/authMode.ts exactly,
# so the same id is accepted or rejected identically on both backends' configs).
# ---------------------------------------------------------------------------

_GUID_RE = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.IGNORECASE
)
_EMPTY_GUID = "00000000-0000-0000-0000-000000000000"
_PLACEHOLDER_WORDS_RE = re.compile(
    r"(your[-_]?|placeholder|changeme|example|todo|xxxx+|\bfixme\b)", re.IGNORECASE
)


def _is_placeholder(value: str) -> bool:
    v = value.strip()
    if v == "" or v == _EMPTY_GUID:
        return True
    if re.search(r"[<>]", v) or re.search(r"\s", v):
        return True
    return bool(_PLACEHOLDER_WORDS_RE.search(v))


def validate_entra_ids(tenant_id: str, client_id: str) -> str | None:
    """Returns `None` when both ids are non-empty, non-placeholder GUIDs, else a
    human-readable error string naming the first problem found."""
    tenant = (tenant_id or "").strip()
    client = (client_id or "").strip()
    if _is_placeholder(tenant):
        return "Entra tenant id (ENTRA_TENANT_ID) is missing or a placeholder."
    if not _GUID_RE.match(tenant):
        return "Entra tenant id (ENTRA_TENANT_ID) is not a valid GUID."
    if _is_placeholder(client):
        return "Entra client id (ENTRA_CLIENT_ID) is missing or a placeholder."
    if not _GUID_RE.match(client):
        return "Entra client id (ENTRA_CLIENT_ID) is not a valid GUID."
    return None


def _bool_env(env: Mapping[str, str], name: str, default: bool = False) -> bool:
    value = env.get(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


_LOOPBACK_HOSTS = {"127.0.0.1", "::1", "localhost"}


def _validate_instance(raw: str) -> str:
    """`ENTRA_INSTANCE` must be `https://`, except plain `http://` to a loopback host
    (127.0.0.1, ::1, localhost) -- the conformance harness's HTTP-only fake issuer
    needs that (#143); never used in Azure (18.5)."""
    value = raw.strip()
    parsed = urlparse(value)
    scheme = parsed.scheme.lower()
    if scheme == "https":
        pass
    elif scheme == "http" and (parsed.hostname or "").lower() in _LOOPBACK_HOSTS:
        pass
    else:
        raise EntraConfigError(
            f'ENTRA_INSTANCE "{raw}" must start with https://, or plain http:// to a '
            "loopback host (127.0.0.1, ::1, localhost)."
        )
    return value if value.endswith("/") else value + "/"


@dataclass(frozen=True)
class EntraSettings:
    mode: Mode
    tenant_id: str | None
    client_id: str | None
    api_scope: str
    app_role: str
    instance: str

    @property
    def issuer(self) -> str:
        return f"{self.instance}{self.tenant_id}/v2.0"

    @property
    def discovery_url(self) -> str:
        return f"{self.issuer}/.well-known/openid-configuration"


# 18.5's synthetic identity for the Development pass-through, used verbatim as
# `request[PRINCIPAL_KEY]` so downstream code (e.g. the session-token mint)
# never needs to special-case "no real principal" -- every request has one.
SYNTHETIC_PRINCIPAL: dict[str, str] = {
    "oid": "00000000-0000-0000-0000-000000000001",
    "tid": _EMPTY_GUID,
    "name": "Development pass-through (no Entra configured)",
}


def resolve_settings(env: Mapping[str, str]) -> EntraSettings:
    """Resolve the auth mode from raw environment variables (18.5's table, exactly).
    Pure and side-effect-free apart from the one loud warning below -- callers pass
    their own `env` mapping (usually `os.environ`), so this is trivially unit-testable
    without mutating process state. Raises `EntraConfigError` on every invalid or
    ambiguous configuration; never returns a "safe" default for one."""
    raw_mode = (env.get("AUTH_MODE") or "").strip()
    normalized_mode = raw_mode.lower()
    if normalized_mode not in ("", "entra", "development"):
        raise EntraConfigError(
            f'Unknown AUTH_MODE "{raw_mode}". Expected "Entra" or "Development".'
        )

    tenant_id = (env.get("ENTRA_TENANT_ID") or "").strip()
    client_id = (env.get("ENTRA_CLIENT_ID") or "").strip()
    ids_blank = tenant_id == "" and client_id == ""
    ids_error = None if ids_blank else validate_entra_ids(tenant_id, client_id)
    # N4 (#163 round-1 review): normalize to lower case. `validate_entra_ids`
    # itself is case-insensitive (the GUID regex has `re.IGNORECASE`), but a
    # real Entra token's `tid`/`aud` claims are always lower-case GUIDs, so an
    # upper-case id typed into config would otherwise fail every token's
    # issuer/tid/aud comparison in `TokenValidator._validate_sync` -- a
    # confusing fail-closed outage rather than a config error caught here.
    # `AUTH_MODE` is already accepted case-insensitively above; note both for
    # #147 parity.
    if not ids_blank:
        tenant_id = tenant_id.lower()
        client_id = client_id.lower()
    production = _bool_env(env, "RUNNING_IN_PRODUCTION", False)

    if normalized_mode == "entra":
        if ids_blank or ids_error:
            detail = f" {ids_error}" if ids_error else ""
            raise EntraConfigError(
                "AUTH_MODE=Entra requires non-empty, valid ENTRA_TENANT_ID and "
                f"ENTRA_CLIENT_ID.{detail}"
            )
        mode = Mode.ENTRA
    elif normalized_mode == "development":
        if not ids_blank:
            raise EntraConfigError(
                "AUTH_MODE=Development must not be combined with Entra ids "
                "(ENTRA_TENANT_ID/ENTRA_CLIENT_ID). Remove them for a real "
                "pass-through deployment, or set AUTH_MODE=Entra to use them."
            )
        if production:
            raise EntraConfigError(
                "AUTH_MODE=Development (and an unset AUTH_MODE) are refused in "
                "Production. Set AUTH_MODE=Entra with valid ENTRA_TENANT_ID and "
                "ENTRA_CLIENT_ID."
            )
        mode = Mode.DEVELOPMENT
    else:  # unset
        if ids_error:
            raise EntraConfigError(
                f"Entra authentication configuration is invalid: {ids_error} Set "
                "non-empty, valid ENTRA_TENANT_ID and ENTRA_CLIENT_ID for an Entra "
                "deployment, or leave both unset for the Development pass-through."
            )
        if not ids_blank:
            if production:
                raise EntraConfigError(
                    "Production requires AUTH_MODE=Entra to be set explicitly; "
                    "Entra ids alone are not enough once RUNNING_IN_PRODUCTION=true."
                )
            mode = Mode.ENTRA
        else:
            if production:
                raise EntraConfigError(
                    "AUTH_MODE=Development (and an unset AUTH_MODE) are refused in "
                    "Production. Set AUTH_MODE=Entra with valid ENTRA_TENANT_ID and "
                    "ENTRA_CLIENT_ID."
                )
            mode = Mode.DEVELOPMENT

    if mode == Mode.ENTRA and production:
        # APP_SESSION_SECRET is required in Production Entra mode -- a fail fast, not
        # `load_app_secret`'s today's-warning-then-random-secret behavior (18.5),
        # since a per-process random secret would break session tokens across
        # replicas and restarts for every real Entra deployment.
        if not (env.get("APP_SESSION_SECRET") or "").strip():
            raise EntraConfigError(
                "APP_SESSION_SECRET is required in Production Entra mode."
            )

    instance = _validate_instance(env.get("ENTRA_INSTANCE") or "https://login.microsoftonline.com/")

    if mode == Mode.DEVELOPMENT:
        logger.warning(
            "=====================================================================\n"
            "  AUTH_MODE is unconfigured: running with a SYNTHETIC Entra identity.\n"
            "  Every request is treated as oid=%s.\n"
            "  This is the Development pass-through (18.5) -- it must NEVER run in\n"
            "  Production. Set AUTH_MODE=Entra with valid ENTRA_TENANT_ID and\n"
            "  ENTRA_CLIENT_ID for a real deployment.\n"
            "=====================================================================",
            SYNTHETIC_PRINCIPAL["oid"],
        )
        return EntraSettings(
            mode=mode, tenant_id=None, client_id=None,
            api_scope=env.get("ENTRA_API_SCOPE") or "access_as_user",
            app_role=env.get("ENTRA_APP_ROLE") or "DriveThru.User",
            instance=instance,
        )

    return EntraSettings(
        mode=mode,
        tenant_id=tenant_id,
        client_id=client_id,
        api_scope=env.get("ENTRA_API_SCOPE") or "access_as_user",
        app_role=env.get("ENTRA_APP_ROLE") or "DriveThru.User",
        instance=instance,
    )


# ---------------------------------------------------------------------------
# Token validation (18.4)
# ---------------------------------------------------------------------------

class TokenValidator:
    """Validates an Entra access token against `settings` (18.4's parity contract).

    Discovery (learning the tenant's `jwks_uri`) is lazy: the first HTTP call this
    class ever makes happens inside the first `validate()`, never at construction or
    at app startup, so an Entra/fake-issuer outage can't crash-loop the app. Once
    discovered, the `jwt.PyJWKClient` itself provides the 24h JWKS cache with at most
    one forced re-fetch per 5 minutes on an unknown `kid` (its own `lifespan` /
    `cooldown_duration`, verified against its source -- this is not reimplemented
    here).

    N1 (#163 round-1 review, Entra-outage resilience): three things that only
    matter while the tenant's discovery endpoint is unreachable, never on the
    happy path:
      - `jwt.PyJWKClient` is built with this validator's own `timeout`, so a
        hung discovery endpoint can't hold its internal lock for its 30s
        default while every request waits behind it.
      - `_validate_sync` rejects anything that isn't structurally a JWT
        (`jwt.get_unverified_header`) before ever calling `_get_jwks_client`,
        so a flood of garbage tokens during an outage can't each independently
        retry discovery.
      - A failed discovery fetch is itself cached, negatively, for
        `_discovery_failure_cooldown` seconds, so repeated requests during an
        outage 401 immediately instead of each re-attempting the same 10s-
        timeout network call serially under `self._lock`.

    #223 round 2 (Rick's review): a LIVE JWKS refetch (e.g. the 24h cache
    expired, or an unknown `kid` forced one) can fail the exact same way a
    discovery fetch can -- `jwt.PyJWKClient.fetch_data` only wraps
    `URLError`/`TimeoutError` in `PyJWKClientConnectionError`; a body-read
    `OSError`/`http.client.HTTPException`, or a non-JSON body
    (`json.JSONDecodeError`, a `ValueError`), all propagate uncaught straight
    through `get_signing_key_from_jwt`. `_get_signing_key` wraps that call the
    same way `_fetch_discovery_document` wraps `urlopen`, and negatively
    caches a failure for `_jwks_failure_cooldown` seconds for the same reason:
    so a JWKS-endpoint outage 401s immediately instead of every request
    serially re-attempting the same doomed network call under `self._lock`.

    #223 round 3 (Rick's review): that cooldown must gate ONLY the network
    fetch, never a `kid` already resolvable from `PyJWKClient`'s own
    still-valid cache -- otherwise a single forged, unauthenticated token
    with an unknown `kid`, arriving during a transient JWKS-endpoint fault,
    would negatively cache a failure that then ALSO rejects every already-
    cached, perfectly valid `kid` for the next `_jwks_failure_cooldown`
    seconds: a trivial, repeatable, unauthenticated denial of service.
    `_get_signing_key` checks the cache directly while cooling down and only
    refuses when that cache is empty/expired or genuinely misses the `kid`.
    """

    def __init__(
        self,
        settings: EntraSettings,
        *,
        timeout: float = 10.0,
        discovery_failure_cooldown: float = 30.0,
        jwks_failure_cooldown: float = 30.0,
    ) -> None:
        self._settings = settings
        self._timeout = timeout
        self._discovery_failure_cooldown = discovery_failure_cooldown
        self._jwks_failure_cooldown = jwks_failure_cooldown
        self._jwks_client: jwt.PyJWKClient | None = None
        self._discovery_failure_until: float | None = None
        self._jwks_failure_until: float | None = None
        self._lock = threading.Lock()

    def _fetch_discovery_document(self) -> dict:
        request = urllib.request.Request(
            self._settings.discovery_url, headers={"Accept": "application/json"}
        )
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                body = json.loads(response.read())
        except (
            urllib.error.URLError,
            TimeoutError,
            ValueError,
            OSError,
            http.client.HTTPException,
        ) as exc:
            # #223 (Rick's #222 review): `urlopen()` itself only ever raises
            # `URLError`/`TimeoutError`, both already covered above, but the
            # *body read* (`response.read()`) and `json.loads()` can also raise
            # a plain `OSError` (e.g. `ConnectionResetError` if the peer drops
            # mid-response) or an `http.client.HTTPException` (e.g.
            # `IncompleteRead`, `BadStatusLine`) -- neither of which is a
            # `URLError` subclass. Before this fix those propagated out of
            # `_fetch_discovery_document` uncaught, past `_get_jwks_client`'s
            # `except EntraUnauthorized` (so the 30s negative-cache cooldown
            # never engaged) and all the way to aiohttp's default handler,
            # which turns an unhandled exception into a 500 -- inconsistent
            # with every other discovery failure here, which fails closed as a
            # 401 (`_unauthorized_response`, consistent today). Catching the
            # same superset of body-read failures here, and re-raising as
            # `EntraUnauthorized`, restores that consistency and lets the
            # existing cooldown apply.
            raise EntraUnauthorized(f"OIDC discovery failed: {exc}") from exc
        if not isinstance(body, dict):
            # A non-dict JSON body (e.g. a bare JSON list or string) would
            # otherwise crash `.get("jwks_uri")` below with an AttributeError,
            # which -- like the exceptions above -- is uncaught and surfaces as
            # a 500 instead of the fail-closed 401 every other malformed-
            # discovery-response case gets.
            raise EntraUnauthorized(
                f"OIDC discovery document was not a JSON object (got {type(body).__name__})"
            )
        return body

    def _get_jwks_client(self) -> jwt.PyJWKClient:
        with self._lock:
            if self._jwks_client is None:
                now = time.monotonic()
                if self._discovery_failure_until is not None and now < self._discovery_failure_until:
                    # N1: a discovery fetch failed recently -- refuse without
                    # retrying the network call again until the cooldown
                    # elapses, instead of blocking this request (and every
                    # other one queued behind `self._lock`) on another
                    # `self._timeout`-second attempt that is very likely to
                    # fail the same way during a real outage.
                    raise EntraUnauthorized(
                        "OIDC discovery failed recently; refusing to retry yet."
                    )
                try:
                    discovery = self._fetch_discovery_document()
                    jwks_uri = discovery.get("jwks_uri")
                    if not jwks_uri:
                        raise EntraUnauthorized("OIDC discovery document has no jwks_uri")
                except EntraUnauthorized:
                    self._discovery_failure_until = now + self._discovery_failure_cooldown
                    raise
                self._jwks_client = jwt.PyJWKClient(
                    jwks_uri,
                    cache_jwk_set=True,
                    lifespan=86400,  # 24h cache (18.4)
                    cooldown_duration=300,  # at most one refetch per 5 min on unknown kid
                    timeout=self._timeout,  # N1: never PyJWKClient's own 30s default
                )
                self._discovery_failure_until = None
            return self._jwks_client

    def _get_signing_key(self, client: jwt.PyJWKClient, token: str) -> jwt.PyJWK:
        """Wraps `client.get_signing_key_from_jwt`, negatively caching body-read
        failures the same way `_get_jwks_client` negatively caches discovery
        failures (#223 round 2, Rick's review).

        PyJWT 2.14's `PyJWKClient.fetch_data` only wraps `URLError`/
        `TimeoutError` in `PyJWKClientConnectionError` -- a `ConnectionResetError`
        (`OSError`) or `http.client.IncompleteRead` (`http.client.HTTPException`)
        mid-body-read, or a non-JSON body (`json.JSONDecodeError`, a
        `ValueError`), all propagate uncaught, past every `except` clause in
        `_validate_sync`, surfacing as a raw 500 -- unlike every other
        JWKS/discovery failure here, which fails closed as a 401. The
        cooldown additionally spares a request storm (e.g. an unknown `kid`
        during a JWKS-endpoint outage) from each independently re-attempting
        the same doomed network call serially under `self._lock`.

        #223 round 3 (Rick's review, a real DoS this time, not just a 500):
        the cooldown must gate ONLY a network fetch, never a `kid` that's
        already resolvable from `PyJWKClient`'s own still-valid 24h JWK Set
        cache. Rick's repro: kid A is cached and valid; an unauthenticated,
        forged token with a random kid B arrives while the JWKS endpoint has
        a transient fault, forcing a refetch that fails and engages the
        cooldown; a legitimate request for kid A then ALSO gets rejected --
        "refusing to retry yet" -- even though resolving kid A needs no
        network call at all. That turns a single forged `kid` into a
        trivial, unauthenticated 30s (repeatable forever) denial of service
        against every legitimate, already-cached token. While the cooldown is
        active, a cache hit for the requested `kid` must still succeed; only
        a cache miss (or an empty/expired cache) is refused without
        attempting the network call the cooldown exists to prevent.
        """
        with self._lock:
            now = time.monotonic()
            cooling_down = (
                self._jwks_failure_until is not None and now < self._jwks_failure_until
            )

        if cooling_down:
            cached_jwk_set = (
                client.jwk_set_cache.get() if client.jwk_set_cache is not None else None
            )
            if cached_jwk_set is not None:
                # `jwk_set_cache.get()` already returns `None` on an empty or
                # expired cache (its own `is_expired()` check), so reaching
                # here means a still-valid cache exists -- `get_signing_keys()`
                # (no `refresh=True`) re-reads that exact same still-valid
                # cache entry and therefore cannot itself trigger a network
                # fetch; it is not a plain re-fetch call.
                kid = jwt.get_unverified_header(token).get("kid")
                signing_key = client.match_kid(client.get_signing_keys(), kid)
                if signing_key is not None:
                    return signing_key
            raise EntraUnauthorized(
                "JWKS refetch failed recently; refusing to retry yet."
            )

        try:
            signing_key = client.get_signing_key_from_jwt(token)
        except (OSError, http.client.HTTPException, ValueError) as exc:
            with self._lock:
                self._jwks_failure_until = time.monotonic() + self._jwks_failure_cooldown
            raise EntraUnauthorized(f"Unable to fetch JWKS: {exc}") from exc
        with self._lock:
            self._jwks_failure_until = None
        return signing_key

    async def validate(self, token: str) -> dict[str, Any]:
        """Returns `{"oid", "tid", "name"}` on success. Raises `EntraUnauthorized` (401)
        or `EntraForbidden` (403). Runs PyJWT's synchronous fetch/verify through
        `asyncio.to_thread` so the event loop never blocks on a JWKS cache miss."""
        return await asyncio.to_thread(self._validate_sync, token)

    def _validate_sync(self, token: str) -> dict[str, Any]:
        try:
            # N1: reject anything that isn't even structurally a JWT (three
            # dot-separated, base64url segments with a parseable JSON header)
            # before ever touching discovery/JWKS -- a flood of garbage
            # tokens must not each independently retry a hung Entra endpoint.
            jwt.get_unverified_header(token)
        except jwt.PyJWTError as exc:
            raise EntraUnauthorized(f"Malformed token: {exc}") from exc

        try:
            client = self._get_jwks_client()
            signing_key = self._get_signing_key(client, token)
        except EntraUnauthorized:
            raise
        # N6 (#163 round-1 review): `jwt.PyJWKClientError` is a SUBCLASS of
        # `jwt.PyJWTError`, so it must be caught first -- the reverse order (as
        # this used to be) made this branch unreachable dead code, and every
        # signing-key-resolution failure was mislabeled "Malformed token"
        # instead of "Unable to resolve a signing key" in logs.
        except jwt.PyJWKClientError as exc:
            raise EntraUnauthorized(f"Unable to resolve a signing key: {exc}") from exc
        except jwt.PyJWTError as exc:
            raise EntraUnauthorized(f"Malformed token: {exc}") from exc

        try:
            claims = jwt.decode(
                token,
                signing_key.key,
                algorithms=["RS256"],  # RS256 only -- `none` and HS* are rejected
                audience=[self._settings.client_id, f"api://{self._settings.client_id}"],
                issuer=self._settings.issuer,
                leeway=300,  # 5-minute clock skew on exp/nbf
                options={"require": ["exp", "nbf"]},
            )
        except jwt.PyJWTError as exc:
            raise EntraUnauthorized(f"Token validation failed: {exc}") from exc

        if claims.get("tid") != self._settings.tenant_id:
            raise EntraUnauthorized("Token tid does not match ENTRA_TENANT_ID.")

        roles = claims.get("roles") or []
        if not isinstance(roles, list) or self._settings.app_role not in roles:
            raise EntraForbidden("Token is missing the required app role.")

        # N2 (#163 round-1 review): `scp` is only ever attacker/tenant
        # controlled -- a non-string value (e.g. a list, the shape JSON would
        # produce for a repeated claim) must not raise `AttributeError` out of
        # `.split()` (a 500), the same way a non-list `roles` is guarded above.
        scp = claims.get("scp")
        scopes = scp.split() if isinstance(scp, str) else []
        if self._settings.api_scope not in scopes:
            # Includes app-only tokens (client credentials flow), which carry no `scp`
            # at all -- rejected on purpose (18.4, "Retail Pulse's AllowAppOnlyTokens
            # opt-in is not ported").
            raise EntraForbidden(
                "Token is missing the required scope (app-only tokens are rejected)."
            )

        return {"oid": claims.get("oid"), "tid": claims.get("tid"), "name": claims.get("name")}


# ---------------------------------------------------------------------------
# Deny-by-default middleware (18.2's route matrix)
# ---------------------------------------------------------------------------

# Anonymous by route name -- `/` (the SPA shell), the static SPA bundle, and the
# health probe (18.2). A route added later gets a NEW name and is therefore
# protected by default unless explicitly added here on purpose.
#
# N7 (#163 round-1 review): `static`'s anonymity is only safe as long as
# `app/frontend/public` (the directory this route serves) holds nothing but
# public SPA/branding assets -- never persona data or anything else that
# should require a session. If that directory ever starts holding
# guest-specific or tenant-specific files, `static` must be re-reviewed before
# it stays on this list.
ANONYMOUS_ROUTE_NAMES = frozenset({"index", "health", "static"})

# The one route that is anonymous for SOME requests and protected for others,
# depending on the requested file's extension (18.2: public branding assets vs.
# `demo/*.json`, fetched with `fetch()` and therefore able to carry a bearer).
#
# F4 (#163 round-1/round-2 review, decided for #147 C# parity): the match is
# CASE-INSENSITIVE. The requested path's extension is lower-cased (see
# `_is_anonymous` below and `persona_loader._validate_persona_assets`, which
# applies the identical rule when validating persona packs at load time)
# before being compared against this fixed, 7-member set. So `logo.svg`,
# `logo.SVG`, and `logo.Svg` are all anonymous; `demo/dummyOrder.JSON` (any
# case) is NOT, because `.json` is not and will never be in this set. The C#
# port must do the same: lower-case the extension, then compare against this
# exact literal set.
PERSONA_ASSET_ROUTE_NAME = "persona-asset"
ANONYMOUS_ASSET_EXTENSIONS = frozenset({".svg", ".png", ".jpg", ".webp", ".ico", ".wav", ".mp3"})

# The one route where the Entra access token may be read from a query parameter
# instead of the Authorization header (18.3) -- browsers can't set headers on a
# WebSocket upgrade request.
REALTIME_PATH = "/realtime"

# F6 (#163 round-2 review): a `web.RequestKey` instead of a plain string, so
# `request[PRINCIPAL_KEY] = ...` doesn't trigger aiohttp's `NotAppKeyWarning`.
# Every reader/writer of the authenticated principal (this module, `app.py`'s
# `get_session_token`, `rtmt.py`'s websocket oid-binding check, and the test
# suite) must import and use this same key object -- it is intentionally NOT
# interchangeable with the string `"principal"` (aiohttp keys the request's
# dict by object identity, not by the key's `str()`).
PRINCIPAL_KEY: web.RequestKey = web.RequestKey("principal", dict)


def _is_anonymous(request: web.Request) -> bool:
    route = request.match_info.route
    name = getattr(route, "name", None)
    if name in ANONYMOUS_ROUTE_NAMES:
        return True
    if name == PERSONA_ASSET_ROUTE_NAME:
        asset_path = request.match_info.get("asset_path", "")
        suffix = PurePosixPath(asset_path).suffix.lower()
        return suffix in ANONYMOUS_ASSET_EXTENSIONS
    return False


def _extract_token(request: web.Request) -> str | None:
    header = request.headers.get("Authorization", "")
    # N3 (#163 round-1 review): the scheme name is matched CASE-INSENSITIVELY
    # (`bearer x`, `BEARER x`, `Bearer x` are all accepted), the same as
    # ASP.NET Core's `JwtBearerHandler` -- so the C# and Python backends behave
    # identically for #147. Everything else about the header is unaffected:
    # a non-bearer scheme (e.g. `Basic ...`) and a bare `Bearer` with no token
    # value both still fall through to the `None` below, exactly as before.
    scheme, _, value = header.partition(" ")
    if scheme.lower() == "bearer":
        token = value.strip()
        return token or None
    # `?access_token` is honored on /realtime ONLY (18.3) -- everywhere else it's
    # ignored, so a token in the query string there still gets 401.
    if request.path == REALTIME_PATH:
        token = request.query.get("access_token")
        if token:
            return token
    return None


def _unauthorized_response() -> web.Response:
    return web.json_response(
        {"error": "unauthorized"}, status=401, headers={"WWW-Authenticate": "Bearer"}
    )


def _forbidden_response() -> web.Response:
    return web.json_response({"error": "forbidden"}, status=403)


def create_middleware(settings: EntraSettings, validator: TokenValidator | None = None):
    """Builds the deny-by-default aiohttp middleware for `settings.mode` (18.2).

    In `Mode.DEVELOPMENT` every request gets the synthetic principal and passes
    through unconditionally -- there is no token concept to enforce. In
    `Mode.ENTRA`, anonymous routes pass through unauthenticated; everything else
    requires a valid bearer (or, on `/realtime` only, `?access_token`), and sets
    `request[PRINCIPAL_KEY]` to `{"oid", "tid", "name"}` on success.
    """
    if settings.mode == Mode.ENTRA and validator is None:
        validator = TokenValidator(settings)

    @web.middleware
    async def entra_middleware(request: web.Request, handler):
        if settings.mode == Mode.DEVELOPMENT:
            request[PRINCIPAL_KEY] = dict(SYNTHETIC_PRINCIPAL)
            return await handler(request)

        if _is_anonymous(request):
            return await handler(request)

        token = _extract_token(request)
        if not token:
            return _unauthorized_response()

        try:
            principal = await validator.validate(token)
        except EntraForbidden as exc:
            logger.warning("Rejected request with a valid token missing role/scope: %r", exc)
            return _forbidden_response()
        except EntraUnauthorized as exc:
            # N6 (#163 round-1 review): `%r` (not `%s`), so attacker-controlled
            # content folded into these messages (e.g. an unverified token's
            # `kid`) is repr-escaped -- a newline or other control character in
            # it can't forge additional, fake-looking log lines.
            logger.warning("Rejected request with an invalid token: %r", exc)
            return _unauthorized_response()

        request[PRINCIPAL_KEY] = principal
        return await handler(request)

    return entra_middleware
