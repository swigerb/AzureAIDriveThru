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
using Microsoft.Extensions.Logging;

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
/// Deliberate scope cuts from cascade_processor.py, mirroring <see cref="RealtimeProcessor"/>'s own
/// documented cuts (docs/dotnet_mapping.md): <c>session.update</c>/<c>extension.resume</c> are
/// explicit no-ops (v1 scope cut, same as Python), there is no context-window-monitor hook (no C#
/// equivalent exists yet), and session resume/rehydration/idle-sweep is out of scope (issue #15).
/// </summary>
public sealed class CascadeProcessor : IPipelineProcessor
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
        TimeProvider? timeProvider = null)
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
        };
        if (promptLoader is not null)
        {
            state.Messages.Add(CascadeChatMessage.System(promptLoader.SystemPrompt));
        }
        var detector = new TurnDetector(_vadConfig.Threshold, _vadConfig.SilenceDurationMs, AudioSampleRate);

        // ── Local helpers (closures over browserSocket/state/toolDefinitions/toolExecutor/...) ──
        // Mirrors RealtimeProcessor.RunSessionAsync's own nested-function style (and
        // cascade_processor.py's own nested-function style inside _run_session/_handle_client_message).

        Task NotifyClientAsync(JsonObject frame, CancellationToken notifyCt) =>
            SendTextAsync(browserSocket, frame.ToJsonString(), notifyCt, ct);

        async Task CancelCurrentTurnAsync(string reason)
        {
            var task = state.CurrentTurnTask;
            var cts = state.CurrentTurnCts;
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

        (CancellationTokenSource Cts, Task Task) Spawn(Func<CancellationToken, Task> body, string label)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(async () =>
            {
                try
                {
                    await body(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when this turn is barged in on -- silent, same as cascade_processor.py's
                    // own background-task wrapper swallowing asyncio.CancelledError.
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unhandled exception in cascade background task '{Label}' (session={SessionId})", label, sessionId);
                }
            });
            return (cts, task);
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
                // of the turn just quietly ending the way `CancelCurrentTurnAsync` expects.
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

                state.Messages.Add((JsonObject)message.DeepClone());
                var previousItemId = MiddleTierItemIds.NewId();
                foreach (var toolCallNode in toolCalls)
                {
                    if (toolCallNode is JsonObject toolCall)
                    {
                        await ExecuteToolCallAsync(toolCall, previousItemId, turnCt).ConfigureAwait(false);
                    }
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
            await CascadeRateLimit.WithRetryAsync(
                _rateLimitSettings,
                async () =>
                {
                    var pcm = await _audioClient.SpeakAsync(text, state.Voice, deployment, turnCt).ConfigureAwait(false);
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
            catch (CascadeRateLimitExhausted)
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "response.done",
                    ["response"] = new JsonObject { ["id"] = responseId },
                }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(finalText))
            {
                await SendTextAsync(browserSocket, new JsonObject
                {
                    ["type"] = "response.audio_transcript.delta",
                    ["delta"] = finalText,
                }.ToJsonString(), turnCt, ct).ConfigureAwait(false);

                try
                {
                    await SpeakAsync(finalText, turnCt).ConfigureAwait(false);
                }
                catch (CascadeRateLimitExhausted)
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
                    // it and lets the turn still finish with `response.done`.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Cascade TTS failed for this turn's final answer (session={SessionId})", sessionId);
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
            catch (CascadeRateLimitExhausted)
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
                    var vadEvent = detector.Feed(pcm);
                    if (vadEvent == "speech_started")
                    {
                        await CancelCurrentTurnAsync("guest started speaking (barge-in)").ConfigureAwait(false);
                        await SendTextAsync(browserSocket, """{"type":"input_audio_buffer.speech_started"}""", ct, ct)
                            .ConfigureAwait(false);
                    }
                    else if (vadEvent == "speech_stopped")
                    {
                        var turnAudio = detector.TakeBuffer();
                        detector.Reset();
                        var (cts, task) = Spawn(turnCt => ProcessTurnAsync(turnAudio, turnCt), "turn");
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
                default:
                    // session.update / extension.resume / anything unrecognized: explicit v1 scope
                    // cut, mirroring cascade_processor.py's own _handle_client_message.
                    break;
            }
        }

        // ── Session start ────────────────────────────────────────────────────────────────────────
        await SendTextAsync(browserSocket, identifiers.ToFrame("extension.session_metadata").ToJsonString(), ct, ct)
            .ConfigureAwait(false);

        var (greetingCts, greetingTask) = Spawn(SendGreetingAsync, "greeting");
        state.CurrentTurnCts = greetingCts;
        state.CurrentTurnTask = greetingTask;

        try
        {
            while (browserSocket.State == WebSocketState.Open)
            {
                var frame = await WebSocketFrameReader.ReadMessageAsync(browserSocket, ct).ConfigureAwait(false);
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
            await CancelCurrentTurnAsync("connection closing").ConfigureAwait(false);
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
    /// just that one call. <c>CancelCurrentTurnAsync</c> cancels a turn's own
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
