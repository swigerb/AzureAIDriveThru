using System.Text.Json.Serialization;

namespace Backend.Personas;

/// <summary>Mirror of personas/menu.schema.json (menu/menuItems.json). #51's per-item fields are
/// additive and optional with the same defaults as the JSON Schema's own "default" keywords, so a
/// pack written before #51 still deserializes identically.</summary>
internal sealed record PersonaMenu
{
    [JsonPropertyName("menuItems")] public required IReadOnlyList<PersonaMenuCategory> MenuItems { get; init; }
}

internal sealed record PersonaMenuCategory
{
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("items")] public required IReadOnlyList<PersonaMenuItem> Items { get; init; }
    // Optional icon glyph (issue 119); the frontend falls back to a neutral default when absent.
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    // Rick's PR 166 round-1 review, required item 9: optional per-menu-mode name/icon override,
    // rendered by the frontend only -- this backend never reads it itself (the raw menuItems.json
    // bytes are what `/personas/{id}/menu.json` actually serves, see PersonaRoutes.cs), it just
    // needs to round-trip through model validation at catalog load time without being rejected.
    [JsonPropertyName("modeDisplay")] public IReadOnlyDictionary<string, PersonaMenuCategoryModeOverride>? ModeDisplay { get; init; }
}

internal sealed record PersonaMenuCategoryModeOverride
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
}

internal sealed record PersonaMenuItem
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sizes")] public required IReadOnlyList<PersonaMenuItemSize> Sizes { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("longDescription")] public string? LongDescription { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }
    [JsonPropertyName("popularity")] public string? Popularity { get; init; }
    [JsonPropertyName("image")] public string? Image { get; init; }
    // Issue #304: optional per-item spoken-name override ("Assorted Munch-kins Donut Hole
    // Treats" for "Assorted MUNCHKINS\u00ae Donut Hole Treats"). Merged into MenuCatalog's
    // spoken-substitution table alongside sizes.spokenAs -- same Spoken()/apply-lexicon
    // mechanism, zero new call sites. Mirrors menu_utils.py's item_fields[key]["spokenName"].
    [JsonPropertyName("spokenName")] public string? SpokenName { get; init; }

    [JsonPropertyName("comboSlot")] public string ComboSlot { get; init; } = "none";
    [JsonPropertyName("happyHourDiscounted")] public bool HappyHourDiscounted { get; init; }
    [JsonPropertyName("aliases")] public IReadOnlyList<string> Aliases { get; init; } = [];
    [JsonPropertyName("bundle")] public PersonaMenuItemBundle? Bundle { get; init; }
    [JsonPropertyName("requiresMachine")] public string? RequiresMachine { get; init; }
    [JsonPropertyName("isExtra")] public bool IsExtra { get; init; }
    [JsonPropertyName("menuPeriod")] public string? MenuPeriod { get; init; }
    [JsonPropertyName("mealNumber")] public string? MealNumber { get; init; }
    // Optional calorie count (issue 165), shown alongside the price on the shared item card.
    [JsonPropertyName("calories")] public int? Calories { get; init; }
}

internal sealed record PersonaMenuItemSize
{
    [JsonPropertyName("size")] public required string Size { get; init; }
    [JsonPropertyName("price")] public required decimal Price { get; init; }
}

internal sealed record PersonaMenuItemBundle
{
    [JsonPropertyName("slots")] public IReadOnlyList<string>? Slots { get; init; }
    [JsonPropertyName("autoFill")] public IReadOnlyDictionary<string, string>? AutoFill { get; init; }
    [JsonPropertyName("defaultSize")] public string? DefaultSize { get; init; }
}
