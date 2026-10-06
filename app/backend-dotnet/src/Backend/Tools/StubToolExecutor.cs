using System.Text.Json;

namespace Backend.Tools;

/// <summary>
/// Thin, order-logic-free <see cref="IToolExecutor"/> that proves issue #13's tool-call wire
/// plumbing (registration → execution → function_call_output upstream →
/// extension.middle_tier_tool_response to the browser → auto response.create) without depending on
/// #14's (Summer's, parallel, uncommitted at the time this shipped) real order/search
/// implementations. Every recognised tool name returns a neutral, clearly-a-stub result; an
/// unrecognised name is handled by the caller exactly like rtmt.py's `self.tools.get(name)` miss
/// (a fallback error result, not an exception).
/// </summary>
public sealed class StubToolExecutor : IToolExecutor
{
    public IReadOnlyList<string> ToolNames { get; }

    public StubToolExecutor(IEnumerable<string> toolNames)
    {
        ToolNames = toolNames.Distinct().ToList();
    }

    public Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        var result = new ToolResult(
            $"Tool '{toolName}' is not yet implemented in the .NET middle tier (see issue #14).",
            ToolResultDirection.ToBoth,
            clientText: $"(stub) {toolName} acknowledged.");
        return Task.FromResult(result);
    }
}
