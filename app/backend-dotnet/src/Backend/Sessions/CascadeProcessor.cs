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
using Backend.Shared;
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
    internal const int MaxToolRounds = 8;
    internal const int AudioSampleRate = 24000;
    internal const int TtsChunkBytes = 24000;

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
    private readonly ILogger<CascadeProcessor> _logger;
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
        CascadeProcessorOptions options,
        CascadeProcessorDependencies dependencies)
    {
        _catalog = catalog;
        var credential = dependencies.BearerTokenProvider ?? DefaultAzureCredentialTokenProvider.Instance.Value;
        _chatClient = new FoundryChatClient(dependencies.HttpClient, options.FoundryEndpoint, credential);
        _audioClient = new FoundryAudioClient(dependencies.HttpClient, options.AudioEndpoint, credential);
        _rateLimitSettings = options.RateLimitSettings;
        _vadConfig = options.VadConfig;
        _promptLoaders = dependencies.PromptLoaders;
        _toolExecutor = dependencies.ToolExecutor;
        _toolExecutorFactory = dependencies.ToolExecutorFactory;
        _allowedVoices = options.AllowedVoices;
        _defaultVoice = options.DefaultVoice;
        _logger = dependencies.Logger;
        // Issue #13 Wave 2 convention (RealtimeProcessor's own _timeProvider): the rate-limit
        // ladder's wall-clock delays (CascadeRateLimit.WithRetryAsync) are driven from this one
        // clock, so a test can swap in a FakeTimeProvider instead of waiting on the real 0.5-8s
        // delays. Defaults to TimeProvider.System in production.
        _timeProvider = dependencies.TimeProvider ?? TimeProvider.System;
        _echoCooldownSeconds = EchoTailSeconds(options.EchoCooldownSeconds);
        _sessionManager = dependencies.SessionManager;
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
    internal sealed class CascadeSessionState
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
        // Guest audio handed to a turn whose transcription hasn't been committed yet. A natural
        // pause longer than silence_duration_ms ends the turn early, and the guest resuming then
        // cancels it (barge-in) mid-STT; this audio is carried into the next turn instead of being
        // dropped. Guarded by TurnRegistryLock.
        public byte[] UntranscribedAudio { get; set; } = [];

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
        string? menuMode = null,
        CascadeDiagnosticCapture? diagnostic = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = CreateSessionContext(browserSocket, persona, resolvedModel, sessionId, linkedCts, menuMode);
        context.Diagnostic = diagnostic;
        var handler = new CascadeClientMessageHandler(context);
        await StartSessionAsync(context, handler).ConfigureAwait(false);
        await ReceiveSessionAsync(context, handler).ConfigureAwait(false);
    }

    internal CascadeSessionContext CreateSessionContext(
        WebSocket browserSocket, Persona persona, ResolvedModel resolvedModel, string sessionId,
        CancellationTokenSource linkedCts, string? menuMode = null)
    {
        _promptLoaders.TryGetValue(persona.Id, out var promptLoader);
        var toolSchemas = BuildToolSchemas(promptLoader);
        var toolDefinitions = CascadeToolDefinitions.FromToolSchemas(toolSchemas);
        var toolExecutor = _toolExecutorFactory?.Invoke(persona, promptLoader, menuMode) ?? _toolExecutor;
        var voice = ClientServerFilter.SanitizeVoice(persona.Voice.Default, _allowedVoices) ?? _defaultVoice;
        var identifiers = new SessionIdentifiers(persona.Id, resolvedModel.Id, sessionId, pipeline: "cascade");

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

        // #126: register this connection with the session registry up front, mirroring
        // RealtimeProcessor.RunSessionAsync's own create_session(...) call before its relay loop
        // starts -- entirely inert when no SessionManager was injected (see that field's own doc
        // comment), so every pre-#126 caller/test keeps today's exact behaviour.
        _sessionManager?.CreateSession(
            sessionId, browserSocket, persona.Id, resolvedModel.Id, menuMode, toolExecutor, voice,
            attachedCts: linkedCts, identifiers: identifiers);

        var context = new CascadeSessionContext
        {
            BrowserSocket = browserSocket,
            Persona = persona,
            ResolvedModel = resolvedModel,
            MenuMode = menuMode,
            LinkedCts = linkedCts,
            CancellationToken = ct,
            SessionManager = _sessionManager,
            State = state,
            PromptLoader = promptLoader,
            Detector = detector,
            AllowedVoices = _allowedVoices,
            Logger = _logger!,
            TimeProvider = _timeProvider,
            TurnRegistryLock = new object(),
            SessionId = sessionId,
            ToolExecutor = toolExecutor,
            Identifiers = identifiers,
        };
        context.ToolCallDispatcher = new CascadeToolCallDispatcher(
            context, _rateLimitSettings, _chatClient, toolDefinitions);
        context.AudioTurnPipeline = new CascadeAudioTurnPipeline(
            browserSocket, persona, state, detector, _catalog, _audioClient, _rateLimitSettings,
            _echoCooldownSeconds, () => context.SessionId, _timeProvider,
            context.NotifyClientAsync, context.NowSeconds, _logger!, ct);
        context.TurnCoordinator = new CascadeTurnCoordinator(context);
        return context;
    }

    private async Task StartSessionAsync(CascadeSessionContext context, CascadeClientMessageHandler handler)
    {
        JsonObject? leftoverFirstFrame = null;
        if (_sessionManager is null)
        {
            await context.NotifyClientAsync(
                context.Identifiers.ToFrame("extension.session_metadata"), context.CancellationToken)
                .ConfigureAwait(false);

            var (greetingCts, greetingTask) = context.TurnCoordinator.Spawn(context.SendGreetingAsync, "greeting");
            context.State.CurrentTurnCts = greetingCts;
            context.State.CurrentTurnTask = greetingTask;
        }
        else
        {
            leftoverFirstFrame = await new CascadeResumeCoordinator(context).NegotiateAsync().ConfigureAwait(false);
        }

        if (leftoverFirstFrame is not null)
        {
            try
            {
                await handler.HandleAsync(leftoverFirstFrame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.CascadeReplayedFirstMessageFailed(ex, context.SessionId);
            }
        }
    }

    private async Task ReceiveSessionAsync(CascadeSessionContext context, CascadeClientMessageHandler handler)
    {
        try
        {
            while (context.BrowserSocket.State == WebSocketState.Open)
            {
                // #126 fix: if resume negotiation's first-frame timer won the race, its
                // ReadMessageAsync call is still in flight on this socket -- must be awaited
                // here rather than starting a second, concurrent ReadMessageAsync (illegal on
                // one WebSocket) or abandoning its eventual result.
                WebSocketFrame? frame;
                if (context.PendingFirstFrameTask is { } pending)
                {
                    context.PendingFirstFrameTask = null;
                    frame = await pending.ConfigureAwait(false);
                }
                else
                {
                    frame = await WebSocketFrameReader.ReadMessageAsync(
                        context.BrowserSocket, context.CancellationToken).ConfigureAwait(false);
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
                    _logger?.CascadeMalformedJson(ex, context.SessionId);
                    continue;
                }
                if (data is null)
                {
                    continue;
                }

                try
                {
                    await handler.HandleAsync(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.CascadeClientMessageHandlingFailed(ex, context.SessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/cancellation.
        }
        catch (WebSocketException ex)
        {
            _logger?.CascadeWebSocketEndedAbruptly(ex, context.SessionId);
        }
        finally
        {
            // Teardown still waits for everything: the current turn (cancelled) and any
            // barge-in tail still waiting on an earlier cancelled turn.
            context.TurnCoordinator.CancelNudge("connection closing");
            await context.TurnCoordinator.CancelCurrentTurnAsync("connection closing").ConfigureAwait(false);
            await context.TurnCoordinator.AwaitQuietlyAsync(context.State.BargeInTail).ConfigureAwait(false);
            _sessionManager?.Detach(context.BrowserSocket, context.SessionId, "socket closed");
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
    /// <c>WebSocket.CreateFromStream(...)</c>) treats a cancelled in-flight <c>SendAsync</c> as a
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
    internal static async Task SendTextAsync(WebSocket socket, string payload, CancellationToken turnCt, CancellationToken socketCt)
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
    internal static string? GetString(JsonObject? obj, string key) =>
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
