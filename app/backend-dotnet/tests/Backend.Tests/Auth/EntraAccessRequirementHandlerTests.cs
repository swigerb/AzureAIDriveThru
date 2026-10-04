using System.Security.Claims;
using Backend.Auth;
using Backend.Personas;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Tests.Auth;

/// <summary>
/// Unit tests for EntraAccessRequirementHandler (issue #147, ADR-002) -- the authorization half of
/// entra_auth.py's deny-by-default middleware, ported as the single requirement every non-exempt
/// route falls under via AddAuthorization's FallbackPolicy/DefaultPolicy. These exercise the
/// handler directly against a hand-built AuthorizationHandlerContext, matching the exact
/// claim/role/scope shapes FakeEntraIssuer.Mint() produces (tests/conformance's
/// Conformance.Fakes/FakeEntraIssuer.cs) without needing a live token or HTTP server -- the
/// over-the-wire proof of the same behavior lives in tests/conformance's Scenarios/Auth suite.
/// </summary>
public sealed class EntraAccessRequirementHandlerTests
{
    private const string AppRole = "DriveThru.User";
    private const string ApiScope = "access_as_user";

    private static EntraAccessRequirementHandler Handler => new();

    private static EntraAccessRequirement Requirement => new(AppRole, ApiScope);

    private static ClaimsPrincipal AuthenticatedPrincipal(string? role = AppRole, string? scope = ApiScope)
    {
        var claims = new List<Claim>
        {
            new("oid", "33333333-3333-3333-3333-333333333333"),
            new("tid", "11111111-1111-1111-1111-111111111111"),
        };
        if (role is not null)
        {
            claims.Add(new Claim("roles", role));
        }
        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }
        var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static HttpContext RequestFor(string? routeName, string? assetPath = null)
    {
        var httpContext = new DefaultHttpContext();
        if (routeName is not null)
        {
            var metadata = new EndpointMetadataCollection(new RouteNameMetadata(routeName));
            httpContext.SetEndpoint(new Endpoint(requestDelegate: null, metadata, displayName: routeName));
        }
        if (assetPath is not null)
        {
            httpContext.Request.RouteValues["assetPath"] = assetPath;
        }
        return httpContext;
    }

    private static async Task<AuthorizationResult> EvaluateAsync(ClaimsPrincipal user, HttpContext resource)
    {
        var context = new AuthorizationHandlerContext([Requirement], user, resource);
        await Handler.HandleAsync(context);
        return context.HasSucceeded ? AuthorizationResult.Succeeded
            : context.HasFailed ? AuthorizationResult.Failed
            : AuthorizationResult.Pending;
    }

    private enum AuthorizationResult { Succeeded, Failed, Pending }

    [Fact]
    public async Task ValidRoleAndScope_Succeeds()
    {
        var result = await EvaluateAsync(AuthenticatedPrincipal(), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Succeeded, result);
    }

    [Fact]
    public async Task MissingRole_Fails()
    {
        var result = await EvaluateAsync(AuthenticatedPrincipal(role: null), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task WrongRole_Fails()
    {
        var result = await EvaluateAsync(AuthenticatedPrincipal(role: "SomeOther.Role"), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task SubstringRole_IsNotAccepted()
    {
        // Issue #147's exact-match requirement -- "DriveThru.UserX" must NOT satisfy a
        // "DriveThru.User" role requirement (conformance AuthRowCases row 4c).
        var result = await EvaluateAsync(AuthenticatedPrincipal(role: "DriveThru.UserX"), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task MissingScope_Fails()
    {
        var result = await EvaluateAsync(AuthenticatedPrincipal(scope: null), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task WrongScope_Fails()
    {
        var result = await EvaluateAsync(AuthenticatedPrincipal(scope: "some.other.scope"), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task SubstringScope_IsNotAccepted()
    {
        // Conformance AuthRowCases row 5c: "access_as_user_admin" must NOT satisfy
        // "access_as_user" (exact, whitespace-delimited match only).
        var result = await EvaluateAsync(AuthenticatedPrincipal(scope: "access_as_user_admin"), RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task MultiScopeClaim_MatchesByWhitespaceSplit()
    {
        var result = await EvaluateAsync(
            AuthenticatedPrincipal(scope: "some.other.scope access_as_user another.scope"),
            RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Succeeded, result);
    }

    [Fact]
    public async Task MultipleScpClaims_TreatedAsNonStringScope_Fails()
    {
        // #163 N2 (Python follow-up, mirrored for #147 parity): entra_auth.py guards
        // `scopes = scp.split() if isinstance(scp, str) else []` because a JSON-array-shaped
        // `scp` claim previously crashed with AttributeError (500). .NET's JWT handler would
        // materialize such an array as multiple separate Claim("scp", ...) entries rather than
        // one non-string value, so the exact Python crash can't occur -- but we still must not
        // silently fall back to just the first claim (FindFirst) and ignore the rest, which could
        // accidentally grant or deny based on claim ordering. Multiple "scp" claims, even if one
        // of them exactly matches the required scope, must fail closed (403), not succeed.
        var claims = new List<Claim>
        {
            new("oid", "33333333-3333-3333-3333-333333333333"),
            new("tid", "11111111-1111-1111-1111-111111111111"),
            new("roles", AppRole),
            new("scp", ApiScope),
            new("scp", "some.other.scope"),
        };
        var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var result = await EvaluateAsync(principal, RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Failed, result);
    }

    [Fact]
    public async Task Unauthenticated_NonAnonymousRoute_StaysPending()
    {
        // No Succeed/Fail: ASP.NET Core's PolicyEvaluator resolves an unauthenticated pending
        // result as a Challenge (401), matching entra_auth.py's EntraUnauthorized path.
        var anonymousIdentity = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await EvaluateAsync(anonymousIdentity, RequestFor("some-protected-route"));

        Assert.Equal(AuthorizationResult.Pending, result);
    }

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("icon.PNG")]
    [InlineData("sound.wav")]
    [InlineData("jingle.mp3")]
    [InlineData("photo.jpg")]
    [InlineData("banner.webp")]
    [InlineData("favicon.ico")]
    public async Task PersonaAsset_AnonymousExtension_Succeeds_EvenUnauthenticated(string assetPath)
    {
        var anonymousIdentity = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await EvaluateAsync(
            anonymousIdentity, RequestFor(PersonaRoutes.PersonaAssetRouteName, assetPath));

        Assert.Equal(AuthorizationResult.Succeeded, result);
    }

    [Fact]
    public async Task PersonaAsset_ProtectedExtension_StaysPending_WhenUnauthenticated()
    {
        // "demo/dummyOrder.json" stays protected even under the persona-asset route, since
        // ".json" isn't in the anonymous extension set (issue #147 bullet 3; tracks #163).
        var anonymousIdentity = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await EvaluateAsync(
            anonymousIdentity, RequestFor(PersonaRoutes.PersonaAssetRouteName, "demo/dummyOrder.json"));

        Assert.Equal(AuthorizationResult.Pending, result);
    }

    [Fact]
    public async Task PersonaAsset_ProtectedExtension_Authenticated_StillRequiresRoleAndScope()
    {
        var result = await EvaluateAsync(
            AuthenticatedPrincipal(), RequestFor(PersonaRoutes.PersonaAssetRouteName, "demo/dummyOrder.json"));

        Assert.Equal(AuthorizationResult.Succeeded, result);
    }

    [Fact]
    public async Task PersonaAsset_AnonymousExtension_IsCaseInsensitive()
    {
        var anonymousIdentity = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await EvaluateAsync(
            anonymousIdentity, RequestFor(PersonaRoutes.PersonaAssetRouteName, "LOGO.SVG"));

        Assert.Equal(AuthorizationResult.Succeeded, result);
    }

    [Fact]
    public async Task OtherRouteName_IgnoresAssetPath_NeverAnonymous()
    {
        // Only the persona-asset route name gets the extension-based anonymous carve-out --
        // an unrelated route with a coincidentally-matching "assetPath" route value must not.
        var anonymousIdentity = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await EvaluateAsync(anonymousIdentity, RequestFor("persona-detail", "logo.svg"));

        Assert.Equal(AuthorizationResult.Pending, result);
    }
}

/// <summary>
/// Pure-configuration assertions for ConfigureJwtBearer/BuildAccessPolicy (issue #147) -- no live
/// HTTP/token validation here (that's tests/conformance's job against FakeEntraIssuer); these
/// guard the options shape itself (Authority, RequireHttpsMetadata, ValidAudiences, algorithms).
/// </summary>
public sealed class ConfigureJwtBearerTests
{
    private static EntraSettings EntraModeSettings(string instance = "https://login.microsoftonline.com/") =>
        new(
            EntraMode.Entra,
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "access_as_user",
            "DriveThru.User",
            instance);

    [Fact]
    public void SetsAuthorityToSettingsIssuer()
    {
        var options = new JwtBearerOptions();
        var settings = EntraModeSettings();

        EntraAuthentication.ConfigureJwtBearer(options, settings);

        Assert.Equal(settings.Issuer, options.Authority);
    }

    [Fact]
    public void RequiresHttpsMetadata_ForHttpsInstance()
    {
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings("https://login.microsoftonline.com/"));

        Assert.True(options.RequireHttpsMetadata);
    }

    [Fact]
    public void AllowsHttpMetadata_ForLoopbackInstance()
    {
        // The conformance harness's FakeEntraIssuer serves plain HTTP on 127.0.0.1 (#143) --
        // RequireHttpsMetadata must be false there or discovery will be refused outright.
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings("http://127.0.0.1:5123/"));

        Assert.False(options.RequireHttpsMetadata);
    }

    [Fact]
    public void ValidAudiences_IncludesBareClientIdAndAppIdUri()
    {
        var options = new JwtBearerOptions();
        var settings = EntraModeSettings();

        EntraAuthentication.ConfigureJwtBearer(options, settings);

        Assert.Contains(settings.ClientId, options.TokenValidationParameters.ValidAudiences);
        Assert.Contains($"api://{settings.ClientId}", options.TokenValidationParameters.ValidAudiences);
    }

    [Fact]
    public void ValidAlgorithms_IsRs256Only()
    {
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings());

        Assert.Equal([SecurityAlgorithms.RsaSha256], options.TokenValidationParameters.ValidAlgorithms);
    }

    [Fact]
    public void MapInboundClaims_IsDisabled()
    {
        // entra_auth.py's TokenValidator reads raw Entra claim names (tid, oid, roles, scp) --
        // the legacy ClaimTypes.* remap must be off so those survive unchanged.
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings());

        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public void RequiresExpirationTime()
    {
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings());

        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
    }

    [Fact]
    public void ClockSkew_IsFiveMinutes()
    {
        // entra_auth.py's TokenValidator._validate_sync: `leeway=300`.
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings());

        Assert.Equal(TimeSpan.FromMinutes(5), options.TokenValidationParameters.ClockSkew);
    }

    [Fact]
    public void BackchannelTimeout_IsTenSeconds()
    {
        // #163 N1a (Python follow-up, mirrored for #147 parity): entra_auth.py's TokenValidator
        // passes this same 10s default to jwt.PyJWKClient's `timeout=` (PyJWT's own default is
        // 30s) so a hung Entra discovery/JWKS endpoint can't stall a request indefinitely.
        var options = new JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, EntraModeSettings());

        Assert.Equal(TimeSpan.FromSeconds(10), options.BackchannelTimeout);
    }
}
