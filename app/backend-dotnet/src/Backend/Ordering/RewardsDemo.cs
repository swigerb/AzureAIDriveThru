using System.Text.Json.Nodes;
using Backend.Personas;

namespace Backend.Ordering;

/// <summary>Fictional, session-only ledger. Never contacts a membership service.</summary>
internal sealed class RewardsDemo(RewardsDemoConfig config)
{
    private bool _checkedIn;
    private int _balance;
    private bool _redeemed;
    public bool Completed { get; private set; }
    private int _awarded;
    private string _message = "Use a fictional demo code. Never enter real member information.";

    public void ResetOrder()
    {
        _redeemed = false;
        Completed = false;
        _awarded = 0;
        _message = "New demo order. Check-in and points remain in this session only.";
    }

    private IEnumerable<decimal> EligiblePrices(IReadOnlyList<OrderItem> items) =>
        items.Where(i => i.Item == config.RewardItem && i.Quantity > 0 && i.Price > 0).Select(i => i.Price);

    public decimal Discount(IReadOnlyList<OrderItem> items)
    {
        var prices = EligiblePrices(items).ToList();
        if (prices.Count == 0) _redeemed = false;
        return _redeemed ? prices.Min() : 0m;
    }

    private int Projected(decimal netTotal) => (int)decimal.Floor(netTotal * config.PointsPerDollar);

    public void Act(string? action, string? code, IReadOnlyList<OrderItem> items, decimal netTotal)
    {
        if (action == "status") return;
        if (Completed)
        {
            _message = "Order already completed. Start a new demo order before making changes.";
            return;
        }
        switch (action)
        {
            case "check_in":
                if (code is null || !config.Codes.TryGetValue(code, out var startingPoints))
                    _message = "Invalid demo code. Use one of the fictional codes shown.";
                else if (_checkedIn)
                    _message = "Already checked in for this session.";
                else
                {
                    _checkedIn = true;
                    _balance = startingPoints;
                    _message = "Demo check-in linked to this order. No Taco Bell account was accessed.";
                }
                break;
            case "redeem":
                if (_checkedIn && _balance >= config.RewardCost && EligiblePrices(items).Any() && !_redeemed)
                {
                    _redeemed = true;
                    _message = "Demo reward reserved. Points are spent only when you complete this demo order.";
                }
                else
                    _message = "Reward unavailable: check in, have enough points, and add the eligible item.";
                break;
            case "unredeem":
                _redeemed = false;
                _message = "Demo reward removed. No points spent.";
                break;
            case "complete":
                if (!_checkedIn || items.Count == 0)
                    _message = "Check in and add an item before completing the demo order.";
                else
                {
                    _awarded = Projected(netTotal);
                    _balance += _awarded - (_redeemed ? config.RewardCost : 0);
                    Completed = true;
                    _message = "Demo order completed once. Fictional points updated; no purchase was placed.";
                }
                break;
            default:
                _message = "Unsupported demo rewards action.";
                break;
        }
    }

    public JsonObject Snapshot(IReadOnlyList<OrderItem> items, decimal netTotal, decimal discount) => new()
    {
        ["checkedIn"] = _checkedIn, ["balance"] = _balance,
        ["rewardItem"] = config.RewardItem, ["rewardCost"] = config.RewardCost,
        ["pointsPerDollar"] = config.PointsPerDollar,
        ["demoCodes"] = new JsonArray(config.Codes.Keys.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["redeemed"] = _redeemed, ["completed"] = Completed,
        ["canRedeem"] = _checkedIn && !Completed && !_redeemed && _balance >= config.RewardCost && EligiblePrices(items).Any(),
        ["pointsToEarn"] = _checkedIn && !Completed ? Projected(netTotal) : 0,
        ["pointsAwarded"] = _awarded, ["discount"] = discount, ["message"] = _message,
    };
}
