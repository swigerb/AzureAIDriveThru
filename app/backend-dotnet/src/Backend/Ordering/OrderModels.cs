using System.Text.Json.Serialization;

namespace Backend.Ordering;

/// <summary>PR #184 round 2 (Rick's review, item 2/3): one PHYSICAL UNIT's own side/drink slot
/// state -- mutable fields mutated in place by <see cref="OrderState"/>'s bundle-slot helpers.
/// Mirrors models.py's <c>_bundle_slots</c> per-slot dict shape
/// (<c>{"item","size","display","last_item","last_size","autofill"}</c>). <see cref="Item"/> is
/// "" when the slot is vacant; <see cref="LastItem"/>/<see cref="LastSize"/> deliberately SURVIVE
/// a vacate (<see cref="OrderState"/>'s VacateBundleComponent) so a later refill of that same item
/// reports as a resize instead of a fresh free absorption.</summary>
internal sealed class BundleSlot
{
    public string Item { get; set; } = "";
    public string Size { get; set; } = "";
    public string Display { get; set; } = "";
    public string LastItem { get; set; } = "";
    public string LastSize { get; set; } = "";
    public bool Autofill { get; set; }
    public decimal Upcharge { get; set; }
}

/// <summary>
/// Port of app/backend/models.py's <c>OrderItem</c> (docs/dotnet_mapping.md, issues #46/#77). A
/// mutable class, not a record -- <see cref="OrderState"/>'s combo/bundle engine mutates
/// <see cref="Quantity"/>/<see cref="Size"/>/<see cref="Price"/>/<see cref="Display"/> of an
/// existing line in place (e.g. absorbing a standalone side into a newly-added combo, or
/// <c>modify</c> resizing a line), exactly like Python's Pydantic model instances do.
/// </summary>
internal sealed class OrderItem
{
    [JsonPropertyName("item")] public required string Item { get; set; }
    [JsonPropertyName("size")] public required string Size { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    /// <summary>#77: additive, defaults to [] for every persona -- the bundle-slot component(s)
    /// absorbed into (or auto-filled onto) this line, e.g. ["Medium Fries", "Coca-Cola"] for a
    /// meal/combo. Rendered on the wire only when non-empty.</summary>
    [JsonPropertyName("components")] public List<string> Components { get; set; } = [];
    /// <summary>Optional, index-aligned with <see cref="Components"/>; values greater than zero
    /// render as per-unit component upcharges instead of "Included".</summary>
    [JsonPropertyName("componentUpcharges")] public List<decimal> ComponentUpcharges { get; set; } = [];

    /// <summary>PR #184 round 2 (Rick's review, item 2/3): per-INSTANCE, per-PHYSICAL-UNIT bundle
    /// slot-fill state -- keyed by component ("sides"/"drinks") -> a LIST of per-unit
    /// <see cref="BundleSlot"/> records, one entry per unit of <see cref="Quantity"/>, synced
    /// lazily by <see cref="OrderState"/>'s SyncBundleSlotList (grows/truncates to match
    /// Quantity). Never serialized on the wire -- JsonIgnore, exactly like models.py's
    /// PrivateAttr never appears in model_dump_json()/the order-summary wire contract; the wire's
    /// own "what's in the combo" story is Components above, never this engine-internal state.
    /// Mutated ONLY by OrderState (this session's sole owner); no other caller should ever read
    /// or write it directly.</summary>
    [JsonIgnore] public Dictionary<string, List<BundleSlot>> BundleSlots { get; } = [];
}

/// <summary>
/// Port of app/backend/models.py's <c>OrderSummary</c> (issue #47). <see cref="TotalDisplay"/>/
/// <see cref="TaxDisplay"/>/<see cref="FinalTotalDisplay"/> are the exact, contract-rounded
/// display strings (<see cref="Money.Format"/>) computed from the same <c>decimal</c> values as
/// <see cref="Total"/>/<see cref="Tax"/>/<see cref="FinalTotal"/>, before any further conversion --
/// unlike Python's ``float``-typed numeric fields (a Pydantic/JSON-wire concession Python needs
/// that this port does not), every numeric field here stays <c>decimal</c> end-to-end.
/// </summary>
internal sealed record OrderSummary(
    IReadOnlyList<OrderItem> Items,
    decimal Total,
    decimal Tax,
    decimal FinalTotal,
    string TotalDisplay,
    string TaxDisplay,
    string FinalTotalDisplay,
    string SpokenReadBack,
    string FinalTotalSpoken)
{
    [JsonPropertyName("items")] public IReadOnlyList<OrderItem> Items { get; init; } = Items;
    [JsonPropertyName("total")] public decimal Total { get; init; } = Total;
    [JsonPropertyName("tax")] public decimal Tax { get; init; } = Tax;
    [JsonPropertyName("finalTotal")] public decimal FinalTotal { get; init; } = FinalTotal;
    [JsonPropertyName("totalDisplay")] public string TotalDisplay { get; init; } = TotalDisplay;
    [JsonPropertyName("taxDisplay")] public string TaxDisplay { get; init; } = TaxDisplay;
    [JsonPropertyName("finalTotalDisplay")] public string FinalTotalDisplay { get; init; } = FinalTotalDisplay;
    /// <summary>Issue #304: server-composed, mandatory spoken order read-back -- every line's
    /// quantity/size/spoken name, then "Your total is {FinalTotalDisplay}." Computed once here (by
    /// <see cref="OrderState.UpdateSummary"/>) so the get_order tool response and this cached field
    /// can never drift apart. Mirrors models.py's <c>OrderSummary.spokenReadBack</c>.</summary>
    [JsonPropertyName("spokenReadBack")] public string SpokenReadBack { get; init; } = SpokenReadBack;
    /// <summary>#313 (Rick's review, item 6): the same <see cref="FinalTotal"/>, spoken out in
    /// words (<see cref="Money.FormatMoneySpoken"/>) -- the model-facing <c>update_order</c> delta
    /// text used to append the digit/`$`-formatted <see cref="FinalTotalDisplay"/> ahead of the
    /// voice read-back, giving the realtime model TWO different renderings of the same total in
    /// one tool result and risking it speaking the wrong one. <see cref="Backend.Tools.OrderToolExecutor"/>'s
    /// BuildDeltaText now builds its delta text from this field instead. Mirrors models.py's
    /// <c>OrderSummary.finalTotalSpoken</c>.</summary>
    [JsonPropertyName("finalTotalSpoken")] public string FinalTotalSpoken { get; init; } = FinalTotalSpoken;

    /// <summary>Builds a summary from raw totals, filling in the three Display strings from
    /// <see cref="Money.Format"/> and the spoken total from <see cref="Money.FormatMoneySpoken"/>
    /// -- mirrors Python's <c>OrderSummary</c> model_validator default-fill behavior, except here
    /// it's the ONLY construction path (no separately-suppliable override), since nothing in this
    /// C# port ever needs to pass a display string that disagrees with its own numeric value.</summary>
    public static OrderSummary Build(
        IReadOnlyList<OrderItem> items, decimal total, decimal tax, decimal finalTotal, string spokenReadBack) =>
        new(items, total, tax, finalTotal, Money.Format(total), Money.Format(tax), Money.Format(finalTotal),
            spokenReadBack, Money.FormatMoneySpoken(finalTotal));

    public static OrderSummary Empty() => Build([], 0m, 0m, 0m, "Your order is currently empty.");
}
