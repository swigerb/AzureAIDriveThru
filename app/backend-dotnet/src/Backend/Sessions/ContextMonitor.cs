namespace Backend.Sessions;

/// <summary>
/// Port of app/backend/session_manager.py's <c>ContextMonitor</c> (issue #13 tail). Estimates
/// token usage in the conversation context window and logs warnings using a simple
/// character-based heuristic (~4 chars/token) -- not exact, but sufficient for warning when a
/// session is approaching the context limit. One instance per session, owned by
/// <see cref="SessionManager"/> (created in <see cref="SessionManager.CreateSession"/>, removed in
/// <see cref="SessionManager.EndSession"/>), same lifetime as
/// Python's own <c>self._context_monitors</c> dict.
/// </summary>
internal sealed class ContextMonitor
{
    private const int CharsPerToken = 4;

    private readonly string _sessionId;
    private readonly int _maxTokens;
    private readonly double _warningPct;
    private readonly double _criticalPct;
    private readonly ILogger? _logger;

    private int _charCount;
    private bool _warnedWarning;
    private bool _warnedCritical;

    public ContextMonitor(
        string sessionId, int maxTokens = 128_000, double warningPct = 80, double criticalPct = 95,
        ILogger? logger = null)
    {
        _sessionId = sessionId;
        _maxTokens = maxTokens;
        _warningPct = warningPct;
        _criticalPct = criticalPct;
        _logger = logger;
    }

    /// <summary>Track content that contributes to the context window. A no-op for null/empty
    /// text, matching Python's own `if not text: return` guard.</summary>
    public void AddContent(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _charCount += text.Length;
        CheckThresholds();
    }

    public int EstimatedTokens => _charCount / CharsPerToken;

    public double UsagePct => _maxTokens <= 0 ? 0.0 : (double)EstimatedTokens / _maxTokens * 100;

    private void CheckThresholds()
    {
        var pct = UsagePct;
        var tokens = EstimatedTokens;

        if (!_warnedCritical && pct >= _criticalPct)
        {
            _logger?.LogWarning(
                "CRITICAL: Context window at {Pct}% ({Tokens:N0}/{MaxTokens:N0} tokens) for session {SessionId}",
                (int)pct, tokens, _maxTokens, _sessionId);
            _warnedCritical = true;
            _warnedWarning = true;
        }
        else if (!_warnedWarning && pct >= _warningPct)
        {
            _logger?.LogWarning(
                "WARNING: Context window at {Pct}% ({Tokens:N0}/{MaxTokens:N0} tokens) for session {SessionId}",
                (int)pct, tokens, _maxTokens, _sessionId);
            _warnedWarning = true;
        }
    }
}
