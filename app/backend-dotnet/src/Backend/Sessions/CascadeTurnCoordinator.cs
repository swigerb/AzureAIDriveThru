using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Shared;

namespace Backend.Sessions;

/// <summary>
/// Owns turn-task, barge-in, and one-shot nudge lifetimes for one cascade connection.
/// </summary>
internal sealed class CascadeTurnCoordinator(CascadeSessionContext context)
{
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
        var wasInFlight = task is { IsCompleted: false };
        cts?.Cancel();
        var previousTail = context.State.BargeInTail;
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
        var (cts, task) = Spawn(async nudgeCt =>
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
                var inFlight = context.State.CurrentTurnTask;
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

    public async Task RunTurnAndSpeakAsync(CancellationToken turnCt)
    {
        var responseId = MiddleTierItemIds.NewId();
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = new JsonObject { ["id"] = responseId },
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);

        string finalText;
        try
        {
            finalText = await context.ToolCallDispatcher.RunChatToolLoopAsync(turnCt).ConfigureAwait(false);
        }
        catch (CascadeRateLimitExhaustedException)
        {
            await SendResponseDoneAsync(responseId, finalText: null, turnCt).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
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

    private async Task<bool> SendFinalTextAsync(string responseId, string finalText, CancellationToken turnCt)
    {
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.audio_transcript.delta",
            ["response_id"] = responseId,
            ["delta"] = finalText,
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
        context.SessionManager?.RecordTurn(context.SessionId, "assistant", finalText);
        try
        {
            await context.AudioTurnPipeline.SpeakAsync(finalText, responseId, turnCt).ConfigureAwait(false);
            return true;
        }
        catch (CascadeRateLimitExhaustedException)
        {
            return true;
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
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
    private Task SendResponseDoneAsync(string responseId, string? finalText, CancellationToken turnCt) =>
        CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "response.done",
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["status"] = "completed",
                ["output"] = string.IsNullOrEmpty(finalText)
                    ? new JsonArray()
                    : new JsonArray(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["transcript"] = finalText }),
                    }),
            },
        }.ToJsonString(), turnCt, context.CancellationToken);

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
        await CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "conversation.item.input_audio_transcription.completed",
            ["transcript"] = transcript,
        }.ToJsonString(), turnCt, context.CancellationToken).ConfigureAwait(false);
        context.State.Messages.Add(CascadeChatMessage.User(transcript));
        ReleaseUntranscribedAudio(turnAudio);
        context.SessionManager?.RecordTurn(context.SessionId, "guest", transcript);
        await RunTurnAndSpeakAsync(turnCt).ConfigureAwait(false);
    }

    public async Task SendGreetingAsync(CancellationToken turnCt)
    {
        if (context.PromptLoader is null)
        {
            return;
        }
        string? text;
        try
        {
            var greeting = (JsonObject)YamlJson.ToJsonNode(
                (IDictionary<object, object>)context.PromptLoader.Greeting)!;
            text = CascadeProcessor.GetString(
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
        context.State.Messages.Add(CascadeChatMessage.User(text));
        context.SessionManager?.MarkConversationStarted(context.SessionId);
        await RunTurnAndSpeakAsync(turnCt).ConfigureAwait(false);
    }
}
