"""Data-completeness test for Rick's PR #107 review, required item 6: every item size in every
pack has a menu price.

``menu_utils._load_menu_data`` builds two separate views of a pack's own ``menuItems.json`` for
each item: ``"sizes"`` (every size the item is offered in -- used by ``tools.py``'s on-menu/size
gate, #73, to decide whether an ``add`` for that size is even accepted) and ``"prices"`` (that
size's own unit price, keyed the same way -- filtered to ONLY the sizes whose ``price`` field is
actually numeric, ``isinstance(s.get("price"), (int, float))``). These two views can silently
diverge: a size entry with a missing or non-numeric ``price`` field still lands in ``"sizes"``
(so the item/size passes the on-menu gate and the add is accepted), but has no matching key in
``"prices"`` -- so ``order_state.py``'s add branch (docs/persona-architecture.md section 6's #104
contract) falls into its "no menu record for this exact (item, size)" direct-caller fallback,
which was never meant to handle an on-menu item at all (see order_state.py's inline comment on
that ``else`` branch). This test is the guard that catches a pack with that gap BEFORE it ships,
for every persona pack this repo currently loads -- the real, deployed packs under ``personas/``
(today just one) and the two test-only fixture packs used by test_persona_binding.py
(``tests/fixtures/personas/test-alpha``/``test-beta``).
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from menu_utils import get_catalog_for_persona
from persona_loader import PersonaCatalog

_REPO_ROOT = Path(__file__).resolve().parents[3]
_REAL_PERSONAS_DIR = _REPO_ROOT / "personas"
_FIXTURES_PERSONAS_DIR = Path(__file__).resolve().parent / "fixtures" / "personas"


def _catalogs_to_check():
    """Every ``PersonaCatalog`` this test sweeps: the real, deployed persona packs plus the
    test-only fixture packs (each loaded with its own catalog, matching how each is loaded
    elsewhere: test_persona_loader.py for the real one, test_persona_binding.py's
    ``_load_fixture_catalog`` for the fixture pair)."""
    return [
        PersonaCatalog.load(personas_dir=_REAL_PERSONAS_DIR),
        PersonaCatalog.load(personas_dir=_FIXTURES_PERSONAS_DIR, enabled=["test-alpha", "test-beta"]),
    ]


def _missing_price_entries(catalog: PersonaCatalog) -> list[str]:
    """Every ``"<persona_id>: <item name> (<size key>)"`` combination this catalog's menu(s)
    list as an available size (``fields["sizes"]``) but with no matching price
    (``fields["prices"]``) -- see the module docstring for why this gap matters."""
    missing = []
    for persona_id in catalog.ids:
        menu = get_catalog_for_persona(catalog.get(persona_id))
        for fields in menu.item_fields.values():
            for size_key in fields["sizes"]:
                if size_key not in fields["prices"]:
                    missing.append(f"{persona_id}: {fields['name']} ({size_key!r})")
    return missing


class TestEveryMenuSizeHasAPrice:
    def test_every_item_size_in_every_pack_has_a_menu_price(self):
        missing: list[str] = []
        for catalog in _catalogs_to_check():
            missing.extend(_missing_price_entries(catalog))
        assert missing == [], (
            f"\n{len(missing)} item/size combination(s) are listed as available (in "
            "menuItems.json's own \"sizes\" list) but have no matching numeric price, so "
            "MenuCatalog.price_for() returns None for them -- an add would fall through to "
            "order_state.py's direct-caller fallback instead of charging a real menu price:\n"
            + "\n".join(f"  {entry}" for entry in missing)
        )
