using System.Text.RegularExpressions;

namespace Backend.Personas;

/// <summary>Result of <see cref="MenuCatalog.ResolveMenuItem"/> -- the real, on-menu record for a
/// resolved name (mirrors menu_utils.py's <c>resolve_menu_item</c> return dict). <c>null</c> from
/// that method means "not on the menu" (issue #73's single on-menu gate); there is no keyword
/// fallback anywhere in this class.</summary>
public sealed record ResolvedMenuItem(
    string Name,
    string Category,
    IReadOnlyList<string> Sizes,
    IReadOnlyDictionary<string, decimal> Prices,
    // This item's own declared daypart ("breakfast"/"lunch"/"allDay"), or null for an item that
    // doesn't carry one (every item on a pack with no features.dayparts today). Issue 165 --
    // MenuCatalog.ItemAvailableNow below is the single reader.
    string? MenuPeriod = null);

/// <summary>
/// Port of app/backend/menu_utils.py's <c>MenuCatalog</c> class (docs/dotnet_mapping.md, design
/// doc section 6, issues #74/#73/#77). One persona's own size vocabulary + menu item
/// classification data, built once and cached per persona id by <see cref="PersonaCatalog"/> (see
/// <see cref="FromPersona"/>). There is no module-level, brand-specific fallback anywhere in this
/// class -- #73 (ADR-001 decision 4, "No off-menu"): every classification method below returns its
/// safe default (<c>""</c>/<c>false</c>/empty collection/<c>null</c>) for a name that doesn't
/// resolve to a real <c>menu/menuItems.json</c> entry (or one of its own <c>aliases</c>), never a
/// keyword/substring guess. <see cref="ResolveMenuItem"/> is the single on-menu gate
/// <c>Tools.UpdateOrder</c> calls before adding anything to an order.
/// </summary>
public sealed class MenuCatalog
{
    private sealed record ItemFields(
        string Name,
        string Category,
        string ComboSlot,
        bool HappyHourDiscounted,
        IReadOnlyList<string> BundleSlots,
        string? RequiresMachine,
        bool IsExtra,
        IReadOnlyList<string> Sizes,
        IReadOnlyDictionary<string, decimal> Prices,
        IReadOnlyDictionary<string, string> BundleAutoFill,
        string? BundleDefaultSize,
        string? MealNumber,
        string? MenuPeriod);

    // Punctuation ignored when compacting a size string for alias lookup (PR #50 review
    // follow-up): "Route-44" and "rt. 44" must resolve identically to "route44"/"rt44" --
    // whitespace alone wasn't enough to catch the hyphen or period variants.
    private const string SizeAliasIgnoredChars = " .-";

    public string PersonaId { get; }

    private readonly IReadOnlyDictionary<string, string> _sizeMap;
    private readonly IReadOnlyDictionary<string, string> _sizeAliases;
    private readonly IReadOnlySet<string> _hiddenSizes;
    private readonly IReadOnlyDictionary<string, string> _spokenAs;
    private readonly IReadOnlyDictionary<string, ItemFields> _itemFields;
    private readonly IReadOnlyDictionary<string, string> _aliasMap;
    private readonly IReadOnlyDictionary<string, string> _categoryMap;
    private readonly IReadOnlyDictionary<string, (string Status, string Label)> _machines;

    public IReadOnlySet<string> AllowedExtraCategories { get; }
    public IReadOnlySet<string> BlockedExtraCategories { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InvalidModifiers { get; }
    public IReadOnlyList<string> BundleNameMarkers { get; }
    public bool BundleConvertStandalone { get; }
    public IReadOnlyDictionary<string, string> BundleMissingPartText { get; }
    // PR #184 round 2 (Rick's review, item 1): this persona's own bundle slot-fill/resize
    // pricing rule ("includedAnySize" default, or "wholeBundleSize") -- see
    // OrderState.FillBundleComponent/ApplyWholeBundleResize.
    public string BundleResizeRule { get; }
    public bool SplitCombinedNames { get; }
    public string SearchQueryRewrite { get; }

    /// <summary>This persona's own canonical-size-key -> display-label map (persona.json
    /// <c>sizes.canonical</c>) -- mirrors menu_utils.py's <c>size_map</c> property, used by
    /// tools.py's <c>update_order</c> to list a rejected item's real available sizes by their
    /// display labels (e.g. <c>"Medium"</c>, not the raw key <c>"medium"</c>) in a
    /// size_not_available rejection.</summary>
    public IReadOnlyDictionary<string, string> SizeMap => _sizeMap;

    /// <summary>This persona's own normalized-item-key -> lower-cased category map -- mirrors
    /// menu_utils.py's public <c>category_map</c> attribute (parity gap closed for #165 round 2,
    /// Rick's review item 4: lets a test enumerate this pack's own real category set, e.g. to
    /// verify every <c>hints.yaml</c> <c>trigger_categories</c> entry actually resolves to one of
    /// them, the same way the Python pack-lint test does).</summary>
    public IReadOnlyDictionary<string, string> CategoryMap => _categoryMap;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _mealNumberIndex;

    private MenuCatalog(
        string personaId,
        IReadOnlyDictionary<string, string> sizeMap,
        IReadOnlyDictionary<string, string> sizeAliases,
        IReadOnlySet<string> hiddenSizes,
        IReadOnlyDictionary<string, string> spokenAs,
        IReadOnlyDictionary<string, ItemFields> itemFields,
        IReadOnlyDictionary<string, string> aliasMap,
        IReadOnlyDictionary<string, (string, string)> machines,
        IReadOnlySet<string> allowedExtraCategories,
        IReadOnlySet<string> blockedExtraCategories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> invalidModifiers,
        IReadOnlyList<string> bundleNameMarkers,
        bool bundleConvertStandalone,
        IReadOnlyDictionary<string, string> bundleMissingPartText,
        string bundleResizeRule,
        bool splitCombinedNames,
        string searchQueryRewrite)
    {
        PersonaId = personaId;
        _sizeMap = sizeMap;
        _sizeAliases = sizeAliases;
        _hiddenSizes = hiddenSizes;
        _spokenAs = spokenAs;
        _itemFields = itemFields;
        _aliasMap = aliasMap;
        _categoryMap = itemFields.ToDictionary(kv => kv.Key, kv => kv.Value.Category);
        _machines = machines;
        AllowedExtraCategories = allowedExtraCategories;
        BlockedExtraCategories = blockedExtraCategories;
        InvalidModifiers = invalidModifiers;
        BundleNameMarkers = bundleNameMarkers;
        BundleConvertStandalone = bundleConvertStandalone;
        BundleMissingPartText = bundleMissingPartText;
        BundleResizeRule = bundleResizeRule;
        SplitCombinedNames = splitCombinedNames;
        SearchQueryRewrite = searchQueryRewrite;

        var mealNumberIndex = new Dictionary<string, List<string>>();
        foreach (var fields in itemFields.Values)
        {
            if (string.IsNullOrEmpty(fields.MealNumber))
            {
                continue;
            }
            if (!mealNumberIndex.TryGetValue(fields.MealNumber, out var names))
            {
                names = [];
                mealNumberIndex[fields.MealNumber] = names;
            }
            names.Add(fields.Name);
        }
        _mealNumberIndex = mealNumberIndex.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>Builds *persona*'s own <see cref="MenuCatalog"/> from its already-loaded
    /// <see cref="Persona.Menu"/> and persona.json blocks. Mirrors menu_utils.py's
    /// <c>MenuCatalog.from_persona</c> + <c>_load_menu_data</c>. Raises
    /// <see cref="PersonaValidationException"/> (via <see cref="MenuKeyValidator.ValidateNoCollisions"/>)
    /// if two menu items or aliases normalize to the same lookup key (issue #128) -- called again
    /// here (in addition to <see cref="PersonaCatalog"/>'s own startup call) so this class can
    /// never be constructed from a colliding pack even if some future caller skips startup
    /// validation.</summary>
    public static MenuCatalog FromPersona(Persona persona)
    {
        var sizesCfg = persona.Sizes;
        var sizeMap = new Dictionary<string, string>(sizesCfg.Canonical);
        var sizeAliases = new Dictionary<string, string>(sizesCfg.Aliases);
        var hiddenSizes = sizesCfg.Hidden.Select(s => (s ?? "").Trim().ToLowerInvariant()).ToHashSet();
        var spokenAs = new Dictionary<string, string>(sizesCfg.SpokenAs);

        string SizeKeyFn(string size) => CanonicalSizeKeyFor(size, sizeAliases, hiddenSizes);

        MenuKeyValidator.ValidateNoCollisions(persona.Menu, persona.Id, persona.MenuPath);

        var itemFields = new Dictionary<string, ItemFields>();
        var aliasMap = new Dictionary<string, string>();
        foreach (var category in persona.Menu.MenuItems)
        {
            var categoryName = (category.Category ?? "").Trim().ToLowerInvariant();
            foreach (var item in category.Items)
            {
                if (string.IsNullOrEmpty(item.Name))
                {
                    continue;
                }

                var key = MenuKeyValidator.MenuKey(item.Name);
                var sizeKeys = item.Sizes.Select(s => SizeKeyFn(s.Size)).Distinct().ToList();
                var prices = new Dictionary<string, decimal>();
                foreach (var s in item.Sizes)
                {
                    prices[SizeKeyFn(s.Size)] = s.Price;
                }

                var bundleAutoFill = item.Bundle?.AutoFill is { } af
                    ? new Dictionary<string, string>(af)
                    : new Dictionary<string, string>();

                itemFields[key] = new ItemFields(
                    Name: item.Name,
                    Category: categoryName,
                    ComboSlot: item.ComboSlot,
                    HappyHourDiscounted: item.HappyHourDiscounted,
                    BundleSlots: item.Bundle?.Slots ?? [],
                    RequiresMachine: item.RequiresMachine,
                    IsExtra: item.IsExtra,
                    Sizes: sizeKeys,
                    Prices: prices,
                    BundleAutoFill: bundleAutoFill,
                    BundleDefaultSize: item.Bundle?.DefaultSize,
                    MealNumber: item.MealNumber,
                    MenuPeriod: item.MenuPeriod);

                foreach (var alias in item.Aliases)
                {
                    var aliasKey = MenuKeyValidator.MenuKey(alias);
                    if (!string.IsNullOrEmpty(aliasKey))
                    {
                        aliasMap[aliasKey] = key;
                    }
                }
            }
        }

        var extrasCfg = persona.Extras;
        var bundlesCfg = persona.Bundles;
        var machines = persona.Machines.ToDictionary(kv => kv.Key, kv => (kv.Value.Status, kv.Value.Label));

        return new MenuCatalog(
            persona.Id,
            sizeMap,
            sizeAliases,
            hiddenSizes,
            spokenAs,
            itemFields,
            aliasMap,
            machines,
            allowedExtraCategories: extrasCfg.AllowedBaseCategories.Select(c => (c ?? "").Trim().ToLowerInvariant()).ToHashSet(),
            blockedExtraCategories: extrasCfg.BlockedBaseCategories.Select(c => (c ?? "").Trim().ToLowerInvariant()).ToHashSet(),
            invalidModifiers: persona.InvalidModifiers.ToDictionary(
                kv => (kv.Key ?? "").Trim().ToLowerInvariant(),
                kv => (IReadOnlyList<string>)kv.Value.ToList()),
            bundleNameMarkers: bundlesCfg.NameMarkers.Select(m => (m ?? "").Trim().ToLowerInvariant())
                .Where(m => m.Length > 0).ToList(),
            bundleConvertStandalone: bundlesCfg.ConvertStandalone,
            bundleMissingPartText: new Dictionary<string, string>(bundlesCfg.MissingPartText),
            bundleResizeRule: string.IsNullOrEmpty(bundlesCfg.ResizeRule) ? "includedAnySize" : bundlesCfg.ResizeRule,
            splitCombinedNames: extrasCfg.SplitCombinedNames,
            searchQueryRewrite: persona.Strategies.SearchQueryRewrite);
    }

    // #74: shared, size-vocabulary-parameterized algorithms -- mirrors menu_utils.py's
    // _compact_size_key/_normalize_size_for/_canonical_size_key_for exactly.
    private static string CompactSizeKey(string? size)
    {
        var key = (size ?? "").Trim().ToLowerInvariant();
        return new string(key.Where(ch => !SizeAliasIgnoredChars.Contains(ch)).ToArray());
    }

    private static string CanonicalSizeKeyFor(
        string? size, IReadOnlyDictionary<string, string> sizeAliases, IReadOnlySet<string> hiddenSizes)
    {
        var key = (size ?? "").Trim().ToLowerInvariant();
        if (hiddenSizes.Contains(key))
        {
            return "standard";
        }
        var compactKey = CompactSizeKey(size);
        if (sizeAliases.TryGetValue(compactKey, out var aliased))
        {
            return aliased;
        }
        return sizeAliases.GetValueOrDefault(key, key);
    }

    private static string NormalizeSizeFor(
        string? size,
        IReadOnlyDictionary<string, string> sizeMap,
        IReadOnlyDictionary<string, string> sizeAliases,
        IReadOnlySet<string> hiddenSizes)
    {
        var key = (size ?? "").Trim().ToLowerInvariant();
        if (hiddenSizes.Contains(key))
        {
            return "";
        }
        return sizeMap.GetValueOrDefault(CanonicalSizeKeyFor(size, sizeAliases, hiddenSizes), "");
    }

    public string MachineStatus(string machine) =>
        _machines.TryGetValue(machine, out var entry) ? entry.Status : "";

    /// <summary>Returns <c>null</c> (not empty string) when the pack never mentions this machine
    /// at all -- distinct from <see cref="MachineStatus"/>'s "" which callers treat as "not
    /// down".</summary>
    public string? MachineStatusOrNull(string machine) =>
        _machines.TryGetValue(machine, out var entry) ? entry.Status : null;

    public string MachineLabel(string machine) =>
        _machines.TryGetValue(machine, out var entry) ? entry.Label : $"{machine} is down";

    public string NormalizeSize(string size) => NormalizeSizeFor(size, _sizeMap, _sizeAliases, _hiddenSizes);

    public string CanonicalSizeKey(string size) => CanonicalSizeKeyFor(size, _sizeAliases, _hiddenSizes);

    private string ResolveAlias(string normalized) => _aliasMap.GetValueOrDefault(normalized, normalized);

    private static string SpokenSubstitutionPattern(string raw)
    {
        var rightBoundaryChars = raw.EndsWith("®", StringComparison.Ordinal) ||
                                 raw.EndsWith("™", StringComparison.Ordinal)
            ? "A-Za-z0-9"
            : "A-Za-z0-9®™";
        return $@"(?<![A-Za-z0-9]){Regex.Escape(raw)}(?![{rightBoundaryChars}])";
    }

    /// <summary>Applies this persona's own <c>sizes.spokenAs</c> spoken-readback substitutions to a
    /// display string (#74; Route 44 readback etc.) -- generic per-persona, never hardcoded. Longer
    /// keys run first and matches require alphanumeric/trademark boundaries so item-name hints (for
    /// example MUNCHKINS® -> Munchkins) cannot rewrite the middle of another token.</summary>
    public string Spoken(string text)
    {
        foreach (var entry in _spokenAs.OrderByDescending(kv => kv.Key.Length))
        {
            var raw = entry.Key;
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }
            text = Regex.Replace(text, SpokenSubstitutionPattern(raw), entry.Value, RegexOptions.CultureInvariant);
        }
        return text;
    }

    public string InferCategory(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _categoryMap.GetValueOrDefault(normalized, "");
    }

    public string InferComboComponent(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields))
        {
            return "";
        }
        return fields.ComboSlot is "sides" or "drinks" ? fields.ComboSlot : "";
    }

    public bool IsHappyHourDiscounted(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _itemFields.TryGetValue(normalized, out var fields) && fields.HappyHourDiscounted;
    }

    public IReadOnlyList<string> BundleSlots(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _itemFields.TryGetValue(normalized, out var fields) ? fields.BundleSlots : [];
    }

    /// <summary>PR #184 round 4 (Rick's review, item 4): this bundle item's own
    /// <c>menu.schema.json</c> <c>bundle.defaultSize</c> (e.g. "Medium" on an S/M/L meal), or
    /// <c>""</c> for an item with no bundle data or no configured default size. The single place
    /// <c>Tools.OrderToolExecutor</c>'s size-validation gate reads to map a guest's missing/
    /// "Standard" size onto the pack's real default meal size instead of rejecting it outright --
    /// see <see cref="BundleAutoFill"/>'s own, pre-existing use of this same field for the
    /// autofilled component's display label. Mirrors menu_utils.py's
    /// <c>bundle_default_size</c>.</summary>
    public string BundleDefaultSize(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _itemFields.TryGetValue(normalized, out var fields) ? fields.BundleDefaultSize ?? "" : "";
    }

    /// <summary>This bundle item's own slot -> filler-description map (menu.schema.json
    /// <c>bundle.autoFill</c>), with a literal <c>{size}</c> token in a template replaced by
    /// <paramref name="resolvedSizeLabel"/> (falling back to the bundle's own
    /// <c>bundle.defaultSize</c> when the guest ordered it with no usable size). Mirrors
    /// menu_utils.py's <c>bundle_autofill</c>.</summary>
    public IReadOnlyDictionary<string, string> BundleAutoFill(string itemName, string resolvedSizeLabel = "")
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields) || fields.BundleAutoFill.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var defaultSize = fields.BundleDefaultSize ?? "";
        var sizeLabel = string.IsNullOrEmpty(resolvedSizeLabel) || resolvedSizeLabel.Equals("standard", StringComparison.OrdinalIgnoreCase)
            ? defaultSize
            : resolvedSizeLabel;
        return fields.BundleAutoFill.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Replace("{size}", sizeLabel).Trim());
    }

    /// <summary>PR #184 round 3 (Rick's review, item E): the same slot -> filler map as
    /// <see cref="BundleAutoFill"/>, but with the literal <c>{size}</c> token (and the trailing
    /// space its template builds in) stripped rather than substituted with a real size label --
    /// the BASE, on-menu item name an autofilled slot's item field should hold (e.g. "World
    /// Famous Fries®"), independent of whatever size currently fills it. A template with no
    /// <c>{size}</c> token at all (e.g. "Hash Browns", a single-size autofill) is returned
    /// unchanged -- it was never size-baked-in to begin with. Mirrors menu_utils.py's
    /// <c>bundle_autofill_names</c>.</summary>
    public IReadOnlyDictionary<string, string> BundleAutoFillNames(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields) || fields.BundleAutoFill.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        return fields.BundleAutoFill.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Replace("{size}", "").Trim());
    }

    /// <summary>Every real menu item name that claims numbered-meal id <paramref name="number"/>
    /// (persona.json <c>mealNumber</c>) -- possibly more than one when a breakfast and a lunch
    /// meal share the same number. Empty for a persona with no numbered meals at all.</summary>
    public IReadOnlyList<string> MealNumberCandidates(string number) =>
        _mealNumberIndex.GetValueOrDefault((number ?? "").Trim(), []);

    /// <summary>Applies this persona's own <c>strategies.searchQueryRewrite</c> to
    /// <paramref name="query"/>, or returns it unchanged for any persona that doesn't opt into a
    /// named strategy. Mirrors menu_utils.py's <c>rewrite_search_query</c>.</summary>
    public string RewriteSearchQuery(string query)
    {
        if (SearchQueryRewrite != "meal_numbers")
        {
            return query;
        }
        var match = System.Text.RegularExpressions.Regex.Match(query, @"\d+");
        if (!match.Success)
        {
            return query;
        }
        var candidates = MealNumberCandidates(match.Value);
        if (candidates.Count == 0)
        {
            return query;
        }
        return $"{query} {string.Join(" ", candidates)}";
    }

    private static readonly string[] CombinedNameConnectors = [" with ", " and ", " + ", " & "];

    /// <summary>#77 (shared extras engine, <c>extras.splitCombinedNames</c>): if
    /// <paramref name="itemName"/> isn't on the menu but is really a known base item plus a known
    /// extra joined by a connector word (e.g. "Caramel Latte with Extra Shot"), returns
    /// (baseName, extraName) using each item's own real menu name -- <c>null</c> if this persona
    /// doesn't opt in, or the name doesn't split into exactly one resolvable base item + one
    /// resolvable <c>isExtra</c> item.</summary>
    public (string BaseName, string ExtraName)? TrySplitCombinedName(string itemName)
    {
        if (!SplitCombinedNames)
        {
            return null;
        }
        foreach (var connector in CombinedNameConnectors)
        {
            var idx = itemName.IndexOf(connector.Trim(), StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }
            var basePart = itemName[..idx].Trim();
            var extraPart = itemName[(idx + connector.Trim().Length)..].Trim();
            if (basePart.Length == 0 || extraPart.Length == 0)
            {
                continue;
            }
            var baseItem = ResolveMenuItem(basePart);
            if (baseItem is null || !IsExtraItem(extraPart))
            {
                continue;
            }
            var extraItem = ResolveMenuItem(extraPart);
            if (extraItem is null)
            {
                continue;
            }
            return (baseItem.Name, extraItem.Name);
        }
        return null;
    }

    public string? RequiresMachine(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _itemFields.TryGetValue(normalized, out var fields) ? fields.RequiresMachine : null;
    }

    public bool IsExtraItem(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        return _itemFields.TryGetValue(normalized, out var fields) && fields.IsExtra;
    }

    /// <summary>THE single on-menu gate (#73): resolves <paramref name="itemName"/> (or one of
    /// its own aliases) to its real menu record, or <c>null</c> if it isn't on the menu at
    /// all.</summary>
    public ResolvedMenuItem? ResolveMenuItem(string itemName)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields))
        {
            return null;
        }
        return new ResolvedMenuItem(fields.Name, fields.Category, fields.Sizes, fields.Prices, fields.MenuPeriod);
    }

    /// <summary>Issue 165: whether <paramref name="itemName"/> is orderable in
    /// <paramref name="activeMode"/> (<c>"breakfast"</c>/<c>"lunch"</c>), this session's own bound
    /// daypart -- <c>true</c> for <paramref name="activeMode"/> <c>null</c> (a persona with no
    /// <c>features.dayparts</c> at all, or an unresolved item name -- <see cref="ResolveMenuItem"/>
    /// is the right place to reject an unknown name, not this one). An item with no own
    /// <c>menuPeriod</c>, or <c>"allDay"</c>, is available in every mode a pack declares -- only an
    /// item whose own <c>menuPeriod</c> names the OTHER daypart is rejected. Mirrors
    /// menu_utils.py's <c>item_available_now</c> byte for byte.</summary>
    public bool ItemAvailableNow(string itemName, string? activeMode)
    {
        if (activeMode is null)
        {
            return true;
        }
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields))
        {
            return true;
        }
        var period = fields.MenuPeriod;
        if (string.IsNullOrEmpty(period) || period == "allDay")
        {
            return true;
        }
        return period == activeMode;
    }

    /// <summary>Rick's #74 follow-up: the unit price for <paramref name="itemName"/> at
    /// <paramref name="sizeKey"/>, straight from this persona's own menu record -- <c>null</c> if
    /// the item or that size isn't on the menu. THE single choke point <c>Tools.UpdateOrder</c>
    /// charges from; the tool call's own <c>price</c> argument is never trusted (#104).</summary>
    public decimal? PriceFor(string itemName, string sizeKey)
    {
        var normalized = ResolveAlias(MenuKeyValidator.MenuKey(itemName));
        if (!_itemFields.TryGetValue(normalized, out var fields))
        {
            return null;
        }
        return fields.Prices.GetValueOrDefault(sizeKey) is var price && fields.Prices.ContainsKey(sizeKey)
            ? price
            : null;
    }
}
