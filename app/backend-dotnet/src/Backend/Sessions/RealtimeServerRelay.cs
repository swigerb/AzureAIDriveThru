using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Realtime;
using Backend.Shared;

namespace Backend.Sessions;

internal sealed class RealtimeServerRelay(RealtimeSessionContext context)
{
    public Task RunAsync() => RelayUpstreamToBrowserAsync();

    JsonObject BuildClientSessionEcho(JsonObject message)
    {
        var session = message["session"] as JsonObject;
        return new JsonObject
        {
            ["type"] = RealtimeProcessor.GetString(message, "type"),
            ["event_id"] = RealtimeProcessor.GetString(message, "event_id"),
            ["session"] = new JsonObject
            {
                ["id"] = session?["id"]?.DeepClone(),
                ["object"] = session?["object"]?.DeepClone(),
                ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = context.State.Voice } },
            },
        };
    }

    async Task<JsonObject?> HandleErrorAsync(JsonObject message)
    {
        var err = message["error"] as JsonObject;
        var rejectedEventId = context.State.Guard.Correlate(message);
        if (rejectedEventId is not null)
        {
            var original = context.State.Guard.OriginalOf(rejectedEventId);
            if (original is not null || !context.State.Guard.ClaimFallback(rejectedEventId))
            {
                context.Logger?.FallbackSessionUpdateAlsoRejected(
                    rejectedEventId, original, RealtimeProcessor.GetString(err, "code"), RealtimeProcessor.GetString(err, "param"), RealtimeProcessor.GetString(err, "message"), context.SessionId);
                return message;
            }
            context.Logger?.SessionUpdateRejected(
                rejectedEventId, RealtimeProcessor.GetString(err, "code"), RealtimeProcessor.GetString(err, "param"), RealtimeProcessor.GetString(err, "message"), context.SessionId);

            var rejectedPayload = context.State.Guard.PayloadOf(rejectedEventId);
            var param = RealtimeProcessor.GetString(err, "param") ?? "";
            if ((rejectedPayload.ContainsKey("reasoning") || rejectedPayload.ContainsKey("parallel_tool_calls"))
                && (param.Length == 0
                    || param.StartsWith("session.reasoning", StringComparison.Ordinal)
                    || param.StartsWith("session.parallel_tool_calls", StringComparison.Ordinal)))
            {
                context.SessionConfig.ReasoningRejected = true;
                context.Logger?.ReasoningOptionsRejected(context.SessionConfig.Deployment ?? "?");
            }

            var fallback = RealtimeSessionBuilder.BuildFallbackSessionUpdate(
                context.SessionConfig, context.ToolSchemas, voice: Overridable.Of<string?>(context.State.Voice),
                systemMessage: Overridable.Of<string?>(context.SystemMessage),
                reasoningOverride: context.ReasoningOverride);
            var fallbackPayload = context.State.Guard.Track(fallback.ToJsonString(), fallbackOf: rejectedEventId);
            await FramePump.SendTextAsync(context.Upstream, fallbackPayload, context.CancellationToken).ConfigureAwait(false);
            return null;
        }

        if (await context.State.RateLimit.OnErrorAsync(message, context.CancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (RealtimeProcessor.GetString(err, "code") == "response_cancel_not_active")
        {
            if (context.Logger is { } benignLogger && benignLogger.IsEnabled(LogLevel.Information))
            {
                benignLogger.RealtimeApiErrorBenign(message.ToJsonString());
            }
            return message;
        }

        if (context.Logger is { } errorLogger && errorLogger.IsEnabled(LogLevel.Error))
        {
            errorLogger.RealtimeApiError(message.ToJsonString());
        }
        return message;
    }

    private async Task<JsonObject?> HandleResponseDoneAsync(JsonObject message)
    {
        if (await context.State.RateLimit.OnResponseDoneAsync(message, context.CancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (context.State.ToolsPending.Count > 0)
        {
            context.State.ToolsPending.Clear();
            // Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2"):
            // port of rtmt.py's _ToolFailureTracker wiring -- tally this round's outcome
            // once, here, not per call, so several parallel failing tool calls in the same
            // response only count as a single failed round.
            context.State.ToolFailures.EndRound();
            if (context.State.ToolFailures.AtCap())
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
                if (context.State.ToolFailures.ConsumeCapNotice())
                {
                    context.Logger?.CappingAutoResponseCreate(context.State.ToolFailures.Count, context.SessionId);
                    await FramePump.SendTextAsync(context.Upstream, ToolFailureCapNotice.BuildMessage(context.PromptLoader), context.CancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    context.Logger?.SuppressingAutoResponseCreate(context.State.ToolFailures.Count, context.SessionId);
                }
            }
            else
            {
                await FramePump.SendTextAsync(context.Upstream, """{"type":"response.create"}""", context.CancellationToken).ConfigureAwait(false);
            }
        }

        var isToolCallResponse = false;
        var spokenParts = new List<string>();
        if (message["response"] is JsonObject response && response["output"] is JsonArray output)
        {
            var outputResult = FilterResponseOutput(response, output);
            isToolCallResponse = outputResult.IsToolCallResponse;
            spokenParts = outputResult.SpokenParts;
            if (isToolCallResponse && context.Logger is { } toolCallLogger
                && toolCallLogger.IsEnabled(LogLevel.Information))
            {
                toolCallLogger.ResponseContainedToolCalls(
                    outputResult.ToolNames.Count, string.Join(", ", outputResult.ToolNames), context.SessionId);
            }
            TrackResponseOutput(response);
        }

        if (!isToolCallResponse)
        {
            await SendRoundTripTokenAsync().ConfigureAwait(false);
        }
        context.SessionManager?.RecordTurn(
            context.State.EffectiveSessionId, "assistant", string.Join(" ", spokenParts));
        return message;
    }

    private sealed record FilteredResponseOutput(
        bool IsToolCallResponse,
        List<string> ToolNames,
        List<string> SpokenParts);

    private FilteredResponseOutput FilterResponseOutput(JsonObject response, JsonArray output)
    {
        var isToolCallResponse = false;
        var toolNames = new List<string>();
        var spokenParts = new List<string>();
        var filtered = new JsonArray();
        foreach (var node in output)
        {
            var type = node is JsonObject item ? RealtimeProcessor.GetString(item, "type") : null;
            if (type == "function_call" && node is JsonObject fnCall)
            {
                isToolCallResponse = true;
                toolNames.Add(RealtimeProcessor.GetString(fnCall, "name") ?? "?");
            }
            if (type is "function_call" or "function_call_output")
            {
                continue;
            }
            if (type == "message" && node is JsonObject messageItem
                && messageItem["content"] is JsonArray contentParts)
            {
                AddSpokenParts(contentParts, spokenParts);
            }
            filtered.Add(node?.DeepClone());
        }
        if (filtered.Count != output.Count)
        {
            response["output"] = filtered;
        }
        return new FilteredResponseOutput(isToolCallResponse, toolNames, spokenParts);
    }

    private void AddSpokenParts(JsonArray contentParts, List<string> spokenParts)
    {
        foreach (var part in contentParts)
        {
            if (part is not JsonObject partObj)
            {
                continue;
            }
            var text = (RealtimeProcessor.GetString(partObj, "transcript")
                ?? RealtimeProcessor.GetString(partObj, "text") ?? "").Trim();
            if (text.Length > 0)
            {
                spokenParts.Add(text);
            }
        }
    }

    private void TrackResponseOutput(JsonObject response)
    {
        var monitor = context.SessionManager?.GetContextMonitor(context.State.EffectiveSessionId);
        if (monitor is null || response["output"] is not JsonArray output)
        {
            return;
        }
        foreach (var item in output)
        {
            if (item is not JsonObject itemObject || itemObject["content"] is not JsonArray content)
            {
                continue;
            }
            foreach (var part in content)
            {
                if (part is JsonObject partObject)
                {
                    monitor.AddContent(RealtimeProcessor.GetString(partObject, "text"));
                    monitor.AddContent(RealtimeProcessor.GetString(partObject, "transcript"));
                }
            }
        }
    }

    private async Task SendRoundTripTokenAsync()
    {
        var identifiers = context.State.Identifiers.AdvanceRoundTrip();
        await FramePump.SendTextAsync(
            context.BrowserSocket, identifiers.ToFrame("extension.round_trip_token").ToJsonString(),
            context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> DispatchServerMessageAsync(JsonObject message, string msgType)
    {
        switch (msgType)
        {
            case "error":
                return await HandleErrorAsync(message).ConfigureAwait(false);
            case "conversation.item.input_audio_transcription.failed":
                context.Logger?.InputAudioTranscriptionFailed(context.SessionConfig.TranscriptionModel, message["error"]?.ToJsonString(), context.SessionId);
                return message;
            case "session.created":
                return await HandleSessionCreatedAsync(message).ConfigureAwait(false);
            case "session.updated":
                return HandleSessionUpdated(message);
            case "response.output_item.added":
                return HandleOutputItemAdded(message);
            case "conversation.item.created":
            case "conversation.item.added":
                return HandleConversationItem(message);
            case "conversation.item.done":
            case "conversation.item.retrieved":
                return HandleRetrievedItem(message);
            case "response.function_call_arguments.delta":
            case "response.function_call_arguments.done":
                return null;
            case "response.output_item.done":
                await HandleOutputItemDoneAsync(message).ConfigureAwait(false);
                return null;
            case "response.done":
                return await HandleResponseDoneAsync(message).ConfigureAwait(false);
            default:
                return message;
        }
    }

    private async Task<JsonObject> HandleSessionCreatedAsync(JsonObject message)
    {
        var echo = BuildClientSessionEcho(message);
        if (!context.State.SessionMetadataSent)
        {
            context.State.SessionMetadataSent = true;
            if (context.SessionManager is not null)
            {
                context.ResumeCoordinator.AnnounceAfterFirstFrameDecisionAsync()
                    .FireAndForget(context.Logger, nameof(ResumeCoordinator.AnnounceAfterFirstFrameDecisionAsync));
            }
            else
            {
                await FramePump.SendTextAsync(context.BrowserSocket,
                    context.State.Identifiers.ToFrame("extension.session_metadata").ToJsonString(),
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        return echo;
    }

    private JsonObject HandleSessionUpdated(JsonObject message)
    {
        context.State.Guard.OnSessionUpdated();
        if (!context.State.SessionConfigured.Task.IsCompleted)
        {
            context.Logger?.SessionUpdatedToolsConfigured(context.SessionId);
            context.State.SessionConfigured.TrySetResult(true);
        }
        return message["session"] is JsonObject ? BuildClientSessionEcho(message) : message;
    }

    private JsonObject? HandleOutputItemAdded(JsonObject message)
    {
        if (message["item"] is not JsonObject item
            || RealtimeProcessor.GetString(item, "type") != "function_call")
        {
            return message;
        }
        var callId = RealtimeProcessor.GetString(item, "call_id");
        if (!string.IsNullOrEmpty(callId) && !context.State.ToolsPending.ContainsKey(callId))
        {
            if (context.Logger is { } logger && logger.IsEnabled(LogLevel.Information))
            {
                logger.ToolCallReceived(RealtimeProcessor.GetString(item, "name"), callId, context.SessionId);
            }
            context.State.ToolsPending[callId] = "";
        }
        return null;
    }

    private JsonObject? HandleConversationItem(JsonObject message)
    {
        if (message["item"] is not JsonObject item)
        {
            return message;
        }
        if (RealtimeProcessor.GetString(item, "type") == "function_call")
        {
            var callId = RealtimeProcessor.GetString(item, "call_id") ?? "";
            context.State.ToolsPending[callId] = RealtimeProcessor.GetString(message, "previous_item_id") ?? "";
            return null;
        }
        return ClientServerFilter.DropFromClient(item) ? null : message;
    }

    private static JsonObject? HandleRetrievedItem(JsonObject message) =>
        message["item"] is JsonObject item && ClientServerFilter.DropFromClient(item) ? null : message;

    private async Task HandleOutputItemDoneAsync(JsonObject message)
    {
        if (message["item"] is JsonObject item && RealtimeProcessor.GetString(item, "type") == "function_call")
        {
            await context.ToolCallDispatcher.HandleToolCallDoneAsync(item).ConfigureAwait(false);
        }
    }

    private async Task RelayUpstreamToBrowserAsync()
    {
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var frame = await ReadUpstreamFrameAsync().ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }
            if (frame.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            await ProcessUpstreamFrameAsync(frame).ConfigureAwait(false);
        }
    }

    private async Task<WebSocketFrame?> ReadUpstreamFrameAsync()
    {
        try
        {
            return await WebSocketFrameReader.ReadMessageAsync(
                context.Upstream, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException ex)
        {
            context.Logger?.UpstreamWebSocketError(ex, context.SessionId);
            return null;
        }
    }

    private async Task ProcessUpstreamFrameAsync(WebSocketFrame frame)
    {
        JsonObject message;
        string msgType;
        try
        {
            message = RelayJson.ParseRelayFrame(frame.Payload);
            msgType = RealtimeProcessor.GetString(message, "type") ?? "";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            context.Logger?.DroppedMalformedServerFrame(context.SessionId);
            return;
        }
        try
        {
            LogResponseLifecycle(message, msgType, "upstream");
            ApplyEventSideEffects(message, msgType);
            var forward = await GetForwardMessageAsync(message, msgType).ConfigureAwait(false);
            if (forward is not null)
            {
                await FramePump.SendTextAsync(
                    context.BrowserSocket, forward.ToJsonString(), context.CancellationToken).ConfigureAwait(false);
                LogResponseLifecycle(forward, msgType, "browser");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger?.ErrorProcessingServerFrame(ex, context.SessionId);
        }
    }

    private void LogResponseLifecycle(JsonObject message, string eventType, string stage)
    {
        if (eventType is not ("response.created" or "response.done")
            || context.Logger is not { } logger
            || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var response = message["response"] as JsonObject;
        var outputTypes = new List<string>();
        var contentTypes = new List<string>();
        if (response?["output"] is JsonArray output)
        {
            foreach (var item in output.OfType<JsonObject>())
            {
                var outputType = RealtimeProcessor.GetString(item, "type");
                if (outputType is not null)
                {
                    outputTypes.Add(outputType);
                }
                if (item["content"] is JsonArray content)
                {
                    foreach (var part in content.OfType<JsonObject>())
                    {
                        var contentType = RealtimeProcessor.GetString(part, "type");
                        if (contentType is not null)
                        {
                            contentTypes.Add(contentType);
                        }
                    }
                }
            }
        }

        var error = response?["status_details"]?["error"] as JsonObject;
        logger.RealtimeResponseLifecycle(
            stage,
            eventType,
            RealtimeProcessor.GetString(response, "id") ?? "?",
            RealtimeProcessor.GetString(response, "status") ?? "unknown",
            string.Join(",", outputTypes.Distinct(StringComparer.Ordinal)),
            string.Join(",", contentTypes.Distinct(StringComparer.Ordinal)),
            RealtimeProcessor.GetString(error, "code") ?? "",
            context.SessionId);
    }

    private void ApplyEventSideEffects(JsonObject message, string msgType)
    {
        switch (msgType)
        {
            case "response.output_audio.delta":
            case "response.audio.delta":
                context.State.AssistantAudioSeen = true;
                context.State.Echo.OnAudioDelta();
                break;
            case "response.output_audio.done":
            case "response.audio.done":
                context.State.Echo.OnAudioDone(context.NowSeconds());
                break;
            case "input_audio_buffer.speech_started":
                HandleSpeechStarted();
                break;
            case "conversation.item.input_audio_transcription.completed":
                HandleTranscriptionCompleted(message);
                break;
            case "response.created":
                context.State.RateLimit.OnResponseCreated();
                break;
            case "response.done":
                context.State.Echo.OnResponseDone(context.NowSeconds());
                break;
        }
    }

    private void HandleSpeechStarted()
    {
        context.State.Echo.OnSpeechStarted();
        context.State.RateLimit.OnGuestSpeech();
        context.State.Nudge?.Cancel("guest speech_started");
        context.SessionManager?.TouchActivity(context.State.EffectiveSessionId);
        context.State.ToolFailures.ResetForNewTurn();
    }

    private void HandleTranscriptionCompleted(JsonObject message)
    {
        context.State.ToolFailures.ResetForNewTurn();
        context.State.Nudge?.Cancel("guest transcription completed");
        context.SessionManager?.TouchActivity(context.State.EffectiveSessionId);
        context.SessionManager?.RecordTurn(
            context.State.EffectiveSessionId, "guest", RealtimeProcessor.GetString(message, "transcript"));
    }

    private Task<JsonObject?> GetForwardMessageAsync(JsonObject message, string msgType)
    {
        if (PassthroughEvents.ServerTypes.Contains(msgType))
        {
            if (PassthroughEvents.GaToLegacy.TryGetValue(msgType, out var legacyType))
            {
                message["type"] = legacyType;
            }
            return Task.FromResult<JsonObject?>(message);
        }
        return DispatchServerMessageAsync(message, msgType);
    }

}
