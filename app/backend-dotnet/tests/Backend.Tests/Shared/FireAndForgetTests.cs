using Backend.Shared;
using Microsoft.Extensions.Logging;

namespace Backend.Tests.Shared;

/// <summary>
/// Verifies <see cref="FireAndForgetExtensions.FireAndForget"/> (issue #335): a faulted
/// fire-and-forget task must be observed and logged (never an unobserved-task-exception crash,
/// never a silently swallowed failure), while a successfully completed or cancelled task logs
/// nothing.
/// </summary>
public sealed class FireAndForgetTests
{
    /// <summary>Captures every Log call so a test can assert a fault was actually logged, without
    /// pulling in an extra test package for a single assertion (same convention as
    /// <see cref="Backend.Tests.Models.ModelDispatchTests"/>'s own <c>RecordingLogger</c>).</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task FaultedTask_LogsErrorWithException()
    {
        var logger = new RecordingLogger();
        var tcs = new TaskCompletionSource();
        var faultingException = new InvalidOperationException("boom");

        tcs.Task.FireAndForget(logger, "test-site");
        tcs.SetException(faultingException);

        // The continuation that logs the fault runs asynchronously -- wait for it to show up
        // instead of racing a fixed delay.
        await WaitForEntryAsync(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(faultingException, entry.Exception);
        Assert.Contains("test-site", entry.Message);
    }

    [Fact]
    public void AlreadyFaultedTask_LogsErrorSynchronously()
    {
        var logger = new RecordingLogger();
        var faultingException = new InvalidOperationException("already failed");
        var task = Task.FromException(faultingException);

        task.FireAndForget(logger, "already-faulted-site");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(faultingException, entry.Exception);
    }

    [Fact]
    public async Task FaultedTaskRunTask_IsObservedAndLogged()
    {
        var logger = new RecordingLogger();

        Task.Run(() => throw new InvalidOperationException("task.run boom"), TestContext.Current.CancellationToken).FireAndForget(logger, "task-run-site");

        await WaitForEntryAsync(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task SuccessfulTask_LogsNothing()
    {
        var logger = new RecordingLogger();
        var tcs = new TaskCompletionSource();

        tcs.Task.FireAndForget(logger, "success-site");
        tcs.SetResult();

        // Give the continuation a turn to run, then assert nothing was logged.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task CancelledTask_LogsNothing()
    {
        var logger = new RecordingLogger();
        var tcs = new TaskCompletionSource();

        tcs.Task.FireAndForget(logger, "cancelled-site");
        tcs.SetCanceled(TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void NullLogger_DoesNotThrowOnFault()
    {
        // EchoSuppressor's sites have no ILogger available; FireAndForget must still observe the
        // task (so it never becomes an unobserved-task-exception) without a logger to report to.
        var exception = Record.Exception(() =>
            Task.FromException(new InvalidOperationException("no logger")).FireAndForget(null, "no-logger-site"));
        Assert.Null(exception);
    }

    private static async Task WaitForEntryAsync(RecordingLogger logger)
    {
        for (var i = 0; i < 100 && logger.Entries.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
