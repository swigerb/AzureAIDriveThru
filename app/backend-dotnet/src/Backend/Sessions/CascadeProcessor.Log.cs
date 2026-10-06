using Microsoft.Extensions.Logging;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="CascadeProcessor"/>,
/// avoiding CA1848/CA1873 allocations on hot paths. EventIds 2000-2999 are reserved for this file;
/// message templates, levels and placeholder names are copied verbatim from the call sites they
/// replace -- the conformance suite matches on log text, so none of that may
/// change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Cascade turn ended with an unexpected exception while cancelling it (session={SessionId})")]
    public static partial void CascadeTurnCancelException(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Cancelled in-flight cascade turn: {Reason} (session={SessionId})")]
    public static partial void CascadeTurnCancelled(this ILogger logger, string reason, string sessionId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Could not send speech_started after a barge-in (session={SessionId})")]
    public static partial void SpeechStartedSendFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Unhandled exception in cascade background task '{Label}' (session={SessionId})")]
    public static partial void CascadeBackgroundTaskFailed(this ILogger logger, Exception ex, string label, string sessionId);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Cascade: resume nudge skipped, a turn is in flight (session={SessionId})")]
    public static partial void CascadeNudgeSkippedTurnInFlight(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "Cascade: guest silent {Seconds}s after resume; nudging (session={SessionId})")]
    public static partial void CascadeNudgeFiring(this ILogger logger, double seconds, string sessionId);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "Cascade: resume nudge cancelled: {Reason} (session={SessionId})")]
    public static partial void CascadeNudgeCancelled(this ILogger logger, string reason, string sessionId);

    // EventIds 2007-2012 (CascadeUnknownTool, CascadeExecutingTool, CascadeToolResultDirection,
    // CascadeToolUnhandledException, CascadeTicketRefreshFailed, CascadeChatToolLoopCapped) moved
    // to CascadeToolCallDispatcher.Log.cs alongside issue #338's CascadeToolCallDispatcher
    // extraction -- same EventIds, templates and levels.

    [LoggerMessage(EventId = 2013, Level = LogLevel.Error, Message = "Cascade chat completion failed (session={SessionId})")]
    public static partial void CascadeChatCompletionFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Warning, Message = "Cascade TTS failed for this turn's final answer (session={SessionId})")]
    public static partial void CascadeTtsFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Error, Message = "Cascade transcription failed (session={SessionId})")]
    public static partial void CascadeTranscriptionFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2016, Level = LogLevel.Warning, Message = "Could not extract greeting text from prompt_loader.greeting (session={SessionId})")]
    public static partial void CascadeGreetingExtractFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2017, Level = LogLevel.Warning, Message = "prompt_loader.greeting had no usable item.content[0].text (session={SessionId})")]
    public static partial void CascadeGreetingTextMissing(this ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2018, Level = LogLevel.Warning, Message = "Dropped extension.set_voice with an unknown/invalid voice {Voice} (session={SessionId})")]
    public static partial void CascadeSetVoiceDropped(this ILogger logger, string? voice, string sessionId);

    [LoggerMessage(EventId = 2019, Level = LogLevel.Warning, Message = "Could not read order state while building a session-resumed announcement (session={SessionId})")]
    public static partial void CascadeOrderStateReadFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "Cascade: resumed session {SessionId} rehydrated ({Count} recent turns); greeting suppressed")]
    public static partial void CascadeResumeRehydrated(this ILogger logger, string sessionId, int count);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Information, Message = "Cascade: resume rejected (reason={Reason}); starting fresh session (session={SessionId})")]
    public static partial void CascadeResumeRejected(this ILogger logger, string? reason, string sessionId);

    [LoggerMessage(EventId = 2022, Level = LogLevel.Error, Message = "Error handling cascade client's replayed first message (session={SessionId})")]
    public static partial void CascadeReplayedFirstMessageFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2023, Level = LogLevel.Warning, Message = "Malformed JSON from cascade browser client, ignoring (session={SessionId})")]
    public static partial void CascadeMalformedJson(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2024, Level = LogLevel.Error, Message = "Error handling cascade client message (session={SessionId})")]
    public static partial void CascadeClientMessageHandlingFailed(this ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2025, Level = LogLevel.Information, Message = "Cascade browser WebSocket ended abruptly (session={SessionId})")]
    public static partial void CascadeWebSocketEndedAbruptly(this ILogger logger, Exception ex, string sessionId);
}
