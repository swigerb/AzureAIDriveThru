using System.Text.Json.Serialization;

namespace Backend.Ordering;

/// <summary>
/// Port of app/backend/models.py's <c>OrderItem</c> (docs/dotnet_mapping.md, issues #46/#77). A
/// mutable class, not a record -- <see cref="OrderState"/>'s combo/bundle engine mutates
/// <see cref="Quantity"/>/<see cref="Size"/>/<see cref="Price"/>/<see cref="Display"/> of an
/// existing line in place (e.g. absorbing a standalone side into a newly-added combo, or
/// <c>modify</c> resizing a line), exactly like Python's Pydantic model instances do.
/// </summary>
public sealed class OrderItem
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
}

/// <summary>
/// Port of app/backend/models.py's <c>OrderSummary</c> (issue #47). <see cref="TotalDisplay"/>/
/// <see cref="TaxDisplay"/>/<see cref="FinalTotalDisplay"/> are the exact, contract-rounded
/// display strings (<see cref="Money.Format"/>) computed from the same <c>decimal</c> values as
/// <see cref="Total"/>/<see cref="Tax"/>/<see cref="FinalTotal"/>, before any further conversion --
/// unlike Python's ``float``-typed numeric fields (a Pydantic/JSON-wire concession Python needs
/// that this port does not), every numeric field here stays <c>decimal</c> end-to-end.
/// </summary>
public sealed record OrderSummary(
    IReadOnlyList<OrderItem> Items,
    decimal Total,
    decimal Tax,
    decimal FinalTotal,
    string TotalDisplay,
    string TaxDisplay,
    string FinalTotalDisplay)
{
    [JsonPropertyName("items")] public IReadOnlyList<OrderItem> Items { get; init; } = Items;
    [JsonPropertyName("total")] public decimal Total { get; init; } = Total;
    [JsonPropertyName("tax")] public decimal Tax { get; init; } = Tax;
    [JsonPropertyName("finalTotal")] public decimal FinalTotal { get; init; } = FinalTotal;
    [JsonPropertyName("totalDisplay")] public string TotalDisplay { get; init; } = TotalDisplay;
    [JsonPropertyName("taxDisplay")] public string TaxDisplay { get; init; } = TaxDisplay;
    [JsonPropertyName("finalTotalDisplay")] public string FinalTotalDisplay { get; init; } = FinalTotalDisplay;

    /// <summary>Builds a summary from raw totals, filling in the three Display strings from
    /// <see cref="Money.Format"/> -- mirrors Python's <c>OrderSummary</c> model_validator default-
    /// fill behavior, except here it's the ONLY construction path (no separately-suppliable
    /// override), since nothing in this C# port ever needs to pass a display string that
    /// disagrees with its own numeric value.</summary>
    public static OrderSummary Build(IReadOnlyList<OrderItem> items, decimal total, decimal tax, decimal finalTotal) =>
        new(items, total, tax, finalTotal, Money.Format(total), Money.Format(tax), Money.Format(finalTotal));

    public static OrderSummary Empty() => Build([], 0m, 0m, 0m);
}
