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
/// execution through <see cref="IToolExecutor"/> (the #13/#14 coordination seam -- #14 lands the
/// real order/search tools; <see cref="StubToolExecutor"/> proves the wire plumbing until then).
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
/// failure-cap ladder (`_ToolFailureTracker`), the full rate-limit retry ladder
/// (`rate_limit.py`'s `RateLimitRecovery` -- this sends one, final `extension.rate_limited`
/// notice instead of retrying), session resume/rehydration itself -- `extension.resume`, the 4002
/// supersede-close, and the 4000 idle-timeout close all need a real session registry and land with
/// #15 -- context-window monitoring/turn recording, and the fast-path regex/marker-substring
/// optimisations (every frame is fully JSON-parsed instead). A guest-initiated
/// `extension.end_session` (1000/"session_ended") needs none of that registry state, so it *is*
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
    private readonly IReadOnlySet<string> _allowedVoices;
    private readonly double _echoCooldownSeconds;
    private readonly double _greetingTimeoutSeconds;
    private readonly ILogger? _logger;

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
        IUpstreamBearerTokenProvider? bearerTokenProvider = null)
    {
        _catalog = catalog;
        _defaultDeployment = defaultDeployment;
        _upstreamEndpoint = upstreamEndpoint;
        _upstreamApiKey = upstreamApiKey;
        _bearerTokenProvider = bearerTokenProvider;
        _sessionConfig = sessionConfig;
        _promptLoaders = promptLoaders;
        _toolExecutor = toolExecutor;
        _allowedVoices = allowedVoices ?? ClientServerFilter.DefaultAllowedVoices;
        _echoCooldownSeconds = echoCooldownSeconds;
        _greetingTimeoutSeconds = greetingTimeoutSeconds;
        _logger = logger;
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
    /// </summary>
    public async Task RunSessionAsync(
        WebSocket browserSocket,
        Persona persona,
        ResolvedModel resolvedModel,
        string sessionId,
        CancellationToken cancellationToken)
    {
        _promptLoaders.TryGetValue(persona.Id, out var promptLoader);
        var systemMessage = promptLoader?.SystemPrompt;
        var voice = ClientServerFilter.SanitizeVoice(persona.Voice.Default, _allowedVoices)
            ?? _sessionConfig.VoiceChoice ?? "marin";
        var toolSchemas = BuildToolSchemas(promptLoader);
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
                flushCt => SendTextAsync(upstream, """{"type":"input_audio_buffer.clear"}""", flushCt)),
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
                await state.SessionConfigured.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
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

                JsonObject message;
                string msgType;
                try
                {
                    // Parse strictly: AllowDuplicateProperties = false makes JsonNode.Parse throw
                    // JsonException immediately for a duplicate key at ANY depth (top-level "type"
                    // as well as a nested duplicate inside "session"), instead of the old lazy
                    // JsonObject dictionary deferring the throw (an ArgumentException) until
                    // something later indexes into the offending object -- which, for a nested
                    // duplicate, happened outside this try (inside ProcessClientMessage), faulting
                    // the loop outright. The ArgumentException catch stays as a defense-in-depth
                    // belt-and-suspenders for any other lazy-dictionary access this doesn't cover.
                    message = JsonNode.Parse(
                            Encoding.UTF8.GetString(frame.Payload),
                            nodeOptions: null,
                            documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false })
                        as JsonObject
                        ?? throw new JsonException("Client frame was not a JSON object.");
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

                    await SendTextAsync(upstream, forwarded.ToJsonString(), ct).ConfigureAwait(false);

                    if (sentType == "response.cancel")
                    {
                        state.Echo.OnBargeIn();
                    }
                    else if (sentType == "response.create")
                    {
                        state.Echo.OnExternalResponseCreate();
                    }

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

            if (RateLimitDetection.IsRateLimitError(err))
            {
                // Scope cut (#13): the full retry ladder (rate_limit.py's RateLimitRecovery) is
                // skipped -- one final `extension.rate_limited` notice tells the browser to
                // apologise instead of silently dropping the guest's turn.
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.rate_limited",
                    ["attempt"] = 1,
                    ["final"] = true,
                }.ToJsonString(), ct).ConfigureAwait(false);
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
            if (!_toolExecutor.ToolNames.Contains(toolName))
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
                var result = await _toolExecutor.ExecuteAsync(toolName, argumentsDoc.RootElement.Clone(), ct)
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
                // #14's order-ticket-refresh-on-failure (order_state_singleton.get_order_summary_json)
                // is out of scope for #13's stub tool executor -- no order state exists yet to
                // refresh a ticket from.
                outputText = "Something went wrong with that action and it did not complete. Don't retry it yet -- " +
                    "call get_order to confirm the order's current state, then ask the guest to repeat what they'd like.";
                sendToClient = false;
                clientText = null;
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
            if (state.ToolsPending.Count > 0)
            {
                state.ToolsPending.Clear();
                // Scope cut (#13): the tool-failure-cap ladder (_ToolFailureTracker) is skipped --
                // always send a bare, tool-free response.create so the model can act on the
                // function_call_output(s) already in the conversation.
                await SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
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
                    // response.created (rate-limit recovery hook skipped, #13 scope cut) and
                    // anything else not otherwise handled falls through unchanged, exactly like
                    // rtmt.py's `updated_message = data` default.
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
                    // See the client→server side's comment: strict parsing (AllowDuplicateProperties
                    // = false) turns a duplicate key at any depth into an immediate JsonException
                    // here, instead of a deferred ArgumentException from indexing into the object
                    // later. Kept even though the fake upstream in practice never sends a
                    // duplicate-key frame, so a future real-upstream one can't crash the relay.
                    message = JsonNode.Parse(
                            Encoding.UTF8.GetString(frame.Payload),
                            nodeOptions: null,
                            documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false })
                        as JsonObject
                        ?? throw new JsonException("Upstream frame was not a JSON object.");
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

    private static string? GetString(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Monotonic "loop time" in seconds, mirroring Python's
    /// <c>asyncio.AbstractEventLoop.time()</c> -- immune to system clock adjustments.</summary>
    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

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
