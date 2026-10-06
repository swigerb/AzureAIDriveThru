using System.Globalization;

namespace Backend.Configuration;

/// <summary>
/// Port of app/backend/session_manager.py's module-level config reads -- config.yaml's
/// `security.idle_timeout_seconds` plus the whole `resume.*` section (issue #15). Every timer
/// field is overridable via <see cref="ConformanceHooks.Seconds"/> the same way
/// <see cref="RateLimitSettings"/>'s two retry delays are, so the conformance harness's
/// `BackendProfile`-driven env vars (`CONFORMANCE_IDLE_TIMEOUT_SECONDS`,
/// `CONFORMANCE_GRACE_SECONDS`, `CONFORMANCE_NUDGE_AFTER_SECONDS`,
/// `CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS`, `CONFORMANCE_SWEEP_INTERVAL_SECONDS`) drive this
/// backend's timers identically to the Python one under test.
/// </summary>
internal sealed class SessionsConfig
{
    public double IdleTimeoutSeconds { get; }
    public bool ResumeEnabled { get; }
    public double GraceSeconds { get; }
    public int MaxDetached { get; }
    public int HistoryTurns { get; }
    public int HistoryChars { get; }
    public double NudgeAfterSeconds { get; }
    public double FirstFrameTimeoutSeconds { get; }
    public double SweepIntervalSeconds { get; }

    /// <summary>Issue #13 tail: config.yaml's `context` section (session_manager.py's
    /// `_CTX_MAX_TOKENS`/`_CTX_WARNING_PCT`/`_CTX_CRITICAL_PCT` module-level reads), threaded
    /// into every <see cref="Sessions.ContextMonitor"/> this manager creates.</summary>
    public int ContextMaxTokens { get; }
    public double ContextWarningThresholdPct { get; }
    public double ContextCriticalThresholdPct { get; }

    public SessionsConfig(
        double idleTimeoutSeconds = 300,
        bool resumeEnabled = true,
        double graceSeconds = 120,
        int maxDetached = 20,
        int historyTurns = 6,
        int historyChars = 2000,
        double nudgeAfterSeconds = 30,
        double firstFrameTimeoutSeconds = 2.0,
        double sweepIntervalSeconds = 15,
        int contextMaxTokens = 128_000,
        double contextWarningThresholdPct = 80,
        double contextCriticalThresholdPct = 95)
    {
        IdleTimeoutSeconds = idleTimeoutSeconds;
        ResumeEnabled = resumeEnabled;
        GraceSeconds = graceSeconds;
        MaxDetached = maxDetached;
        HistoryTurns = historyTurns;
        HistoryChars = historyChars;
        NudgeAfterSeconds = nudgeAfterSeconds;
        FirstFrameTimeoutSeconds = firstFrameTimeoutSeconds;
        SweepIntervalSeconds = sweepIntervalSeconds;
        ContextMaxTokens = contextMaxTokens;
        ContextWarningThresholdPct = contextWarningThresholdPct;
        ContextCriticalThresholdPct = contextCriticalThresholdPct;
    }

    public static SessionsConfig FromConfig(AppConfig config)
    {
        var security = config.TryGetSection("security");
        var resume = config.TryGetSection("resume");
        var context = config.TryGetSection("context");

        return new SessionsConfig(
            idleTimeoutSeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_IDLE_TIMEOUT_SECONDS", GetDouble(security, "idle_timeout_seconds", 300)),
            resumeEnabled: GetBool(resume, "enabled", true),
            graceSeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_GRACE_SECONDS", GetDouble(resume, "grace_seconds", 120)),
            maxDetached: GetInt(resume, "max_detached", 20),
            historyTurns: GetInt(resume, "history_turns", 6),
            historyChars: GetInt(resume, "history_chars", 2000),
            nudgeAfterSeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_NUDGE_AFTER_SECONDS", GetDouble(resume, "nudge_after_seconds", 30)),
            firstFrameTimeoutSeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS", GetDouble(resume, "first_frame_timeout_seconds", 2.0)),
            sweepIntervalSeconds: ConformanceHooks.Seconds(
                "CONFORMANCE_SWEEP_INTERVAL_SECONDS", GetDouble(resume, "sweep_interval_seconds", 15)),
            contextMaxTokens: GetInt(context, "max_tokens", 128_000),
            contextWarningThresholdPct: GetDouble(context, "warning_threshold_pct", 80),
            contextCriticalThresholdPct: GetDouble(context, "critical_threshold_pct", 95));
    }

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback) =>
        section is not null && section.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out var value)
            ? value
            : fallback;

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback) =>
        section is not null && section.TryGetValue(key, out var raw) &&
        double.TryParse(raw?.ToString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(IDictionary<object, object>? section, string key, bool fallback)
    {
        if (section is null || !section.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }
        if (raw is bool direct)
        {
            return direct;
        }
        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }
}
