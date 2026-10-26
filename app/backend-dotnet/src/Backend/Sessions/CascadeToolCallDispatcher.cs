using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Runs complete chat/tool rounds using the connection's current identity and executor.
/// Resume can replace those bindings before the first turn starts.
/// </summary>
internal sealed class CascadeToolCallDispatcher(
    CascadeSessionContext context,
    CascadeRateLimitSettings rateLimitSettings,
    FoundryChatClient chatClient,
    IReadOnlyList<JsonObject> toolDefinitions)
{
    private async Task<JsonObject> CallChatCompletionAsync(
        CancellationToken turnCt, IReadOnlyList<JsonObject>? messages = null, bool disableTools = false)
    {
        if (context.Diagnostic is { } diagnostic && diagnostic.SessionId != context.SessionId)
        {
            context.Logger.LogWarning("Synthetic cascade diagnostics disabled: session binding changed.");
            context.Diagnostic = null;
        }
        return await CascadeRateLimit.WithRetryAsync(
            rateLimitSettings,
            () => chatClient.CompleteAsync(messages ?? context.State.Messages, context.State.Deployment,
                toolDefinitions, turnCt, context.Diagnostic, disableTools),
            "chat completion", context.NotifyClientAsync, context.SessionId, context.Logger, turnCt,
            context.TimeProvider).ConfigureAwait(false);
    }

    private async Task ExecuteToolCallAsync(JsonObject toolCall, string previousItemId, CancellationToken turnCt)
    {
        string callId = CascadeProcessor.GetString(toolCall, "id") ?? "";
        string name = CascadeProcessor.GetString(toolCall["function"] as JsonObject, "name") ?? "";
        if (!context.ToolExecutor.ToolNames.Contains(name))
        {
            context.Logger.CascadeUnknownTool(name, context.SessionId);
            context.State.Messages.Add(CascadeChatMessage.Tool("", callId));
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
            string? rawArguments = CascadeProcessor.GetString(toolCall["function"] as JsonObject, "arguments");
            string argumentsJson = string.IsNullOrEmpty(rawArguments) ? "{}" : rawArguments;
            using var argumentsDoc = JsonDocument.Parse(argumentsJson);
            context.Logger.CascadeExecutingTool(name, context.SessionId);
            ToolResult result = await context.ToolExecutor.ExecuteAsync(name, argumentsDoc.RootElement.Clone(), turnCt).ConfigureAwait(false);
            context.Logger.CascadeToolResultDirection(name, result.Destination, context.SessionId);

            // Issue #13 tail: track tool call args + result in the context window, mirroring
            // cascade_processor.py's own ctx_monitor.add_content(tool_call.function.arguments
            // or "") / ctx_monitor.add_content(result.to_text()) right after logging the
            // result.
            ContextMonitor? ctxMonitorForTool = context.SessionManager?.GetContextMonitor(context.SessionId);
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
            context.Logger.CascadeToolUnhandledException(ex, name, context.SessionId);
            outputText = context.PromptLoader?.RenderError("tool_execution_failed") ??
                "Something went wrong with that action and it did not complete. Don't retry it yet -- " +
                "call get_order to confirm the order's current state, then ask the guest to repeat what they'd like.";
            sendToClient = false;
            clientText = null;

            await RefreshTicketAsync(previousItemId, turnCt).ConfigureAwait(false);
        }

        context.State.Messages.Add(CascadeChatMessage.Tool(outputText, callId));
        if (sendToClient)
        {
            await context.NotifyClientAsync(new JsonObject
            {
                ["type"] = "extension.middle_tier_tool_response",
                ["previous_item_id"] = previousItemId,
                ["tool_name"] = name,
                ["tool_result"] = clientText,
            }, turnCt).ConfigureAwait(false);
        }
    }

    private async Task RefreshTicketAsync(string previousItemId, CancellationToken turnCt)
    {
        if (context.ToolExecutor is not IOrderTicketSource ticketSource)
        {
            return;
        }
        string? ticketJson = null;
        try
        {
            ticketJson = ticketSource.CurrentOrderSummaryJson;
        }
        catch (Exception ex)
        {
            context.Logger.CascadeTicketRefreshFailed(ex, context.SessionId);
        }
        if (ticketJson is not null)
        {
            await context.NotifyClientAsync(new JsonObject
            {
                ["type"] = "extension.middle_tier_tool_response",
                ["previous_item_id"] = previousItemId,
                ["tool_name"] = "get_order",
                ["tool_result"] = ticketJson,
            }, turnCt).ConfigureAwait(false);
        }
    }

    public async Task<string> RunChatToolLoopAsync(CancellationToken turnCt)
    {
        for (int round = 0; round < CascadeProcessor.MaxToolRounds; round++)
        {
            JsonObject message = await CallChatCompletionAsync(turnCt).ConfigureAwait(false);
            if (message["tool_calls"] is not JsonArray toolCalls || toolCalls.Count == 0)
            {
                string content = CascadeProcessor.GetString(message, "content") ?? "";
                context.State.Messages.Add(CascadeChatMessage.Assistant(content));
                return content;
            }

            int preRoundCount = context.State.Messages.Count;
            context.State.Messages.Add((JsonObject)message.DeepClone());
            string previousItemId = MiddleTierItemIds.NewId();
            try
            {
                foreach (JsonNode? toolCallNode in toolCalls)
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
                context.State.Messages.RemoveRange(preRoundCount, context.State.Messages.Count - preRoundCount);
                throw;
            }
        }
        context.Logger.CascadeChatToolLoopCapped(CascadeProcessor.MaxToolRounds, context.SessionId);
        return "";
    }

    public async Task<string> RunGreetingAsync(string instructions, CancellationToken turnCt)
    {
        // Keep the one-shot instruction out of the resumable conversation, including on cancellation.
        var messages = context.State.Messages
            .Where(message => message["role"]?.GetValue<string>() == "system")
            .Select(message => (JsonObject)message.DeepClone()).ToList();
        messages.Add(CascadeChatMessage.System(instructions));
        JsonObject message = await CallChatCompletionAsync(turnCt, messages, disableTools: true).ConfigureAwait(false);
        if (message["tool_calls"] is JsonArray { Count: > 0 })
        {
            throw new InvalidOperationException("Greeting completion returned tool calls with tools disabled");
        }

        string content = CascadeProcessor.GetString(message, "content") ?? "";
        if (!string.IsNullOrWhiteSpace(content))
        {
            context.State.Messages.Add(CascadeChatMessage.Assistant(content));
        }

        return content;
    }
}
