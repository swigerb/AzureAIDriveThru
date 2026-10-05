using SearchIndexRequestBuilder;

namespace SearchIndexIngestor;

/// <summary>
/// Faithful port of setup_search_index.py's <c>ingest_plan</c> (lines 413-445): the real
/// (non-dry-run) per-persona work -- create/update the index, generate embeddings, upload in
/// 100-document batches, delete anything stale, verify the final document count with retry.
/// </summary>
public sealed class SearchIndexOrchestrator
{
    private const int BatchSize = 100;
    // verify_document_count's own retry knobs (setup_search_index.py lines 326-348).
    private const int VerifyAttempts = 10;
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(2.0);

    private readonly SearchIndexHttpClient _searchClient;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly string _openAiEndpoint;
    private readonly string _embeddingDeployment;
    private readonly TextWriter _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SearchIndexOrchestrator(
        SearchIndexHttpClient searchClient,
        IEmbeddingClient embeddingClient,
        string openAiEndpoint,
        string embeddingDeployment,
        TextWriter log,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _searchClient = searchClient;
        _embeddingClient = embeddingClient;
        _openAiEndpoint = openAiEndpoint;
        _embeddingDeployment = embeddingDeployment;
        _log = log;
        _delay = delay ?? ((delay, token) => Task.Delay(delay, token));
    }

    public async Task IngestAsync(PersonaIngestPlan plan, CancellationToken cancellationToken)
    {
        _log.WriteLine($"Persona '{plan.PersonaId}': setting up Azure AI Search index '{plan.IndexName}'...");
        var indexDefinition = SearchIndexDefinitionBuilder.Build(plan.IndexName, _openAiEndpoint, _embeddingDeployment);
        await _searchClient.CreateOrUpdateIndexAsync(plan.IndexName, indexDefinition, cancellationToken)
            .ConfigureAwait(false);

        _log.WriteLine($"Persona '{plan.PersonaId}': generating embeddings for {plan.DocumentCount} document(s)...");
        var embeddings = await _embeddingClient
            .GenerateEmbeddingsAsync(plan.TextsForEmbedding, _embeddingDeployment, cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != plan.Documents.Count)
        {
            // Python's zip(plan.documents, embeddings) silently truncates to the shorter sequence
            // on a count mismatch (ingest_plan, line 435) instead of failing -- this port instead
            // raises a clean, catchable error (Rick's review, item 3), matching this port's
            // established "expected, resolvable configuration/validation failure" convention (see
            // PersonaTargeting's remarks) rather than letting the loop below throw an unhandled
            // ArgumentOutOfRangeException/IndexOutOfRangeException.
            throw new InvalidOperationException(
                $"Persona '{plan.PersonaId}': Azure OpenAI returned {embeddings.Count} embedding(s) " +
                $"for {plan.Documents.Count} document(s) -- expected one embedding per document.");
        }
        for (var i = 0; i < plan.Documents.Count; i++)
        {
            var vector = new System.Text.Json.Nodes.JsonArray();
            foreach (var component in embeddings[i])
            {
                // JsonValue.Create(double) drops the decimal point for a whole-number value (e.g.
                // -1.0 serializes as "-1"), unlike Python's json.dumps, which always writes a float
                // with a '.'. Azure AI Search's REST API can treat an int-shaped and a float-shaped
                // token differently for the same Collection(Edm.Single) field even though they're
                // numerically equal, so every component is parsed from its Python-faithful float
                // text (PythonJsonDumps.PythonFloatRepr) instead, preserving that shape exactly as
                // the real script's requests would.
                vector.Add(System.Text.Json.Nodes.JsonNode.Parse(
                    SearchIndexRequestBuilder.PythonJsonDumps.PythonFloatRepr(component)));
            }
            plan.Documents[i]["embedding"] = vector;
        }

        var keepIds = new HashSet<string>(
            plan.Documents.Select(d => d["id"]!.GetValue<string>()), StringComparer.Ordinal);

        for (var i = 0; i < plan.Documents.Count; i += BatchSize)
        {
            var batchDocuments = plan.Documents.Skip(i).Take(BatchSize).ToList();
            var batchBody = DocumentBatchBuilder.BuildBatches(batchDocuments).Single();
            var succeeded = await _searchClient.UploadBatchAsync(plan.IndexName, batchBody, cancellationToken)
                .ConfigureAwait(false);
            _log.WriteLine($"Uploaded batch {i}-{i + batchDocuments.Count}: {succeeded}/{batchDocuments.Count} succeeded");
        }

        // delete_stale_documents always logs its deleted count, including zero (lines 442-443) --
        // not conditional on there being anything to delete.
        var existingIds = await _searchClient.ListAllDocumentIdsAsync(plan.IndexName, cancellationToken)
            .ConfigureAwait(false);
        var staleIds = existingIds.Where(id => !keepIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < staleIds.Count; i += BatchSize)
        {
            var batch = staleIds.Skip(i).Take(BatchSize).ToList();
            await _searchClient.DeleteBatchAsync(plan.IndexName, batch, cancellationToken).ConfigureAwait(false);
        }
        _log.WriteLine($"Persona '{plan.PersonaId}': deleted {staleIds.Count} stale document(s) no longer on the menu.");

        await VerifyDocumentCountAsync(plan, cancellationToken).ConfigureAwait(false);
        _log.WriteLine($"Persona '{plan.PersonaId}': search index setup complete.");
    }

    /// <summary>
    /// <c>verify_document_count</c> (lines 326-348, issue #84 acceptance): Azure AI Search's own
    /// count endpoint can lag briefly behind a just-completed upload/delete, so this retries up to
    /// <see cref="VerifyAttempts"/> times, <see cref="VerifyDelay"/> apart. Unlike every other
    /// "clean, catchable" error this port raises as <see cref="InvalidOperationException"/> (see
    /// <see cref="PersonaTargeting"/>'s remarks), a final count mismatch here mirrors Python's own
    /// <c>raise RuntimeError(...)</c> (an unhandled exception, not a <c>SystemExit</c> -- the one
    /// failure path in the whole script that is NOT a plain <c>SystemExit</c>/configuration error)
    /// with a plain <see cref="Exception"/>, so <see cref="CliRunner"/>'s catch block (scoped to
    /// <see cref="InvalidOperationException"/> only) does not swallow it the same clean way --
    /// matching Python's own distinct "ingest completed, but acceptance failed" failure mode.
    /// </summary>
    private async Task VerifyDocumentCountAsync(PersonaIngestPlan plan, CancellationToken cancellationToken)
    {
        int? actual = null;
        for (var attempt = 0; attempt < VerifyAttempts; attempt++)
        {
            actual = await _searchClient.GetDocumentCountAsync(plan.IndexName, cancellationToken)
                .ConfigureAwait(false);
            if (actual == plan.DocumentCount)
            {
                return;
            }
            if (attempt < VerifyAttempts - 1)
            {
                await _delay(VerifyDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new Exception(
            $"Index '{plan.IndexName}' holds {actual} document(s) after ingest; the plan has {plan.DocumentCount}.");
    }
}
