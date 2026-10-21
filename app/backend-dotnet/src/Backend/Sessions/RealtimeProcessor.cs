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
        var deployment = string.IsNullOrEmpty(resolvedModel.Deployment) ? _defaultDeployment : resolvedModel.Deployment;

        using var upstream = new ClientWebSocket();
        if (!await ConnectUpstreamAsync(upstream, browserSocket, deployment, sessionId, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }
        var state = CreateSessionState(
            upstream, browserSocket, persona, resolvedModel, sessionId, binding.Voice, binding.ToolExecutor);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterSession(browserSocket, upstream, persona, resolvedModel, menuMode, state, linkedCts);
        var context = CreateSessionContext(
            browserSocket, upstream, persona, resolvedModel, menuMode, state, linkedCts);
        await RelaySessionAsync(context, linkedCts).ConfigureAwait(false);
    }

    private async Task<bool> ConnectUpstreamAsync(
        ClientWebSocket upstream, WebSocket browserSocket, string deployment, string sessionId,
        CancellationToken cancellationToken)
    {
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
            return false;
        }
        return true;
    }

    private RealtimeSessionState CreateSessionState(
        WebSocket upstream, WebSocket browserSocket, Persona persona, ResolvedModel resolvedModel,
        string sessionId, string voice, IToolExecutor toolExecutor) =>
        new()
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

    private void RegisterSession(
        WebSocket browserSocket, WebSocket upstream, Persona persona, ResolvedModel resolvedModel,
        string? menuMode, RealtimeSessionState state, CancellationTokenSource linkedCts)
    {
        // ── Issue #15: register this connection with the session registry up front, mirroring
        // rtmt.py's own create_session(...) call before _forward_messages starts relaying any
        // client traffic. Entirely inert when no SessionManager was injected (see that field's
        // own doc comment) -- every pre-#15 caller/test keeps today's exact behaviour.
        if (_sessionManager is not null)
        {
            _sessionManager.CreateSession(
                state.SessionId, browserSocket, persona.Id, resolvedModel.Id, menuMode, state.ToolExecutor, state.Voice,
                attachedCts: linkedCts, identifiers: state.Identifiers, attachedSupersededFlag: state.Superseded);

            state.Nudge = new NudgeScheduler(
                _sessionManager.Config.NudgeAfterSeconds,
                sendNudgeAsync: nudgeCt => SendNudgeAsync(upstream, persona, state, nudgeCt),
                isRateLimitBusy: () => state.RateLimit.Busy,
                sessionConfigured: state.SessionConfigured.Task,
                timeProvider: _timeProvider,
                sessionId: state.SessionId,
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
                _ = Task.Delay(TimeSpan.FromSeconds(firstFrameTimeoutSeconds), _timeProvider, linkedCts.Token)
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
    }

    private async Task SendNudgeAsync(
        WebSocket upstream, Persona persona, RealtimeSessionState state, CancellationToken ct)
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
        var json = nudgeItem.ToJsonString();
        await SendTextAsync(upstream, json, ct).ConfigureAwait(false);
        await SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
        _sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(json);
    }

    private RealtimeSessionContext CreateSessionContext(
        WebSocket browserSocket, WebSocket upstream, Persona persona, ResolvedModel resolvedModel,
        string? menuMode, RealtimeSessionState state, CancellationTokenSource linkedCts)
    {
        _promptLoaders.TryGetValue(persona.Id, out var promptLoader);
        var sessionId = state.SessionId;
        var ct = linkedCts.Token;
        var greetingGate = new GreetingGate(
            upstream, promptLoader, state, _sessionManager, _timeProvider, _greetingTimeoutSeconds, sessionId, _logger!, ct);

        var toolCallDispatcher = new ToolCallDispatcher(upstream, browserSocket, state, _sessionManager, sessionId, _logger!, ct);

        JsonObject BuildVoiceUpdateFrame(string newVoice) => new()
        {
            ["type"] = "session.update",
            ["event_id"] = EventIds.NewEventId("sonic_voice"),
            ["session"] = GaSessionTranslator.ToGaSession(new JsonObject { ["voice"] = newVoice }),
        };

        // Issue #338: resume/supersede-announce handling (RejectLateResumeAsync,
        // HandleResumeFirstFrameAsync, AnnounceAfterFirstFrameDecisionAsync,
        // SendFreshSessionMetadataAsync) is its own collaborator now (ResumeCoordinator.cs) --
        // same logic, same log templates/EventIds. BuildVoiceUpdateFrame stays a delegate onto
        // the local function above rather than being duplicated inside the new class.
        var resumeCoordinator = new ResumeCoordinator(
            browserSocket, upstream, persona, resolvedModel, menuMode, sessionId, linkedCts, state,
            _sessionManager, BuildVoiceUpdateFrame, _logger!, ct);

        return new RealtimeSessionContext
        {
            Processor = this,
            BrowserSocket = browserSocket,
            Upstream = upstream,
            Persona = persona,
            ResolvedModel = resolvedModel,
            MenuMode = menuMode,
            SessionId = sessionId,
            SystemMessage = promptLoader?.SystemPrompt,
            PromptLoader = promptLoader,
            ToolSchemas = BuildToolSchemas(promptLoader),
            ReasoningOverride = Overridable.Of<bool?>(resolvedModel.Reasoning),
            State = state,
            SessionConfig = _sessionConfig,
            SessionManager = _sessionManager,
            GreetingGate = greetingGate,
            ToolCallDispatcher = toolCallDispatcher,
            ResumeCoordinator = resumeCoordinator,
            AllowedVoices = _allowedVoices,
            Logger = _logger!,
            TimeProvider = _timeProvider,
            CancellationToken = ct,
        };
    }

    private async Task RelaySessionAsync(RealtimeSessionContext context, CancellationTokenSource linkedCts)
    {
        var state = context.State;
        var browserSocket = context.BrowserSocket;
        var upstream = context.Upstream;
        var ct = context.CancellationToken;
        try
        {
            var bootstrap = state.Guard.Stamp(RealtimeSessionBuilder.BuildBootstrapSessionUpdate(
                _sessionConfig, context.ToolSchemas,
                voice: Overridable.Of<string?>(state.Voice),
                systemMessage: Overridable.Of<string?>(context.SystemMessage),
                reasoningOverride: context.ReasoningOverride));
            await SendTextAsync(upstream, bootstrap.ToJsonString(), ct).ConfigureAwait(false);
            _logger?.UpstreamSessionBootstrapped(context.ToolSchemas.Count, context.SessionId);

            var browserToUpstream = new RealtimeClientRelay(context).RunAsync();
            var upstreamToBrowser = new RealtimeServerRelay(context).RunAsync();
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
            _logger?.UnexpectedRelayError(ex, context.SessionId);
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
