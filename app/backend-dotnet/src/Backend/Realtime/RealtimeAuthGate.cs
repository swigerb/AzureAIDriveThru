using Backend.Auth;
using Backend.Configuration;

namespace Backend.Realtime;

/// <summary>
/// Pre-upgrade auth gate for the `/realtime` WebSocket route: Origin validation, then (only when
/// config.yaml's `security.require_session_token` is true) HMAC session-token validation. Ports
/// app/backend/rtmt.py's `_websocket_handler` "Task 3" / "Task 4" checks byte-for-byte -- same
/// order, same rejection status codes, same response body text (docs/dotnet_mapping.md).
///
/// Kept as a pure, directly-unit-testable function -- like `Health/HealthEndpoint.cs`'s
/// `Handle` -- so the logic can be proven without spinning up a real Kestrel host. The
/// conformance suite's `Scenarios/Http/OriginValidationTests.cs` and
/// `Scenarios/Security/OriginValidationTests.cs` prove it again over the real wire against this
/// backend (`CONFORMANCE_BACKEND=dotnet`).
///
/// swigerb/AzureAIDriveThru#12, PR #96 review (Rick, required item 1): the C# `/realtime` route
/// previously accepted any WebSocket unconditionally. Chose to enforce here (rather than leave
/// `/realtime` unmapped until #13) because the conformance suite already has real, over-the-wire
/// Origin-rejection scenarios that can prove parity against this backend today; the token check
/// is ported alongside it because `SessionTokenService` already exists and the config-gated
/// behaviour (default `require_session_token: false`, matching Python) is only a few lines.
/// </summary>
public static class RealtimeAuthGate
{
    /// <summary>Returns a rejection <see cref="IResult"/> (403/401) if the request must be
    /// rejected before <c>AcceptWebSocketAsync</c>, or null if the request may proceed.</summary>
    public static IResult? Check(
        string origin, string host, string? token, SecurityConfig security, SessionTokenService tokenService, ILogger logger)
    {
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

        // ── HMAC session token validation ("Task 4") -- only when config.yaml opts in. ──
        if (security.RequireSessionToken && !tokenService.Validate(token ?? string.Empty))
        {
            logger.LogWarning("Rejected WebSocket with invalid/expired session token");
            return Results.Text("Invalid or expired token", statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }
}
