using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Realtime;

namespace Backend.Sessions;

/// <summary>Handles browser frames for one realtime session.</summary>
internal sealed class RealtimeClientRelay(RealtimeSessionContext context)
{
    private const string SessionEndedCloseReason = "session_ended";

    public async Task RunAsync()
    {
        bool isFirstFrame = context.SessionManager is not null;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            WebSocketFrame? frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }
            if (frame.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            bool checkingFirstFrame = isFirstFrame;
            isFirstFrame = false;
            if (!await ProcessFrameAsync(frame, checkingFirstFrame).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<WebSocketFrame?> ReadFrameAsync()
    {
        try
        {
            WebSocketFrame? frame = await WebSocketFrameReader.ReadMessageAsync(
                context.BrowserSocket, context.CancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                await FramePump.CloseIfOpenAsync(
                    context.BrowserSocket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
            }
            return frame;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException ex)
        {
            context.Logger.BrowserWebSocketError(ex, context.SessionId);
            return null;
        }
    }

    private async Task<bool> ProcessFrameAsync(WebSocketFrame frame, bool checkingFirstFrame)
    {
        if (!checkingFirstFrame)
        {
            FramePump.AppendFastPathResult fastPath = context.Processor.TryAppendFastPath(frame.Payload, context.State.Echo);
            if (fastPath.IsMatch)
            {
                await context.Processor.ForwardFastPathAudioAsync(
                    fastPath, frame.Payload, context.Upstream, context.SessionId, context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        JsonObject message;
        string msgType;
        try
        {
            message = RelayJson.ParseRelayFrame(frame.Payload);
            msgType = RealtimeProcessor.GetString(message, "type") ?? "";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            context.Logger.DroppedMalformedClientFrame(context.SessionId);
            if (checkingFirstFrame)
            {
                context.State.FirstFrameDecision.TrySetResult(false);
            }
            return true;
        }

        if (checkingFirstFrame && !await HandleFirstFrameAsync(message, msgType).ConfigureAwait(false))
        {
            return true;
        }
        return await HandleClientMessageAsync(message, msgType).ConfigureAwait(false);
    }

    private async Task<bool> HandleFirstFrameAsync(JsonObject message, string msgType)
    {
        if (msgType == "extension.resume")
        {
            if (context.State.FirstFrameDecision.Task.IsCompleted)
            {
                await context.ResumeCoordinator.RejectLateResumeAsync(
                    "first-frame timeout already decided").ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await context.ResumeCoordinator.HandleResumeFirstFrameAsync(message).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Logger.ErrorProcessingExtensionResume(ex, context.SessionId);
                    context.State.FirstFrameDecision.TrySetResult(false);
                }
            }
            return false;
        }
        context.State.FirstFrameDecision.TrySetResult(false);
        return true;
    }

    private async Task<bool> HandleClientMessageAsync(JsonObject message, string msgType)
    {
        try
        {
            if (msgType.Length == 0)
            {
                context.Logger.DroppedClientFrameMissingType(context.SessionId);
                return true;
            }
            if (msgType == "extension.end_session")
            {
                context.Logger.GuestEndedSession(context.SessionId);
                context.SessionManager?.EndSession(context.State.EffectiveSessionId, SessionEndedCloseReason);
                await FramePump.CloseIfOpenAsync(
                    context.BrowserSocket, WebSocketCloseStatus.NormalClosure, SessionEndedCloseReason)
                    .ConfigureAwait(false);
                return false;
            }
            if (msgType.StartsWith("extension.", StringComparison.Ordinal))
            {
                await HandleExtensionMessageAsync(msgType, message).ConfigureAwait(false);
                return true;
            }
            if (msgType == "input_audio_buffer.append"
                && context.State.Echo.ShouldSuppressAudio(context.NowSeconds()))
            {
                return true;
            }
            if (msgType != "input_audio_buffer.append")
            {
                context.SessionManager?.TouchActivity(context.State.EffectiveSessionId);
            }
            await ForwardClientMessageAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.ErrorProcessingClientFrame(ex, context.SessionId);
            return true;
        }
    }

    private async Task ForwardClientMessageAsync(JsonObject message)
    {
        bool hooksEnabled = BackendEnvironment.Get("CONFORMANCE_TEST_HOOKS") == "1";
        (JsonObject? forwarded, string? sentType) = ProcessClientMessage(message, hooksEnabled);
        if (forwarded is null)
        {
            return;
        }
        await FramePump.ForwardClientFrameAsync(
            forwarded, sentType, context.State.Echo, context.State.RateLimit,
            context.Upstream, context.CancellationToken).ConfigureAwait(false);
        if (sentType == "response.create")
        {
            context.State.Nudge?.Cancel("browser response.create");
        }
        if (!context.State.GreetingSent && sentType == "session.update")
        {
            await context.GreetingGate.SendOnceAsync("client-session.update").ConfigureAwait(false);
        }
        if (context.State.NudgeArmEligible && sentType == "session.update")
        {
            context.State.NudgeArmEligible = false;
            context.State.Nudge?.Arm();
        }
    }

    private (JsonObject? Forwarded, string? SentType) ProcessClientMessage(JsonObject message, bool hooksEnabled)
    {
        JsonObject? filtered = ClientServerFilter.Filter(message, hooksEnabled);
        if (filtered is null)
        {
            return (null, null);
        }
        string msgType = RealtimeProcessor.GetString(filtered, "type") ?? "";
        if (msgType != "session.update")
        {
            return (filtered, msgType);
        }
        if (filtered["session"] is not JsonObject clientSession)
        {
            return (null, null);
        }
        var sessionIn = new JsonObject();
        foreach (string key in ClientServerFilter.ClientSessionKeys)
        {
            if (clientSession.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                sessionIn[key] = value.DeepClone();
            }
        }
        if (clientSession.TryGetPropertyValue("turn_detection", out JsonNode? turnDetectionNode))
        {
            JsonObject? sanitized = ClientServerFilter.SanitizeTurnDetection(turnDetectionNode);
            sessionIn["turn_detection"] = sanitized is not null
                ? sanitized
                : (JsonObject)RealtimeSessionBuilder.BootstrapClientSession()["turn_detection"]!.DeepClone();
        }
        JsonObject session = RealtimeSessionBuilder.BuildSession(
            context.SessionConfig, sessionIn, context.ToolSchemas,
            voiceLocked: context.State.AssistantAudioSeen,
            voice: Overridable.Of<string?>(context.State.Voice),
            systemMessage: Overridable.Of<string?>(context.SystemMessage),
            reasoningOverride: context.ReasoningOverride);
        filtered["session"] = session;
        context.State.Guard.Stamp(filtered);
        TrackSessionUpdate(session);
        return (filtered, msgType);
    }

    private void TrackSessionUpdate(JsonObject session)
    {
        ContextMonitor? monitor = context.SessionManager?.GetContextMonitor(context.State.EffectiveSessionId);
        if (monitor is null)
        {
            return;
        }
        monitor.AddContent(RealtimeProcessor.GetString(session, "instructions"));
        if (session["tools"] is JsonArray injectedTools)
        {
            foreach (JsonNode? toolSchema in injectedTools)
            {
                monitor.AddContent(toolSchema?.ToJsonString());
            }
        }
    }

    private async Task HandleExtensionMessageAsync(string msgType, JsonObject message)
    {
        context.SessionManager?.TouchActivity(context.State.EffectiveSessionId);
        if (msgType == "extension.demo_rewards")
        {
            await FramePump.SendTextAsync(context.BrowserSocket,
                RewardsDemoFrames.Handle(context.State.ToolExecutor, message).ToJsonString(), context.CancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (msgType == "extension.resume")
        {
            if (context.SessionManager is not null)
            {
                await context.ResumeCoordinator.RejectLateResumeAsync(
                    "not this connection's first frame").ConfigureAwait(false);
            }
            return;
        }
        if (await context.Processor.TryHandleSessionOverrideExtensionMessageAsync(
                msgType, message, context.State.EffectiveSessionId, context.BrowserSocket, context.CancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }
        if (msgType != "extension.set_voice")
        {
            return;
        }
        string? candidate = RealtimeProcessor.GetString(message, "voice");
        string? newVoice = ClientServerFilter.SanitizeVoice(candidate, context.AllowedVoices);
        if (newVoice is null)
        {
            context.Logger.DroppedSetVoice(candidate, context.SessionId);
            return;
        }
        context.State.Voice = newVoice;
        context.SessionManager?.SetVoice(context.State.EffectiveSessionId, newVoice);
        if (context.State.AssistantAudioSeen)
        {
            context.Logger.VoiceDeferredToNextConversation(newVoice, context.SessionId);
            return;
        }
        string voiceUpdate = context.State.Guard.Track(RealtimeSessionContext.BuildVoiceUpdateFrame(newVoice).ToJsonString());
        await FramePump.SendTextAsync(context.Upstream, voiceUpdate, context.CancellationToken).ConfigureAwait(false);
    }
}
