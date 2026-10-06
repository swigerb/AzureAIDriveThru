namespace Backend.Configuration;

/// <summary>
/// Typed view of config.yaml's `security` section -- specifically the two fields the `/realtime`
/// pre-upgrade auth gate needs (`Realtime/RealtimeAuthGate.cs`), matching app/backend/rtmt.py's
/// module-level `_security_cfg = _config.get("security", {})` (docs/dotnet_mapping.md). Issue #15
/// moved `idle_timeout_seconds` into <see cref="SessionsConfig"/> instead (it belongs with the
/// rest of the session-lifecycle timers, not this request-gate-scoped view). The remaining
/// `security.*` keys (`max_concurrent_sessions`, `allow_client_log_control`) still aren't consumed
/// by anything, so they stay out of this typed view rather than guessed at ahead of the wave that
/// needs them.
///
/// YamlDotNet's untyped `Deserialize&lt;object?&gt;()` returns every scalar as a plain string (see
/// `Prompts/PromptLoader.cs`'s `ParsePriority` comment for the same gotcha) -- so
/// `require_session_token: false` in config.yaml comes back as the string "False"/"false", not the
/// bool `false`. <see cref="FromConfig"/> tolerates both shapes for the same reason ParsePriority
/// does.
/// </summary>
internal sealed class SecurityConfig
{
    private SecurityConfig(IReadOnlyList<string> allowedOrigins, bool requireSessionToken)
    {
        AllowedOrigins = allowedOrigins;
        RequireSessionToken = requireSessionToken;
    }

    /// <summary>config.yaml's `security.allowed_origins` -- exact Origin strings allowed even when
    /// they don't match the request's Host header. Empty by default (same-origin only), matching
    /// rtmt.py's `_security_cfg.get("allowed_origins", [])`.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; }

    /// <summary>config.yaml's `security.require_session_token`. False by default, matching
    /// rtmt.py's `_security_cfg.get("require_session_token", False)`.</summary>
    public bool RequireSessionToken { get; }

    public static SecurityConfig FromConfig(AppConfig config)
    {
        var section = config.TryGetSection("security");
        if (section is null)
        {
            return new SecurityConfig(Array.Empty<string>(), requireSessionToken: false);
        }

        var allowedOrigins = section.TryGetValue("allowed_origins", out var originsRaw) && originsRaw is IEnumerable<object> origins
            ? origins.Select(o => o?.ToString() ?? string.Empty).ToList()
            : new List<string>();

        var requireSessionToken = section.TryGetValue("require_session_token", out var requireRaw) && ParseBool(requireRaw);

        return new SecurityConfig(allowedOrigins, requireSessionToken);
    }

    private static bool ParseBool(object? value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => false,
    };
}
