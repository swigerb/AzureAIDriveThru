using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UpdateMenuSizes;

/// <summary>
/// A faithful port of scripts/update_menu_sizes.py (issue #16's first C# tooling port -- see
/// docs/dotnet_tooling.md). Adds the size/price variants parsed from a persona's production POS
/// export (<c>personas/&lt;id&gt;/menu/source/*-menu-items.json</c>, found via
/// <see cref="PersonaMenuLocator"/>) to the handful of drink/slush/shake/blast items named in that
/// same persona's own <c>product_search_map.json</c> (see <see cref="LoadProductSearchMap"/>),
/// inside the persona's <c>menuItems.json</c>.
///
/// This class intentionally reproduces the Python twin's logic exactly, including its one
/// apparent dead/no-op size prefix (see <see cref="SizePrefixes"/>) -- a port's job is to match
/// its twin's observable behavior, not to quietly "fix" something that looks like a bug. Any
/// future behavior change belongs in the Python script first, with this port following.
/// </summary>
public static class MenuSizeUpdater
{
    /// <summary>
    /// (prefix, normalized size key), checked in order -- first prefix match wins, matching
    /// Python's <c>extract_size</c>.
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, string SizeKey)> SizePrefixes =
    [
        ("Mini ", "mini"),
        ("Sm ", "small"),
        ("Small ", "small"),
        ("Med ", "medium"),
        ("Medium ", "medium"),
        ("Lg ", "large"),
        ("Large ", "large"),
        // The Python source writes this entry as a RAW string literal, r"RT 44\u00ae ", so it is
        // the 12 literal characters "RT 44\u00ae " (a backslash followed by u, 0, 0, a, e, not the
        // (R) symbol) -- it can never match a real production displayName. The next entry, with
        // the actual (R) character, is the one that does real work. Kept here only so this port
        // reproduces the twin's dead code rather than silently removing it.
        ("RT 44\\u00ae ", "rt 44"),
        ("RT 44\u00ae ", "rt 44"),
    ];

    /// <summary>Output order for an item's size variants, matching Python's SIZE_ORDER.</summary>
    public static readonly IReadOnlyList<string> SizeOrder = ["mini", "small", "medium", "large", "rt 44"];

    /// <summary>
    /// Loads a persona's menuItems.json-name -&gt; production-search-term map from
    /// <paramref name="productSearchMapFilePath"/> (a flat JSON object of string -&gt; string).
    /// This data moved out of this shared, persona-agnostic port and into each persona's own
    /// <c>personas/&lt;id&gt;/menu/product_search_map.json</c> (issue #16, PR #224 review R1):
    /// the mapped item names and search terms ARE that persona's own menu/brand data, not
    /// something a generic tooling port should hardcode. Parsed via <see cref="JsonDocument"/>
    /// (not the reflection-based <see cref="JsonSerializer.Deserialize{T}(Stream, JsonSerializerOptions?)"/>),
    /// matching <see cref="LoadProductionProducts"/>'s existing convention elsewhere in this file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadProductSearchMap(string productSearchMapFilePath)
    {
        using var stream = File.OpenRead(productSearchMapFilePath);
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{productSearchMapFilePath}' did not parse as a JSON object of " +
                "menuItems.json-name -> production-search-term strings.");
        }

        var map = new Dictionary<string, string>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
        }

        if (map.Count == 0)
        {
            throw new InvalidDataException($"'{productSearchMapFilePath}' contained no entries.");
        }
        return map;
    }

    /// <summary>The production export's two fields this tool actually reads, per product.</summary>
    public readonly record struct ProductEntry(string DisplayName, decimal Price);

    /// <summary>Result of a single <see cref="UpdateMenu"/> run, for callers (and tests) to inspect.</summary>
    public sealed record UpdateResult(int UpdatedCount, IReadOnlyList<string> Log);

    /// <summary>
    /// Returns (sizeKey, base) for a production displayName -- base is the name with the matched
    /// size prefix stripped, or the unmodified name with a null sizeKey if nothing matched.
    /// Matches Python's <c>extract_size</c> exactly, including prefix match order.
    /// </summary>
    public static (string? SizeKey, string Base) ExtractSize(string displayName)
    {
        foreach (var (prefix, sizeKey) in SizePrefixes)
        {
            if (displayName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (sizeKey, displayName[prefix.Length..]);
            }
        }
        return (null, displayName);
    }

    /// <summary>
    /// Reads `data["menus"]`'s FIRST value in document order (matching Python's
    /// <c>list(data["menus"].values())[0]</c>) and returns its `products` object's values, in
    /// document order, projected down to the two fields the twin reads (displayName, price).
    /// </summary>
    public static List<ProductEntry> LoadProductionProducts(string productionFilePath)
    {
        using var stream = File.OpenRead(productionFilePath);
        using var doc = JsonDocument.Parse(stream);

        JsonElement? firstMenu = null;
        if (doc.RootElement.TryGetProperty("menus", out var menus))
        {
            foreach (var menuProperty in menus.EnumerateObject())
            {
                firstMenu = menuProperty.Value;
                break;
            }
        }

        if (firstMenu is null || !firstMenu.Value.TryGetProperty("products", out var products))
        {
            return [];
        }

        var result = new List<ProductEntry>();
        foreach (var productProperty in products.EnumerateObject())
        {
            var product = productProperty.Value;
            var displayName = product.TryGetProperty("displayName", out var nameEl) &&
                               nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;
            var price = product.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number
                ? priceEl.GetDecimal()
                : 0m;
            result.Add(new ProductEntry(displayName, price));
        }
        return result;
    }

    /// <summary>
    /// Finds all sized variants of <paramref name="searchTerm"/> among <paramref name="products"/>,
    /// walking them in production-document order -- first match per size key wins, matching
    /// Python's <c>if size_key not in sizes: sizes[size_key] = price</c>.
    /// </summary>
    public static Dictionary<string, decimal> FindSizesForProduct(IReadOnlyList<ProductEntry> products, string searchTerm)
    {
        var sizes = new Dictionary<string, decimal>();
        var searchLower = searchTerm.ToLowerInvariant();

        foreach (var product in products)
        {
            var name = product.DisplayName;
            var price = product.Price;
            if (price <= 0m)
            {
                continue;
            }
            if (!name.ToLowerInvariant().Contains(searchLower))
            {
                continue;
            }

            var (sizeKey, baseName) = ExtractSize(name);
            if (sizeKey is null)
            {
                continue;
            }

            // Avoid picking up unrelated products (e.g. "Cherry Limeade Slush" when searching
            // "Cherry Limeade"). Matches the twin's special-cased exclusions exactly, including
            // that the Ocean Water check tests the FULL display name, not the prefix-stripped base.
            if (searchTerm == "Cherry Limeade" && baseName.ToLowerInvariant().Contains("slush"))
            {
                continue;
            }
            if (searchTerm == "Cherry Limeade" && baseName.ToLowerInvariant().Contains("diet"))
            {
                continue;
            }
            if (searchTerm == "Ocean Water" && name.ToLowerInvariant().Contains("diet"))
            {
                continue;
            }

            if (!sizes.ContainsKey(sizeKey))
            {
                sizes[sizeKey] = price;
            }
        }
        return sizes;
    }

    /// <summary>
    /// Reconciles every <paramref name="productSearchMapFilePath"/> (see
    /// <see cref="LoadProductSearchMap"/>) item in <paramref name="menuFilePath"/> against
    /// <paramref name="productionFilePath"/>'s size variants, rewriting
    /// <paramref name="menuFilePath"/> in place -- the same observable effect as Python's
    /// <c>update_menu()</c>. Returns how many items changed and the same SKIP/UPDATED log lines
    /// the Python twin prints, for callers to display or assert against.
    /// </summary>
    public static UpdateResult UpdateMenu(string productionFilePath, string menuFilePath, string productSearchMapFilePath)
    {
        var products = LoadProductionProducts(productionFilePath);
        var productSearchMap = LoadProductSearchMap(productSearchMapFilePath);

        var menuText = File.ReadAllText(menuFilePath);
        var menuNode = JsonNode.Parse(menuText)
            ?? throw new InvalidDataException($"'{menuFilePath}' did not parse as a JSON object.");

        var log = new List<string>();
        var updatedCount = 0;

        var categories = menuNode["menuItems"]!.AsArray();
        foreach (var categoryNode in categories)
        {
            var items = categoryNode!["items"]!.AsArray();
            foreach (var itemNode in items)
            {
                var item = itemNode!.AsObject();
                var name = item["name"]!.GetValue<string>();
                if (!productSearchMap.TryGetValue(name, out var searchTerm))
                {
                    continue;
                }

                var prodSizes = FindSizesForProduct(products, searchTerm);
                if (prodSizes.Count == 0)
                {
                    log.Add($"  SKIP {name}: no production data found");
                    continue;
                }

                var newSizes = new List<(string Size, decimal Price)>();
                foreach (var sizeKey in SizeOrder)
                {
                    if (prodSizes.TryGetValue(sizeKey, out var price))
                    {
                        newSizes.Add((sizeKey, price));
                    }
                }

                var existingSizes = item["sizes"]!.AsArray();
                var oldSizeKeys = new HashSet<string>(existingSizes.Select(s => s!["size"]!.GetValue<string>()));
                var newSizeKeys = new HashSet<string>(newSizes.Select(s => s.Size));

                var changed = !oldSizeKeys.SetEquals(newSizeKeys);
                if (!changed)
                {
                    foreach (var existing in existingSizes)
                    {
                        var sizeKey = existing!["size"]!.GetValue<string>();
                        if (!newSizeKeys.Contains(sizeKey))
                        {
                            continue;
                        }
                        var existingPrice = existing["price"]!.GetValue<decimal>();
                        var newPrice = newSizes.First(n => n.Size == sizeKey).Price;
                        if (existingPrice != newPrice)
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                {
                    var oldCount = existingSizes.Count;
                    var newSizesArray = new JsonArray();
                    foreach (var (size, price) in newSizes)
                    {
                        newSizesArray.Add(new JsonObject
                        {
                            ["size"] = size,
                            // Python's json.dump re-serializes every number it parsed as a float
                            // (any production price, since all of them have a decimal point) via
                            // float.__repr__ -- the shortest round-trippable form, e.g. 1.50 ->
                            // "1.5", not whatever trailing-zero scale a C# decimal happened to
                            // retain from parsing. JsonNode.Parse on that exact string preserves it
                            // verbatim when re-serialized (see PythonFloatRepr), unlike assigning
                            // the decimal directly.
                            ["price"] = JsonNode.Parse(PythonFloatRepr(price)),
                        });
                    }
                    item["sizes"] = newSizesArray;
                    updatedCount++;
                    // Python's log line embeds the new sizes list via an f-string, which calls
                    // Python's list repr -- ['mini', 'small'], not C#'s default
                    // string.Join-style [mini, small]. SizeOrder's values are a small fixed,
                    // internal set of plain words with no quotes/backslashes, so naive
                    // single-quote wrapping is safe here (not a general-purpose Python repr).
                    var sizesRepr = "[" + string.Join(", ", newSizes.Select(s => $"'{s.Size}'")) + "]";
                    log.Add($"  UPDATED {name}: {oldCount} -> {newSizes.Count} sizes: {sizesRepr}");
                }
                else
                {
                    log.Add($"  SKIP {name}: already up to date");
                }
            }
        }

        // Byte-for-byte match of Python's `json.dump(menu_data, f, indent=4, ensure_ascii=False)`
        // + `f.write("\n")`: PythonJsonEncoder reproduces ensure_ascii=False's narrower escape set
        // (see its own doc comment), and NewLine/the trailing newline use Environment.NewLine to
        // reproduce Python's text-mode "w" newline translation (CRLF on Windows, LF elsewhere) for
        // every newline the write emits, including the final one -- System.Text.Json's own default
        // NewLine already matches Environment.NewLine on this SDK, but this is set explicitly so
        // that remains true regardless of SDK defaults changing.
        var writeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 4,
            NewLine = Environment.NewLine,
            Encoder = PythonJsonEncoder.Instance,
        };
        File.WriteAllText(menuFilePath, menuNode.ToJsonString(writeOptions) + Environment.NewLine);

        log.Add(string.Empty);
        log.Add($"Updated {updatedCount} items in menuItems.json");

        return new UpdateResult(updatedCount, log);
    }

    /// <summary>
    /// Formats <paramref name="value"/> the way Python's <c>json.dump</c> would format the same
    /// value after round-tripping it through <c>json.load</c> as a float (every production price
    /// has a decimal point, so Python always parses it as a float, never an int) -- the shortest
    /// decimal string that round-trips to the same IEEE-754 double, with at least one digit after
    /// the point (Python's float repr always shows ".0" for a whole number; .NET's default
    /// shortest-round-trip double formatting does not).
    /// </summary>
    internal static string PythonFloatRepr(decimal value)
    {
        var text = ((double)value).ToString(CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'e', 'E']) < 0 ? text + ".0" : text;
    }
}
