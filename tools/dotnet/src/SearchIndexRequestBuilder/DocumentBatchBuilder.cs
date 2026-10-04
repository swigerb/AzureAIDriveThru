using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Independently reconstructs the exact Azure AI Search REST request bodies that
/// <c>upload_documents</c> (setup_search_index.py lines 294-309) sends via
/// <c>SearchClient.merge_or_upload_documents(batch)</c> -- one
/// <c>POST /indexes('&lt;name&gt;')/docs/search.index</c> per 100-document batch, each document
/// tagged with an <c>"@search.action": "mergeOrUpload"</c> (captured empirically the same way as
/// SearchIndexDefinitionBuilder -- see its remarks).
/// </summary>
internal static class DocumentBatchBuilder
{
    private const int BatchSize = 100;

    /// <summary>Builds every batch body for <paramref name="documents"/>, each already carrying its
    /// "embedding" field (attach via <see cref="FixtureEmbedding"/> first, mirroring how the real
    /// <c>ingest_plan</c> only attaches embeddings after <c>generate_embeddings</c> returns, before
    /// calling <c>upload_documents</c>).</summary>
    public static List<JsonObject> BuildBatches(IReadOnlyList<JsonObject> documents)
    {
        var batches = new List<JsonObject>();
        for (var i = 0; i < documents.Count; i += BatchSize)
        {
            var batch = documents.Skip(i).Take(BatchSize);
            var value = new JsonArray();
            foreach (var doc in batch)
            {
                var tagged = new JsonObject { ["@search.action"] = "mergeOrUpload" };
                foreach (var property in doc)
                {
                    tagged[property.Key] = property.Value?.DeepClone();
                }
                value.Add(tagged);
            }
            batches.Add(new JsonObject { ["value"] = value });
        }
        return batches;
    }
}
