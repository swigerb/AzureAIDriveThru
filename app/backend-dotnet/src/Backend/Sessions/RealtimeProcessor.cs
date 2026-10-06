using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Shared;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Port of app/backend/rtmt.py's <c>RTMiddleTier</c> (issue #13, docs/persona-architecture.md
/// section 7). Owns the whole bidirectional browser &lt;-&gt; Azure OpenAI Realtime GA relay for one
/// session: upstream connect, bootstrap session.update, greeting gate, the two per-direction
/// relay loops (session.update translation/voice-lock/echo-suppression/barge-in on the way up,
/// tool-call dispatch/session echo/round-trip-token/rate-limit notice on the way down), and tool
/// execution through <see cref="IToolExecutor"/> (the #13/#14 coordination seam). Program.cs's
/// <c>toolExecutorFactory</c> builds one session-bound <c>SessionToolExecutor</c> per connection
/// once persona binding resolves (#14/PR #149); <see cref="StubToolExecutor"/> remains available
/// as a plumbing-only fallback (e.g. tests that don't care about tool behaviour).
///
/// <see cref="RunSessionAsync"/> is called directly by Program.cs's `/realtime` handler after
/// <c>AcceptWebSocketAsync</c>, bypassing <see cref="SessionActor"/>'s generic mailbox entirely --
/// the realtime pipeline needs to WRITE to the browser socket (tool responses, session echoes,
/// round-trip tokens, the greeting itself), which the mailbox/`ProcessAsync` shape has no way to
/// do. This is fully additive: <see cref="IPipelineProcessor"/>, <see cref="SessionActor"/> and
/// <see cref="SessionEvent"/> are unchanged, and <see cref="ProcessAsync"/> remains a stub --
/// nothing posts to this processor's mailbox for the "realtime" pipeline any more.
///
/// Deliberate scope cuts from rtmt.py, documented in docs/dotnet_mapping.md: the tool
/// failure-cap ladder (`_ToolFailureTracker`), turn recording, and the fast-path regex/marker-substring
/// optimisations (every frame is fully JSON-parsed instead). Session resume/rehydration itself --
/// `extension.resume`, the 4002 supersede-close, and the 4000 idle-timeout close -- needed a real
/// session registry and landed with #15 (PR #244); this class and <see cref="SessionManager"/> now
/// implement all three. Context-window monitoring (<see cref="ContextMonitor"/>) landed in the
/// issue #13 tail: every `ctx_monitor.add_content` call site in rtmt.py (session.update
/// instructions/tools, tool call args/result, response output text/transcript, greeting,
/// resume-nudge, and rehydration text) has a matching <see cref="SessionManager.GetContextMonitor"/>
/// call here, with the sole exception of the verbose-only user-transcript tracking site, which is
/// intentionally not ported since it only ever fires in Python when verbose debug logging -- itself
/// a separate, still-deferred scope cut -- is enabled (see the
/// `conversation.item.input_audio_transcription.completed` case below for the full reasoning).
/// Issue #13 Wave 4 closed the rate-limit retry ladder scope cut: `rate_limit.py`'s
/// `RateLimitRecovery` is now ported verbatim as <see cref="RateLimitRecovery"/>, wired at the
/// same seams as Python (response.created/response.done/error/guest-speech/external
/// response.create/teardown) -- see that class's own doc comment for the full algorithm and the
/// one deliberate behavioural difference from Python (lock-protected scheduling, needed only
/// because this port runs two genuinely concurrent relay loops where asyncio has one). A
/// guest-initiated `extension.end_session` (1000/"session_ended") needs none of that registry
/// state, so it *is*
/// implemented here (issue #13's carried-over S1.2 transport acceptance). ADR-002's Entra auth to
/// `/realtime` (#147) is the INBOUND browser-facing check on Program.cs's pre-upgrade gate and is
/// unrelated to this file: the upstream auth header chosen in <see cref="ResolveUpstreamAuthHeaderAsync"/>
/// below (api-key, or a managed-identity bearer token when no key is configured -- PR #140 R5) is
/// the OUTBOUND call to the Azure OpenAI realtime endpoint itself and applies regardless of #147.
/// </summary>
internal sealed class RealtimeProcessor : IPipelineProcessor
{
    /// <summary>Port of app/backend/session_manager.py's <c>SESSION_ENDED_CLOSE_REASON</c> --
    /// paired with the standard <see cref="WebSocketCloseStatus.NormalClosure"/> (1000) code for a
    /// guest-initiated <c>extension.end_session</c>.</summary>
    private const string SessionEndedCloseReason = "session_ended";

    /// <summary>Issue #338: kept here (not just moved to <see cref="FramePump.SupersededCloseTimeout"/>)
    /// so every existing external reference (CascadeProcessor, Backend.Tests) keeps compiling
    /// unchanged -- same value, same instance, just forwarded.</summary>
    internal static readonly TimeSpan SupersededCloseTimeout = FramePump.SupersededCloseTimeout;

    private readonly ModelCatalog _catalog;
    private readonly string _defaultDeployment;
    private readonly string _upstreamEndpoint;
    private readonly string _upstreamApiKey;
    private readonly IUpstreamBearerTokenProvider? _bearerTokenProvider;
    private readonly RealtimeSessionConfig _sessionConfig;
    private readonly IReadOnlyDictionary<string, PromptLoader> _promptLoaders;
    private readonly IToolExecutor _toolExecutor;
    private readonly Func<Persona, PromptLoader?, string?, IToolExecutor>? _toolExecutorFactory;
    private readonly IReadOnlySet<string> _allowedVoices;
    private readonly double _echoCooldownSeconds;
    private readonly double _greetingTimeoutSeconds;
    private readonly ILogger<RealtimeProcessor> _logger;
    private readonly ILogger<RateLimitRecovery> _rateLimitLogger;
    private readonly ILogger<NudgeScheduler> _nudgeLogger;
    private readonly TimeProvider _timeProvider;
    private readonly RateLimitSettings _rateLimitSettings;
    private readonly SessionManager? _sessionManager;
    private readonly Configuration.ConnectionConfig _connectionConfig;

    public RealtimeProcessor(
        ModelCatalog catalog,
        RealtimeProcessorOptions options,
        RealtimeProcessorDependencies dependencies)
    {
        _catalog = catalog;
        _defaultDeployment = options.DefaultDeployment;
        _upstreamEndpoint = options.UpstreamEndpoint;
        _upstreamApiKey = options.UpstreamApiKey;
        _bearerTokenProvider = dependencies.BearerTokenProvider;
        _sessionConfig = options.SessionConfig;
        _promptLoaders = dependencies.PromptLoaders;
        _toolExecutor = dependencies.ToolExecutor;
        _toolExecutorFactory = dependencies.ToolExecutorFactory;
        _allowedVoices = options.AllowedVoices;
        _echoCooldownSeconds = options.EchoCooldownSeconds;
        _greetingTimeoutSeconds = options.GreetingTimeoutSeconds;
        _logger = dependencies.Logger;
        _rateLimitLogger = dependencies.RateLimitLogger;
        _nudgeLogger = dependencies.NudgeLogger;
        // Issue #13 Wave 4: the rate-limit retry ladder's own config (resilience.rate_limit in
        // config.yaml) -- defaults to the Python-matching shipped defaults if the caller (normally
        // Program.cs, via RateLimitSettings.FromAppConfig) doesn't supply one.
        _rateLimitSettings = options.RateLimitSettings;
        // Issue #13 Wave 2: every time-dependent piece of the relay (echo-suppression cooldowns,
        // the greeting-gate timeout, the "loop time" ShouldSuppressAudio/OnAudioDone/OnResponseDone
        // read) is driven from this one clock, so a test can swap in a FakeTimeProvider instead of
        // waiting on real wall-clock delays. Defaults to TimeProvider.System in production.
        _timeProvider = dependencies.TimeProvider ?? TimeProvider.System;
        // Issue #15: the session registry (resume/rehydration/idle/grace/nudge). Left null by
        // every existing caller/test that doesn't pass one -- the whole feature is then fully
        // inert: extension.resume is silently swallowed (the pre-#15 scope-cut behaviour) and no
        // extra session_metadata field/first-frame wait is introduced, so nothing built against
        // this constructor before #15 changes behaviour.
        _sessionManager = dependencies.SessionManager;
        // Issue #13 tail: config.yaml's `connection` section (ws_heartbeat_seconds/
        // ws_connect_timeout_total/ws_connect_timeout_connect) -- see ConnectionConfig's own doc
        // comment for the full heartbeat/connect-timeout mapping.
        _connectionConfig = options.ConnectionConfig;
    }

    public string PipelineName => "realtime";

    public ResolvedModel ResolveModel(Persona persona, string? requestedModelId) =>
        ModelDispatch.ResolveRealtimeModel(persona, requestedModelId, _catalog, _defaultDeployment, _logger);

    /// <summary>Nothing posts to this processor's mailbox -- <see cref="RunSessionAsync"/> owns
    /// the realtime pipeline's whole event loop directly (see class doc).</summary>
    public Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Per-connection mutable state -- the C# equivalent of the local variables
    /// rtmt.py's <c>_forward_messages</c> closes over (<c>voice</c>, <c>assistant_audio_seen</c>,
    /// <c>greeting_sent</c>, <c>tools_pending</c>, <c>session_configured</c>, ...).</summary>
    internal sealed class RealtimeSessionState
    {
        public required string SessionId { get; init; }
        public required string Voice { get; set; }
        public required EchoSuppressor Echo { get; init; }
        public required RateLimitRecovery RateLimit { get; init; }
        public required ToolFailureTracker ToolFailures { get; init; }
        public required SessionUpdateGuard Guard { get; init; }
        // Rick's #244 review (issue 5): settable (not init-only) so a successful resume can swap
        // in the ORIGINAL session's shared SessionIdentifiers instance (see
        // HandleResumeFirstFrameAsync), replacing the fresh one this connection minted before it
        // knew whether it would end up resuming.
        public required SessionIdentifiers Identifiers { get; set; }
        public bool AssistantAudioSeen { get; set; }
        public bool GreetingSent { get; set; }
        public bool SessionMetadataSent { get; set; }
        public Dictionary<string, string> ToolsPending { get; } = new();
        public TaskCompletionSource<bool> SessionConfigured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // ── Issue #15: session registry / resume / idle / grace / nudge ──
        // Mutable (not init-only) because a successful resume swaps it for the PERSISTED
        // IToolExecutor from the session this connection resumed, so order state survives a
        // detach/reconnect instead of starting over empty (see RealtimeProcessor's class doc).
        public required IToolExecutor ToolExecutor { get; set; }
        /// <summary>Rick's #244 round-2 review, issue 1: this connection's OWN
        /// <see cref="SupersededFlag"/> -- created once per connection (fresh-or-
        /// resumed alike, same as <see cref="Identifiers"/>) and handed to
        /// <see cref="SessionManager.CreateSession"/>/<see cref="SessionManager.TryResume"/> as
        /// <c>attachedSupersededFlag</c> so a LATER resume by some other connection can mark THIS
        /// connection's own instance superseded. See that class's doc comment for why this exists
        /// alongside (not instead of) cancelling the linked CTS.</summary>
        public SupersededFlag Superseded { get; } = new();
        /// <summary>This connection's OWN session id in the registry -- the provisional id it was
        /// created with (<see cref="SessionId"/>), UNLESS a resume succeeds, in which case it
        /// becomes the resumed session's own (older) id. Every <see cref="SessionManager"/> call
        /// made for the lifetime of this connection (touch-activity, detach, ...) must use this,
        /// never <see cref="SessionId"/> directly, once a resume may have happened.</summary>
        public string EffectiveSessionId { get; set; } = "";
        /// <summary>Set true only by a successful resume whose own <c>conversation_started</c> was
        /// true (issue #181): the next client-authored <c>session.update</c> this connection
        /// forwards arms <see cref="Nudge"/> exactly once, then this is cleared.</summary>
        public bool NudgeArmEligible { get; set; }
        public NudgeScheduler? Nudge { get; set; }
        /// <summary>Resolved exactly once, by whichever happens first: this connection's own first
        /// client frame (resume or not), or the first-frame-timeout fallback. <c>true</c> means a
        /// resume was accepted (<see cref="ResumeAnnounce"/> carries the outcome to announce);
        /// <c>false</c> means "fresh" (a plain <c>extension.session_metadata</c>, same as every
        /// connection before #15). Never created/awaited at all when no <see cref="SessionManager"/>
        /// was injected -- see that field's own doc comment.</summary>
        public TaskCompletionSource<bool> FirstFrameDecision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ResumeOutcome? ResumeAnnounce { get; set; }
        /// <summary>Port of rtmt.py's own <c>announced</c> nonlocal: set true once this socket's
        /// first-frame decision has been made and its own resume-id baton handed to the browser --
        /// whether that happened via a fresh-connection <c>extension.session_metadata</c>
        /// (`AnnounceAfterFirstFrameDecisionAsync`'s "fresh" branch) or a successfully
        /// resumed connection's <c>extension.session_resumed</c> (both mirror rtmt.py's
        /// <c>announce_fresh()</c> and <c>handle_resume()</c>, which both set the nonlocal). Once
        /// true, a later stray (non-first-frame) <c>extension.resume</c> attempt on this same
        /// socket gets a rotated-id re-announce after its rejection -- exactly mirroring Python's
        /// <c>reject_late_resume()</c>, which re-announces for ANY connection that already holds a
        /// baton, fresh or resumed alike.</summary>
        public bool MetadataAnnounced { get; set; }
    }

    /// <summary>
    /// Owns one session's whole upstream connection and bidirectional relay, from the moment the
    /// browser's WebSocket has been accepted until either side disconnects. Mirrors rtmt.py's
    /// <c>_forward_messages</c> end to end (session bootstrap, greeting gate, tool dispatch,
    /// echo suppression/barge-in, session echoes, round-trip tokens) -- see the class doc for the
    /// scope cuts.
    ///
    /// <paramref name="menuMode"/> (issue 165): this session's own bound daypart, already
    /// validated and defaulted by Program.cs's <c>/realtime</c> handler -- <c>null</c> for a
    /// persona with no <c>features.dayparts</c>. Threaded straight into
    /// <see cref="_toolExecutorFactory"/> so <c>OrderToolExecutor</c>/<c>SearchTool</c> get it at
    /// construction time, matching how <see cref="MenuCatalog"/>/<see cref="PromptLoader"/> are
    /// already threaded -- there's no mid-session mode switching (in either backend), so a
    /// one-time constructor injection is enough.
    /// </summary>
    public async Task RunSessionAsync(
        WebSocket browserSocket,
        Persona persona,
        ResolvedModel resolvedModel,
        string sessionId,
        CancellationToken cancellationToken,
        string? menuMode = null)
    {
        var binding = ResolveSessionBinding(persona, menuMode);
        var promptLoader = binding.PromptLoader;
        var systemMessage = binding.SystemMessage;
        var voice = binding.Voice;
        var toolSchemas = binding.ToolSchemas;
        var toolExecutor = binding.ToolExecutor;
        var reasoningOverride = Overridable.Of<bool?>(resolvedModel.Reasoning);
        var deployment = string.IsNullOrEmpty(resolvedModel.Deployment) ? _defaultDeployment : resolvedModel.Deployment;

        using var upstream = new ClientWebSocket();
        // Issue #13 tail: rtmt.py's upstream ws_connect always passes
        // heartbeat=_WS_HEARTBEAT_SEC -- KeepAliveInterval is .NET's closest analog (sends/expects
        // periodic pings so a dead peer is detected rather than hanging forever). Set before
        // ConnectAsync, same as aiohttp requires heartbeat configured at connect time.
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(_connectionConfig.WsHeartbeatSeconds);
        // PR #140 R5: NOT #147 (that's the inbound Entra check on the browser-facing /realtime
        // upgrade in Program.cs) -- this picks the OUTBOUND credential for the Azure OpenAI
        // realtime endpoint itself, api-key when one is configured, else a managed-identity
        // bearer token, matching rtmt.py's DefaultAzureCredential fallback.
        try
        {
            var (headerName, headerValue) = await ResolveUpstreamAuthHeaderAsync(cancellationToken).ConfigureAwait(false);
            upstream.Options.SetRequestHeader(headerName, headerValue);

            // Rick's #280 review, item 3: rtmt.py's upstream ws_connect uses
            // aiohttp.ClientTimeout(total=ws_connect_timeout_total, connect=ws_connect_timeout_connect)
            // -- a genuine two-phase split, where a stalled TCP/TLS connect fails at the shorter
            // `connect` bound even though the overall budget is the longer `total` one. The
            // SocketsHttpHandler.ConnectTimeout below, wired through the HttpMessageInvoker overload
            // of ConnectAsync, is .NET's equivalent of that `connect` sub-phase (it only bounds the
            // TCP/TLS connect, not the WebSocket upgrade handshake that follows). The outer CTS below
            // still bounds the WHOLE call at ws_connect_timeout_total, same as before, so a slow
            // (but under 10s) connect followed by a slow upgrade still gets the full 30s budget.
            using var connectPhaseHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(_connectionConfig.WsConnectTimeoutConnectSeconds),
            };
            using var connectPhaseInvoker = new HttpMessageInvoker(connectPhaseHandler);
            using var connectTimeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_connectionConfig.WsConnectTimeoutSeconds));
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, connectTimeoutCts.Token);
            await upstream.ConnectAsync(BuildUpstreamUri(_upstreamEndpoint, deployment), connectPhaseInvoker, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.UpstreamConnectFailed(ex, deployment, sessionId);
            await CloseIfOpenAsync(browserSocket, WebSocketCloseStatus.InternalServerError, "Upstream connection failed")
                .ConfigureAwait(false);
            return;
        }

        var state = new RealtimeSessionState
        {
            SessionId = sessionId,
            Voice = voice,
            Echo = new EchoSuppressor(_echoCooldownSeconds,
                flushCt => SendTextAsync(upstream, """{"type":"input_audio_buffer.clear"}""", flushCt),
                _timeProvider),
            RateLimit = new RateLimitRecovery(
                _rateLimitSettings,
                sendUpstream: (payload, rlCt) => SendTextAsync(upstream, payload, rlCt),
                sendClient: (payload, rlCt) => SendTextAsync(browserSocket, payload.ToJsonString(), rlCt),
                timeProvider: _timeProvider,
                sessionId: sessionId,
                logger: _rateLimitLogger),
            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2"): one
            // tracker per connection, same lifetime as RateLimit/Echo above -- mirrors rtmt.py's
            // per-connection `tool_failures = _ToolFailureTracker()`.
            ToolFailures = new ToolFailureTracker(),
            Guard = new SessionUpdateGuard(),
            // Rick's #244 review, issue 5: an independently-minted token, NOT the internal
            // sessionId -- mirrors order_state.py's own session_token/session_id separation (two
            // different random values in two different namespaces). Persisted into
            // SessionManager (below) as the SAME object reference, so a resume hands this exact
            // instance back out instead of a fresh one restarting round_trip_index at 0 (see
            // SessionRecord.Identifiers's own doc comment).
            Identifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, sessionToken: null),
            ToolExecutor = toolExecutor,
            EffectiveSessionId = sessionId,
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        // ── Issue #15: register this connection with the session registry up front, mirroring
        // rtmt.py's own create_session(...) call before _forward_messages starts relaying any
        // client traffic. Entirely inert when no SessionManager was injected (see that field's
        // own doc comment) -- every pre-#15 caller/test keeps today's exact behaviour.
        if (_sessionManager is not null)
        {
            _sessionManager.CreateSession(
                sessionId, browserSocket, persona.Id, resolvedModel.Id, menuMode, toolExecutor, voice,
                attachedCts: linkedCts, identifiers: state.Identifiers, attachedSupersededFlag: state.Superseded);

            state.Nudge = new NudgeScheduler(
                _sessionManager.Config.NudgeAfterSeconds,
                sendNudgeAsync: async nudgeCt =>
                {
                    var nudgeItem = new JsonObject
                    {
                        ["type"] = "conversation.item.create",
                        ["item"] = new JsonObject
                        {
                            ["id"] = MiddleTierItemIds.NewId(),
                            ["type"] = "message",
                            ["role"] = "system",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = SessionManager.BuildNudgeText(persona.RoleName),
                            }),
                        },
                    };
                    var nudgeItemJson = nudgeItem.ToJsonString();
                    await SendTextAsync(upstream, nudgeItemJson, nudgeCt).ConfigureAwait(false);
                    await SendTextAsync(upstream, """{"type":"response.create"}""", nudgeCt).ConfigureAwait(false);
                    // Issue #13 tail: track the nudge in the context window, mirroring rtmt.py's
                    // ctx_monitor.add_content(nudge) in the resume-nudge timer callback.
                    _sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(nudgeItemJson);
                },
                isRateLimitBusy: () => state.RateLimit.Busy,
                sessionConfigured: state.SessionConfigured.Task,
                timeProvider: _timeProvider,
                sessionId: sessionId,
                logger: _nudgeLogger);

            // "Decide fresh" fallback: if the browser's very first frame never arrives (or isn't
            // extension.resume) within first_frame_timeout_seconds, unblock the deferred
            // session_metadata/session_resumed announce so a slow/silent client isn't stuck
            // forever. Rick's #244 review (issue 2): a real first frame racing in AFTER this
            // fires is a LATE resume, not a no-op -- TrySetResult being idempotent only protects
            // the TASK'S VALUE, not HandleResumeFirstFrameAsync's side effects (it mutates the
            // shared SessionManager registry and this connection's own state regardless of
            // whether the TCS it eventually calls TrySetResult on is already resolved). The
            // checkingFirstFrame branch below now checks FirstFrameDecision.Task.IsCompleted
            // BEFORE calling HandleResumeFirstFrameAsync for exactly this reason, mirroring
            // rtmt.py's `if not resume_decided.is_set()` guard that runs BEFORE handle_resume is
            // ever invoked (never inside it) -- once this fallback has fired, ANY subsequent
            // extension.resume (first frame or not) is rejected via reject_late_resume.
            var firstFrameTimeoutSeconds = _sessionManager.Config.FirstFrameTimeoutSeconds;
            if (firstFrameTimeoutSeconds > 0)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(firstFrameTimeoutSeconds), _timeProvider, ct)
                    .ContinueWith(
                        t =>
                        {
                            if (!t.IsCanceled)
                            {
                                state.FirstFrameDecision.TrySetResult(false);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
            }
            else
            {
                state.FirstFrameDecision.TrySetResult(false);
            }
        }

        // ── Local helpers (closures over browserSocket/upstream/state/toolSchemas/...) ──────────
        // Mirrors rtmt.py's own nested-function style inside _forward_messages (send_greeting_once
        // etc. are themselves local closures there too), kept as one method for the same reason:
        // the whole relay is one session's worth of tightly-coupled sequential state.

        // Issue #338: the greeting gate itself is its own collaborator now (GreetingGate.cs) --
        // same logic, same log templates/EventIds, just constructed here with this connection's
        // own upstream/promptLoader/state/timing so SendOnceAsync below is a pure delegate.
        var greetingGate = new GreetingGate(
            upstream, promptLoader, state, _sessionManager, _timeProvider, _greetingTimeoutSeconds, sessionId, _logger!, ct);

        // Issue #338: tool-call dispatch (HandleToolCallDoneAsync) is its own collaborator now
        // (ToolCallDispatcher.cs) -- same logic, same log templates/EventIds, constructed here
        // with this connection's own upstream/browserSocket/state so the call site below is a
        // pure delegate.
        var toolCallDispatcher = new ToolCallDispatcher(upstream, browserSocket, state, _sessionManager, sessionId, _logger!, ct);

        JsonObject BuildVoiceUpdateFrame(string newVoice) => new()
        {
            ["type"] = "session.update",
            ["event_id"] = EventIds.NewEventId("sonic_voice"),
            ["session"] = GaSessionTranslator.ToGaSession(new JsonObject { ["voice"] = newVoice }),
        };

        // Issue #15: rejects an extension.resume that arrives too late to possibly win the
        // session -- either because it isn't structurally this connection's first frame at all
        // (HandleClientExtensionMessageAsync's call site), or because it IS the first frame but
        // state.FirstFrameDecision was already resolved by the timeout fallback racing ahead of
        // it (RelayBrowserToUpstreamAsync's checkingFirstFrame call site, Rick's #244 review
        // issue 2) -- both are the exact same "resume_decided already set" case Python's
        // reject_late_resume handles, so both now share this one rejection path instead of the
        // first-frame race silently falling through to HandleResumeFirstFrameAsync's destructive
        // (registry-mutating) resume logic.
        async Task RejectLateResumeAsync(string logReason)
        {
            _logger?.DroppedLateResume(logReason, sessionId);
            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "extension.resume_rejected",
                ["reason"] = "not_first_frame",
            }.ToJsonString(), ct).ConfigureAwait(false);

            // rtmt.py's reject_late_resume: the browser drops its stored id on ANY
            // rejection, so re-announce this socket's own session (with a rotated id) --
            // for any connection that already holds a resume-id baton, fresh or resumed
            // alike (MetadataAnnounced is set by both paths; see its doc comment). The only
            // connections that never set it are ones torn down before their first-frame
            // decision was ever reached.
            if (state.MetadataAnnounced)
            {
                await SendFreshSessionMetadataAsync().ConfigureAwait(false);
            }
        }

        async Task HandleClientExtensionMessageAsync(string msgType, JsonObject message)
        {
            _sessionManager?.TouchActivity(state.EffectiveSessionId);

            if (msgType == "extension.resume")
            {
                if (_sessionManager is not null)
                {
                    // A resume attempt that isn't this connection's own first frame -- #15's
                    // registry is live, but ResumeHandshakeTests' late-resume scenario requires a
                    // FRESH rejection (never the connection's original/already-settled decision)
                    // and the session itself must stay open, not close.
                    await RejectLateResumeAsync("not this connection's first frame").ConfigureAwait(false);
                }
                // _sessionManager is null: the whole resume feature isn't wired in for this
                // instance -- keep the pre-#15 behaviour of silently consuming it.
                return;
            }

            if (await TryHandleSessionOverrideExtensionMessageAsync(msgType, message, state.EffectiveSessionId, browserSocket, ct).ConfigureAwait(false))
            {
                return;
            }

            if (msgType != "extension.set_voice")
            {
                // set_verbose_logging/set_log_to_file (#13 scope cuts, see class doc) -- consumed
                // silently, never forwarded upstream. extension.end_session is handled by the
                // caller (RelayBrowserToUpstreamAsync), not here, since it needs to break the
                // relay loop rather than just fall through to the next frame.
                return;
            }
            var candidate = GetString(message, "voice");
            var newVoice = ClientServerFilter.SanitizeVoice(candidate, _allowedVoices);
            if (newVoice is null)
            {
                _logger?.DroppedSetVoice(candidate, sessionId);
                return;
            }
            state.Voice = newVoice;
            _sessionManager?.SetVoice(state.EffectiveSessionId, newVoice);
            if (state.AssistantAudioSeen)
            {
                // GA would reject this outright (cannot_update_voice) and take tools/instructions
                // down with it -- defer to the next unlocked session.update, same as Python.
                _logger?.VoiceDeferredToNextConversation(newVoice, sessionId);
                return;
            }
            var voiceUpdate = state.Guard.Track(BuildVoiceUpdateFrame(newVoice).ToJsonString());
            await SendTextAsync(upstream, voiceUpdate, ct).ConfigureAwait(false);
        }

        // Issue #15: handles extension.resume when it IS this connection's own first frame (see
        // the isFirstFrame gate in RelayBrowserToUpstreamAsync -- a late resume is rejected there
        // via HandleClientExtensionMessageAsync instead, never here). Resolves
        // state.FirstFrameDecision exactly once either way, so AnnounceAfterFirstFrameDecisionAsync
        // (armed from the session.created handler) can proceed.
        //
        // Rick's #244 review (issue 2): callers MUST check state.FirstFrameDecision.Task.IsCompleted
        // before calling this -- it is never safe to call once that's already true (the timeout
        // fallback got there first), since every line below mutates the shared SessionManager
        // registry and this connection's own state unconditionally, regardless of whether
        // TrySetResult on an already-resolved TCS is a value no-op. See the class-level comment on
        // the timeout fallback above for the full rtmt.py parity reasoning.
        async Task HandleResumeFirstFrameAsync(JsonObject message)
        {
            var presentedId = GetString(message, "resume_id");
            var outcome = _sessionManager!.TryResume(
                browserSocket, presentedId, persona.Id, resolvedModel.Id, menuMode, sessionId, linkedCts,
                attachedSupersededFlag: state.Superseded);
            if (!outcome.Accepted)
            {
                _logger?.ExtensionResumeRejected(outcome.Reason, sessionId);
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.resume_rejected",
                    ["reason"] = outcome.Reason,
                }.ToJsonString(), ct).ConfigureAwait(false);
                state.FirstFrameDecision.TrySetResult(false);
                return;
            }

            _logger?.SessionResumedWithId(outcome.SessionId, sessionId);
            state.EffectiveSessionId = outcome.SessionId!;
            state.ToolExecutor = outcome.ToolExecutor!;
            state.Voice = outcome.Voice!;
            // Rick's #244 review (issue 5): adopt the ORIGINAL session's identifiers object (same
            // reference, so its round_trip_index keeps counting up from where the prior
            // connection left off) instead of leaving state.Identifiers at the brand-new one this
            // connection minted before knowing whether it would end up resuming anything.
            if (outcome.Identifiers is { } originalIdentifiers)
            {
                state.Identifiers = originalIdentifiers;
            }
            // Issue #181: conversation_started gates whether this resume rehydrates silently
            // (greeting already happened -- GreetingSent=true suppresses GreetingGate.SendOnceAsync
            // entirely) or re-greets as if fresh. Only a rehydrating resume is nudge-eligible; a
            // resume before the greeting ever fired still greets normally and must not nudge on
            // top of that.
            state.GreetingSent = outcome.ConversationStarted;
            state.NudgeArmEligible = outcome.ConversationStarted;
            state.ResumeAnnounce = outcome;
            state.FirstFrameDecision.TrySetResult(true);

            if (outcome.StaleWs is { } staleWs)
            {
                // Rick's #244 round-2 review, issue 1: this USED to await the close-output send
                // and only THEN cancel outcome.StaleCts, all inline in THIS (the new, winning)
                // connection's own call stack -- reasoned (previous comment, now wrong) that
                // CloseOutputAsync "can never hang" because it never waits for the peer's
                // handshake reply. That is true of the HANDSHAKE wait, but CloseOutputAsync is
                // still a SEND, and a send can block on a half-open network-switch where the stale
                // socket's outbound isn't draining (e.g. assistant audio was streaming when the
                // network died) or its send lock is held -- Rick proved it with a probe over
                // exactly such a non-draining transport. Awaited inline here, that blocks not just
                // the stale connection's teardown but THIS connection's own first-frame handling
                // (and everything downstream of it), so the new socket forwards nothing -- no
                // session.update, no audio -- until an eventual 4000 idle close deletes the order,
                // even though the guest already saw session_resumed.
                //
                // Fixed the same way Python does it (rtmt.py's background `_close_superseded`,
                // ~1026): fire the close-and-cancel off as a background task with its own short
                // timeout, so it can never block this connection's own processing, while the 4002
                // close is still attempted promptly on a best-effort basis. The tool-dispatch race
                // this ordering previously depended on (StaleCts cancelled before another send
                // could race it) is now closed by state.Superseded instead -- TryResume marks the
                // STALE connection's own SupersededFlag synchronously under its lock the instant it
                // captures staleWs, independent of how long this background close later takes, and
                // HandleToolCallDoneAsync checks that flag (not StaleCts) before ever dispatching a
                // tool, because OrderToolExecutor.ExecuteAsync is synchronous, ignores its own
                // CancellationToken, and mutates OrderState, which isn't thread-safe -- a promptly
                // cancelled StaleCts alone was never enough to stop an already-started dispatch.
                Task.Run(
                    () => CloseSupersededStaleConnectionAsync(staleWs, outcome.StaleCts, SupersededCloseTimeout, _logger!),
                    CancellationToken.None).FireAndForget(_logger, nameof(CloseSupersededStaleConnectionAsync));
            }
        }

        (JsonObject? Forwarded, string? SentType) ProcessClientMessage(JsonObject message, bool hooksEnabled)
        {
            var filtered = ClientServerFilter.Filter(message, hooksEnabled);
            if (filtered is null)
            {
                return (null, null);
            }
            var msgType = GetString(filtered, "type") ?? "";
            if (msgType != "session.update")
            {
                return (filtered, msgType);
            }
            if (filtered["session"] is not JsonObject clientSession)
            {
                return (null, null);
            }
            var sessionIn = new JsonObject();
            foreach (var key in ClientServerFilter.ClientSessionKeys)
            {
                if (clientSession.TryGetPropertyValue(key, out var value) && value is not null)
                {
                    sessionIn[key] = value.DeepClone();
                }
            }
            if (clientSession.TryGetPropertyValue("turn_detection", out var turnDetectionNode))
            {
                var sanitized = ClientServerFilter.SanitizeTurnDetection(turnDetectionNode);
                sessionIn["turn_detection"] = sanitized is not null
                    ? sanitized
                    : (JsonObject)RealtimeSessionBuilder.BootstrapClientSession()["turn_detection"]!.DeepClone();
            }
            var session = RealtimeSessionBuilder.BuildSession(
                _sessionConfig, sessionIn, toolSchemas,
                voiceLocked: state.AssistantAudioSeen,
                voice: Overridable.Of<string?>(state.Voice),
                systemMessage: Overridable.Of<string?>(systemMessage),
                reasoningOverride: reasoningOverride);
            filtered["session"] = session;
            state.Guard.Stamp(filtered);
            // Issue #13 tail: track the system message + tool schemas this session.update just
            // injected in the context window -- mirrors rtmt.py's _process_message_to_server
            // ctx_monitor.add_content(session.get("instructions", "")) / per-tool-schema calls.
            var ctxMonitorForUpdate = _sessionManager?.GetContextMonitor(state.EffectiveSessionId);
            if (ctxMonitorForUpdate is not null)
            {
                ctxMonitorForUpdate.AddContent(GetString(session, "instructions"));
                if (session["tools"] is JsonArray injectedTools)
                {
                    foreach (var toolSchema in injectedTools)
                    {
                        ctxMonitorForUpdate.AddContent(toolSchema?.ToJsonString());
                    }
                }
            }
            return (filtered, msgType);
        }

        async Task RelayBrowserToUpstreamAsync()
        {
            // Issue #15: only the very first TEXT frame this connection forwards gets the
            // resume-or-fresh decision treatment; everything after goes through the pre-#15 code
            // paths completely unchanged. Entirely skipped when no SessionManager was injected.
            var isFirstFrame = _sessionManager is not null;

            while (!ct.IsCancellationRequested)
            {
                WebSocketFrame? frame;
                try
                {
                    frame = await WebSocketFrameReader.ReadMessageAsync(browserSocket, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    _logger?.BrowserWebSocketError(ex, sessionId);
                    break;
                }
                if (frame is null)
                {
                    await CloseIfOpenAsync(browserSocket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                    break;
                }
                if (frame.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var checkingFirstFrame = isFirstFrame;
                isFirstFrame = false;

                if (!checkingFirstFrame)
                {
                    var fastPath = TryAppendFastPath(frame.Payload, state.Echo);
                    if (fastPath.IsMatch)
                    {
                        // Issue #13 Wave 2: skips the JSON parse, allow-list rebuild and re-serialize
                        // entirely for the one exact frame shape addUserAudio() sends (~10/sec while
                        // the guest is talking). Echo-suppression gating still runs first, exactly as
                        // it does on the slow path below -- the fast path only ever changes HOW a
                        // genuine, unsuppressed append frame gets forwarded, never WHETHER it does.
                        await ForwardFastPathAudioAsync(fastPath, frame.Payload, upstream, sessionId, ct).ConfigureAwait(false);
                        continue;
                    }
                }
                // checkingFirstFrame skips the fast path unconditionally: a real extension.resume
                // can never match that one fixed shape anyway, and the first frame always needs a
                // real parse regardless, so there is no behavioural loss, only one skipped (and
                // guaranteed-to-fail) shape check.

                JsonObject message;
                string msgType;
                try
                {
                    // Strict parse: see RelayJson.ParseRelayFrame's doc for why
                    // AllowDuplicateProperties = false matters (a duplicate key at any depth
                    // throws JsonException immediately here instead of a deferred ArgumentException
                    // from indexing into the object later, which for a nested duplicate happened
                    // outside this try -- inside ProcessClientMessage -- faulting the loop outright).
                    // The ArgumentException catch stays as defense-in-depth for any other
                    // lazy-dictionary access this doesn't cover.
                    message = RelayJson.ParseRelayFrame(frame.Payload);
                    msgType = GetString(message, "type") ?? "";
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    _logger?.DroppedMalformedClientFrame(sessionId);
                    if (checkingFirstFrame)
                    {
                        state.FirstFrameDecision.TrySetResult(false);
                    }
                    continue;
                }

                if (checkingFirstFrame)
                {
                    if (msgType == "extension.resume")
                    {
                        // Rick's #244 review (issue 2): this frame is structurally first, but the
                        // first-frame TIMEOUT may have already fired and resolved the decision
                        // (slow/stalled frame racing against the fallback timer -- see that
                        // fallback's own doc comment above). Mirrors rtmt.py's
                        // `if not resume_decided.is_set()` guard, which runs BEFORE handle_resume
                        // is ever called, not inside it: once already decided, this is a LATE
                        // resume regardless of its position in the stream, and must be rejected
                        // via the exact same path as a structurally-non-first-frame late resume --
                        // never routed into HandleResumeFirstFrameAsync, whose side effects on the
                        // shared SessionManager registry and this connection's own state are not
                        // safe to run twice (or at all) once the decision is already settled.
                        if (state.FirstFrameDecision.Task.IsCompleted)
                        {
                            await RejectLateResumeAsync("first-frame timeout already decided").ConfigureAwait(false);
                            continue;
                        }

                        try
                        {
                            await HandleResumeFirstFrameAsync(message).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger?.ErrorProcessingExtensionResume(ex, sessionId);
                            state.FirstFrameDecision.TrySetResult(false);
                        }
                        continue;
                    }

                    // Any other first frame (almost always a plain session.update -- the browser's
                    // normal non-resume bootstrap) decides "fresh" immediately, before falling
                    // through to its own ordinary handling below -- satisfies
                    // ResumeHandshakeTests' non-resume-first-frame timing assertion (must decide
                    // well under the first-frame-timeout fallback).
                    state.FirstFrameDecision.TrySetResult(false);
                }

                try
                {
                    if (msgType.Length == 0)
                    {
                        _logger?.DroppedClientFrameMissingType(sessionId);
                        continue;
                    }

                    if (msgType == "extension.end_session")
                    {
                        // Port of rtmt.py's _forward_messages: a guest-initiated end_session closes
                        // the browser socket with the fixed 1000/"session_ended" shape immediately.
                        // Issue #15: unlike every other close, this one PERMANENTLY ends the
                        // session (deletes the order/resume credential right now) rather than
                        // detaching it with a grace window -- mirrors session_manager.py's own
                        // end_session() semantics: the same resume id must come back "unknown",
                        // never "expired", after this.
                        _logger?.GuestEndedSession(sessionId);
                        _sessionManager?.EndSession(state.EffectiveSessionId, SessionEndedCloseReason);
                        await CloseIfOpenAsync(browserSocket, WebSocketCloseStatus.NormalClosure, SessionEndedCloseReason)
                            .ConfigureAwait(false);
                        break;
                    }

                    if (msgType.StartsWith("extension.", StringComparison.Ordinal))
                    {
                        await HandleClientExtensionMessageAsync(msgType, message).ConfigureAwait(false);
                        continue;
                    }

                    if (msgType == "input_audio_buffer.append" && state.Echo.ShouldSuppressAudio(NowSeconds()))
                    {
                        continue;
                    }

                    // Port of rtmt.py's BROWSER->SERVER touch_activity call sites: every forwarded
                    // client frame EXCEPT input_audio_buffer.append (far too high-frequency to be
                    // a meaningful idle-activity signal, and already excluded above when actually
                    // suppressed -- an unsuppressed append still reaches here, so this still
                    // touches activity for genuine mic audio) keeps the idle clock from expiring a
                    // live conversation. Rick's #244 review (issue 1): this is only HALF of
                    // rtmt.py's touch_activity call sites -- it also touches on two UPSTREAM
                    // (server->client) events, input_audio_buffer.speech_started and
                    // conversation.item.input_audio_transcription.completed, handled in the
                    // server->client relay loop below (see its switch statement) precisely because
                    // a guest who is talking generates ONLY input_audio_buffer.append frames on
                    // this (browser->server) side, which deliberately never touch activity -- the
                    // upstream VAD/transcription events are the only activity signal available for
                    // a guest who is mid-speech without yet having sent anything else.
                    if (msgType != "input_audio_buffer.append")
                    {
                        _sessionManager?.TouchActivity(state.EffectiveSessionId);
                    }

                    var hooksEnabled = BackendEnvironment.Get("CONFORMANCE_TEST_HOOKS") == "1";
                    var (forwarded, sentType) = ProcessClientMessage(message, hooksEnabled);
                    if (forwarded is null)
                    {
                        continue;
                    }

                    // ForwardClientFrameAsync (dev's #252 race fix) already calls
                    // echo.OnExternalResponseCreate/rateLimit.OnExternalResponseCreate("browser")
                    // BEFORE forwarding a browser response.create upstream -- see its own doc
                    // comment. Issue #15/#181's nudge-cancel wasn't part of that merge (Nudge
                    // didn't exist on dev yet), so it's added here, after the same send, matching
                    // where it always lived relative to the send in this branch.
                    await ForwardClientFrameAsync(forwarded, sentType, state.Echo, state.RateLimit, upstream, ct)
                        .ConfigureAwait(false);
                    if (sentType == "response.create")
                    {
                        // Issue #15/#181: the guest (or the UI on the guest's behalf) asking for a
                        // response is exactly the "guest spoke" signal that cancels a pending nudge.
                        state.Nudge?.Cancel("browser response.create");
                    }

                    if (!state.GreetingSent && sentType == "session.update")
                    {
                        await greetingGate.SendOnceAsync("client-session.update").ConfigureAwait(false);
                    }

                    // Issue #181: a resumed connection only arms its nudge once ITS OWN
                    // client-authored session.update has been forwarded (the browser's
                    // mic-restart/resumeConversation() path) -- never immediately on accepting the
                    // resume itself. A resume whose client never re-arms the mic must never nudge.
                    if (state.NudgeArmEligible && sentType == "session.update")
                    {
                        state.NudgeArmEligible = false;
                        state.Nudge?.Arm();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // R3: any per-frame processing/send failure must not fault this loop and end
                    // the session silently -- log with the session id (never the payload, which
                    // may carry guest PII/order details) and move on to the next frame.
                    _logger?.ErrorProcessingClientFrame(ex, sessionId);
                }
            }
        }

        JsonObject BuildClientSessionEcho(JsonObject message)
        {
            var session = message["session"] as JsonObject;
            return new JsonObject
            {
                ["type"] = GetString(message, "type"),
                ["event_id"] = GetString(message, "event_id"),
                ["session"] = new JsonObject
                {
                    ["id"] = session?["id"]?.DeepClone(),
                    ["object"] = session?["object"]?.DeepClone(),
                    ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = state.Voice } },
                },
            };
        }

        async Task<JsonObject?> HandleErrorAsync(JsonObject message)
        {
            var err = message["error"] as JsonObject;
            var rejectedEventId = state.Guard.Correlate(message);
            if (rejectedEventId is not null)
            {
                var original = state.Guard.OriginalOf(rejectedEventId);
                if (original is not null || !state.Guard.ClaimFallback(rejectedEventId))
                {
                    _logger?.FallbackSessionUpdateAlsoRejected(
                        rejectedEventId, original, GetString(err, "code"), GetString(err, "param"), GetString(err, "message"), sessionId);
                    return message;
                }
                _logger?.SessionUpdateRejected(
                    rejectedEventId, GetString(err, "code"), GetString(err, "param"), GetString(err, "message"), sessionId);

                var rejectedPayload = state.Guard.PayloadOf(rejectedEventId);
                var param = GetString(err, "param") ?? "";
                if ((rejectedPayload.ContainsKey("reasoning") || rejectedPayload.ContainsKey("parallel_tool_calls"))
                    && (param.Length == 0
                        || param.StartsWith("session.reasoning", StringComparison.Ordinal)
                        || param.StartsWith("session.parallel_tool_calls", StringComparison.Ordinal)))
                {
                    _sessionConfig.ReasoningRejected = true;
                    _logger?.ReasoningOptionsRejected(_sessionConfig.Deployment ?? "?");
                }

                var fallback = RealtimeSessionBuilder.BuildFallbackSessionUpdate(
                    _sessionConfig, toolSchemas, voice: Overridable.Of<string?>(state.Voice),
                    systemMessage: Overridable.Of<string?>(systemMessage), reasoningOverride: reasoningOverride);
                var fallbackPayload = state.Guard.Track(fallback.ToJsonString(), fallbackOf: rejectedEventId);
                await SendTextAsync(upstream, fallbackPayload, ct).ConfigureAwait(false);
                return null;
            }

            if (await state.RateLimit.OnErrorAsync(message, ct).ConfigureAwait(false))
            {
                return null;
            }

            if (GetString(err, "code") == "response_cancel_not_active")
            {
                if (_logger is { } benignLogger && benignLogger.IsEnabled(LogLevel.Information))
                {
                    benignLogger.RealtimeApiErrorBenign(message.ToJsonString());
                }
                return message;
            }

            if (_logger is { } errorLogger && errorLogger.IsEnabled(LogLevel.Error))
            {
                errorLogger.RealtimeApiError(message.ToJsonString());
            }
            return message;
        }

        async Task<JsonObject?> HandleResponseDoneAsync(JsonObject message)
        {
            if (await state.RateLimit.OnResponseDoneAsync(message, ct).ConfigureAwait(false))
            {
                return null;
            }

            if (state.ToolsPending.Count > 0)
            {
                state.ToolsPending.Clear();
                // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2"):
                // port of rtmt.py's _ToolFailureTracker wiring -- tally this round's outcome
                // once, here, not per call, so several parallel failing tool calls in the same
                // response only count as a single failed round.
                state.ToolFailures.EndRound();
                if (state.ToolFailures.AtCap())
                {
                    // Two (or more) consecutive *failed rounds* on this connection with no guest
                    // turn in between. The FIRST response.done that reaches the cap gets one
                    // server-authored, tool-free response.create instead of nothing: the model
                    // already has the function_call_output(s) in context, so it can apologise out
                    // loud and ask the guest what to do, but tool_choice="none" stops it from
                    // calling a tool again on this turn. This frame is server-authored (never
                    // derived from browser input), so the #31 browser->upstream allow-list in
                    // ClientServerFilter is unaffected. Every response.done AFTER that one, while
                    // still at the cap with no guest turn in between, goes back to sending
                    // nothing at all -- otherwise a model (or a deterministic test double) that
                    // keeps calling tools regardless of tool_choice could ride an unbounded
                    // ladder of one-more-apology responses with zero guest input.
                    if (state.ToolFailures.ConsumeCapNotice())
                    {
                        _logger?.CappingAutoResponseCreate(state.ToolFailures.Count, sessionId);
                        await SendTextAsync(upstream, ToolFailureCapNotice.BuildMessage(promptLoader), ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _logger?.SuppressingAutoResponseCreate(state.ToolFailures.Count, sessionId);
                    }
                }
                else
                {
                    await SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
                }
            }

            var isToolCallResponse = false;
            var spokenParts = new List<string>();
            if (message["response"] is JsonObject response && response["output"] is JsonArray output)
            {
                var toolCallNames = new List<string>();
                var filtered = new JsonArray();
                foreach (var node in output)
                {
                    var type = node is JsonObject o ? GetString(o, "type") : null;
                    if (type == "function_call" && node is JsonObject fnCall)
                    {
                        isToolCallResponse = true;
                        toolCallNames.Add(GetString(fnCall, "name") ?? "?");
                    }
                    if (type is "function_call" or "function_call_output")
                    {
                        // The browser must never see the model's raw function_call (tool name +
                        // JSON arguments) or a function_call_output embedded in response.done's
                        // output array -- it already got the tool result via
                        // extension.middle_tier_tool_response (response.output_item.done above).
                        continue;
                    }
                    if (type == "message" && node is JsonObject messageItem && messageItem["content"] is JsonArray contentParts)
                    {
                        // Rick's #244 review (issue 3): rtmt.py's own record_turn("carhop", spoken)
                        // builds "spoken" by joining EVERY message-type output item's
                        // transcript-or-text content, space-separated -- do the same here so a
                        // rehydrated session's replayed history matches what was actually said.
                        foreach (var part in contentParts)
                        {
                            if (part is JsonObject partObj)
                            {
                                var text = (GetString(partObj, "transcript") ?? GetString(partObj, "text") ?? "").Trim();
                                if (text.Length > 0)
                                {
                                    spokenParts.Add(text);
                                }
                            }
                        }
                    }
                    filtered.Add(node?.DeepClone());
                }
                if (filtered.Count != output.Count)
                {
                    response["output"] = filtered;
                }
                if (isToolCallResponse && _logger is { } toolCallLogger && toolCallLogger.IsEnabled(LogLevel.Information))
                {
                    toolCallLogger.ResponseContainedToolCalls(toolCallNames.Count, string.Join(", ", toolCallNames), sessionId);
                }

                // Issue #13 tail: track response output content in the context window -- mirrors
                // rtmt.py's own (separate) loop over message["response"]["output"] after the
                // function_call/function_call_output scrub above, tracking every item's content
                // text/transcript regardless of output item type.
                var ctxMonitorForResponse = _sessionManager?.GetContextMonitor(state.EffectiveSessionId);
                if (ctxMonitorForResponse is not null && response["output"] is JsonArray finalOutput)
                {
                    foreach (var outItem in finalOutput)
                    {
                        if (outItem is JsonObject outItemObj && outItemObj["content"] is JsonArray outContentParts)
                        {
                            foreach (var contentNode in outContentParts)
                            {
                                if (contentNode is JsonObject contentObj)
                                {
                                    ctxMonitorForResponse.AddContent(GetString(contentObj, "text"));
                                    ctxMonitorForResponse.AddContent(GetString(contentObj, "transcript"));
                                }
                            }
                        }
                    }
                }
            }

            if (!isToolCallResponse)
            {
                var identifiers = state.Identifiers.AdvanceRoundTrip();
                await SendTextAsync(browserSocket, identifiers.ToFrame("extension.round_trip_token").ToJsonString(), ct)
                    .ConfigureAwait(false);
            }

            // Rick's #244 review (issue 3), broadened per round-2 review issue 2: rtmt.py also
            // records the assistant's own turn here (session_manager.py's
            // record_turn("carhop", spoken)) for later rehydration -- and, per rtmt.py ~2070-2077,
            // does so for EVERY response.done that carries a "response" (spoken is simply "" when
            // there's nothing to say), completely independent of is_tool_call_response: that gate
            // only decides whether round_trip_token is advanced/sent above, never whether the turn
            // is recorded. A tool-call round still has the model say SOMETHING in the same
            // response (e.g. "Let me check on that") that a resumed session should rehydrate, so
            // this port now records every response.done the same way, no longer skipping
            // tool-call responses. This port uses the role string "assistant" (matching
            // RecordTurn's own existing doc comment and RecentTurnsLocked's existing
            // "guest"/anything-else-is-the-persona convention) rather than hardcoding "carhop" --
            // functionally identical, since build_rehydration_item/RecentTurnsLocked both already
            // treat ANY non-"guest" role string as "the persona spoke," substituting the resumed
            // session's own bound persona's role label regardless of what was actually stored.
            _sessionManager?.RecordTurn(state.EffectiveSessionId, "assistant", string.Join(" ", spokenParts));

            return message;
        }

        // Issue #15: reads an IOrderTicketSource best-effort, matching HandleToolCallDoneAsync's
        // own post-exception refresh pattern -- a session with no readable order state yet (or an
        // executor that throws on read) just gets "{}" instead of losing the whole announcement.
        string SafeOrderSummaryJson(IOrderTicketSource source)
        {
            try
            {
                return source.CurrentOrderSummaryJson;
            }
            catch (Exception ex)
            {
                _logger?.OrderStateReadForAnnouncementFailed(ex, sessionId);
                return "{}";
            }
        }

        // Issue #15: the deferred half of the session.created handler -- waits for this
        // connection's own first-frame decision (resume accepted/rejected/never attempted) before
        // telling the browser which it got. Ordering: the bootstrap session.update that
        // RunSessionAsync already sent upstream always precedes whatever this sends, since
        // session.created itself can only arrive after that connect/bootstrap completed.
        async Task AnnounceAfterFirstFrameDecisionAsync()
        {
            bool resumed;
            try
            {
                resumed = await state.FirstFrameDecision.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session tore down (browser/upstream closed, cancellation requested) before any
                // first-frame decision was ever reached -- nothing left to announce.
                return;
            }

            try
            {
                if (resumed)
                {
                    var outcome = state.ResumeAnnounce!;
                    var orderSummaryJson = outcome.ToolExecutor is IOrderTicketSource ticketSource
                        ? SafeOrderSummaryJson(ticketSource)
                        : "{}";
                    var resumedFrame = new JsonObject
                    {
                        ["type"] = "extension.session_resumed",
                        ["order_summary"] = JsonNode.Parse(orderSummaryJson) ?? new JsonObject(),
                        ["session_token"] = state.Identifiers.SessionToken,
                        ["round_trip_index"] = state.Identifiers.RoundTripIndex,
                        // Rick's #244 review (issue 5): rtmt.py's handle_resume always includes
                        // round_trip_token alongside round_trip_index in this frame (App.tsx's own
                        // resume handling reads it, per types.ts's SessionResumedMessage) -- it was
                        // simply missing here even though SessionIdentifiers.RoundTripToken already
                        // exists as a computed property.
                        ["round_trip_token"] = state.Identifiers.RoundTripToken,
                        ["resume_id"] = outcome.ResumeId,
                    };
                    // Parity with rtmt.py's handle_resume (sets `announced = True` right before sending
                    // extension.session_resumed): a successfully resumed connection holds a baton (its
                    // own resume id) exactly like a fresh connection does, so if a later stray
                    // extension.resume invalidates it, this socket must get the same rotated-id
                    // re-announce a fresh connection would -- not silence.
                    state.MetadataAnnounced = true;
                    await SendTextAsync(browserSocket, resumedFrame.ToJsonString(), ct).ConfigureAwait(false);

                    if (outcome.ConversationStarted)
                    {
                        var rehydrationItem = new JsonObject
                        {
                            ["type"] = "conversation.item.create",
                            ["item"] = new JsonObject
                            {
                                ["id"] = MiddleTierItemIds.NewId(),
                                ["type"] = "message",
                                ["role"] = "system",
                                ["content"] = new JsonArray(new JsonObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] = SessionManager.BuildRehydrationText(
                                        orderSummaryJson,
                                        outcome.RecentTurns ?? Array.Empty<(string Role, string Text)>(),
                                        persona.RoleName),
                                }),
                            },
                        };
                        var rehydrationItemJson = rehydrationItem.ToJsonString();
                        await SendTextAsync(upstream, rehydrationItemJson, ct).ConfigureAwait(false);
                        // Issue #13 tail: track the rehydration item in the context window, mirroring
                        // rtmt.py's ctx_monitor.add_content(rehydration) right after it's sent.
                        _sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(rehydrationItemJson);

                        // Restore the persisted voice on the (brand new) upstream connection BEFORE any
                        // response.create can fire -- the bootstrap session.update already went out with
                        // whatever voice the fresh persona binding resolved to, so this corrects it in
                        // place (VoicePickerTests' resume-restore-precedes-response.create requirement).
                        var voiceUpdate = state.Guard.Track(BuildVoiceUpdateFrame(state.Voice).ToJsonString());
                        await SendTextAsync(upstream, voiceUpdate, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await SendFreshSessionMetadataAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Best-effort fire-and-forget announce: teardown can race the post-decision send.
            }
            catch (WebSocketException)
            {
                // Best-effort fire-and-forget announce: a closing socket should not emit a fresh error log.
            }
        }

        // Port of rtmt.py's announce_fresh(): mints a rotated resumeId and (re-)announces
        // extension.session_metadata. Called once from AnnounceAfterFirstFrameDecisionAsync for a
        // genuinely fresh connection, and again -- for ANY connection that already holds a resume-id
        // baton (state.MetadataAnnounced, set by both the fresh path and a successful resume) -- from
        // a late (non-first-frame) extension.resume rejection, so the browser's dropped stored id is
        // replaced with a fresh one without re-greeting or otherwise disturbing the still-live
        // session.
        async Task SendFreshSessionMetadataAsync()
        {
            var resumeId = _sessionManager!.IssueResumeId(state.EffectiveSessionId);
            var metadataFrame = state.Identifiers.ToFrame("extension.session_metadata");
            if (resumeId is not null)
            {
                metadataFrame["resumeId"] = resumeId;
            }
            state.MetadataAnnounced = true;
            await SendTextAsync(browserSocket, metadataFrame.ToJsonString(), ct).ConfigureAwait(false);
        }

        async Task<JsonObject?> DispatchServerMessageAsync(JsonObject message, string msgType)
        {
            switch (msgType)
            {
                case "error":
                    return await HandleErrorAsync(message).ConfigureAwait(false);

                case "conversation.item.input_audio_transcription.failed":
                    _logger?.InputAudioTranscriptionFailed(_sessionConfig.TranscriptionModel, message["error"]?.ToJsonString(), sessionId);
                    return message;

                case "session.created":
                    {
                        var echo = BuildClientSessionEcho(message);
                        if (!state.SessionMetadataSent)
                        {
                            state.SessionMetadataSent = true;
                            if (_sessionManager is not null)
                            {
                                // Deferred: the browser doesn't learn whether this connection is fresh
                                // or a resume until its own first frame has been processed (or the
                                // first-frame-timeout fallback elapses) -- see
                                // HandleResumeFirstFrameAsync/RelayBrowserToUpstreamAsync. Fire-and-forget
                                // here (not awaited): session.created's own caller must not block on it.
                                AnnounceAfterFirstFrameDecisionAsync().FireAndForget(_logger, nameof(AnnounceAfterFirstFrameDecisionAsync));
                            }
                            else
                            {
                                await SendTextAsync(browserSocket,
                                    state.Identifiers.ToFrame("extension.session_metadata").ToJsonString(), ct).ConfigureAwait(false);
                            }
                        }
                        return echo;
                    }

                case "session.updated":
                    state.Guard.OnSessionUpdated();
                    if (!state.SessionConfigured.Task.IsCompleted)
                    {
                        _logger?.SessionUpdatedToolsConfigured(sessionId);
                        state.SessionConfigured.TrySetResult(true);
                    }
                    return message["session"] is JsonObject ? BuildClientSessionEcho(message) : message;

                case "response.output_item.added":
                    {
                        if (message["item"] is JsonObject item && GetString(item, "type") == "function_call")
                        {
                            var callId = GetString(item, "call_id");
                            if (!string.IsNullOrEmpty(callId) && !state.ToolsPending.ContainsKey(callId))
                            {
                                if (_logger is { } toolReceivedLogger && toolReceivedLogger.IsEnabled(LogLevel.Information))
                                {
                                    toolReceivedLogger.ToolCallReceived(GetString(item, "name"), callId, sessionId);
                                }
                                state.ToolsPending[callId] = "";
                            }
                            return null;
                        }
                        return message;
                    }

                case "conversation.item.created":
                case "conversation.item.added":
                    {
                        if (message["item"] is JsonObject item)
                        {
                            if (GetString(item, "type") == "function_call")
                            {
                                var callId = GetString(item, "call_id") ?? "";
                                state.ToolsPending[callId] = GetString(message, "previous_item_id") ?? "";
                                return null;
                            }
                            if (ClientServerFilter.DropFromClient(item))
                            {
                                return null;
                            }
                        }
                        return message;
                    }

                case "conversation.item.done":
                case "conversation.item.retrieved":
                    if (message["item"] is JsonObject doneItem && ClientServerFilter.DropFromClient(doneItem))
                    {
                        return null;
                    }
                    return message;

                case "response.function_call_arguments.delta":
                case "response.function_call_arguments.done":
                    return null;

                case "response.output_item.done":
                    if (message["item"] is JsonObject doneCallItem && GetString(doneCallItem, "type") == "function_call")
                    {
                        await toolCallDispatcher.HandleToolCallDoneAsync(doneCallItem).ConfigureAwait(false);
                    }
                    return null;

                case "response.done":
                    return await HandleResponseDoneAsync(message).ConfigureAwait(false);

                default:
                    // response.created's rate-limit-recovery side effect runs in the
                    // echo-suppression/barge-in switch above (RelayUpstreamToBrowserAsync), same
                    // as response.done's; everything here (including response.created itself)
                    // falls through unchanged, exactly like rtmt.py's `updated_message = data`
                    // default.
                    return message;
            }
        }

        async Task RelayUpstreamToBrowserAsync()
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocketFrame? frame;
                try
                {
                    frame = await WebSocketFrameReader.ReadMessageAsync(upstream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    _logger?.UpstreamWebSocketError(ex, sessionId);
                    break;
                }
                if (frame is null)
                {
                    break;
                }
                if (frame.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                JsonObject message;
                string msgType;
                try
                {
                    // Strict parse: see RelayJson.ParseRelayFrame's doc and the client→server
                    // side's comment above. Kept even though the fake upstream in practice never
                    // sends a duplicate-key frame, so a future real-upstream one can't crash the
                    // relay.
                    message = RelayJson.ParseRelayFrame(frame.Payload);
                    msgType = GetString(message, "type") ?? "";
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    _logger?.DroppedMalformedServerFrame(sessionId);
                    continue;
                }

                try
                {
                    // Echo-suppression/barge-in side effects -- independent of the passthrough/switch
                    // dispatch below, mirroring rtmt.py's dual marker-substring + switch-case wiring
                    // collapsed into one pass since this port always fully parses (see class doc).
                    switch (msgType)
                    {
                        case "response.output_audio.delta":
                        case "response.audio.delta":
                            state.AssistantAudioSeen = true;
                            state.Echo.OnAudioDelta();
                            break;
                        case "response.output_audio.done":
                        case "response.audio.done":
                            state.Echo.OnAudioDone(NowSeconds());
                            break;
                        case "input_audio_buffer.speech_started":
                            state.Echo.OnSpeechStarted();
                            // Issue #13 Wave 4: the guest taking the turn drops any pending
                            // rate-limit retry, same as Python's RateLimitRecovery.on_guest_speech.
                            state.RateLimit.OnGuestSpeech();
                            // Issue #15/#181: genuine guest speech also cancels a pending nudge --
                            // the guest answering (even just acknowledging) means no "are you still
                            // there?" prompt is needed.
                            state.Nudge?.Cancel("guest speech_started");
                            // Rick's #244 review (issue 1): rtmt.py's from_server_to_client also
                            // touches activity here -- a talking guest whose browser->server frames
                            // are ALL input_audio_buffer.append (which deliberately never touches
                            // activity on that side, see RelayBrowserToUpstreamAsync's own comment)
                            // would otherwise idle-time-out (4000) mid-conversation despite being
                            // actively engaged.
                            _sessionManager?.TouchActivity(state.EffectiveSessionId);
                            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review
                            // "S1"): genuine guest speech is one of rtmt.py's two
                            // reset_for_new_turn() triggers -- breaks the tool-failure streak, same
                            // as the completed-transcription case below.
                            state.ToolFailures.ResetForNewTurn();
                            break;
                        case "conversation.item.input_audio_transcription.completed":
                            // Issue #13 tail: rtmt.py's own ctx_monitor.add_content(transcript[:200])
                            // for this event type is nested strictly inside `if (verbose or
                            // _VERBOSE_GLOBAL):` -- i.e. it only fires when verbose debug logging is
                            // on, which defaults OFF in production (_VERBOSE_GLOBAL's default is
                            // false, and no session enables it unless the browser explicitly sends
                            // extension.set_verbose_logging). Verbose debug logging itself remains a
                            // documented, deliberate C# scope cut (see docs/dotnet_mapping.md), so
                            // this port intentionally does NOT track this specific event -- that
                            // matches Python's own default (verbose-off) production behavior exactly.
                            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review
                            // "S1"): the other guest-turn signal (besides speech_started) that
                            // resets the tool-failure streak -- a completed input transcription is
                            // still genuine guest activity even if VAD never fired speech_started
                            // first (e.g. push-to-talk clients).
                            state.ToolFailures.ResetForNewTurn();
                            // Issue #15/#181: same guest-activity signal, same nudge cancellation.
                            state.Nudge?.Cancel("guest transcription completed");
                            // Rick's #244 review (issue 1): the other upstream touch_activity call
                            // site -- see the speech_started case above for the full reasoning
                            // (push-to-talk clients may never fire speech_started at all, so this
                            // is not merely redundant with it).
                            _sessionManager?.TouchActivity(state.EffectiveSessionId);
                            // Rick's #244 review (issue 3): rtmt.py also records the guest's own
                            // turn here (session_manager.py's record_turn("guest", ...)) for later
                            // rehydration -- RecordTurn itself no-ops on a null/blank/whitespace-only
                            // transcript and on a session with history disabled, matching
                            // record_turn's own internal guards exactly (see
                            // SessionManager.RecordTurn's doc comment), so no pre-filtering is
                            // needed here.
                            _sessionManager?.RecordTurn(state.EffectiveSessionId, "guest", GetString(message, "transcript"));
                            break;
                        case "response.created":
                            // Issue #13 Wave 4: tells the ladder a response just started -- our own
                            // retry's (sets awaiting-retry first, so this is a no-op for it) or
                            // anyone else's (VAD, a tool follow-up, the greeting) which drops any
                            // stale retry state the same way Python's on_response_created does.
                            state.RateLimit.OnResponseCreated();
                            break;
                        case "response.done":
                            // swigerb/SonicAIDriveThru#48: a greeting that produced no audio (text-only
                            // fallback, cancelled/failed before any audio) never reaches OnAudioDone --
                            // response.done is the guaranteed event for every response, so it's the
                            // fallback that ends greeting suppression instead of leaving the mic muted
                            // until the guest physically interrupts.
                            state.Echo.OnResponseDone(NowSeconds());
                            break;
                    }

                    JsonObject? forward;
                    if (PassthroughEvents.ServerTypes.Contains(msgType))
                    {
                        if (PassthroughEvents.GaToLegacy.TryGetValue(msgType, out var legacyType))
                        {
                            message["type"] = legacyType;
                        }
                        forward = message;
                    }
                    else
                    {
                        forward = await DispatchServerMessageAsync(message, msgType).ConfigureAwait(false);
                    }

                    if (forward is not null)
                    {
                        await SendTextAsync(browserSocket, forward.ToJsonString(), ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // R3: any per-frame processing/send failure must not fault this loop and end
                    // the session silently -- log with the session id (never the payload, which
                    // may carry guest PII/order details) and move on to the next frame.
                    _logger?.ErrorProcessingServerFrame(ex, sessionId);
                }
            }
        }

        try
        {
            var bootstrap = state.Guard.Stamp(RealtimeSessionBuilder.BuildBootstrapSessionUpdate(
                _sessionConfig, toolSchemas,
                voice: Overridable.Of<string?>(voice),
                systemMessage: Overridable.Of<string?>(systemMessage),
                reasoningOverride: reasoningOverride));
            await SendTextAsync(upstream, bootstrap.ToJsonString(), ct).ConfigureAwait(false);
            _logger?.UpstreamSessionBootstrapped(toolSchemas.Count, sessionId);

            var browserToUpstream = RelayBrowserToUpstreamAsync();
            var upstreamToBrowser = RelayUpstreamToBrowserAsync();
            try
            {
                await Task.WhenAny(browserToUpstream, upstreamToBrowser).ConfigureAwait(false);
            }
            finally
            {
                // Python's asyncio.gather leaves both loops running until each independently
                // exits; proactively cancelling the counterpart here avoids a hang if only one
                // side ever closes/errors.
                linkedCts.Cancel();
                await SwallowAsync(browserToUpstream).ConfigureAwait(false);
                await SwallowAsync(upstreamToBrowser).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.UnexpectedRelayError(ex, sessionId);
        }
        finally
        {
            // swigerb/SonicAIDriveThru#59: cancel any delayed echo flush timer so it can't fire
            // (and attempt a send) after this connection has already gone away.
            state.Echo.Close();
            // Issue #13 Wave 4: same reasoning -- a pending rate-limit retry must never fire (and
            // attempt a send) after the socket has already gone away.
            state.RateLimit.Cancel("socket closed");
            // Issue #15: same reasoning -- a pending nudge must never fire after teardown.
            state.Nudge?.Cancel("socket closed");
            // Issue #15: unconditional detach, mirroring rtmt.py's own `finally:` comment --
            // this runs for EVERY disconnect (graceful close, abrupt abort/EOF, or any unhandled
            // exception above), never just the orderly-close path. A resume already in flight
            // (state.FirstFrameDecision not yet resolved) leaves EffectiveSessionId at the
            // original provisional id, so the provisional (now-abandoned) session is what gets
            // detached/evicted -- correct, since no resume ever actually completed for it.
            _sessionManager?.Detach(browserSocket, state.EffectiveSessionId, "socket closed");
            await CloseIfOpenAsync(browserSocket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
            await CloseIfOpenAsync(upstream, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
        }
    }

    // ── Issue #338: thin delegates to FramePump (see that class's own doc comment) ───────────
    // Kept here, with the SAME signatures as before the extraction, so every call site above is
    // untouched -- pure move-and-delegate. FramePump holds the actual logic (and now the EXACT
    // same supersede-close sequence CascadeProcessor also calls), these just plug in the
    // instance state (_logger/_timeProvider) FramePump's static methods take as parameters.
    private Task SwallowAsync(Task task) => FramePump.SwallowAsync(task, _logger!);

    private static Task CloseIfOpenAsync(WebSocket socket, WebSocketCloseStatus status, string? description) =>
        FramePump.CloseIfOpenAsync(socket, status, description);

    internal static Task CloseSupersededStaleConnectionAsync(
        WebSocket staleWs, CancellationTokenSource? staleCts, TimeSpan closeTimeout, ILogger logger) =>
        FramePump.CloseSupersededStaleConnectionAsync(staleWs, staleCts, closeTimeout, logger);

    private static Task SendTextAsync(WebSocket socket, string payload, CancellationToken ct) =>
        FramePump.SendTextAsync(socket, payload, ct);

    internal static Task SendBytesAsync(WebSocket socket, byte[] payload, CancellationToken ct) =>
        FramePump.SendBytesAsync(socket, payload, ct);

    internal FramePump.AppendFastPathResult TryAppendFastPath(byte[] payload, EchoSuppressor echo) =>
        FramePump.TryAppendFastPath(payload, echo, _timeProvider);

    internal Task ForwardFastPathAudioAsync(FramePump.AppendFastPathResult fastPath, byte[] payload, WebSocket upstream, string sessionId, CancellationToken ct) =>
        FramePump.ForwardFastPathAudioAsync(fastPath, payload, upstream, sessionId, ct, _logger!);

    internal static Task ForwardClientFrameAsync(
        JsonObject forwarded, string? sentType, EchoSuppressor echo, RateLimitRecovery rateLimit, WebSocket upstream, CancellationToken ct) =>
        FramePump.ForwardClientFrameAsync(forwarded, sentType, echo, rateLimit, upstream, ct);

    internal static bool TryMatchAppendFastPath(byte[] payload) => FramePump.TryMatchAppendFastPath(payload);

    // Issue #338: internal (not private) so extracted collaborators (e.g. ToolCallDispatcher) in
    // the same assembly can reuse it instead of duplicating this JSON helper.
    internal static string? GetString(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Consumes extension messages that mutate only this session's own order settings
    /// (machine/happy-hour overrides), never forwarding anything upstream to Azure OpenAI. Internal
    /// so tests can exercise the exact drop/apply behaviour without a live websocket relay.
    /// <paramref name="browserSocket"/> is optional (tests that don't care about the ticket push
    /// pass none) -- production call sites always pass the live connection's own socket.</summary>
    internal async Task<bool> TryHandleSessionOverrideExtensionMessageAsync(
        string msgType, JsonObject message, string effectiveSessionId, WebSocket? browserSocket = null, CancellationToken ct = default)
    {
        if (_sessionManager is null)
        {
            return msgType is "extension.set_machine_status" or "extension.set_happy_hour_mode";
        }

        if (msgType == "extension.set_machine_status")
        {
            var machine = GetString(message, "machine");
            var candidateStatus = GetString(message, "status");
            var status = ClientServerFilter.SanitizeMachineStatus(candidateStatus);
            if (string.IsNullOrEmpty(machine) || status is null
                || !_sessionManager.SetMachineStatus(effectiveSessionId, machine, status))
            {
                _logger?.DroppedSetMachineStatus(machine, candidateStatus, effectiveSessionId);
                return true;
            }

            _logger?.AppliedSetMachineStatus(machine, status, effectiveSessionId);
            return true;
        }

        if (msgType == "extension.set_happy_hour_mode")
        {
            var candidateMode = GetString(message, "mode");
            var mode = ClientServerFilter.SanitizeHappyHourMode(candidateMode);
            if (mode is null || !_sessionManager.SetHappyHourMode(effectiveSessionId, mode))
            {
                _logger?.DroppedSetHappyHourMode(candidateMode, effectiveSessionId);
                return true;
            }

            _logger?.AppliedSetHappyHourMode(mode, effectiveSessionId);

            // #309 (R2): the mode change just recomputed OrderState.Summary
            // (OrderState.SetHappyHourMode -> UpdateSummary) -- push it to the browser right now,
            // the same extension.middle_tier_tool_response shape a successful update_order/
            // get_order tool call already pushes, so the on-screen ticket never lags a mode
            // change until the guest's next, unrelated order action. previous_item_id is null
            // (unlike every REAL tool-call push, which always carries the actual pending call's
            // item id) because this isn't answering any model tool call at all -- the frontend's
            // onReceivedExtensionMiddleTierToolResponse only arms the "a spoken follow-up is
            // coming" flag when previous_item_id is present, specifically so this synthetic push
            // can't wedge mic/response state waiting for a round_trip_token that will never
            // arrive for it (Unity's #309 review note).
            if (browserSocket is not null)
            {
                string? ticketJson = null;
                try
                {
                    ticketJson = _sessionManager.GetOrderSummaryJson(effectiveSessionId);
                }
                catch (Exception ex)
                {
                    _logger?.TicketRefreshAfterHappyHourModeFailed(ex, effectiveSessionId);
                }

                if (ticketJson is not null)
                {
                    await SendTextAsync(browserSocket, new JsonObject
                    {
                        ["type"] = "extension.middle_tier_tool_response",
                        ["previous_item_id"] = null,
                        ["tool_name"] = "get_order",
                        ["tool_result"] = ticketJson,
                    }.ToJsonString(), ct).ConfigureAwait(false);
                }
            }

            return true;
        }

        return false;
    }

    private double NowSeconds() => _timeProvider.GetTimestamp() / (double)_timeProvider.TimestampFrequency;

    private static IReadOnlyList<JsonObject> BuildToolSchemas(PromptLoader? promptLoader)
    {
        if (promptLoader is null)
        {
            return [];
        }
        var list = new List<JsonObject>(promptLoader.ToolSchemas.Count);
        foreach (var schema in promptLoader.ToolSchemas)
        {
            if (YamlJson.ToJsonNode((IDictionary<object, object>)schema) is JsonObject obj)
            {
                list.Add(obj);
            }
        }
        return list;
    }

    /// <summary>Per-session binding for this connection's own bound persona: its own
    /// <see cref="PromptLoader"/> (or null if unregistered), the system prompt and tool schemas
    /// rendered from THAT SAME loader, the voice, and the <see cref="IToolExecutor"/>
    /// <see cref="_toolExecutorFactory"/> builds from THAT SAME loader (issue #170 round 3, R5 --
    /// Rick's PR #175 round-2 review: the tool executor must be built from the connection's own
    /// bound persona's loader, not the deployment default's or any other persona's, so a
    /// tool-execution error renders that SAME persona's own text -- mirrors R4's fix for
    /// `session.tools[].description` above, now proven on the C# side too). Internal (not
    /// private) purely so <c>RealtimeProcessorSessionBindingTests</c> can exercise the
    /// resolution directly, with two differently-bound loaders and a capturing
    /// `toolExecutorFactory`, without needing a real WebSocket/upstream connection -- same reason
    /// <see cref="ResolveUpstreamAuthHeaderAsync"/> below is internal.</summary>
    internal (PromptLoader? PromptLoader, string? SystemMessage, string Voice, IReadOnlyList<JsonObject> ToolSchemas, IToolExecutor ToolExecutor)
        ResolveSessionBinding(Persona persona, string? menuMode)
    {
        _promptLoaders.TryGetValue(persona.Id, out var promptLoader);
        var systemMessage = promptLoader?.SystemPrompt;
        var voice = ClientServerFilter.SanitizeVoice(persona.Voice.Default, _allowedVoices)
            ?? _sessionConfig.VoiceChoice ?? "marin";
        var toolSchemas = BuildToolSchemas(promptLoader);
        var toolExecutor = _toolExecutorFactory?.Invoke(persona, promptLoader, menuMode) ?? _toolExecutor;
        return (promptLoader, systemMessage, voice, toolSchemas, toolExecutor);
    }

    /// <summary>PR #140 R5: chooses the outbound auth header for the upstream Azure OpenAI
    /// realtime connect -- `api-key` when one is configured (the only mode before this round),
    /// else a managed-identity bearer token via <see cref="_bearerTokenProvider"/> (falling back
    /// to the lazily-constructed real <see cref="DefaultAzureCredentialTokenProvider"/> if none
    /// was injected), matching rtmt.py's <c>DefaultAzureCredential</c> fallback and its
    /// <c>https://cognitiveservices.azure.com/.default</c> scope. Internal (not private) purely so
    /// <c>UpstreamAuthHeaderTests</c> can exercise the selection without a real
    /// ClientWebSocket or Azure credential.</summary>
    internal async Task<(string HeaderName, string HeaderValue)> ResolveUpstreamAuthHeaderAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_upstreamApiKey))
        {
            return ("api-key", _upstreamApiKey);
        }

        var provider = _bearerTokenProvider ?? DefaultAzureCredentialTokenProvider.Instance.Value;
        var token = await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return ("Authorization", $"Bearer {token}");
    }

    /// <summary>Builds the upstream GA realtime endpoint URI (`/openai/v1/realtime?model=...`),
    /// converting the configured http(s) base endpoint to the ws(s) scheme
    /// <see cref="ClientWebSocket"/> requires.</summary>
    private static Uri BuildUpstreamUri(string endpoint, string deployment)
    {
        var baseUri = new Uri(endpoint, UriKind.Absolute);
        var scheme = baseUri.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => "wss",
            _ => "ws",
        };
        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = "/openai/v1/realtime",
            Query = $"model={Uri.EscapeDataString(deployment)}",
        };
        return builder.Uri;
    }
}
