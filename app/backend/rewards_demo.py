"""Fictional, session-only loyalty ledger. Never calls a membership service."""

from decimal import ROUND_FLOOR, Decimal


class RewardsDemo:
    def __init__(self, config):
        self.config = config
        self.checked_in = False
        self.balance = 0
        self.redeemed = False
        self.reserved_item = None
        self.completed = False
        self.awarded = 0
        self.message = "Use a fictional demo code. Never enter real member information."

    def reset_order(self):
        self.redeemed = False
        self.reserved_item = None
        self.completed = False
        self.awarded = 0
        self.message = "New demo order. Check-in and points remain in this session only."

    def reward_item(self, items):
        if self.reserved_item:
            return self.reserved_item
        return next((name for name in self.config.preferredRewardItems
                     if any(i.item == name and i.quantity > 0 and i.price > 0 for i in items)),
                    self.config.rewardItem)

    def discount(self, items) -> Decimal:
        eligible = [Decimal(str(i.price)) for i in items
                    if i.item == self.reward_item(items) and i.quantity > 0 and i.price > 0]
        if not eligible:
            self.redeemed = False
            self.reserved_item = None
        return min(eligible) if self.redeemed else Decimal("0")

    def projected(self, net_total: Decimal) -> int:
        return int((net_total * self.config.pointsPerDollar).to_integral_value(rounding=ROUND_FLOOR))

    def act(self, action, code, items, net_total: Decimal):
        if action == "status":
            return
        if self.completed:
            self.message = "Order already completed. Start a new demo order before making changes."
        elif action == "check_in":
            # Exact allow-list only: do not retain or echo submitted identifiers.
            if not isinstance(code, str) or code not in self.config.codes:
                self.message = "Invalid demo code. Use one of the fictional codes shown."
            elif self.checked_in:
                self.message = "Already checked in for this session."
            else:
                self.checked_in = True
                self.balance = self.config.codes[code]
                self.message = "Demo check-in linked to this order. No Taco Bell account was accessed."
        elif action == "redeem":
            eligible = any(i.item == self.reward_item(items) and i.quantity > 0 and i.price > 0 for i in items)
            if self.checked_in and self.balance >= self.config.rewardCost and eligible and not self.redeemed:
                self.redeemed = True
                self.reserved_item = self.reward_item(items)
                self.message = "Demo reward reserved. Points are spent only when you complete this demo order."
            else:
                self.message = "Reward unavailable: check in, have enough points, and add the eligible item."
        elif action == "unredeem":
            self.redeemed = False
            self.reserved_item = None
            self.message = "Demo reward removed. No points spent."
        elif action == "complete":
            if not self.checked_in or not items:
                self.message = "Check in and add an item before completing the demo order."
            else:
                self.awarded = self.projected(net_total)
                self.balance += self.awarded - (self.config.rewardCost if self.redeemed else 0)
                self.completed = True
                self.message = "Demo order completed once. Fictional points updated; no purchase was placed."
        else:
            self.message = "Unsupported demo rewards action."

    def snapshot(self, items, net_total, discount):
        reward_item = self.reward_item(items)
        eligible = any(i.item == reward_item and i.quantity > 0 and i.price > 0 for i in items)
        return {
            "checkedIn": self.checked_in, "balance": self.balance,
            "rewardItem": reward_item, "rewardCost": self.config.rewardCost,
            "pointsPerDollar": self.config.pointsPerDollar,
            "demoCodes": list(self.config.codes),
            "redeemed": self.redeemed, "completed": self.completed,
            "canRedeem": self.checked_in and not self.completed and not self.redeemed
            and self.balance >= self.config.rewardCost and eligible,
            "pointsToEarn": self.projected(net_total) if self.checked_in and not self.completed else 0,
            "pointsAwarded": self.awarded, "discount": float(discount), "message": self.message,
        }


async def handle_rewards_frame(ws, session_id, data):
    """Shared by realtime and cascade. No frame is forwarded upstream."""
    from order_state import order_state_singleton

    order_state_singleton.rewards_action(session_id, data.get("action"), data.get("code"))
    await ws.send_json({
        "type": "extension.middle_tier_tool_response", "previous_item_id": None,
        "tool_name": "get_order",
        "tool_result": order_state_singleton.get_order_summary_json(session_id),
    })
