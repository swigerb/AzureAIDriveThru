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

    // Issue #304: optional persona-level phonetic lexicon (e.g. {"Widget": "Wid-jet"}),
    // consulted ONLY by the cascade pipeline's SpeakAsync (never by the realtime model, never
    // applied to order/search/display data). Mirrors persona_loader.py's
    // PersonaManifest.pronunciations.
    [JsonPropertyName("pronunciations")] public Dictionary<string, string>? Pronunciations { get; init; }
    [JsonPropertyName("machines")] public required Dictionary<string, PersonaMachine> Machines { get; init; }
    [JsonPropertyName("models")] public required PersonaModelsBlock Models { get; init; }
    [JsonPropertyName("strategies")] public required PersonaStrategies Strategies { get; init; }
    [JsonPropertyName("features")] public required PersonaFeatures Features { get; init; }
    [JsonPropertyName("ui")] public required PersonaUi Ui { get; init; }

    /// <summary>Not part of the JSON, populated by PersonaCatalog once the pack's
    /// menu/menuItems.json has been separately loaded and validated.</summary>
    [JsonIgnore]
    public PersonaMenu Menu { get; init; } = null!;

    // Not part of the JSON -- populated by PersonaCatalog.LoadOnePersona alongside Menu, mirroring
    // persona_loader.py's Persona.assets_dir/menu_path/prompts_dir (issue #12 part 2). These are
    // the traversal boundary/content-hash inputs for the persona asset/menu HTTP routes and the
    // per-persona PromptLoader, resolved once at startup rather than recomputed per request.
    [JsonIgnore] public string PackDir { get; init; } = null!;
    [JsonIgnore] public string AssetsDir { get; init; } = null!;
    [JsonIgnore] public string MenuPath { get; init; } = null!;
    [JsonIgnore] public string PromptsDir { get; init; } = null!;
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
    // PR #184 round 2 (Rick's review, item 1): which of this pack's OWN pricing rules a bundle
    // slot-fill/resize follows -- "includedAnySize" (default; a slot filled/resized to ANY size
    // never changes what the bundle itself costs), "wholeBundleSize" (a slot resize re-prices
    // the WHOLE bundle from its own per-size menu data and relabels every other filled slot to
    // match), or "componentUpcharge" (bundle price plus positive component deltas over
    // IncludedSize). Optional with defaults so every existing pack's persona.json needs no changes.
    [JsonPropertyName("resizeRule")] public string ResizeRule { get; init; } = "includedAnySize";
    [JsonPropertyName("includedSize")] public string? IncludedSize { get; init; }
}

public sealed record PersonaExtras
{
    [JsonPropertyName("allowedBaseCategories")] public required List<string> AllowedBaseCategories { get; init; }
    [JsonPropertyName("blockedBaseCategories")] public required List<string> BlockedBaseCategories { get; init; }
    [JsonPropertyName("splitCombinedNames")] public required bool SplitCombinedNames { get; init; }
}

/// <summary>#77: each pack owns its own guest-facing out-of-stock label, so a machine going
/// down doesn't fall back to a hardcoded, Sonic-only apology (mirrors persona_loader.py's
/// _Machine).</summary>
public sealed record PersonaMachine
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
}

public sealed record PersonaModelsBlock
{
    [JsonPropertyName("realtime")] public required PersonaModelPipeline Realtime { get; init; }
    [JsonPropertyName("cascade")] public PersonaModelPipeline? Cascade { get; init; }
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
    [JsonPropertyName("sessionBar")] public PersonaSessionBar? SessionBar { get; init; }
    /// <summary>Issue #164 E1: optional menu-category-name -> emoji/icon override, keyed by the
    /// category's exact menuItems.json name. Lets a pack whose menu data is shared territory
    /// (issue #165's menu-data work) restore its original category icons WITHOUT editing menuItems.json.</summary>
    [JsonPropertyName("categoryIcons")] public Dictionary<string, string>? CategoryIcons { get; init; }
    /// <summary>Issue #164 R2 (PR #167 round 1 review): optional per-slot override of which
    /// brand role a handful of shared text elements draw their color from. Any key a pack omits
    /// falls back to the shared default (badge=primary, countChip=secondary,
    /// footerTagline=secondary, footerExtra=secondary) -- see the frontend's
    /// `lib/personaTextRoles.ts`.</summary>
    [JsonPropertyName("textRoles")] public PersonaTextRoles? TextRoles { get; init; }
}

/// <summary>Issue #164 R2 (PR #167 round 1 review): which brand role a shared text element's
/// color is drawn from. Each field is one of "primary" | "primaryDeep" | "secondary" | "accent" |
/// "ink" (enforced by the JSON Schema `enum`, not re-validated here -- see the file banner).</summary>
public sealed record PersonaTextRoles
{
    [JsonPropertyName("badge")] public string? Badge { get; init; }
    [JsonPropertyName("countChip")] public string? CountChip { get; init; }
    [JsonPropertyName("footerTagline")] public string? FooterTagline { get; init; }
    [JsonPropertyName("footerExtra")] public string? FooterExtra { get; init; }
}

/// <summary>Issue #164 C3/E4: 'plain' (default) is the mono session-token bar most originals
/// used, which follows the page's light/dark mode. 'chips' is one original's
/// colored pill-chip bar, which always stays on its light background regardless of page
/// theme.</summary>
public sealed record PersonaSessionBar
{
    [JsonPropertyName("variant")] public string? Variant { get; init; }
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
    [JsonPropertyName("surface")] public PersonaThemeSurface? Surface { get; init; }
    [JsonPropertyName("menuSurface")] public PersonaThemeMenuSurface? MenuSurface { get; init; }
}

/// <summary>Optional extended brand accent palette (issue #80 F2). Every key optional.</summary>
public sealed record PersonaThemeAccents
{
    [JsonPropertyName("primaryHex")] public string? PrimaryHex { get; init; }
    [JsonPropertyName("primaryStrong")] public string? PrimaryStrong { get; init; }
    [JsonPropertyName("primaryLight")] public string? PrimaryLight { get; init; }
    /// <summary>Issue #164 R2 (PR #167 round 1 review): deeper/darker shade of `primary`,
    /// distinct from `primaryStrong`, for text needing more contrast than the plain primary hex.
    /// Defaults to `primaryHex` (see the frontend's `personaTheme.ts::deriveAccents`) so a pack
    /// that doesn't author this key renders unchanged.</summary>
    [JsonPropertyName("primaryDeep")] public string? PrimaryDeep { get; init; }
    [JsonPropertyName("primaryTintOnDark")] public string? PrimaryTintOnDark { get; init; }
    [JsonPropertyName("secondaryHex")] public string? SecondaryHex { get; init; }
    [JsonPropertyName("secondaryStrong")] public string? SecondaryStrong { get; init; }
    /// <summary>Issue #164 R3 (PR #167 round 1 review): lighter shade of `secondary`, for
    /// pill/gradient accents brighter than `secondaryStrong`. Defaults to `secondaryStrong` (see
    /// the frontend's `personaTheme.ts::deriveAccents`) so a pack that doesn't author this key
    /// renders unchanged.</summary>
    [JsonPropertyName("secondaryLight")] public string? SecondaryLight { get; init; }
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

/// <summary>Optional shadcn-style UI slot palette (issue #117). Every key optional; the same shape
/// is reused for both `light.surface` and `dark.surface` (see PersonaThemeTokens.Surface).</summary>
public sealed record PersonaThemeSurface
{
    [JsonPropertyName("cardForeground")] public string? CardForeground { get; init; }
    [JsonPropertyName("secondary")] public string? Secondary { get; init; }
    [JsonPropertyName("secondaryForeground")] public string? SecondaryForeground { get; init; }
    [JsonPropertyName("muted")] public string? Muted { get; init; }
    [JsonPropertyName("mutedForeground")] public string? MutedForeground { get; init; }
    [JsonPropertyName("accent")] public string? Accent { get; init; }
    [JsonPropertyName("accentForeground")] public string? AccentForeground { get; init; }
    [JsonPropertyName("destructive")] public string? Destructive { get; init; }
    [JsonPropertyName("border")] public string? Border { get; init; }
    [JsonPropertyName("chart2")] public string? Chart2 { get; init; }
    [JsonPropertyName("chart3")] public string? Chart3 { get; init; }
    [JsonPropertyName("chart4")] public string? Chart4 { get; init; }
    [JsonPropertyName("chart5")] public string? Chart5 { get; init; }
}

/// <summary>Issue #169 (Rick's PR #167 round 3 review, item N13): optional pack-level menu-card/
/// header surface tokens, layered on top of the shadcn <see cref="PersonaThemeSurface"/> slots
/// above. Every key is optional and the same shape is reused for both `light.menuSurface` and
/// `dark.menuSurface` (same convention as <see cref="PersonaThemeSurface"/>) even though a mode
/// only ever sets a subset -- a pack that omits the block entirely still deserializes and simply
/// renders the shared neutral defaults app/frontend/src/index.css falls back to.</summary>
public sealed record PersonaThemeMenuSurface
{
    [JsonPropertyName("categoryCardBackground")] public string? CategoryCardBackground { get; init; }
    [JsonPropertyName("itemCardBackground")] public string? ItemCardBackground { get; init; }
    [JsonPropertyName("categoryTitleColor")] public string? CategoryTitleColor { get; init; }
}

public sealed record PersonaAssets
{
    [JsonPropertyName("logo")] public required string Logo { get; init; }
    [JsonPropertyName("favicon")] public required string Favicon { get; init; }
    [JsonPropertyName("apologyClip")] public string? ApologyClip { get; init; }
    /// <summary>Issue #164 B2: true for a pack whose original PNG logo has an opaque white
    /// background and was shown on a white rounded tile -- the frontend renders that tile only
    /// when this is set, so a transparent-background SVG logo isn't given one.</summary>
    [JsonPropertyName("logoTile")] public bool? LogoTile { get; init; }
}

public sealed record PersonaHero
{
    [JsonPropertyName("headline")] public required string Headline { get; init; }
    /// <summary>Issue #164 A5: overrides the shared neutral "Voice Ordering Demo" hero pill
    /// copy. Omit to use the shared default.</summary>
    [JsonPropertyName("badge")] public string? Badge { get; init; }
    /// <summary>Issue #164 A4: the persona's own hero sub-headline sentence.</summary>
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("callouts")] public required List<PersonaHeroCallout> Callouts { get; init; }
    [JsonPropertyName("spotlight")] public required List<PersonaHeroSpotlight> Spotlight { get; init; }
}

/// <summary>Issue #164 A3: one of the hero's three compact callout pills. `Tone` names which of
/// the persona's own theme roles (primary/secondary/accent) the pill's gradient is drawn from --
/// the shared component stays brand-free by never hard-coding a color itself.</summary>
public sealed record PersonaHeroCallout
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
    [JsonPropertyName("tone")] public required string Tone { get; init; }
}

/// <summary>Issue #164 A1/A2: one of the hero's two spotlight cards. The first card shape uses
/// `Rows` (label/value pairs); the second instead pairs a `Body` sentence with an `Accent`
/// pairing suggestion -- matching each original's two distinct card layouts.</summary>
public sealed record PersonaHeroSpotlight
{
    [JsonPropertyName("icon")] public required string Icon { get; init; }
    [JsonPropertyName("kicker")] public required string Kicker { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("rows")] public List<PersonaHeroSpotlightRow>? Rows { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("accent")] public string? Accent { get; init; }
    // Issue #164 A2: which brand role the second ("body") card's border/wash/kicker draw from.
    // Defaults to "secondary" in the frontend when omitted.
    [JsonPropertyName("tone")] public string? Tone { get; init; }
    // Issue #164 R4 (PR #167 round 1 review): which brand role the second ("body") card's accent
    // pairing-line text is drawn from, independent of `Tone` above. Defaults to the tone-derived
    // mapping already used before this field existed, so a pack that omits it renders unchanged.
    [JsonPropertyName("accentTone")] public string? AccentTone { get; init; }
    // Issue #164 A2: optional hex wash for this card's background (e.g. one original's tinted
    // beverage card). Omit for the shared neutral surface.
    [JsonPropertyName("tint")] public string? Tint { get; init; }
}

public sealed record PersonaHeroSpotlightRow
{
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
    // Issue #164 R4 (PR #167 round 1 review): which brand role this row's value text is drawn
    // from. Defaults to "primary" in the frontend when omitted, matching each original's own
    // per-row coloring.
    [JsonPropertyName("tone")] public string? Tone { get; init; }
}
