"""Demo data conformance tests for issue #83 (P2-14).

Rick's #87 demo-readiness review found every persona pack's demo data (``assets/demo/
dummyOrder.json`` + ``dummyTranscripts.json``) stale in some way: an order line naming an
item/size that isn't on the real menu, a price that no longer matches the menu, an item that
requires a machine the pack marks down, or a transcript that narrates a different order (wrong
items and/or a wrong total) than the one actually being built.

This module is a SHARED, pack-discovering conformance check -- it contains NO brand literals.
It walks every persona pack ``PersonaCatalog.load()`` discovers on disk (auto-detected, so it
automatically covers any future pack too) and, for each one, validates its demo data against
that SAME pack's own menu, machine status, and tax rate:

  1. every demo order line's item/size resolves to a real menu entry, at the real menu price;
  2. no demo order line depends on a machine the pack currently marks down;
  3. the transcript's final summary line states a total that equals the order's own total,
     computed with the SAME exact ``Decimal`` math and ``ROUND_HALF_UP`` rounding
     ``order_state.py``'s own ticket uses (``money_utils.to_decimal``/``format_money``), and
     compared as formatted ``"$NN.NN"`` strings -- never float arithmetic + ``round()``, which
     can disagree with the ticket by a cent for some future pack's rate or prices;
  4. the transcript's final summary line never name-checks a REAL menu item that isn't actually
     part of the demo order (the exact defect Rick found: a transcript mentioning items the
     order doesn't contain).

The validation logic lives in small, pure, brand-agnostic functions (``validate_demo_order``,
``compute_order_total``, ``find_final_summary_amount``, ``transcript_total_errors``,
``validate_transcript_containment``) so a dedicated mutation test can call them directly against
a deliberately corrupted in-memory copy of a real pack's order and prove the check actually fails
closed -- never a check that always happens to pass.
"""

import copy
import json
import re
import sys
from decimal import Decimal
from pathlib import Path

import pytest

sys.path.append(str(Path(__file__).resolve().parents[1]))

from menu_utils import MenuCatalog, get_catalog_for_persona  # noqa: E402
from money_utils import format_money, to_decimal  # noqa: E402
from persona_loader import Persona, PersonaCatalog  # noqa: E402

_REPO_ROOT = Path(__file__).resolve().parents[3]
_CURRENCY_RE = re.compile(r"\$(\d+\.\d{2})")


# ===========================================================================
# Pure validation helpers (brand-agnostic; no persona/pack literals anywhere)
# ===========================================================================


def _normalize(text: str) -> str:
    """Lowercase, strip trademark glyphs and punctuation, for tolerant substring matching."""
    return re.sub(r"[^a-z0-9 ]", "", text.lower())


def _validate_order_line(line: dict, menu_catalog: MenuCatalog) -> list[str]:
    """Every error found for a single demo order line against the pack's real menu/machines."""
    errors: list[str] = []
    item_name = line["item"]
    resolved = menu_catalog.resolve_menu_item(item_name)
    if resolved is None:
        errors.append(f"{item_name!r} is not a real menu item")
        return errors

    size_key = menu_catalog.canonical_size_key(line["size"])
    real_price = menu_catalog.price_for(item_name, size_key)
    if real_price is None:
        errors.append(f"{item_name!r} has no menu price for size {line['size']!r}")
    elif abs(real_price - line["price"]) > 0.001:
        errors.append(
            f"{item_name!r} ({line['size']}) demo price {line['price']} "
            f"!= menu price {real_price}"
        )

    machine = menu_catalog.requires_machine(item_name)
    if machine and menu_catalog.machine_status(machine) == "down":
        errors.append(f"{item_name!r} requires {machine!r}, which this pack marks down")

    return errors


def validate_demo_order(order_lines: list[dict], menu_catalog: MenuCatalog) -> list[str]:
    """Every error found across an entire demo order's lines. Empty list == fully valid."""
    errors: list[str] = []
    for line in order_lines:
        errors.extend(_validate_order_line(line, menu_catalog))
    return errors


def compute_order_total(order_lines: list[dict], tax_rate) -> str:
    """The demo order's total, computed and rounded EXACTLY the way order_state.py's own ticket
    total is (#46, ``_update_summary``): accumulate ``price * quantity`` in exact ``Decimal``
    with no intermediate rounding, then ``tax = subtotal * tax_rate``, then
    ``total = subtotal + tax``, and format the single final result once with
    ``money_utils.format_money`` (``ROUND_HALF_UP``) -- never float arithmetic + ``round()``,
    which can disagree with the ticket by a cent for some future pack's rate or prices."""
    subtotal = Decimal("0")
    for line in order_lines:
        subtotal += to_decimal(line["price"]) * line["quantity"]
    tax = subtotal * to_decimal(tax_rate)
    total = subtotal + tax
    return format_money(total)


def find_final_summary_amount(transcript_entries: list[dict]) -> tuple[str, str] | None:
    """Scan a transcript backwards for the last entry that states a currency amount, and
    return ``(text, amount)`` for the LAST dollar amount in that entry's text -- the demo
    order-summary convention this repo's transcripts use ("...Your total is $NN.NN."). Prefers
    ``translation`` (the guest-facing English text) since several transcript lines are
    non-English; falls back to ``text``. ``amount`` is the exact matched two-decimal-digit
    string (e.g. ``"32.96"``), never round-tripped through ``float``, so it can be compared
    character-for-character against ``money_utils.format_money``'s own ``"$NN.NN"`` string.
    Returns ``None`` if no entry states an amount."""
    for entry in reversed(transcript_entries):
        text = entry.get("translation") or entry.get("text") or ""
        matches = _CURRENCY_RE.findall(text)
        if matches:
            return text, matches[-1]
    return None


def transcript_total_errors(order_lines: list[dict], transcript_entries: list[dict], tax_rate) -> list[str]:
    """Every error found comparing a transcript's stated final total against the order's own
    exact-decimal total (``compute_order_total``, i.e. the same math and rounding order_state.py's
    ticket uses) -- comparing formatted ``"$NN.NN"`` strings, never floats. Empty list == the
    transcript's stated total matches the order."""
    expected = compute_order_total(order_lines, tax_rate)
    found = find_final_summary_amount(transcript_entries)
    if found is None:
        return [
            "no transcript entry states a currency total (expected a final summary line "
            "like '...Your total is $NN.NN.')"
        ]
    _, stated_amount = found
    stated = f"${stated_amount}"
    if stated != expected:
        return [f"transcript states total {stated} but the demo order's own decimal math computes to {expected}"]
    return []


def validate_transcript_containment(
    order_lines: list[dict], summary_text: str, menu_catalog: MenuCatalog
) -> list[str]:
    """Every REAL menu item name mentioned (case-insensitive substring, trademark glyphs
    stripped) in the transcript's final summary line must also be one of the order's actual
    items. This is exactly Rick's reported defect shape: a transcript narrating a real menu
    item the guest never actually ordered."""
    errors: list[str] = []
    normalized_summary = _normalize(summary_text)
    order_names_normalized = [_normalize(line["item"]) for line in order_lines]
    for fields in menu_catalog.item_fields.values():
        name = fields.get("name")
        if not name:
            continue
        normalized_name = _normalize(name)
        if not normalized_name or normalized_name not in normalized_summary:
            continue
        if not any(
            normalized_name in ordered or ordered in normalized_name
            for ordered in order_names_normalized
        ):
            errors.append(
                f"transcript summary mentions real menu item {name!r}, "
                "which is not part of the demo order"
            )
    return errors


# ===========================================================================
# Fixtures / discovery
# ===========================================================================


def _load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def catalog() -> PersonaCatalog:
    return PersonaCatalog.load()


def _persona_ids() -> list[str]:
    # A bare, arg-free load: exactly the packs PersonaCatalog auto-discovers on disk today --
    # no brand literals, and automatically covers future packs.
    return PersonaCatalog.load().ids


# ===========================================================================
# Per-pack demo data conformance
# ===========================================================================


class TestDemoOrderMatchesMenu:
    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_every_order_line_resolves_at_the_real_menu_price(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        menu_catalog = get_catalog_for_persona(persona)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")

        errors = validate_demo_order(order, menu_catalog)

        assert errors == [], f"{persona_id}: " + "; ".join(errors)

    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_no_order_line_depends_on_a_machine_thats_down(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        menu_catalog = get_catalog_for_persona(persona)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")

        for line in order:
            machine = menu_catalog.requires_machine(line["item"])
            if machine:
                assert menu_catalog.machine_status(machine) != "down", (
                    f"{persona_id}: demo order includes {line['item']!r}, which requires "
                    f"{machine!r}, but this pack currently marks that machine down"
                )


class TestDemoTranscriptMatchesOrder:
    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_transcript_final_total_matches_the_computed_order_total(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")
        transcript = _load_json(persona.assets_dir / "demo" / "dummyTranscripts.json")

        tax_rate = persona.manifest.pricing.taxRate
        errors = transcript_total_errors(order, transcript, tax_rate)
        assert errors == [], f"{persona_id}: " + "; ".join(errors)

    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_transcript_summary_never_names_an_item_not_in_the_order(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        menu_catalog = get_catalog_for_persona(persona)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")
        transcript = _load_json(persona.assets_dir / "demo" / "dummyTranscripts.json")

        found = find_final_summary_amount(transcript)
        assert found is not None
        summary_text, _ = found

        errors = validate_transcript_containment(order, summary_text, menu_catalog)
        assert errors == [], f"{persona_id}: " + "; ".join(errors)


# ===========================================================================
# Mutation test: proves the price/menu check above actually fails closed
# ===========================================================================


class TestMutationIsCaught:
    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_a_corrupted_demo_price_is_caught(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        menu_catalog = get_catalog_for_persona(persona)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")

        # Ground truth: the real (fixed) demo data must be clean before we corrupt a copy of it.
        assert validate_demo_order(order, menu_catalog) == []

        mutated = copy.deepcopy(order)
        mutated[0]["price"] = round(mutated[0]["price"] + 1.00, 2)

        errors = validate_demo_order(mutated, menu_catalog)
        assert errors, (
            f"{persona_id}: mutating one demo order line's price must be caught by "
            "validate_demo_order, but it reported no errors"
        )

    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_a_corrupted_transcript_total_is_caught(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")
        transcript = _load_json(persona.assets_dir / "demo" / "dummyTranscripts.json")
        tax_rate = persona.manifest.pricing.taxRate

        # Ground truth: the real (fixed) demo data must be clean before we corrupt a copy of it.
        assert transcript_total_errors(order, transcript, tax_rate) == []

        mutated = copy.deepcopy(transcript)
        for entry in reversed(mutated):
            text = entry.get("translation") or entry.get("text") or ""
            match = _CURRENCY_RE.search(text)
            if match:
                bad_total = f"{float(match.group(1)) + 5.00:.2f}"
                entry["translation"] = re.sub(_CURRENCY_RE, f"${bad_total}", text)
                if "text" in entry:
                    entry["text"] = re.sub(_CURRENCY_RE, f"${bad_total}", entry["text"])
                break

        errors = transcript_total_errors(order, mutated, tax_rate)
        assert errors, (
            f"{persona_id}: mutating the transcript's stated total must be caught by "
            "transcript_total_errors"
        )

    @pytest.mark.parametrize("persona_id", _persona_ids())
    def test_a_demo_line_requiring_a_down_machine_is_caught(self, catalog, persona_id):
        persona: Persona = catalog.get(persona_id)
        menu_catalog = get_catalog_for_persona(persona)
        order = _load_json(persona.assets_dir / "demo" / "dummyOrder.json")

        # Ground truth: the real (fixed) demo data must be clean before we corrupt a copy of it.
        assert validate_demo_order(order, menu_catalog) == []

        machine = next(
            (m for m in (menu_catalog.requires_machine(line["item"]) for line in order) if m),
            None,
        )
        if machine is None:
            pytest.skip(f"{persona_id}'s demo order has no line that requires a machine")

        # Corrupt a scratch copy of the catalog's own machine-status map only -- never the real
        # persona data -- to prove the machine-down check actually fails closed.
        mutated_catalog = copy.deepcopy(menu_catalog)
        _, label = mutated_catalog.machines[machine]
        mutated_catalog.machines[machine] = ("down", label)

        errors = validate_demo_order(order, mutated_catalog)
        assert errors, (
            f"{persona_id}: forcing {machine!r} down for a demo order line that requires it "
            "must be caught by validate_demo_order"
        )
