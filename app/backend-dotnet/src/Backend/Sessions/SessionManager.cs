using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Backend.Configuration;
using Backend.Realtime;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>Result of <see cref="SessionManager.TryResume"/> -- mirrors
/// app/backend/session_manager.py's <c>ResumeOutcome</c> dataclass. <see cref="StaleWs"/> is the
/// still-attached socket to supersede with 4002 (non-blocking, async) when the resumed session was
/// never detached in the first place; <see cref="ConversationStarted"/> (Python's
/// <c>has_sent_greeting</c>) gates whether the resume rehydrates (greeting already happened) or
/// just re-greets as if fresh. <see cref="StaleCts"/> -- a C#-only addition with no Python
/// counterpart (asyncio has no equivalent "cancel this other task" handle threaded through a
/// dataclass) -- is the STALE connection's own linked <see cref="CancellationTokenSource"/>,
/// present alongside <see cref="StaleWs"/> precisely when there is one to steal from; cancelling it
/// is what actually stops that connection's relay loops (and any in-flight tool dispatch through
/// the shared <see cref="IToolExecutor"/>) promptly on supersede, rather than relying solely on it
/// noticing the 4002 close frame on its own schedule.</summary>
public sealed record ResumeOutcome(
    bool Accepted,
    string? Reason = null,
    string? SessionId = null,
    string? ResumeId = null,
    WebSocket? StaleWs = null,
    bool ConversationStarted = false,
    IToolExecutor? ToolExecutor = null,
    string? Voice = null,
    IReadOnlyList<(string Role, string Text)>? RecentTurns = null,
    CancellationTokenSource? StaleCts = null,
    SessionIdentifiers? Identifiers = null);

/// <summary>Rick's #244 round-2 review (issue 1): a signal independent of <see cref="StaleCts"/>'s
/// cancellation, set exactly once, synchronously, inside <see cref="SessionManager"/>'s own lock
/// the instant a resume captures a still-attached socket as stale -- i.e. as early as possible,
/// before any socket IO (the background supersede-close) is even scheduled, let alone awaited.
/// <c>OrderToolExecutor.ExecuteAsync</c> is synchronous and ignores its <see cref="CancellationToken"/>
/// parameter entirely, and the <c>OrderState</c> it mutates isn't thread-safe, so a stale
/// connection's own in-flight <c>HandleToolCallDoneAsync</c> cannot rely on <see cref="StaleCts"/>
/// ever being cancelled promptly (the background close it now shares a fate with may legitimately
/// take up to its own short timeout against a non-draining peer) -- it needs a flag it can check
/// synchronously, with no IO and no dependency on how long that close takes.</summary>
public sealed class SupersededFlag
{
    private volatile bool _value;

    public bool IsSuperseded => _value;

    public void MarkSuperseded() => _value = true;
}

/// <summary>
/// Port of app/backend/session_manager.py's <c>SessionManager</c> (issue #15). A single
/// process-wide singleton (constructor-injected into <see cref="Backend.Sessions.RealtimeProcessor"/>
/// the same way <see cref="Backend.Realtime.RateLimitSettings"/> is) tracking every session's
/// attach/detach/resume/idle state -- NOT the same thing as the orthogonal
/// <see cref="SessionRegistry"/>/<see cref="SessionActor"/> mailbox plumbing from issue #12, which
/// has nothing to do with resume/idle/grace.
///
/// Every public method takes <see cref="_sync"/> for its own state transition and returns before
/// any async IO is attempted by the caller (same split <see cref="Backend.Realtime.RateLimitRecovery"/>
/// and <see cref="Backend.Realtime.EchoSuppressor"/> already use) -- resume/detach/idle-close can
/// all race against each other across the two per-connection relay loops of DIFFERENT sessions
/// (unlike those two classes, which are purely per-connection), so this class's lock protects the
/// whole shared registry, not just one session's state.
/// </summary>
public sealed class SessionManager
{
    public const int IdleCloseCode = 4000;
    public const string IdleCloseReason = "idle_timeout";
    public const int SupersededCloseCode = 4002;
    public const string SupersededCloseReason = "superseded";

    /// <summary>Every middle-tier-authored conversation item id is stamped with this prefix --
    /// mirrors <see cref="Backend.Realtime.ClientServerFilter.MiddleTierItemIdPrefix"/> (kept as
    /// its own constant here, not a cross-reference, since session_manager.py's own
    /// <c>MIDDLE_TIER_ITEM_ID_PREFIX</c> is independent of rtmt.py's filtering constant too).</summary>
    private const string RehydrationPreamble =
        "The guest reconnected mid-order after a brief connection drop. Do not greet them again or " +
        "restart the conversation -- just continue helping with their order where it left off.";

    private static readonly CompositeFormat NudgeTextTemplate = CompositeFormat.Parse(
        "The guest has been silent for a while after reconnecting. As {0}, briefly check in once " +
        "(e.g. \"Still there? Let me know if you'd like to add anything else or if you're ready to pay.\") " +
        "without repeating the full order back.");

    private readonly SessionsConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, SessionRecord> _sessions = new();
    private readonly Dictionary<string, string> _resumeIndex = new(); // digest -> sessionId
    private readonly LinkedList<string> _detachedLru = new(); // oldest first
    private readonly Dictionary<string, LinkedListNode<string>> _detachedNodes = new();
    /// <summary>Port of session_manager.py's `self._context_monitors` dict. Kept independent of
    /// <see cref="_sessions"/> (rather than a field on <see cref="SessionRecord"/>), mirroring
    /// Python. Created in <see cref="CreateSession"/>, removed in <see cref="EndSession"/>.</summary>
    private readonly Dictionary<string, ContextMonitor> _contextMonitors = new();

    public SessionManager(SessionsConfig? config = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _config = config ?? new SessionsConfig();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public SessionsConfig Config => _config;

    /// <summary>Test-only: exposes the internal lock object for the same reason
    /// <see cref="Backend.Realtime.RateLimitRecovery.SyncRootForTests"/> does -- holding it across a
    /// FakeTimeProvider.Advance() to deterministically reproduce a timer-vs-cancellation race.</summary>
    internal object SyncRootForTests => _sync;

    private sealed class SessionRecord
    {
        public required string SessionId { get; init; }
        public required string PersonaId { get; init; }
        public required string ModelId { get; init; }
        public string? MenuMode { get; init; }
        public required IToolExecutor ToolExecutor { get; set; }
        public string Voice { get; set; } = "";
        public bool ConversationStarted { get; set; }
        public WebSocket? AttachedSocket { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        public DateTimeOffset? DetachedAt { get; set; }
        public string? ResumeDigest { get; set; }
        public Queue<(string Role, string Text)> Transcript { get; } = new();

        /// <summary>The CURRENTLY attached connection's own linked CTS (Rick's #244 review,
        /// issue 4), set by whichever of <see cref="CreateSession"/>/<see cref="TryResume"/> most
        /// recently attached a socket to this record. Null for callers (tests, mainly) that don't
        /// pass one -- supersede then falls back to the close-frame-only behaviour that existed
        /// before this field, never a hard requirement.</summary>
        public CancellationTokenSource? AttachedCts { get; set; }

        /// <summary>Rick's #244 round-2 review, issue 1: the CURRENTLY attached connection's own
        /// <see cref="SupersededFlag"/> -- see that class's own doc comment for why this exists
        /// alongside (not instead of) <see cref="AttachedCts"/>.</summary>
        public SupersededFlag? AttachedSupersededFlag { get; set; }

        /// <summary>Rick's #244 review, issue 5: the wire-facing sessionToken/roundTripIndex pair
        /// (Backend.Realtime.SessionIdentifiers is already a mutable, in-place-incrementing class --
        /// see its own AdvanceRoundTrip -- so persisting THIS SAME OBJECT here, and handing the
        /// identical reference back out on every resume, is all that's needed for a resumed
        /// connection's round-trip counter to keep counting up from where the original connection
        /// left off instead of restarting at a brand-new token/index=0. Mirrors
        /// order_state.py's session-keyed `session_token`/`round_trip_index` fields, which
        /// order_state_singleton.get_session_identifiers/advance_round_trip read/mutate the exact
        /// same way (by session id, independent of which physical socket is currently
        /// attached).</summary>
        public SessionIdentifiers? Identifiers { get; set; }
    }

    // ── Session lifecycle ──

    /// <summary>Registers a brand-new (provisional, per rtmt.py's own terminology) session for a
    /// just-accepted browser socket -- called once at the very top of
    /// <c>RealtimeProcessor.RunSessionAsync</c> and <c>CascadeProcessor.RunSessionAsync</c>, before the resume handshake on the first client
    /// frame decides whether this provisional session survives or is replaced by a resumed
    /// one.</summary>
    public void CreateSession(string sessionId, WebSocket ws, string personaId, string modelId, string? menuMode,
        IToolExecutor toolExecutor, string voice, CancellationTokenSource? attachedCts = null,
        SessionIdentifiers? identifiers = null, SupersededFlag? attachedSupersededFlag = null)
    {
        lock (_sync)
        {
            _sessions[sessionId] = new SessionRecord
            {
                SessionId = sessionId,
                PersonaId = personaId,
                ModelId = modelId,
                MenuMode = menuMode,
                ToolExecutor = toolExecutor,
                Voice = voice,
                AttachedSocket = ws,
                LastActivity = _timeProvider.GetUtcNow(),
                AttachedCts = attachedCts,
                Identifiers = identifiers,
                AttachedSupersededFlag = attachedSupersededFlag,
            };
            _contextMonitors[sessionId] = CreateContextMonitorLocked(sessionId);
        }
    }

    private ContextMonitor CreateContextMonitorLocked(string sessionId) =>
        new(sessionId, _config.ContextMaxTokens, _config.ContextWarningThresholdPct,
            _config.ContextCriticalThresholdPct, _logger);

    /// <summary>Port of session_manager.py's `get_context_monitor`: returns null for a null/unknown
    /// session id, never throws.</summary>
    public ContextMonitor? GetContextMonitor(string? sessionId)
    {
        if (sessionId is null)
        {
            return null;
        }
        lock (_sync)
        {
            return _contextMonitors.GetValueOrDefault(sessionId);
        }
    }

    public void TouchActivity(string sessionId)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var record))
            {
                record.LastActivity = _timeProvider.GetUtcNow();
            }
        }
    }

    public void MarkConversationStarted(string sessionId)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var record))
            {
                record.ConversationStarted = true;
            }
        }
    }

    public string GetVoice(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record) ? record.Voice : "";
        }
    }

    public void SetVoice(string sessionId, string voice)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var record))
            {
                record.Voice = voice;
            }
        }
    }

    public IReadOnlyDictionary<string, string> GetMachineOverrides(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record)
                && record.ToolExecutor is IOrderSessionSettings settings
                ? settings.GetMachineOverrides()
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public bool SetMachineStatus(string sessionId, string machine, string status)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record)
                && record.ToolExecutor is IOrderSessionSettings settings
                && settings.SetMachineOverride(machine, status);
        }
    }

    public string GetHappyHourMode(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record)
                && record.ToolExecutor is IOrderSessionSettings settings
                ? settings.GetHappyHourMode()
                : "auto";
        }
    }

    public bool SetHappyHourMode(string sessionId, string mode)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record)
                && record.ToolExecutor is IOrderSessionSettings settings
                && settings.SetHappyHourMode(mode);
        }
    }

    /// <summary>#309 (R2): best-effort read of this session's current order ticket -- the same
    /// shape <c>get_order</c>/<c>reset_order</c> already produce (<see
    /// cref="IOrderTicketSource.CurrentOrderSummaryJson"/>) -- so a caller can push a refreshed
    /// ticket to the browser right after a mutation that isn't itself a tool call (e.g.
    /// <c>extension.set_happy_hour_mode</c>). Null for an unknown session, or an executor that
    /// doesn't implement <see cref="IOrderTicketSource"/> (mirrors <c>GetHappyHourMode</c>'s own
    /// "session not found" fallback shape).</summary>
    public string? GetOrderSummaryJson(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var record) && record.ToolExecutor is IOrderTicketSource ticketSource
                ? ticketSource.CurrentOrderSummaryJson
                : null;
        }
    }

    /// <summary>Port of record_turn: keeps the last <see cref="SessionsConfig.HistoryTurns"/> turns
    /// ("guest"/"assistant"), each capped at <see cref="SessionsConfig.HistoryChars"/> characters,
    /// for later rehydration. A no-op once the config disables history (`history_turns &lt;=
    /// 0`).</summary>
    public void RecordTurn(string sessionId, string role, string? text)
    {
        if (_config.HistoryTurns <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var capped = text.Length > _config.HistoryChars ? text[..Math.Max(_config.HistoryChars, 0)] : text;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var record))
            {
                return;
            }
            record.Transcript.Enqueue((role, capped));
            while (record.Transcript.Count > _config.HistoryTurns)
            {
                record.Transcript.Dequeue();
            }
        }
    }

    // ── Resume credential ──

    /// <summary>Port of issue_resume_id: mints a fresh, single-use resume id for the session,
    /// invalidating any previous one. Only its SHA-256 digest is retained -- the raw value is
    /// returned to the caller (which sends it to the browser over the socket) and never logged or
    /// stored anywhere else. Returns null when resume is disabled.</summary>
    public string? IssueResumeId(string sessionId)
    {
        if (!_config.ResumeEnabled)
        {
            return null;
        }
        var resumeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); // 64 chars, in [32,128]
        var digest = Digest(resumeId);
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var record))
            {
                return null;
            }
            if (record.ResumeDigest is not null)
            {
                _resumeIndex.Remove(record.ResumeDigest);
            }
            record.ResumeDigest = digest;
            _resumeIndex[digest] = sessionId;
        }
        return resumeId;
    }

    private static string Digest(string resumeId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resumeId)));

    /// <summary>
    /// Port of session_manager.py's <c>resume()</c>. <paramref name="provisionalSessionId"/> is the
    /// socket's own just-created session (ended on success, per Python's <c>current</c> handling);
    /// <paramref name="requestedPersonaId"/>/<paramref name="requestedModelId"/>/
    /// <paramref name="requestedMenuMode"/> are this connection's OWN already-fully-resolved
    /// binding (Program.cs has already applied every default by the time RunSessionAsync starts),
    /// so -- unlike the Python port, which still has to resolve an omitted `None` to "the
    /// deployment default persona"/etc. itself -- these are compared directly with no further
    /// default resolution.
    /// </summary>
    public ResumeOutcome TryResume(
        WebSocket ws,
        string? presentedId,
        string requestedPersonaId,
        string requestedModelId,
        string? requestedMenuMode,
        string provisionalSessionId,
        CancellationTokenSource? attachedCts = null,
        SupersededFlag? attachedSupersededFlag = null)
    {
        if (!_config.ResumeEnabled)
        {
            return new ResumeOutcome(false, Reason: "disabled");
        }
        if (presentedId is null || presentedId.Length is < 32 or > 128)
        {
            return new ResumeOutcome(false, Reason: "malformed");
        }

        var digest = Digest(presentedId);
        string? staleReason;
        ResumeOutcome? outcome = null;
        WebSocket? staleWs;

        lock (_sync)
        {
            if (!_resumeIndex.TryGetValue(digest, out var sessionId) || !_sessions.TryGetValue(sessionId, out var record)
                || record.ResumeDigest is null || !FixedTimeEquals(record.ResumeDigest, digest))
            {
                outcome = new ResumeOutcome(false, Reason: "unknown");
            }
            else
            {
                var now = _timeProvider.GetUtcNow();
                var idleDeadline = record.LastActivity + TimeSpan.FromSeconds(_config.IdleTimeoutSeconds);
                DateTimeOffset? expires = record.DetachedAt is { } detachedAt
                    ? Min(detachedAt + TimeSpan.FromSeconds(_config.GraceSeconds), idleDeadline)
                    : null;
                if (now > idleDeadline || (expires is { } exp && now >= exp))
                {
                    EndSessionLocked(sessionId, "resume attempted after expiry");
                    outcome = new ResumeOutcome(false, Reason: "expired");
                }
                else if (record.PersonaId != requestedPersonaId)
                {
                    _logger?.LogInformation(
                        "Resume rejected for session {SessionId}: bound persona {Bound} != requested persona {Requested} (persona_mismatch)",
                        sessionId, record.PersonaId, requestedPersonaId);
                    outcome = new ResumeOutcome(false, Reason: "persona_mismatch");
                }
                else if (record.ModelId != requestedModelId)
                {
                    _logger?.LogInformation(
                        "Resume rejected for session {SessionId}: bound model {Bound} != requested model {Requested} (model_mismatch)",
                        sessionId, record.ModelId, requestedModelId);
                    outcome = new ResumeOutcome(false, Reason: "model_mismatch");
                }
                else if (record.MenuMode != requestedMenuMode)
                {
                    _logger?.LogInformation(
                        "Resume rejected for session {SessionId}: bound menu mode {Bound} != requested menu mode {Requested} (mode_mismatch)",
                        sessionId, record.MenuMode, requestedMenuMode);
                    outcome = new ResumeOutcome(false, Reason: "mode_mismatch");
                }
                else
                {
                    // Consume the presented id before anything else can use it.
                    _resumeIndex.Remove(digest);
                    record.ResumeDigest = null;

                    staleWs = record.AttachedSocket;
                    if (ReferenceEquals(staleWs, ws))
                    {
                        staleWs = null;
                    }
                    // Issue 4 (Rick's #244 review): capture the STALE connection's own CTS before
                    // overwriting it with this (new, winning) connection's -- null whenever there
                    // is no still-attached socket to steal from (staleWs is also null then), same
                    // condition as staleWs itself.
                    var staleCts = staleWs is not null ? record.AttachedCts : null;
                    // Rick's #244 round-2 review, issue 1: mark the STALE connection's own flag
                    // SYNCHRONOUSLY, right here under the lock -- not deferred to whenever the
                    // background supersede-close (started by the caller afterward) happens to run
                    // or finish. This is what lets the stale connection's own in-flight
                    // HandleToolCallDoneAsync refuse to dispatch a tool the instant it checks,
                    // regardless of how long that close takes against a non-draining peer.
                    if (staleWs is not null)
                    {
                        record.AttachedSupersededFlag?.MarkSuperseded();
                        record.AttachedSocket = null;
                    }
                    if (provisionalSessionId != sessionId)
                    {
                        EndSessionLocked(provisionalSessionId, "replaced by resume");
                    }
                    RemoveFromDetachedLocked(sessionId);
                    record.DetachedAt = null;
                    record.AttachedSocket = ws;
                    record.AttachedCts = attachedCts;
                    record.AttachedSupersededFlag = attachedSupersededFlag;
                    record.LastActivity = now;

                    var newId = IssueResumeIdLocked(record);
                    staleReason = staleWs is not null ? "; superseding a still-attached socket" : "";
                    _logger?.LogInformation(
                        "Session {SessionId} resumed{Superseded}", sessionId, staleReason);

                    outcome = new ResumeOutcome(
                        true,
                        SessionId: sessionId,
                        ResumeId: newId,
                        StaleWs: staleWs,
                        ConversationStarted: record.ConversationStarted,
                        ToolExecutor: record.ToolExecutor,
                        Voice: record.Voice,
                        RecentTurns: RecentTurnsLocked(record),
                        StaleCts: staleCts,
                        Identifiers: record.Identifiers);
                }
            }
        }

        return outcome!;
    }

    /// <summary>Constant-time digest compare (the resume id itself is never compared directly --
    /// only its digest, same as Python's <c>hmac.compare_digest</c>).</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Must be called with <see cref="_sync"/> held -- the locked half of
    /// <see cref="IssueResumeId"/>, reused by <see cref="TryResume"/> so rotation happens under the
    /// same lock acquisition as the rest of the resume transition.</summary>
    private string? IssueResumeIdLocked(SessionRecord record)
    {
        if (!_config.ResumeEnabled)
        {
            return null;
        }
        var resumeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var digest = Digest(resumeId);
        if (record.ResumeDigest is not null)
        {
            _resumeIndex.Remove(record.ResumeDigest);
        }
        record.ResumeDigest = digest;
        _resumeIndex[digest] = record.SessionId;
        return resumeId;
    }

    // ── Rehydration / nudge item text ──

    private List<(string Role, string Text)> RecentTurnsLocked(SessionRecord record)
    {
        var budget = Math.Max(_config.HistoryChars, 0);
        var kept = new List<(string Role, string Text)>();
        foreach (var (role, text) in record.Transcript.Reverse())
        {
            if (budget <= 0)
            {
                break;
            }
            var trimmed = text;
            if (trimmed.Length > budget)
            {
                trimmed = "…" + trimmed[(trimmed.Length - budget + 1)..];
            }
            kept.Add((role, trimmed));
            budget -= trimmed.Length;
        }
        kept.Reverse();
        return kept;
    }

    /// <summary>Port of build_rehydration_item: a system-role conversation item carrying the
    /// current order JSON plus the recent transcript, so a resumed upstream connection can
    /// continue the order without re-greeting. <paramref name="orderSummaryJson"/> is read by the
    /// caller via <see cref="IOrderTicketSource.CurrentOrderSummaryJson"/> on the SAME
    /// <see cref="IToolExecutor"/> instance this session has used throughout (order state survives
    /// the detach -- it is never recreated on resume).</summary>
    public static string BuildRehydrationText(
        string orderSummaryJson, IReadOnlyList<(string Role, string Text)> recentTurns, string roleName)
    {
        var roleLabel = char.ToUpperInvariant(roleName[0]) + roleName[1..];
        var history = string.Join(
            "\n", recentTurns.Select(t => $"{(t.Role == "guest" ? "Guest" : roleLabel)}: {t.Text}"));
        return $"{RehydrationPreamble}\n\nCurrent order (JSON): {orderSummaryJson}\n\n" +
               $"Recent conversation (oldest first):\n{(history.Length > 0 ? history : "(none recorded)")}";
    }

    public static string BuildNudgeText(string roleName) =>
        string.Format(CultureInfo.InvariantCulture, NudgeTextTemplate, roleName);

    // ── End / detach ──

    /// <summary>Permanently ends a session: drops its resume credential, detached-LRU entry and
    /// transcript. Does NOT touch the order itself (that is <see cref="IToolExecutor"/>'s own
    /// lifetime, owned by whatever created it) -- a no-op for an unknown/null session id.</summary>
    public void EndSession(string? sessionId, string reason)
    {
        if (sessionId is null)
        {
            return;
        }
        lock (_sync)
        {
            EndSessionLocked(sessionId, reason);
        }
    }

    private void EndSessionLocked(string sessionId, string reason)
    {
        if (!_sessions.Remove(sessionId, out var record))
        {
            return;
        }
        if (record.ResumeDigest is not null)
        {
            _resumeIndex.Remove(record.ResumeDigest);
        }
        _contextMonitors.Remove(sessionId);
        RemoveFromDetachedLocked(sessionId);
        _logger?.LogInformation("Session {SessionId} ended ({Reason})", sessionId, reason);
    }

    private void RemoveFromDetachedLocked(string sessionId)
    {
        if (_detachedNodes.Remove(sessionId, out var node))
        {
            _detachedLru.Remove(node);
        }
    }

    /// <summary>
    /// Port of detach_session: called when a client socket closes. A no-op if this session was
    /// already ended (idle close raced the close handler) or taken over by a resume (the stale
    /// socket's own close handler reaching here after the 4002 supersede) -- both detected by
    /// <paramref name="ws"/> no longer being the session's own attached socket. Otherwise holds the
    /// order for the grace period (bounded by the remaining idle budget), evicting the oldest
    /// detached session once <see cref="SessionsConfig.MaxDetached"/> is exceeded.
    /// </summary>
    public void Detach(WebSocket ws, string sessionId, string reason)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var record) || !ReferenceEquals(record.AttachedSocket, ws))
            {
                return;
            }
            record.AttachedSocket = null;

            if (!_config.ResumeEnabled || _config.GraceSeconds <= 0)
            {
                EndSessionLocked(sessionId, $"{reason}; resume disabled");
                return;
            }

            var now = _timeProvider.GetUtcNow();
            record.DetachedAt = now;
            RemoveFromDetachedLocked(sessionId);
            _detachedNodes[sessionId] = _detachedLru.AddLast(sessionId);

            var idleDeadline = record.LastActivity + TimeSpan.FromSeconds(_config.IdleTimeoutSeconds);
            var expires = Min(now + TimeSpan.FromSeconds(_config.GraceSeconds), idleDeadline);
            if (expires <= now)
            {
                EndSessionLocked(sessionId, $"{reason}; idle budget exhausted");
                return;
            }
            _logger?.LogInformation(
                "Session {SessionId} detached ({Reason}); holding order for {Seconds:F0}s",
                sessionId, reason, (expires - now).TotalSeconds);

            while (_detachedLru.Count > Math.Max(_config.MaxDetached, 0))
            {
                var oldest = _detachedLru.First!.Value;
                EndSessionLocked(oldest, "evicted: max_detached reached");
            }
        }
    }

    // ── Idle / grace sweep ──

    /// <summary>Port of sweep_detached: ends every detached session whose grace hold or idle
    /// budget has run out. Returns the number ended (diagnostics/tests only).</summary>
    public int SweepDetached()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var expired = _detachedLru.Where(sid =>
            {
                var record = _sessions[sid];
                var idleDeadline = record.LastActivity + TimeSpan.FromSeconds(_config.IdleTimeoutSeconds);
                var expires = Min(record.DetachedAt!.Value + TimeSpan.FromSeconds(_config.GraceSeconds), idleDeadline);
                return now >= expires;
            }).ToList();
            foreach (var sid in expired)
            {
                EndSessionLocked(sid, "grace hold expired");
            }
            return expired.Count;
        }
    }

    /// <summary>
    /// Port of close_idle_sessions: ends every ATTACHED session idle beyond the timeout BEFORE
    /// closing its socket with 4000 -- so the socket's own close handling (RealtimeProcessor's
    /// <c>Detach</c> call in its <c>finally</c> block) finds the session already gone and does
    /// nothing, exactly like Python's own ordering comment. <see cref="WebSocket.CloseOutputAsync"/>
    /// (not <see cref="WebSocket.CloseAsync"/>) is used deliberately: this runs from the background
    /// sweep task while the session's OWN relay loop may still have a <c>ReceiveAsync</c> pending on
    /// the very same socket, and (like <see cref="Backend.Realtime.WebSocketFrameReader"/>'s own
    /// 1009 path) sending the close frame and returning immediately -- without also waiting here for
    /// the peer's close handshake reply -- avoids contending with that pending receive.
    /// </summary>
    public async Task CloseIdleSessionsAsync(CancellationToken ct)
    {
        List<(WebSocket Socket, string SessionId)> idle;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            idle = _sessions.Values
                .Where(r => r.AttachedSocket is not null &&
                            now - r.LastActivity > TimeSpan.FromSeconds(_config.IdleTimeoutSeconds))
                .Select(r => (r.AttachedSocket!, r.SessionId))
                .ToList();
            foreach (var (_, sessionId) in idle)
            {
                EndSessionLocked(sessionId, IdleCloseReason);
            }
        }

        foreach (var (socket, sessionId) in idle)
        {
            _logger?.LogWarning("Closing idle session {SessionId} (idle beyond {Seconds}s)", sessionId, _config.IdleTimeoutSeconds);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync((WebSocketCloseStatus)IdleCloseCode, IdleCloseReason, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort: the peer may have already torn the connection down.
            }
        }

        SweepDetached();
    }

    /// <summary>Background loop (started once from Program.cs, cancelled at app shutdown): scans
    /// for idle/expired sessions every <see cref="SessionsConfig.SweepIntervalSeconds"/>. Mirrors
    /// Python's <c>_idle_check_loop</c>; one process-wide loop, not per-connection, so it is not a
    /// candidate for the CTS-identity cancellation pattern the per-connection timers use.</summary>
    public async Task RunSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CloseIdleSessionsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Idle/grace sweep error");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.SweepIntervalSeconds), _timeProvider, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
