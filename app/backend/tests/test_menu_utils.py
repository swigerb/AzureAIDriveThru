import json
import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1]))

import menu_utils
from default_persona import get_default_persona
from menu_utils import strip_modifiers

_REPO_ROOT = Path(__file__).resolve().parents[3]
_GOLDEN_CATEGORIES_PATH = _REPO_ROOT / "tests" / "conformance" / "testdata" / "golden-menu-categories.json"
_MENU_ITEMS_PATH = _REPO_ROOT / "personas" / "sonic" / "menu" / "menuItems.json"

# #74 (Rick's PR #102 review, item 2): the module-level brand-specific globals (SIZE_MAP,
# MENU_CATEGORY_MAP, etc.) are gone from menu_utils -- every classification below now goes
# through a real MenuCatalog, built here from the deployment's real default persona pack, exactly
# the same way `order_state.py`/`tools.py` resolve ANY bound persona's menu. This is not a
# behavior change: it is the same underlying data, loaded through the one remaining code path.
_SONIC = menu_utils.get_catalog_for_persona(get_default_persona())


def _load_golden_categories() -> list[dict]:
    with _GOLDEN_CATEGORIES_PATH.open("r", encoding="utf-8") as f:
        return json.load(f)["items"]


def _load_menu_item_names() -> set[str]:
    with _MENU_ITEMS_PATH.open("r", encoding="utf-8") as f:
        data = json.load(f)
    return {item["name"] for category in data["menuItems"] for item in category["items"]}


def _load_menu_item_raw_fields() -> dict[str, dict]:
    """Read menuItems.json directly (no ``menu_utils`` involved at all) -- item name ->
    ``{"comboSlot", "happyHourDiscounted", "requiresMachine"}`` exactly as authored in the pack,
    defaulting missing fields the same way the schema documents (``"none"`` / ``False`` /
    ``None``). Used to check the golden table against the RAW DATA itself (issue #71), independent
    of whether ``menu_utils``'s loader or classification functions have a bug.

    ``requiresMachine`` (PR #99 review decision 4): whether the item needs the ``slush_machine``
    or ``ice_cream_machine`` (or ``None`` if it needs neither) -- e.g. an 86'd machine at a
    physical store. Compared here the same way as comboSlot/happyHourDiscounted so the golden
    table can't silently drift from the pack on this field either."""
    with _MENU_ITEMS_PATH.open("r", encoding="utf-8") as f:
        data = json.load(f)
    fields: dict[str, dict] = {}
    for category in data["menuItems"]:
        for item in category["items"]:
            fields[item["name"]] = {
                "comboSlot": item.get("comboSlot", "none"),
                "happyHourDiscounted": bool(item.get("happyHourDiscounted", False)),
                "requiresMachine": item.get("requiresMachine"),
            }
    return fields


class GoldenTableCheckedAgainstPackDataTests(unittest.TestCase):
    """issue #71: the golden category table is CHECKED AGAINST the persona pack's data, not
    generated from it -- menuItems.json is hand-authored per the design doc (section 4.3/6) and
    the golden table is an independent, hand-authored oracle; this test compares the two directly,
    reading menuItems.json's raw ``comboSlot``/``happyHourDiscounted``/``requiresMachine`` fields
    with no ``menu_utils`` code involved at all (see ``InferComboComponentGoldenCategoryTests``
    above for the equivalent check that goes THROUGH ``infer_combo_component``/
    ``is_happy_hour_discounted``, which additionally proves the loader/classification functions
    agree with the raw data)."""

    @classmethod
    def setUpClass(cls):
        cls.golden = _load_golden_categories()
        cls.pack_fields = _load_menu_item_raw_fields()

    def test_every_golden_row_matches_the_pack_items_raw_fields(self):
        mismatches = []
        for row in self.golden:
            pack_item = self.pack_fields.get(row["item"])
            if pack_item is None:
                mismatches.append(f"{row['item']!r}: not found in menuItems.json")
                continue
            if pack_item["comboSlot"] != row["comboSlot"]:
                mismatches.append(
                    f"{row['item']!r}: pack comboSlot {pack_item['comboSlot']!r} != golden {row['comboSlot']!r}"
                )
            if pack_item["happyHourDiscounted"] != row["happyHourDiscounted"]:
                mismatches.append(
                    f"{row['item']!r}: pack happyHourDiscounted {pack_item['happyHourDiscounted']!r} "
                    f"!= golden {row['happyHourDiscounted']!r}"
                )
            if pack_item["requiresMachine"] != row.get("requiresMachine"):
                mismatches.append(
                    f"{row['item']!r}: pack requiresMachine {pack_item['requiresMachine']!r} "
                    f"!= golden {row.get('requiresMachine')!r}"
                )
        self.assertEqual(mismatches, [], "\n".join(mismatches))


class InferComboComponentGoldenCategoryTests(unittest.TestCase):
    """#39 / PR #50 review: every menuItems.json item must classify into its documented
    combo-slot (side/drink/none, ``infer_combo_component``) AND happy-hour-discount eligibility
    (``is_happy_hour_discounted``) -- two SEPARATE columns in the golden table, enforced
    independently so a future change to one can't silently regress the other (e.g. Ched 'R'
    Peppers back into a free combo side via the wide "Extras & Sides"/"Hot Dogs & Tots" bucket,
    or a sundae back into the happy-hour discount)."""

    @classmethod
    def setUpClass(cls):
        cls.golden = _load_golden_categories()

    def test_golden_table_covers_every_menu_item_exactly(self):
        """Not just ">= 60" (PR #50 follow-up): the golden item names must be EXACTLY the
        menuItems.json names, so a renamed/added/removed menu item is caught immediately instead
        of silently leaving the golden table stale."""
        golden_names = {row["item"] for row in self.golden}
        menu_names = _load_menu_item_names()
        self.assertEqual(golden_names, menu_names, "Golden table item names must exactly match menuItems.json")

    def test_every_golden_item_matches_its_documented_combo_slot(self):
        mismatches = []
        for row in self.golden:
            expected = row["comboSlot"]
            actual = _SONIC.infer_combo_component(row["item"]) or "none"
            if actual != expected:
                mismatches.append(f"{row['item']!r}: expected comboSlot {expected!r}, got {actual!r}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_every_golden_item_matches_its_documented_happy_hour_discount(self):
        """Independent from the combo-slot check above -- PR #50 review: happy-hour discount
        eligibility must be verified on its own axis, not inferred from comboSlot=="drinks"."""
        mismatches = []
        for row in self.golden:
            expected = row["happyHourDiscounted"]
            actual = _SONIC.is_happy_hour_discounted(row["item"])
            if actual != expected:
                mismatches.append(f"{row['item']!r}: expected happyHourDiscounted {expected!r}, got {actual!r}")
        self.assertEqual(mismatches, [], "\n".join(mismatches))

    def test_ched_r_peppers_is_not_a_combo_side(self):
        """PR #50 pricing regression: 'Ched 'R' Peppers' is an "Extras & Sides"-adjacent item, not
        one of the two combo side-slot items (Tots, Groovy Fries) -- it must be charged in full,
        not silently absorbed for free into a combo. It also must not match the bare substring
        'pepper' as the drink 'Dr Pepper' (the original #39 bug)."""
        self.assertEqual(_SONIC.infer_combo_component("Ched 'R' Peppers"), "")

    def test_only_tots_and_groovy_fries_fill_the_combo_side_slot(self):
        """PR #50 must-fix: the menu's own combo description says "your choice of a side (Tots or
        Fries) and a drink" -- the combo side slot is an explicit allow-list of exactly those two
        items, not the whole "Hot Dogs & Tots"/"Extras & Sides" category."""
        self.assertEqual(_SONIC.infer_combo_component("Tots"), "sides")
        self.assertEqual(_SONIC.infer_combo_component("Groovy Fries"), "sides")

    def test_extras_and_sides_lookalikes_are_not_combo_sides(self):
        """PR #50 pricing regression: these 9+ items were being silently absorbed for free into a
        combo's side slot because the whole "Extras & Sides"/"Hot Dogs & Tots" category mapped to
        "sides". They must all charge in full alongside a combo."""
        for name in (
            "Crispy Tenders - 3 Piece",
            "Crispy Tenders - 5 Piece",
            "Premium Chicken Bites",
            "FRITOS® Chili Cheese Wrap",
            "FRITOS® Chili Cheese Jr. Wrap",
            "Fritos Chili Cheese Pie",
            "Soft Pretzel Twist",
            "Mozzarella Sticks",
            "Ched 'R' Peppers",
            "Onion Rings",
            "Cheese Tots",
            "Cheese Groovy Fries",
            "Chili Cheese Groovy Fries",
            "Chili Cheese Tots",
        ):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)

    def test_sundaes_are_neither_a_combo_drink_nor_happy_hour_discounted(self):
        """Brian's #39 decision: sundaes are full price during happy hour and can't fill a
        combo's drink slot, even though they live in the "Shakes & Ice Cream" category."""
        for name in ("Hot Fudge Sundae", "Caramel Sundae"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

    def test_hot_dog_entrees_are_not_the_sides_bucket(self):
        """Hot-dog entrees share the "Hot Dogs & Tots" JSON category with real sides (Tots,
        Onion Rings, ...) but are food items, not a fillable combo side slot."""
        for name in ("All-American Dog", "Chili Cheese Coney", "Footlong Quarter Pound Coney", "Corn Dog"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)

    def test_dr_pepper_without_the_registered_trademark_symbol_still_resolves_directly(self):
        """#73: there is no more keyword fallback at all -- "Dr Pepper" (spoken/typed without the
        "®" the real menuItems.json name "Dr Pepper®" carries) still resolves because ``_menu_key``
        strips "®" before the lookup, an exact-match resolution, never a substring/keyword guess.
        "Peppercorn Ranch Dip" is genuinely off-menu and must return the safe default."""
        self.assertEqual(_SONIC.infer_combo_component("Dr Pepper"), "drinks")
        self.assertEqual(_SONIC.infer_combo_component("Diet Dr Pepper"), "drinks")
        self.assertEqual(_SONIC.infer_combo_component("Peppercorn Ranch Dip"), "")

    def test_slushes_and_drinks_are_happy_hour_discounted(self):
        self.assertTrue(_SONIC.is_happy_hour_discounted("Cherry Limeade"))
        self.assertTrue(_SONIC.is_happy_hour_discounted("Ocean Water®"))

    def test_shakes_and_blasts_are_full_price_during_happy_hour(self):
        """Brian's decision (2026-09-25, #39 follow-up): Shakes & Blasts are NOT happy-hour
        discounted -- full price. Since #71 this is a permanent, explicit
        ``happyHourDiscounted: false`` on every Shakes & Ice Cream item in menuItems.json (not a
        Python switch any more), and this test is what pins/documents that final answer.
        Combo-drink-slot eligibility is unaffected -- a separate question (PR #50 review)."""
        self.assertFalse(_SONIC.is_happy_hour_discounted("Vanilla Classic Shake"))
        self.assertFalse(_SONIC.is_happy_hour_discounted("SONIC Blast® made with OREO® Cookie Pieces"))
        self.assertEqual(_SONIC.infer_combo_component("Vanilla Classic Shake"), "drinks")

    def test_floats_fill_the_combo_drink_slot_but_are_never_happy_hour_discounted(self):
        """Brian's #64/#72 decision: floats can fill a combo's drink slot (unlike shakes/sundaes,
        which are food, not a drink-slot filler) but do NOT get the happy-hour discount -- unlike
        every other Slushes & Drinks / fountain-drink item, which IS discounted. Mutation check:
        flipping either ``happyHourDiscounted`` to true or ``comboSlot`` to "none"/anything but
        "drinks" for any of these three menuItems.json rows must fail this test."""
        for name in ("Root Beer Float", "Coke Float", "Dr Pepper Float"):
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)
            self.assertEqual(_SONIC.infer_combo_component(name), "drinks", name)

    def test_fountain_drinks_added_by_72_fill_the_combo_drink_slot_and_are_happy_hour_discounted(self):
        """#72: the 8 fountain drinks named by the issue are ordinary Slushes & Drinks items --
        they fill the combo drink slot and ARE happy-hour discounted, same as every other item in
        that category (contrast with the floats above, which share the drink slot but are never
        discounted)."""
        for name in (
            "Coca-Cola®",
            "Diet Coke®",
            "Coca-Cola® Zero",
            "Dr Pepper®",
            "Diet Dr Pepper®",
            "BARQ'S® Root Beer",
            "Sprite®",
            "Sprite Zero®",
        ):
            self.assertTrue(_SONIC.is_happy_hour_discounted(name), name)
            self.assertEqual(_SONIC.infer_combo_component(name), "drinks", name)

    def test_coke_zero_alias_resolves_to_coca_cola_zero(self):
        """#72 names the item "Coke Zero" in its acceptance criteria; the real menuItems.json name
        is "Coca-Cola® Zero" -- the alias must resolve to the same classification as the canonical
        name."""
        self.assertEqual(_SONIC.infer_category("Coke Zero"), _SONIC.infer_category("Coca-Cola® Zero"))
        self.assertEqual(_SONIC.infer_combo_component("Coke Zero"), _SONIC.infer_combo_component("Coca-Cola® Zero"))
        self.assertEqual(_SONIC.is_happy_hour_discounted("Coke Zero"), _SONIC.is_happy_hour_discounted("Coca-Cola® Zero"))

    def test_priced_extras_added_by_72_are_never_a_combo_slot_or_happy_hour_discounted(self):
        """#72's priced add-ons (flavor add-in, add bacon, whipped topping) are isExtra: true
        items, not food/drink combo components, and are never happy-hour discounted."""
        for name in ("Flavor Add-In", "Add Bacon", "Whipped Topping"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

    def test_whipped_topping_aliases_resolve_to_the_same_classification(self):
        """PR #98 Rick review item 2: "Whipped Topping" is the canonical isExtra menu item name
        (source modifier "Whip Topping", Easy/Regular tier, $0.20); "whipped cream" and "whip" are
        its spoken aliases and must resolve to the same classification."""
        for alias in ("whipped cream", "whip"):
            self.assertEqual(_SONIC.infer_category(alias), _SONIC.infer_category("Whipped Topping"))
            self.assertEqual(_SONIC.infer_combo_component(alias), _SONIC.infer_combo_component("Whipped Topping"))
            self.assertEqual(_SONIC.is_happy_hour_discounted(alias), _SONIC.is_happy_hour_discounted("Whipped Topping"))

    def test_burgers_combos_and_hot_dog_entrees_are_never_happy_hour_discounted(self):
        for name in ("Crispy Chicken Sandwich", "SONIC® Cheeseburger Combo", "Corn Dog", "Tots", "Groovy Fries"):
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)


class CustomisedItemMenuLookupTests(unittest.TestCase):
    """PR #50 review (second round): modifiers travel inside item_name (e.g. "Tots (Extra
    Crispy)", tools.py's ``update_order``), so every menuItems.json-based lookup must strip them
    via the one shared ``strip_modifiers``/``_menu_key`` rule before classifying -- a customised
    item must classify EXACTLY like its base item, never fall through to a keyword fallback that
    disagrees with the base item's real menuItems.json category."""

    def test_strip_modifiers_removes_a_trailing_parenthesized_suffix(self):
        self.assertEqual(strip_modifiers("Tots (Extra Crispy)"), "Tots")
        self.assertEqual(strip_modifiers("Chili Cheese Tots (Extra Cheese)"), "Chili Cheese Tots")
        self.assertEqual(strip_modifiers("Cherry Limeade"), "Cherry Limeade")

    def test_chili_cheese_tots_customised_is_charged_in_full_not_absorbed(self):
        """Rick's repro: Cheeseburger Combo 8.49 + "Chili Cheese Tots (Extra Cheese)" was
        measuring as absorbed free (8.49) instead of charged in full (12.28) because the raw,
        un-stripped name missed the menuItems.json category lookup and fell through to a keyword
        fallback that (wrongly) matched "tots"."""
        self.assertEqual(_SONIC.infer_combo_component("Chili Cheese Tots (Extra Cheese)"), "")

    def test_chili_cheese_groovy_fries_customised_is_charged_in_full_not_absorbed(self):
        self.assertEqual(_SONIC.infer_combo_component("Chili Cheese Groovy Fries (No Chili)"), "")

    def test_plain_tots_customised_still_fills_the_combo_side_slot(self):
        """The allow-listed items themselves must still be absorbed once customised -- only the
        modifier is stripped, the underlying item is unchanged."""
        self.assertEqual(_SONIC.infer_combo_component("Tots (Extra Crispy)"), "sides")
        self.assertEqual(_SONIC.infer_combo_component("Groovy Fries (Extra Salty)"), "sides")

    def test_unknown_misspelled_item_never_fills_the_side_slot_even_as_a_substring_match(self):
        """PR #50 must-fix 2: the side fallback for unknown items is deleted entirely -- a
        misspelling/off-menu item (here "chilli cheese tots", a typo) must never silently absorb
        into a combo's side slot. A charged item is visible and correctable; a free one is a
        silent revenue loss."""
        self.assertEqual(_SONIC.infer_combo_component("chilli cheese tots"), "")
        self.assertEqual(_SONIC.infer_combo_component("totstastic snack"), "")

    def test_cherry_limeade_customised_still_gets_the_happy_hour_discount(self):
        self.assertTrue(_SONIC.is_happy_hour_discounted("Cherry Limeade (Extra Cherries)"))

    def test_ched_r_peppers_customised_is_still_not_a_combo_side_or_dr_pepper(self):
        self.assertEqual(_SONIC.infer_combo_component("Ched 'R' Peppers (Extra Spicy)"), "")

    def test_customised_category_matches_base_item_category(self):
        self.assertEqual(_SONIC.infer_category("Tots (Extra Crispy)"), _SONIC.infer_category("Tots"))

    def test_shakes_and_blasts_are_never_happy_hour_discounted_plain_or_customised(self):
        """PR #50 review's original intent (a single answer for every shake/blast, plain or
        customised, on-menu or off-menu) still holds under #71's data-driven classification --
        there is simply no longer a switch to flip: ``happyHourDiscounted: false`` on every
        Shakes & Ice Cream pack item. #73: the off-menu keyword fallback is gone entirely, so an
        off-menu name like "Chocolate Malt" now resolves via the safe default (never discounted,
        never a combo-slot filler) instead of a keyword guess -- still never discounted either
        way, which is why this assertion is unchanged, but the combo-slot answer below changes."""
        on_menu_plain = "Vanilla Classic Shake"
        on_menu_customised = "Vanilla Classic Shake (No Whip)"
        on_menu_blast_customised = "SONIC Blast® made with OREO® Cookie Pieces (Extra Candy)"
        off_menu_plain = "Chocolate Malt"
        off_menu_customised = "Chocolate Malt (Extra Malt)"

        for name in (on_menu_plain, on_menu_customised, on_menu_blast_customised, off_menu_plain, off_menu_customised):
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

        # #73: off-menu names are no longer swept into the combo drink slot by a keyword guess --
        # they fill no slot at all, exactly like every other unresolved name.
        self.assertEqual(_SONIC.infer_combo_component(off_menu_customised), "")


class TotsAliasNormalisationTests(unittest.TestCase):
    """Brian's decision (2026-09-25, new issue #60): any spoken name-variant of PLAIN Tots --
    "Tot", "Tots", "Tater Tot", "Tater Tots", the common misspelling "Tator Tot(s)" -- fills the
    combo side slot exactly like the real "Tots" menuItems.json item does. This is an explicit
    alias allow-list, NOT a substring match: Rick's PR #50 revenue rule still applies, so a
    real-but-different menu item ("Chili Cheese Tots", "Cheese Tots") and an off-menu near-miss
    ("Loaded Tots Supreme", the misspelled "chilli cheese tots") must all still be charged in
    full."""

    def test_every_plain_tots_alias_fills_the_combo_side_slot(self):
        for name in ("Tot", "tot", "Tots", "TOTS", "Tater Tot", "Tater Tots", "Tator Tots", "Tator Tot"):
            self.assertEqual(_SONIC.infer_combo_component(name), "sides", name)

    def test_customised_tots_alias_still_fills_the_combo_side_slot(self):
        """Alias resolution runs after ``_menu_key`` normalisation, so a bracketed modifier is
        stripped first exactly like it is for the real "Tots" item."""
        self.assertEqual(_SONIC.infer_combo_component("Tater Tots (Extra Crispy)"), "sides")
        self.assertEqual(_SONIC.infer_combo_component("Tator Tots (Extra Crispy)"), "sides")

    def test_spoken_misspelling_tator_tots_is_recognised(self):
        self.assertEqual(_SONIC.infer_combo_component("tator tots"), "sides")

    def test_real_but_different_tots_menu_items_still_charged_in_full(self):
        """These are separate, real menuItems.json items -- not aliases of plain Tots -- and must
        keep being charged in full, exactly like Rick's PR #50 revenue rule requires."""
        for name in ("Chili Cheese Tots", "Cheese Tots"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)

    def test_off_menu_near_miss_names_still_charged_in_full(self):
        """The alias is an EXACT match against the alias set, not a substring check -- these
        off-menu names merely contain "tot(s)" and must not be swept up by the alias."""
        for name in ("Loaded Tots Supreme", "chilli cheese tots", "totstastic snack"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)

    def test_tots_alias_resolves_the_same_category_as_the_real_tots_item(self):
        """issue #71 (deliberate behaviour change from #60): aliases now resolve for EVERY
        lookup, not just the combo side slot -- so an alias like "Tater Tot" now resolves via
        ``MENU_CATEGORY_MAP`` to "Tots"'s real category ("Hot Dogs & Tots"), not the pre-#71
        keyword-fallback answer ("sides"). Functionally equivalent for downstream behaviour
        (tools.py's upsell hint and extras rules block both categories identically), but the
        string itself changes -- pinned here on purpose. Happy-hour-discount eligibility is
        unaffected either way (Tots was never a drink, discounted or not)."""
        for name in ("Tater Tot", "Tater Tots", "Tator Tots"):
            self.assertEqual(_SONIC.infer_category(name), "hot dogs & tots", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)


class AliasResolvesIdenticallyOnEveryLookupTests(unittest.TestCase):
    """issue #71: an alias must be OBSERVABLY IDENTICAL to its canonical item across ALL THREE
    classification lookups (category, combo slot, happy-hour discount) -- not just the combo side
    slot #60 originally scoped. Pins the exact requirement from the issue text: flipping a name
    between an alias and the real item name must never change the answer to any of the three
    questions."""

    def test_every_tots_alias_matches_the_canonical_tots_item_on_every_axis(self):
        canonical = "Tots"
        aliases = (
            "Tot", "tot", "Tots", "TOTS",
            "Tater Tot", "Tater Tots", "Tator Tot", "Tator Tots",
            "tatertot", "tatertots", "tatortot", "tatortots",
            "tater-tot", "tater-tots", "tator-tot", "tator-tots",
        )
        for alias in aliases:
            with self.subTest(alias=alias):
                self.assertEqual(_SONIC.infer_category(alias), _SONIC.infer_category(canonical), alias)
                self.assertEqual(_SONIC.infer_combo_component(alias), _SONIC.infer_combo_component(canonical), alias)
                self.assertEqual(
                    _SONIC.is_happy_hour_discounted(alias), _SONIC.is_happy_hour_discounted(canonical), alias
                )


class MoreTotsAliasFormsTests(unittest.TestCase):
    """PR #61 review, must-fix 3: one-word forms ("tatertot(s)", "tatortot(s)") and hyphenated
    forms ("tater-tot(s)", "tator-tot(s)"). ``_menu_key`` does NOT collapse a hyphen to a space
    (confirmed via ``_menu_key("Tater-Tot") == "tater-tot"``, not "tater tot") -- a hyphen is not
    whitespace and none of ``strip_modifiers``'s ``.split()``/``" ".join(...)`` pass, nor any of
    ``_menu_key``'s three symbol-replacements, touch it. So the hyphenated forms need their OWN
    entries in "Tots"'s ``aliases`` list in menuItems.json; they are not already covered by the
    "tater tot"/"tator tot" (space) entries added for #60."""

    def test_one_word_forms_fill_the_combo_side_slot(self):
        for name in ("tatertot", "TaterTot", "tatertots", "TATERTOTS", "tatortot", "tatortots"):
            self.assertEqual(_SONIC.infer_combo_component(name), "sides", name)

    def test_hyphenated_forms_fill_the_combo_side_slot(self):
        for name in ("tater-tot", "Tater-Tot", "tater-tots", "TATER-TOTS", "tator-tot", "tator-tots"):
            self.assertEqual(_SONIC.infer_combo_component(name), "sides", name)

    def test_customised_new_alias_forms_still_fill_the_combo_side_slot(self):
        for name in ("Tater-Tots (Extra Crispy)", "TaterTots (Extra Crispy)"):
            self.assertEqual(_SONIC.infer_combo_component(name), "sides", name)

    def test_new_alias_forms_resolve_the_same_category_as_the_real_tots_item(self):
        """See ``test_tots_alias_resolves_the_same_category_as_the_real_tots_item`` above (#71):
        every alias form resolves category too, now -- not "sides" any more."""
        for name in ("tatertots", "tater-tots"):
            self.assertEqual(_SONIC.infer_category(name), "hot dogs & tots", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

    def test_near_miss_spellings_still_charged_in_full(self):
        """"Totts" (typo, doubled T) and "Tater Tot's" (stray apostrophe) are NOT in the alias
        set -- they must stay charged in full exactly like any other off-menu near-miss (Rick's
        PR #50 revenue rule)."""
        for name in ("Totts", "Tater Tot's", "Tatertot's"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)


class GroovyFriesAliasTests(unittest.TestCase):
    """#73 (Rick's PR #100 review, optional/cheap item): "fries" is an unambiguous plain-English
    spoken alias for "Groovy Fries" -- ``menuItems.json`` only has three fries items ("Groovy
    Fries", "Cheese Groovy Fries", "Chili Cheese Groovy Fries"), and only the plain, unqualified
    one is a sensible resolution of the bare word "fries" alone."""

    def test_fries_resolves_to_groovy_fries(self):
        resolved = _SONIC.resolve_menu_item("fries")
        self.assertIsNotNone(resolved)
        self.assertEqual(resolved["name"], "Groovy Fries")

    def test_fries_is_case_insensitive(self):
        for name in ("Fries", "FRIES", "  fries  "):
            resolved = _SONIC.resolve_menu_item(name)
            self.assertIsNotNone(resolved, name)
            self.assertEqual(resolved["name"], "Groovy Fries", name)

    def test_cheese_and_chili_cheese_variants_are_unaffected(self):
        """The alias is scoped to plain "Groovy Fries" only -- it must not make the cheese/chili
        cheese variants resolve to the plain item, nor vice versa."""
        self.assertEqual(_SONIC.resolve_menu_item("Cheese Groovy Fries")["name"], "Cheese Groovy Fries")
        self.assertEqual(
            _SONIC.resolve_menu_item("Chili Cheese Groovy Fries")["name"], "Chili Cheese Groovy Fries"
        )


class SizeWordFailSafeTests(unittest.TestCase):
    """PR #61 review, must-fix 5 -- no behaviour change, a pinned fail-safe contract. Size words
    embedded directly in the name text are NOT stripped by ``strip_modifiers`` (only a bracketed
    ``(...)`` modifier is), so a size word inside the name breaks the alias's exact match, while
    the same size word expressed as a bracketed modifier does not (it is stripped before the
    alias lookup, exactly like any other modifier)."""

    def test_size_word_in_the_name_text_is_charged_in_full(self):
        """"Large Tater Tots" -- the size word is part of the name text, so it survives
        ``_menu_key()`` and the resulting key ("large tater tots") is not one of "Tots"'s
        ``aliases`` in menuItems.json."""
        self.assertEqual(_SONIC.infer_combo_component("Large Tater Tots"), "", "Large Tater Tots")

    def test_size_word_as_a_bracketed_modifier_still_absorbs(self):
        """"Tater Tots (Large)" -- the size word is a bracketed modifier, stripped by
        ``strip_modifiers`` before the alias lookup runs, leaving "Tater Tots" which does
        resolve via "Tots"'s ``aliases``."""
        self.assertEqual(_SONIC.infer_combo_component("Tater Tots (Large)"), "sides", "Tater Tots (Large)")


class CanonicalSizeKeyNoDisplaySizesPinningTests(unittest.TestCase):
    """Rick's PR #100 review, required item 2 (pin): ``canonical_size_key`` already treats every
    "no real size word" spelling -- ``""``, ``"n/a"``, ``"na"``, ``"none"``, ``"n.a."`` -- as the
    literal ``"standard"`` key (see its docstring and ``_NO_DISPLAY_SIZES`` above), which is what
    lets a real single-size item (e.g. "Salted Caramel Toffee Croissant Bites", whose only size is
    "Standard") be ordered as ``"n/a"``/``""``/etc. without #73's on-menu size gate wrongly
    rejecting it as size_not_available. This behaviour existed before this PR revision but had no
    dedicated pytest pinning it directly -- this class is that pin, so a future edit to
    ``_NO_DISPLAY_SIZES``/``canonical_size_key`` that breaks it fails loudly here instead of only
    showing up as a mysterious conformance/live-order regression."""

    def test_empty_string_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key(""), "standard")

    def test_n_slash_a_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key("n/a"), "standard")

    def test_na_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key("na"), "standard")

    def test_none_word_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key("none"), "standard")

    def test_n_dot_a_dot_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key("n.a."), "standard")

    def test_standard_itself_is_standard(self):
        self.assertEqual(_SONIC.canonical_size_key("standard"), "standard")

    def test_case_and_whitespace_insensitive(self):
        self.assertEqual(_SONIC.canonical_size_key("  N/A  "), "standard")
        self.assertEqual(_SONIC.canonical_size_key("NONE"), "standard")


class OffMenuNamesReturnSafeDefaultsSinceIssue73Tests(unittest.TestCase):
    """#73 (P2-4, "No off-menu ordering"): the keyword-guessing fallback that used to answer for
    ANY name not found in ``MENU_CATEGORY_MAP`` (``_keyword_fallback_combo_drink`` /
    ``_keyword_fallback_happy_hour_discounted``, plus the matching side/keyword guess in
    ``infer_category``/``infer_combo_component``) is deleted outright, per ADR-001 decision 4 ("if
    it's not on the menu in our source data, you cannot order it"). An unresolved name is now
    classified purely by its safe default -- ``infer_category`` -> ``""``, ``infer_combo_component``
    -> ``""``, ``is_happy_hour_discounted`` -> ``False`` -- never by guessing by keyword. This
    class replaces the old ``KeywordFallbackPrecedenceTests`` / ``KeywordFallbackWordBoundaryTests``
    / ``KeywordOverCorrectionTests``, which pinned the ordering/word-boundary/over-correction
    behaviour of that now-deleted keyword-guessing code; ``tools.py``'s ``update_order`` rejects
    every one of these names outright as ``not_on_menu`` before classification is even reached, but
    ``menu_utils`` itself must still degrade safely for any other caller.

    A mutation re-adding a keyword fallback (e.g. matching "shake" or "tea" by substring again)
    would make several of these assertions fail."""

    def test_off_menu_names_that_used_to_match_shake_blast_or_fountain_keywords_are_unclassified(self):
        for name in (
            "Cherry Limeade Shake",
            "Strawberry Lemonade Shake",
            "Dr Pepper Shake",
            "Sweet Tea Blast",
            "Chocolate Milkshake",
            "Cherry Slushes",
            "Blue Raspberry Slushie",
            "Grape Slushy",
            "Overshake Deluxe",
        ):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

    def test_off_menu_names_that_used_to_falsely_match_steak_or_a_plural_drink_are_unclassified(self):
        """"Philly Cheesesteak"/"Steak Sandwich" (contain "tea" as a bare substring of "steak") and
        "Cokes" (plural -- only the singular "Coke" is a real alias) are all off-menu; none of them
        get swept up by any keyword any more, because there is no keyword matching left at all."""
        for name in ("Philly Cheesesteak", "Steak Sandwich", "Cokes"):
            self.assertEqual(_SONIC.infer_combo_component(name), "", name)
            self.assertFalse(_SONIC.is_happy_hour_discounted(name), name)

    def test_real_fountain_drink_and_tea_names_still_resolve_directly_not_via_a_keyword(self):
        """The genuinely on-menu names resolve fine -- via ``MENU_CATEGORY_MAP``'s direct lookup,
        never a keyword guess. Contrast with "Sweet Tea"/"Iced Tea"/"Unsweetened Tea" above, none of
        which are real menuItems.json names (the real ones are "Sweet Iced Tea"/"Unsweet Iced
        Tea")."""
        for name in ("Cherry Limeade", "Sweet Iced Tea", "Unsweet Iced Tea", "Coke"):
            self.assertEqual(_SONIC.infer_combo_component(name), "drinks", name)
            self.assertTrue(_SONIC.is_happy_hour_discounted(name), name)


class MenuCategoryMapDirectResolutionTests(unittest.TestCase):
    """PR #50 review (round 4, should-fix 2): every menuItems.json item must resolve via
    ``MENU_CATEGORY_MAP`` directly -- never by falling through to keyword-guessing luck. This is
    the regression class the OREO Blast's NBSP caused: ``MENU_CATEGORY_MAP`` used to be keyed by a
    bare ``name.lower()``, which doesn't collapse NBSP, so that item's own exact name missed its
    own map entry and only classified correctly because the substring "blast" happened to still
    match in the keyword fallback."""

    @classmethod
    def setUpClass(cls):
        cls.menu_names = _load_menu_item_names()

    def test_every_menu_item_name_resolves_directly_via_the_category_map(self):
        """Direct proof the map key resolves -- ``_menu_key(name)`` must be a member of
        ``MENU_CATEGORY_MAP`` for every real menu item, with no fallback involved at all."""
        missing = [name for name in self.menu_names if menu_utils._menu_key(name) not in _SONIC.category_map]
        self.assertEqual(missing, [], f"Missing from MENU_CATEGORY_MAP: {missing}")

    def test_combo_and_happy_hour_classification_never_reaches_a_keyword_fallback(self):
        """#73: the keyword-fallback functions this test used to patch-and-raise
        (``_keyword_fallback_combo_drink``/``_keyword_fallback_happy_hour_discounted``) are deleted
        entirely, so there's nothing left to patch -- the strongest possible proof they're never
        reached. Classifying every real menu item name must simply succeed without error."""
        for name in self.menu_names:
            _SONIC.infer_combo_component(name)
            _SONIC.is_happy_hour_discounted(name)


class TrademarkAndCurlyApostropheNormalisationTests(unittest.TestCase):
    """PR #50 review round 5, should-fix item 4: "™" and the curly apostrophe "\u2019" must be
    normalised in ``_menu_key()`` exactly like "®" already is. Fourteen ``menuItems.json`` names
    carry "™" (the "SONIC Smasher™" family, plain and Combo variants, plus the "$6 All-American
    Smasher™ Meal" and "Ultimate Meat & Cheese Breakfast Burrito™" pair added by issue #72 Part 2's
    full export import) and three carry "\u2019" (the "SONIC Blast® made with REESE'S"/"...M&M'S®
    Chocolate Candies" family and "Chocolate Peanut Butter Shake Made With REESE'S", also added by
    Part 2 -- the raw JSON names use the curly apostrophe verbatim). All originally missed their
    own ``MENU_CATEGORY_MAP`` entry and relied on keyword-fallback luck exactly like the OREO
    Blast's NBSP did before round 4."""

    @classmethod
    def setUpClass(cls):
        cls.menu_names = _load_menu_item_names()
        cls.tm_names = [name for name in cls.menu_names if "\u2122" in name]
        cls.curly_apostrophe_names = [name for name in cls.menu_names if "\u2019" in name]

    def test_menu_data_has_the_expected_special_character_names(self):
        """Sanity check on the fixture itself so this test class fails loudly, not silently, if
        ``menuItems.json`` ever changes which names carry these characters."""
        self.assertEqual(len(self.tm_names), 14, self.tm_names)
        self.assertEqual(len(self.curly_apostrophe_names), 3, self.curly_apostrophe_names)

    def test_trademark_symbol_is_stripped_from_the_menu_key(self):
        for name in self.tm_names:
            with self.subTest(name=name):
                self.assertNotIn("\u2122", menu_utils._menu_key(name))

    def test_curly_apostrophe_is_normalised_to_a_plain_apostrophe(self):
        for name in self.curly_apostrophe_names:
            with self.subTest(name=name):
                self.assertNotIn("\u2019", menu_utils._menu_key(name))
                self.assertIn("'", menu_utils._menu_key(name))

    def test_trademark_and_curly_apostrophe_names_resolve_directly_via_the_category_map(self):
        """Direct proof of resolution -- ``_menu_key(name)`` must be a member of
        ``MENU_CATEGORY_MAP`` for every "™"- or "\u2019"-bearing menu item, with no fallback
        involved at all."""
        affected = self.tm_names + self.curly_apostrophe_names
        missing = [name for name in affected if menu_utils._menu_key(name) not in _SONIC.category_map]
        self.assertEqual(missing, [], f"Missing from MENU_CATEGORY_MAP: {missing}")

    def test_classification_never_reaches_a_keyword_fallback_for_these_names(self):
        """#73: same rationale as ``MenuCategoryMapDirectResolutionTests`` above -- the
        keyword-fallback functions are deleted entirely, so classifying every "™"- or
        "\u2019"-bearing name must simply succeed without error."""
        for name in self.tm_names + self.curly_apostrophe_names:
            _SONIC.infer_combo_component(name)
            _SONIC.is_happy_hour_discounted(name)

    def test_spoken_smasher_without_the_trademark_symbol_still_resolves(self):
        """A guest's speech-to-text transcription realistically omits an unspeakable "™" symbol --
        prove a Smasher spoken/typed without it still resolves via the map."""
        spoken = "All-American SONIC Smasher"
        on_menu = next(name for name in self.menu_names if menu_utils._menu_key(name) == menu_utils._menu_key(spoken))
        self.assertIn("™", on_menu)


if __name__ == "__main__":
    unittest.main()
