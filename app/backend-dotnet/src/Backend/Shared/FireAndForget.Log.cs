namespace Backend.Shared;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for
/// <see cref="FireAndForgetExtensions"/>, avoiding CA1848 allocations. EventIds 9100-9199 are
/// reserved for this file; message templates, levels and placeholder names are copied verbatim
/// from the call sites they replace -- the conformance suite matches on log text, so none of that
/// may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 9100, Level = LogLevel.Error, Message = "Unobserved exception in fire-and-forget task: {Description}")]
    public static partial void FireAndForgetTaskFaulted(this ILogger logger, Exception? exception, string description);
}
