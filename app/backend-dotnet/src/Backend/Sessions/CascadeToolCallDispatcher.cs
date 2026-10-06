using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the chat-completion/tool-dispatch collaborator extracted from
/// <see cref="CascadeProcessor.RunSessionAsync"/> -- cascade_processor.py's own
/// <c>_run_chat_tool_loop</c>/<c>_execute_tool_call</c> local closures, ported verbatim. Mirrors
/// <see cref="ToolCallDispatcher"/>'s role on the realtime side, adapted to cascade's own
/// chat-completion-with-tool-calls loop (realtime dispatches ONE tool call per
/// <c>response.function_call_arguments.done</c> event; cascade dispatches a whole ROUND of tool
/// calls returned by one chat-completion response before looping back for the model's next turn).
///
/// Pure move-and-delegate: every line below is unchanged from the original local functions except
/// that closed-over locals became constructor parameters -- including
/// <paramref name="notifyClientAsync"/>, which stays a delegate onto
/// <c>RunSessionAsync</c>'s own <c>NotifyClientAsync</c> local function (not duplicated here), and
/// <see cref="CascadeProcessor.SendTextAsync"/>/<see cref="CascadeProcessor.GetString"/>, reused
/// directly rather than re-implemented.
/// </summary>
internal sealed class CascadeToolCallDispatcher(
    WebSocket browserSocket,
    IToolExecutor toolExecutor,
    PromptLoader? promptLoader,
    CascadeProcessor.CascadeSessionState state,
    CascadeRateLimitSettings rateLimitSettings,
    FoundryChatClient chatClient,
    IReadOnlyList<JsonObject> toolDefinitions,
    SessionManager? sessionManager,
    string sessionId,
    TimeProvider timeProvider,
    Func<JsonObject, CancellationToken, Task> notifyClientAsync,
    ILogger logger,
    CancellationToken ct)
{
    private async Task<JsonObject> CallChatCompletionAsync(CancellationToken turnCt) =>
        await CascadeRateLimit.WithRetryAsync(
            rateLimitSettings,
            () => chatClient.CompleteAsync(state.Messages, state.Deployment, toolDefinitions, turnCt),
            "chat completion", notifyClientAsync, sessionId, logger, turnCt, timeProvider).ConfigureAwait(false);

    private async Task ExecuteToolCallAsync(JsonObject toolCall, string previousItemId, CancellationToken turnCt)
    {
        var callId = CascadeProcessor.GetString(toolCall, "id") ?? "";
        var name = CascadeProcessor.GetString(toolCall["function"] as JsonObject, "name") ?? "";
        if (!toolExecutor.ToolNames.Contains(name))
        {
            logger?.CascadeUnknownTool(name, sessionId);
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
            var rawArguments = CascadeProcessor.GetString(toolCall["function"] as JsonObject, "arguments");
            var argumentsJson = string.IsNullOrEmpty(rawArguments) ? "{}" : rawArguments;
            using var argumentsDoc = JsonDocument.Parse(argumentsJson);
            logger?.CascadeExecutingTool(name, sessionId);
            var result = await toolExecutor.ExecuteAsync(name, argumentsDoc.RootElement.Clone(), turnCt).ConfigureAwait(false);
            logger?.CascadeToolResultDirection(name, result.Destination, sessionId);

            // Issue #13 tail: track tool call args + result in the context window, mirroring
            // cascade_processor.py's own ctx_monitor.add_content(tool_call.function.arguments
            // or "") / ctx_monitor.add_content(result.to_text()) right after logging the
            // result.
            var ctxMonitorForTool = sessionManager?.GetContextMonitor(sessionId);
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
            logger?.CascadeToolUnhandledException(ex, name, sessionId);
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
                    logger?.CascadeTicketRefreshFailed(ticketEx, sessionId);
                }
                if (ticketJson is not null)
                {
                    await CascadeProcessor.SendTextAsync(browserSocket, new JsonObject
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
            await CascadeProcessor.SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "extension.middle_tier_tool_response",
                ["previous_item_id"] = previousItemId,
                ["tool_name"] = name,
                ["tool_result"] = clientText,
            }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> RunChatToolLoopAsync(CancellationToken turnCt)
    {
        for (var round = 0; round < CascadeProcessor.MaxToolRounds; round++)
        {
            var message = await CallChatCompletionAsync(turnCt).ConfigureAwait(false);
            var toolCalls = message["tool_calls"] as JsonArray;
            if (toolCalls is null || toolCalls.Count == 0)
            {
                var content = CascadeProcessor.GetString(message, "content") ?? "";
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
        logger?.CascadeChatToolLoopCapped(CascadeProcessor.MaxToolRounds, sessionId);
        return "";
    }
}
