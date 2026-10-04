using System.Text;
using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Faithful port of <c>prepare_documents</c> (setup_search_index.py lines 248-283): turns one
/// persona's parsed <c>menuItems.json</c> into the same ordered list of search-document field sets
/// and embedding-input texts the Python twin builds, in the exact same order (category groups, then
/// items, in file order -- never sorted), so later 100-document batching slices identically on both
/// sides.
/// </summary>
internal static class MenuDocumentBuilder
{
    /// <summary>One prepared document's field values (not yet given an "embedding" field --
    /// <see cref="FixtureEmbedding"/> attaches that afterwards, mirroring how the real
    /// <c>ingest_plan</c> only attaches real embeddings after <c>generate_embeddings</c> returns)
    /// plus the text that embedding would be generated from.</summary>
    public sealed record PreparedDocument(JsonObject Fields, string CombinedTextForEmbedding);

    public static List<PreparedDocument> Build(JsonNode menuData)
    {
        var documents = new List<PreparedDocument>();

        foreach (var categoryGroupNode in menuData["menuItems"]!.AsArray())
        {
            var categoryGroup = categoryGroupNode!.AsObject();
            var categoryName = categoryGroup["category"]!.GetValue<string>();

            foreach (var itemNode in categoryGroup["items"]!.AsArray())
            {
                var item = itemNode!.AsObject();
                var name = item["name"]!.GetValue<string>();
                var description = item["description"]!.GetValue<string>();
                var longDescription = GetStringOrDefault(item, "longDescription", "");

                var docId = SanitizeKey($"{categoryName}_{name.Replace(' ', '_')}".ToLowerInvariant());
                var combinedText = $"{categoryName} {name} {description} {longDescription}";

                var fields = new JsonObject
                {
                    ["id"] = docId,
                    ["category"] = categoryName,
                    ["name"] = name,
                    ["description"] = description,
                    ["longDescription"] = longDescription,
                    ["origin"] = GetStringOrDefault(item, "origin", ""),
                    ["caffeineContent"] = GetStringOrDefault(item, "caffeineContent", ""),
                    ["brewingMethod"] = GetStringOrDefault(item, "brewingMethod", ""),
                    ["popularity"] = GetStringOrDefault(item, "popularity", ""),
                    // #165: empty string (not omitted), matching setup_search_index.py's own
                    // comment -- the field must always be present so `menuPeriod eq '<mode>'`
                    // never 400s with "document is missing required field" on an upload/merge.
                    ["menuPeriod"] = GetStringOrDefault(item, "menuPeriod", ""),
                    ["sizes"] = PythonJsonDumps.Serialize(item["sizes"]),
                };

                documents.Add(new PreparedDocument(fields, combinedText));
            }
        }

        return documents;
    }

    private static string GetStringOrDefault(JsonObject obj, string key, string fallback) =>
        obj.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<string>() : fallback;

    /// <summary>Same semantics as setup_search_index.py's <c>sanitize_key</c> (line 111-113):
    /// <c>re.sub(r"[^a-zA-Z0-9_\-]", "_", key)</c>. Python's <c>re</c> module iterates <c>str</c>
    /// by Unicode CODE POINT, so one astral character (e.g. an emoji outside the Basic Multilingual
    /// Plane, encoded as a UTF-16 surrogate PAIR) is one invalid match, replaced by a single "_".
    /// Iterates by <see cref="Rune"/> (PR #250 review R4) rather than by <c>char</c>/
    /// <c>Regex</c> (which would walk UTF-16 code UNITS instead) for the same reason -- a
    /// plain <c>Regex.Replace</c> would treat that same emoji's two surrogate halves as two
    /// separate invalid characters and emit "__" (two underscores) where Python emits one.</summary>
    private static string SanitizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var rune in key.EnumerateRunes())
        {
            builder.Append(IsValidKeyRune(rune) ? (char)rune.Value : '_');
        }
        return builder.ToString();
    }

    private static bool IsValidKeyRune(Rune rune) =>
        rune.Value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
