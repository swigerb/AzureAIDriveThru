using System.Text.Json;
using System.Text.Json.Nodes;

namespace UpdateMenuSizes;

/// <summary>
/// A faithful port of scripts/update_menu_sizes.py (issue #16's first C# tooling port -- see
/// docs/dotnet_tooling.md). Adds the size/price variants parsed from the Sonic production POS
/// export (sonic-menu-items.json) to the handful of drink/slush/shake/blast items named in
/// <see cref="ProductSearchMap"/>, inside the UI's menuItems.json.
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

    /// <summary>Maps a menuItems.json item name to its search term in the production export.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProductSearchMap = new Dictionary<string, string>
    {
        ["Cherry Limeade"] = "Cherry Limeade",
        ["Blue Raspberry Slush"] = "Blue Raspberry Slush",
        ["Ocean Water\u00ae"] = "Ocean Water",
        ["Oreo\u00ae Peanut Butter Shake"] = "OREO\u00ae Peanut Butter Master Shake",
        ["Classic Vanilla Shake"] = "Vanilla Classic Shake",
        ["SONIC Blast\u00ae with M&M'S\u00ae"] = "SONIC Blast\u00ae made with M&M",
    };

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
    /// Reconciles every <see cref="ProductSearchMap"/> item in <paramref name="menuFilePath"/>
    /// against <paramref name="productionFilePath"/>'s size variants, rewriting
    /// <paramref name="menuFilePath"/> in place -- the same observable effect as Python's
    /// <c>update_menu()</c>. Returns how many items changed and the same SKIP/UPDATED log lines
    /// the Python twin prints, for callers to display or assert against.
    /// </summary>
    public static UpdateResult UpdateMenu(string productionFilePath, string menuFilePath)
    {
        var products = LoadProductionProducts(productionFilePath);

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
                if (!ProductSearchMap.TryGetValue(name, out var searchTerm))
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
                            ["price"] = price,
                        });
                    }
                    item["sizes"] = newSizesArray;
                    updatedCount++;
                    log.Add($"  UPDATED {name}: {oldCount} -> {newSizes.Count} sizes: [{string.Join(", ", newSizes.Select(s => s.Size))}]");
                }
                else
                {
                    log.Add($"  SKIP {name}: already up to date");
                }
            }
        }

        var writeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 4,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(menuFilePath, menuNode.ToJsonString(writeOptions) + "\n");

        log.Add(string.Empty);
        log.Add($"Updated {updatedCount} items in menuItems.json");

        return new UpdateResult(updatedCount, log);
    }
}
