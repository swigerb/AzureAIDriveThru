using System.Text.Json;

namespace Backend.Tools;

/// <summary>
/// Port of app/backend/tools.py's <c>ToolResultDirection</c> (issue #13's tool-call plumbing
/// acceptance target -- the interface Summer's #14 order/search tool implementations bind to).
/// Where a tool's result goes: back to the model only (<see cref="ToServer"/>), to the browser
/// only (<see cref="ToClient"/>, rare), or both (<see cref="ToBoth"/>, the common case -- the model
/// gets its function_call_output, the browser gets an extension.middle_tier_tool_response so the
/// UI ticket can update without waiting for the model to narrate it).
/// </summary>
public enum ToolResultDirection
{
    ToServer,
    ToClient,
    ToBoth,
}

/// <summary>
/// Port of app/backend/tools.py's <c>ToolResult</c>. <see cref="ServerText"/> is what the model
/// sees as its function_call_output; <see cref="ClientText"/> (defaulting to
/// <see cref="ServerText"/> when null) is what rides on extension.middle_tier_tool_response's
/// tool_result field when <see cref="Destination"/> includes the client.
/// </summary>
public sealed record ToolResult(ToolResultDirection Destination, string ServerText, string? ClientText = null)
{
    public string ToText() => ServerText;

    public string ToClientText() => ClientText ?? ServerText;
}

/// <summary>
/// Session-bound seam for executing one realtime tool call (issue #13/#14 coordination point --
/// see issue #14 and PR #140 comments). <see cref="Backend.Sessions.RealtimeProcessor"/> calls this
/// on every <c>response.output_item.done</c> function_call, exactly mirroring rtmt.py's
/// <c>tool.target(args, session_id)</c> dispatch. #14's real order/search tool implementations bind
/// here; until that lands, <see cref="StubToolExecutor"/> proves the relay's tool-call wire
/// plumbing end-to-end (registration, execution, function_call_output upstream,
/// extension.middle_tier_tool_response to the browser, auto response.create) without any real
/// order/search logic.
/// </summary>
public interface IToolExecutor
{
    /// <summary>The tool names this executor recognises (mirrors rtmt.py's <c>self.tools</c> dict
    /// keys) -- used to build the bootstrap/session tool schema list and to detect an unknown tool
    /// name the same way rtmt.py's <c>self.tools.get(name)</c> miss does.</summary>
    IReadOnlySet<string> ToolNames { get; }

    /// <summary>Executes one tool call. <paramref name="sessionId"/> is passed for every tool
    /// (unlike Python's name-based `("update_order", "get_order", "reset_order", "search")`
    /// special-case) -- a stub/thin adapter has no order-state tools to distinguish yet, and a real
    /// #14 implementation can simply ignore it for a tool that doesn't need it. Must never throw
    /// for a well-formed call; an execution failure should be reported via the returned
    /// <see cref="ToolResult"/> or (for a genuinely unexpected fault) let the caller's own
    /// catch-all produce the neutral fallback text, exactly like rtmt.py's `except Exception` around
    /// `tool.target(...)`.</summary>
    Task<ToolResult> ExecuteAsync(string toolName, JsonElement arguments, string? sessionId, CancellationToken cancellationToken);
}
