using Microsoft.Extensions.Logging;

namespace Backend.Shared;

/// <summary>
/// One small helper for every discarded fire-and-forget <see cref="Task"/> in the backend:
/// observes the task and logs a fault instead of letting it become either an
/// unobserved-task-exception process crash (pre-.NET-4.5 behavior some hosts still opt back into)
/// or a silently swallowed failure (a discarded Task's exception is otherwise never looked at
/// again). Every call site's own task body already handles its OWN expected failures internally --
/// this is strictly a last-resort safety net, not a replacement for those inner try/catch blocks,
/// so it must never change what those bodies catch or how they log.
/// </summary>
internal static class FireAndForgetExtensions
{
    /// <summary>Starts observing <paramref name="task"/> without awaiting it here. If the task
    /// later faults, logs the exception at Error level via <paramref name="logger"/>, tagged with
    /// <paramref name="description"/> so the log line identifies which fire-and-forget call site
    /// failed. A task that completes successfully or is cancelled logs nothing (cancellation is an
    /// expected shutdown/supersede path, not a fault).</summary>
    public static void FireAndForget(this Task task, ILogger? logger, string description)
    {
        if (task.IsCompletedSuccessfully || task.IsCanceled)
        {
            return;
        }
        if (task.IsFaulted)
        {
            LogFault(task, logger, description);
            return;
        }
        _ = ObserveAsync(task, logger, description);
    }

    private static async Task ObserveAsync(Task task, ILogger? logger, string description)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown/supersede path, not a fault -- nothing to log.
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unobserved exception in fire-and-forget task: {Description}", description);
        }
    }

    private static void LogFault(Task task, ILogger? logger, string description)
    {
        var ex = task.Exception?.GetBaseException();
        logger?.LogError(ex, "Unobserved exception in fire-and-forget task: {Description}", description);
    }
}
