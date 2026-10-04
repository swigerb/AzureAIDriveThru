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
            // PR #224 review R3: a non-string value here (e.g. a typo'd `null`/number/object) must
            // not silently become "" -- FindSizesForProduct's search term would then be an empty
            // string, whose Contains check matches EVERY production displayName, corrupting that
            // menu item's sizes with whatever product happens to be first in production order
            // instead of visibly failing. Fail loudly and specifically instead.
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"'{productSearchMapFilePath}': entry '{property.Name}' is not a string " +
                    $"(found {property.Value.ValueKind}). Every value must be the production " +
                    "search term string, or FindSizesForProduct would match every product.");
            }
            map[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        if (map.Count == 0)
        {
            throw new InvalidDataException($"'{productSearchMapFilePath}' contained no entries.");
        }
        return map;
    }

    /// <summary>
    /// The production export's fields this tool actually reads, per product. <paramref name="PriceText"/>
    /// is the exact raw JSON number token the price was parsed from (e.g. "2" or "1.50"), preserved
    /// alongside the parsed <paramref name="Price"/> decimal so a whole-number literal can be written
    /// back unchanged -- a C# <see cref="decimal"/> alone can't distinguish "price Python parsed as
    /// an int" from "price Python parsed as a float" (see <see cref="FormatPriceLikePython"/>).
    /// Null when no number was present (not expected for a real production export's price field).
    /// </summary>
    public readonly record struct ProductEntry(string DisplayName, decimal Price, string? PriceText = null);

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
            decimal price = 0m;
            string? priceText = null;
            if (product.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number)
            {
                price = priceEl.GetDecimal();
                // The raw token, e.g. "2" or "1.50" -- not re-derived from `price`, which already
                // lost whether the source literal had a decimal point.
                priceText = priceEl.GetRawText();
            }
            result.Add(new ProductEntry(displayName, price, priceText));
        }
        return result;
    }

    /// <summary>
    /// Finds all sized variants of <paramref name="searchTerm"/> among <paramref name="products"/>,
    /// walking them in production-document order -- first match per size key wins, matching
    /// Python's <c>if size_key not in sizes: sizes[size_key] = price</c>. Returns the whole winning
    /// <see cref="ProductEntry"/> per size key (not just its price) so callers can still see the
    /// original price's raw JSON text for byte-parity writes (see <see cref="FormatPriceLikePython"/>).
    /// </summary>
    public static Dictionary<string, ProductEntry> FindSizesForProduct(IReadOnlyList<ProductEntry> products, string searchTerm)
    {
        var sizes = new Dictionary<string, ProductEntry>();
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
                sizes[sizeKey] = product;
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

                var newSizes = new List<(string Size, ProductEntry Entry)>();
                foreach (var sizeKey in SizeOrder)
                {
                    if (prodSizes.TryGetValue(sizeKey, out var entry))
                    {
                        newSizes.Add((sizeKey, entry));
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
                        var newPrice = newSizes.First(n => n.Size == sizeKey).Entry.Price;
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
                    foreach (var (size, entry) in newSizes)
                    {
                        newSizesArray.Add(new JsonObject
                        {
                            ["size"] = size,
                            // Python's json.dump re-serializes every number via the SAME type it
                            // parsed it as: a production price with no '.'/'e'/'E' parsed as an int
                            // and is written back byte-for-byte unchanged (e.g. a whole-number
                            // "price": 2 stays 2, never becomes 2.0); one that has a decimal point
                            // parsed as a float and is re-serialized via float.__repr__ -- the
                            // shortest round-trippable form, e.g. 1.50 -> "1.5", not whatever
                            // trailing-zero scale a C# decimal happened to retain from parsing.
                            // FormatPriceLikePython picks between entry.PriceText (the original
                            // token, reused verbatim) and PythonFloatRepr(entry.Price) accordingly;
                            // JsonNode.Parse on that exact string preserves it verbatim when
                            // re-serialized, unlike assigning the decimal directly.
                            ["price"] = JsonNode.Parse(FormatPriceLikePython(entry.Price, entry.PriceText)),
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

        // Python's json.load/json.dump round-trip re-serializes EVERY number in the whole parsed
        // dict via float repr if it parsed as a float (any token with '.', 'e', or 'E' -- e.g.
        // "2.50" -> "2.5"), and leaves it alone if it parsed as an int (no such token -- e.g. "2"
        // stays "2", never becomes "2.0"). This port's own edits above already produce
        // canonical-repr price literals for changed items (via PythonFloatRepr), but every OTHER
        // number already in the document -- untouched items' prices included -- round-trips
        // through JsonNode verbatim as originally written unless walked and rewritten here too
        // (PR #224 review R3: caught because a non-size-field int or an untouched trailing-zero
        // price elsewhere in the tree would otherwise silently diverge from Python's output).
        NormalizeNumberLiteralsLikePythonJsonDump(menuNode);

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
    /// Formats a production price's new-size write the way Python's <c>json.dump</c> would,
    /// matching whichever type Python's <c>json.load</c> would have parsed the ORIGINAL production
    /// price token as (PR #224 review R4: not every production price has a decimal point -- a
    /// whole-number price like <c>"price": 2</c> parses as a Python <c>int</c> and <c>json.dump</c>
    /// writes it back unchanged as <c>2</c>, never <c>2.0</c>). When <paramref name="priceText"/>
    /// (the original raw JSON token, from <see cref="ProductEntry.PriceText"/>) is known and
    /// contains none of <c>.</c>/<c>e</c>/<c>E</c>, it is reused verbatim. Otherwise -- a token with
    /// a decimal point, or no original text available -- falls back to <see cref="PythonFloatRepr(decimal)"/>.
    /// </summary>
    internal static string FormatPriceLikePython(decimal price, string? priceText) =>
        priceText is not null && priceText.IndexOfAny(['.', 'e', 'E']) < 0
            ? priceText
            : PythonFloatRepr(price);

    /// <summary>
    /// Formats <paramref name="value"/> the way Python's <c>json.dump</c> would format a price
    /// known to have parsed as a <c>float</c> (its original token had a decimal point, per
    /// <see cref="FormatPriceLikePython"/>) -- the shortest decimal string that round-trips to the
    /// same IEEE-754 double, with at least one digit after the point (Python's float repr always
    /// shows ".0" for a whole number; .NET's default shortest-round-trip double formatting does
    /// not).
    /// </summary>
    internal static string PythonFloatRepr(decimal value) => PythonFloatRepr((double)value);

    /// <summary>Core formatting shared by both <see cref="PythonFloatRepr(decimal)"/> and the
    /// whole-tree number normalization below, which parses an existing float-literal token's raw
    /// text as a double rather than starting from a C# <see cref="decimal"/>.</summary>
    internal static string PythonFloatRepr(double value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'e', 'E']) < 0 ? text + ".0" : text;
    }

    /// <summary>
    /// Walks the WHOLE <paramref name="node"/> tree (not just the sizes/price fields
    /// <see cref="UpdateMenu"/> directly edits) and rewrites every number literal the way Python's
    /// <c>json.load</c> + <c>json.dump</c> round-trip would: a token containing <c>.</c>, <c>e</c>,
    /// or <c>E</c> parses as a Python <c>float</c> and is re-serialized via float repr (e.g.
    /// <c>"2.50"</c> -&gt; <c>"2.5"</c>); a token with none of those parses as a Python <c>int</c>
    /// and is written back byte-for-byte unchanged (e.g. <c>"2"</c> stays <c>"2"</c>, never becomes
    /// <c>"2.0"</c>). PR #224 review R3: this must cover every number anywhere in the document,
    /// because Python's <c>json.dump</c> re-serializes the ENTIRE parsed dict, not only the
    /// size/price fields this tool itself changed.
    /// </summary>
    internal static void NormalizeNumberLiteralsLikePythonJsonDump(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                // ToList(): iterating obj directly while assigning obj[key] below would mutate
                // the collection being enumerated.
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    NormalizeChildInPlace(obj[key], replacement => obj[key] = replacement);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var index = i;
                    NormalizeChildInPlace(arr[index], replacement => arr[index] = replacement);
                }
                break;
        }
    }

    /// <summary>
    /// If <paramref name="child"/> is a float-literal number token, builds its normalized
    /// replacement and hands it to <paramref name="assign"/> (a freshly-<see cref="JsonNode.Parse"/>d
    /// node has no parent yet, so it is always safe to assign into its new slot). Otherwise,
    /// recurses into containers in place and never calls <paramref name="assign"/> at all --
    /// re-assigning a JsonObject/JsonArray property to the SAME child reference it already holds
    /// throws ("the node already has a parent"), so an unmodified child must be left untouched,
    /// not written back to its own slot.
    /// </summary>
    private static void NormalizeChildInPlace(JsonNode? child, Action<JsonNode?> assign)
    {
        if (child is JsonValue value && value.TryGetValue(out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number)
        {
            var raw = element.GetRawText();
            if (raw.IndexOfAny(['.', 'e', 'E']) >= 0)
            {
                assign(JsonNode.Parse(PythonFloatRepr(double.Parse(raw, CultureInfo.InvariantCulture))));
            }
            // else: an integer literal -- Python keeps this as an int, written back unchanged.
            return;
        }
        NormalizeNumberLiteralsLikePythonJsonDump(child);
    }
}
