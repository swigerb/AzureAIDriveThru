using System.Text;
using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>One flattened Azure AI Search document, mirroring the shape `tools.py` expects back.</summary>
public sealed record MenuDocument(string Id, string Name, string Category, string Description, string SizesJson);

/// <summary>
/// Loads `app/frontend/src/data/menuItems.json` (the single source of menu data for both the
/// real Azure AI Search index and this fake) and flattens it into search documents the same
/// shape `tools.py` selects from: id, name, category, description, and sizes as a JSON string
/// (tools.py does `json.loads()` on the sizes field, so it must round-trip as a string, not a
/// nested array).
/// </summary>
public static class MenuIndex
{
    public static IReadOnlyList<MenuDocument> Load(string menuItemsJsonPath)
    {
        using var stream = File.OpenRead(menuItemsJsonPath);
        using var document = JsonDocument.Parse(stream);

        var documents = new List<MenuDocument>();
        foreach (var category in document.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            var categoryName = category.GetProperty("category").GetString() ?? "";
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var sizesJson = item.TryGetProperty("sizes", out var sizes)
                    ? sizes.GetRawText()
                    : "[]";
                documents.Add(new MenuDocument(Id: Slugify(categoryName, name), Name: name, Category: categoryName,
                    Description: description, SizesJson: sizesJson));
            }
        }

        return documents;
    }

    /// <summary>
    /// Issue #76 part 2: resolves every persona id in <paramref name="personaIds"/> to the Azure
    /// AI Search index name its OWN persona.json declares (`search.indexName`) and that SAME
    /// pack's own `menu/menuItems.json` path -- so <see cref="Conformance.Fakes.FakeSearchServer"/>
    /// can serve one Kestrel host that answers each persona's own index with only that persona's
    /// own menu documents, instead of a single-pack document set regardless of which index a
    /// persona's SearchClient actually targeted (the exact gap
    /// PersonaBusinessRuleConformanceTests.cs's own doc comment calls out). A hand-rolled,
    /// harness-local read of just the two persona.json fields this fake needs -- deliberately NOT
    /// a dependency on app/backend/persona_loader.py's or
    /// app/backend-dotnet/src/Backend/Personas/PersonaCatalog.cs's full schema validation, which
    /// belongs to the backends under test, not their test double.
    ///
    /// Rick's PR #108 second review, required item C: two DIFFERENT personas declaring the SAME
    /// `search.indexName` (a copy-paste mistake in a new pack's persona.json, most likely) used to
    /// silently overwrite the first persona's entry in this map with the second's -- the fake
    /// would then answer that shared index with only the LAST persona's menu documents, and any
    /// conformance row exercising the first persona's search would silently get the wrong
    /// persona's catalog back instead of failing loudly. Throws instead, naming both persona ids
    /// and the index name they collide on, the moment a second, DIFFERENT persona claims an
    /// already-claimed index name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveIndexPaths(string personasDir, IEnumerable<string> personaIds)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownerByIndexName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var personaId in personaIds)
        {
            var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
            using var stream = File.OpenRead(personaJsonPath);
            using var document = JsonDocument.Parse(stream);
            var indexName = document.RootElement.GetProperty("search").GetProperty("indexName").GetString();
            if (string.IsNullOrEmpty(indexName))
            {
                throw new InvalidOperationException(
                    $"persona.json for '{personaId}' under '{personasDir}' has no search.indexName.");
            }

            if (ownerByIndexName.TryGetValue(indexName, out var firstOwner) && firstOwner != personaId)
            {
                throw new InvalidOperationException(
                    $"Duplicate Azure AI Search index name '{indexName}': both '{firstOwner}' and " +
                    $"'{personaId}' (personas under '{personasDir}') declare search.indexName=" +
                    $"'{indexName}' in their own persona.json. Every enabled persona must have its " +
                    "own, distinct index name -- a shared name would make this fake (and the real " +
                    "Azure AI Search service) answer both personas' searches from whichever " +
                    "persona's documents were indexed last.");
            }
            ownerByIndexName[indexName] = personaId;

            map[indexName] = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        }

        return map;
    }

    private static string Slugify(string category, string name)
    {
        var combined = $"{category}-{name}";
        var builder = new StringBuilder(combined.Length);
        foreach (var c in combined.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        return builder.ToString().Trim('-');
    }
}
