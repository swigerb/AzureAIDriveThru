using Backend.Tools;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: log declarations for <see cref="ToolCallDispatcher"/>, moved verbatim (same
/// EventIds, templates and levels) out of RealtimeProcessor.Log.cs's 1000-1999 range when
/// HandleToolCallDoneAsync was extracted into its own collaborator.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1019, Level = LogLevel.Warning, Message = "Tool call {CallId} not found in pending tools (session={SessionId})")]
    public static partial void ToolCallNotFoundInPending(this ILogger logger, string? callId, string sessionId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Error, Message = "Unknown tool requested: {ToolName} (session={SessionId})")]
    public static partial void UnknownToolRequested(this ILogger logger, string toolName, string sessionId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information, Message = "Dropping tool call '{ToolName}' for call_id={CallId}: this connection was superseded by a resume elsewhere (session={SessionId})")]
    public static partial void ToolCallDroppedSuperseded(this ILogger logger, string toolName, string? callId, string sessionId);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Executing tool '{ToolName}' (session={SessionId})")]
    public static partial void ExecutingTool(this ILogger logger, string toolName, string sessionId);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Information, Message = "Tool '{ToolName}' result direction={Direction} (session={SessionId})")]
    public static partial void ToolResultDirectionLogged(this ILogger logger, string toolName, ToolResultDirection direction, string sessionId);

    [LoggerMessage(EventId = 1024, Level = LogLevel.Error, Message = "Tool '{ToolName}' raised an unhandled exception (session={SessionId})")]
    public static partial void ToolUnhandledException(this ILogger logger, Exception ex, string toolName, string sessionId);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Warning, Message = "Could not read order state to refresh the ticket after a tool failure (session={SessionId})")]
    public static partial void TicketRefreshAfterToolFailureFailed(this ILogger logger, Exception ex, string sessionId);
}
