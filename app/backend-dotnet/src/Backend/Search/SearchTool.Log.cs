using Microsoft.Extensions.Logging;

namespace Backend.Search;

/// <summary>
/// Source-generated <see cref="ILogger"/> partial methods for <see cref="SearchTool"/> (issue
/// #336: CA1848/CA1873 on hot paths). EventIds 4000-4999 are reserved for this file; message
/// templates, levels and placeholder names are copied verbatim from the call sites they replace --
/// the conformance suite and ops dashboards match on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Error, Message = "Search timed out for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SearchTimedOut(this ILogger logger, Exception exc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Search field-name mismatch for persona {PersonaId} index {IndexName}; retrying with a minimal projection: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SearchFieldNameMismatch(this ILogger logger, Exception exc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "Search field-name-mismatch retry also failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SearchFieldNameMismatchRetryFailed(this ILogger logger, Exception retryExc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Semantic ranker rejected by the service for persona {PersonaId} index {IndexName}; retrying without it: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SemanticRankerRejected(this ILogger logger, Exception exc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "Semantic-ranker retry also failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SemanticRankerRetryFailed(this ILogger logger, Exception retryExc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Error, Message = "Search failed for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SearchFailed(this ILogger logger, Exception exc, string? personaId, string indexName, string exceptionType, string exceptionMessage);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Error, Message = "Search failed unexpectedly for persona {PersonaId} index {IndexName}: {ExceptionType}: {ExceptionMessage}")]
    public static partial void SearchFailedUnexpectedly(this ILogger logger, Exception exc, string? personaId, string indexName, string exceptionType, string exceptionMessage);
}
