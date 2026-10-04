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
/// session registry and land with #15 -- context-window monitoring/turn recording, and the
/// fast-path regex/marker-substring optimisations (every frame is fully JSON-parsed instead).
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
        RateLimitSettings? rateLimitSettings = null)
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
        public required SessionIdentifiers Identifiers { get; init; }
        public bool AssistantAudioSeen { get; set; }
        public bool GreetingSent { get; set; }
        public bool SessionMetadataSent { get; set; }
        public Dictionary<string, string> ToolsPending { get; } = new();
        public TaskCompletionSource<bool> SessionConfigured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        // PR #140 R5: NOT #147 (that's the inbound Entra check on the browser-facing /realtime
        // upgrade in Program.cs) -- this picks the OUTBOUND credential for the Azure OpenAI
        // realtime endpoint itself, api-key when one is configured, else a managed-identity
        // bearer token, matching rtmt.py's DefaultAzureCredential fallback.
        try
        {
            var (headerName, headerValue) = await ResolveUpstreamAuthHeaderAsync(cancellationToken).ConfigureAwait(false);
            upstream.Options.SetRequestHeader(headerName, headerValue);

            await upstream.ConnectAsync(BuildUpstreamUri(_upstreamEndpoint, deployment), cancellationToken)
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
            Identifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, sessionId),
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

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
            await SendTextAsync(upstream, """{"type":"input_audio_buffer.clear"}""", ct).ConfigureAwait(false);
            await SendTextAsync(upstream, BuildGreetingFrame().ToJsonString(), ct).ConfigureAwait(false);
            await SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
        }

        JsonObject BuildVoiceUpdateFrame(string newVoice) => new()
        {
            ["type"] = "session.update",
            ["event_id"] = EventIds.NewEventId("sonic_voice"),
            ["session"] = GaSessionTranslator.ToGaSession(new JsonObject { ["voice"] = newVoice }),
        };

        async Task HandleClientExtensionMessageAsync(string msgType, JsonObject message)
        {
            if (msgType != "extension.set_voice")
            {
                // extension.resume/set_verbose_logging/set_log_to_file (#13 scope cuts, see class
                // doc) -- consumed silently, never forwarded upstream. extension.end_session is
                // handled by the caller (RelayBrowserToUpstreamAsync), not here, since it needs to
                // break the relay loop rather than just fall through to the next frame.
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
            return (filtered, msgType);
        }

        async Task RelayBrowserToUpstreamAsync()
        {
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
                    continue;
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
                        // the browser socket with the fixed 1000/"session_ended" shape immediately --
                        // this needs no session registry/resume state (unlike the resume-triggered
                        // 4002 supersede or the idle-timeout 4000, both #15) since it is purely "the
                        // guest asked to leave right now".
                        _logger?.LogInformation("Guest ended session (session={SessionId})", sessionId);
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

                    var hooksEnabled = Environment.GetEnvironmentVariable("CONFORMANCE_TEST_HOOKS") == "1";
                    var (forwarded, sentType) = ProcessClientMessage(message, hooksEnabled);
                    if (forwarded is null)
                    {
                        continue;
                    }

                    await ForwardClientFrameAsync(forwarded, sentType, state.Echo, state.RateLimit, upstream, ct)
                        .ConfigureAwait(false);

                    if (!state.GreetingSent && sentType == "session.update")
                    {
                        await SendGreetingOnceAsync("client-session.update").ConfigureAwait(false);
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
            if (!toolExecutor.ToolNames.Contains(toolName))
            {
                _logger?.LogError("Unknown tool requested: {ToolName} (session={SessionId})", toolName, sessionId);
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
                var result = await toolExecutor.ExecuteAsync(toolName, argumentsDoc.RootElement.Clone(), ct)
                    .ConfigureAwait(false);
                _logger?.LogInformation("Tool '{ToolName}' result direction={Direction} (session={SessionId})",
                    toolName, result.Destination, sessionId);
                outputText = result.Destination is ToolResultDirection.ToServer or ToolResultDirection.ToBoth
                    ? result.ToText() : "";
                sendToClient = result.Destination is ToolResultDirection.ToClient or ToolResultDirection.ToBoth;
                clientText = sendToClient ? result.ToClientText() : null;
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
                if (toolExecutor is IOrderTicketSource ticketSource)
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
            }

            if (!isToolCallResponse)
            {
                var identifiers = state.Identifiers.AdvanceRoundTrip();
                await SendTextAsync(browserSocket, identifiers.ToFrame("extension.round_trip_token").ToJsonString(), ct)
                    .ConfigureAwait(false);
            }

            return message;
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
                        await SendTextAsync(browserSocket,
                            state.Identifiers.ToFrame("extension.session_metadata").ToJsonString(), ct).ConfigureAwait(false);
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
                            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review
                            // "S1"): genuine guest speech is one of rtmt.py's two
                            // reset_for_new_turn() triggers -- breaks the tool-failure streak, same
                            // as the completed-transcription case below.
                            state.ToolFailures.ResetForNewTurn();
                            break;
                        case "conversation.item.input_audio_transcription.completed":
                            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review
                            // "S1"): the other guest-turn signal (besides speech_started) that
                            // resets the tool-failure streak -- a completed input transcription is
                            // still genuine guest activity even if VAD never fired speech_started
                            // first (e.g. push-to-talk clients).
                            state.ToolFailures.ResetForNewTurn();
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
