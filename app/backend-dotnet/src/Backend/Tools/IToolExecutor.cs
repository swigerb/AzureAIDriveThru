using System.Text.Json;

namespace Backend.Tools;

/// <summary>
/// Session-bound seam for executing one realtime tool call -- the shape agreed on issue #14
/// (comment: "IToolExecutor contract for #13/#140's relay to dispatch through") and implemented by
/// <see cref="SessionToolExecutor"/> (PR #149), which composes <see cref="OrderToolExecutor"/>
/// (update_order/get_order/reset_order) and <see cref="Search.SearchTool"/> (search) into the one
/// instance a session's actor exposes.
///
/// <para><b>Design note (agreed on issue #14, referenced from #13):</b> Python's tool functions
/// take an explicit <c>session_id: str</c> and look up shared, module-level state
/// (<c>order_state_singleton</c>) keyed by it. In C# there is no such lookup: <b>one
/// <see cref="IToolExecutor"/> instance is owned by exactly one session's actor</b> (see
/// docs/dotnet_mapping.md's actor-confinement note) and is already closed over that session's own
/// <c>OrderState</c>/<c>MenuCatalog</c>/<c>PromptLoader</c> -- so there is no <c>sessionId</c>
/// parameter here at all, and no possibility of one session's tool call touching another session's
/// order by a wrong key. <see cref="Sessions.RealtimeProcessor"/> builds one
/// <see cref="IToolExecutor"/> per session (once persona binding resolves) and calls
/// <see cref="ExecuteAsync"/> on every <c>response.output_item.done</c> function_call, exactly
/// mirroring rtmt.py's <c>tool.target(args, session_id)</c> dispatch.</para>
/// </summary>
internal interface IToolExecutor
{
    /// <summary>Every tool name this executor can dispatch for its session's bound persona
    /// (typically "search", "update_order", "get_order", "reset_order" -- mirrors
    /// attach_tools_rtmt's <c>rtmt.tools[...]</c> registrations). The relay checks membership
    /// before calling <see cref="ExecuteAsync"/> to raise its own "unknown tool" handling for a
    /// tool name the model hallucinated instead of letting an exception surface.</summary>
    IReadOnlyList<string> ToolNames { get; }

    /// <summary>Executes <paramref name="args"/> (the raw JSON object the model's function-call
    /// carried -- an empty object for get_order/reset_order, which take no arguments) against this
    /// executor's own session-bound state, returning the structured/plain-text
    /// <see cref="ToolResult"/> to relay back to the model (and, for
    /// <see cref="ToolResultDirection.ToBoth"/>/<see cref="ToolResultDirection.ToClient"/>, to the
    /// connected client). Must never throw for a well-formed call -- an execution failure should be
    /// reported via the returned <see cref="ToolResult"/> or (for a genuinely unexpected fault) let
    /// the caller's own catch-all produce the neutral fallback text, exactly like rtmt.py's
    /// <c>except Exception</c> around <c>tool.target(...)</c>.</summary>
    Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default);
}
