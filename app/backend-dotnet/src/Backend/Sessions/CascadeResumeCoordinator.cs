using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Backend.Shared;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Owns the cascade first-frame resume handshake for one connection.
/// </summary>
internal sealed class CascadeResumeCoordinator(CascadeSessionContext context)
{
    public async Task<JsonObject?> NegotiateAsync()
    {
        WebSocketFrame? firstFrame = await ReadFirstFrameAsync().ConfigureAwait(false);
        (JsonObject? resumeData, JsonObject? leftover) = ParseFirstFrame(firstFrame);
        if (resumeData is not null && await TryResumeAsync(resumeData).ConfigureAwait(false))
        {
            return null;
        }
        string? resumeId = context.SessionManager!.IssueResumeId(context.SessionId);
        JsonObject metadataFrame = ResumeEnvelope.BuildSessionMetadataFrame(context.Identifiers, resumeId);
        await context.NotifyClientAsync(metadataFrame, context.CancellationToken).ConfigureAwait(false);
        SetGreetingTurn();
        return leftover;
    }

    private async Task<WebSocketFrame?> ReadFirstFrameAsync()
    {
        WebSocketFrame? firstFrame = null;
        double timeoutSeconds = context.SessionManager!.Config.FirstFrameTimeoutSeconds;
        if (timeoutSeconds > 0)
        {
            context.PendingFirstFrameTask = WebSocketFrameReader.ReadMessageAsync(
                context.BrowserSocket, context.CancellationToken);
            Task winner = await Task.WhenAny(
                    context.PendingFirstFrameTask,
                    Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), context.TimeProvider, context.CancellationToken))
                .ConfigureAwait(false);
            if (winner == context.PendingFirstFrameTask)
            {
                firstFrame = await context.PendingFirstFrameTask.ConfigureAwait(false);
                context.PendingFirstFrameTask = null;
            }
        }

        return firstFrame;
    }

    private static (JsonObject? ResumeData, JsonObject? Leftover) ParseFirstFrame(WebSocketFrame? firstFrame)
    {
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
            if (parsed is not null && CascadeProcessor.GetString(parsed, "type") == "extension.resume")
            {
                resumeData = parsed;
            }
            else
            {
                leftover = parsed;
            }
        }

        return (resumeData, leftover);
    }

    private async Task<bool> TryResumeAsync(JsonObject resumeData)
    {
        string? presentedId = CascadeProcessor.GetString(resumeData, "resume_id");
        ResumeOutcome outcome = context.SessionManager!.TryResume(
            context.BrowserSocket,
            presentedId,
            context.Persona.Id,
            context.ResolvedModel.Id,
            context.MenuMode,
            context.SessionId,
            context.LinkedCts);
        if (outcome.Accepted)
        {
            context.SessionId = outcome.SessionId!;
            context.ToolExecutor = outcome.ToolExecutor!;
            context.State.Voice = outcome.Voice!;
            if (outcome.Identifiers is { } originalIdentifiers)
            {
                context.Identifiers = originalIdentifiers;
            }
            if (outcome.StaleWs is { } staleWs)
            {
                Task.Run(
                    () => FramePump.CloseSupersededStaleConnectionAsync(
                        staleWs, outcome.StaleCts, FramePump.s_supersededCloseTimeout, context.Logger),
                    CancellationToken.None).FireAndForget(
                        context.Logger, nameof(FramePump.CloseSupersededStaleConnectionAsync));
            }
            string orderSummaryJson = context.ToolExecutor is IOrderTicketSource ticketSource
                ? SafeOrderSummaryJson(ticketSource)
                : "{}";
            await CascadeProcessor.SendTextAsync(
                context.BrowserSocket,
                ResumeEnvelope.BuildSessionResumedFrame(
                    context.Identifiers, orderSummaryJson, outcome.ResumeId).ToJsonString(),
                context.CancellationToken,
                context.CancellationToken).ConfigureAwait(false);

            if (outcome.ConversationStarted)
            {
                context.State.Messages.Add(CascadeChatMessage.System(SessionManager.BuildRehydrationText(
                    orderSummaryJson,
                    outcome.RecentTurns ?? [],
                    context.State.RoleName)));
                if (context.SessionManager.Config.NudgeAfterSeconds > 0)
                {
                    context.State.NudgeEligible = true;
                }
                context.Logger.CascadeResumeRehydrated(
                    context.SessionId, outcome.RecentTurns?.Count ?? 0);
            }
            else
            {
                SetGreetingTurn();
            }
            return true;
        }

        context.Logger.CascadeResumeRejected(outcome.Reason, context.SessionId);
        await CascadeProcessor.SendTextAsync(
            context.BrowserSocket,
            ResumeEnvelope.BuildResumeRejectedFrame(outcome.Reason).ToJsonString(),
            context.CancellationToken,
            context.CancellationToken).ConfigureAwait(false);
        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort order read through IOrderTicketSource, whose implementations can fail in any way; the failure is logged and an empty summary is used.")]
    private string SafeOrderSummaryJson(IOrderTicketSource source)
    {
        try
        {
            return source.CurrentOrderSummaryJson;
        }
        catch (Exception ex)
        {
            context.Logger.CascadeOrderStateReadFailed(ex, context.SessionId);
            return "{}";
        }
    }

    private void SetGreetingTurn()
    {
        (CancellationTokenSource? greetingCts, Task? greetingTask) = context.TurnCoordinator.Spawn(context.SendGreetingAsync, "greeting");
        context.State.CurrentTurnCts = greetingCts;
        context.State.CurrentTurnTask = greetingTask;
    }
}
