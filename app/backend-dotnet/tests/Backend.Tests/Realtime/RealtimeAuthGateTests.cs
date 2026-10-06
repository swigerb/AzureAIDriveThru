using Backend.Auth;
using Backend.Configuration;
using Backend.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Realtime;

/// <summary>Unit tests for the `/realtime` pre-upgrade auth gate (PR #96 review, required item
/// 1): Origin validation, then (only when config.yaml's security.require_session_token is true)
/// HMAC session-token validation -- same order and rejection statuses as
/// app/backend/rtmt.py's `_websocket_handler`. Kept as pure-function tests (like
/// Health/HealthEndpointTests.cs), with the conformance suite's Scenarios/Http and
/// Scenarios/Security OriginValidationTests proving the same behaviour over the real wire against
/// this backend.</summary>
public sealed class RealtimeAuthGateTests
{
    private static readonly byte[] Secret = "test-secret-at-least-32-bytes-long!"u8.ToArray();
    private const string Host = "example.com";

    [Fact]
    public void Check_NoOrigin_TokenNotRequired_Allows()
    {
        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: null,
            security: NoAuthRequired(), tokenService: NewTokenService(), NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Check_MatchingOrigin_Allows()
    {
        var result = RealtimeAuthGate.Check(
            origin: $"https://{Host}", host: Host, token: null,
            security: NoAuthRequired(), tokenService: NewTokenService(), NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_MismatchedOrigin_Returns403WithPythonBody()
    {
        var result = RealtimeAuthGate.Check(
            origin: "https://attacker.example", host: Host, token: null,
            security: NoAuthRequired(), tokenService: NewTokenService(), NullLogger.Instance);

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status403Forbidden, "Origin not allowed");
    }

    [Fact]
    public void Check_MismatchedOrigin_ButExplicitlyAllowlisted_Allows()
    {
        var security = SecurityConfigFor(allowedOrigins: ["https://partner.example"], requireSessionToken: false);

        var result = RealtimeAuthGate.Check(
            origin: "https://partner.example", host: Host, token: null,
            security, tokenService: NewTokenService(), NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Check_TokenNotRequired_MissingToken_StillAllows()
    {
        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: null,
            security: NoAuthRequired(), tokenService: NewTokenService(), NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_TokenRequired_MissingToken_Returns401WithPythonBody()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: true);

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: null,
            security, tokenService: NewTokenService(), NullLogger.Instance);

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status401Unauthorized, "Invalid or expired token");
    }

    [Fact]
    public void Check_TokenRequired_ValidToken_Allows()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: true);
        var tokenService = NewTokenService();
        var token = tokenService.Create();

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_TokenRequired_InvalidToken_Returns401()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: true);

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: "not-a-real-token",
            security, tokenService: NewTokenService(), NullLogger.Instance);

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status401Unauthorized, "Invalid or expired token");
    }

    [Fact]
    public async Task Check_OriginRejectionTakesPrecedenceOverTokenCheck()
    {
        // rtmt.py checks Origin before the token ("Task 3" before "Task 4") -- a request that
        // fails both must get the Origin rejection, not the token rejection.
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: true);

        var result = RealtimeAuthGate.Check(
            origin: "https://attacker.example", host: Host, token: null,
            security, tokenService: NewTokenService(), NullLogger.Instance);

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status403Forbidden, "Origin not allowed");
    }

    // ── Issue #147 (ADR-002): entra_mode forces the session-token check on, and additionally
    // binds the token's oid to the Entra-validated principal's own oid. ─────────────────────────

    [Fact]
    public void Check_EntraMode_SessionTokenForced_EvenWhenConfigDisablesIt()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: null,
            security, tokenService: NewTokenService(), NullLogger.Instance, entraMode: true, principalOid: "oid-1");

        Assert.NotNull(result);
    }

    [Fact]
    public void Check_EntraMode_MatchingOid_Allows()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);
        var tokenService = NewTokenService();
        var token = tokenService.Create(oid: "33333333-3333-3333-3333-333333333333");

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance,
            entraMode: true, principalOid: "33333333-3333-3333-3333-333333333333");

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_EntraMode_MismatchedOid_Returns401()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);
        var tokenService = NewTokenService();
        var token = tokenService.Create(oid: "33333333-3333-3333-3333-333333333333");

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance,
            entraMode: true, principalOid: "44444444-4444-4444-4444-444444444444");

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status401Unauthorized, "Invalid or expired token");
    }

    [Fact]
    public async Task Check_EntraMode_TokenWithNoOid_Returns401()
    {
        // A token minted without an oid (e.g. pre-#147) must not satisfy the Entra-mode binding
        // check even though it is otherwise structurally valid.
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);
        var tokenService = NewTokenService();
        var token = tokenService.Create();

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance,
            entraMode: true, principalOid: "33333333-3333-3333-3333-333333333333");

        Assert.NotNull(result);
        await AssertResponse(result!, StatusCodes.Status401Unauthorized, "Invalid or expired token");
    }

    [Fact]
    public void Check_DevelopmentMode_OidBindingNotEnforced()
    {
        // entraMode defaults to false (Development pass-through) -- the oid check must not run
        // even if a principalOid happened to be passed in.
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);
        var tokenService = NewTokenService();
        var token = tokenService.Create();

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance, entraMode: false, principalOid: null);

        Assert.Null(result);
    }

    [Fact]
    public async Task Check_TokenRequired_MissingToken_SetsWwwAuthenticateBearerHeader()
    {
        // The pre-#147 implementation was missing this header on both the missing-token and
        // oid-mismatch 401s; rtmt.py's _websocket_handler sets it on every 401 this gate raises.
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: true);

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token: null,
            security, tokenService: NewTokenService(), NullLogger.Instance);

        Assert.NotNull(result);
        await AssertWwwAuthenticateBearer(result!);
    }

    [Fact]
    public async Task Check_EntraMode_MismatchedOid_SetsWwwAuthenticateBearerHeader()
    {
        var security = SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);
        var tokenService = NewTokenService();
        var token = tokenService.Create(oid: "33333333-3333-3333-3333-333333333333");

        var result = RealtimeAuthGate.Check(
            origin: string.Empty, host: Host, token,
            security, tokenService, NullLogger.Instance,
            entraMode: true, principalOid: "44444444-4444-4444-4444-444444444444");

        Assert.NotNull(result);
        await AssertWwwAuthenticateBearer(result!);
    }

    private static SessionTokenService NewTokenService() => new(Secret);

    private static SecurityConfig NoAuthRequired() => SecurityConfigFor(allowedOrigins: [], requireSessionToken: false);

    private static SecurityConfig SecurityConfigFor(IReadOnlyList<string> allowedOrigins, bool requireSessionToken)
    {
        var originsYaml = allowedOrigins.Count == 0
            ? "  allowed_origins: []\n"
            : "  allowed_origins:\n" + string.Join(string.Empty, allowedOrigins.Select(o => $"    - {o}\n"));

        var yaml =
            "model:\n  foo: bar\n" +
            "business_rules:\n  foo: bar\n" +
            "cache:\n  foo: bar\n" +
            "audio:\n  foo: bar\n" +
            "connection:\n  foo: bar\n" +
            "security:\n" +
            originsYaml +
            $"  require_session_token: {(requireSessionToken ? "true" : "false")}\n";

        var path = Path.Combine(Path.GetTempPath(), "squanchy-realtime-auth-gate-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return SecurityConfig.FromConfig(AppConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task AssertResponse(IResult result, int expectedStatusCode, string expectedBody)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal(expectedBody, await reader.ReadToEndAsync());
    }

    private static async Task AssertWwwAuthenticateBearer(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
    }
}
