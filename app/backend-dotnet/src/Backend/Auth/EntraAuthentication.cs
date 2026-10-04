using System.IO;
using System.Net.Http;
using System.Text.Json;
using Backend.Personas;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Auth;

/// <summary>
/// Issue #147 (ADR-002): wires Entra JwtBearer validation as a fallback authorization policy over
/// every API route, byte-for-byte matching app/backend/entra_auth.py's TokenValidator + deny-by-
/// default entra_middleware (docs/dotnet_mapping.md). Split into small, pure, directly-unit-
/// testable static methods (<see cref="ConfigureJwtBearer"/>, <see cref="BuildAccessPolicy"/>) --
/// matching the codebase's existing philosophy (HealthEndpoint.Handle, RealtimeAuthGate.Check,
/// PersonaRoutes.Map) -- so both Program.cs and a lightweight test-only host can call the exact
/// same wiring without booting the full application.
/// </summary>
public static class EntraAuthentication
{
    /// <summary>
    /// entra_auth.py's ANONYMOUS_ASSET_EXTENSIONS: a persona asset is anonymous purely by its
    /// (lowercased) file extension, regardless of path segments -- e.g. "demo/dummyOrder.json"
    /// stays protected even though it sits under a "demo/" folder, because ".json" isn't in this
    /// set. #163 (F4) has now decided and pinned this rule as CASE-INSENSITIVE (merged in PR
    /// #222): "logo.svg"/"logo.SVG"/"logo.Svg" are all anonymous; "demo/dummyOrder.JSON" (any
    /// case) stays protected, since ".json" is not and will never be in this set. This C# port
    /// already matched that exact rule (the lower-case comparison below predates #163's decision
    /// by coincidence) -- no behavior change was needed here, only this comment and the stale
    /// "Unity is concurrently finalizing" language it replaces.
    /// </summary>
    public static readonly IReadOnlySet<string> AnonymousAssetExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".svg", ".png", ".jpg", ".webp", ".ico", ".wav", ".mp3" };

    /// <summary>entra_auth.py's REALTIME_PATH -- the only route where a `?access_token=` query
    /// parameter is honoured as a Bearer token fallback.</summary>
    public const string RealtimePath = "/realtime";

    /// <summary>
    /// Registers the Entra JwtBearer authentication scheme and the fallback+default authorization
    /// policy (issue #147 bullets 1 and 3). Only called in <see cref="EntraMode.Entra"/> -- in
    /// <see cref="EntraMode.Development"/>, Program.cs registers no authentication scheme at all
    /// and leaves authorization unconfigured (FallbackPolicy null), matching entra_auth.py's
    /// Development pass-through: unconditional, zero-enforcement access.
    /// </summary>
    public static void AddEntraAuthentication(IServiceCollection services, EntraSettings settings)
    {
        // #223 item 1 (Rick's review of PR #225, mirrored here for #147 parity): one gate per
        // running app, shared by every request on the JwtBearer scheme -- see
        // ConfigureJwtBearer's OnMessageReceived/OnAuthenticationFailed wiring below.
        var discoveryFailureGate = new DiscoveryFailureGate();
        services.AddSingleton(discoveryFailureGate);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(options, settings, discoveryFailureGate));

        services.AddSingleton<IAuthorizationHandler, EntraAccessRequirementHandler>();
        services.AddAuthorization(options =>
        {
            var policy = BuildAccessPolicy(settings);
            options.DefaultPolicy = policy;
            options.FallbackPolicy = policy;
        });
    }

    /// <summary>
    /// Pure JwtBearerOptions configuration -- issue #147 bullet 1's exact validation shape:
    /// issuer/audience/signing-keys-via-OIDC-metadata/lifetime, plus the explicit `tid` check
    /// entra_auth.py performs separately from PyJWT's own issuer check. ValidAudiences accepts
    /// both the bare client id and the "api://{clientId}" App ID URI form, matching
    /// TokenValidator._validate_sync's `audience=[client_id, f"api://{client_id}"]`.
    /// <paramref name="discoveryFailureGate"/> defaults to a fresh, per-call instance when
    /// omitted (existing callers/tests that don't care about the cooldown); AddEntraAuthentication
    /// passes a single app-lifetime instance shared across every request on the scheme.
    /// </summary>
    public static void ConfigureJwtBearer(
        JwtBearerOptions options, EntraSettings settings, DiscoveryFailureGate? discoveryFailureGate = null)
    {
        discoveryFailureGate ??= new DiscoveryFailureGate();
        options.Authority = settings.Issuer;
        options.RequireHttpsMetadata = settings.Instance.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        // #163 N1a (Python follow-up, mirrored here for #147 parity): bound how long a hung OIDC
        // discovery/JWKS endpoint can stall a request. Python's TokenValidator passes this same
        // 10s default to jwt.PyJWKClient's own `timeout=` (overriding PyJWT's 30s default) --
        // ASP.NET Core's JwtBearerHandler uses this BackchannelTimeout for both the discovery
        // document fetch (via its ConfigurationManager) and the signing-key refresh, so setting
        // it here is the direct .NET equivalent. (N1b -- the early malformed-token reject before
        // any network call -- is a Python-side implementation detail of its hand-rolled JWKS
        // client; ASP.NET Core's JwtBearerHandler already parses/validates the token shape before
        // ever touching the ConfigurationManager, so there's no equivalent gap to close here. N1c
        // -- the negative discovery-failure cache -- IS reimplemented below via
        // DiscoveryFailureGate, see OnMessageReceived/OnAuthenticationFailed; see
        // docs/dotnet_mapping.md's #147/#223 section for the full comparison.)
        options.BackchannelTimeout = TimeSpan.FromSeconds(10);
        // entra_auth.py's TokenValidator reads claims by their raw Entra names (tid, oid, roles,
        // scp) -- MapInboundClaims=false stops the legacy ClaimTypes.* remap so those claim types
        // survive unchanged onto ClaimsPrincipal, matching FakeEntraIssuerJwtBearerValidationTests.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudiences = [settings.ClientId!, $"api://{settings.ClientId}"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireExpirationTime = true,
            RoleClaimType = "roles",
            NameClaimType = "name",
        };
        options.Events = new JwtBearerEvents
        {
            // Issue #147 bullet 4: `?access_token=` is honoured ONLY on /realtime, and only when
            // no Authorization header is present -- entra_auth.py's _extract_token checks the
            // Bearer header first, unconditionally, everywhere; the query fallback only applies
            // when that header is absent AND the path is exactly /realtime. #163 N3 (mirrored
            // here, already native): the Authorization header's "Bearer" scheme itself is matched
            // CASE-INSENSITIVELY by ASP.NET Core's JwtBearerHandler before this event ever runs
            // (it compares the scheme token with StringComparison.OrdinalIgnoreCase internally),
            // so "bearer x"/"BEARER x"/"BeArEr x" are already accepted exactly like Python's
            // entra_middleware._extract_token now does -- no code change was needed for that part.
            OnMessageReceived = context =>
            {
                if (!context.Request.Headers.ContainsKey("Authorization") &&
                    context.Request.Path.Equals(RealtimePath, StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!StringValues.IsNullOrEmpty(accessToken))
                    {
                        context.Token = accessToken.ToString();
                    }
                }

                // #223 item 1 (Rick's review of PR #225, mirrored here for #147 parity): once
                // OnAuthenticationFailed below has recorded a discovery/JWKS fetch failure,
                // short-circuit here -- before JwtBearerHandler ever reattempts the same doomed
                // network call via SetupTokenValidationParametersAsync -- so a downed or
                // unreachable identity provider doesn't cost every single request another
                // backchannel timeout. Only matters when a token is actually present; an
                // anonymous/no-token request never reaches the config-manager fetch anyway. Fails
                // closed via context.Fail -- same 401 Challenge path as any other authentication
                // failure (see OnChallenge below), never a 500/503.
                //
                // #246 mirror-check (Summer's Python JWKS-cooldown-race fix; coordinator's
                // follow-up question on PR #226): this short-circuit must NOT fire for a request
                // whose signing key is already resolvable without any network call --
                // HasUsableLastKnownGoodConfiguration's doc comment below has the full story.
                var hasToken = context.Request.Headers.ContainsKey("Authorization") || context.Token is not null;
                if (hasToken && discoveryFailureGate.IsInCooldown() && !HasUsableLastKnownGoodConfiguration(options))
                {
                    context.Fail("OIDC discovery/JWKS endpoint is in a failure cooldown window.");
                }

                return Task.CompletedTask;
            },

            // #223 item 1 (Rick's review of PR #225, mirrored here for #147 parity):
            // entra_auth.py's _fetch_discovery_document now catches URLError/TimeoutError/
            // ValueError/OSError/http.client.HTTPException (connection resets, incomplete reads,
            // malformed JSON) and re-raises as EntraUnauthorized, so every discovery/JWKS failure
            // surfaces as a 401, never a 500, with a 30s negative-cache cooldown before the next
            // attempt. JwtBearerHandler's own outer catch (HandleAuthenticateAsync) RE-THROWS any
            // exception reaching here unless this event sets a Result -- left unhandled, a
            // connection reset/incomplete read/malformed-JSON discovery failure would crash the
            // pipeline as an unhandled 500, not fail closed as a 401. IsDiscoveryOrBackchannelFailure
            // recognizes that failure shape (walking InnerException, since
            // Microsoft.IdentityModel.Protocols.ConfigurationManager&lt;T&gt; wraps the real cause
            // in its own InvalidOperationException when no last-known-good config exists yet);
            // context.Fail converts the would-be-rethrown exception into an ordinary
            // AuthenticateResult.Fail, which flows into the existing OnChallenge handler's 401
            // response exactly like any other authentication failure.
            OnAuthenticationFailed = context =>
            {
                if (IsDiscoveryOrBackchannelFailure(context.Exception))
                {
                    discoveryFailureGate.RecordFailure();
                    context.Fail(context.Exception);
                }
                return Task.CompletedTask;
            },

            // entra_auth.py's TokenValidator checks `claims.get("tid") != settings.tenant_id`
            // explicitly, separately from PyJWT's own issuer validation -- a defense-in-depth
            // check this port keeps even though ValidIssuer already pins the same tenant via the
            // issuer URL, since a future issuer shape change could otherwise silently drop it.
            OnTokenValidated = context =>
            {
                var tid = context.Principal?.FindFirst("tid")?.Value;
                if (!string.Equals(tid, settings.TenantId, StringComparison.Ordinal))
                {
                    context.Fail("Token tid does not match the configured ENTRA_TENANT_ID.");
                }
                return Task.CompletedTask;
            },

            // Issue #147 bullet 3: byte-compatible 401 body with Python's entra_middleware's
            // _unauthorized_response -- {"error": "unauthorized"} plus WWW-Authenticate: Bearer.
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error": "unauthorized"}""");
            },

            // entra_middleware's _forbidden_response -- {"error": "forbidden"}, no challenge
            // header (a valid-but-under-permissioned token isn't a "come back with a token"
            // case).
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error": "forbidden"}""");
            },
        };
    }

    /// <summary>
    /// True if `exception` (or any exception in its InnerException chain) is a network/
    /// backchannel or malformed-response failure from fetching OIDC discovery metadata or JWKS
    /// signing keys -- the .NET analogue of entra_auth.py's
    /// `except (urllib.error.URLError, TimeoutError, ValueError, OSError,
    /// http.client.HTTPException)` (#223, Rick's review of PR #225). Walks InnerException because
    /// Microsoft.IdentityModel.Protocols.ConfigurationManager&lt;T&gt; wraps the real cause (e.g. an
    /// HttpRequestException from a connection reset, or a JsonException from a malformed body)
    /// inside its own InvalidOperationException ("IDX20803") when no last-known-good
    /// configuration exists yet to fall back on.
    /// </summary>
    private static bool IsDiscoveryOrBackchannelFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// #246 mirror-check (Summer's Python JWKS-cooldown-race fix; coordinator's follow-up question
    /// on PR #226): entra_auth.py's own discovery-failure cooldown (`_discovery_failure_until`)
    /// only ever gates the COLD-START path -- the check lives solely inside
    /// `if self._jwks_client is None`, so once a JWKS client has been built successfully once, a
    /// LATER forged/unknown-kid-triggered refetch failure never re-engages it; from then on Python
    /// relies entirely on PyJWT's own internal signing-key cache plus its 5-minute
    /// `cooldown_duration` (see the comment above TokenValidator.__init__ -- "this is not
    /// reimplemented here"). <see cref="DiscoveryFailureGate"/>'s OnMessageReceived check had no
    /// equivalent "only while cold" narrowing: <see cref="EntraAuthentication.IsDiscoveryOrBackchannelFailure"/>
    /// only records a failure for a genuine network/parse error (never a plain
    /// SecurityTokenSignatureKeyNotFoundException from an unrelated unknown kid), but IF that
    /// unknown-kid token's forced <c>ConfigurationManager.RequestRefresh()</c> attempt happened to
    /// coincide with a real transient Entra/discovery outage, the resulting cooldown would 401
    /// EVERY other request for the next 30s -- including ones bearing a completely different,
    /// already-cached kid that Microsoft.IdentityModel.Tokens could resolve instantly from
    /// <see cref="BaseConfigurationManager.LastKnownGoodConfiguration"/> without touching the
    /// network at all. That is exactly the "forged/unrelated input blocks an already-cached,
    /// otherwise-valid result" bug class Rick's review fixed in Python (#246: a cache-hit path
    /// must not be defeated by an unrelated miss). <see cref="BaseConfigurationManager.IsLastKnownGoodValid"/>
    /// is a pure, allocation-free, synchronous property read (no I/O, no async, mirroring
    /// `self._jwks_client is not None`'s own O(1) cost) that Microsoft.IdentityModel.Tokens itself
    /// only ever sets to a non-null, unexpired value after at least one token has already
    /// validated successfully against some configuration -- i.e. "warm" in precisely the same
    /// sense Python's `self._jwks_client is not None` means "warm". Letting the request through in
    /// that case does not reopen the unbounded-repeated-network-call DoS the gate exists to
    /// prevent: an unresolvable/still-unknown kid falls through to ordinary token validation,
    /// which is itself bounded by <see cref="BaseConfigurationManager.RefreshInterval"/> (5-minute
    /// default) -- more conservative than this gate's own 30s window, not less.
    /// </summary>
    private static bool HasUsableLastKnownGoodConfiguration(JwtBearerOptions options) =>
        options.ConfigurationManager is BaseConfigurationManager { IsLastKnownGoodValid: true };

    /// <summary>
    /// The single fallback+default authorization policy (issue #147 bullet 1/3): one
    /// <see cref="EntraAccessRequirement"/>, whose handler both enforces the role+scope
    /// requirement and special-cases the persona-asset anonymous-extension escape hatch -- see
    /// <see cref="EntraAccessRequirementHandler"/>.
    /// </summary>
    public static AuthorizationPolicy BuildAccessPolicy(EntraSettings settings) =>
        new AuthorizationPolicyBuilder()
            .AddRequirements(new EntraAccessRequirement(settings.AppRole, settings.ApiScope))
            .Build();
}

/// <summary>The one requirement <see cref="EntraAuthentication.BuildAccessPolicy"/> builds its
/// policy from -- the configured app role and API scope every authenticated request must carry.</summary>
public sealed class EntraAccessRequirement(string appRole, string apiScope) : IAuthorizationRequirement
{
    public string AppRole { get; } = appRole;
    public string ApiScope { get; } = apiScope;
}

/// <summary>
/// Byte-for-byte port of entra_auth.py's deny-by-default middleware's authorization half (the
/// anonymous-route/anonymous-asset-extension check already happened in Python's `_is_anonymous`
/// before token extraction even began; here the same decision is folded into the ONE
/// authorization requirement every non-exempt endpoint falls under, since ASP.NET Core's
/// `FallbackPolicy` has no per-request "skip this check" primitive of its own). Also ports
/// TokenValidator's role ("roles" must contain app_role, exact match -- no prefix/substring
/// matching) and scope ("scp" split on whitespace must contain api_scope, exact match) checks,
/// which JwtBearer's own validation doesn't express.
/// </summary>
public sealed class EntraAccessRequirementHandler : AuthorizationHandler<EntraAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, EntraAccessRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext && IsAnonymousAssetRequest(httpContext))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Leave pending (no Succeed, no explicit Fail): the authentication result for this
            // request has already failed/is absent, so ASP.NET Core's PolicyEvaluator resolves
            // this as a Challenge (401), matching entra_auth.py's EntraUnauthorized path --
            // never reaching here at all for a role/scope check, since there's no principal yet.
            return Task.CompletedTask;
        }

        var roles = context.User.FindAll("roles").Select(claim => claim.Value);
        var hasRole = roles.Contains(requirement.AppRole, StringComparer.Ordinal);

        // #163 N2 (Python follow-up, mirrored here for #147 parity): `scopes = scp.split() if
        // isinstance(scp, str) else []`. A JSON-array-shaped `scp` claim (non-standard for Entra,
        // which always issues it as a single space-delimited string, but not unforgeable by a
        // malicious/malformed token) previously crashed Python's `.split()` with an AttributeError
        // (500). In .NET, System.IdentityModel's JWT handler materializes a JSON array claim value
        // as MULTIPLE separate Claim("scp", ...) entries rather than one non-string value, so the
        // exact Python crash shape can't occur here -- but the equivalent question still matters:
        // what should a request with more than one "scp" claim do? We treat it the same way
        // Python's guard does for a non-string value: not-a-valid-scope-string, so no scope
        // matches and the requirement fails closed (403), rather than naively taking just the
        // first claim via FindFirst and silently ignoring the rest (which could under- or
        // over-grant depending on which value happened to come first).
        var scopeClaims = context.User.FindAll("scp").ToList();
        var hasScope = scopeClaims.Count == 1 &&
            scopeClaims[0].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(requirement.ApiScope, StringComparer.Ordinal);

        if (hasRole && hasScope)
        {
            context.Succeed(requirement);
        }
        else
        {
            // entra_auth.py raises EntraForbidden here -- a structurally valid, correctly-signed,
            // right-tenant/audience token that is simply missing the required role or scope (or
            // is an app-only/client-credentials token with no scp claim at all) is a 403, not a
            // 401: the bearer doesn't need a *different* token, it needs different permissions.
            context.Fail();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// entra_auth.py's `_is_anonymous` for the persona-asset route specifically: anonymous iff
    /// the route is PersonaRoutes.PersonaAssetRouteName AND the requested asset path's lowercased
    /// extension is in <see cref="EntraAuthentication.AnonymousAssetExtensions"/>. Checked against
    /// the raw requested `assetPath` route value (matching Python's check against
    /// `request.match_info["asset_path"]` before any on-disk resolution), not the resolved file
    /// path -- an unresolvable/404 asset path still gets this same anonymity determination first.
    /// </summary>
    private static bool IsAnonymousAssetRequest(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();
        var routeName = endpoint?.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName;
        if (!string.Equals(routeName, PersonaRoutes.PersonaAssetRouteName, StringComparison.Ordinal))
        {
            return false;
        }

        var assetPath = httpContext.Request.RouteValues.TryGetValue("assetPath", out var value) ? value as string : null;
        if (string.IsNullOrEmpty(assetPath))
        {
            return false;
        }

        var extension = GetPythonStyleSuffix(assetPath);
        return EntraAuthentication.AnonymousAssetExtensions.Contains(extension);
    }

    /// <summary>
    /// Python's `PurePosixPath(asset_path).suffix.lower()` -- NOT .NET's `Path.GetExtension`,
    /// which has neither of PurePosixPath's two special cases. #223 (Rick's review of PR #225,
    /// mirrored here for #147 parity): a dotfile name like ".png" (or "demo/.png") has NO
    /// extension in Python -- a leading dot is its "hidden file" marker, not an extension
    /// delimiter -- so `_is_anonymous(".png")` is False (such an asset is NOT anonymous, and
    /// would also fail `persona_loader._validate_persona_assets`'s startup classification as an
    /// unrecognized file type, issue #144 -- not ported to C#, out of #147's scope, but this is
    /// the auth-time half of the same algorithm). `Path.GetExtension(".png")` instead returns
    /// ".png", which would have incorrectly treated such a file as anonymous here. A trailing dot
    /// (e.g. "foo.") is likewise not an extension delimiter in Python. CPython's actual
    /// PurePosixPath.suffix is equivalent to:
    /// `i = name.rfind('.'); return name[i:] if 0 &lt; i &lt; len(name) - 1 else ''` -- only the
    /// final path segment (name) is considered (so "demo/.png" reduces to ".png" first, same as
    /// "logo.tar.gz" reduces to only its last dot-suffix, ".gz", not ".tar.gz").
    /// </summary>
    private static string GetPythonStyleSuffix(string assetPath)
    {
        var lastSlash = assetPath.LastIndexOf('/');
        var name = lastSlash >= 0 ? assetPath[(lastSlash + 1)..] : assetPath;
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= name.Length - 1)
        {
            return string.Empty;
        }
        return name[dotIndex..].ToLowerInvariant();
    }
}
