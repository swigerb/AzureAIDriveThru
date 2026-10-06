using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Port of app/backend/cascade_processor.py's <c>CascadeProcessor</c> (issue #13's S3 middle-tier
/// tail, issue #82): the STT -&gt; chat-completions-with-tools -&gt; TTS pipeline, for personas/models
/// that opt into <c>models.cascade</c> instead of the Realtime API
/// (<see cref="RealtimeProcessor"/>). Reuses the SAME <see cref="IToolExecutor"/>/
/// <c>OrderState</c>/<c>SearchTool</c> machinery realtime does (built once per connection by the
/// SAME <c>BuildSessionToolExecutor</c> factory Program.cs already wires for realtime) so pricing,
/// menu validation, and tool results are identical regardless of which pipeline a persona/model
/// binds to -- and speaks the SAME wire protocol toward the browser
/// (<c>extension.session_metadata</c>, <c>extension.middle_tier_tool_response</c>,
/// <c>extension.round_trip_token</c>, <c>response.created</c>/<c>response.audio_transcript.delta</c>/
/// <c>response.audio.delta</c>/<c>response.done</c>, <c>extension.rate_limited</c>) so the frontend
/// needs no changes to render either pipeline.
///
/// Unlike <see cref="RealtimeProcessor"/> there is no single upstream WebSocket to relay -- this
/// class owns three independent upstream REST calls per turn (<see cref="FoundryChatClient"/> for
/// chat-completions-with-tools, <see cref="FoundryAudioClient"/> for STT/TTS) and its own local
/// voice-activity detector (<see cref="TurnDetector"/>) to decide when a guest has started/stopped
/// speaking, since there is no upstream `server_vad` doing that for it (the whole reason cascade
/// exists: see cascade_processor.py's own module docstring). A guest's speech-burst audio is
/// buffered locally (not streamed upstream frame-by-frame) and uploaded as one WAV file once
/// <see cref="TurnDetector"/> reports <c>speech_stopped</c>.
///
/// Session registry (issue #126): <see cref="RunSessionAsync"/> registers every connection with
/// <see cref="SessionManager.CreateSession"/> (exactly like <see cref="RealtimeProcessor"/> and
/// cascade_processor.py's own <c>create_session</c>), which also creates the session's
/// <see cref="ContextMonitor"/>; the registry's own end-of-session path removes it. Resume
/// (<c>extension.resume</c> via <c>NegotiateResumeAsync</c>), idle nudge and the echo-suppression
/// cooldown mirror the realtime pipeline. `ExecuteToolCallAsync` tracks tool call
/// args/result in the context monitor, mirroring cascade_processor.py's own two
/// <c>ctx_monitor.add_content</c> call sites exactly.
/// </summary>
internal sealed class CascadeProcessor : IPipelineProcessor
{
    private const int MaxToolRounds = 8;
    private const int AudioSampleRate = 24000;
    private const int TtsChunkBytes = 24000;

    private readonly ModelCatalog _catalog;
    private readonly FoundryChatClient _chatClient;
    private readonly FoundryAudioClient _audioClient;
    private readonly CascadeRateLimitSettings _rateLimitSettings;
    private readonly CascadeVadConfig _vadConfig;
    private readonly IReadOnlyDictionary<string, PromptLoader> _promptLoaders;
    private readonly IToolExecutor _toolExecutor;
    private readonly Func<Persona, PromptLoader?, string?, IToolExecutor>? _toolExecutorFactory;
    private readonly IReadOnlySet<string> _allowedVoices;
    private readonly string _defaultVoice;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    // #126: acoustic tail after estimated playback during which mic audio is still dropped.
    // Derived from the same `audio.echo_cooldown_seconds` the realtime pipeline uses, but capped
    // at 300ms: realtime's 1.5s only delays a buffer clear (#187/#190), whereas cascade's
    // suppression is a hard drop, so a long value would swallow short guest replies. 0 =>
    // echo suppression disabled entirely. Identical to cascade_processor.py's `_echo_tail_seconds`.
    private readonly double _echoCooldownSeconds;
    internal const double EchoTailMaxSeconds = 0.3;

    internal static double EchoTailSeconds(double configured) =>
        Math.Max(0.0, Math.Min(configured, EchoTailMaxSeconds));
    // #126: the session registry (resume/rehydration/idle/grace/nudge) -- mirrors
    // RealtimeProcessor's own `_sessionManager` field exactly, including its null-is-inert
    // convention: every pre-#126 caller/test that constructs a CascadeProcessor without passing
    // one keeps today's exact behaviour (immediate extension.session_metadata, immediate
    // greeting, no resume handshake, extension.resume silently ignored).
    private readonly SessionManager? _sessionManager;

    public CascadeProcessor(
        ModelCatalog catalog,
        string foundryEndpoint,
        string audioEndpoint,
        AppConfig appConfig,
        IReadOnlyDictionary<string, PromptLoader> promptLoaders,
        IToolExecutor toolExecutor,
        HttpClient httpClient,
        IReadOnlySet<string>? allowedVoices = null,
        string defaultVoice = "marin",
        ILogger? logger = null,
        IUpstreamBearerTokenProvider? bearerTokenProvider = null,
        Func<Persona, PromptLoader?, string?, IToolExecutor>? toolExecutorFactory = null,
        TimeProvider? timeProvider = null,
        double echoCooldownSeconds = 1.5,
        SessionManager? sessionManager = null)
    {
        _catalog = catalog;
        var credential = bearerTokenProvider ?? DefaultAzureCredentialTokenProvider.Instance.Value;
        _chatClient = new FoundryChatClient(httpClient, foundryEndpoint, credential);
        _audioClient = new FoundryAudioClient(httpClient, audioEndpoint, credential);
        _rateLimitSettings = CascadeRateLimitSettings.FromAppConfig(appConfig);
        _vadConfig = CascadeVadConfig.FromAppConfig(appConfig);
        _promptLoaders = promptLoaders;
        _toolExecutor = toolExecutor;
        _toolExecutorFactory = toolExecutorFactory;
        _allowedVoices = allowedVoices ?? ClientServerFilter.DefaultAllowedVoices;
        _defaultVoice = defaultVoice;
        _logger = logger;
        // Issue #13 Wave 2 convention (RealtimeProcessor's own _timeProvider): the rate-limit
        // ladder's wall-clock delays (CascadeRateLimit.WithRetryAsync) are driven from this one
        // clock, so a test can swap in a FakeTimeProvider instead of waiting on the real 0.5-8s
        // delays. Defaults to TimeProvider.System in production.
        _timeProvider = timeProvider ?? TimeProvider.System;
        _echoCooldownSeconds = EchoTailSeconds(echoCooldownSeconds);
        _sessionManager = sessionManager;
    }

    public string PipelineName => "cascade";

    public ResolvedModel ResolveModel(Persona persona, string? requestedModelId) =>
        ModelDispatch.ResolveCascadeModel(persona, requestedModelId, _catalog);

    /// <summary>Nothing posts to this processor's mailbox -- <see cref="RunSessionAsync"/> owns
    /// the cascade pipeline's whole session loop directly, exactly like
    /// <see cref="RealtimeProcessor.ProcessAsync"/>.</summary>
    public Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Per-connection mutable state -- the C# equivalent of cascade_processor.py's
    /// <c>_CascadeSessionState</c> dataclass.</summary>
    private sealed class CascadeSessionState
    {
        public required string SessionId { get; init; }
        public required string Deployment { get; init; }
        public string Voice { get; set; } = "marin";
        public List<JsonObject> Messages { get; } = [];
        public Task? CurrentTurnTask { get; set; }
        public CancellationTokenSource? CurrentTurnCts { get; set; }
        // Completes once the most recent barge-in's cancelled turn has fully stopped and its
        // speech_started frame has been sent -- see RunSessionAsync's BargeIn helper.
        public Task? BargeInTail { get; set; }

        // #126 one-shot resume nudge (mirrors rtmt.py's/RealtimeProcessor's own
        // NudgeScheduler-arming pair, kept as plain fields here -- not a NudgeScheduler instance --
        // since cascade has no upstream `session.update`/rate-limit-busy gate to wait on; its own
        // "skip if a turn is in flight" check is just CurrentTurnTask, already on this object).
        // NudgeEligible is set True only by a mid-conversation resume with `nudge_after_seconds >
        // 0`; NudgeArmed latches once the nudge timer has actually been scheduled (this socket's
        // own first proof of liveness -- its first streamed mic chunk), so arming can only ever
        // happen once per connection.
        public bool NudgeEligible { get; set; }
        public bool NudgeArmed { get; set; }
        public Task? NudgeTask { get; set; }
        public CancellationTokenSource? NudgeCts { get; set; }

        /// <summary>This connection's own bound persona's role label (e.g. "carhop") -- needed by
        /// <see cref="SessionManager.BuildRehydrationText"/>/<see cref="SessionManager.BuildNudgeText"/>,
        /// both of which address the guest in-persona.</summary>
        public string RoleName { get; set; } = "team member";
    }

    /// <summary>
    /// Owns one session's whole cascade loop: emits <c>extension.session_metadata</c>, spawns the
    /// connect-time greeting as a background task (so a guest can barge in on it, same as every
    /// real guest turn), then reads+reassembles browser messages until the socket closes,
    /// dispatching each to <see cref="TurnDetector"/>/the chat-tool-loop/TTS as appropriate. Mirrors
    /// cascade_processor.py's <c>handle</c> + <c>_run_session</c> -- Program.cs's <c>/realtime</c>
    /// handler already owns the WebSocket accept/session-registry/teardown wrapper generically
    /// (identically for every pipeline), so this method only needs the INNER session loop, exactly
    /// like <see cref="RealtimeProcessor.RunSessionAsync"/> does for realtime.
    /// </summary>
    public async Task RunSessionAsync(
        WebSocket browserSocket,
        Persona persona,
        ResolvedModel resolvedModel,
        string sessionId,
        CancellationToken cancellationToken,
        string? menuMode = null)
    {
        _promptLoaders.TryGetValue(persona.Id, out var promptLoader);
        var toolSchemas = BuildToolSchemas(promptLoader);
        var toolDefinitions = CascadeToolDefinitions.FromToolSchemas(toolSchemas);
        var toolExecutor = _toolExecutorFactory?.Invoke(persona, promptLoader, menuMode) ?? _toolExecutor;
        var voice = ClientServerFilter.SanitizeVoice(persona.Voice.Default, _allowedVoices) ?? _defaultVoice;
        var identifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, sessionId, pipeline: "cascade");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var state = new CascadeSessionState
        {
            SessionId = sessionId,
            Deployment = resolvedModel.Deployment,
            Voice = voice,
            RoleName = persona.RoleName,
        };
        if (promptLoader is not null)
        {
            state.Messages.Add(CascadeChatMessage.System(promptLoader.SystemPrompt));
        }
        var detector = new TurnDetector(_vadConfig.Threshold, _vadConfig.SilenceDurationMs, AudioSampleRate);

        // #126 fix: set by NegotiateResumeAsync when its first-frame timer wins the race against
        // the in-flight ReadMessageAsync call (see that method's own doc comment for why this
        // read is never cancelled) -- the main receive loop below must await this instead of
        // issuing a second, concurrent ReadMessageAsync on the same socket.
        Task<WebSocketFrame?>? pendingFirstFrameTask = null;

        // #126: register this connection with the session registry up front, mirroring
        // RealtimeProcessor.RunSessionAsync's own create_session(...) call before its relay loop
        // starts -- entirely inert when no SessionManager was injected (see that field's own doc
        // comment), so every pre-#126 caller/test keeps today's exact behaviour.
        _sessionManager?.CreateSession(
            sessionId, browserSocket, persona.Id, resolvedModel.Id, menuMode, toolExecutor, voice,
            attachedCts: linkedCts, identifiers: identifiers);

        // ── Local helpers (closures over browserSocket/state/toolDefinitions/toolExecutor/...) ──
        // Mirrors RealtimeProcessor.RunSessionAsync's own nested-function style (and
        // cascade_processor.py's own nested-function style inside _run_session/_handle_client_message).

        Task NotifyClientAsync(JsonObject frame, CancellationToken notifyCt) =>
            SendTextAsync(browserSocket, frame.ToJsonString(), notifyCt, ct);

        // #126: a monotonic-comparable clock reading for TurnDetector's echo-cooldown deadline
        // math, driven off `_timeProvider` (so a FakeTimeProvider-based test can control it)
        // rather than `DateTime.UtcNow` directly.
        // Guards the CurrentTurn*/Nudge* registry: a firing nudge hands itself over to
        // CurrentTurnTask from a pool thread while the receive loop may be in BargeIn/CancelNudge.
        var turnRegistryLock = new object();

        double NowSeconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

        async Task CancelCurrentTurnAsync(string reason)
        {
            Task? task;
            CancellationTokenSource? cts;
            lock (turnRegistryLock)
            {
                task = state.CurrentTurnTask;
                cts = state.CurrentTurnCts;
            }
            if (task is null)
            {
                return;
            }
            if (task.IsCompleted)
            {
                state.CurrentTurnTask = null;
                state.CurrentTurnCts = null;
                cts?.Dispose();
                return;
            }
            cts?.Cancel();
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected -- that's what cancelling the turn is for.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cascade turn ended with an unexpected exception while cancelling it (session={SessionId})", sessionId);
            }
            _logger?.LogInformation("Cancelled in-flight cascade turn: {Reason} (session={SessionId})", reason, sessionId);
            state.CurrentTurnTask = null;
            state.CurrentTurnCts = null;
            cts?.Dispose();
        }

        // #126/PR #290: barge-in must NEVER block the browser receive loop. It used to call
        // CancelCurrentTurnAsync (cancel + AWAIT the turn task) inline from HandleClientMessageAsync,
        // then send speech_started inline too. But a cancelled turn can only stop once its
        // in-flight browser SendAsync finishes (SendTextAsync deliberately never hands turnCt to the
        // real write -- see its #236 doc comment), and that write only finishes once the browser
        // reads. If the browser is itself blocked WRITING to us (e.g. streaming more mic audio
        // while our TTS reply has filled the socket), both peers sit in a write that the other
        // never reads: a hard deadlock that stalls the session until teardown. So the receive loop
        // only cancels and detaches the turn here, then keeps reading; a chained "barge-in tail"
        // waits for the cancelled turn to really stop and only then sends speech_started (so no
        // stale audio from the cut-off reply can follow it), and the next turn waits for that tail
        // before it starts (so its frames always follow speech_started, and it never mutates
        // state.Messages concurrently with the cancelled turn's own cleanup).
        void BargeIn()
        {
            Task? task;
            CancellationTokenSource? cts;
            lock (turnRegistryLock)
            {
                task = state.CurrentTurnTask;
                cts = state.CurrentTurnCts;
                state.CurrentTurnTask = null;
                state.CurrentTurnCts = null;
            }
            var wasInFlight = task is { IsCompleted: false };
            cts?.Cancel();
            var previousTail = state.BargeInTail;
            state.BargeInTail = Task.Run(async () =>
            {
                await AwaitQuietlyAsync(previousTail).ConfigureAwait(false);
                await AwaitQuietlyAsync(task).ConfigureAwait(false);
                cts?.Dispose();
                if (wasInFlight)
                {
                    _logger?.LogInformation("Cancelled in-flight cascade turn: {Reason} (session={SessionId})",
                        "guest started speaking (barge-in)", sessionId);
                }
                try
                {
                    await SendTextAsync(browserSocket, """{"type":"input_audio_buffer.speech_started"}""", ct, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    // Session is shutting down or the socket is gone -- nothing left to notify.
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not send speech_started after a barge-in (session={SessionId})", sessionId);
                }
            });
        }

        async Task AwaitQuietlyAsync(Task? t)
        {
            if (t is null)
            {
                return;
            }
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected -- that's what cancelling the turn is for.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cascade turn ended with an unexpected exception while cancelling it (session={SessionId})", sessionId);
            }
        }

        (CancellationTokenSource Cts, Task Task) Spawn(Func<CancellationToken, Task> body, string label)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(async () =>
            {
                try
                {
                    await body(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    // Expected when this turn is barged in on -- silent, same as cascade_processor.py's
                    // own background-task wrapper swallowing asyncio.CancelledError.
                    //
                    // Follow-up to #236 Rick re-review item 3: the same `when` guard used at each
                    // inner catch (ExecuteToolCallAsync/TranscribeAsync/SpeakAsync) belongs here too.
                    // Without it, an OperationCanceledException from something unrelated to a
                    // barge-in or session shutdown -- e.g. an HttpClient-internal timeout
                    // (TaskCanceledException) surfacing from the chat-completion call itself, which
                    // isn't individually try/caught anywhere between here and RunChatToolLoopAsync --
                    // would be misclassified as "the turn was barged in on" and silently swallowed,
                    // with nothing logged and no response.done ever reaching the guest. Filtering on
                    // `cts.Token.IsCancellationRequested` (this Spawn call's own linked token, the
                    // same one `body` was invoked with) means a genuine non-barge-in timeout instead
                    // falls through to the `catch (Exception ex)` below, which logs it.
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unhandled exception in cascade background task '{Label}' (session={SessionId})", label, sessionId);
                }
            });
            return (cts, task);
        }

        // #126, mirrors rtmt.py's own nudge_after_silence/cancel_nudge: a ONE-SHOT timer, armed
        // at most once per connection (HandleClientMessageAsync's own NudgeArmed latch below). If
        // the guest stays silent for `nudge_after_seconds` and no turn is in flight (mid-turn,
        // mid-greeting, or the assistant is already speaking), nudge once through this pipeline's
        // own normal chat-tool-loop + TTS turn machinery -- never stacked on top of a turn, never
        // rescheduled.
        void ScheduleNudge()
        {
            var (cts, task) = Spawn(async nudgeCt =>
            {
                await Task.Delay(TimeSpan.FromSeconds(_sessionManager!.Config.NudgeAfterSeconds), _timeProvider, nudgeCt)
                    .ConfigureAwait(false);
                bool skipped;
                lock (turnRegistryLock)
                {
                    if (nudgeCt.IsCancellationRequested)
                    {
                        return;
                    }
                    var inFlight = state.CurrentTurnTask;
                    // A barged-in turn still winding down (BargeInTail) counts as in flight too.
                    skipped = (inFlight is not null && !inFlight.IsCompleted) || state.BargeInTail is { IsCompleted: false };
                    if (!skipped)
                    {
                        // The firing nudge becomes a real turn: BargeIn and teardown cancel AND
                        // await it via CurrentTurnTask/CurrentTurnCts like any other turn, so no
                        // audio follows speech_started and its Messages cleanup never races the
                        // next turn. NudgeTask/NudgeCts hand over ownership (CancelNudge is then
                        // a no-op for it).
                        state.CurrentTurnTask = state.NudgeTask;
                        state.CurrentTurnCts = state.NudgeCts;
                        state.NudgeTask = null;
                        state.NudgeCts = null;
                    }
                }
                if (skipped)
                {
                    // Mid-turn, or the assistant is already speaking -- never stack a nudge on
                    // top of a real turn. One-shot: a skipped nudge is not rescheduled.
                    _logger?.LogInformation("Cascade: resume nudge skipped, a turn is in flight (session={SessionId})", sessionId);
                    return;
                }
                _logger?.LogInformation(
                    "Cascade: guest silent {Seconds}s after resume; nudging (session={SessionId})",
                    _sessionManager!.Config.NudgeAfterSeconds, sessionId);
                state.Messages.Add(CascadeChatMessage.User(SessionManager.BuildNudgeText(state.RoleName)));
                await RunTurnAndSpeakAsync(nudgeCt).ConfigureAwait(false);
            }, "nudge");
            state.NudgeCts = cts;
            state.NudgeTask = task;
        }

        // #126, mirrors rtmt.py's own cancel_nudge: cancels a still-pending nudge timer. A no-op
        // if none is pending (e.g. this connection was never nudge-eligible, or the nudge already
        // fired).
        void CancelNudge(string reason)
        {
            Task? task;
            CancellationTokenSource? cts;
            lock (turnRegistryLock)
            {
                task = state.NudgeTask;
                cts = state.NudgeCts;
                state.NudgeTask = null;
                state.NudgeCts = null;
                if (task is not null && !task.IsCompleted)
                {
                    cts?.Cancel();
                    _logger?.LogInformation("Cascade: resume nudge cancelled: {Reason} (session={SessionId})", reason, sessionId);
                }
            }
            cts?.Dispose();
        }

        async Task<JsonObject> CallChatCompletionAsync(CancellationToken turnCt) =>
            await CascadeRateLimit.WithRetryAsync(
                _rateLimitSettings,
                () => _chatClient.CompleteAsync(state.Messages, state.Deployment, toolDefinitions, turnCt),
                "chat completion", NotifyClientAsync, sessionId, _logger, turnCt, _timeProvider).ConfigureAwait(false);

        async Task ExecuteToolCallAsync(JsonObject toolCall, string previousItemId, CancellationToken turnCt)
        {
            var callId = GetString(toolCall, "id") ?? "";
            var name = GetString(toolCall["function"] as JsonObject, "name") ?? "";
            if (!toolExecutor.ToolNames.Contains(name))
            {
                _logger?.LogError("Unknown tool requested: {ToolName} (session={SessionId})", name, sessionId);
                state.Messages.Add(CascadeChatMessage.Tool("", callId));
                return;
            }

            string outputText;
            bool sendToClient;
            string? clientText;
            try
            {
                // Mirrors Python's `tool_call.function.arguments or "{}"`: treat BOTH a missing
                // field and an empty string the same way (some tool calls with no parameters come
                // back as `"arguments": ""`, not an omitted field -- `?? "{}"` alone only covers
                // the missing-field case and would otherwise send "" into JsonDocument.Parse,
                // throwing and routing a legitimate no-arg call into the generic error branch below).
                var rawArguments = GetString(toolCall["function"] as JsonObject, "arguments");
                var argumentsJson = string.IsNullOrEmpty(rawArguments) ? "{}" : rawArguments;
                using var argumentsDoc = JsonDocument.Parse(argumentsJson);
                _logger?.LogInformation("Executing cascade tool '{ToolName}' (session={SessionId})", name, sessionId);
                var result = await toolExecutor.ExecuteAsync(name, argumentsDoc.RootElement.Clone(), turnCt).ConfigureAwait(false);
                _logger?.LogInformation("Cascade tool '{ToolName}' result direction={Direction} (session={SessionId})",
                    name, result.Destination, sessionId);

                // Issue #13 tail: track tool call args + result in the context window, mirroring
                // cascade_processor.py's own ctx_monitor.add_content(tool_call.function.arguments
                // or "") / ctx_monitor.add_content(result.to_text()) right after logging the
                // result.
                var ctxMonitorForTool = _sessionManager?.GetContextMonitor(sessionId);
                if (ctxMonitorForTool is not null)
                {
                    ctxMonitorForTool.AddContent(argumentsJson);
                    ctxMonitorForTool.AddContent(result.ToText());
                }

                outputText = result.Destination is ToolResultDirection.ToServer or ToolResultDirection.ToBoth
                    ? result.ToText() : "";
                sendToClient = result.Destination is ToolResultDirection.ToClient or ToolResultDirection.ToBoth;
                clientText = sendToClient ? result.ToClientText() : null;
            }
            catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
            {
                // Python's mirror-image `except Exception:` here (cascade_processor.py's
                // `_execute_tool_call`) never catches cancellation in the first place --
                // `asyncio.CancelledError` derives from `BaseException`, not `Exception`. C#'s
                // `OperationCanceledException` DOES derive from `Exception`, so without this
                // clause a guest barging in mid-tool-call would get logged as a tool failure and
                // a synthetic "something went wrong" error message appended to history, instead
                // of the turn just quietly ending the way barge-in (`BargeIn`) expects.
                //
                // #236 Rick re-review item 3 (LOW): the `when` guard matters -- an
                // `OperationCanceledException` can also come from an HttpClient-internal timeout
                // (a `TaskCanceledException`, which derives from `OperationCanceledException`)
                // that has NOTHING to do with a barge-in -- `turnCt` itself was never cancelled.
                // Without this guard, that would be misclassified as "the turn was barged in on"
                // and silently swallowed here (re-thrown, then silently absorbed by `Spawn`'s own
                // catch), with no log and no `response.done` ever reaching the guest. Filtering on
                // `turnCt.IsCancellationRequested` means a genuine non-barge-in timeout instead
                // falls through to the `catch (Exception ex)` below, which DOES log it and still
                // lets the turn finish (synthetic tool-failure message, `response.done` still sent).
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Cascade tool '{ToolName}' raised an unhandled exception (session={SessionId})", name, sessionId);
                outputText = promptLoader?.RenderError("tool_execution_failed") ??
                    "Something went wrong with that action and it did not complete. Don't retry it yet -- " +
                    "call get_order to confirm the order's current state, then ask the guest to repeat what they'd like.";
                sendToClient = false;
                clientText = null;

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
                            "Could not read order state to refresh the ticket after a cascade tool failure (session={SessionId})",
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
                        }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
                    }
                }
            }

            state.Messages.Add(CascadeChatMessage.Tool(outputText, callId));
            if (sendToClient)
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.middle_tier_tool_response",
                    ["previous_item_id"] = previousItemId,
                    ["tool_name"] = name,
                    ["tool_result"] = clientText,
                }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
            }
        }

        async Task<string> RunChatToolLoopAsync(CancellationToken turnCt)
        {
            for (var round = 0; round < MaxToolRounds; round++)
            {
                var message = await CallChatCompletionAsync(turnCt).ConfigureAwait(false);
                var toolCalls = message["tool_calls"] as JsonArray;
                if (toolCalls is null || toolCalls.Count == 0)
                {
                    var content = GetString(message, "content") ?? "";
                    state.Messages.Add(CascadeChatMessage.Assistant(content));
                    return content;
                }

                var preRoundCount = state.Messages.Count;
                state.Messages.Add((JsonObject)message.DeepClone());
                var previousItemId = MiddleTierItemIds.NewId();
                try
                {
                    foreach (var toolCallNode in toolCalls)
                    {
                        if (toolCallNode is JsonObject toolCall)
                        {
                            await ExecuteToolCallAsync(toolCall, previousItemId, turnCt).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // #247: a barge-in (`OperationCanceledException`, re-thrown unchanged by
                    // `ExecuteToolCallAsync`'s own `when (turnCt.IsCancellationRequested)` guard)
                    // or any other failure mid-round can leave some of this round's `toolCalls`
                    // ids answered (a tool message appended) and others not. A chat API that sees
                    // an assistant `tool_calls` message without a matching tool message for EVERY
                    // id 400s the next request -- exactly what the fake chat server in the
                    // conformance scenario enforces. Rather than appending neutral placeholder
                    // tool messages for the unanswered ids, truncate the whole round back out of
                    // `state.Messages`: history is then always either "fully pre-round" or "fully
                    // post-round", never partially answered, and the next turn's model can always
                    // re-discover real-world state via `get_order` (the same tool-failure-recovery
                    // instruction `ExecuteToolCallAsync` already gives it), so nothing is actually
                    // lost. A tool that already mutated the order (e.g. `update_order`) keeps its
                    // real-world effect -- the order store is untouched by this truncation.
                    state.Messages.RemoveRange(preRoundCount, state.Messages.Count - preRoundCount);
                    throw;
                }
            }
            _logger?.LogWarning("Cascade chat-tool loop hit its {MaxRounds}-round cap without a final answer (session={SessionId})",
                MaxToolRounds, sessionId);
            return "";
        }

        async Task<string> TranscribeAsync(byte[] turnAudio, CancellationToken turnCt)
        {
            var cascadeAudio = _catalog.CascadeAudio
                ?? throw new InvalidOperationException("config.yaml has no models.cascade transcription/tts configured.");
            var deployment = _catalog.DeploymentFor(cascadeAudio.Transcription)
                ?? throw new InvalidOperationException(
                    $"Cascade transcription model '{cascadeAudio.Transcription}' has no AZURE_AI_MODEL_DEPLOYMENTS entry.");
            return await CascadeRateLimit.WithRetryAsync(
                _rateLimitSettings,
                () => _audioClient.TranscribeAsync(turnAudio, deployment, AudioSampleRate, turnCt),
                "transcription", NotifyClientAsync, sessionId, _logger, turnCt, _timeProvider).ConfigureAwait(false);
        }

        async Task SpeakAsync(string text, CancellationToken turnCt)
        {
            var cascadeAudio = _catalog.CascadeAudio
                ?? throw new InvalidOperationException("config.yaml has no models.cascade transcription/tts configured.");
            var deployment = _catalog.DeploymentFor(cascadeAudio.Tts)
                ?? throw new InvalidOperationException(
                    $"Cascade TTS model '{cascadeAudio.Tts}' has no AZURE_AI_MODEL_DEPLOYMENTS entry.");
            // Issue #304: apply this persona's own phonetic pronunciation lexicon ONLY to the TTS
            // input text -- never to the chat transcript/history sent to the browser (that still
            // carries the unmodified `text`). Mirrors cascade_processor.py's
            // `_apply_pronunciations`/`_speak`.
            var ttsText = persona.Pronunciations is { Count: > 0 } pronunciations
                ? MenuCatalog.ApplyLexicon(text, pronunciations)
                : text;
            await CascadeRateLimit.WithRetryAsync(
                _rateLimitSettings,
                async () =>
                {
                    var pcm = await _audioClient.SpeakAsync(ttsText, state.Voice, deployment, turnCt).ConfigureAwait(false);
                    if (_echoCooldownSeconds > 0)
                    {
                        // #126: arm echo suppression for the GUEST'S estimated speaker playback
                        // of this reply (not this loop's own fast send time) plus a short acoustic
                        // tail (`_echoCooldownSeconds`, capped at 300ms in the constructor).
                        // 0 disables suppression entirely, identically to cascade_processor.py's
                        // `_speak`. PCM16 mono => 2 bytes/sample.
                        var durationSeconds = pcm.Length / (double)(AudioSampleRate * 2);
                        detector.StartEchoCooldown(durationSeconds + _echoCooldownSeconds, NowSeconds());
                    }
                    for (var offset = 0; offset < pcm.Length; offset += TtsChunkBytes)
                    {
                        var chunkLength = Math.Min(TtsChunkBytes, pcm.Length - offset);
                        var chunk = pcm.AsSpan(offset, chunkLength).ToArray();
                        await SendTextAsync(browserSocket, new JsonObject
                        {
                            ["type"] = "response.audio.delta",
                            ["delta"] = Convert.ToBase64String(chunk),
                        }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
                    }
                },
                "text-to-speech", NotifyClientAsync, sessionId, _logger, turnCt, _timeProvider).ConfigureAwait(false);
        }

        async Task SendFailedResponseDoneAsync(string responseId, string message, CancellationToken turnCt)
        {
            // #262: closes out a turn that failed (non-429) after `response.created` was already
            // sent, so the browser is never left thinking a response is still in progress. Shape
            // mirrors the Realtime API's own failed-response `response.done` (`status: "failed"`,
            // `status_details.error`) -- see RealtimeProcessor's own passthrough of upstream's
            // `response.done`, which never needs to construct this shape itself -- so the
            // frontend's shared `onReceivedResponseDone` handler needs no cascade-specific
            // branch, just a `status` check. The plain `error` event mirrors the Realtime API's
            // own `error` passthrough (upstream protocol errors reach the browser the same way).
            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = "server_error", ["message"] = message },
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "response.done",
                ["response"] = new JsonObject
                {
                    ["id"] = responseId,
                    ["status"] = "failed",
                    ["status_details"] = new JsonObject
                    {
                        ["type"] = "failed",
                        ["error"] = new JsonObject { ["type"] = "server_error", ["message"] = message },
                    },
                    ["output"] = new JsonArray(),
                },
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
        }

        async Task RunTurnAndSpeakAsync(CancellationToken turnCt)
        {
            var responseId = MiddleTierItemIds.NewId();
            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = new JsonObject { ["id"] = responseId },
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);

            string finalText;
            try
            {
                finalText = await RunChatToolLoopAsync(turnCt).ConfigureAwait(false);
            }
            catch (CascadeRateLimitExhaustedException)
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "response.done",
                    ["response"] = new JsonObject { ["id"] = responseId },
                }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
            {
                // Barge-in: `BargeIn` owns this turn's cleanup, not us.
                throw;
            }
            catch (Exception ex)
            {
                // #262: a non-429 chat-completion failure (e.g. a transient 500) used to
                // propagate straight out of this method, through `ProcessTurnAsync`/
                // `SendGreetingAsync`, into `Spawn`'s own `catch (Exception ex) { _logger?.
                // LogError(...) }` -- which swallows it with NO `response.done` ever reaching
                // the browser. The frontend had already flipped to "response in progress" on
                // `response.created` above, with nothing left to ever flip it back: the mic
                // stayed muted and the UI stuck, forever, on a turn that will never continue.
                // Send a terminal `response.done` (failed status) plus a best-effort `error`
                // event instead. The 429 path and barge-in (both just above) are unaffected --
                // this `catch` only ever reaches OTHER failures.
                _logger?.LogError(ex, "Cascade chat completion failed (session={SessionId})", sessionId);
                await SendFailedResponseDoneAsync(responseId, "chat completion failed", turnCt).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(finalText))
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "response.audio_transcript.delta",
                    ["delta"] = finalText,
                }.ToJsonString(), turnCt, ct).ConfigureAwait(false);

                // #126: feeds this session's own rehydration/resume history -- see
                // ProcessTurnAsync's matching guest-side RecordTurn call for why.
                _sessionManager?.RecordTurn(sessionId, "assistant", finalText);

                try
                {
                    await SpeakAsync(finalText, turnCt).ConfigureAwait(false);
                }
                catch (CascadeRateLimitExhaustedException)
                {
                    // Already notified via the final extension.rate_limited frame -- nothing more to do.
                }
                catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
                {
                    // Same parity note as the tool-call catch above: Python's bare `except
                    // Exception:` around `_speak` never catches `asyncio.CancelledError`, so a
                    // barge-in cancelling TTS mid-stream must propagate here too, not get logged
                    // as a TTS failure. The `when` guard (see `ExecuteToolCallAsync` for the full
                    // rationale) keeps a non-barge-in HttpClient timeout from being misclassified
                    // the same way -- it instead falls to `catch (Exception ex)` below, which logs
                    // it and still ends the turn with a failed `response.done`.
                    throw;
                }
                catch (Exception ex)
                {
                    // #262: same gap as the chat-completion catch above, for TTS -- previously
                    // this just logged and fell through to a NORMAL `response.done` below (no
                    // status, no error event), silently hiding a real TTS failure from the guest/
                    // frontend as if the turn had succeeded with no audio. Report it the same way
                    // chat-completion failures are now reported, instead of a quiet, misleading
                    // "success".
                    _logger?.LogWarning(ex, "Cascade TTS failed for this turn's final answer (session={SessionId})", sessionId);
                    await SendFailedResponseDoneAsync(responseId, "text-to-speech failed", turnCt).ConfigureAwait(false);
                    return;
                }
            }

            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "response.done",
                ["response"] = new JsonObject { ["id"] = responseId },
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);

            identifiers.AdvanceRoundTrip();
            await SendTextAsync(browserSocket, identifiers.ToFrame("extension.round_trip_token").ToJsonString(), turnCt, ct)
                .ConfigureAwait(false);
        }

        async Task ProcessTurnAsync(byte[] turnAudio, CancellationToken turnCt)
        {
            if (turnAudio.Length == 0)
            {
                return;
            }
            string transcript;
            try
            {
                transcript = await TranscribeAsync(turnAudio, turnCt).ConfigureAwait(false);
            }
            catch (CascadeRateLimitExhaustedException)
            {
                return;
            }
            catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
            {
                // Same parity note as the two catches above: Python's bare `except Exception:`
                // around `_transcribe` never catches `asyncio.CancelledError`, so a barge-in
                // cancelling STT mid-flight must propagate, not get logged as a transcription
                // failure. The `when` guard keeps a non-barge-in HttpClient timeout from being
                // misclassified the same way -- see `ExecuteToolCallAsync` for the full rationale.
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Cascade transcription failed (session={SessionId})", sessionId);
                return;
            }
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            await SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "conversation.item.input_audio_transcription.completed",
                ["transcript"] = transcript,
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
            state.Messages.Add(CascadeChatMessage.User(transcript));
            // #126: feeds this session's own rehydration/resume history -- see
            // SessionManager.BuildRehydrationText/RecentTurns. Never called before this issue (a
            // cascade session's recent-turns list was always empty), so a resumed cascade session
            // had nothing real to rehydrate with.
            _sessionManager?.RecordTurn(sessionId, "guest", transcript);
            await RunTurnAndSpeakAsync(turnCt).ConfigureAwait(false);
        }

        async Task SendGreetingAsync(CancellationToken turnCt)
        {
            if (promptLoader is null)
            {
                return;
            }
            string? text;
            try
            {
                // Same conversion RealtimeProcessor.BuildGreetingFrame already uses for this exact
                // dictionary -- ["item"]["content"][0]["text"], matching cascade_processor.py's own
                // `prompt_loader.get_greeting()["item"]["content"][0]["text"]`.
                var greeting = (JsonObject)YamlJson.ToJsonNode((IDictionary<object, object>)promptLoader.Greeting)!;
                text = GetString((greeting["item"]?["content"] as JsonArray)?[0] as JsonObject, "text");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not extract greeting text from prompt_loader.greeting (session={SessionId})", sessionId);
                return;
            }
            if (string.IsNullOrEmpty(text))
            {
                _logger?.LogWarning("prompt_loader.greeting had no usable item.content[0].text (session={SessionId})", sessionId);
                return;
            }

            state.Messages.Add(CascadeChatMessage.User(text));
            // #126: mark as soon as the greeting STARTS (not when it finishes speaking) --
            // mirrors rtmt.py's own send_greeting_once -- so a connection dropped mid-greeting
            // still resumes silently (rehydrated, no second greeting) rather than being treated
            // as "never greeted" and re-greeted on reconnect. Before this issue, cascade never
            // called this at all, so ResumeOutcome.ConversationStarted was always false for a
            // cascade session.
            _sessionManager?.MarkConversationStarted(sessionId);
            await RunTurnAndSpeakAsync(turnCt).ConfigureAwait(false);
        }

        async Task HandleClientMessageAsync(JsonObject data)
        {
            var type = GetString(data, "type");
            switch (type)
            {
                case "input_audio_buffer.append":
                    {
                        var audioB64 = GetString(data, "audio");
                        if (string.IsNullOrEmpty(audioB64))
                        {
                            return;
                        }
                        byte[] pcm;
                        try
                        {
                            pcm = Convert.FromBase64String(audioB64);
                        }
                        catch (FormatException)
                        {
                            return;
                        }
                        if (state.NudgeEligible && !state.NudgeArmed)
                        {
                            // #126 (mirrors rtmt.py's `nudge_awaiting_client_live` gate): arm the
                            // one-shot resume nudge the first time THIS socket's guest proves the
                            // conversation is live -- its own first streamed mic chunk -- never
                            // merely because the resume handshake itself succeeded. Latched so this
                            // only ever fires once per connection.
                            state.NudgeArmed = true;
                            ScheduleNudge();
                        }
                        var vadEvent = detector.Feed(pcm, NowSeconds());
                        if (vadEvent == "speech_started")
                        {
                            // #126: real guest activity -- reset (cancel, never reschedule) any
                            // pending resume nudge, same one-shot semantics as rtmt.py's own
                            // `cancel_nudge`. Runs BEFORE BargeIn: a pending nudge is cancelled
                            // under the registry lock, and one that already fired is a
                            // CurrentTurnTask that BargeIn then cancels like any other turn.
                            CancelNudge("guest started speaking");
                            BargeIn();
                        }
                        else if (vadEvent == "speech_stopped")
                        {
                            CancelNudge("guest turn started");
                            var turnAudio = detector.TakeBuffer();
                            detector.Reset();
                            var bargeInTail = state.BargeInTail;
                            var (cts, task) = Spawn(async turnCt =>
                            {
                                // Never overlap the turn a barge-in just cut off (see BargeIn).
                                if (bargeInTail is not null)
                                {
                                    await bargeInTail.WaitAsync(turnCt).ConfigureAwait(false);
                                }
                                await ProcessTurnAsync(turnAudio, turnCt).ConfigureAwait(false);
                            }, "turn");
                            state.CurrentTurnCts = cts;
                            state.CurrentTurnTask = task;
                        }
                        break;
                    }
                case "input_audio_buffer.clear":
                    detector.Reset();
                    break;
                case "extension.set_voice":
                    {
                        // #236 Rick re-review item 2 (MEDIUM, blocking): mirror RealtimeProcessor's
                        // HandleClientExtensionMessageAsync (~line 306) exactly -- go through the same
                        // allow-list check rather than accepting any non-empty string, and log (rather
                        // than silently drop) an unknown/invalid voice.
                        var candidate = GetString(data, "voice");
                        var newVoice = ClientServerFilter.SanitizeVoice(candidate, _allowedVoices);
                        if (newVoice is null)
                        {
                            _logger?.LogWarning(
                                "Dropped extension.set_voice with an unknown/invalid voice {Voice} (session={SessionId})",
                                candidate, sessionId);
                            break;
                        }
                        state.Voice = newVoice;
                        break;
                    }
                case "extension.resume":
                    // #126: this connection's own first frame was ALREADY consumed (and, if it
                    // looked like a resume attempt, already decided) by the pre-loop negotiation
                    // below -- mirrors cascade_processor.py's/RealtimeProcessor's own
                    // reject_late_resume: a SECOND extension.resume on an already-running
                    // connection can never legitimately win (this socket already has its own
                    // session identity), so it is rejected rather than silently ignored or
                    // treated as if it could still replace this connection's identity.
                    await NotifyClientAsync(new JsonObject
                    {
                        ["type"] = "extension.resume_rejected",
                        ["reason"] = "not_first_frame",
                    }, ct).ConfigureAwait(false);
                    break;
                default:
                    // session.update / anything unrecognized: explicit v1 scope cut, mirroring
                    // cascade_processor.py's own _handle_client_message.
                    break;

            }
        }

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

        // #126: cascade's own resume handshake, through the SAME SessionManager.TryResume/
        // grace-held order state/4002-supersede semantics the realtime pipeline already uses --
        // a resume is honoured ONLY as this socket's literal first client frame, mirroring
        // RealtimeProcessor's own HandleResumeFirstFrameAsync/reject_late_resume pair.
        //
        // #126 follow-up fix: this MUST NOT implement the timeout by cancelling the
        // WebSocketFrameReader.ReadMessageAsync call itself -- a minimal repro confirmed that
        // cancelling a .NET WebSocket's ReceiveAsync aborts the socket (State -> Aborted) as a
        // side effect, after which every subsequent SendAsync throws WebSocketException. That
        // silently broke EVERY cascade connection (not just resume attempts) once Program.cs
        // started always wiring a real SessionManager in -- the session_metadata frame below
        // would throw, the exception propagated unhandled out of RunSessionAsync, and the
        // registered session just sat there until SessionManager's own idle sweep eventually
        // closed it 45s later. Mirrors RealtimeProcessor's own first-frame race instead
        // (FirstFrameDecision/Task.Delay(...).ContinueWith(TrySetResult)): the read is started
        // once, against the SESSION's own cancellation token (never a separate timeout token),
        // and is simply raced against a timer via Task.WhenAny. If the timer wins, the read is
        // left running rather than cancelled -- its eventual result is consumed as this
        // connection's first real message by the main loop below (pendingFirstFrameTask)
        // instead of a second, illegal, concurrent ReadMessageAsync call on the same socket.
        // Entirely inert when no SessionManager was injected (see that field's own doc comment).
        async Task<JsonObject?> NegotiateResumeAsync()
        {
            WebSocketFrame? firstFrame = null;
            var timeoutSeconds = _sessionManager!.Config.FirstFrameTimeoutSeconds;
            if (timeoutSeconds > 0)
            {
                pendingFirstFrameTask = WebSocketFrameReader.ReadMessageAsync(browserSocket, ct);
                var winner = await Task.WhenAny(
                        pendingFirstFrameTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider, ct))
                    .ConfigureAwait(false);
                if (winner == pendingFirstFrameTask)
                {
                    firstFrame = await pendingFirstFrameTask.ConfigureAwait(false);
                    pendingFirstFrameTask = null;
                }
                // else: the timer won -- pendingFirstFrameTask is left set (still in-flight) for
                // the main loop's first iteration to await, exactly like "no resume attempt",
                // mirroring cascade_processor.py's own asyncio.wait_for TimeoutError catch.
            }

            JsonObject? resumeData = null;
            JsonObject? leftover = null;
            if (firstFrame is { MessageType: WebSocketMessageType.Text })
            {
                JsonObject? parsed;
                try
                {
                    parsed = JsonNode.Parse(firstFrame.Payload) as JsonObject;
                }
                catch (JsonException)
                {
                    parsed = null;
                }
                if (parsed is not null && GetString(parsed, "type") == "extension.resume")
                {
                    resumeData = parsed;
                }
                else
                {
                    // A real (non-resume) first frame consumed while peeking -- e.g.
                    // extension.set_voice sent with no stored resume id yet -- replayed into the
                    // main loop below so it is never silently dropped.
                    leftover = parsed;
                }
            }

            if (resumeData is not null)
            {
                var presentedId = GetString(resumeData, "resume_id");
                var outcome = _sessionManager.TryResume(
                    browserSocket, presentedId, persona.Id, resolvedModel.Id, menuMode, sessionId, linkedCts);
                if (outcome.Accepted)
                {
                    sessionId = outcome.SessionId!;
                    toolExecutor = outcome.ToolExecutor!;
                    state.Voice = outcome.Voice!;
                    // Adopt the ORIGINAL session's identifiers object (same reference, so its
                    // RoundTripIndex keeps counting up from where the prior connection left off)
                    // instead of leaving this connection's brand-new one bound.
                    if (outcome.Identifiers is { } originalIdentifiers)
                    {
                        identifiers = originalIdentifiers;
                    }
                    if (outcome.StaleWs is { } staleWs)
                    {
                        _ = Task.Run(
                            () => RealtimeProcessor.CloseSupersededStaleConnectionAsync(
                                staleWs, outcome.StaleCts, RealtimeProcessor.SupersededCloseTimeout, _logger),
                            CancellationToken.None);
                    }
                    var orderSummaryJson = toolExecutor is IOrderTicketSource ticketSource
                        ? SafeOrderSummaryJson(ticketSource)
                        : "{}";
                    await SendTextAsync(browserSocket, new JsonObject
                    {
                        ["type"] = "extension.session_resumed",
                        ["order_summary"] = JsonNode.Parse(orderSummaryJson) ?? new JsonObject(),
                        ["session_token"] = identifiers.SessionToken,
                        ["round_trip_index"] = identifiers.RoundTripIndex,
                        ["round_trip_token"] = identifiers.RoundTripToken,
                        ["resume_id"] = outcome.ResumeId,
                    }.ToJsonString(), ct, ct).ConfigureAwait(false);

                    if (outcome.ConversationStarted)
                    {
                        // #126/#247 parity: never replay an in-flight turn, never re-greet --
                        // brief the new connection with the order + recent transcript, then stay
                        // silent until the guest speaks.
                        state.Messages.Add(CascadeChatMessage.System(SessionManager.BuildRehydrationText(
                            orderSummaryJson, outcome.RecentTurns ?? Array.Empty<(string Role, string Text)>(),
                            state.RoleName)));
                        if (_sessionManager.Config.NudgeAfterSeconds > 0)
                        {
                            state.NudgeEligible = true;
                        }
                        _logger?.LogInformation(
                            "Cascade: resumed session {SessionId} rehydrated ({Count} recent turns); greeting suppressed",
                            sessionId, outcome.RecentTurns?.Count ?? 0);
                    }
                    else
                    {
                        var (greetingCts, greetingTask) = Spawn(SendGreetingAsync, "greeting");
                        state.CurrentTurnCts = greetingCts;
                        state.CurrentTurnTask = greetingTask;
                    }
                    return null; // the resume frame itself is fully consumed either way, never replayed
                }

                _logger?.LogInformation(
                    "Cascade: resume rejected (reason={Reason}); starting fresh session (session={SessionId})",
                    outcome.Reason, sessionId);
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "extension.resume_rejected",
                    ["reason"] = outcome.Reason,
                }.ToJsonString(), ct, ct).ConfigureAwait(false);
                // Falls through to the fresh path below -- the resume frame is fully consumed
                // either way, accepted or rejected.
            }

            // Fresh path (no resume attempted, or one was attempted and rejected): announce this
            // connection's own identity and start the greeting -- the exact pre-#126
            // unconditional behaviour.
            var resumeId = _sessionManager.IssueResumeId(sessionId);
            var metadataFrame = identifiers.ToFrame("extension.session_metadata");
            if (resumeId is not null)
            {
                metadataFrame["resumeId"] = resumeId;
            }
            await SendTextAsync(browserSocket, metadataFrame.ToJsonString(), ct, ct).ConfigureAwait(false);
            var (freshGreetingCts, freshGreetingTask) = Spawn(SendGreetingAsync, "greeting");
            state.CurrentTurnCts = freshGreetingCts;
            state.CurrentTurnTask = freshGreetingTask;

            return leftover;
        }

        // ── Session start ────────────────────────────────────────────────────────────────────────
        JsonObject? leftoverFirstFrame = null;
        if (_sessionManager is null)
        {
            await SendTextAsync(browserSocket, identifiers.ToFrame("extension.session_metadata").ToJsonString(), ct, ct)
                .ConfigureAwait(false);

            var (greetingCts, greetingTask) = Spawn(SendGreetingAsync, "greeting");
            state.CurrentTurnCts = greetingCts;
            state.CurrentTurnTask = greetingTask;
        }
        else
        {
            leftoverFirstFrame = await NegotiateResumeAsync().ConfigureAwait(false);
        }

        if (leftoverFirstFrame is not null)
        {
            try
            {
                await HandleClientMessageAsync(leftoverFirstFrame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling cascade client's replayed first message (session={SessionId})", sessionId);
            }
        }

        try
        {
            while (browserSocket.State == WebSocketState.Open)
            {
                // #126 fix: if NegotiateResumeAsync's first-frame timer won the race, its
                // ReadMessageAsync call is still in flight on this socket -- must be awaited
                // here rather than starting a second, concurrent ReadMessageAsync (illegal on
                // one WebSocket) or abandoning its eventual result.
                WebSocketFrame? frame;
                if (pendingFirstFrameTask is { } pending)
                {
                    pendingFirstFrameTask = null;
                    frame = await pending.ConfigureAwait(false);
                }
                else
                {
                    frame = await WebSocketFrameReader.ReadMessageAsync(browserSocket, ct).ConfigureAwait(false);
                }
                if (frame is null)
                {
                    break;
                }
                if (frame.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                JsonObject? data;
                try
                {
                    data = JsonNode.Parse(frame.Payload) as JsonObject;
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "Malformed JSON from cascade browser client, ignoring (session={SessionId})", sessionId);
                    continue;
                }
                if (data is null)
                {
                    continue;
                }

                try
                {
                    await HandleClientMessageAsync(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error handling cascade client message (session={SessionId})", sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/cancellation.
        }
        catch (WebSocketException ex)
        {
            _logger?.LogInformation(ex, "Cascade browser WebSocket ended abruptly (session={SessionId})", sessionId);
        }
        finally
        {
            // Teardown still waits for everything: the current turn (cancelled) and any
            // barge-in tail still waiting on an earlier cancelled turn.
            CancelNudge("connection closing");
            await CancelCurrentTurnAsync("connection closing").ConfigureAwait(false);
            await AwaitQuietlyAsync(state.BargeInTail).ConfigureAwait(false);
            _sessionManager?.Detach(browserSocket, sessionId, "socket closed");
        }
    }


    /// <summary>
    /// Sends one text frame to the browser socket. <paramref name="turnCt"/> is only ever used to
    /// decide whether to bother sending at all (via <see cref="CancellationToken.ThrowIfCancellationRequested"/>,
    /// checked BEFORE the write is issued) -- the write itself always uses <paramref name="socketCt"/>,
    /// the session-lifetime token, never <paramref name="turnCt"/>.
    ///
    /// #236 Rick re-review item 1 (HIGH, blocking): .NET's <c>ManagedWebSocket</c> (the
    /// implementation behind both Kestrel's server-side <see cref="WebSocket"/> and
    /// <see cref="WebSocket.CreateFromStream"/>) treats a cancelled in-flight <c>SendAsync</c> as a
    /// fatal, unrecoverable transport error: cancelling it mid-write aborts the ENTIRE socket, not
    /// just that one call. <c>BargeIn</c> cancels a turn's own
    /// <c>CurrentTurnCts</c>/<c>turnCt</c> on barge-in while the SESSION (and its socket) must keep
    /// running for the next turn -- so passing <c>turnCt</c> straight into
    /// <c>browserSocket.SendAsync</c> (as every call site here used to) meant a barge-in landing
    /// mid-write (e.g. while a long TTS reply streams <c>response.audio.delta</c> chunks) silently
    /// killed the guest's WebSocket: the socket flips to <c>Aborted</c>, the browser-read loop exits
    /// the connection, and this method's own `socket.State != Open` early-return makes every
    /// subsequent send a silent no-op -- nothing is logged, nothing throws, the guest just goes
    /// quiet. Rick reproduced this end-to-end with the real <c>Backend.dll</c> (barge-in during a
    /// large TTS reply under client backpressure) and matched it to the first round of this PR's CI
    /// failures (a greeting cancelled mid-send of <c>extension.round_trip_token</c> right after
    /// <c>response.done</c>). Passing <paramref name="socketCt"/> (only ever cancelled once, at
    /// final session teardown -- see <c>RunSessionAsync</c>'s own <c>linkedCts</c>) keeps every
    /// already-in-flight write safe from a turn-level cancellation, while
    /// <paramref name="turnCt"/>'s <c>ThrowIfCancellationRequested()</c> still stops a cancelled
    /// turn from issuing any FURTHER sends, preserving the original "stop talking once barged in
    /// on" behaviour.
    /// </summary>
    private static async Task SendTextAsync(WebSocket socket, string payload, CancellationToken turnCt, CancellationToken socketCt)
    {
        turnCt.ThrowIfCancellationRequested();
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, socketCt).ConfigureAwait(false);
    }

    /// <summary>Duplicated from <see cref="RealtimeProcessor"/>'s own private helper of the same
    /// name -- a defensive string read that never throws for a malformed/missing/non-string field,
    /// used for every piece of JSON this class reads off the wire or an upstream REST response.</summary>
    private static string? GetString(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Duplicated from <see cref="RealtimeProcessor"/>'s own private helper of the same
    /// name (that one stays private to its class) -- both pipelines resolve a connection's own
    /// tool schemas from its bound persona's <see cref="PromptLoader"/> the same way.</summary>
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
}
