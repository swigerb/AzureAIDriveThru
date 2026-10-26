using Microsoft.Extensions.Logging;

namespace Backend.Tests.TestSupport;

/// <summary>
/// An <see cref="ILogger"/> that keeps the fully rendered text of every entry, for tests that
/// assert on what actually reaches a log sink -- for example that a hostile, client-controlled
/// value is neutralised before it is written.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<CapturedEntry> _entries = [];

    /// <summary>A snapshot of everything logged so far, oldest first.</summary>
    public IReadOnlyList<CapturedEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add(new CapturedEntry(logLevel, eventId, message));
        }
    }

    internal sealed record CapturedEntry(LogLevel Level, EventId EventId, string Message);
}
