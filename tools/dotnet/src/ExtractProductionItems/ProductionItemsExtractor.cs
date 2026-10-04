using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExtractProductionItems;

/// <summary>One size/price variant of a production item (<c>get_size_variants</c>'s return shape).</summary>
public sealed record SizeVariant(string Size, double Price);

/// <summary>One structured production item, matching <c>extract_production_items()</c>'s per-item dict.</summary>
public sealed record ProductionItem(
    string ProductId,
    string Name,
    string Description,
    string ImageUrl,
    string Category,
    IReadOnlyList<SizeVariant> Sizes);

/// <summary>One UI menu item, matching <c>load_ui_items()</c>'s per-item dict (name + its group's category).</summary>
public sealed record UiItem(string Name, string Category);

/// <summary>
/// Faithful C# port of scripts/extract_production_items.py (issue #16 batch 1): a read-only
/// report tool, not a redesign. Every public method here mirrors one of the Python script's
/// top-level functions 1:1, preserving the exact iteration-order semantics the real report's
/// byte-for-byte output depends on (see each method's remarks) -- proven against the real Python
/// twin by ExtractProductionItems.Tests/PythonParityTests.cs.
/// </summary>
public static class ProductionItemsExtractor
{
    private static readonly (string Prefix, string Label)[] SizePrefixes =
    [
        ("Mini ", "Mini"),
        ("Sm ", "Small"),
        ("Med ", "Medium"),
        ("Lg ", "Large"),
        ("RT 44", "RT 44"),
        ("Rt. 44", "RT 44"),
    ];

    private static readonly Regex NonAlphanumericSpace = new("[^a-z0-9 ]", RegexOptions.Compiled);

    /// <summary>
    /// Port of <c>extract_production_items()</c>: walks the production POS export's category tree
    /// to build the flat, de-duplicated (first-category-wins) product list, skipping recipes and
    /// falling back to a single "Standard" size when a product has no resolvable size variants.
    /// </summary>
    public static IReadOnlyList<ProductionItem> ExtractProductionItems(string productionPath)
    {
        using var stream = File.OpenRead(productionPath);
        using var doc = JsonDocument.Parse(stream);

        // list(raw_data["menus"].values())[0] -- the FIRST value in the "menus" object's
        // document order, not a specific named key.
        JsonElement menu = default;
        var foundMenu = false;
        foreach (var menuProp in doc.RootElement.GetProperty("menus").EnumerateObject())
        {
            menu = menuProp.Value;
            foundMenu = true;
            break;
        }
        if (!foundMenu)
        {
            throw new InvalidOperationException($"'{productionPath}' has no entries under \"menus\".");
        }

        var products = BuildLookup(menu.GetProperty("products"));
        var categoriesElement = menu.GetProperty("categories");
        var categories = BuildLookup(categoriesElement);
        var productGroups = BuildLookup(menu.GetProperty("productGroups"));

        // Find top-level categories: categories.keys() filtered by set membership, preserving
        // categories.keys()'s own document order (the filter itself is order-irrelevant).
        var childCatIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catProp in categoriesElement.EnumerateObject())
        {
            foreach (var childRef in EnumerateChildRefs(catProp.Value))
            {
                if (childRef.StartsWith("categories.", StringComparison.Ordinal))
                {
                    childCatIds.Add(childRef["categories.".Length..]);
                }
            }
        }
        var topLevelCats = new List<string>();
        foreach (var catProp in categoriesElement.EnumerateObject())
        {
            if (!childCatIds.Contains(catProp.Name))
            {
                topLevelCats.Add(catProp.Name);
            }
        }

        // product_category_map: first occurrence (across ALL top-level categories, walked in
        // order, each recursing depth-first through childRefs in document order) wins -- this
        // becomes both the de-duplication rule AND the final list's order.
        var productCategoryMap = new List<(string ProductId, string CategoryName)>();
        var seenProductIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catId in topLevelCats)
        {
            foreach (var (productId, leafCategoryName) in CollectProductsFromCategory(catId, categories))
            {
                if (seenProductIds.Add(productId))
                {
                    productCategoryMap.Add((productId, leafCategoryName));
                }
            }
        }

        var structured = new List<ProductionItem>();
        foreach (var (productId, categoryName) in productCategoryMap)
        {
            if (!products.TryGetValue(productId, out var product))
            {
                continue;
            }
            if (product.TryGetProperty("isRecipe", out var isRecipe) && IsTruthy(isRecipe))
            {
                continue;
            }

            var sizeVariants = GetSizeVariants(product, products, productGroups);
            if (sizeVariants.Count == 0)
            {
                sizeVariants = [new SizeVariant("Standard", GetDoubleOrDefault(product, "price", 0.0))];
            }

            structured.Add(new ProductionItem(
                ProductId: productId,
                Name: StringOrDefault(product, "displayName", productId),
                Description: StringOrDefault(product, "description", ""),
                ImageUrl: StringOrDefault(product, "imageUrl", ""),
                Category: categoryName,
                Sizes: sizeVariants));
        }

        return structured;
    }

    /// <summary>
    /// Port of <c>load_ui_items()</c>: flattens the UI menu file's category groups into a flat
    /// (name, category) list, in document order. Uses direct property access (not a "get with
    /// default"), matching the Python twin's plain <c>group["category"]</c>/<c>item["name"]</c>
    /// indexing -- a missing key is a malformed-fixture bug in both, not a value to paper over.
    /// </summary>
    public static IReadOnlyList<UiItem> LoadUiItems(string menuPath)
    {
        using var stream = File.OpenRead(menuPath);
        using var doc = JsonDocument.Parse(stream);

        var items = new List<UiItem>();
        if (doc.RootElement.TryGetProperty("menuItems", out var menuItems) && menuItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in menuItems.EnumerateArray())
            {
                var category = group.GetProperty("category").GetString()!;
                foreach (var item in group.GetProperty("items").EnumerateArray())
                {
                    items.Add(new UiItem(item.GetProperty("name").GetString()!, category));
                }
            }
        }
        return items;
    }

    /// <summary>
    /// Port of <c>normalize_size_name()</c>: known verbose-display-name prefixes map to a fixed
    /// label; otherwise strips the parent product's own display name as a substring (and the
    /// registered-trademark symbol), falling back to "Standard" when nothing distinctive remains.
    /// Guards against <paramref name="parentDisplayName"/> being empty (<see cref="string.Replace(string,string)"/>
    /// throws <see cref="ArgumentException"/> for an empty <c>oldValue</c>, unlike Python's
    /// <c>str.replace("", "")</c>, which is a harmless no-op) -- same final string either way.
    /// </summary>
    public static string NormalizeSizeName(string childDisplayName, string parentDisplayName)
    {
        foreach (var (prefix, label) in SizePrefixes)
        {
            if (childDisplayName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return label;
            }
        }

        var stripped = string.IsNullOrEmpty(parentDisplayName)
            ? childDisplayName
            : childDisplayName.Replace(parentDisplayName, "");
        stripped = stripped.Trim();
        stripped = stripped.Replace("\u00ae", "").Trim();
        return stripped.Length > 0 ? stripped : "Standard";
    }

    /// <summary>
    /// Port of <c>normalize()</c>: lowercase, strip trademark symbols, collapse anything outside
    /// [a-z0-9 ] to whitespace, then collapse/trim whitespace runs -- used only for the UI-vs-
    /// production gap-analysis name comparison, never for display.
    /// </summary>
    public static string Normalize(string name)
    {
        var lowered = name.ToLowerInvariant().Replace("\u00ae", "").Replace("\u2122", "");
        var scrubbed = NonAlphanumericSpace.Replace(lowered, " ");
        var tokens = scrubbed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", tokens);
    }

    /// <summary>
    /// Port of <c>main()</c>'s report-printing body (minus the final "return data for
    /// programmatic use" dict, which nothing reads). Returns one "display line" per line of
    /// output, exactly as Python's <c>print()</c> calls would emit them (embedded <c>\n</c>s in an
    /// f-string split across multiple display lines, same as a literal multi-line print) -- the
    /// caller is expected to write each line with its own line terminator (<c>TextWriter.WriteLine</c>),
    /// matching how <see cref="UpdateMenuSizes.CliRunner"/>'s <c>UpdateResult.Log</c> is consumed.
    /// </summary>
    public static IReadOnlyList<string> BuildReport(
        IReadOnlyList<ProductionItem> production, IReadOnlyList<UiItem> uiItems)
    {
        var lines = new List<string>();
        void Append(string s) => lines.AddRange(s.Split('\n'));

        Append(new string('=', 70));
        Append("  PRODUCTION ITEMS IN AZURE AI SEARCH INDEX");
        Append("  (Extracted using same logic as sonic_menu_ingestion_search.ipynb)");
        Append(new string('=', 70));

        // by_category (grouping, preserving within-group insertion order = production's own
        // order) and cat_counts (a Counter built from `production`, whose own dict iteration
        // order is first-occurrence order) in a single pass over `production`.
        var byCategory = new Dictionary<string, List<ProductionItem>>(StringComparer.Ordinal);
        var catCounts = new List<(string Category, int Count)>();
        var catCountIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in production)
        {
            if (!byCategory.TryGetValue(item.Category, out var itemsInCategory))
            {
                itemsInCategory = [];
                byCategory[item.Category] = itemsInCategory;
            }
            itemsInCategory.Add(item);

            if (catCountIndex.TryGetValue(item.Category, out var index))
            {
                catCounts[index] = (item.Category, catCounts[index].Count + 1);
            }
            else
            {
                catCountIndex[item.Category] = catCounts.Count;
                catCounts.Add((item.Category, 1));
            }
        }

        Append($"\nTotal production items: {production.Count}");
        Append($"Total categories: {catCounts.Count}\n");

        // Counter.most_common() (no `n`) is `sorted(self.items(), key=itemgetter(1), reverse=True)`
        // -- Python's sort is documented stable EVEN with reverse=True, so ties keep cat_counts'
        // own (first-occurrence-in-`production`) relative order. LINQ's OrderByDescending is also
        // a stable sort, so sorting catCounts (itself built in first-occurrence order above)
        // reproduces the same tie-break exactly.
        foreach (var (category, count) in catCounts.OrderByDescending(c => c.Count))
        {
            Append($"\n{new string('─', 60)}");
            Append($"  {category}  ({count} items)");
            Append(new string('─', 60));

            // sorted(..., key=lambda x: x["name"]) -- Python's default string comparison is
            // ordinal (codepoint) comparison, not culture-aware.
            foreach (var item in byCategory[category].OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var sizesStr = string.Join(", ", item.Sizes.Select(FormatSize));
                Append($"  • {item.Name}");
                Append($"    Sizes: {sizesStr}");
            }
        }

        var prodNames = new HashSet<string>(production.Select(i => Normalize(i.Name)), StringComparer.Ordinal);
        var uiNames = new HashSet<string>(uiItems.Select(i => Normalize(i.Name)), StringComparer.Ordinal);
        var inUiNotProd = uiNames.Except(prodNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var inProdNotUi = prodNames.Except(uiNames).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Append("\n" + new string('=', 70));
        Append("  GAP ANALYSIS: UI menuItems.json vs Production Index");
        Append(new string('=', 70));

        Append($"\n  Production index items: {prodNames.Count}");
        Append($"  UI sidebar items:      {uiNames.Count}");
        Append($"  Overlap:               {prodNames.Intersect(uiNames).Count()}");

        Append($"\n  ⛔ In UI but NOT in production ({inUiNotProd.Count}):");
        if (inUiNotProd.Count > 0)
        {
            foreach (var n in inUiNotProd)
            {
                Append($"     • {n}");
            }
        }
        else
        {
            // Python's literal "(none — UI is clean)" contains a real em dash (U+2014) --
            // verbatim-ported third-party output text, not newly-authored prose, so it is kept
            // exactly as the Python twin writes it (not reworded to avoid an em dash).
            Append("     (none — UI is clean)");
        }

        Append($"\n  ✅ In production but NOT in UI ({inProdNotUi.Count}):");
        if (inProdNotUi.Count > 0)
        {
            foreach (var n in inProdNotUi)
            {
                Append($"     • {n}");
            }
        }
        else
        {
            Append("     (none)");
        }

        return lines;
    }

    private static string FormatSize(SizeVariant size) =>
        $"{size.Size}=${size.Price.ToString("F2", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Port of <c>collect_products_from_category()</c>: recursively walks a category's
    /// <c>childRefs</c> (in document order), descending into sub-categories and collecting
    /// (productId, leafCategoryName) pairs for direct product refs.
    /// </summary>
    private static List<(string ProductId, string LeafCategoryName)> CollectProductsFromCategory(
        string categoryId, Dictionary<string, JsonElement> categories, string? leafName = null)
    {
        if (!categories.TryGetValue(categoryId, out var category))
        {
            return [];
        }

        var currentName = !string.IsNullOrEmpty(leafName) ? leafName! : StringOrDefault(category, "displayName", categoryId);
        var result = new List<(string, string)>();
        foreach (var childRef in EnumerateChildRefs(category))
        {
            if (childRef.StartsWith("categories.", StringComparison.Ordinal))
            {
                var subCategoryId = childRef["categories.".Length..];
                var subName = categories.TryGetValue(subCategoryId, out var subCategory)
                    ? StringOrDefault(subCategory, "displayName", subCategoryId)
                    : subCategoryId;
                result.AddRange(CollectProductsFromCategory(subCategoryId, categories, subName));
            }
            else if (childRef.StartsWith("products.", StringComparison.Ordinal))
            {
                result.Add((childRef["products.".Length..], currentName));
            }
        }
        return result;
    }

    /// <summary>Port of <c>get_size_variants()</c>: resolves a product's size variants via
    /// <c>relatedProducts.alternatives</c> -&gt; <c>productGroups</c> -&gt; child products, in
    /// document order (never sorted).</summary>
    private static List<SizeVariant> GetSizeVariants(
        JsonElement product,
        Dictionary<string, JsonElement> products,
        Dictionary<string, JsonElement> productGroups)
    {
        var sizes = new List<SizeVariant>();
        if (!product.TryGetProperty("relatedProducts", out var relatedProducts) ||
            relatedProducts.ValueKind != JsonValueKind.Object ||
            !relatedProducts.TryGetProperty("alternatives", out var alternatives) ||
            alternatives.ValueKind != JsonValueKind.Object)
        {
            return sizes;
        }

        var parentDisplayName = StringOrDefault(product, "displayName", "");
        foreach (var groupRefProp in alternatives.EnumerateObject())
        {
            var groupRef = groupRefProp.Name;
            if (!groupRef.StartsWith("productGroups.", StringComparison.Ordinal))
            {
                continue;
            }
            var groupId = groupRef["productGroups.".Length..];
            if (!productGroups.TryGetValue(groupId, out var group))
            {
                continue;
            }
            foreach (var childRef in EnumerateChildRefs(group))
            {
                if (!childRef.StartsWith("products.", StringComparison.Ordinal))
                {
                    continue;
                }
                var childId = childRef["products.".Length..];
                if (!products.TryGetValue(childId, out var childProduct))
                {
                    continue;
                }
                var childDisplayName = StringOrDefault(childProduct, "displayName", childId);
                var price = GetDoubleOrDefault(childProduct, "price", 0.0);
                sizes.Add(new SizeVariant(NormalizeSizeName(childDisplayName, parentDisplayName), price));
            }
        }
        return sizes;
    }

    /// <summary><c>obj.get("childRefs", {}).keys()</c> -- the child-ref object's property names,
    /// in document order, or empty when absent/not an object.</summary>
    private static IEnumerable<string> EnumerateChildRefs(JsonElement obj)
    {
        if (obj.TryGetProperty("childRefs", out var childRefs) && childRefs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in childRefs.EnumerateObject())
            {
                yield return prop.Name;
            }
        }
    }

    /// <summary>
    /// <c>obj.get(propertyName, fallback)</c> for a string-valued property: returns
    /// <paramref name="fallback"/> when the property is absent. As a documented simplification
    /// (real POS/menu exports never do this), a property present but not a JSON string is also
    /// treated as absent, rather than replicating Python's "key present with a non-string/None
    /// value" edge case exactly.
    /// </summary>
    private static string StringOrDefault(JsonElement obj, string propertyName, string fallback) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : fallback;

    /// <summary><c>obj.get(propertyName, fallback)</c> for a numeric-valued property (price
    /// fields may be a JSON int or float in the source data -- both are read as a <see cref="double"/>,
    /// since the only use of a price in this tool is <c>:.2f</c>-style display formatting, which
    /// Python applies identically to an int or a float).</summary>
    private static double GetDoubleOrDefault(JsonElement obj, string propertyName, double fallback) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    /// <summary>Python truthiness for a JSON value (used for <c>product.get("isRecipe", False)</c>,
    /// which skips a product on ANY truthy value, not just a literal JSON <c>true</c>).</summary>
    private static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => false,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.String => element.GetString()!.Length > 0,
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>Builds an O(1)-by-key lookup for a JSON object, used wherever the Python twin does
    /// repeated <c>dict.get(id)</c> calls. NEVER enumerated itself for order (a <see cref="Dictionary{TKey,TValue}"/>'s
    /// enumeration order is not a documented guarantee) -- anywhere document order matters, the
    /// original <see cref="JsonElement"/>'s own <c>EnumerateObject()</c> is used instead.</summary>
    private static Dictionary<string, JsonElement> BuildLookup(JsonElement obj)
    {
        var lookup = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            lookup[prop.Name] = prop.Value;
        }
        return lookup;
    }
}
