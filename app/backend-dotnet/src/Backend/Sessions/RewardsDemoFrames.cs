using System.Text.Json.Nodes;
using Backend.Tools;

namespace Backend.Sessions;

internal static class RewardsDemoFrames
{
    public static JsonObject Handle(IToolExecutor tools, JsonObject frame)
    {
        if (tools is not IRewardsDemoControl rewards ||
            !rewards.RewardsAction(RealtimeProcessor.GetString(frame, "action"), RealtimeProcessor.GetString(frame, "code")))
            return new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "Demo rewards unavailable." } };
        return new JsonObject
        {
            ["type"] = "extension.middle_tier_tool_response", ["previous_item_id"] = null,
            ["tool_name"] = "get_order", ["tool_result"] = rewards.CurrentOrderSummaryJson,
        };
    }
}
