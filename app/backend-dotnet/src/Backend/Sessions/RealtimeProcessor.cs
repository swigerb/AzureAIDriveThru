using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging;

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
/// failure-cap ladder (`_ToolFailureTracker`), session resume/rehydration itself --
/// `extension.resume`, the 4002 supersede-close, and the 4000 idle-timeout close all need a real
/// session registry and land with #15 -- turn recording, and the fast-path regex/marker-substring
/// optimisations (every frame is fully JSON-parsed instead). Context-window monitoring
/// (<see cref="ContextMonitor"/>) landed in the issue #13 tail: every `ctx_monitor.add_content`
/// call site in rtmt.py (session.update instructions/tools, tool call args/result, response
/// output text/transcript, greeting, resume-nudge, and rehydration text) has a matching
/// <see cref="SessionManager.GetContextMonitor"/> call here, with the sole exception of the
/// verbose-only user-transcript tracking site, which is intentionally not ported since it only
/// ever fires in Python when verbose debug logging -- itself a separate, still-deferred scope
/// cut -- is enabled (see the `conversation.item.input_audio_transcription.completed` case below
/// for the full reasoning).
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
public sealed class RealtimeProcessor : IPipelineProcessor
{
    /// <summary>Port of app/backend/session_manager.py's <c>SESSION_ENDED_CLOSE_REASON</c> --
    /// paired with the standard <see cref="WebSocketCloseStatus.NormalClosure"/> (1000) code for a
    /// guest-initiated <c>extension.end_session</c>.</summary>
    private const string SessionEndedCloseReason = "session_ended";

    /// <summary>Rick's #244 round-2 review, issue 1: bounds how long the BACKGROUND
    /// supersede-close (<see cref="CloseSupersededStaleConnectionAsync"/>) may spend trying to
    /// drain a courtesy close frame to a stale peer before giving up and cancelling its CTS
    /// anyway. Python's own <c>_close_superseded</c> (rtmt.py ~1026) has no timeout at all --
    /// it is already a background task, so an unbounded await never blocks anything else, and a
    /// stuck peer just leaks one background task/socket forever. This port chooses to bound it
    /// instead (a stuck peer's resources get reclaimed eventually), but the bound must be loose:
    /// CI run 37208960846 caught the first value here (2s) aborting the ordinary, healthy-peer
    /// <c>Resuming_from_a_still_attached_socket_supersedes_it_with_4002</c> conformance test
    /// under full-suite parallel load -- the close frame hadn't even failed to send, it simply
    /// hadn't finished within 2s of CPU-starved scheduling, so the timeout fired and aborted a
    /// peer that was never actually stuck. Widened to 10s, which stays comfortably inside that
    /// test's own 30s <c>FrameTimeout</c> while giving a merely-slow-under-load close far more
    /// room than a merely-busy CI runner should ever need, and still reclaims a truly
    /// never-draining peer (Rick's actual repro) in bounded time rather than Python's
    /// forever.</summary>
    internal static readonly TimeSpan SupersededCloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Rick's #244 round-2 re-review: the poll interval
    /// <see cref="CloseSupersededStaleConnectionAsync"/> uses while waiting for the stale socket to
    /// settle (leave <see cref="WebSocketState.Open"/>/<see cref="WebSocketState.CloseSent"/>)
    /// before it cancels <c>staleCts</c>. Short enough that a healthy peer's near-instant answering
    /// close is noticed within a few polls (no perceptible delay added to the common case), long
    /// enough not to busy-spin the thread pool while waiting out a genuinely stuck peer for the
    /// rest of <see cref="SupersededCloseTimeout"/>.</summary>
    private static readonly TimeSpan SupersededSettlePollInterval = TimeSpan.FromMilliseconds(10);

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
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RateLimitSettings _rateLimitSettings;
    private readonly SessionManager? _sessionManager;
    private readonly Configuration.ConnectionConfig _connectionConfig;

    public RealtimeProcessor(
        ModelCatalog catalog,
        string defaultDeployment,
        string upstreamEndpoint,
        string upstreamApiKey,
        RealtimeSessionConfig sessionConfig,
        IReadOnlyDictionary<string, PromptLoader> promptLoaders,
        IToolExecutor toolExecutor,
        IReadOnlySet<string>? allowedVoices = null,
        double echoCooldownSeconds = 1.5,
        double greetingTimeoutSeconds = 5.0,
        ILogger? logger = null,
        IUpstreamBearerTokenProvider? bearerTokenProvider = null,
        Func<Persona, PromptLoader?, string?, IToolExecutor>? toolExecutorFactory = null,
        TimeProvider? timeProvider = null,
        RateLimitSettings? rateLimitSettings = null,
        SessionManager? sessionManager = null,
        Configuration.ConnectionConfig? connectionConfig = null)
    {
        _catalog = catalog;
        _defaultDeployment = defaultDeployment;
        _upstreamEndpoint = upstreamEndpoint;
        _upstreamApiKey = upstreamApiKey;
        _bearerTokenProvider = bearerTokenProvider;
        _sessionConfig = sessionConfig;
        _promptLoaders = promptLoaders;
        _toolExecutor = toolExecutor;
        _toolExecutorFactory = toolExecutorFactory;
        _allowedVoices = allowedVoices ?? ClientServerFilter.DefaultAllowedVoices;
        _echoCooldownSeconds = echoCooldownSeconds;
        _greetingTimeoutSeconds = greetingTimeoutSeconds;
        _logger = logger;
        // Issue #13 Wave 4: the rate-limit retry ladder's own config (resilience.rate_limit in
        // config.yaml) -- defaults to the Python-matching shipped defaults if the caller (normally
        // Program.cs, via RateLimitSettings.FromAppConfig) doesn't supply one.
        _rateLimitSettings = rateLimitSettings ?? new RateLimitSettings();
        // Issue #13 Wave 2: every time-dependent piece of the relay (echo-suppression cooldowns,
        // the greeting-gate timeout, the "loop time" ShouldSuppressAudio/OnAudioDone/OnResponseDone
        // read) is driven from this one clock, so a test can swap in a FakeTimeProvider instead of
        // waiting on real wall-clock delays. Defaults to TimeProvider.System in production.
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Issue #15: the session registry (resume/rehydration/idle/grace/nudge). Left null by
        // every existing caller/test that doesn't pass one -- the whole feature is then fully
        // inert: extension.resume is silently swallowed (the pre-#15 scope-cut behaviour) and no
        // extra session_metadata field/first-frame wait is introduced, so nothing built against
        // this constructor before #15 changes behaviour.
        _sessionManager = sessionManager;
        // Issue #13 tail: config.yaml's `connection` section (ws_heartbeat_seconds/
        // ws_connect_timeout_total/ws_connect_timeout_connect) -- see ConnectionConfig's own doc
        // comment for the full heartbeat/connect-timeout mapping.
        _connectionConfig = connectionConfig ?? new Configuration.ConnectionConfig();
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
    private sealed class RealtimeSessionState
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
        /// <see cref="SessionManager.SupersededFlag"/> -- created once per connection (fresh-or-
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
        /// (<see cref="AnnounceAfterFirstFrameDecisionAsync"/>'s "fresh" branch) or a successfully
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
        var reasoningOverride = Overridable<bool?>.Of(resolvedModel.Reasoning);
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
            _logger?.LogError(ex,
                "Failed to connect to upstream realtime endpoint for deployment {Deployment} (session={SessionId})",
                deployment, sessionId);
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
                logger: _logger),
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
                logger: _logger);

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

        double ParseGreetingTimeoutSeconds()
        {
            var raw = Environment.GetEnvironmentVariable("CONFORMANCE_GREETING_TIMEOUT_SECONDS");
            return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : _greetingTimeoutSeconds;
        }

        JsonObject BuildGreetingFrame()
        {
            JsonObject greeting = promptLoader is not null
                ? (JsonObject)YamlJson.ToJsonNode((IDictionary<object, object>)promptLoader.Greeting)!
                : new JsonObject
                {
                    ["type"] = "conversation.item.create",
                    ["item"] = new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Hello!" }),
                    },
                };
            if (greeting["item"] is JsonObject item)
            {
                item["id"] = MiddleTierItemIds.NewId();
            }
            return greeting;
        }

        async Task SendGreetingOnceAsync(string trigger)
        {
            if (state.GreetingSent)
            {
                return;
            }
            var timeoutSeconds = ParseGreetingTimeoutSeconds();
            try
            {
                await state.SessionConfigured.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning(
                    "No session.updated within {Timeout}s; sending greeting anyway (session={SessionId})",
                    timeoutSeconds, sessionId);
            }
            if (state.GreetingSent)
            {
                return;
            }
            state.GreetingSent = true;
            state.Echo.StartGreetingSuppression();
            _logger?.LogInformation("Sending greeting (trigger={Trigger}, session={SessionId})", trigger, sessionId);
            var greetingFrameJson = BuildGreetingFrame().ToJsonString();
            await SendTextAsync(upstream, """{"type":"input_audio_buffer.clear"}""", ct).ConfigureAwait(false);
            await SendTextAsync(upstream, greetingFrameJson, ct).ConfigureAwait(false);
            await SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
            // Issue #13 tail: track the greeting in the context window, mirroring rtmt.py's
            // ctx_monitor.add_content(greeting_msg) right after the greeting is sent.
            _sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(greetingFrameJson);
            // Rick's #244 review (issue 5): rtmt.py's send_greeting_once marks
            // conversation_started immediately after sending the greeting's response.create
            // (mark_greeting_sent), NOT after the greeting's response.done later arrives -- a
            // connection drop between those two points must still resume silently (rehydrating,
            // no re-greet), not fall back to greeting again, since the guest already heard it
            // start. Previously this port only marked it at the first non-tool-call response.done,
            // which is wrong specifically for that drop-during-the-greeting window.
            _sessionManager?.MarkConversationStarted(state.EffectiveSessionId);
        }

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
            _logger?.LogWarning(
                "Dropped extension.resume arriving after the first-frame decision ({Reason}, session={SessionId})",
                logReason, sessionId);
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
                _logger?.LogWarning(
                    "Dropped extension.set_voice with an unknown/invalid voice {Voice} (session={SessionId})",
                    candidate, sessionId);
                return;
            }
            state.Voice = newVoice;
            _sessionManager?.SetVoice(state.EffectiveSessionId, newVoice);
            if (state.AssistantAudioSeen)
            {
                // GA would reject this outright (cannot_update_voice) and take tools/instructions
                // down with it -- defer to the next unlocked session.update, same as Python.
                _logger?.LogInformation(
                    "Assistant audio already present -- voice {Voice} applies from the next conversation (session={SessionId})",
                    newVoice, sessionId);
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
                _logger?.LogInformation(
                    "extension.resume rejected (reason={Reason}, session={SessionId})", outcome.Reason, sessionId);
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.resume_rejected",
                    ["reason"] = outcome.Reason,
                }.ToJsonString(), ct).ConfigureAwait(false);
                state.FirstFrameDecision.TrySetResult(false);
                return;
            }

            _logger?.LogInformation(
                "Session resumed (resumedSessionId={ResumedSessionId}, session={SessionId})", outcome.SessionId, sessionId);
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
            // (greeting already happened -- GreetingSent=true suppresses SendGreetingOnceAsync
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
                _ = Task.Run(
                    () => CloseSupersededStaleConnectionAsync(staleWs, outcome.StaleCts, SupersededCloseTimeout, _logger),
                    CancellationToken.None);
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
                voice: Overridable<string?>.Of(state.Voice),
                systemMessage: Overridable<string?>.Of(systemMessage),
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
                    _logger?.LogWarning(ex, "Browser WebSocket error (session={SessionId})", sessionId);
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
                    _logger?.LogWarning("Dropped malformed/non-object client→server frame (session={SessionId})", sessionId);
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
                            _logger?.LogWarning(ex, "Error processing extension.resume (session={SessionId})", sessionId);
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
                        _logger?.LogWarning("Dropped client→server frame with a missing/non-string type (session={SessionId})", sessionId);
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
                        _logger?.LogInformation("Guest ended session (session={SessionId})", sessionId);
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

                    var hooksEnabled = Environment.GetEnvironmentVariable("CONFORMANCE_TEST_HOOKS") == "1";
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
                        await SendGreetingOnceAsync("client-session.update").ConfigureAwait(false);
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
                    _logger?.LogWarning(ex, "Error processing client→server frame (session={SessionId})", sessionId);
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
                    _logger?.LogError(
                        "Fallback session.update {EventId} (for {Original}) was ALSO rejected: code={Code} param={Param} " +
                        "message={Message} -- tools may NOT be registered for this conversation (session={SessionId})",
                        rejectedEventId, original, GetString(err, "code"), GetString(err, "param"), GetString(err, "message"), sessionId);
                    return message;
                }
                _logger?.LogError(
                    "Upstream REJECTED session.update {EventId}: code={Code} param={Param} message={Message} -- resending a " +
                    "minimal session.update (instructions + tools only) so the tools survive (session={SessionId})",
                    rejectedEventId, GetString(err, "code"), GetString(err, "param"), GetString(err, "message"), sessionId);

                var rejectedPayload = state.Guard.PayloadOf(rejectedEventId);
                var param = GetString(err, "param") ?? "";
                if ((rejectedPayload.ContainsKey("reasoning") || rejectedPayload.ContainsKey("parallel_tool_calls"))
                    && (param.Length == 0
                        || param.StartsWith("session.reasoning", StringComparison.Ordinal)
                        || param.StartsWith("session.parallel_tool_calls", StringComparison.Ordinal)))
                {
                    _sessionConfig.ReasoningRejected = true;
                    _logger?.LogError(
                        "Deployment {Deployment} rejected reasoning-model options; no longer sending `reasoning` / " +
                        "`parallel_tool_calls` from this process. Set model.reasoning_effort to \"\" for this deployment.",
                        _sessionConfig.Deployment ?? "?");
                }

                var fallback = RealtimeSessionBuilder.BuildFallbackSessionUpdate(
                    _sessionConfig, toolSchemas, voice: Overridable<string?>.Of(state.Voice),
                    systemMessage: Overridable<string?>.Of(systemMessage), reasoningOverride: reasoningOverride);
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
                _logger?.LogInformation(
                    "OpenAI Realtime API error (benign -- response already finished): {Error}", message.ToJsonString());
                return message;
            }

            _logger?.LogError("OpenAI Realtime API error: {Error}", message.ToJsonString());
            return message;
        }

        async Task HandleToolCallDoneAsync(JsonObject item)
        {
            var callId = GetString(item, "call_id");
            if (callId is null || !state.ToolsPending.TryGetValue(callId, out var previousItemId))
            {
                _logger?.LogWarning("Tool call {CallId} not found in pending tools (session={SessionId})", callId, sessionId);
                return;
            }
            var toolName = GetString(item, "name") ?? "";
            if (!state.ToolExecutor.ToolNames.Contains(toolName))
            {
                _logger?.LogError("Unknown tool requested: {ToolName} (session={SessionId})", toolName, sessionId);
                return;
            }

            // Rick's #244 round-2 review, issue 1: refuse to dispatch once this connection has
            // been superseded by a resume elsewhere, checked HERE -- synchronously, with no IO --
            // rather than relying on StaleCts having been cancelled promptly. StaleCts cancellation
            // now happens from a BACKGROUND task (see HandleResumeFirstFrameAsync) that may still
            // be mid-flight against a non-draining stale peer, and even when prompt,
            // OrderToolExecutor.ExecuteAsync is synchronous and ignores its own CancellationToken
            // entirely -- an in-flight call already past this point would run to completion and
            // mutate the shared (not thread-safe) OrderState regardless of cancellation. This flag
            // is set synchronously, under SessionManager's own lock, the instant TryResume captures
            // this connection as stale (SessionManager.SupersededFlag's own doc comment has the
            // full reasoning), so it is safe to trust here with no further synchronization.
            if (state.Superseded.IsSuperseded)
            {
                _logger?.LogInformation(
                    "Dropping tool call '{ToolName}' for call_id={CallId}: this connection was superseded by a " +
                    "resume elsewhere (session={SessionId})", toolName, callId, sessionId);
                return;
            }

            string outputText;
            bool sendToClient;
            string? clientText;
            try
            {
                var argumentsJson = GetString(item, "arguments") ?? "{}";
                using var argumentsDoc = JsonDocument.Parse(argumentsJson);
                _logger?.LogInformation("Executing tool '{ToolName}' (session={SessionId})", toolName, sessionId);
                var result = await state.ToolExecutor.ExecuteAsync(toolName, argumentsDoc.RootElement.Clone(), ct)
                    .ConfigureAwait(false);
                _logger?.LogInformation("Tool '{ToolName}' result direction={Direction} (session={SessionId})",
                    toolName, result.Destination, sessionId);
                outputText = result.Destination is ToolResultDirection.ToServer or ToolResultDirection.ToBoth
                    ? result.ToText() : "";
                sendToClient = result.Destination is ToolResultDirection.ToClient or ToolResultDirection.ToBoth;
                clientText = sendToClient ? result.ToClientText() : null;

                // Issue #13 tail: track tool call args + result in the context window, mirroring
                // rtmt.py's ctx_monitor.add_content(item.get("arguments", "")) /
                // ctx_monitor.add_content(result.to_text()).
                var ctxMonitorForTool = _sessionManager?.GetContextMonitor(state.EffectiveSessionId);
                if (ctxMonitorForTool is not null)
                {
                    ctxMonitorForTool.AddContent(argumentsJson);
                    ctxMonitorForTool.AddContent(result.ToText());
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Tool '{ToolName}' raised an unhandled exception (session={SessionId})", toolName, sessionId);
                outputText = "Something went wrong with that action and it did not complete. Don't retry it yet -- " +
                    "call get_order to confirm the order's current state, then ask the guest to repeat what they'd like.";
                sendToClient = false;
                clientText = null;

                // Issue #14, Rick's PR #149 R4 review (Python parity: rtmt.py's post-exception
                // order_state_singleton.get_order_summary_json read): refresh the guest-visible
                // order ticket from the session's own current order state -- not from the failed
                // tool's own result, since it never produced one. Best-effort: only executors that
                // opt into IOrderTicketSource support this (StubToolExecutor does not), and the
                // read itself is wrapped separately from the send so a session with no readable
                // order state yet just skips the refresh instead of losing the function_call_output
                // below too.
                if (state.ToolExecutor is IOrderTicketSource ticketSource)
                {
                    string? ticketJson = null;
                    try
                    {
                        ticketJson = ticketSource.CurrentOrderSummaryJson;
                    }
                    catch (Exception ticketEx)
                    {
                        _logger?.LogWarning(ticketEx,
                            "Could not read order state to refresh the ticket after a tool failure (session={SessionId})",
                            sessionId);
                    }

                    if (ticketJson is not null)
                    {
                        await SendTextAsync(browserSocket, new JsonObject
                        {
                            ["type"] = "extension.middle_tier_tool_response",
                            ["previous_item_id"] = previousItemId,
                            ["tool_name"] = "get_order",
                            ["tool_result"] = ticketJson,
                        }.ToJsonString(), ct).ConfigureAwait(false);
                    }
                }

                // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2"):
                // marks this round failed; HandleResponseDoneAsync's response.done handling below
                // tallies the round (not the call) exactly once via EndRound().
                state.ToolFailures.RecordCallFailure();
            }

            await SendTextAsync(upstream, new JsonObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JsonObject
                {
                    ["id"] = MiddleTierItemIds.NewId(),
                    ["type"] = "function_call_output",
                    ["call_id"] = callId,
                    ["output"] = outputText,
                },
            }.ToJsonString(), ct).ConfigureAwait(false);

            if (sendToClient)
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.middle_tier_tool_response",
                    ["previous_item_id"] = previousItemId,
                    ["tool_name"] = toolName,
                    ["tool_result"] = clientText,
                }.ToJsonString(), ct).ConfigureAwait(false);
            }
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
                        _logger?.LogWarning(
                            "Capping auto response.create with tool_choice=none after {Count} " +
                            "consecutive failed tool round(s) (session={SessionId})",
                            state.ToolFailures.Count, sessionId);
                        await SendTextAsync(upstream, ToolFailureCapNotice.BuildMessage(promptLoader), ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Suppressing auto response.create -- still at the {Count}-round cap " +
                            "with no guest turn since the apology (session={SessionId})",
                            state.ToolFailures.Count, sessionId);
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
                if (isToolCallResponse)
                {
                    _logger?.LogInformation("Response contained {Count} tool call(s): {Names} (session={SessionId})",
                        toolCallNames.Count, string.Join(", ", toolCallNames), sessionId);
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
                _logger?.LogWarning(ex,
                    "Could not read order state while building a session-resumed announcement (session={SessionId})",
                    sessionId);
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
                    _logger?.LogError(
                        "Input audio transcription failed (model={Model}): {Error} (session={SessionId})",
                        _sessionConfig.TranscriptionModel, message["error"]?.ToJsonString(), sessionId);
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
                            _ = AnnounceAfterFirstFrameDecisionAsync();
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
                        _logger?.LogInformation(
                            "session.updated received -- tools are configured (session={SessionId})", sessionId);
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
                            _logger?.LogInformation(
                                "Tool call received: name={ToolName}, call_id={CallId} (session={SessionId})",
                                GetString(item, "name"), callId, sessionId);
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
                        await HandleToolCallDoneAsync(doneCallItem).ConfigureAwait(false);
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
                    _logger?.LogWarning(ex, "Upstream WebSocket error (session={SessionId})", sessionId);
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
                    _logger?.LogWarning("Dropped malformed/non-object server→client frame (session={SessionId})", sessionId);
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
                    _logger?.LogWarning(ex, "Error processing server→client frame (session={SessionId})", sessionId);
                }
            }
        }

        try
        {
            var bootstrap = state.Guard.Stamp(RealtimeSessionBuilder.BuildBootstrapSessionUpdate(
                _sessionConfig, toolSchemas,
                voice: Overridable<string?>.Of(voice),
                systemMessage: Overridable<string?>.Of(systemMessage),
                reasoningOverride: reasoningOverride));
            await SendTextAsync(upstream, bootstrap.ToJsonString(), ct).ConfigureAwait(false);
            _logger?.LogInformation(
                "Upstream session bootstrapped with {ToolCount} tool(s) before relaying client traffic (session={SessionId})",
                toolSchemas.Count, sessionId);

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
            _logger?.LogError(ex, "Unexpected error in realtime relay (session={SessionId})", sessionId);
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

    private async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: linkedCts.Cancel() in the caller's finally is what unblocks the
            // counterpart loop's pending ReadMessageAsync/SendTextAsync in the first place.
        }
        catch (Exception ex)
        {
            // R3: this used to say "already logged inside the loop itself", which was wrong for a
            // send failure or any other exception the loop didn't itself expect and log -- that
            // exception surfaced here with nothing in the logs at all. Both relay loops now catch
            // and log their own per-frame failures, so reaching here at all means something above
            // the per-frame try/catch faulted (e.g. the loop's own setup) -- log it at Error so a
            // silently-ended session always leaves a trace.
            _logger?.LogError(ex, "Unhandled exception draining a realtime relay loop");
        }
    }

    private static async Task CloseIfOpenAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            await socket.CloseAsync(status, description, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the peer may have already torn the connection down.
        }
    }

    /// <summary>Rick's #244 review, issue 4 (and round-2 review, issue 1): the send-only half of
    /// <see cref="CloseIfOpenAsync"/>, used wherever a socket's OWN relay loop may still have a
    /// <c>ReceiveAsync</c> pending on it concurrently (supersede; mirrors
    /// SessionManager.CloseIdleSessionsAsync's identical choice and doc comment for the idle-sweep
    /// case). <see cref="WebSocket.CloseOutputAsync"/> never waits for the peer's own handshake
    /// reply the way <see cref="WebSocket.CloseAsync"/> does, so it never contends with that
    /// pending receive -- but it is still a send, and a send can still block on a non-draining
    /// transport (half-open network-switch, full receive window) until there is buffer space or
    /// <paramref name="cancellationToken"/> fires; an EARLIER version of this comment claimed it
    /// "can never hang", which Rick's round-2 review (issue 1) disproved with a probe over exactly
    /// such a transport. Callers that cannot afford to be blocked by a stuck PEER (i.e. anywhere
    /// this runs inline in some OTHER connection's own call stack, like the supersede path) must
    /// pass a bounded token rather than <see cref="CancellationToken.None"/>.</summary>
    private static async Task CloseOutputIfOpenAsync(
        WebSocket socket, WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            await socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the peer may have already torn the connection down, or (round-2 review,
            // issue 1) cancellationToken fired because the peer wasn't draining -- either way this
            // is the connection that's being superseded/dropped, so an incomplete close frame is
            // acceptable; the caller still cancels its CTS/tears it down regardless.
        }
    }

    /// <summary>Rick's #244 round-2 review, issue 1: runs the stale-socket close-and-cancel
    /// sequence that USED to sit inline in <c>HandleResumeFirstFrameAsync</c>, but now off the
    /// NEW (winning) connection's own call stack entirely -- see the call site's doc comment for
    /// why awaiting it there was unsafe. Bounded by <paramref name="closeTimeout"/> so a
    /// non-draining stale peer can delay this method's own completion by at most that long, never
    /// indefinitely; a cancelled <see cref="WebSocket.CloseOutputAsync"/> aborts the stuck send,
    /// which is an acceptable outcome for the connection that's losing anyway.
    ///
    /// Rick's round-2 RE-review (CI run 37210749254): widening <paramref name="closeTimeout"/>
    /// alone (2s -> 10s) was not the whole fix. <paramref name="staleCts"/> is the SAME token
    /// source the stale connection's own relay loop passed into its still-pending
    /// <c>browserSocket.ReceiveAsync</c> (it is reading for a NEXT client frame that will never
    /// come, since this peer just lost the race). Cancelling a token that's registered with an
    /// in-flight <see cref="WebSocket"/> receive/send does not just stop that one call -- per
    /// .NET's documented WebSocket cancellation semantics it ABORTS THE WHOLE SOCKET. Cancelling
    /// <paramref name="staleCts"/> immediately after the courtesy close frame was sent could abort
    /// <paramref name="staleWs"/> before the healthy stale peer's own answering close frame (which
    /// the very same pending <c>ReceiveAsync</c> is waiting to observe) had been processed,
    /// racing away the clean 4002 the peer would otherwise have seen -- exactly the failure mode
    /// behind <c>Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002</c>'s
    /// "Expected 4002, Actual null" under CI load. So: after the close frame is away, wait for
    /// <paramref name="staleWs"/> to leave <see cref="WebSocketState.Open"/>/<see
    /// cref="WebSocketState.CloseSent"/> (i.e. for that already-in-flight receive to notice the
    /// peer's own close reply and complete on its own, harmlessly) before ever touching
    /// <paramref name="staleCts"/> -- reusing the SAME <paramref name="closeTimeout"/> budget so a
    /// genuinely stuck peer (never answers) is still bounded exactly as before. <paramref
    /// name="staleCts"/> is always cancelled in the <c>finally</c> once settled-or-timed-out,
    /// independent of whether the close itself completed, timed out, or threw -- a stale
    /// connection's relay loops must stop either way, this just makes sure that stop can never
    /// itself be the thing that drops the courtesy close frame. Marked <c>internal</c> (not
    /// <c>private</c>) specifically so Backend.Tests can call it directly with a
    /// <c>FakeWebSocket</c> rigged to hang on <c>CloseOutputAsync</c> and assert it still completes
    /// within <paramref name="closeTimeout"/> plus slack -- a deterministic, environment-independent
    /// proof of the fix that doesn't depend on reproducing genuine TCP backpressure.</summary>
    internal static async Task CloseSupersededStaleConnectionAsync(
        WebSocket staleWs,
        CancellationTokenSource? staleCts,
        TimeSpan closeTimeout,
        ILogger? logger = null)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(closeTimeout);
            await CloseOutputIfOpenAsync(
                    staleWs, (WebSocketCloseStatus)SessionManager.SupersededCloseCode, SessionManager.SupersededCloseReason,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            await WaitForStaleSocketToSettleAsync(staleWs, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // CloseOutputIfOpenAsync and WaitForStaleSocketToSettleAsync already swallow their own
            // expected failures/timeouts; this guards the Task.Run itself (e.g. the
            // CancellationTokenSource construction) so a stray exception here can never prevent the
            // finally below from running.
            logger?.LogWarning(ex, "Unexpected failure closing a superseded stale connection's output");
        }
        finally
        {
            staleCts?.Cancel();
        }
    }

    /// <summary>Waits for <paramref name="staleWs"/> to leave <see cref="WebSocketState.Open"/> or
    /// <see cref="WebSocketState.CloseSent"/> -- i.e. for the stale connection's own already-pending
    /// <c>ReceiveAsync</c> to observe the peer's answering close frame (a healthy peer) and
    /// complete on its own, so the caller's subsequent <c>staleCts.Cancel()</c> never has to abort
    /// that receive mid-flight. Polls on <see cref="SupersededSettlePollInterval"/> rather than
    /// reacting to an event because <see cref="WebSocket"/> exposes no "state changed" signal; the
    /// socket is typically a <c>FakeWebSocket</c> or <c>ManagedWebSocket</c>, neither cheap nor
    /// meaningful to wrap further for this. Bounded by <paramref name="cancellationToken"/> (the
    /// SAME budget as the preceding close send) so a genuinely stuck peer that never answers still
    /// falls through to the unconditional cancel in the same overall bounded time as before this
    /// fix -- this method only ever makes the HEALTHY-peer path safer, never the stuck-peer path
    /// slower.</summary>
    private static async Task WaitForStaleSocketToSettleAsync(WebSocket staleWs, CancellationToken cancellationToken)
    {
        try
        {
            while (staleWs.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                await Task.Delay(SupersededSettlePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Budget exhausted waiting for a peer that never answered -- the caller's finally
            // cancels staleCts regardless, which is the same "fine for the loser" outcome this
            // method existed to protect a HEALTHY peer from in the first place.
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Issue #13 Wave 2 audio-append fast path: forwards a frame's ORIGINAL bytes
    /// unchanged, skipping the UTF8-decode + re-encode round trip <see cref="SendTextAsync"/> does
    /// for a frame built from a <see cref="JsonObject"/>. Only ever called with
    /// <see cref="WebSocketFrame.Payload"/> itself, so "identical forwarded bytes" is exact, not
    /// just byte-equal after a round trip. Internal (not private) so
    /// <c>AudioAppendFastPathTests</c> can assert on exactly what reaches the socket without
    /// standing up a real upstream connection.</summary>
    internal static async Task SendBytesAsync(WebSocket socket, byte[] payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Result of <see cref="TryAppendFastPath"/>: whether <paramref name="payload"/>
    /// matched the fast-path shape at all, and if so, whether echo suppression says it must be
    /// dropped rather than forwarded.</summary>
    internal readonly record struct AppendFastPathResult(bool IsMatch, bool Suppressed);

    /// <summary>Issue #13 Wave 2: the audio-append fast path's full decision -- shape match plus
    /// the SAME echo-suppression gate the slow path applies (<c>state.Echo.ShouldSuppressAudio(...)</c>
    /// at line ~442 below) -- extracted to its own internal method (same idiom as
    /// <see cref="ResolveSessionBinding"/>/<see cref="ResolveUpstreamAuthHeaderAsync"/>) so a test
    /// can prove the gating without a live upstream socket.</summary>
    internal AppendFastPathResult TryAppendFastPath(byte[] payload, EchoSuppressor echo)
    {
        if (!TryMatchAppendFastPath(payload))
        {
            return new AppendFastPathResult(IsMatch: false, Suppressed: false);
        }
        return new AppendFastPathResult(IsMatch: true, Suppressed: echo.ShouldSuppressAudio(NowSeconds()));
    }

    /// <summary>Rick's #229 review (round 2): the fast path's actual forward decision --
    /// <c>RelayBrowserToUpstreamAsync</c>'s <c>if (!fastPath.Suppressed)</c> branch -- extracted
    /// to its own internal method so a test can drive it end to end (real shape match, real
    /// echo-suppression gate, real forward) through a fake <see cref="WebSocket"/> stand-in for
    /// <paramref name="upstream"/>, without needing a live upstream connection. A no-op when
    /// <paramref name="fastPath"/> says the frame must be dropped (assistant still speaking);
    /// otherwise forwards <paramref name="payload"/>'s bytes completely unchanged, matching
    /// Python's own fast-path forward (errors are logged and swallowed, same as the slow path,
    /// since a single dropped audio frame must never tear down the whole session).</summary>
    internal async Task ForwardFastPathAudioAsync(AppendFastPathResult fastPath, byte[] payload, WebSocket upstream, string sessionId, CancellationToken ct)
    {
        if (fastPath.Suppressed)
        {
            return;
        }
        try
        {
            await SendBytesAsync(upstream, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Error forwarding fast-path audio append frame (session={SessionId})", sessionId);
        }
    }

    /// <summary>Issue #252 (Rick's review of #237's CI flake, same root cause independently
    /// diagnosed in this PR): <c>RelayBrowserToUpstreamAsync</c>'s send-with-bookkeeping step for
    /// every non-fast-path client→server frame, extracted to its own internal method (same idiom
    /// as <see cref="ForwardFastPathAudioAsync"/> above) so a test can drive the EXACT production
    /// ordering end to end through a fake <see cref="WebSocket"/> whose <c>SendAsync</c>
    /// synchronously simulates "the upstream's reply for this very frame was already fully
    /// processed before the send returns" -- precisely the interleaving a real race under load
    /// would produce -- without needing two concurrently-running relay loops racing for real.
    ///
    /// Recording "this response.create was browser-initiated" (<paramref name="rateLimit"/>'s
    /// <c>OnExternalResponseCreate("browser")</c>) and the symmetrical echo-suppression bookkeeping
    /// MUST happen before <paramref name="forwarded"/> is sent upstream, not after: the old order
    /// (send, then bookkeeping) left a TOCTOU window where upstream's own
    /// response.created/response.done for THIS SAME response.create could complete first --
    /// legitimately scheduling the ladder's first retry -- before this continuation resumed to make
    /// the bookkeeping call, which would then wrongly cancel the very retry it just caused
    /// (mistaking it for a stale leftover one). Recording "browser-initiated" before the send closes
    /// the window by construction: upstream cannot react to a frame it has not received yet.</summary>
    internal async Task ForwardClientFrameAsync(
        JsonObject forwarded,
        string? sentType,
        EchoSuppressor echo,
        RateLimitRecovery rateLimit,
        WebSocket upstream,
        CancellationToken ct)
    {
        if (sentType == "response.create")
        {
            echo.OnExternalResponseCreate();
            rateLimit.OnExternalResponseCreate("browser");
        }

        await SendTextAsync(upstream, forwarded.ToJsonString(), ct).ConfigureAwait(false);

        if (sentType == "response.cancel")
        {
            echo.OnBargeIn();
        }
    }

    /// <summary>Port of app/backend/rtmt.py's <c>_CLIENT_APPEND_FAST_PATH_RE</c> (PR #49 round 2
    /// "M1"): the ONE exact byte shape useRealtime.tsx's <c>addUserAudio()</c> sends --
    /// <c>{"type":"input_audio_buffer.append","audio":"BASE64"}</c>, no <c>event_id</c>, no extra
    /// whitespace, no different key order. Deliberately anchored at both ends and over the whole
    /// payload (not a substring search): PR #49's own review history is why -- an earlier,
    /// unanchored substring fast path could be spoofed by embedding a fake
    /// <c>"type":"input_audio_buffer.append"</c> string inside a nested/arbitrary JSON value.
    /// Anything that doesn't match this exactly (an event_id, extra keys, a byte outside the
    /// base64 alphabet anywhere in the audio value, a trailing byte) falls through to the full
    /// parse + allow-list path below, which still accepts a genuine append frame in any other
    /// shape, just without the fast path's saved JSON-parse/rebuild/re-serialize work.</summary>
    private static readonly byte[] AppendFastPathPrefix =
        Encoding.ASCII.GetBytes("{\"type\":\"input_audio_buffer.append\",\"audio\":\"");
    private static readonly byte[] AppendFastPathSuffix = Encoding.ASCII.GetBytes("\"}");

    internal static bool TryMatchAppendFastPath(byte[] payload)
    {
        if (payload.Length < AppendFastPathPrefix.Length + AppendFastPathSuffix.Length)
        {
            return false;
        }
        if (!payload.AsSpan(0, AppendFastPathPrefix.Length).SequenceEqual(AppendFastPathPrefix))
        {
            return false;
        }
        var suffixStart = payload.Length - AppendFastPathSuffix.Length;
        if (!payload.AsSpan(suffixStart).SequenceEqual(AppendFastPathSuffix))
        {
            return false;
        }
        foreach (var b in payload.AsSpan(AppendFastPathPrefix.Length, suffixStart - AppendFastPathPrefix.Length))
        {
            if (!IsFastPathAudioAlphabetByte(b))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Deliberately matches rtmt.py's <c>_CLIENT_APPEND_FAST_PATH_RE</c> character class
    /// <c>[A-Za-z0-9+/=]</c> exactly, NOT <see cref="ClientServerFilter"/>'s stricter
    /// <c>^[A-Za-z0-9+/]*={0,2}$</c> slow-path audio regex (nor rtmt.py's own equally stricter
    /// <c>_CLIENT_BASE64_RE</c>): both languages' fast-path regexes allow a <c>=</c> anywhere in
    /// the value, any number of times, not just 0-2 trailing padding characters. This is a known,
    /// pre-existing looseness in the fast path versus the slow path in BOTH implementations (not
    /// introduced by this port) -- harmless, because the fast path only ever decides whether a
    /// frame takes the fast lane to the SAME unmodified upstream Azure OpenAI Realtime API, which
    /// independently validates/rejects malformed base64 itself; it never widens what the browser
    /// is allowed to do or what gets accepted as well-formed. Kept exactly as loose as Python's own
    /// fast path so the C# port's forwarding behaviour matches byte-for-byte, per this port's
    /// "match Python's handling exactly" requirement (issue #13).</summary>
    private static bool IsFastPathAudioAlphabetByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'+' || b == (byte)'/' || b == (byte)'=';

    private static string? GetString(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Monotonic "loop time" in seconds, mirroring Python's
    /// <c>asyncio.AbstractEventLoop.time()</c> -- immune to system clock adjustments. Issue #13
    /// Wave 2: sourced from the injected <see cref="_timeProvider"/> (not
    /// <c>Environment.TickCount64</c>) so a <c>FakeTimeProvider</c>-backed test can advance it
    /// deterministically.</summary>
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
    /// private) purely so <see cref="RealtimeProcessorSessionBindingTests"/> can exercise the
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
    /// <see cref="UpstreamAuthHeaderTests"/> can exercise the selection without a real
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
