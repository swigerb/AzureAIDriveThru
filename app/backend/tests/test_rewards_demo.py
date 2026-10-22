import asyncio
import json
import re
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


def test_customized_meal_late_sweet_reward_and_exact_totals(session):
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.handle_order_update(session, "add", "3 Crunchy Tacos Combo", "standard", 1, 999)
    orders.handle_order_update(session, "remove", "3 Crunchy Tacos Combo", "standard", 1, 0)
    orders.handle_order_update(session, "add", "3 Soft Tacos Combo (No Tomatoes)", "standard", 1, 999)
    add(session, "Chips and Nacho Cheese Sauce")
    orders.handle_order_update(session, "add", "Mountain Dew Baja Blast", "large", 1, 999)
    first = summary(session)
    assert len(first["items"]) == 2
    meal = first["items"][0]
    assert meal["item"] == "3 Soft Tacos Combo (No Tomatoes)"
    assert meal["size"] == "standard"
    assert meal["price"] == 9.49
    assert meal["components"] == ["Large Mountain Dew Baja Blast"]
    assert first["finalTotalDisplay"] == "$12.94"
    assert not first["rewardsDemo"]["completed"]
    add(session, "Cinnamon Twists")
    assert summary(session)["finalTotalDisplay"] == "$14.55"
    assert summary(session)["rewardsDemo"]["rewardItem"] == "Cinnamon Twists"
    orders.rewards_action(session, "redeem")
    rewarded = summary(session)
    assert rewarded["rewardsDemo"]["discount"] == 1.49
    assert rewarded["totalDisplay"] == "$11.98"
    assert rewarded["taxDisplay"] == "$0.96"
    assert rewarded["finalTotalDisplay"] == "$12.94"
    orders.rewards_action(session, "complete")
    orders.rewards_action(session, "complete")
    assert summary(session)["rewardsDemo"]["balance"] == 369
    assert summary(session)["rewardsDemo"]["pointsAwarded"] == 119


@pytest.mark.parametrize("drink_before_omission", [False, True])
@pytest.mark.parametrize("quantity", [1, 2])
def test_real_tools_replace_omissions_instead_of_renaming_with_modify(session, drink_before_omission, quantity):
    import tools

    def update(action, item, size="Standard"):
        return asyncio.run(tools.update_order({
            "action": action, "item_name": item, "size": size, "quantity": quantity,
        }, session))

    update("add", "3 Crunchy Tacos Combo")
    update("remove", "3 Crunchy Tacos Combo")
    update("add", "3 Soft Tacos Combo")
    if drink_before_omission:
        update("add", "Mountain Dew Baja Blast", "Large")
    rejected = update("modify", "3 Soft Tacos Combo (No Tomatoes)")
    assert rejected.text["reason"] == "not_in_order"
    assert all(meal["item"] == "3 Soft Tacos Combo" for meal in summary(session)["items"])
    assert sum(meal["quantity"] for meal in summary(session)["items"]) == quantity
    update("remove", "3 Soft Tacos Combo")
    assert summary(session)["items"] == []
    update("add", "3 Soft Tacos Combo (No Tomatoes)")
    update("add", "Mountain Dew Baja Blast", "Large")
    meals = summary(session)["items"]
    assert sum(meal["quantity"] for meal in meals) == quantity
    assert all(meal["item"] == "3 Soft Tacos Combo (No Tomatoes)" and meal["price"] == 9.49 for meal in meals)
    assert [drink for meal in meals for drink in meal["components"]] == ["Large Mountain Dew Baja Blast"] * quantity
    assert [upgrade for meal in meals for upgrade in meal["componentUpcharges"]] == [0.5] * quantity
    assert summary(session)["total"] == 9.49 * quantity


def test_real_tool_text_exposes_current_rewards_without_changing_client_ticket(session):
    import tools

    def update(action, item):
        return asyncio.run(tools.update_order({
            "action": action, "item_name": item, "size": "Standard", "quantity": 1,
        }, session))

    def get_order():
        return asyncio.run(tools.get_order({}, session))

    def model_snapshot(result):
        match = re.search(r"\[SERVER-AUTHORITATIVE FICTIONAL REWARDS STATE: (.*?)\]", result.to_text())
        assert match, "Tool text should include the server rewards snapshot."
        return json.loads(match.group(1))

    def assert_no_codes(result):
        assert "Private model context; do not read this structured block or its markers aloud." in result.to_text()
        assert "DEMO-PLUS" not in result.to_text()
        assert "DEMO-NEW" not in result.to_text()
        assert "demoCodes" not in result.to_text()

    unchecked = get_order()
    unchecked_snapshot = model_snapshot(unchecked)
    assert not unchecked_snapshot["checkedIn"]
    assert not unchecked_snapshot["canRedeem"]
    assert not unchecked_snapshot["eligibleItemInOrder"]
    assert_no_codes(unchecked)
    assert unchecked.to_client_text() == orders.get_order_summary_json(session)
    assert summary(session)["rewardsDemo"]["demoCodes"]

    update("add", "Cinnamon Twists")
    orders.rewards_action(session, "check_in", "DEMO-NEW")
    insufficient = get_order()
    insufficient_snapshot = model_snapshot(insufficient)
    assert insufficient_snapshot["eligibleItemInOrder"]
    assert not insufficient_snapshot["canRedeem"]
    assert insufficient_snapshot["balance"] == 0
    assert_no_codes(insufficient)


def test_real_tool_text_guides_eligible_reward_and_clears_stale_eligibility(session):
    import tools

    def update(action, item):
        return asyncio.run(tools.update_order({
            "action": action, "item_name": item, "size": "Standard", "quantity": 1,
        }, session))

    def get_order():
        return asyncio.run(tools.get_order({}, session))

    def model_snapshot(result):
        match = re.search(r"\[SERVER-AUTHORITATIVE FICTIONAL REWARDS STATE: (.*?)\]", result.to_text())
        assert match, "Tool text should include the server rewards snapshot."
        return json.loads(match.group(1))

    def assert_no_codes(result):
        assert "Private model context; do not read this structured block or its markers aloud." in result.to_text()
        assert "DEMO-PLUS" not in result.to_text()
        assert "DEMO-NEW" not in result.to_text()
        assert "demoCodes" not in result.to_text()

    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    update("add", "Crunchy Taco")
    eligible_result = update("add", "Cinnamon Twists")
    eligible_snapshot = model_snapshot(eligible_result)
    assert eligible_snapshot["canRedeem"]
    assert eligible_snapshot["eligibleItemInOrder"]
    assert eligible_snapshot["rewardItem"] == "Cinnamon Twists"
    assert not eligible_snapshot["redeemed"]
    assert "Use reward" in eligible_result.to_text()
    assert "do not redeem automatically" in eligible_result.to_text()
    assert eligible_result.to_client_text() == orders.get_order_summary_json(session)
    assert summary(session)["rewardsDemo"]["demoCodes"]
    assert_no_codes(eligible_result)

    orders.rewards_action(session, "redeem")
    reserved = get_order()
    reserved_snapshot = model_snapshot(reserved)
    assert reserved_snapshot["redeemed"]
    assert reserved_snapshot["discount"] == 1.49
    assert reserved_snapshot["postDiscountTotal"] == summary(session)["totalDisplay"]
    assert_no_codes(reserved)

    removed = update("remove", "Cinnamon Twists")
    removed_snapshot = model_snapshot(removed)
    assert removed_snapshot["eligibleItemInOrder"]
    assert removed_snapshot["canRedeem"]
    assert removed_snapshot["rewardItem"] == "Crunchy Taco"
    assert not removed_snapshot["redeemed"]
    assert removed_snapshot["discount"] == 0
    assert removed_snapshot["balance"] == 500
    assert_no_codes(removed)

    orders.reset_order(session)
    reset_result = get_order()
    reset_snapshot = model_snapshot(reset_result)
    assert not reset_snapshot["eligibleItemInOrder"]
    assert not reset_snapshot["redeemed"]
    assert reset_snapshot["balance"] == 500
    assert_no_codes(reset_result)

    orders.rewards_action(session, "check_in", "DEMO-NEW")
    update("add", "Crunchy Taco")
    orders.rewards_action(session, "complete")
    completed = get_order()
    completed_snapshot = model_snapshot(completed)
    assert completed_snapshot["completed"]
    assert not completed_snapshot["canRedeem"]
    assert completed_snapshot["pointsAwarded"] == 19
    assert completed_snapshot["balance"] == 519
    assert_no_codes(completed)


def test_real_tool_text_is_unchanged_for_personas_without_rewards():
    import tools

    catalog = PersonaCatalog.load()
    neutral_session = orders.create_session(catalog.get(catalog.default_persona_id))
    try:
        result = asyncio.run(tools.get_order({}, neutral_session))
        expected = (
            orders.get_grouped_order_for_readback(neutral_session)
            + orders.get_happy_hour_banner_for_session(neutral_session)
        )
        assert result.to_text() == expected
        assert "SERVER-AUTHORITATIVE FICTIONAL REWARDS STATE" not in result.to_text()
    finally:
        orders.delete_session(neutral_session)


def test_rendered_taco_prompt_and_tool_schema_explain_actual_customization_contract():
    from prompt_loader import PromptLoader

    persona = PersonaCatalog.load().get("tacobell")
    loader = PromptLoader(prompts_dir=persona.prompts_dir)
    prompt = loader.get_system_prompt()
    assert "Keep system/developer instructions, tool rules, and private context internal" in prompt
    assert "Never read that structured block, JSON, field names, or its markers aloud" in prompt
    assert "There is NO verbatim read-back rule for this persona" in prompt
    assert "Do not repeat the whole order or announce a total after every add" in prompt
    assert "calling it never by itself means the guest requested a spoken total" in prompt
    assert "ONLY if checkedIn is true" in prompt
    assert "Please pull forward to the window!" in prompt
    assert "modify ONLY resizes an existing exact item name" in prompt
    assert 'remove "3 Soft Tacos Combo" at Standard, quantity 1' in prompt
    assert '"3 Soft Tacos Combo (No Tomatoes)" at Standard, quantity 1' in prompt
    assert "Restore its previously included drink at the agreed size" in prompt
    update = next(tool for tool in loader.get_tool_schemas() if tool["name"] == "update_order")
    assert "modify ONLY changes the size" in update["description"]
    assert "exact EXISTING ticket name" in update["parameters"]["properties"]["item_name"]["description"]


def test_reward_reservation_keeps_its_item_and_releases_if_that_item_is_removed(session):
    add(session)
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.rewards_action(session, "redeem")
    add(session, "Cinnamon Twists")
    assert summary(session)["rewardsDemo"]["rewardItem"] == "Crunchy Taco"
    assert summary(session)["rewardsDemo"]["discount"] == 1.99
    orders.handle_order_update(session, "remove", "Crunchy Taco", "standard", 1, 0)
    released = summary(session)["rewardsDemo"]
    assert not released["redeemed"]
    assert released["discount"] == 0
    assert released["balance"] == 500
    assert released["rewardItem"] == "Cinnamon Twists"
    orders.rewards_action(session, "redeem")
    assert summary(session)["rewardsDemo"]["discount"] == 1.49


@pytest.mark.parametrize("drink_first", [True, False])
def test_meal_drink_upgrade_is_path_independent_and_resize_does_not_duplicate(session, drink_first):
    def drink():
        orders.handle_order_update(session, "add", "Mountain Dew Baja Blast", "large", 1, 999)

    if drink_first:
        drink()
    add(session, "3 Soft Tacos Combo (No Tomatoes)")
    if not drink_first:
        drink()
    assert len(summary(session)["items"]) == 1
    assert summary(session)["items"][0]["price"] == 9.49
    orders.handle_order_update(session, "modify", "Mountain Dew Baja Blast", "medium", 1, 999)
    assert summary(session)["items"][0]["price"] == 8.99
    orders.handle_order_update(session, "modify", "Mountain Dew Baja Blast", "large", 1, 999)
    meal = summary(session)["items"][0]
    assert meal["price"] == 9.49
    assert meal["components"] == ["Large Mountain Dew Baja Blast"]
    assert meal["componentUpcharges"] == [0.5]


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
    ("preferredRewardItems", [""]),
    ("preferredRewardItems", ["Cinnamon Twists", "Cinnamon Twists"]),
    ("preferredRewardItems", [123]),
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


@pytest.mark.parametrize("style", [None, "fullReadback", "naturalDriveThru", "unknown", 7])
def test_real_pack_speech_style_validation_and_actual_tool_boundary(tmp_path, style):
    import tools

    source = Path(__file__).resolve().parents[3] / "personas"
    # Catalogs are cached by immutable persona ID. Give each valid pack mutation its own identity.
    pack_id = "voice-fixture-" + str(style or "absent").lower()
    shutil.copytree(source / "tacobell", tmp_path / pack_id)
    for schema in ["persona.schema.json", "menu.schema.json"]:
        shutil.copyfile(source / schema, tmp_path / schema)
    path = tmp_path / pack_id / "persona.json"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    manifest["id"] = pack_id
    manifest.pop("speechStyle")
    if style is not None:
        manifest["speechStyle"] = style
    path.write_text(json.dumps(manifest), encoding="utf-8")
    if style in ("unknown", 7):
        with pytest.raises(PersonaValidationError, match="speechStyle"):
            PersonaCatalog.load(personas_dir=tmp_path, enabled=[pack_id], default_persona_id=pack_id)
        return
    persona = PersonaCatalog.load(personas_dir=tmp_path, enabled=[pack_id], default_persona_id=pack_id).get(pack_id)
    assert persona.manifest.speechStyle == (style or "fullReadback")
    sid = orders.create_session(persona)
    try:
        changed = asyncio.run(tools.update_order({
            "action": "add", "item_name": "Crunchy Taco", "size": "Standard", "quantity": 1,
        }, sid))
        assert changed.to_client_text() == orders.get_order_summary_json(sid)
        if style == "naturalDriveThru":
            assert "PRIVATE ORDER CONTEXT" in changed.to_text()
            assert "total" not in changed.to_text().split("PRIVATE ORDER CONTEXT")[0].lower()
            assert "The preceding order read-back is the guest-facing text." not in changed.to_text()
        else:
            assert "PRIVATE ORDER CONTEXT" not in changed.to_text()
            assert orders.get_grouped_order_for_readback(sid) in changed.to_text()
            assert "Your total is" in changed.to_text()
    finally:
        orders.delete_session(sid)


def test_natural_turns_keep_totals_private_sauces_saved_and_rewards_authoritative(session):
    import tools
    from prompt_loader import PromptLoader

    def run(name, args=None):
        return asyncio.run(getattr(tools, name)(args or {}, session))

    initial = run("get_order")
    assert '"checkedIn": false' in initial.to_text()
    assert "only when checkedIn is true" in initial.to_text()
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    checked_in = run("get_order")
    assert '"checkedIn": true' in checked_in.to_text()
    assert '"balance": 500' in checked_in.to_text()

    turns = [
        ("add", "3 Crunchy Tacos Combo", "Standard", 1),
        ("remove", "3 Crunchy Tacos Combo", "Standard", 1),
        ("add", "3 Soft Tacos Combo (No Tomatoes)", "Standard", 1),
        ("add", "Chips and Nacho Cheese Sauce", "Standard", 1),
        ("add", "Mountain Dew Baja Blast", "Large", 1),
        ("add", "Fire Sauce Packet", "Standard", 2),
    ]
    for action, item, size, quantity in turns:
        result = run("update_order", dict(action=action, item_name=item, size=size, quantity=quantity))
        text = result.to_text()
        acknowledgement = text.split("PRIVATE ORDER CONTEXT")[0].strip()
        assert not re.search(r"demo|server|ticket|tool|read.?back|total|\$\d", acknowledgement, re.I), acknowledgement
        assert "Do not announce totals or the full order after routine changes" in text
        assert result.to_client_text() == orders.get_order_summary_json(session)
        assert "DEMO-PLUS" not in text
    current = summary(session)
    sauce = next(i for i in current["items"] if i["item"] == "Fire Sauce Packet")
    assert sauce["quantity"] == 2 and sauce["price"] == 0
    assert current["finalTotalDisplay"] == "$12.94"
    assert not current["rewardsDemo"]["canRedeem"]
    final = run("get_order")
    assert final.to_text().startswith("\n\nPRIVATE ORDER CONTEXT:")
    assert '"finalTotalDisplay": "$12.94"' in final.to_text()
    run("update_order", dict(action="add", item_name="Cinnamon Twists", size="Standard", quantity=1))
    assert summary(session)["finalTotalDisplay"] == "$14.55"
    orders.rewards_action(session, "redeem")
    discounted = run("get_order")
    assert '"redeemed": true' in discounted.to_text()
    assert summary(session)["finalTotalDisplay"] == "$12.94"
    assert summary(session)["rewardsDemo"]["pointsToEarn"] == 119
    orders.rewards_action(session, "complete")
    assert summary(session)["rewardsDemo"]["balance"] == 369
    cleared = run("reset_order")
    assert cleared.to_text().startswith("Order cleared.")
    assert "demoCodes" not in cleared.to_text()
    assert summary(session)["items"] == []
    loader = PromptLoader(prompts_dir=PersonaCatalog.load().get("tacobell").prompts_dir)
    greeting = loader.get_greeting()
    assert "Welcome to Taco Bell! Are you checking in with the Taco Bell app today?" in str(greeting)


@pytest.mark.parametrize("item", ["Mild Sauce Packet", "Hot Sauce Packet", "Fire Sauce Packet", "Diablo Sauce Packet"])
def test_zero_price_sauces_cannot_earn_or_redeem_points(session, item):
    add(session, item, quantity=2)
    orders.rewards_action(session, "check_in", "DEMO-PLUS")
    orders.rewards_action(session, "redeem")
    current = summary(session)
    assert current["items"][0]["quantity"] == 2
    assert current["total"] == 0
    assert not current["rewardsDemo"]["canRedeem"]
    assert current["rewardsDemo"]["discount"] == 0
    assert current["rewardsDemo"]["pointsToEarn"] == 0


def test_natural_acknowledges_only_applied_quantities_and_limit_rejections_do_not_mutate(session):
    import tools
    from rtmt import ToolResultDirection

    def update(action, item, quantity, size="Standard"):
        return asyncio.run(tools.update_order(
            dict(action=action, item_name=item, size=size, quantity=quantity), session))

    update("add", "Fire Sauce Packet", 2)
    removed = update("remove", "Fire Sauce Packet", 8)
    assert removed.to_text().startswith("Removed two Fire Sauce Packet.")
    assert summary(session)["items"] == []
    missing = update("remove", "Fire Sauce Packet", 8)
    assert missing.to_text().startswith("No Fire Sauce Packet were removed; none matched your order.")
    update("add", "3 Soft Tacos Combo", 1)
    update("add", "Mountain Dew Baja Blast", 1, "Large")
    component = update("remove", "Mountain Dew Baja Blast", 8, "Large")
    assert component.to_text().startswith("Removed one Large Mountain Dew Baja Blast.")
    assert not summary(session)["items"][0].get("components")
    update("add", "Fire Sauce Packet", 9)
    before = summary(session)
    limited = update("add", "Fire Sauce Packet", 2)
    assert limited.destination == ToolResultDirection.TO_SERVER
    assert "Confirmed" not in limited.to_text()
    assert summary(session) == before
    update("add", "Hot Sauce Packet", 9)
    update("add", "Mild Sauce Packet", 5)
    before = summary(session)
    limited = update("add", "Diablo Sauce Packet", 2)
    assert limited.destination == ToolResultDirection.TO_SERVER
    assert "Confirmed" not in limited.to_text()
    assert summary(session) == before


def test_real_conformance_expectations_match_current_natural_voice_pack():
    from prompt_loader import PromptLoader

    root = Path(__file__).resolve().parents[3]
    persona = PersonaCatalog.load().get("tacobell")
    loader = PromptLoader(prompts_dir=persona.prompts_dir)
    expected = json.loads((root / "tests" / "conformance" / "testdata" / "personas" / "tacobell" / "smoke.json")
                          .read_text(encoding="utf-8"))
    assert expected["greetingSubstring"] in loader.get_greeting()["item"]["content"][0]["text"]
    assert expected["instructionsSubstring"] in loader.get_system_prompt()
    search = next(t for t in loader.get_tool_schemas() if t["name"] == "search")
    assert expected["toolDescriptionSubstring"] in search["description"]
