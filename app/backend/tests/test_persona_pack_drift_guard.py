"""Persona pack <-> frontend copy drift guard (Rick's #92 review, issue #71).

The Sonic persona pack (``personas/sonic/menu/menuItems.json`` and ``personas/sonic/assets/**``)
is the single source of truth for menu data and brand assets. The frontend still keeps its own
copies of five asset groups (logo, favicon, the four apology audio clips, and the two demo JSON
files) plus its own ``menuItems.json`` used for local/offline UI rendering. Those frontend copies
are allowed to LAG the pack -- e.g. the frontend's menu JSON may legitimately be a strict subset
of the pack's items/sizes while a new item is being rolled out -- but they must never CONTRADICT
it: every frontend item must exist in the pack with the same sizes, prices, and description, and
the five duplicated asset groups must stay byte-identical to the pack's copies.

This guard is temporary. Per the wave plan, issue #80 (F3/F4/F7) retires the frontend's duplicated
menu copy and asset copies entirely (the frontend will read directly from the persona pack), and
this test file is deleted as part of that work -- it is not meant to be a permanent part of the
suite.
"""

import hashlib
import json
import unittest
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[3]

_FRONTEND_MENU_PATH = _REPO_ROOT / "app" / "frontend" / "src" / "data" / "menuItems.json"
_PACK_MENU_PATH = _REPO_ROOT / "personas" / "sonic" / "menu" / "menuItems.json"

# (frontend copy, pack copy) -- the five asset groups from Rick's #92 review. The apology-clips
# group is four physical files (en/es/fr/ja); every one of the four is checked individually.
_ASSET_PAIRS = (
    (
        _REPO_ROOT / "app" / "frontend" / "src" / "assets" / "sonic-logo.svg",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "logo.svg",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "public" / "favicon.ico",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "favicon.ico",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "public" / "audio" / "apology-en.wav",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "audio" / "apology-en.wav",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "public" / "audio" / "apology-es.wav",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "audio" / "apology-es.wav",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "public" / "audio" / "apology-fr.wav",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "audio" / "apology-fr.wav",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "public" / "audio" / "apology-ja.wav",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "audio" / "apology-ja.wav",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "src" / "data" / "dummyOrder.json",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "demo" / "dummyOrder.json",
    ),
    (
        _REPO_ROOT / "app" / "frontend" / "src" / "data" / "dummyTranscripts.json",
        _REPO_ROOT / "personas" / "sonic" / "assets" / "demo" / "dummyTranscripts.json",
    ),
)


def _flatten_menu_items(path: Path) -> dict[str, dict]:
    """Load a menuItems.json file into ``{name: item}``, ignoring the category grouping."""
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    items: dict[str, dict] = {}
    for category_entry in data.get("menuItems", []):
        for item in category_entry.get("items", []):
            items[item["name"]] = item
    return items


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


# Issue #72 Part 2: Rick's rules confirmed these two items are NOT in the Sonic production export
# (not naming mismatches -- genuinely absent from the source of truth), so ADR-001 decision 4 (no
# off-menu ordering) requires removing them from the persona pack. app/frontend/** is explicitly
# off-limits for Part 2, so the frontend's stale copy still references them; issue #80 (frontend
# migration off its own menuItems.json copy) is the tracked follow-up that will delete this
# exception along with the rest of this temporary guard file. This is a narrow, named allow-list --
# not a loosening of the "frontend must be a subset" invariant for any other item.
_KNOWN_FRONTEND_ONLY_ITEMS_PENDING_ISSUE_80 = {
    "Jr Double Cheeseburger Combo",
    "All-American SONIC Smasher\u2122 Combo",
}


class FrontendMenuNeverContradictsThePackTests(unittest.TestCase):
    """The frontend's menuItems.json may be a subset of the pack's (a lagging copy while a new
    item rolls out), but every item it DOES list must agree with the pack's copy of that item on
    sizes, prices, and description."""

    @classmethod
    def setUpClass(cls):
        cls.frontend_items = _flatten_menu_items(_FRONTEND_MENU_PATH)
        cls.pack_items = _flatten_menu_items(_PACK_MENU_PATH)

    def test_every_frontend_item_exists_in_the_pack(self):
        """The frontend is never allowed to invent an item the pack doesn't know about -- it may
        only lag behind (subset), never diverge (superset or disjoint). Exception: the two items
        in _KNOWN_FRONTEND_ONLY_ITEMS_PENDING_ISSUE_80, confirmed removed from the pack by #72
        Part 2 because they don't exist in the Sonic production export -- see the comment above."""
        missing = sorted(set(self.frontend_items) - set(self.pack_items) - _KNOWN_FRONTEND_ONLY_ITEMS_PENDING_ISSUE_80)
        self.assertEqual(missing, [], f"Frontend items missing from the persona pack: {missing}")

    def test_shared_items_have_identical_sizes_and_prices(self):
        mismatches = []
        for name, frontend_item in self.frontend_items.items():
            pack_item = self.pack_items.get(name)
            if pack_item is None:
                continue  # already reported by test_every_frontend_item_exists_in_the_pack
            frontend_sizes = [(s["size"], s["price"]) for s in frontend_item.get("sizes", [])]
            pack_sizes = [(s["size"], s["price"]) for s in pack_item.get("sizes", [])]
            if frontend_sizes != pack_sizes:
                mismatches.append(f"{name!r}: frontend sizes {frontend_sizes} != pack sizes {pack_sizes}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_shared_items_have_identical_descriptions(self):
        mismatches = []
        for name, frontend_item in self.frontend_items.items():
            pack_item = self.pack_items.get(name)
            if pack_item is None:
                continue  # already reported by test_every_frontend_item_exists_in_the_pack
            if frontend_item.get("description") != pack_item.get("description"):
                mismatches.append(
                    f"{name!r}: frontend description {frontend_item.get('description')!r} "
                    f"!= pack description {pack_item.get('description')!r}"
                )
        self.assertEqual(mismatches, [], "\n".join(mismatches))


class DuplicatedAssetsStayByteIdenticalTests(unittest.TestCase):
    """Rick's #92 review: the frontend's own copies of the logo, favicon, apology audio clips,
    and demo JSON files must stay byte-identical to the persona pack's copies -- there is no
    "lagging but consistent" allowance for binary/opaque assets the way there is for the menu
    JSON's individual fields."""

    def test_all_duplicated_asset_pairs_are_byte_identical(self):
        mismatches = []
        for frontend_path, pack_path in _ASSET_PAIRS:
            if not frontend_path.exists() or not pack_path.exists():
                mismatches.append(f"missing file(s): {frontend_path} / {pack_path}")
                continue
            if _sha256(frontend_path) != _sha256(pack_path):
                mismatches.append(f"{frontend_path.name}: frontend copy != pack copy ({pack_path})")
        self.assertEqual(mismatches, [], "\n".join(mismatches))


if __name__ == "__main__":
    unittest.main()
