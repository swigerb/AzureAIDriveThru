using Microsoft.Extensions.Logging;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="RealtimeProcessor"/>,
/// avoiding CA1848/CA1873 allocations on hot paths. EventIds 1000-1999 are reserved for this file;
/// message templates, levels and placeholder names are copied verbatim from the call sites they
/// replace -- the conformance suite matches on log text, so none of that may
/// change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Failed to connect to upstream realtime endpoint for deployment {Deployment} (session={SessionId})")]
    public static partial void UpstreamConnectFailed(this ILogger logger, Exception ex, string deployment, string sessionId);

    // EventIds 1001-1002 moved to GreetingGate.Log.cs with issue #338's GreetingGate
    // extraction -- same EventIds, same templates, same levels, just a new home alongside the
    // code that logs them now.

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Dropped extension.resume arriving after the first-frame decision ({Reason}, session={SessionId})")]
    public static partial void DroppedLateResume(this ILogger logger, string reason, string sessionId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "Dropped extension.set_voice with an unknown/invalid voice {Voice} (session={SessionId})")]
    public static partial void DroppedSetVoice(this ILogger logger, string? voice, string sessionId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Assistant audio already present -- voice {Voice} applies from the next conversation (session={SessionId})")]
    public static partial void VoiceDeferredToNextConversation(this ILogger logger, string voice, string sessionId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "extension.resume rejected (reason={Reason}, session={SessionId})")]
    public static partial void ExtensionResumeRejected(this ILogger logger, string? reason, string sessionId);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "Session resumed (resumedSessionId={ResumedSessionId}, session={SessionId})")]
    public static partial void SessionResumedWithId(this ILogger logger, string? resumedSessionId, string sessionId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "Browser WebSocket error (session={SessionId})")]
    public static partial void BrowserWebSocketError(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "Dropped malformed/non-object client→server frame (session={SessionId})")]
    public static partial void DroppedMalformedClientFrame(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Error processing extension.resume (session={SessionId})")]
    public static partial void ErrorProcessingExtensionResume(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "Dropped client→server frame with a missing/non-string type (session={SessionId})")]
    public static partial void DroppedClientFrameMissingType(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "Guest ended session (session={SessionId})")]
    public static partial void GuestEndedSession(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "Error processing client→server frame (session={SessionId})")]
    public static partial void ErrorProcessingClientFrame(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Error, Message = "Fallback session.update {EventId} (for {Original}) was ALSO rejected: code={Code} param={Param} message={Message} -- tools may NOT be registered for this conversation (session={SessionId})")]
    public static partial void FallbackSessionUpdateAlsoRejected(this ILogger logger, string eventId, string? original, string? code, string? param, string? message, string sessionId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Error, Message = "Upstream REJECTED session.update {EventId}: code={Code} param={Param} message={Message} -- resending a minimal session.update (instructions + tools only) so the tools survive (session={SessionId})")]
    public static partial void SessionUpdateRejected(this ILogger logger, string eventId, string? code, string? param, string? message, string sessionId);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Error, Message = "Deployment {Deployment} rejected reasoning-model options; no longer sending `reasoning` / `parallel_tool_calls` from this process. Set model.reasoning_effort to \"\" for this deployment.")]
    public static partial void ReasoningOptionsRejected(this ILogger logger, string deployment);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information, Message = "OpenAI Realtime API error (benign -- response already finished): {Error}")]
    public static partial void RealtimeApiErrorBenign(this ILogger logger, string error);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Error, Message = "OpenAI Realtime API error: {Error}")]
    public static partial void RealtimeApiError(this ILogger logger, string error);

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

    [LoggerMessage(EventId = 1026, Level = LogLevel.Warning, Message = "Capping auto response.create with tool_choice=none after {Count} consecutive failed tool round(s) (session={SessionId})")]
    public static partial void CappingAutoResponseCreate(this ILogger logger, int count, string sessionId);

    [LoggerMessage(EventId = 1027, Level = LogLevel.Warning, Message = "Suppressing auto response.create -- still at the {Count}-round cap with no guest turn since the apology (session={SessionId})")]
    public static partial void SuppressingAutoResponseCreate(this ILogger logger, int count, string sessionId);

    [LoggerMessage(EventId = 1028, Level = LogLevel.Information, Message = "Response contained {Count} tool call(s): {Names} (session={SessionId})")]
    public static partial void ResponseContainedToolCalls(this ILogger logger, int count, string names, string sessionId);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning, Message = "Could not read order state while building a session-resumed announcement (session={SessionId})")]
    public static partial void OrderStateReadForAnnouncementFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Error, Message = "Input audio transcription failed (model={Model}): {Error} (session={SessionId})")]
    public static partial void InputAudioTranscriptionFailed(this ILogger logger, string? model, string? error, string sessionId);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "session.updated received -- tools are configured (session={SessionId})")]
    public static partial void SessionUpdatedToolsConfigured(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1032, Level = LogLevel.Information, Message = "Tool call received: name={ToolName}, call_id={CallId} (session={SessionId})")]
    public static partial void ToolCallReceived(this ILogger logger, string? toolName, string? callId, string sessionId);

    [LoggerMessage(EventId = 1033, Level = LogLevel.Warning, Message = "Upstream WebSocket error (session={SessionId})")]
    public static partial void UpstreamWebSocketError(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1034, Level = LogLevel.Warning, Message = "Dropped malformed/non-object server→client frame (session={SessionId})")]
    public static partial void DroppedMalformedServerFrame(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1035, Level = LogLevel.Warning, Message = "Error processing server→client frame (session={SessionId})")]
    public static partial void ErrorProcessingServerFrame(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 1036, Level = LogLevel.Information, Message = "Upstream session bootstrapped with {ToolCount} tool(s) before relaying client traffic (session={SessionId})")]
    public static partial void UpstreamSessionBootstrapped(this ILogger logger, int toolCount, string sessionId);

    [LoggerMessage(EventId = 1037, Level = LogLevel.Error, Message = "Unexpected error in realtime relay (session={SessionId})")]
    public static partial void UnexpectedRelayError(this ILogger logger, Exception ex, string sessionId);

    // EventIds 1038-1040 moved to FramePump.Log.cs with issue #338's FramePump extraction --
    // same EventIds, same templates, same levels, just a new home alongside the code that logs
    // them now.

    [LoggerMessage(EventId = 1041, Level = LogLevel.Warning, Message = "Dropped extension.set_machine_status with unknown/invalid machine or status (machine={Machine}, status={Status}, session={SessionId})")]
    public static partial void DroppedSetMachineStatus(this ILogger logger, string? machine, string? status, string sessionId);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Information, Message = "Applied extension.set_machine_status machine={Machine} status={Status} (session={SessionId})")]
    public static partial void AppliedSetMachineStatus(this ILogger logger, string? machine, string? status, string sessionId);

    [LoggerMessage(EventId = 1043, Level = LogLevel.Warning, Message = "Dropped extension.set_happy_hour_mode with an invalid mode or unsupported persona (mode={Mode}, session={SessionId})")]
    public static partial void DroppedSetHappyHourMode(this ILogger logger, string? mode, string sessionId);

    [LoggerMessage(EventId = 1044, Level = LogLevel.Information, Message = "Applied extension.set_happy_hour_mode mode={Mode} (session={SessionId})")]
    public static partial void AppliedSetHappyHourMode(this ILogger logger, string? mode, string sessionId);

    [LoggerMessage(EventId = 1045, Level = LogLevel.Warning, Message = "Could not read order state to push a refreshed ticket after extension.set_happy_hour_mode (session={SessionId})")]
    public static partial void TicketRefreshAfterHappyHourModeFailed(this ILogger logger, Exception ex, string sessionId);
}
