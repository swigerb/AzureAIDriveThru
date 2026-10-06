using Backend.Auth;
using Backend.Configuration;

namespace Backend.Realtime;

/// <summary>
/// Pre-upgrade auth gate for the `/realtime` WebSocket route: Origin validation, then HMAC
/// session-token validation (forced on in Entra mode; otherwise only when config.yaml's
/// `security.require_session_token` is true), then (Entra mode only) binding that session
/// token's `oid` to the Entra-validated principal's own `oid`. Ports app/backend/rtmt.py's
/// `_websocket_handler` "Task 3" / "Task 4" checks plus issue #147's entra_mode additions
/// byte-for-byte -- same order, same rejection status codes, same response body text
/// (docs/dotnet_mapping.md).
///
/// Kept as a pure, directly-unit-testable function -- like `Health/HealthEndpoint.cs`'s
/// `Handle` -- so the logic can be proven without spinning up a real Kestrel host. The
/// conformance suite's `Scenarios/Http/OriginValidationTests.cs` and
/// `Scenarios/Security/OriginValidationTests.cs` prove it again over the real wire against this
/// backend (`CONFORMANCE_BACKEND=dotnet`), as does `Scenarios/Auth/AuthRowSpecialCaseTests.cs`
/// for the entra_mode/oid-binding additions.
///
/// swigerb/AzureAIDriveThru#12, PR #96 review (Rick, required item 1): the C# `/realtime` route
/// previously accepted any WebSocket unconditionally. Chose to enforce here (rather than leave
/// `/realtime` unmapped until #13) because the conformance suite already has real, over-the-wire
/// Origin-rejection scenarios that can prove parity against this backend today; the token check
/// is ported alongside it because `SessionTokenService` already exists and the config-gated
/// behaviour (default `require_session_token: false`, matching Python) is only a few lines.
///
/// Note this runs entirely INSIDE the `/realtime` endpoint's own handler body, after ASP.NET
/// Core's authentication/authorization middleware has already run for the request (issue #147's
/// JwtBearer fallback policy, Program.cs) -- that ordering is what makes a bad-Origin, no-token
/// request 401 (the Entra check rejects first) rather than 403 (this gate's own Origin check),
/// matching 18.11 row 11.
/// </summary>
internal static class RealtimeAuthGate
{
    /// <summary>Returns a rejection <see cref="IResult"/> (403/401) if the request must be
    /// rejected before <c>AcceptWebSocketAsync</c>, or null if the request may proceed.</summary>
    public static IResult? Check(
        string origin,
        string host,
        string? token,
        SecurityConfig security,
        SessionTokenService tokenService,
        ILogger logger,
        bool entraMode = false,
        string? principalOid = null)
    {
        // Issue #147, persona-architecture.md 18.11 row 14: an access-log-shaped line for every
        // /realtime handshake attempt (accepted or rejected), mirroring aiohttp's own access
        // logger wrapping app.py's whole request pipeline -- that line is this row's own positive
        // control proving the leak checks below aren't vacuously passing because nothing was
        // logged at all. Deliberately logs only Origin/Host, never `token`/`principalOid`.
        logger.LogInformation("Realtime handshake: GET /realtime (host={Host}, origin={Origin})", host, origin);

        // ── Origin validation ("Task 3") -- missing/empty Origin is accepted unchanged
        // (non-browser and same-process callers legitimately omit it); this only hardens the
        // case where an Origin *is* present but doesn't match. ──
        if (!string.IsNullOrEmpty(origin)
            && !OriginValidator.MatchesHost(origin, host)
            && !security.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            logger.LogWarning("Rejected WebSocket from disallowed origin: host={Host} origin={Origin}", host, origin);
            return Results.Text("Origin not allowed", statusCode: StatusCodes.Status403Forbidden);
        }

        // ── HMAC session token validation ("Task 4"; issue #144/#147 design doc 18.3) -- in
        // Entra mode, require_session_token is FORCED on (config.yaml cannot turn it off), and
        // the token must carry the SAME oid as the Entra principal the JwtBearer fallback policy
        // already validated upstream. In Development pass-through, config.yaml's
        // `security.require_session_token` still governs this unchanged. ──
        if (entraMode || security.RequireSessionToken)
        {
            if (!tokenService.TryValidate(token ?? string.Empty, out var tokenOid))
            {
                logger.LogWarning("Rejected WebSocket with invalid/expired session token");
                return UnauthorizedWithBearerChallenge("Invalid or expired token");
            }

            if (entraMode && (string.IsNullOrEmpty(tokenOid) || !string.Equals(tokenOid, principalOid, StringComparison.Ordinal)))
            {
                logger.LogWarning("Rejected WebSocket: session token oid does not match Entra principal");
                return UnauthorizedWithBearerChallenge("Invalid or expired token");
            }
        }

        return null;
    }

    /// <summary>
    /// rtmt.py's 401s for this gate all carry `headers={"WWW-Authenticate": "Bearer"}` alongside
    /// the plain-text body -- <c>Results.Text</c> has no headers overload, so this is a small
    /// custom <see cref="IResult"/> rather than a library helper.
    /// </summary>
    private static IResult UnauthorizedWithBearerChallenge(string text) => new BearerChallengeTextResult(text);

    private sealed class BearerChallengeTextResult(string text) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
            httpContext.Response.ContentType = "text/plain; charset=utf-8";
            return httpContext.Response.WriteAsync(text);
        }
    }
}
