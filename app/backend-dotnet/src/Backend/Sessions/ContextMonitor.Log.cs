namespace Backend.Sessions;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="ContextMonitor"/>. EventIds
/// 10030-10031 come from the sessions area's 10000-10999 range. Both templates are the verbatim text
/// of the inline <c>LogWarning</c> calls they replaced, at the same level; the conformance suite
/// matches on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 10030, Level = LogLevel.Warning, Message = "CRITICAL: Context window at {Pct}% ({Tokens:N0}/{MaxTokens:N0} tokens) for session {SessionId}")]
    public static partial void ContextWindowCritical(this ILogger logger, int pct, int tokens, int maxTokens, string sessionId);

    // SYSLIB1012: the "WARNING:" prefix is part of the verbatim template (a port of
    // session_manager.py's own text, matched by the conformance suite), so it stays.
#pragma warning disable SYSLIB1012
    [LoggerMessage(EventId = 10031, Level = LogLevel.Warning, Message = "WARNING: Context window at {Pct}% ({Tokens:N0}/{MaxTokens:N0} tokens) for session {SessionId}")]
    public static partial void ContextWindowWarning(this ILogger logger, int pct, int tokens, int maxTokens, string sessionId);
#pragma warning restore SYSLIB1012
}
