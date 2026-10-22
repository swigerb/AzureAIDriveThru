namespace Backend.Tools;

internal interface IRewardsDemoControl
{
    bool RewardsAction(string? action, string? code);
    string CurrentOrderSummaryJson { get; }
}
