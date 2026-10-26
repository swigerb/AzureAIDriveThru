"""Real-pack regression for an included slush swap, not an extra drink."""
import asyncio
import json
import sys
from decimal import Decimal
from pathlib import Path

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import get_catalog_for_persona
from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from prompt_loader import PromptLoader
from rtmt import ToolResultDirection
from tools import get_order, update_order

PERSONAS = Path(__file__).resolve().parents[3] / "personas"
BUNDLE = "SuperSONIC® Double Cheeseburger Combo (Pickles)"
OLD = "Rainbow Slush"
NEW = "Medium Cherry Flavor Bubble Slush"
CATALOG = PersonaCatalog.load(personas_dir=PERSONAS)
PACK_IDS = CATALOG.ids
SLUSH_PERSONA = next(CATALOG.get(p) for p in PACK_IDS if get_catalog_for_persona(CATALOG.get(p)).resolve_menu_item(OLD))
BUNDLE_PACK_IDS = [
    p for p in PACK_IDS
    if any(i.get("bundle") for c in json.loads(CATALOG.get(p).menu_path.read_text(encoding="utf-8"))["menuItems"] for i in c["items"]) and
    sum(i.get("comboSlot") == "drinks" for c in json.loads(CATALOG.get(p).menu_path.read_text(encoding="utf-8"))["menuItems"] for i in c["items"]) > 1
]


@pytest.fixture
def session():
    sid = order_state_singleton.create_session(persona=SLUSH_PERSONA, menu_mode="lunch")
    try:
        for item, size in [(BUNDLE, "standard"), ("Tots", "medium"), (OLD, "standard")]:
            result = call(sid, "add", item, size)
            assert result.destination == ToolResultDirection.TO_BOTH, result.to_text()
        yield sid
    finally:
        order_state_singleton.delete_session(sid)


def call(sid, action, item, size="medium", quantity=1, **kwargs):
    return asyncio.run(update_order(dict(action=action, item_name=item, size=size, quantity=quantity, **kwargs), sid))


@pytest.mark.parametrize("replacement,size,upcharge", [(NEW, "standard", "0"), ("Cherry Slush", "large", "0.60")])
def test_replace_included_slush_is_atomic_and_uses_menu_upcharge(session, replacement, size, upcharge):
    result = call(session, "replace", replacement, size, original_item_name=OLD)
    assert result.destination == ToolResultDirection.TO_BOTH
    ticket = json.loads(result.to_client_text())
    assert len(ticket["items"]) == 1
    combo = ticket["items"][0]
    assert combo["item"] == BUNDLE
    assert "Tots" in combo["display"]
    assert replacement in combo["display"]
    assert OLD not in combo["display"]
    assert Decimal(str(ticket["total"])) == Decimal("10.19") + Decimal(upcharge)
    assert "Replaced" in result.to_text()
    assert OLD not in ticket["spokenReadBack"]
    assert replacement in ticket["spokenReadBack"]
    assert order_state_singleton.get_combo_requirements(session)["is_complete"]
    current = asyncio.run(get_order({}, session))
    assert json.loads(current.to_client_text()) == ticket


@pytest.mark.parametrize("item,size,original,reason", [
    (NEW, "standard", "", "invalid_replacement"),
    (NEW, "standard", "Cherry Limeade", "replacement_target_not_found"),
    ("Tots", "medium", OLD, "incompatible_replacement"),
    ("Invented Slush", "medium", OLD, "not_on_menu"),
    (NEW, "impossible", OLD, "size_not_available"),
    ("Vanilla Classic Shake", "medium", OLD, "machine_unavailable"),
])
def test_failed_replacement_preserves_entire_ticket(session, item, size, original, reason):
    before = order_state_singleton.get_order_summary_json(session)
    result = call(session, "replace", item, size, original_item_name=original)
    assert result.destination == ToolResultDirection.TO_SERVER
    assert json.loads(result.to_text())["reason"] == reason
    assert order_state_singleton.get_order_summary_json(session) == before


def test_add_different_drink_still_means_an_extra(session):
    result = call(session, "add", NEW, "standard")
    ticket = json.loads(result.to_client_text())
    assert len(ticket["items"]) == 2
    assert OLD in ticket["items"][0]["display"]
    assert ticket["items"][1]["item"] == NEW


@pytest.mark.parametrize("persona_id", [p for p in PACK_IDS if CATALOG.get(p).manifest.speechStyle != "naturalDriveThru"])
def test_finish_contract_overrides_open_order_questions(persona_id):
    loader = PromptLoader(brand=persona_id, prompts_dir=PERSONAS / persona_id / "prompts")
    prompt = loader.get_system_prompt()
    assert "FINISH INTENT OVERRIDES" in prompt
    assert "Nope, that'll be it" in prompt
    assert "Do NOT ask 'Anything else?'" in prompt
    assert "If the guest repeats that they are done and the order has not changed" in prompt
    assert "do not repeat the read-back or call get_order again" in prompt
    assert "ALWAYS state the total in the closing phrase" not in prompt
    assert "- Call get_order and read back items with TOTAL only" not in prompt
    assert "NEVER say 'All set'" not in prompt
    assert "Every closing statement MUST" not in prompt


@pytest.mark.parametrize("persona_id", BUNDLE_PACK_IDS)
def test_loaded_schema_exposes_targeted_atomic_component_replacement(persona_id):
    loader = PromptLoader(brand=persona_id, prompts_dir=PERSONAS / persona_id / "prompts")
    schema = next(s for s in loader.get_tool_schemas() if s["name"] == "update_order")
    properties = schema["parameters"]["properties"]
    assert "replace" in properties["action"]["enum"]
    assert "original_item_name" in properties
    assert properties["combo_index"]["minimum"] == 0
    assert schema["parameters"]["additionalProperties"] is False
    assert "original_item_name" in loader.get_system_prompt()


def test_ambiguous_combo_lines_require_explicit_selection(session):
    call(session, "add", BUNDLE + " (No Onion)", "standard")
    call(session, "add", "Tots")
    call(session, "add", OLD, "standard")
    before = order_state_singleton.get_order_summary_json(session)
    rejected = call(session, "replace", NEW, "standard", original_item_name=OLD)
    assert json.loads(rejected.to_text())["reason"] == "ambiguous_replacement"
    assert order_state_singleton.get_order_summary_json(session) == before
    selected = call(session, "replace", NEW, "standard", original_item_name=OLD, combo_index=1)
    assert selected.destination == ToolResultDirection.TO_BOTH
    items = json.loads(selected.to_client_text())["items"]
    assert OLD in items[0]["display"]
    assert NEW in items[1]["display"]


@pytest.mark.parametrize("selector", [-1, 999, "0", True])
def test_invalid_combo_selector_never_falls_back_to_another_combo(session, selector):
    before = order_state_singleton.get_order_summary_json(session)
    result = call(session, "replace", NEW, "standard", original_item_name=OLD, combo_index=selector)
    assert result.destination == ToolResultDirection.TO_SERVER
    assert order_state_singleton.get_order_summary_json(session) == before


@pytest.mark.parametrize("quantity", [0, 2, -1])
def test_replacement_requires_exactly_one_component(session, quantity):
    before = order_state_singleton.get_order_summary_json(session)
    result = call(session, "replace", NEW, "standard", quantity=quantity, original_item_name=OLD)
    assert json.loads(result.to_text())["reason"] == "invalid_replacement"
    assert order_state_singleton.get_order_summary_json(session) == before


def test_side_replacement_preserves_included_drink_and_charges_only_real_size_delta(session):
    result = call(session, "replace", "Groovy Fries", "large", original_item_name="Tots")
    ticket = json.loads(result.to_client_text())
    combo = ticket["items"][0]
    assert len(ticket["items"]) == 1
    assert "Large Groovy Fries" in combo["display"]
    assert "Tots" not in combo["display"]
    assert OLD in combo["display"]
    assert combo["price"] == 10.89
    assert combo["componentUpcharges"] == [0.70, 0]


def test_quantity_two_replacement_changes_one_unit_not_standalone_same_drink(session):
    call(session, "add", BUNDLE, "standard")
    call(session, "add", "Tots")
    call(session, "add", OLD, "standard")
    call(session, "add", OLD, "standard")
    before = json.loads(order_state_singleton.get_order_summary_json(session))["items"]
    assert len(before) == 2
    assert before[0]["item"] == BUNDLE and before[0]["quantity"] == 2
    assert before[1]["item"] == OLD and before[1]["quantity"] == 1
    result = call(session, "replace", "Cherry Slush", "large", original_item_name=OLD)
    items = json.loads(result.to_client_text())["items"]
    assert len(items) == 3
    assert sum(i["quantity"] for i in items) == 3
    assert any(i["item"] == OLD and i["quantity"] == 1 for i in items)
    assert any(i["item"] == BUNDLE and OLD in i["display"] and i["price"] == 10.19 for i in items)
    assert any(i["item"] == BUNDLE and "Cherry Slush" in i["display"] and i["price"] == 10.79 for i in items)
    assert not any(i["item"] == "Cherry Slush" for i in items)


def test_replacement_reprices_down_and_keeps_standalone_same_drink(session):
    call(session, "add", OLD, "standard")
    call(session, "replace", "Cherry Slush", "large", original_item_name=OLD)
    result = call(session, "replace", NEW, "standard", original_item_name="Cherry Slush")
    ticket = json.loads(result.to_client_text())
    assert len(ticket["items"]) == 2
    assert NEW in ticket["items"][0]["display"]
    assert ticket["items"][0]["price"] == 10.19
    assert ticket["items"][1]["item"] == OLD


@pytest.mark.parametrize("persona_id", BUNDLE_PACK_IDS)
def test_real_bundle_packs_replace_drinks_at_their_own_included_price(persona_id):
    persona = CATALOG.get(persona_id)
    menu = get_catalog_for_persona(persona)
    items = [i for c in json.loads(persona.menu_path.read_text(encoding="utf-8"))["menuItems"] for i in c["items"]]
    bundle = next(i for i in items if "drinks" in i.get("bundle", {}).get("slots", []) and menu.item_available_now(i["name"], "lunch"))
    drinks = [
        i for i in items if i.get("comboSlot") == "drinks" and not menu.is_extra_item(i["name"]) and
        menu.item_available_now(i["name"], "lunch") and menu.machine_status(menu.requires_machine(i["name"])) != "down"
    ][:2]
    assert len(drinks) == 2
    old, new = drinks
    sid = order_state_singleton.create_session(persona=persona, menu_mode="lunch")
    try:
        assert call(sid, "add", bundle["name"], bundle["sizes"][0]["size"]).destination == ToolResultDirection.TO_BOTH
        assert call(sid, "add", old["name"], old["sizes"][0]["size"]).destination == ToolResultDirection.TO_BOTH
        result = call(sid, "replace", new["name"], new["sizes"][0]["size"], original_item_name=old["name"])
        assert result.destination == ToolResultDirection.TO_BOTH
        ticket = json.loads(result.to_client_text())
        assert len(ticket["items"]) == 1
        assert new["name"] in ticket["items"][0]["display"]
        assert old["name"] not in ticket["items"][0]["display"]
        assert ticket["items"][0]["price"] == bundle["sizes"][0]["price"]
    finally:
        order_state_singleton.delete_session(sid)
