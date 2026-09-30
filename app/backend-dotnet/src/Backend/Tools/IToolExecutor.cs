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
/// Session-bound seam for executing one realtime tool call -- the shape agreed on issue #14
/// (comment: "IToolExecutor contract for #13/#140's relay to dispatch through") and already merged
/// into #14's own `squad/14-csharp-tools-orders` branch (`Tools/SessionToolExecutor.cs`, PR #149) as
/// the real implementation. <see cref="Backend.Sessions.RealtimeProcessor"/> calls
/// <see cref="ExecuteAsync"/> on every <c>response.output_item.done</c> function_call, exactly
/// mirroring rtmt.py's <c>tool.target(args, session_id)</c> dispatch -- session scoping happens by
/// construction (one <c>IToolExecutor</c> instance per session's actor, composed once persona
/// binding resolves), not by an extra per-call session id parameter, which is why this interface
/// (unlike an earlier draft of it in this same PR) carries no <c>sessionId</c> argument. #14's real
/// order/search tool implementation binds here; until that PR merges,
/// <see cref="StubToolExecutor"/> proves the relay's tool-call wire plumbing end-to-end
/// (registration, execution, function_call_output upstream, extension.middle_tier_tool_response to
/// the browser, auto response.create) without any real order/search logic.
/// </summary>
public interface IToolExecutor
{
    /// <summary>The tool names this executor recognises (mirrors rtmt.py's <c>self.tools</c> dict
    /// keys) -- used to build the bootstrap/session tool schema list and to detect an unknown tool
    /// name the same way rtmt.py's <c>self.tools.get(name)</c> miss does.</summary>
    IReadOnlyList<string> ToolNames { get; }

    /// <summary>Executes one tool call. Must never throw for a well-formed call; an execution
    /// failure should be reported via the returned <see cref="ToolResult"/> or (for a genuinely
    /// unexpected fault) let the caller's own catch-all produce the neutral fallback text, exactly
    /// like rtmt.py's `except Exception` around `tool.target(...)`.</summary>
    Task<ToolResult> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default);
}
