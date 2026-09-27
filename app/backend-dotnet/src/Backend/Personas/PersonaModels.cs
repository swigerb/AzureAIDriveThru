using System.Text.Json.Serialization;

namespace Backend.Personas;

// Strongly-typed mirror of personas/persona.schema.json (design doc section 4.4: "System.Text.Json
// source-generated records with JsonUnmappedMemberHandling.Disallow"). JSON Schema validation
// (PersonaSchemaValidator) runs FIRST against the raw JSON and is the primary source of
// field-named error messages -- this second, strongly-typed layer is defense in depth (mirrors
// persona_loader.py's jsonschema-then-Pydantic double validation) so a schema/model drift can
// never silently let an unrecognised shape through as if it were valid.

public sealed record Persona
{
    /// <summary>Optional, purely-editor-hint self-reference (JSON Schema's own "$schema"
    /// keyword). Schema-valid but not part of the typed contract otherwise -- present here only
    /// so JsonUnmappedMemberHandling.Disallow doesn't reject a manifest that includes it.</summary>
    [JsonPropertyName("$schema")] public string? Schema { get; init; }

    [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("roleName")] public required string RoleName { get; init; }
    [JsonPropertyName("locales")] public required PersonaLocales Locales { get; init; }
    [JsonPropertyName("store")] public required PersonaStore Store { get; init; }
    [JsonPropertyName("voice")] public required PersonaVoice Voice { get; init; }
    [JsonPropertyName("search")] public required PersonaSearch Search { get; init; }
    [JsonPropertyName("pricing")] public required PersonaPricing Pricing { get; init; }
    [JsonPropertyName("sizes")] public required PersonaSizes Sizes { get; init; }
    [JsonPropertyName("bundles")] public required PersonaBundles Bundles { get; init; }
    [JsonPropertyName("extras")] public required PersonaExtras Extras { get; init; }
    [JsonPropertyName("invalidModifiers")] public required Dictionary<string, List<string>> InvalidModifiers { get; init; }
    [JsonPropertyName("machines")] public required Dictionary<string, string> Machines { get; init; }
    [JsonPropertyName("models")] public required PersonaModelsBlock Models { get; init; }
    [JsonPropertyName("strategies")] public required PersonaStrategies Strategies { get; init; }
    [JsonPropertyName("features")] public required PersonaFeatures Features { get; init; }
    [JsonPropertyName("ui")] public required PersonaUi Ui { get; init; }

    /// <summary>Not part of the JSON, populated by PersonaCatalog once the pack's
    /// menu/menuItems.json has been separately loaded and validated.</summary>
    [JsonIgnore]
    public PersonaMenu Menu { get; init; } = null!;
}

public sealed record PersonaLocales
{
    [JsonPropertyName("default")] public required string Default { get; init; }
    [JsonPropertyName("supported")] public required List<string> Supported { get; init; }
}

public sealed record PersonaStore
{
    [JsonPropertyName("timezone")] public required string Timezone { get; init; }
}

public sealed record PersonaVoice
{
    [JsonPropertyName("default")] public required string Default { get; init; }
}

public sealed record PersonaSearch
{
    [JsonPropertyName("indexName")] public required string IndexName { get; init; }
    [JsonPropertyName("contentFields")] public required List<string> ContentFields { get; init; }
}

public sealed record PersonaPricing
{
    [JsonPropertyName("taxRate")] public required string TaxRate { get; init; }
    [JsonPropertyName("happyHour")] public PersonaHappyHour? HappyHour { get; init; }
}

public sealed record PersonaHappyHour
{
    [JsonPropertyName("startHour")] public required int StartHour { get; init; }
    [JsonPropertyName("endHour")] public required int EndHour { get; init; }
    [JsonPropertyName("priceMultiplier")] public required string PriceMultiplier { get; init; }
    [JsonPropertyName("announce")] public required bool Announce { get; init; }
    [JsonPropertyName("banner")] public required string Banner { get; init; }
}

public sealed record PersonaSizes
{
    [JsonPropertyName("canonical")] public required Dictionary<string, string> Canonical { get; init; }
    [JsonPropertyName("aliases")] public required Dictionary<string, string> Aliases { get; init; }
    [JsonPropertyName("spokenAs")] public required Dictionary<string, string> SpokenAs { get; init; }
    [JsonPropertyName("hidden")] public required List<string> Hidden { get; init; }
    [JsonPropertyName("default")] public string? Default { get; init; }
}

public sealed record PersonaBundles
{
    [JsonPropertyName("nameMarkers")] public required List<string> NameMarkers { get; init; }
    [JsonPropertyName("convertStandalone")] public required bool ConvertStandalone { get; init; }
    [JsonPropertyName("missingPartText")] public required Dictionary<string, string> MissingPartText { get; init; }
}

public sealed record PersonaExtras
{
    [JsonPropertyName("allowedBaseCategories")] public required List<string> AllowedBaseCategories { get; init; }
    [JsonPropertyName("blockedBaseCategories")] public required List<string> BlockedBaseCategories { get; init; }
    [JsonPropertyName("splitCombinedNames")] public required bool SplitCombinedNames { get; init; }
}

public sealed record PersonaModelsBlock
{
    [JsonPropertyName("realtime")] public required PersonaModelPipeline Realtime { get; init; }
    [JsonPropertyName("cascade")] public PersonaModelPipeline? Cascade { get; init; }
    [JsonPropertyName("local")] public PersonaModelPipeline? Local { get; init; }
}

public sealed record PersonaModelPipeline
{
    [JsonPropertyName("default")] public required string Default { get; init; }
    [JsonPropertyName("allowed")] public required List<string> Allowed { get; init; }
}

public sealed record PersonaStrategies
{
    [JsonPropertyName("searchQueryRewrite")] public required string SearchQueryRewrite { get; init; }
}

public sealed record PersonaFeatures
{
    [JsonPropertyName("dayparts")] public required bool Dayparts { get; init; }
}

public sealed record PersonaUi
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("theme")] public required PersonaTheme Theme { get; init; }
    [JsonPropertyName("assets")] public required PersonaAssets Assets { get; init; }
    [JsonPropertyName("strings")] public required Dictionary<string, Dictionary<string, string>> Strings { get; init; }
    [JsonPropertyName("hero")] public required PersonaHero Hero { get; init; }
    [JsonPropertyName("legal")] public required string Legal { get; init; }
}

public sealed record PersonaTheme
{
    [JsonPropertyName("light")] public required PersonaThemeTokens Light { get; init; }
    [JsonPropertyName("dark")] public PersonaThemeTokens? Dark { get; init; }
    [JsonPropertyName("font")] public PersonaThemeFont? Font { get; init; }
}

public sealed record PersonaThemeFont
{
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("importUrl")] public string? ImportUrl { get; init; }
}

public sealed record PersonaThemeTokens
{
    [JsonPropertyName("primary")] public string? Primary { get; init; }
    [JsonPropertyName("secondary")] public string? Secondary { get; init; }
    [JsonPropertyName("background")] public string? Background { get; init; }
    [JsonPropertyName("foreground")] public string? Foreground { get; init; }
    [JsonPropertyName("accents")] public PersonaThemeAccents? Accents { get; init; }
}

/// <summary>Optional extended brand accent palette (issue #80 F2). Every key optional.</summary>
public sealed record PersonaThemeAccents
{
    [JsonPropertyName("primaryHex")] public string? PrimaryHex { get; init; }
    [JsonPropertyName("primaryStrong")] public string? PrimaryStrong { get; init; }
    [JsonPropertyName("primaryLight")] public string? PrimaryLight { get; init; }
    [JsonPropertyName("primaryTintOnDark")] public string? PrimaryTintOnDark { get; init; }
    [JsonPropertyName("secondaryHex")] public string? SecondaryHex { get; init; }
    [JsonPropertyName("secondaryStrong")] public string? SecondaryStrong { get; init; }
    [JsonPropertyName("secondaryTintOnDark")] public string? SecondaryTintOnDark { get; init; }
    [JsonPropertyName("accent")] public string? Accent { get; init; }
    [JsonPropertyName("accentLight")] public string? AccentLight { get; init; }
    [JsonPropertyName("ink")] public string? Ink { get; init; }
    [JsonPropertyName("surfaceTint")] public string? SurfaceTint { get; init; }
    [JsonPropertyName("surfaceDark")] public string? SurfaceDark { get; init; }
    [JsonPropertyName("surfaceDarkAlt")] public string? SurfaceDarkAlt { get; init; }
    [JsonPropertyName("success")] public string? Success { get; init; }
    [JsonPropertyName("neutral")] public string? Neutral { get; init; }
}

public sealed record PersonaAssets
{
    [JsonPropertyName("logo")] public required string Logo { get; init; }
    [JsonPropertyName("favicon")] public required string Favicon { get; init; }
    [JsonPropertyName("apologyClip")] public string? ApologyClip { get; init; }
}

public sealed record PersonaHero
{
    [JsonPropertyName("headline")] public required string Headline { get; init; }
    [JsonPropertyName("callouts")] public required List<string> Callouts { get; init; }
}
