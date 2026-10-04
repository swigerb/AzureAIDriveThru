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
    /// set. Unity is concurrently finalizing this exact rule's final shape in issue #163; this
    /// matches current Python behaviour today and must track #163's resolution (see PR notes).
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
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(options, settings));

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
    /// </summary>
    public static void ConfigureJwtBearer(JwtBearerOptions options, EntraSettings settings)
    {
        options.Authority = settings.Issuer;
        options.RequireHttpsMetadata = settings.Instance.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
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
            // when that header is absent AND the path is exactly /realtime.
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

        var scopeClaim = context.User.FindFirst("scp")?.Value;
        var hasScope = scopeClaim is not null &&
            scopeClaim.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(requirement.ApiScope, StringComparer.Ordinal);

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

        var extension = Path.GetExtension(assetPath).ToLowerInvariant();
        return EntraAuthentication.AnonymousAssetExtensions.Contains(extension);
    }
}
