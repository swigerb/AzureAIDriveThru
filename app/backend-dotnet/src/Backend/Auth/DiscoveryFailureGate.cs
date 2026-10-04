namespace Backend.Auth;

/// <summary>
/// Tracks OIDC discovery/JWKS fetch failures and applies a short cooldown so a downed or
/// unreachable identity provider doesn't cause every single incoming request to re-attempt (and
/// re-timeout on) the same doomed network call. #223 item 1 (Rick's review of PR #225, mirrored
/// here for #147 parity): entra_auth.py's planned negative-discovery-cache cooldown is 30 seconds
/// -- a request arriving while the gate is in cooldown fails closed immediately (401, via
/// EntraAuthentication.ConfigureJwtBearer's OnMessageReceived) without ever calling
/// GetConfigurationAsync() again. One instance is shared across every request on the JwtBearer
/// scheme for the lifetime of the app (registered as a DI singleton by AddEntraAuthentication).
/// #246 mirror-check (Summer's Python JWKS-cooldown-race fix, coordinator's follow-up question on
/// PR #226): OnMessageReceived only honours <see cref="IsInCooldown"/> when there is no
/// already-warm, usable Last-Known-Good configuration to fall back on -- see
/// EntraAuthentication.HasUsableLastKnownGoodConfiguration's doc comment for the full rationale;
/// this gate itself needed no change, only its caller's use of it.
/// <see cref="TimeProvider"/> is injectable purely for deterministic unit testing of the cooldown
/// window; production code always uses <see cref="TimeProvider.System"/> (the implicit default).
/// </summary>
public sealed class DiscoveryFailureGate(TimeProvider? timeProvider = null, TimeSpan? cooldown = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    private readonly Lock _lock = new();
    private DateTimeOffset? _lastFailureUtc;

    /// <summary>Call when an OIDC discovery/JWKS fetch has just failed, starting (or restarting)
    /// the cooldown window from now.</summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _lastFailureUtc = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>True if a discovery/JWKS failure was recorded within the cooldown window and a
    /// new fetch attempt should be skipped (fail closed instead).</summary>
    public bool IsInCooldown()
    {
        lock (_lock)
        {
            return _lastFailureUtc is { } last && _timeProvider.GetUtcNow() - last < _cooldown;
        }
    }
}
