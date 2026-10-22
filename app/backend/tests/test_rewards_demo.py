import asyncio
import json
import shutil
import sys
from decimal import Decimal
from pathlib import Path
from unittest.mock import AsyncMock

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import order_state_singleton as orders
from persona_loader import PersonaCatalog, PersonaValidationError
from rewards_demo import handle_rewards_frame


@pytest.fixture
def session():
    catalog = PersonaCatalog.load(personas_dir=Path(__file__).resolve().parents[3] / "personas")
    sid = orders.create_session(catalog.get("tacobell"))
    yield sid
    orders.delete_session(sid)


def summary(sid):
    return json.loads(orders.get_order_summary_json(sid))


def add(sid, item="Crunchy Taco", quantity=1):
    orders.handle_order_update(sid, "add", item, "standard", quantity, 999)


@pytest.mark.parametrize("code", ["123456", "DEMO-UNKNOWN", "demo-plus", "", None, 123, {}, ["DEMO-PLUS"]])
def test_only_exact_fictional_codes_are_accepted_without_retaining_input(session, code):
    orders.rewards_action(session, "check_in", code)
    result = summary(session)["rewardsDemo"]
    assert not result["checkedIn"]
    assert result["balance"] == 0
    assert result["message"].startswith("Invalid demo code")


def test_reward_discount_tax_points_and_completion_are_authoritative_and_idempotent(session):
    add(session, quantity=2)
    add(session, "Crunchwrap Supreme")
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    assert summary(session)["rewardsDemo"]["canRedeem"]
    orders.rewards_action(session, "redeem")
    first = summary(session)
    assert first["rewardsDemo"]["discount"] == 1.99
    assert first["totalDisplay"] == "$7.98"
    assert first["taxDisplay"] == "$0.64"
    assert first["finalTotalDisplay"] == "$8.62"
    assert first["rewardsDemo"]["pointsToEarn"] == 79
    orders.rewards_action(session, "redeem")
    assert summary(session)["total"] == first["total"]
    orders.rewards_action(session, "complete")
    completed = summary(session)
    assert completed["rewardsDemo"]["balance"] == 329
    assert completed["rewardsDemo"]["pointsAwarded"] == 79
    for action in ["complete", "redeem", "check_in", "unredeem"]:
        orders.rewards_action(session, action, "DEMO-PLUS")
        assert summary(session)["rewardsDemo"]["balance"] == 329
    with pytest.raises(ValueError, match="Reset"):
        add(session)


def test_insufficient_points_and_future_earnings_cannot_be_spent(session):
    add(session, quantity=10)
    orders.rewards_action(session, "check_in", "DEMO-NEW")
    orders.rewards_action(session, "redeem")
    result = summary(session)
    assert not result["rewardsDemo"]["redeemed"]
    assert result["rewardsDemo"]["balance"] == 0
    assert result["rewardsDemo"]["pointsToEarn"] == 199
    assert result["totalDisplay"] == "$19.90"


def test_removal_releases_pending_reward_without_debit_and_reset_retains_checkin(session):
    add(session)
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.rewards_action(session, "redeem")
    orders.handle_order_update(session, "remove", "Crunchy Taco", "standard", 1, 0)
    result = summary(session)
    assert result["total"] == 0
    assert not result["rewardsDemo"]["redeemed"]
    assert result["rewardsDemo"]["balance"] == 500
    orders.reset_order(session)
    orders.rewards_action(session, "check_in", "DEMO-NEW")
    assert summary(session)["rewardsDemo"]["balance"] == 500
    assert summary(session)["rewardsDemo"]["checkedIn"]


def test_no_award_for_empty_or_unchecked_order_and_modified_item_is_not_reward_eligible(session):
    orders.rewards_action(session, "complete")
    assert not summary(session)["rewardsDemo"]["completed"]
    add(session, "Crunchy Taco (No Lettuce)")
    orders.rewards_action(session, "complete")
    assert not summary(session)["rewardsDemo"]["completed"]
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.rewards_action(session, "redeem")
    assert summary(session)["rewardsDemo"]["discount"] == 0


def test_completed_reset_preserves_balance_but_clears_order_award_and_reservation(session):
    add(session)
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.rewards_action(session, "complete")
    assert summary(session)["rewardsDemo"]["balance"] == 519
    orders.rewards_action(session, "reset")
    result = summary(session)
    assert result["items"] == []
    assert result["rewardsDemo"]["balance"] == 519
    assert not result["rewardsDemo"]["completed"]
    assert result["rewardsDemo"]["pointsAwarded"] == 0
    assert not result["rewardsDemo"]["redeemed"]


def test_sessions_and_other_personas_do_not_share_ledger(session):
    catalog = PersonaCatalog.load()
    other = orders.create_session(catalog.get("tacobell"))
    neutral = orders.create_session(catalog.get(catalog.default_persona_id))
    try:
        orders.rewards_action(session, "check_in", "DEMO-PLUS")
        assert summary(other)["rewardsDemo"]["balance"] == 0
        assert "rewardsDemo" not in summary(neutral)
        with pytest.raises(ValueError, match="not enabled"):
            orders.rewards_action(neutral, "check_in", "DEMO-PLUS")
    finally:
        orders.delete_session(other)
        orders.delete_session(neutral)


def test_status_snapshot_and_rejected_actions_do_not_spend_or_award(session):
    add(session)
    orders.rewards_action(session, "status")
    orders.rewards_action(session, "arbitrary")
    result = summary(session)["rewardsDemo"]
    assert result["balance"] == 0
    assert result["pointsAwarded"] == 0
    assert result["message"] == "Unsupported demo rewards action."


def test_extension_pushes_same_ticket_without_model_tool_followup(session):
    ws = AsyncMock()
    asyncio.run(handle_rewards_frame(ws, session, {"action": "check_in", "code": "DEMO-PLUS"}))
    frame = ws.send_json.call_args.args[0]
    assert frame["type"] == "extension.middle_tier_tool_response"
    assert frame["previous_item_id"] is None
    assert frame["tool_name"] == "get_order"
    assert json.loads(frame["tool_result"]) == summary(session)


def test_pack_brand_menu_and_exact_theme_tokens(session):
    persona = PersonaCatalog.load().get("tacobell")
    assert persona.manifest.ui.theme.light.accents.primaryHex == "#501098"
    assert persona.manifest.ui.theme.font.family == "Arial"
    assert persona.manifest.rewardsDemo.pointsPerDollar == 10
    assert persona.manifest.rewardsDemo.rewardCost == 250
    menu = json.loads(persona.menu_path.read_text(encoding="utf-8"))
    assert set(menu["menuItems"][0]["items"][0]) >= {"name", "sizes"}
    assert Decimal(str(summary(session)["tax"])) == 0


@pytest.mark.parametrize("field,value", [
    ("codes", {"123456": 500}),
    ("codes", {"DEMO-PLUS": -1}),
    ("pointsPerDollar", 0),
    ("rewardCost", 0),
])
def test_mutated_real_pack_rejects_nonfictional_codes_and_invalid_points(tmp_path, field, value):
    source = Path(__file__).resolve().parents[3] / "personas"
    shutil.copytree(source / "tacobell", tmp_path / "tacobell")
    for schema in ["persona.schema.json", "menu.schema.json"]:
        shutil.copyfile(source / schema, tmp_path / schema)
    manifest = tmp_path / "tacobell" / "persona.json"
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["rewardsDemo"][field] = value
    manifest.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(PersonaValidationError, match="rewardsDemo"):
        PersonaCatalog.load(personas_dir=tmp_path, enabled=["tacobell"], default_persona_id="tacobell")
