namespace Backend.Tools;

/// <summary>
/// Optional, session-bound control surface for extension-originated order settings that are owned
/// by the per-session <c>OrderState</c> rather than by the websocket relay itself: per-machine
/// availability overrides and the happy-hour mode override. Kept separate from
/// <see cref="IToolExecutor"/> for the same reason as <see cref="IOrderTicketSource"/>:
/// non-order executors remain valid without implementing it.
/// </summary>
public interface IOrderSessionSettings
{
    bool SetMachineOverride(string machine, string status);
    IReadOnlyDictionary<string, string> GetMachineOverrides();
    string? EffectiveMachineStatus(string machine);
    bool SetHappyHourMode(string mode);
    string GetHappyHourMode();
}
