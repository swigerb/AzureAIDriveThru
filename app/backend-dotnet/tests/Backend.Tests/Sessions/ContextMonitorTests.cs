using Microsoft.Extensions.Logging;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #13 tail: port of app/backend/tests/test_rtmt.py's <c>ContextMonitorTests</c> --
/// verifies <see cref="Backend.Sessions.ContextMonitor"/>'s token estimation (char-count / 4) and
/// warning/critical threshold logging match Python's own <c>ContextMonitor</c> exactly. Python's
/// <c>test_warning_threshold_logged</c> patches the module-level <c>_CTX_MAX_TOKENS</c> default to
/// 100 before constructing <c>ContextMonitor("test-session")</c>; this port passes <c>maxTokens:
/// 100</c> straight into the constructor instead (the C# port takes it as an explicit parameter
/// rather than a patchable module global), which exercises the identical threshold-crossing math.
/// </summary>
public sealed class ContextMonitorTests
{
    /// <summary>Captures every Log call so a test can assert a warning was actually logged,
    /// without pulling in an extra test package for a single assertion (same convention as
    /// <see cref="Backend.Tests.Models.ModelDispatchTests"/>'s own <c>RecordingLogger</c>).</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void InitialState_IsZero()
    {
        var cm = new Backend.Sessions.ContextMonitor("test-session");
        Assert.Equal(0, cm.EstimatedTokens);
        Assert.Equal(0.0, cm.UsagePct);
    }

    [Fact]
    public void AddContent_IncreasesTokenEstimate()
    {
        var cm = new Backend.Sessions.ContextMonitor("test-session");
        cm.AddContent(new string('a', 400)); // ~100 tokens
        Assert.Equal(100, cm.EstimatedTokens);
    }

    [Fact]
    public void AddEmptyContent_IsSafe()
    {
        var cm = new Backend.Sessions.ContextMonitor("test-session");
        cm.AddContent("");
        cm.AddContent(null);
        Assert.Equal(0, cm.EstimatedTokens);
    }

    [Fact]
    public void WarningThreshold_LogsCritical()
    {
        var logger = new RecordingLogger();
        var cm = new Backend.Sessions.ContextMonitor("test-session", maxTokens: 100, logger: logger);
        cm.AddContent(new string('a', 400)); // 100 tokens = 100% of 100 max
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("CRITICAL"));
    }
}
