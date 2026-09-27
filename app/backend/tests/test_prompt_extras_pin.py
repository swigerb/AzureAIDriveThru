"""Prompt/menu extras pin (PR #98, Rick's review item 4).

Rick's required fix #4: the system prompt's "Extras:" line must list EXACTLY the persona pack's
``isExtra`` menu items, at their menu prices -- no more, no fewer, and no independently-invented
prompt price (decision 4: anything not in the menu source data cannot be ordered/priced). This
test reads both sides directly from disk -- ``personas/sonic/menu/menuItems.json`` and
``personas/sonic/prompts/system_prompt.yaml`` -- with no shared helper code between them, so it
can't pass by construction.

Mutation check (see decision note for the demonstrated/reverted evidence): changing either side
independently -- a menu ``isExtra`` item's name or price, or the prompt's "Extras:" wording --
without updating the other must fail one of the two tests below.
"""

import json
import re
import unittest
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[3]
_MENU_ITEMS_PATH = _REPO_ROOT / "personas" / "sonic" / "menu" / "menuItems.json"
_SYSTEM_PROMPT_PATH = _REPO_ROOT / "personas" / "sonic" / "prompts" / "system_prompt.yaml"

_EXTRAS_LINE_RE = re.compile(r"^\s*-\s*Extras:\s*(.+?)\s*$", re.MULTILINE)
_EXTRAS_CHUNK_RE = re.compile(r"^(.+?)\s+\$(\d+(?:\.\d+)?)$")


def _load_menu_isextra_items() -> dict[str, float]:
    """Read menuItems.json directly (no ``menu_utils`` involved) -- every ``isExtra: true``
    item's name -> its single "Standard" size price."""
    with _MENU_ITEMS_PATH.open("r", encoding="utf-8") as f:
        data = json.load(f)
    items: dict[str, float] = {}
    for category in data["menuItems"]:
        for item in category["items"]:
            if not item.get("isExtra"):
                continue
            sizes = item.get("sizes") or []
            if len(sizes) != 1:
                raise AssertionError(f"isExtra item {item['name']!r} must have exactly one size, got {sizes!r}")
            items[item["name"]] = sizes[0]["price"]
    return items


def _load_prompt_extras_line() -> str:
    """Read system_prompt.yaml as raw text (no ``prompt_loader``/Jinja2 involved) and return the
    text after "Extras:" on its bullet line."""
    raw = _SYSTEM_PROMPT_PATH.read_text(encoding="utf-8")
    match = _EXTRAS_LINE_RE.search(raw)
    if not match:
        raise AssertionError("system_prompt.yaml must have an 'Extras:' bullet line")
    return match.group(1)


def _parse_prompt_extras(line: str) -> dict[str, float]:
    """Parse a comma-separated "name $price" list into ``{name.lower(): price}``."""
    parsed: dict[str, float] = {}
    for chunk in line.split(","):
        chunk = chunk.strip()
        if not chunk:
            continue
        match = _EXTRAS_CHUNK_RE.match(chunk)
        if not match:
            raise AssertionError(f"could not parse prompt extras chunk: {chunk!r}")
        name, price = match.group(1).strip().lower(), float(match.group(2))
        parsed[name] = price
    return parsed


class PromptExtrasPinnedToMenuIsExtraItemsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.menu_extras = _load_menu_isextra_items()
        cls.prompt_extras = _parse_prompt_extras(_load_prompt_extras_line())

    def test_prompt_extras_names_match_menu_isextra_item_names_exactly(self):
        """Same set of names, case-insensitively -- no menu isExtra item is missing from the
        prompt, and the prompt never lists something that isn't a real, priced menu item
        (decision 4)."""
        menu_names_lower = {name.lower() for name in self.menu_extras}
        prompt_names_lower = set(self.prompt_extras)
        self.assertEqual(prompt_names_lower, menu_names_lower)

    def test_prompt_extras_prices_match_menu_isextra_item_prices_exactly(self):
        """Every prompt price is the exact menu price -- not a stale, rounded, or independently
        chosen number."""
        menu_by_lower = {name.lower(): price for name, price in self.menu_extras.items()}
        for name, price in self.prompt_extras.items():
            self.assertIn(name, menu_by_lower)
            self.assertEqual(price, menu_by_lower[name], name)

    def test_there_are_exactly_five_priced_extras_today(self):
        """Sanity/mutation guard: pins the current count (flavor add-in, add bacon, whipped
        topping, plus #72 Part 2's sweet cream and jalapeños) so silently adding or dropping an
        isExtra item on either side without updating the other is caught even if names/prices
        happen to still line up in every other pair."""
        self.assertEqual(len(self.menu_extras), 5, self.menu_extras)
        self.assertEqual(len(self.prompt_extras), 5, self.prompt_extras)


if __name__ == "__main__":
    unittest.main()
