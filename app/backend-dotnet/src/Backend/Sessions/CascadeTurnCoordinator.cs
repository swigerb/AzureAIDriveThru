using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Prompts;
using Backend.Realtime;

namespace Backend.Sessions;

/// <summary>
/// Owns turn-task, barge-in, and one-shot nudge lifetimes for one cascade connection.
/// </summary>
internal sealed class CascadeTurnCoordinator(CascadeSessionContext context)
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Turn-cancel boundary: whatever the cancelled turn faulted with is logged; teardown and barge-in must proceed regardless.")]
    public async Task CancelCurrentTurnAsync(string reason)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (context.TurnRegistryLock)
        {
            task = context.State.CurrentTurnTask;
            cts = context.State.CurrentTurnCts;
        }
        if (task is null)
        {
            return;
        }
        if (task.IsCompleted)
        {
            context.State.CurrentTurnTask = null;
            context.State.CurrentTurnCts = null;
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
        }
        catch (Exception ex)
        {
            context.Logger.CascadeTurnCancelException(ex, context.SessionId);
        }
        context.Logger.CascadeTurnCancelled(reason, context.SessionId);
        context.State.CurrentTurnTask = null;
        context.State.CurrentTurnCts = null;
        cts?.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget barge-in tail: a failed speech_started send is logged and must never fault the background task.")]
    public void BargeIn()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (context.TurnRegistryLock)
        {
            task = context.State.CurrentTurnTask;
            cts = context.State.CurrentTurnCts;
            context.State.CurrentTurnTask = null;
            context.State.CurrentTurnCts = null;
        }
        bool wasInFlight = task is { IsCompleted: false };
        cts?.Cancel();
        Task? previousTail = context.State.BargeInTail;
        context.State.BargeInTail = Task.Run(async () =>
        {
            await AwaitQuietlyAsync(previousTail).ConfigureAwait(false);
            await AwaitQuietlyAsync(task).ConfigureAwait(false);
            cts?.Dispose();
            if (wasInFlight)
            {
                context.Logger.CascadeTurnCancelled("guest started speaking (barge-in)", context.SessionId);
            }
            try
            {
                await CascadeProcessor.SendTextAsync(
                    context.BrowserSocket, """{"type":"input_audio_buffer.speech_started"}""",
                    context.CancellationToken, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
            catch (Exception ex)
            {
                context.Logger.SpeechStartedSendFailed(ex, context.SessionId);
            }
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Awaits an already-cancelled turn purely to drain it; any fault it ends with is logged, never rethrown.")]
    public async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            context.Logger.CascadeTurnCancelException(ex, context.SessionId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget turn boundary: a faulted background turn is logged and must never surface as an unobserved task exception.")]
    public (CancellationTokenSource Cts, Task Task) Spawn(
        Func<CancellationToken, Task> body,
        string label)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var task = Task.Run(async () =>
        {
            try
            {
                await body(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                context.Logger.CascadeBackgroundTaskFailed(ex, label, context.SessionId);
            }
        });
        return (cts, task);
    }

    public void ScheduleNudge()
    {
        (CancellationTokenSource? cts, Task? task) = Spawn(async nudgeCt =>
        {
            await Task.Delay(
                    TimeSpan.FromSeconds(context.SessionManager!.Config.NudgeAfterSeconds),
                    context.TimeProvider,
                    nudgeCt)
                .ConfigureAwait(false);
            bool skipped;
            lock (context.TurnRegistryLock)
            {
                if (nudgeCt.IsCancellationRequested)
                {
                    return;
                }
                Task? inFlight = context.State.CurrentTurnTask;
                skipped = (inFlight is not null && !inFlight.IsCompleted)
                    || context.State.BargeInTail is { IsCompleted: false };
                if (!skipped)
                {
                    context.State.CurrentTurnTask = context.State.NudgeTask;
                    context.State.CurrentTurnCts = context.State.NudgeCts;
                    context.State.NudgeTask = null;
                    context.State.NudgeCts = null;
                }
            }
            if (skipped)
            {
                context.Logger.CascadeNudgeSkippedTurnInFlight(context.SessionId);
                return;
            }
            context.Logger.CascadeNudgeFiring(
                context.SessionManager!.Config.NudgeAfterSeconds, context.SessionId);
            context.State.Messages.Add(CascadeChatMessage.User(
                SessionManager.BuildNudgeText(context.State.RoleName)));
            await RunTurnAndSpeakAsync(nudgeCt).ConfigureAwait(false);
        }, "nudge");
        context.State.NudgeCts = cts;
        context.State.NudgeTask = task;
    }

    public void CancelNudge(string reason)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (context.TurnRegistryLock)
        {
            task = context.State.NudgeTask;
            cts = context.State.NudgeCts;
            context.State.NudgeTask = null;
            context.State.NudgeCts = null;
            if (task is not null && !task.IsCompleted)
            {
                cts?.Cancel();
                context.Logger.CascadeNudgeCancelled(reason, context.SessionId);
            }
        }
        cts?.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Turn boundary: any chat-completion failure is logged and closed out with a failed response.done so the browser never waits on a dead turn.")]
    public async Task RunTurnAndSpeakAsync(CancellationToken turnCt, string? greetingInstructions = null)
    {
        string responseId = MiddleTierItemIds.NewId();
        if (context.Diagnostic is { } diagnostic)
        {
            diagnostic.ResponseId = responseId;
        }
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = new JsonObject { ["id"] = responseId },
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
        context.Diagnostic?.Emission("response.created");

        string finalText;
        try
        {
            finalText = greetingInstructions is null
                ? await context.ToolCallDispatcher.RunChatToolLoopAsync(turnCt).ConfigureAwait(false)
                : await context.ToolCallDispatcher.RunGreetingAsync(greetingInstructions, turnCt).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(finalText))
            {
                throw new InvalidOperationException("Chat tool loop returned no spoken answer; the ticket may have changed.");
            }
        }
        catch (CascadeRateLimitExhaustedException)
        {
            await SendResponseDoneAsync(responseId, finalText: null, turnCt).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            context.Diagnostic?.Emission("cancelled");
            throw;
        }
        catch (FoundryHttpException ex) when (ex.IsContentFilter)
        {
            // Same terminal shape the Realtime API sends for a filtered turn, so the frontend asks
            // the guest to rephrase instead of going silent. No automatic retry.
            context.Logger.CascadeChatContentFiltered(context.SessionId);
            await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
            {
                ["type"] = "response.done",
                ["response"] = new JsonObject
                {
                    ["id"] = responseId,
                    ["status"] = "incomplete",
                    ["status_details"] = new JsonObject { ["type"] = "incomplete", ["reason"] = "content_filter" },
                    ["output"] = new JsonArray(),
                },
            }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            context.Logger.CascadeChatCompletionFailed(ex, context.SessionId);
            await context.AudioTurnPipeline.SendFailedResponseDoneAsync(
                responseId, "chat completion failed", turnCt).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(finalText)
            && !await SendFinalTextAsync(responseId, finalText, turnCt).ConfigureAwait(false))
        {
            return;
        }
        await SendResponseDoneAsync(responseId, finalText, turnCt).ConfigureAwait(false);
        context.Identifiers.AdvanceRoundTrip();
        await CascadeProcessor.SendTextAsync(
            context.BrowserSocket,
            context.Identifiers.ToFrame("extension.round_trip_token").ToJsonString(),
            turnCt,
            context.CancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Turn boundary: any text-to-speech failure is logged and closed out with a failed response.done so the browser never waits on a dead turn.")]
    private async Task<bool> SendFinalTextAsync(string responseId, string finalText, CancellationToken turnCt)
    {
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.audio_transcript.delta",
            ["response_id"] = responseId,
            ["delta"] = finalText,
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
        context.Diagnostic?.Emission("transcript.sent");
        context.SessionManager?.RecordTurn(context.SessionId, "assistant", finalText);
        try
        {
            context.Diagnostic?.Emission("tts.started");
            await context.AudioTurnPipeline.SpeakAsync(finalText, responseId, turnCt).ConfigureAwait(false);
            context.Diagnostic?.Emission("tts.completed");
            return true;
        }
        catch (CascadeRateLimitExhaustedException)
        {
            return true;
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            context.Diagnostic?.Emission("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            context.Logger.CascadeTtsFailed(ex, context.SessionId);
            await context.AudioTurnPipeline.SendFailedResponseDoneAsync(
                responseId, "text-to-speech failed", turnCt).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>The Realtime API's <c>response.done</c> shape: the frontend renders the assistant's
    /// line from <c>output[].content[].transcript</c>, exactly as it does for realtime turns.</summary>
    private async Task SendResponseDoneAsync(string responseId, string? finalText, CancellationToken turnCt)
    {
        JsonArray output = string.IsNullOrEmpty(finalText)
            ? []
            : [new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["transcript"] = finalText }),
            }];
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.done",
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["status"] = "completed",
                ["output"] = output,
            },
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
        context.Diagnostic?.Emission("response.done");
    }

    private void ReleaseUntranscribedAudio(byte[] turnAudio)
    {
        lock (context.TurnRegistryLock)
        {
            if (ReferenceEquals(context.State.UntranscribedAudio, turnAudio))
            {
                context.State.UntranscribedAudio = [];
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Turn boundary: any transcription failure is logged and releases the carried audio instead of faulting the turn.")]
    public async Task ProcessTurnAsync(byte[] turnAudio, CancellationToken turnCt)
    {
        if (turnAudio.Length == 0)
        {
            return;
        }
        string transcript;
        try
        {
            transcript = await context.AudioTurnPipeline.TranscribeAsync(turnAudio, turnCt).ConfigureAwait(false);
        }
        catch (CascadeRateLimitExhaustedException)
        {
            ReleaseUntranscribedAudio(turnAudio);
            return;
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReleaseUntranscribedAudio(turnAudio);
            context.Logger.CascadeTranscriptionFailed(ex, context.SessionId);
            return;
        }
        if (string.IsNullOrWhiteSpace(transcript))
        {
            ReleaseUntranscribedAudio(turnAudio);
            return;
        }
        // Commit point: decided under the same lock speech_stopped uses to merge carried audio.
        // Barge-in cancels this turn before the next speech_stopped can merge, so either the
        // turn commits here (carry cleared, merge sees only new audio) or it was already
        // cancelled (audio stays carried, no transcript emitted). Never both.
        lock (context.TurnRegistryLock)
        {
            turnCt.ThrowIfCancellationRequested();
            if (ReferenceEquals(context.State.UntranscribedAudio, turnAudio))
            {
                context.State.UntranscribedAudio = [];
            }
        }
        context.State.Messages.Add(CascadeChatMessage.User(transcript));
        context.SessionManager?.TouchActivity(context.SessionId);
        context.SessionManager?.RecordTurn(context.SessionId, "guest", transcript);
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["transcript"] = transcript,
        }.ToJsonString(), CancellationToken.None, context.CancellationToken).ConfigureAwait(false);
        await RunTurnAndSpeakAsync(turnCt).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Malformed persona greeting config can fail as a cast, null, or JSON type error; any failure is logged and skips the greeting.")]
    public async Task SendGreetingAsync(CancellationToken turnCt)
    {
        if (context.PromptLoader is null)
        {
            return;
        }
        string? text;
        bool scoped;
        try
        {
            var greeting = (JsonObject)YamlJson.ToJsonNode(
                (IDictionary<object, object>)context.PromptLoader.Greeting)!;
            scoped = greeting["type"]?.GetValue<string>() == "response.create";
            text = scoped ? greeting["response"]!["instructions"]!.GetValue<string>()
                : CascadeProcessor.GetString(
                    (greeting["item"]?["content"] as JsonArray)?[0] as JsonObject, "text");
        }
        catch (Exception ex)
        {
            context.Logger.CascadeGreetingExtractFailed(ex, context.SessionId);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            context.Logger.CascadeGreetingTextMissing(context.SessionId);
            return;
        }
        if (!scoped)
        {
            context.State.Messages.Add(CascadeChatMessage.User(text));
        }

        context.SessionManager?.MarkConversationStarted(context.SessionId);
        await RunTurnAndSpeakAsync(turnCt, scoped ? text : null).ConfigureAwait(false);
    }
}
