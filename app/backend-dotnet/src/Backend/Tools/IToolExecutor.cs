using System.Text.Json;

namespace Backend.Tools;

/// <summary>
/// The seam Beth's #13 realtime middle-tier port calls into for every function/tool call the
/// model issues in one session: given the tool name the Realtime API reported and its raw JSON
/// arguments, execute it against THIS session's own bound order/menu/persona state and return a
/// <see cref="ToolResult"/> exactly as rtmt.py's own <c>rtmt.tools[name].target(args, session_id)</c>
/// dispatch does in Python.
///
/// <para><b>Design note (agreed on issue #14, referenced from #13):</b> Python's tool functions
/// take an explicit <c>session_id: str</c> and look up shared, module-level state
/// (<c>order_state_singleton</c>) keyed by it. In C# there is no such lookup: <b>one
/// <see cref="IToolExecutor"/> instance is owned by exactly one <c>SessionActor</c></b> (see
/// docs/dotnet_mapping.md's actor-confinement note) and is already closed over that session's own
/// <c>OrderState</c>/<c>MenuCatalog</c>/<c>PromptLoader</c> -- so there is no <c>sessionId</c>
/// parameter here at all, and no possibility of one session's tool call touching another session's
/// order by a wrong key. Beth's relay resolves "which session" simply by calling the
/// <see cref="IToolExecutor"/> instance that session's own actor exposes; it never passes a session
/// identifier across this interface.</para>
/// </summary>
public interface IToolExecutor
{
    /// <summary>Every tool name this executor can dispatch for its session's bound persona
    /// (typically "search", "update_order", "get_order", "reset_order" -- mirrors
    /// attach_tools_rtmt's <c>rtmt.tools[...]</c> registrations). Beth's relay can check membership
    /// before calling <see cref="ExecuteAsync"/> to raise its own "tool_execution_failed" style
    /// message for a tool name the model hallucinated instead of letting an exception surface.</summary>
    IReadOnlyCollection<string> ToolNames { get; }

    /// <summary>Executes <paramref name="toolName"/> with <paramref name="arguments"/> (the raw
    /// JSON object the model's function-call carried -- an empty object for get_order/reset_order,
    /// which take no arguments) against this executor's own session-bound state, returning the
    /// structured/plain-text <see cref="ToolResult"/> to relay back to the model (and, for
    /// <see cref="ToolResultDirection.ToBoth"/>/<see cref="ToolResultDirection.ToClient"/>, to the
    /// connected client). Throws <see cref="KeyNotFoundException"/> if <paramref name="toolName"/>
    /// is not in <see cref="ToolNames"/> -- callers should check <see cref="ToolNames"/> first for
    /// unrecognized tool names rather than relying on the exception path.</summary>
    Task<ToolResult> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}
