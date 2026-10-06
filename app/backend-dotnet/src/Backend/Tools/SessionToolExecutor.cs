using System.Text.Json;
using Backend.Search;

namespace Backend.Tools;

/// <summary>
/// Composes <see cref="OrderToolExecutor"/> (update_order/get_order/reset_order) and <see
/// cref="SearchTool"/> (search) into the one <see cref="IToolExecutor"/> a session's
/// <c>SessionActor</c> exposes to Beth's #13 realtime relay -- exactly the "whatever composes
/// both into one <see cref="IToolExecutor"/>" placeholder <see cref="OrderToolExecutor"/>'s own doc
/// comment points at. Like both of the tools it wraps, one instance is owned by exactly one
/// session's actor; it holds no state of its own beyond the two composed tools.
/// </summary>
public sealed class SessionToolExecutor : IToolExecutor, IOrderTicketSource, IOrderSessionSettings
{
    private readonly OrderToolExecutor _orderTools;
    private readonly SearchTool _search;

    public SessionToolExecutor(OrderToolExecutor orderTools, SearchTool search)
    {
        _orderTools = orderTools;
        _search = search;
        ToolNames = ["search", .. orderTools.ToolNames];
    }

    public IReadOnlyList<string> ToolNames { get; }

    /// <summary>Delegates to the composed <see cref="OrderToolExecutor"/> -- see
    /// <see cref="IOrderTicketSource"/>'s own doc comment for why this exists.</summary>
    public string CurrentOrderSummaryJson => _orderTools.CurrentOrderSummaryJson;

    public bool SetMachineOverride(string machine, string status) => _orderTools.SetMachineOverride(machine, status);

    public IReadOnlyDictionary<string, string> GetMachineOverrides() => _orderTools.GetMachineOverrides();

    public string? EffectiveMachineStatus(string machine) => _orderTools.EffectiveMachineStatus(machine);

    public bool SetHappyHourMode(string mode) => _orderTools.SetHappyHourMode(mode);

    public string GetHappyHourMode() => _orderTools.GetHappyHourMode();

    public Task<ToolResult> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        toolName == "search"
            ? _search.ExecuteAsync(arguments, cancellationToken)
            : _orderTools.ExecuteAsync(toolName, arguments, cancellationToken);
}
