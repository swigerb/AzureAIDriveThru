using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the tool-call dispatch collaborator extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/> -- rtmt.py's own
/// <c>response.output_item.done</c> function_call handling, ported verbatim as
/// <c>HandleToolCallDoneAsync</c>.
///
/// Pure move-and-delegate: every line below is unchanged from the original local function except
/// that closed-over locals (<paramref name="upstream"/>, <paramref name="browserSocket"/>,
/// <paramref name="state"/>, ...) became constructor parameters, and the <c>SendTextAsync</c>
/// local wrapper became <see cref="FramePump.SendTextAsync"/>.
/// </summary>
internal sealed class ToolCallDispatcher(
    WebSocket upstream,
    WebSocket browserSocket,
    RealtimeProcessor.RealtimeSessionState state,
    SessionManager? sessionManager,
    string sessionId,
    ILogger logger,
    CancellationToken ct)
{
    public async Task HandleToolCallDoneAsync(JsonObject item)
    {
        var callId = RealtimeProcessor.GetString(item, "call_id");
        if (callId is null || !state.ToolsPending.TryGetValue(callId, out var previousItemId))
        {
            logger?.ToolCallNotFoundInPending(callId, sessionId);
            return;
        }
        var toolName = RealtimeProcessor.GetString(item, "name") ?? "";
        if (!state.ToolExecutor.ToolNames.Contains(toolName))
        {
            logger?.UnknownToolRequested(toolName, sessionId);
            return;
        }

        // Rick's #244 round-2 review, issue 1: refuse to dispatch once this connection has
        // been superseded by a resume elsewhere, checked HERE -- synchronously, with no IO --
        // rather than relying on StaleCts having been cancelled promptly. StaleCts cancellation
        // now happens from a BACKGROUND task (see HandleResumeFirstFrameAsync) that may still
        // be mid-flight against a non-draining stale peer, and even when prompt,
        // OrderToolExecutor.ExecuteAsync is synchronous and ignores its own CancellationToken
        // entirely -- an in-flight call already past this point would run to completion and
        // mutate the shared (not thread-safe) OrderState regardless of cancellation. This flag
        // is set synchronously, under SessionManager's own lock, the instant TryResume captures
        // this connection as stale (SessionManager.SupersededFlag's own doc comment has the
        // full reasoning), so it is safe to trust here with no further synchronization.
        if (state.Superseded.IsSuperseded)
        {
            logger?.ToolCallDroppedSuperseded(toolName, callId, sessionId);
            return;
        }

        string outputText;
        bool sendToClient;
        string? clientText;
        try
        {
            var argumentsJson = RealtimeProcessor.GetString(item, "arguments") ?? "{}";
            using var argumentsDoc = JsonDocument.Parse(argumentsJson);
            logger?.ExecutingTool(toolName, sessionId);
            var result = await state.ToolExecutor.ExecuteAsync(toolName, argumentsDoc.RootElement.Clone(), ct)
                .ConfigureAwait(false);
            logger?.ToolResultDirectionLogged(toolName, result.Destination, sessionId);
            outputText = result.Destination is ToolResultDirection.ToServer or ToolResultDirection.ToBoth
                ? result.ToText() : "";
            sendToClient = result.Destination is ToolResultDirection.ToClient or ToolResultDirection.ToBoth;
            clientText = sendToClient ? result.ToClientText() : null;

            // Issue #13 tail: track tool call args + result in the context window, mirroring
            // rtmt.py's ctx_monitor.add_content(item.get("arguments", "")) /
            // ctx_monitor.add_content(result.to_text()).
            var ctxMonitorForTool = sessionManager?.GetContextMonitor(state.EffectiveSessionId);
            if (ctxMonitorForTool is not null)
            {
                ctxMonitorForTool.AddContent(argumentsJson);
                ctxMonitorForTool.AddContent(result.ToText());
            }
        }
        catch (Exception ex)
        {
            logger?.ToolUnhandledException(ex, toolName, sessionId);
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
            if (state.ToolExecutor is IOrderTicketSource ticketSource)
            {
                string? ticketJson = null;
                try
                {
                    ticketJson = ticketSource.CurrentOrderSummaryJson;
                }
                catch (Exception ticketEx)
                {
                    logger?.TicketRefreshAfterToolFailureFailed(ticketEx, sessionId);
                }

                if (ticketJson is not null)
                {
                    await FramePump.SendTextAsync(browserSocket, new JsonObject
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

        await FramePump.SendTextAsync(upstream, new JsonObject
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
            await FramePump.SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "extension.middle_tier_tool_response",
                ["previous_item_id"] = previousItemId,
                ["tool_name"] = toolName,
                ["tool_result"] = clientText,
            }.ToJsonString(), ct).ConfigureAwait(false);
        }
    }
}
