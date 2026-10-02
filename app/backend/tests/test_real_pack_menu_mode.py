import asyncio
import json
import sys
from decimal import Decimal
from pathlib import Path

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

from order_state import order_state_singleton
from persona_loader import PersonaCatalog
from rtmt import ToolResultDirection
from tools import update_order

REPO_ROOT = Path(__file__).resolve().parents[3]
PERSONAS_DIR = REPO_ROOT / "personas"
MENU_MODE_DATA_DIR = REPO_ROOT / "tests" / "conformance" / "testdata" / "personas"


def _run(coro):
    return asyncio.run(coro)


def _dayparts_persona_ids() -> list[str]:
    catalog = PersonaCatalog.load(personas_dir=PERSONAS_DIR)
    return [persona_id for persona_id in catalog.ids if catalog.get(persona_id).manifest.features.dayparts]


def _load_vectors(persona_id: str) -> dict:
    path = MENU_MODE_DATA_DIR / persona_id / "menuMode.json"
    assert path.exists(), f"{persona_id} declares dayparts but has no menuMode.json golden vectors"
    return json.loads(path.read_text(encoding="utf-8"))


def _add_args(item: dict) -> dict:
    return {
        "action": "add",
        "item_name": item["name"],
        "size": item["size"],
        "quantity": 1,
        "price": float(Decimal(item["price"])),
    }


def _new_session(persona_id: str, mode: str) -> str:
    persona = PersonaCatalog.load(personas_dir=PERSONAS_DIR, enabled=[persona_id], default_persona_id=persona_id).get(persona_id)
    return order_state_singleton.create_session(persona=persona, menu_mode=mode)


@pytest.mark.parametrize("persona_id", _dayparts_persona_ids())
def test_real_dayparts_pack_accepts_matching_items_and_all_day_items(persona_id: str):
    vectors = _load_vectors(persona_id)
    cases = [
        ("breakfast", vectors["breakfastItem"]),
        ("lunch", vectors["lunchItem"]),
        ("breakfast", vectors["allDayItem"]),
        ("lunch", vectors["allDayItem"]),
    ]
    for mode, item in cases:
        sid = _new_session(persona_id, mode)
        try:
            result = _run(update_order(_add_args(item), sid))
            assert result.destination == ToolResultDirection.TO_BOTH
            assert len(order_state_singleton.get_order_items(sid)) == 1
        finally:
            order_state_singleton.delete_session(sid)


@pytest.mark.parametrize("persona_id", _dayparts_persona_ids())
def test_real_dayparts_pack_rejects_out_of_mode_items(persona_id: str):
    vectors = _load_vectors(persona_id)
    cases = [
        ("lunch", vectors["breakfastItem"]),
        ("breakfast", vectors["lunchItem"]),
    ]
    for mode, item in cases:
        sid = _new_session(persona_id, mode)
        try:
            result = _run(update_order(_add_args(item), sid))
            assert result.destination == ToolResultDirection.TO_SERVER
            assert result.text["reason"] == "item_out_of_mode"
            assert result.text["item_name"] == item["name"]
            assert order_state_singleton.get_order_items(sid) == []
        finally:
            order_state_singleton.delete_session(sid)
