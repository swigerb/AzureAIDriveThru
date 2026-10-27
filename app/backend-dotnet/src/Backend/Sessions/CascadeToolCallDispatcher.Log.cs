using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="CascadeToolCallDispatcher"/>.
/// EventIds 2007-2012 moved here verbatim from <c>CascadeProcessor.Log.cs</c> alongside issue
/// #338's <see cref="CascadeToolCallDispatcher"/> extraction -- same EventId, template and level;
/// the conformance suite matches on log text, so none of that may change. EventId 10020 (from the
/// sessions area's 10000-10999 range) was added later when an inline <c>LogWarning</c> call was
/// converted verbatim.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 2007, Level = LogLevel.Error, Message = "Unknown tool requested: {ToolName} (session={SessionId})")]
    public static partial void CascadeUnknownTool(this ILogger logger, string toolName, string sessionId);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Information, Message = "Executing cascade tool '{ToolName}' (session={SessionId})")]
    public static partial void CascadeExecutingTool(this ILogger logger, string toolName, string sessionId);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Information, Message = "Cascade tool '{ToolName}' result direction={Direction} (session={SessionId})")]
    public static partial void CascadeToolResultDirection(this ILogger logger, string toolName, ToolResultDirection direction, string sessionId);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Error, Message = "Cascade tool '{ToolName}' raised an unhandled exception (session={SessionId})")]
    public static partial void CascadeToolUnhandledException(this ILogger logger, Exception ex, string toolName, string sessionId);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning, Message = "Could not read order state to refresh the ticket after a cascade tool failure (session={SessionId})")]
    public static partial void CascadeTicketRefreshFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Warning, Message = "Cascade chat-tool loop hit its {MaxRounds}-round cap without a final answer (session={SessionId})")]
    public static partial void CascadeChatToolLoopCapped(this ILogger logger, int maxRounds, string sessionId);

    [LoggerMessage(EventId = 10020, Level = LogLevel.Warning, Message = "Synthetic cascade diagnostics disabled: session binding changed.")]
    public static partial void CascadeDiagnosticsDisabledSessionBindingChanged(this ILogger logger);
}
